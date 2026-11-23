using HidSharp;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// Logitech devices speaking HID++ 2.0 – through a LIGHTSPEED / Unifying / Bolt receiver
/// (device index 1-6) or connected directly by cable (index 0xFF).
/// Uses the 20-byte "long" vendor collection (report 0x11).
/// Battery features, in order of preference: 0x1004 Unified Battery, 0x1000 Battery Status, 0x1001 Battery Voltage.
/// </summary>
public sealed class LogitechProvider : IBatteryProvider, IRestorable
{
    private const int LogitechVid = 0x046D;
    private const byte LongReport = 0x11;
    private const byte SwId = 0x0B; // identifies our requests among G HUB's
    private const ushort FeatName = 0x0005, FeatUnified = 0x1004, FeatStatus = 0x1000, FeatVoltage = 0x1001;
    /// <summary>ADC measurement, used by G533/G633/G635/G733/G933/G935 headsets (HeadsetControl).</summary>
    private const ushort FeatAdc = 0x1F20;

    /// <summary>
    /// A mouse that stops answering is most likely asleep (Logitech receivers can't tell asleep from switched off):
    /// keep showing its last value, dimmed, for a while — also across app restarts, via <see cref="Restore"/>.
    /// </summary>
    private static readonly TimeSpan StaleKeep = TimeSpan.FromHours(8);

    private sealed class Known
    {
        public string Name = "Logitech";
        public DeviceKind Kind = DeviceKind.Mouse;
        public ushort BatteryFeature;
        public byte BatteryIndex;
        public DeviceBattery? Last;
    }

    private readonly Dictionary<string, Known> _known = new();

    /// <summary>Seeds the "asleep" cache with the previous run's readings (the receiver must still be present to show them).</summary>
    public void Restore(IEnumerable<DeviceBattery> lastKnown)
    {
        foreach (var d in lastKnown.Where(d => d.Source == "logitech" && !_known.ContainsKey(d.Id)))
            _known[d.Id] = new Known { Name = d.Name, Kind = d.Kind, Last = d };
    }

    public string Name => "Logitech HID++";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        var seen = new HashSet<string>();
        var presentPorts = new HashSet<string>();

        foreach (var dev in DeviceList.Local.GetHidDevices(LogitechVid))
        {
            if (!IsLongHidpp(dev)) continue;
            var serial = DeviceKey(dev);
            presentPorts.Add($"logi:{serial}:");
            using var s = HidUtil.TryOpen(dev);
            if (s == null) continue;

            bool isReceiver = (dev.ProductID & 0xFF00) == 0xC500;
            IEnumerable<byte> indices = isReceiver ? Ping(s, new byte[] { 1, 2, 3, 4, 5, 6 }) : Ping(s, new byte[] { 0xFF });

            foreach (var idx in indices)
            {
                var key = $"logi:{serial}:{idx}";
                try
                {
                    var b = ReadDevice(s, key, idx);
                    if (b == null) continue;
                    seen.Add(key);
                    result.Add(b);
                }
                catch (Exception e) { Log.Error($"Logitech {key}", e); }
            }
        }

        // Devices that went quiet: show their last known value as "stale" for a while.
        foreach (var (key, k) in _known)
        {
            if (seen.Contains(key) || k.Last == null) continue;
            if (!presentPorts.Any(key.StartsWith)) continue; // receiver unplugged → really gone
            if (DateTime.Now - (k.Last.LastSeen ?? DateTime.MinValue) < StaleKeep)
                result.Add(k.Last with { Stale = true });
        }
        return result;
    }

    private DeviceBattery? ReadDevice(HidStream s, string key, byte idx)
    {
        if (!_known.TryGetValue(key, out var k) || k.BatteryFeature == 0)
        {
            k = new Known();
            foreach (var f in new[] { FeatUnified, FeatStatus, FeatVoltage, FeatAdc })
            {
                var fi = GetFeatureIndex(s, idx, f);
                if (fi is > 0) { k.BatteryFeature = f; k.BatteryIndex = fi.Value; break; }
            }
            if (k.BatteryFeature == 0) return null; // no battery (wired-only device, receiver itself…)
            ReadNameAndType(s, idx, k);
            _known[key] = k;
        }

        int? pct; ChargeState state; bool approx = false;
        switch (k.BatteryFeature)
        {
            case FeatUnified:
            {
                var r = Request(s, idx, k.BatteryIndex, 1);
                if (r == null) { k.BatteryFeature = 0; return null; }
                pct = r[4];
                state = r[6] switch { 1 or 2 => ChargeState.Charging, 3 => ChargeState.Full, _ => ChargeState.Discharging };
                if (pct == 0 && (r[5] & 0x0F) != 0) { pct = LevelFlagsToPercent(r[5]); approx = true; }
                break;
            }
            case FeatStatus:
            {
                var r = Request(s, idx, k.BatteryIndex, 0);
                if (r == null) { k.BatteryFeature = 0; return null; }
                pct = r[4];
                state = r[6] switch { 1 or 2 or 4 => ChargeState.Charging, 3 => ChargeState.Full, _ => ChargeState.Discharging };
                break;
            }
            case FeatAdc:
            {
                var r = Request(s, idx, k.BatteryIndex, 0);
                if (r == null) { k.BatteryFeature = 0; return null; }
                int mv = (r[4] << 8) | r[5];
                if (mv < HeadsetCurve[^1].mv) return null; // below the curve: headset off / no reading
                pct = Interpolate(HeadsetCurve, mv);
                approx = true;
                state = r[6] == 0x03 ? ChargeState.Charging : ChargeState.Discharging;
                if (k.Kind == DeviceKind.Mouse && k.Name == "Logitech") k.Kind = DeviceKind.Headset;
                break;
            }
            default:
            {
                var r = Request(s, idx, k.BatteryIndex, 0);
                if (r == null) { k.BatteryFeature = 0; return null; }
                int mv = (r[4] << 8) | r[5];
                byte flags = r[6];
                pct = VoltageToPercent(mv);
                approx = true;
                state = (flags & 0x80) == 0 ? ChargeState.Discharging
                      : (flags & 0x07) == 1 ? ChargeState.Full : ChargeState.Charging;
                break;
            }
        }

        k.Last = new DeviceBattery(key, k.Name, k.Kind, pct, state, "logitech", approx, LastSeen: DateTime.Now);
        return k.Last;
    }

    // ---- HID++ helpers -------------------------------------------------------------

    private static bool IsLongHidpp(HidDevice d)
    {
        try { return d.GetMaxInputReportLength() == 20 && d.GetMaxOutputReportLength() == 20; }
        catch { return false; }
    }

    private static string DeviceKey(HidDevice d)
    {
        // Same receiver keeps the same key across polls; strip the collection suffix of the path.
        var p = d.DevicePath.ToLowerInvariant();
        int i = p.IndexOf("&col", StringComparison.Ordinal);
        return $"{d.ProductID:X4}:{HidUtil.StableHash(i > 0 ? p[..i] : p)}";
    }

    /// <summary>Sends a root "ping" to every index at once and returns those that answered.</summary>
    private static List<byte> Ping(HidStream s, byte[] indices)
    {
        foreach (var idx in indices)
        {
            var b = new byte[20];
            b[0] = LongReport; b[1] = idx; b[2] = 0x00; b[3] = (1 << 4) | SwId; b[6] = 0x5A;
            try { s.Write(b); } catch { return new List<byte>(); }
        }
        var alive = new List<byte>();
        var deadline = Environment.TickCount64 + 700;
        while (alive.Count < indices.Length)
        {
            int left = (int)(deadline - Environment.TickCount64);
            var r = HidUtil.ReadUntil(s, r => r.Length >= 7 && r[0] == LongReport && r[2] == 0x00 && r[3] == ((1 << 4) | SwId) && r[6] == 0x5A, left);
            if (r == null) break;
            if (!alive.Contains(r[1])) alive.Add(r[1]);
        }
        return alive;
    }

    private static byte[]? Request(HidStream s, byte idx, byte feature, byte function, params byte[] args)
    {
        var b = new byte[20];
        b[0] = LongReport; b[1] = idx; b[2] = feature; b[3] = (byte)((function << 4) | SwId);
        args.CopyTo(b, 4);
        try { s.Write(b); } catch { return null; }
        var r = HidUtil.ReadUntil(s, r => r.Length >= 20 && r[0] == LongReport && r[1] == idx &&
                                     ((r[2] == feature && r[3] == b[3]) ||           // answer
                                      (r[2] == 0xFF && r[3] == feature && r[4] == b[3])), // HID++ 2.0 error
                                 1000);
        return r == null || r[2] == 0xFF ? null : r;
    }

    private static byte? GetFeatureIndex(HidStream s, byte idx, ushort feature)
    {
        var r = Request(s, idx, 0x00, 0, (byte)(feature >> 8), (byte)feature);
        return r?[4];
    }

    private static void ReadNameAndType(HidStream s, byte idx, Known k)
    {
        var fi = GetFeatureIndex(s, idx, FeatName);
        if (fi is not > 0) return;
        var len = Request(s, idx, fi.Value, 0)?[4] ?? 0;
        var chars = new List<byte>();
        while (chars.Count < len)
        {
            var r = Request(s, idx, fi.Value, 1, (byte)chars.Count);
            if (r == null) break;
            chars.AddRange(r.Skip(4).Take(Math.Min(16, len - chars.Count)));
        }
        var name = System.Text.Encoding.UTF8.GetString(chars.ToArray()).TrimEnd('\0', ' ');
        if (name.Length > 0) k.Name = name;

        var type = Request(s, idx, fi.Value, 2)?[4];
        k.Kind = type switch
        {
            0 or 2 => DeviceKind.Keyboard,
            3 or 4 or 5 => DeviceKind.Mouse,
            8 => DeviceKind.Headset,
            11 => DeviceKind.Controller,
            null => name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Keyboard : DeviceKind.Mouse,
            _ => DeviceKind.Other,
        };
    }

    private static int LevelFlagsToPercent(byte flags) =>
        (flags & 0x08) != 0 ? 100 : (flags & 0x04) != 0 ? 50 : (flags & 0x02) != 0 ? 20 : 5;

    /// <summary>Typical Li-ion discharge curve (same approximation Solaar uses for feature 0x1001).</summary>
    private static readonly (int mv, int pct)[] Curve =
    {
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    };

    /// <summary>HeadsetControl's calibration for the G633/G635/G733/G933/G935 family.</summary>
    private static readonly (int mv, int pct)[] HeadsetCurve =
    {
        (4100, 100), (3950, 80), (3850, 60), (3750, 40), (3650, 20), (3500, 10), (3300, 5), (3150, 0),
    };

    internal static int VoltageToPercent(int mv) => Interpolate(Curve, mv);

    private static int Interpolate((int mv, int pct)[] curve, int mv)
    {
        if (mv >= curve[0].mv) return 100;
        for (int i = 1; i < curve.Length; i++)
        {
            if (mv >= curve[i].mv)
            {
                var (hiV, hiP) = curve[i - 1];
                var (loV, loP) = curve[i];
                return (int)Math.Round(loP + (double)(mv - loV) * (hiP - loP) / (hiV - loV));
            }
        }
        return 0;
    }

    public void Dispose() { }
}
