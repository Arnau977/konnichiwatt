using HidSharp;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// Corsair wireless headsets, protocol from HeadsetControl (lib/devices/corsair_void_rich.hpp, corsair_void_v2w.hpp).
///  • Void family (Void Wireless / RGB Elite / Pro…): write C9 64 → [2] = % (bit 7 = mic-up flag), [4] = 0 off · 1/2 on · 4/5 charging
///  • Wireless V2 receivers (Void Wireless V2, Virtuoso Max, HS80 Max): Corsair "Bragi" framing
///    00 02 09 02 12 = headset heartbeat (no reply → off), 00 02 09 02 0F = battery, value LE16 at [4..5] in tenths of %.
/// Corsair wireless mice/keyboards are not included: their Bragi interface needs a mode switch that would fight iCUE.
/// </summary>
public sealed class CorsairHeadsetProvider : IBatteryProvider
{
    private const int Vid = 0x1B1C;

    private static readonly HashSet<int> VoidPids = new()
    {
        0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23,
        0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56,
    };

    private static readonly Dictionary<int, string> V2Pids = new()
    {
        [0x2A08] = "Corsair Void Wireless V2",
        [0x2A02] = "Corsair Virtuoso Max Wireless",
        [0x0A97] = "Corsair HS80 Max Wireless",
    };

    public string Name => "Corsair (auriculares)";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var group in DeviceList.Local.GetHidDevices(Vid).GroupBy(d => d.ProductID))
        {
            if (VoidPids.Contains(group.Key))
            {
                var dev = group.FirstOrDefault(d => HidUtil.TopUsage(d) == 0xFFC50001 && HidUtil.OutLen(d) > 0);
                if (dev != null && ReadVoid(dev) is DeviceBattery b) result.Add(b);
            }
            else if (V2Pids.TryGetValue(group.Key, out var name))
            {
                var dev = group.FirstOrDefault(d => HidUtil.Interface(d) == 4 && HidUtil.OutLen(d) >= 64);
                if (dev != null && ReadV2(dev, name) is DeviceBattery b) result.Add(b);
            }
        }
        return result;
    }

    private static DeviceBattery? ReadVoid(HidDevice dev)
    {
        using var s = HidUtil.TryOpen(dev);
        if (s == null) return null;
        var r = HidUtil.Transact(s, dev, new byte[] { 0xC9, 0x64 }, r => r.Length >= 5);
        if (r == null || r[4] == 0 || r[4] is not (1 or 2 or 4 or 5)) return null;
        int pct = Math.Min(r[2] & 0x7F, 100);
        return new DeviceBattery($"corsair:{dev.ProductID:X4}", HidUtil.SafeProductName(dev, "Corsair Void"), DeviceKind.Headset,
            pct, r[4] is 4 or 5 ? ChargeState.Charging : ChargeState.Discharging, "corsair", LastSeen: DateTime.Now);
    }

    private static DeviceBattery? ReadV2(HidDevice dev, string name)
    {
        using var s = HidUtil.TryOpen(dev);
        if (s == null) return null;

        // Headset heartbeat through the receiver: no answer means the headset is off.
        // HeadsetControl does not validate replies beyond their length; we do the same and range-check the value.
        var hb = HidUtil.Transact(s, dev, new byte[] { 0x00, 0x02, 0x09, 0x02, 0x12 }, r => r.Length >= 3, 800);
        if (hb == null) return null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var r = HidUtil.Transact(s, dev, new byte[] { 0x00, 0x02, 0x09, 0x02, 0x0F },
                r => r.Length >= 6, 800);
            if (r == null) continue;
            int tenths = r[4] | (r[5] << 8);
            if (tenths is <= 0 or > 1000) continue;
            return new DeviceBattery($"corsair:{dev.ProductID:X4}", name, DeviceKind.Headset, tenths / 10,
                ChargeState.Unknown, "corsair", LastSeen: DateTime.Now);
        }
        return null;
    }

    public void Dispose() { }
}
