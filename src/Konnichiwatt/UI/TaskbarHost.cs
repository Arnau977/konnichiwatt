using Konnichiwatt.Core;
using static Konnichiwatt.UI.Native;

namespace Konnichiwatt.UI;

/// <summary>
/// Keeps one <see cref="TaskbarWidget"/> per taskbar: the main one (Shell_TrayWnd) and, if enabled, those of
/// secondary monitors (Shell_SecondaryTrayWnd). Taskbars come and go (monitor plugged, Explorer restart), so the
/// set is re-synchronised on every refresh.
/// </summary>
internal sealed class TaskbarHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly TaskbarAnchorService _anchors = new();
    private readonly Dictionary<IntPtr, TaskbarWidget> _widgets = new();
    private IReadOnlyList<DeviceBattery> _devices = Array.Empty<DeviceBattery>();
    private bool _fallbackLogged;

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action<TaskbarWidget, bool>? HoverChanged;

    public TaskbarHost(AppSettings settings) => _settings = settings;

    public void SetDevices(IReadOnlyList<DeviceBattery> devices)
    {
        _devices = devices;
        Sync();
        foreach (var w in _widgets.Values) w.SetDevices(devices);
    }

    public void Refresh(bool force = false)
    {
        Sync();
        foreach (var w in _widgets.Values) w.Refresh(force);
    }

    private void Sync()
    {
        var wanted = new List<(IntPtr Handle, bool Primary)>();
        if (_settings.ShowTaskbarWidget)
        {
            var monitors = MonitorsCached();
            bool Shows(IntPtr taskbar, bool primary) =>
                _settings.ShowsOnMonitor(Monitors.IdForWindow(taskbar, monitors), primary);

            var main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero && Shows(main, true)) wanted.Add((main, true));
            var h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                if (Shows(h, false)) wanted.Add((h, false));

            // The chosen screens may have no taskbar right now (Windows' "show my taskbar on all displays" is
            // off, or that monitor is unplugged): never leave the user without the widget — use the main taskbar.
            bool fallback = wanted.Count == 0 && main != IntPtr.Zero;
            if (fallback)
            {
                wanted.Add((main, true));
                if (!_fallbackLogged) Log.Write("Ninguna pantalla elegida tiene barra de tareas: se usa la principal");
            }
            _fallbackLogged = fallback;
        }
        _anchors.Track(wanted.Where(t => !t.Primary).Select(t => t.Handle));

        foreach (var (handle, widget) in _widgets.ToList())
        {
            if (wanted.Any(t => t.Handle == handle) && !widget.IsDead) continue;
            widget.Dispose();
            _widgets.Remove(handle);
        }
        foreach (var (handle, primary) in wanted)
        {
            if (_widgets.ContainsKey(handle)) continue;
            var w = new TaskbarWidget(_settings, handle, primary, _anchors.Get);
            w.LeftClick += () => LeftClick?.Invoke();
            w.RightClick += () => RightClick?.Invoke();
            w.HoverChanged += (widget, hover) => HoverChanged?.Invoke(widget, hover);
            _widgets[handle] = w;
            w.SetDevices(_devices);
        }
    }

    // Monitor names/ids only change when displays are reconfigured; re-query at most every few seconds.
    private List<MonitorDesc> _monitors = new();
    private long _monitorsAt;

    private List<MonitorDesc> MonitorsCached()
    {
        if (_monitors.Count == 0 || Environment.TickCount64 - _monitorsAt > 5000)
        {
            _monitors = Monitors.All();
            _monitorsAt = Environment.TickCount64;
        }
        return _monitors;
    }

    /// <summary>Call when displays change so the next sync sees the new monitors at once.</summary>
    public void InvalidateMonitors() => _monitorsAt = 0;

    public void Dispose()
    {
        foreach (var w in _widgets.Values) w.Dispose();
        _widgets.Clear();
        _anchors.Dispose();
    }
}
