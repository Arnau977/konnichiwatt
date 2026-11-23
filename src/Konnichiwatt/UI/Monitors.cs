using System.Runtime.InteropServices;
using Konnichiwatt.Core;

namespace Konnichiwatt.UI;

/// <param name="Id">Stable identifier: the monitor's device path (survives reboots and re-numbering).</param>
/// <param name="Name">Commercial name from the EDID (e.g. "LG ULTRAGEAR"), or "Pantalla N".</param>
/// <param name="Number">Windows display number (the N in \\.\DISPLAYN).</param>
/// <param name="Friendly">The name comes from the monitor (EDID), not our "Display N" fallback.</param>
internal sealed record MonitorDesc(string Id, string Name, string GdiName, Rectangle Bounds, bool Primary, int Number, bool Friendly);

/// <summary>
/// Connected monitors with friendly names and stable ids, via the Display Configuration API
/// (QueryDisplayConfig / DisplayConfigGetDeviceInfo) — the same source as Windows' display settings.
/// </summary>
internal static class Monitors
{
    public static List<MonitorDesc> All()
    {
        var names = QueryNames();
        return Screen.AllScreens
            .Select(s =>
            {
                int number = ParseNumber(s.DeviceName);
                var (id, friendly) = names.TryGetValue(s.DeviceName, out var n) ? n : (s.DeviceName, "");
                bool hasName = !string.IsNullOrWhiteSpace(friendly);
                var name = hasName ? friendly : L.ScreenN(number);
                return new MonitorDesc(id.ToLowerInvariant(), name, s.DeviceName, s.Bounds, s.Primary, number, hasName);
            })
            .OrderByDescending(m => m.Primary).ThenBy(m => m.Number)
            .ToList();
    }

    /// <summary>
    /// Ids of the monitors that currently have a taskbar. The main one always does; secondary ones only while
    /// Windows' "Show my taskbar on all displays" is on (Explorer then creates a Shell_SecondaryTrayWnd for each).
    /// </summary>
    public static HashSet<string> WithTaskbar(IReadOnlyList<MonitorDesc> monitors)
    {
        var ids = new HashSet<string>();
        var main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero && IdForWindow(main, monitors) is string mainId) ids.Add(mainId);
        var h = IntPtr.Zero;
        while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            if (IdForWindow(h, monitors) is string id) ids.Add(id);
        return ids;
    }

    /// <summary>Id of the monitor a window (e.g. a taskbar) is on.</summary>
    public static string? IdForWindow(IntPtr hwnd, IReadOnlyList<MonitorDesc> monitors)
    {
        var hmon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (hmon == IntPtr.Zero || !GetMonitorInfo(hmon, ref info)) return null;
        return monitors.FirstOrDefault(m => string.Equals(m.GdiName, info.szDevice, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static int ParseNumber(string gdiName) =>
        int.TryParse(new string(gdiName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()), out var n) ? n : 0;

    /// <summary>GDI name (\\.\DISPLAY1) → (device path, friendly name).</summary>
    private static Dictionary<string, (string Id, string Name)> QueryNames()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

            for (int i = 0; i < pathCount; i++)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = paths[i].sourceInfo.adapterId;
                source.header.id = paths[i].sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = paths[i].targetInfo.adapterId;
                target.header.id = paths[i].targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref target) != 0) continue;

                var id = string.IsNullOrEmpty(target.monitorDevicePath) ? source.viewGdiDeviceName : target.monitorDevicePath;
                result[source.viewGdiDeviceName] = (id, target.monitorFriendlyDeviceName ?? "");
            }
        }
        catch (Exception e) { Log.Error("QueryDisplayConfig", e); }
        return result;
    }

    // ---- Interop -------------------------------------------------------------------------

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
        public uint scaling; public uint refreshNumerator; public uint refreshDenominator; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    /// <summary>Only its size matters to us (64 bytes: type, id, adapter LUID and a 48-byte union).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor;
        public Native.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX info);
}
