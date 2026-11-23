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
            var main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) wanted.Add((main, true));
            if (_settings.ShowOnAllTaskbars)
            {
                var h = IntPtr.Zero;
                while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                    wanted.Add((h, false));
            }
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

    public void Dispose()
    {
        foreach (var w in _widgets.Values) w.Dispose();
        _widgets.Clear();
        _anchors.Dispose();
    }
}
