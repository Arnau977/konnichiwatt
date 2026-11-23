using HidSharp;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// SteelSeries Arctis wireless headsets. Requests, offsets and value mappings from HeadsetControl
/// (lib/devices/steelseries_*.hpp). Replies are handled in hidapi form (no leading 0 for unnumbered reports),
/// so offsets match HeadsetControl's.
/// </summary>
public sealed class SteelSeriesProvider : IBatteryProvider
{
    private const int Vid = 0x1038;

    internal enum Family { Arctis7, Arctis9, ProWireless, Nova7Discrete, Nova7Percent, Arctis7Plus, NovaProWireless }

    private static readonly Dictionary<int, (string Name, Family Family)> Pids = new()
    {
        [0x1260] = ("SteelSeries Arctis 7", Family.Arctis7),
        [0x12AD] = ("SteelSeries Arctis 7", Family.Arctis7),
        [0x1252] = ("SteelSeries Arctis Pro", Family.Arctis7),
        [0x1280] = ("SteelSeries Arctis Pro", Family.Arctis7),
        [0x12C2] = ("SteelSeries Arctis 9", Family.Arctis9),
        [0x12C4] = ("SteelSeries Arctis 9", Family.Arctis9),
        [0x1290] = ("SteelSeries Arctis Pro Wireless", Family.ProWireless),
        [0x2202] = ("SteelSeries Arctis Nova 7", Family.Nova7Discrete),
        [0x2206] = ("SteelSeries Arctis Nova 7", Family.Nova7Discrete),
        [0x223A] = ("SteelSeries Arctis Nova 7", Family.Nova7Discrete),
        [0x227A] = ("SteelSeries Arctis Nova 7", Family.Nova7Discrete),
        [0x22A4] = ("SteelSeries Arctis Nova 7", Family.Nova7Discrete),
        [0x22A1] = ("SteelSeries Arctis Nova 7", Family.Nova7Percent),
        [0x227E] = ("SteelSeries Arctis Nova 7 Gen 2", Family.Nova7Percent),
        [0x2258] = ("SteelSeries Arctis Nova 7", Family.Nova7Percent),
        [0x229E] = ("SteelSeries Arctis Nova 7", Family.Nova7Percent),
        [0x22AD] = ("SteelSeries Arctis Nova 7", Family.Nova7Percent),
        [0x22A9] = ("SteelSeries Arctis Nova 7", Family.Nova7Percent),
        [0x22A5] = ("SteelSeries Arctis Nova 7", Family.Nova7Percent),
        [0x220E] = ("SteelSeries Arctis 7+", Family.Arctis7Plus),
        [0x2212] = ("SteelSeries Arctis 7P+", Family.Arctis7Plus),
        [0x2216] = ("SteelSeries Arctis 7X+", Family.Arctis7Plus),
        [0x2236] = ("SteelSeries Arctis 7+", Family.Arctis7Plus),
        [0x12E0] = ("SteelSeries Arctis Nova Pro Wireless", Family.NovaProWireless),
        [0x12E5] = ("SteelSeries Arctis Nova Pro Wireless", Family.NovaProWireless),
    };

    public string Name => "SteelSeries";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var group in DeviceList.Local.GetHidDevices(Vid).GroupBy(d => d.ProductID))
        {
            if (!Pids.TryGetValue(group.Key, out var info)) continue;
            var dev = group.FirstOrDefault(d => Matches(d, info.Family));
            if (dev == null) continue;
            using var s = HidUtil.TryOpen(dev);
            if (s == null) continue;

            var r = Read(s, dev, info.Family);
            if (r is not var (pct, state)) continue;
            result.Add(new DeviceBattery($"steelseries:{group.Key:X4}", info.Name, DeviceKind.Headset, pct, state, "steelseries",
                Approximate: info.Family is Family.ProWireless or Family.Nova7Discrete or Family.Arctis7Plus or Family.NovaProWireless,
                LastSeen: DateTime.Now));
        }
        return result;
    }

    private static bool Matches(HidDevice d, Family f)
    {
        if (HidUtil.OutLen(d) == 0) return false;
        return f switch
        {
            Family.Arctis7 => HidUtil.TopUsage(d) == 0xFF430202,
            Family.Arctis9 or Family.ProWireless => HidUtil.Interface(d) == 0,
            Family.NovaProWireless => HidUtil.Interface(d) == 4,
            _ => HidUtil.TopUsage(d) == 0xFFC00001 && HidUtil.Interface(d) == 3,
        };
    }

    private static (int? Pct, ChargeState State)? Read(HidStream s, HidDevice d, Family f)
    {
        if (f == Family.ProWireless)
        {
            // Pro Wireless answers the battery query even with the headset off: ask its status first.
            var st = HidUtil.Transact(s, d, new byte[] { 0x41, 0xAA }, r => r.Length >= 1);
            if (st == null || st[0] == 0x02) return null;
        }
        var (request, minLength) = BatteryRequest(f);
        var reply = HidUtil.Transact(s, d, request, r => r.Length >= minLength);
        return reply == null ? null : Decode(f, reply);
    }

    /// <summary>Battery request (first byte = report ID, hidapi convention) and minimum reply length per family.</summary>
    internal static (byte[] Request, int MinLength) BatteryRequest(Family f) => f switch
    {
        Family.Arctis7 => (new byte[] { 0x06, 0x18 }, 3),
        Family.Arctis9 => (new byte[] { 0x00, 0x20 }, 5),
        Family.ProWireless => (new byte[] { 0x40, 0xAA }, 1),
        Family.NovaProWireless => (new byte[] { 0x06, 0xB0 }, 16),
        _ => (new byte[] { 0x00, 0xB0 }, 4), // Nova 7 family and Arctis 7+
    };

    /// <summary>Decodes a battery reply (hidapi form). Null = headset off / unusable.</summary>
    internal static (int? Pct, ChargeState State)? Decode(Family f, byte[] r)
    {
        switch (f)
        {
            case Family.Arctis7:
                return (Math.Min((int)r[2], 100), ChargeState.Unknown);
            case Family.Arctis9:
                if (r[4] == 0xFF) return null;
                return (Math.Clamp((r[3] - 0x64) * 100 / (0x9A - 0x64), 0, 100),
                        r[4] == 0x01 ? ChargeState.Charging : ChargeState.Discharging);
            case Family.ProWireless:
                return (Math.Min(r[0], (byte)4) * 25, ChargeState.Unknown);
            case Family.NovaProWireless:
                if (r[15] == 0x01) return null;
                return ((int)Math.Round(Math.Min(r[6], (byte)8) * 12.5), r[15] == 0x02 ? ChargeState.Charging : ChargeState.Discharging);
            case Family.Arctis7Plus:
                if (r[1] == 0x01) return null;
                return (Math.Min(r[2], (byte)4) * 25, r[3] == 0x01 ? ChargeState.Charging : ChargeState.Discharging);
            default: // Nova 7
                if (r[3] == 0x00) return null; // headset off
                int pct = f == Family.Nova7Discrete ? Math.Min(r[2], (byte)4) * 25 : Math.Min((int)r[2], 100);
                return (pct, r[3] is 0x01 or 0x02 ? ChargeState.Charging : ChargeState.Discharging);
        }
    }

    public void Dispose() { }
}
