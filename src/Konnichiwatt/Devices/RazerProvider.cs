using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// Razer wireless mice / keyboards. Protocol from OpenRazer (driver/razercommon.*, razerchromacommon.c, razermouse_driver.*):
/// 90-byte feature report (report ID 0) — [status][transaction id][remaining(2)][protocol][data size][class][id][args 80][crc][0].
///   battery level: class 0x07 id 0x80 size 0x02 → args[1] 0-255
///   charging:      class 0x07 id 0x84 size 0x02 → args[1] 0/1
///   crc = XOR of bytes 2..87. Reply status 0x02 = OK, 0x01 = busy (retry), others = failure / device asleep.
/// Read-only commands only. Feature reports are exchanged through a handle opened with no read/write access,
/// which Windows allows even on the mouse collection it owns exclusively.
/// </summary>
public sealed class RazerProvider : IBatteryProvider
{
    private const int RazerVid = 0x1532;

    private sealed record Model(string Name, DeviceKind Kind, byte Tid, int WaitMs, int Interface = 0, bool HasCharge = true);

    private const int W600 = 1, W31 = 31, W60 = 60, W400 = 400;

    private static readonly Dictionary<int, Model> Models = BuildModels();

    private static Dictionary<int, Model> BuildModels()
    {
        var m = new Dictionary<int, Model>();
        void Mouse(string name, byte tid, int wait, bool charge, params int[] pids)
        {
            foreach (var p in pids) m[p] = new Model(name, DeviceKind.Mouse, tid, wait, HasCharge: charge);
        }
        Mouse("Razer Lancehead Wireless", 0x3F, W600, true, 0x005A, 0x0059);
        Mouse("Razer Lancehead Wireless", 0x1F, W31, true, 0x006F, 0x0070);
        Mouse("Razer Mamba Wireless", 0x3F, W31, true, 0x0072, 0x0073);
        Mouse("Razer Mamba Wireless", 0xFF, W600, true, 0x0045, 0x0044, 0x0025, 0x0024);
        Mouse("Razer DeathAdder V2 Pro", 0x3F, W60, true, 0x007D, 0x007C);
        Mouse("Razer DeathAdder V2 X HyperSpeed", 0x1F, W31, false, 0x009C);
        Mouse("Razer DeathAdder V3 Pro", 0x1F, W31, true, 0x00B7, 0x00B6, 0x00C3, 0x00C2);
        Mouse("Razer DeathAdder V3 HyperSpeed", 0x1F, W31, true, 0x00C5, 0x00C4);
        Mouse("Razer DeathAdder V4 Pro", 0x1F, W31, true, 0x00BF, 0x00BE);
        Mouse("Razer Viper Ultimate", 0xFF, W60, true, 0x007B, 0x007A);
        Mouse("Razer Viper V2 Pro", 0x1F, W31, true, 0x00A6, 0x00A5);
        Mouse("Razer Viper Mini SE", 0x1F, W60, true, 0x009F, 0x009E);
        Mouse("Razer Viper V3 HyperSpeed", 0x1F, W60, false, 0x00B8);
        Mouse("Razer HyperPolling Dongle", 0x1F, W60, true, 0x00B3);
        Mouse("Razer Viper V3 Pro", 0x1F, W60, true, 0x00C1);
        Mouse("Razer Viper V3 Pro", 0x1F, W31, true, 0x00C0);
        Mouse("Razer Viper V3 Pro SE", 0x1F, W60, true, 0x00DF);
        Mouse("Razer Viper V3 Pro SE", 0x1F, W31, true, 0x00DE);
        m[0x00E6] = new Model("Razer Viper V4 Pro", DeviceKind.Mouse, 0x1F, W60, Interface: 3);
        m[0x00E5] = new Model("Razer Viper V4 Pro", DeviceKind.Mouse, 0x1F, W60, Interface: 3);
        Mouse("Razer Basilisk Ultimate", 0x1F, W31, true, 0x0088, 0x0086);
        Mouse("Razer Basilisk X HyperSpeed", 0xFF, W31, false, 0x0083);
        Mouse("Razer Basilisk V3 Pro", 0x1F, W31, true, 0x00AB, 0x00AA);
        Mouse("Razer Basilisk V3 Pro 35K", 0x1F, W31, true, 0x00CD, 0x00CC, 0x00D7, 0x00D6);
        Mouse("Razer Basilisk V3 X HyperSpeed", 0x1F, W31, false, 0x00B9);
        Mouse("Razer Basilisk Mobile", 0x1F, W31, false, 0x00D4, 0x00D3);
        Mouse("Razer Naga Pro", 0x1F, W31, true, 0x0090);
        Mouse("Razer Naga Pro", 0x1F, W600, true, 0x008F);
        Mouse("Razer Naga V2 Pro", 0x1F, W31, true, 0x00A8, 0x00A7);
        Mouse("Razer Naga V2 HyperSpeed", 0x1F, W31, false, 0x00B4);
        Mouse("Razer Orochi V2", 0x1F, W400, false, 0x0094, 0x0095, 0x00E0);
        Mouse("Razer Atheris", 0x1F, W400, false, 0x0062);
        Mouse("Razer Cobra HyperSpeed", 0x1F, W31, true, 0x00DB, 0x00DA);
        Mouse("Razer Cobra Pro", 0x1F, W31, true, 0x00B0, 0x00AF);
        Mouse("Razer Pro Click", 0x1F, W31, true, 0x0077, 0x0080, 0x009A);
        Mouse("Razer Pro Click V2", 0x1F, W31, true, 0x00D1, 0x00D0);
        Mouse("Razer Pro Click V2 Vertical", 0x1F, W31, true, 0x00C8, 0x00C7);

        // Keyboards: PIDs from OpenRazer razerkbd_driver.h; transaction id not confirmed → 0 = auto-detect.
        void Kbd(string name, params int[] pids)
        {
            foreach (var p in pids) m[p] = new Model(name, DeviceKind.Keyboard, 0, W31);
        }
        Kbd("Razer BlackWidow V3 Pro", 0x025A, 0x025C);
        Kbd("Razer BlackWidow V3 Mini HyperSpeed", 0x0258, 0x0271);
        Kbd("Razer BlackWidow V4 Mini HyperSpeed", 0x02B9, 0x02BA);
        Kbd("Razer BlackWidow V4 Low Profile TKL HyperSpeed", 0x02D4, 0x02D2);
        Kbd("Razer BlackWidow V4 TKL HyperSpeed", 0x02D7, 0x02D5);
        Kbd("Razer DeathStalker V2 Pro", 0x0292, 0x0290);
        Kbd("Razer DeathStalker V2 Pro TKL", 0x0298, 0x0296);
        return m;
    }

    private readonly Dictionary<int, byte> _detectedTid = new();

    public string Name => "Razer";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        var done = new HashSet<int>();
        foreach (var dev in DeviceList.Local.GetHidDevices(RazerVid))
        {
            if (!Models.TryGetValue(dev.ProductID, out var model) || done.Contains(dev.ProductID)) continue;
            if (HidUtil.FeatureLen(dev) != 91) continue;
            int iface = HidUtil.Interface(dev);
            if (iface >= 0 && iface != model.Interface) continue;

            using var h = OpenNoAccess(dev.DevicePath);
            if (h == null) continue;

            var tids = model.Tid != 0 ? new[] { model.Tid }
                     : _detectedTid.TryGetValue(dev.ProductID, out var t) ? new[] { t }
                     : new byte[] { 0x1F, 0x3F, 0xFF };
            foreach (var tid in tids)
            {
                var level = Query(h, tid, 0x80, model.WaitMs);
                if (level == null) continue;
                if (model.Tid == 0) _detectedTid[dev.ProductID] = tid;

                bool charging = model.HasCharge && Query(h, tid, 0x84, model.WaitMs) == 1;
                int pct = (int)Math.Round(level.Value * 100 / 255.0);
                result.Add(new DeviceBattery($"razer:{dev.ProductID:X4}", model.Name, model.Kind, pct,
                    charging ? (pct >= 100 ? ChargeState.Full : ChargeState.Charging) : ChargeState.Discharging,
                    "razer", LastSeen: DateTime.Now));
                done.Add(dev.ProductID);
                break;
            }
        }
        return result;
    }

    /// <summary>Sends a class-0x07 "get" and returns args[1], or null if the device did not answer OK.</summary>
    private static int? Query(SafeFileHandle h, byte tid, byte commandId, int waitMs)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var req = new byte[91]; // [0] = report ID 0, then the 90-byte razer_report
            req[2] = tid;
            req[6] = 0x02;     // data size
            req[7] = 0x07;     // command class: misc / power
            req[8] = commandId;
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= req[i]; // report bytes 2..87
            req[89] = crc;

            if (!HidD_SetFeature(h, req, req.Length)) return null;
            Thread.Sleep(Math.Max(1, waitMs));
            var resp = new byte[91];
            if (!HidD_GetFeature(h, resp, resp.Length)) return null;

            byte status = resp[1];
            if (status == 0x01) { Thread.Sleep(10); continue; } // busy
            if (status != 0x02 || resp[7] != 0x07 || resp[8] != commandId) return null;
            return resp[10]; // args[1]
        }
        return null;
    }

    private static SafeFileHandle? OpenNoAccess(string path)
    {
        var h = CreateFile(path, 0, 3 /* share read|write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    public void Dispose() { }
}
