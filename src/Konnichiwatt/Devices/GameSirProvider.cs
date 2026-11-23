using HidSharp;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// GameSir 2.4 GHz dongles of the "SAPJoyUni" family (T4n Pro / Nova Pro, dongle 3537:103E).
/// In PC mode the pad is presented as a wired Xbox 360 controller (no battery via XInput), but the
/// dongle has a vendor HID collection (usage page 0xFFA0): output report 0x07 (32 B), input 0x06 (64 B).
///
/// Frames: [reportId][len][group][op][payload…]. Device info (what GameSir Connect calls GetDeviceInfo):
///   reply 06 0E 0A 01 | id | fw(2) | mac(6) | battery | type
///   battery: 0-100 %, 255 = charging.
/// The 0x06 stream also carries gamepad-state frames (len 0x1A) and idle zero frames, which we skip.
/// </summary>
public sealed class GameSirProvider : IBatteryProvider
{
    private const int GameSirVid = 0x3537;
    private const byte GroupInfo = 0x0A, OpDeviceInfo = 0x01;

    // Candidate encodings of the request; the one that works is remembered.
    private static readonly byte[][] Requests =
    {
        new byte[] { 0x07, 0x03, GroupInfo, OpDeviceInfo },
        new byte[] { 0x07, 0x04, GroupInfo, OpDeviceInfo, 0x00 },
    };
    private int _working = -1;

    public string Name => "GameSir (dongle)";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var dev in DeviceList.Local.GetHidDevices(GameSirVid))
        {
            try
            {
                if (dev.GetMaxOutputReportLength() != 32 || dev.GetMaxInputReportLength() != 64) continue;
            }
            catch { continue; }

            using var s = HidUtil.TryOpen(dev);
            if (s == null) continue;

            var info = QueryInfo(s);
            if (info == null) continue; // dongle without a linked controller (pad off)

            int raw = info[13];
            var mac = BitConverter.ToString(info, 7, 6).Replace("-", "");
            result.Add(new DeviceBattery(
                Id: $"gamesir:{mac}",
                Name: GameSirName(dev.ProductID),
                Kind: DeviceKind.Controller,
                Percent: raw == 0xFF ? null : Math.Min(raw, 100),
                State: raw == 0xFF ? ChargeState.Charging : ChargeState.Discharging,
                Source: "gamesir",
                LastSeen: DateTime.Now));
        }
        return result;
    }

    private byte[]? QueryInfo(HidStream s)
    {
        var order = _working >= 0 ? new[] { _working } : Enumerable.Range(0, Requests.Length).ToArray();
        foreach (var i in order)
        {
            var buf = new byte[32];
            Requests[i].CopyTo(buf, 0);
            try { s.Write(buf); } catch { return null; }
            var r = HidUtil.ReadUntil(s, r => r.Length >= 14 && r[0] == 0x06 && r[1] >= 0x0D &&
                                             r[2] == GroupInfo && r[3] == OpDeviceInfo, 800);
            if (r != null)
            {
                if (_working != i) Log.Write($"GameSir: device-info request #{i} works");
                _working = i;
                return r;
            }
        }
        return null;
    }

    private static string GameSirName(int pid) => pid switch
    {
        0x103E => "GameSir T4n Pro",
        _ => "Mando GameSir",
    };

    public void Dispose() { }
}
