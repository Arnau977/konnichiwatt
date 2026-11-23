using HidSharp;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// Sony DualSense / DualSense Edge / DualShock 4. Purely passive: the battery is in every full input report,
/// we only listen (no output reports, so games and Steam are unaffected).
/// Layout and decoding from the Linux kernel driver drivers/hid/hid-playstation.c:
///   DualSense USB 0x01 (64 B) status @53 · BT 0x31 (78 B) status @54 — low nibble level (×10 +5), high nibble 0 dis / 1 chg / 2 full
///   DualShock 4 USB 0x01 (64 B) status @30 · BT 0x11 (78 B) status @32 — low nibble level, bit 4 cable
/// On Bluetooth the pad may send only the reduced 0x01 report (no battery) until some app (Steam, DS4Windows)
/// enables the full report; in that case we can't read it and the pad is not shown.
/// </summary>
public sealed class PlayStationProvider : IBatteryProvider
{
    private const int SonyVid = 0x054C;
    private static readonly Dictionary<int, (string Name, bool DualSense)> Pids = new()
    {
        [0x05C4] = ("DualShock 4", false),
        [0x09CC] = ("DualShock 4", false),
        [0x0BA0] = ("DualShock 4 (adaptador)", false),
        [0x0CE6] = ("DualSense", true),
        [0x0DF2] = ("DualSense Edge", true),
    };

    public string Name => "PlayStation";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var dev in DeviceList.Local.GetHidDevices(SonyVid))
        {
            if (!Pids.TryGetValue(dev.ProductID, out var info) || HidUtil.InLen(dev) < 64) continue;
            bool bt = HidUtil.IsBluetooth(dev);
            using var s = HidUtil.TryOpen(dev);
            if (s == null) continue;

            byte? status = null;
            HidUtil.ReadUntil(s, r =>
            {
                status = Status(r, info.DualSense, bt);
                return status != null;
            }, 500);
            if (status is not byte st) continue;

            var (pct, state) = info.DualSense ? DecodeDualSense(st) : DecodeDs4(st);
            if (pct == null && state == ChargeState.Unknown) continue;
            result.Add(new DeviceBattery($"ps:{HidUtil.StableHash(dev.DevicePath)}", info.Name, DeviceKind.Controller,
                pct, state, "playstation", Approximate: true, LastSeen: DateTime.Now));
        }
        return result;
    }

    internal static byte? Status(byte[] r, bool dualSense, bool bt)
    {
        if (r.Length == 0) return null;
        if (dualSense)
        {
            if (r[0] == 0x31 && r.Length >= 78) return r[54];
            if (r[0] == 0x01 && !bt && r.Length >= 64) return r[53];
        }
        else
        {
            if (r[0] == 0x11 && r.Length >= 78) return r[32];
            if (r[0] == 0x01 && !bt && r.Length >= 64)
            {
                // DS4 USB dongle with no pad linked sends an all-zero report (sticks rest at 0x80 when linked).
                if (r[1] == 0 && r[2] == 0 && r[3] == 0 && r[4] == 0) return null;
                return r[30];
            }
        }
        return null;
    }

    internal static (int?, ChargeState) DecodeDualSense(byte st)
    {
        int level = st & 0x0F, charging = st >> 4;
        return charging switch
        {
            0x0 => (Math.Min(level * 10 + 5, 100), ChargeState.Discharging),
            0x1 => (Math.Min(level * 10 + 5, 100), ChargeState.Charging),
            0x2 => (100, ChargeState.Full),
            _ => (null, ChargeState.Unknown),
        };
    }

    internal static (int?, ChargeState) DecodeDs4(byte st)
    {
        int level = st & 0x0F;
        bool cable = (st & 0x10) != 0;
        if (!cable) return (level < 10 ? level * 10 + 5 : 100, ChargeState.Discharging);
        if (level < 10) return (level * 10 + 5, ChargeState.Charging);
        if (level == 10) return (100, ChargeState.Charging);
        if (level == 11) return (100, ChargeState.Full);
        return (null, ChargeState.Unknown);
    }

    public void Dispose() { }
}
