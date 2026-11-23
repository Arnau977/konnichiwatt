using System.Diagnostics;
using System.Drawing.Text;
using Microsoft.Win32;
using Konnichiwatt.Core;
using Konnichiwatt.UI;

namespace Konnichiwatt;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DeviceMonitor _monitor;
    private readonly TaskbarHost _taskbars;
    private readonly DetailsPopup _popup = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _layoutTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 350 };
    private readonly HashSet<string> _lowNotified = new();
    private readonly HashSet<string> _chargingIncomplete = new();
    private IReadOnlyList<DeviceBattery> _all = Array.Empty<DeviceBattery>();
    private TaskbarWidget? _hovered;
    /// <summary>Set by a right click: no hover flyout until the mouse leaves the widget.</summary>
    private bool _suppressPopup;
    private SettingsWindow? _settingsWindow;
    private IntPtr _trayIconHandle;
    private int _lastThreshold;

    public TrayApplicationContext(bool openSettings = false)
    {
        _lastThreshold = _settings.LowBatteryThreshold;
        _monitor = new DeviceMonitor(Program.CreateProviders(), TimeSpan.FromSeconds(_settings.PollSeconds));
        _monitor.Updated += OnUpdated;

        _taskbars = new TaskbarHost(_settings);
        _taskbars.RightClick += () => { _suppressPopup = true; ShowMenuAtCursor(); };
        _taskbars.LeftClick += () => _monitor.RequestPoll();
        // Any click cancels the pending flyout, so it can't pop up over (and close) the context menu.
        _taskbars.Pressed += () => _hoverTimer.Stop();
        _taskbars.HoverChanged += (widget, hover) =>
        {
            if (hover)
            {
                _hovered = widget;
                if (!_suppressPopup && !_menu.Visible) _hoverTimer.Start();
            }
            else if (widget == _hovered)
            {
                _hoverTimer.Stop();
                _popup.Hide();
                _hovered = null;
                _suppressPopup = false; // leaving the widget re-arms the flyout
            }
        };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_suppressPopup || _menu.Visible) return;
            ShowPopup();
        };

        _menu.Opening += (_, _) => BuildMenu();
        _tray = new NotifyIcon { Text = "Konnichiwatt", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };
        UpdateTrayIcon();

        _layoutTimer.Tick += (_, _) => _taskbars.Refresh();
        _layoutTimer.Start();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        _monitor.Start();
        Log.Write("Started");
        if (openSettings) OpenSettings();
    }

    private List<DeviceBattery> Customized => _settings.Arrange(_all);
    private List<DeviceBattery> Visible => Customized.Where(_settings.IsShown).ToList();

    private void OnUpdated(IReadOnlyList<DeviceBattery> devices)
    {
        _all = devices;
        Republish();
        var customized = Customized;
        CheckLowBattery(customized.Where(d => !_settings.HiddenDevices.Contains(d.Id)));
        CheckChargeComplete(customized);
    }

    /// <summary>Pushes the current list to every view (after a poll or a settings change).</summary>
    private void Republish()
    {
        _taskbars.SetDevices(Visible);
        if (_popup.Visible) ShowPopup();
        UpdateTrayIcon();
        _settingsWindow?.RefreshDevices();
    }

    private void ShowPopup()
    {
        var w = _hovered;
        var visible = Visible;
        if (w == null || visible.Count == 0) { _popup.Hide(); return; }
        _popup.ShowNear(w.ScreenBounds, w.TaskbarBounds, visible, w.Scale);
    }

    private void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        _taskbars.Refresh(force: true);
        UpdateTrayIcon();
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            _settingsWindow?.ApplyTheme();
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _taskbars.InvalidateMonitors();
        _taskbars.Refresh(force: true);
        _settingsWindow?.DisplaysChanged();
    }

    /// <summary>Any change made in the settings window: persist it and apply it everywhere.</summary>
    private void OnSettingsChanged()
    {
        _settings.PruneCustomizations();
        _settings.Save();
        _monitor.Interval = TimeSpan.FromSeconds(_settings.PollSeconds);
        if (_settings.LowBatteryThreshold != _lastThreshold)
        {
            _lastThreshold = _settings.LowBatteryThreshold;
            _lowNotified.Clear();
        }
        _taskbars.SetDevices(Visible);
        _taskbars.Refresh(force: true);
        if (_popup.Visible) ShowPopup();
        UpdateTrayIcon();
    }

    private void OpenSettings()
    {
        _popup.Hide();
        if (_settingsWindow is { IsDisposed: false })
        {
            if (_settingsWindow.WindowState == FormWindowState.Minimized) _settingsWindow.WindowState = FormWindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, new SettingsWindow.Callbacks(
            RawDevices: () => _all,
            Changed: OnSettingsChanged,
            PollNow: _monitor.RequestPoll,
            Diagnostics: RunDiagnostics,
            TestNotification: () => _tray.ShowBalloonTip(5000, "Konnichiwatt 👋⚡",
                "Así se verán los avisos de batería baja y de carga completa.", ToolTipIcon.Info)),
            _tray.Icon is Icon i ? (Icon)i.Clone() : null);
        _settingsWindow.FormClosed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ---- Tray icon -------------------------------------------------------------------

    private void UpdateTrayIcon()
    {
        var visible = Visible;
        var lowest = visible.Where(d => !d.Stale && d.Percent != null).OrderBy(d => d.Percent).FirstOrDefault();
        bool light = Theme.IsLightTaskbar();
        int size = SystemInformation.SmallIconSize.Width * 2;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var f = Theme.IconFont(size * 0.95f);
            bool low = lowest?.Percent <= _settings.LowBatteryThreshold && lowest?.IsCharging == false;
            using var b = new SolidBrush(low ? Theme.Low(light) : Theme.Foreground(light));
            var glyph = Theme.BatteryGlyph(lowest?.Percent, lowest?.IsCharging ?? false);
            var s = g.MeasureString(glyph, f, PointF.Empty, StringFormat.GenericTypographic);
            g.DrawString(glyph, f, b, (size - s.Width) / 2, (size - s.Height) / 2, StringFormat.GenericTypographic);
        }
        var hicon = bmp.GetHicon();
        var old = _tray.Icon;
        _tray.Icon = Icon.FromHandle(hicon);
        old?.Dispose();
        if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);
        _trayIconHandle = hicon;

        var tip = visible.Count == 0
            ? "Konnichiwatt\nNingún dispositivo detectado"
            : string.Join("\n", visible.Select(d => $"{d.Name}: {Theme.PercentText(d, true)}{(d.IsCharging ? " ⚡" : "")}{(d.Stale ? " (reposo)" : "")}"));
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    // ---- Notifications ---------------------------------------------------------------

    private void CheckLowBattery(IEnumerable<DeviceBattery> devices)
    {
        foreach (var d in devices)
        {
            if (d.Percent is not int p || d.Stale) continue;
            bool low = p <= _settings.LowBatteryThreshold && !d.IsCharging;
            if (low && _lowNotified.Add(d.Id) && _settings.LowBatteryNotifications)
                _tray.ShowBalloonTip(8000, "Batería baja", $"{d.Name}: {p}%", ToolTipIcon.Warning);
            else if (!low && (d.IsCharging || p > _settings.LowBatteryThreshold + 5))
                _lowNotified.Remove(d.Id);
        }
    }

    /// <summary>
    /// Notifies once when a device we saw charging (below 100 %) reaches full charge. A device that is already
    /// full when the app starts is not announced.
    /// </summary>
    private void CheckChargeComplete(IEnumerable<DeviceBattery> devices)
    {
        foreach (var d in devices)
        {
            if (d.Stale) continue;
            bool complete = d.State == ChargeState.Full || (d.IsCharging && d.Percent >= 100);
            if (d.IsCharging && !complete)
                _chargingIncomplete.Add(d.Id);
            else if (complete)
            {
                if (_chargingIncomplete.Remove(d.Id) && _settings.ChargeCompleteNotifications)
                    _tray.ShowBalloonTip(8000, "Carga completa", $"{d.Name} está al 100 %. Ya puedes desconectarlo.", ToolTipIcon.Info);
            }
            else
                _chargingIncomplete.Remove(d.Id); // unplugged before finishing
        }
    }

    // ---- Menu (the essentials; everything else lives in the settings window) -----------

    private void ShowMenuAtCursor()
    {
        _popup.Hide();
        // Required so the menu closes when clicking elsewhere (we are not the foreground app).
        Native.SetForegroundWindow(_menu.Handle);
        _menu.Show(Cursor.Position);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var visible = Visible;
        if (visible.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("Ningún dispositivo visible") { Enabled = false });
        foreach (var d in visible)
        {
            var text = $"{d.Name}  —  {Theme.PercentText(d, true)} {Theme.StateText(d).ToLowerInvariant()}";
            _menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Actualizar ahora", null, (_, _) => _monitor.RequestPoll());
        var settings = new ToolStripMenuItem("Ajustes…", null, (_, _) => OpenSettings());
        settings.Font = new Font(settings.Font, FontStyle.Bold);
        _menu.Items.Add(settings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Salir", null, (_, _) => ExitThread());
    }

    private void RunDiagnostics()
    {
        Task.Run(() =>
        {
            try
            {
                var text = _monitor.BuildDiagnostics();
                Directory.CreateDirectory(Log.Directory);
                var path = Path.Combine(Log.Directory, "diagnostico.txt");
                File.WriteAllText(path, text);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception e) { Log.Error("Diagnostics", e); }
        });
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _settingsWindow?.Close();
        _layoutTimer.Stop();
        _monitor.Dispose();
        _taskbars.Dispose();
        _popup.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);
        base.ExitThreadCore();
    }
}
