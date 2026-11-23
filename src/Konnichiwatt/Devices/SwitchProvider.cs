using HidSharp;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// Nintendo Switch Pro Controller / Joy-Con, and third-party pads that emulate them
/// (e.g. GameSir T4n Pro in Switch mode). Battery lives in byte 2 of every 0x21/0x30 input report:
/// high nibble = level (8 full, 6 medium, 4 low, 2 critical, 0 empty) | charging bit.
/// We send a harmless "request device info" sub-command (0x02) so the pad answers with a 0x21 report
/// without changing its input mode.
/// </summary>
public sealed class SwitchProvider : IBatteryProvider
{
    private const int NintendoVid = 0x057E;
    private static readonly Dictionary<int, (string Name, DeviceKind Kind)> Pids = new()
    {
        [0x2009] = ("Pro Controller", DeviceKind.Controller),
        [0x2006] = ("Joy-Con (L)", DeviceKind.Controller),
        [0x2007] = ("Joy-Con (R)", DeviceKind.Controller),
    };

    private static readonly byte[] NeutralRumble = { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 };
    private byte _counter;

    public string Name => "Nintendo Switch";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var dev in DeviceList.Local.GetHidDevices(NintendoVid))
        {
            if (!Pids.TryGetValue(dev.ProductID, out var info)) continue;
            int outLen;
            try { outLen = dev.GetMaxOutputReportLength(); } catch { continue; }
            if (outLen < 12) continue;

            bool bluetooth = dev.DevicePath.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);
            var name = HidUtil.SafeProductName(dev, info.Name);
            var id = $"switch:{HidUtil.StableHash(dev.DevicePath)}";

            byte? status = null;
            using (var s = HidUtil.TryOpen(dev))
            {
                if (s != null) status = ReadStatusByte(s, outLen);
            }

            if (status is byte b)
            {
                var (pct, state) = DecodeStatus(b);
                result.Add(new DeviceBattery(id, name, info.Kind, pct, state, "switch", Approximate: true, LastSeen: DateTime.Now));
            }
            else if (!bluetooth)
            {
                // Plugged by USB and not initialised by anybody: we can't read it, but it is charging.
                result.Add(new DeviceBattery(id, name, info.Kind, null, ChargeState.Charging, "switch", LastSeen: DateTime.Now));
            }
        }
        return result;
    }

    /// <summary>Byte 2 of 0x21/0x30 reports: high nibble = level (8 full … 0 empty) | charging bit.</summary>
    internal static (int Percent, ChargeState State) DecodeStatus(byte b)
    {
        int nibble = b >> 4;
        int level = nibble & 0x0E;
        bool charging = (nibble & 0x01) != 0;
        int pct = level switch { >= 8 => 100, 6 => 70, 4 => 40, 2 => 15, _ => 0 };
        var state = charging ? (level >= 8 ? ChargeState.Full : ChargeState.Charging) : ChargeState.Discharging;
        return (pct, state);
    }

    private byte? ReadStatusByte(HidStream s, int outLen)
    {
        var buf = new byte[outLen];
        buf[0] = 0x01;
        buf[1] = (byte)(_counter++ & 0x0F);
        NeutralRumble.CopyTo(buf, 2);
        buf[10] = 0x02; // sub-command: request device info
        try { s.Write(buf); }
        catch { return null; }
        var r = HidUtil.ReadUntil(s, r => r.Length > 3 && r[0] is 0x21 or 0x30 or 0x31, 1000);
        return r?[2];
    }

    public void Dispose() { }
}
