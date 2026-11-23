using HidSharp;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// HyperX wireless headsets (HP-era firmware). The USB dongle answers vendor commands
/// on its 31-byte vendor collection: 21 BB &lt;cmd&gt; → 21 BB &lt;cmd&gt; &lt;value&gt;.
///   0x03 connection status (0x02 = headset on, 0x01 = off)
///   0x0B battery percent
///   0x0C charging (1 = charging)
/// </summary>
public sealed class HyperXProvider : IBatteryProvider
{
    private sealed record Model(int Vid, int Pid, string Name);

    private static readonly Model[] Models =
    {
        new(0x03F0, 0x098D, "HyperX Cloud Alpha Wireless"),
    };

    private const byte CmdStatus = 0x03, CmdBattery = 0x0B, CmdCharging = 0x0C;

    public string Name => "HyperX";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        PollCloud2Wireless(result);
        PollCloudFlight(result);
        foreach (var model in Models)
        {
            var dev = DeviceList.Local.GetHidDevices(model.Vid, model.Pid)
                .FirstOrDefault(d => SafeOutLen(d) >= 3);
            if (dev == null) continue;

            using var s = HidUtil.TryOpen(dev);
            if (s == null) continue;

            int outLen = dev.GetMaxOutputReportLength();
            var status = Query(s, outLen, CmdStatus);
            if (status == 0x01) continue; // dongle present, headset switched off
            var pct = Query(s, outLen, CmdBattery);
            if (pct is null or > 100) continue;
            var chg = Query(s, outLen, CmdCharging);

            result.Add(new DeviceBattery(
                Id: $"hyperx:{model.Vid:X4}:{model.Pid:X4}",
                Name: model.Name,
                Kind: DeviceKind.Headset,
                Percent: pct,
                State: chg == 1 ? ChargeState.Charging : ChargeState.Discharging,
                Source: "hyperx",
                LastSeen: DateTime.Now));
        }
        return result;
    }

    private static byte? Query(HidStream s, int outLen, byte cmd)
    {
        var buf = new byte[outLen];
        buf[0] = 0x21; buf[1] = 0xBB; buf[2] = cmd;
        try { s.Write(buf); }
        catch (IOException) { return null; }
        catch (TimeoutException) { return null; }
        var r = HidUtil.ReadUntil(s, r => r.Length > 3 && r[0] == 0x21 && r[1] == 0xBB && r[2] == cmd, 1000);
        return r?[3];
    }

    // ---- Other models, protocol from HeadsetControl (hyperx_cloud_2_wireless.hpp, hyperx_cloud_flight.hpp) -----------

    /// <summary>Cloud II Wireless: 52-byte writes 06 FF BB &lt;cmd&gt;; cmd 02 → [7] = %, cmd 03 → [4] = 1 charging.</summary>
    private static void PollCloud2Wireless(List<DeviceBattery> result)
    {
        foreach (var pid in new[] { 0x0696, 0x018B })
        {
            var dev = DeviceList.Local.GetHidDevices(0x03F0, pid).FirstOrDefault(d => HidUtil.TopUsage(d) == 0xFF900303);
            if (dev == null) continue;
            using var s = HidUtil.TryOpen(dev);
            if (s == null) continue;
            var lvl = HidUtil.Transact(s, dev, new byte[] { 0x06, 0xFF, 0xBB, 0x02 },
                r => r.Length >= 8 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == 0x02);
            if (lvl == null || lvl[7] > 100) continue;
            var chg = HidUtil.Transact(s, dev, new byte[] { 0x06, 0xFF, 0xBB, 0x03 },
                r => r.Length >= 5 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == 0x03);
            result.Add(new DeviceBattery($"hyperx:03F0:{pid:X4}", "HyperX Cloud II Wireless", DeviceKind.Headset, lvl[7],
                chg?[4] == 1 ? ChargeState.Charging : ChargeState.Discharging, "hyperx", LastSeen: DateTime.Now));
        }
    }

    /// <summary>Cloud Flight: write 21 FF 05 → voltage BE16 at [3..4]; &gt; 4107 mV means charging.</summary>
    private static void PollCloudFlight(List<DeviceBattery> result)
    {
        foreach (var pid in new[] { 0x16C4, 0x1723 })
        {
            var dev = DeviceList.Local.GetHidDevices(0x0951, pid).FirstOrDefault(d => HidUtil.OutLen(d) >= 20);
            if (dev == null) continue;
            using var s = HidUtil.TryOpen(dev);
            if (s == null) continue;
            var r = HidUtil.Transact(s, dev, new byte[] { 0x21, 0xFF, 0x05 }, r => r.Length >= 5 && r[0] == 0x21, 2000);
            if (r == null) continue;
            int mv = (r[3] << 8) | r[4];
            if (mv <= 0) continue;
            bool charging = mv > 0x100B;
            result.Add(new DeviceBattery($"hyperx:0951:{pid:X4}", "HyperX Cloud Flight", DeviceKind.Headset,
                charging ? null : FlightPercent(mv), charging ? ChargeState.Charging : ChargeState.Discharging,
                "hyperx", Approximate: true, LastSeen: DateTime.Now));
        }
    }

    private static int FlightPercent(int v)
    {
        if (v <= 3648) return (int)Math.Round(0.00125 * v);
        if (v > 3975) return 100;
        double p = 0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3) + 2.238321 * v * v - 5706.256 * v + 5452299.0;
        return (int)Math.Clamp(Math.Round(p), 0, 100);
    }

    private static int SafeOutLen(HidDevice d)
    {
        try { return d.GetMaxOutputReportLength(); } catch { return 0; }
    }

    public void Dispose() { }
}
