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
    private string _lastThresholds = "";
    private readonly BatteryHistory _history = BatteryHistory.Load();
    private readonly Updater _updater = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60_000 }; // first check after 1 min
    private Action? _balloonClick;
    private string? _announcedVersion;
    private bool _installing;
    /// <summary>State was already saved before swapping the executable; don't touch disk again while exiting.</summary>
    private bool _savedForUpdate;
    /// <summary>Another launch of the app (open settings) or the installer (quit) talking to us.</summary>
    private IDisposable? _instanceListener;

    public TrayApplicationContext(bool openSettings = false, string? updatedFrom = null, bool justInstalled = false)
    {
        L.Init(_settings.Language);
        _lastThresholds = ThresholdsSignature();
        _popup.IsLow = _settings.IsLow;
        _popup.History = _history;
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

        _tray.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _tray.BalloonTipClosed += (_, _) => _balloonClick = null;
        if (Updater.CanSelfUpdate)
        {
            if (Updater.TestMode) _updateTimer.Interval = 5000;
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = (int)TimeSpan.FromHours(12).TotalMilliseconds;
                await CheckForUpdatesAsync(userInitiated: false);
            };
            _updateTimer.Start();
        }

        _monitor.Start();
        Log.Write($"Started {Updater.CurrentVersion.ToString(3)}");
        if (updatedFrom != null)
            Notify(L.UpdatedTitle, L.UpdatedBody(Updater.CurrentVersion.ToString(3)), ToolTipIcon.Info);
        else if (justInstalled)
            Notify(L.InstalledTitle, L.InstalledBody, ToolTipIcon.Info, OpenSettings);
        if (openSettings) OpenSettings();

        try { _instanceListener = AppInstance.Listen(show: OpenSettings, quit: ExitThread); }
        catch (Exception e) { Log.Error("Instance listener", e); }
    }

    // ---- Updates -----------------------------------------------------------------------

    private void Notify(string title, string body, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _tray.ShowBalloonTip(8000, title, body, icon);
    }

    /// <summary>Checks the manifest; installs automatically or offers it, depending on the setting.</summary>
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        var m = await _updater.CheckAsync();
        _settingsWindow?.UpdatesChanged();
        if (m == null) return;
        if (_settings.AutoUpdate && !userInitiated) await InstallUpdateAsync(m);
        else if (_announcedVersion != m.Version || userInitiated)
        {
            _announcedVersion = m.Version;
            Notify(L.UpdateAvailableTitle, L.UpdateAvailableBody(m.Version), ToolTipIcon.Info, () => _ = InstallUpdateAsync(m));
        }
    }

    private async Task InstallUpdateAsync(UpdateManifest m)
    {
        if (_installing) return;
        _installing = true;
        try
        {
            void SaveEverything()
            {
                _settings.Save();
                _history.Save();
                _monitor.SaveStateNow();
            }
            if (await _updater.InstallAsync(m, SaveEverything))
            {
                _savedForUpdate = true;
                ExitThread(); // the new version is already starting
                return;
            }
            Notify(L.UpdateFailedTitle, L.UpdateFailedBody, ToolTipIcon.Warning);
        }
        finally { _installing = false; }
    }

    private List<DeviceBattery> Customized => _settings.Arrange(_all);
    private List<DeviceBattery> Visible => Customized.Where(_settings.IsShown).ToList();
    /// <summary>What the taskbar widget (and its flyout) shows: visible devices, minus those discreet mode hides.</summary>
    private List<DeviceBattery> OnTaskbar => Visible.Where(_settings.ShowsOnTaskbar).ToList();

    private string ThresholdsSignature() =>
        _settings.LowBatteryThreshold + ";" + string.Join(";", _settings.Customizations
            .Where(kv => kv.Value.LowThreshold != null).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.LowThreshold}"));

    private void OnUpdated(IReadOnlyList<DeviceBattery> devices)
    {
        _all = devices;
        _history.Record(devices, DateTime.Now);
        Republish();
        var customized = Customized;
        CheckLowBattery(customized.Where(d => !_settings.HiddenDevices.Contains(d.Id)));
        CheckChargeComplete(customized);
    }

    /// <summary>Pushes the current list to every view (after a poll or a settings change).</summary>
    private void Republish()
    {
        _taskbars.SetDevices(OnTaskbar);
        if (_popup.Visible) ShowPopup();
        UpdateTrayIcon();
        _settingsWindow?.RefreshDevices();
    }

    private void ShowPopup()
    {
        var w = _hovered;
        var visible = OnTaskbar;
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
        var thresholds = ThresholdsSignature();
        if (thresholds != _lastThresholds)
        {
            _lastThresholds = thresholds;
            _lowNotified.Clear(); // re-evaluate alerts with the new thresholds
        }
        _taskbars.SetDevices(OnTaskbar);
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
            Estimate: _history.EstimateText,
            Updater: _updater,
            CheckForUpdates: () => CheckForUpdatesAsync(userInitiated: true),
            InstallUpdate: () => { if (_updater.Available is { } m) _ = InstallUpdateAsync(m); },
            Changed: OnSettingsChanged,
            PollNow: _monitor.RequestPoll,
            Diagnostics: RunDiagnostics,
            TestNotification: () => Notify("Konnichiwatt 👋⚡",
                L.TestNotificationBody, ToolTipIcon.Info)),
            AppIcon());
        _settingsWindow.FormClosed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>The .exe's own icon (amber bolt), for windows.</summary>
    private static Icon? AppIcon()
    {
        try { return Environment.ProcessPath is string p ? Icon.ExtractAssociatedIcon(p) : null; }
        catch { return null; }
    }

    // ---- Tray icon -------------------------------------------------------------------

    private void UpdateTrayIcon()
    {
        var visible = Visible;
        var lowest = visible.Where(d => !d.Stale && d.Percent != null).OrderBy(d => d.Percent).FirstOrDefault();
        bool light = Theme.IsLightTaskbar();
        int size = SystemInformation.SmallIconSize.Width * 2; // Windows downsamples: smoother edges at 16-24 px
        bool low = lowest != null && _settings.IsLow(lowest);

        using var bmp = TrayIconRenderer.Render(size, lowest?.Percent, lowest?.IsCharging ?? false, low, light);
        var hicon = bmp.GetHicon();
        var old = _tray.Icon;
        _tray.Icon = Icon.FromHandle(hicon);
        old?.Dispose();
        if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);
        _trayIconHandle = hicon;

        var tip = visible.Count == 0
            ? $"Konnichiwatt\n{L.NoDevicesDetected}"
            : string.Join("\n", visible.Select(d => $"{d.Name}: {Theme.PercentText(d, true)}{(d.IsCharging ? " ⚡" : "")}{(d.Stale ? $" ({L.AsleepShort})" : "")}"));
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    // ---- Notifications ---------------------------------------------------------------

    private void CheckLowBattery(IEnumerable<DeviceBattery> devices)
    {
        foreach (var d in devices)
        {
            if (d.Percent is not int p || d.Stale) continue;
            int threshold = _settings.ThresholdFor(d.Id);
            bool low = p <= threshold && !d.IsCharging;
            if (low && _lowNotified.Add(d.Id) && _settings.LowBatteryNotifications)
                Notify(L.LowBatteryTitle, $"{d.Name}: {p}%", ToolTipIcon.Warning);
            else if (!low && (d.IsCharging || p > threshold + 5))
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
                    Notify(L.ChargeCompleteTitle, L.ChargeCompleteBody(d.Name), ToolTipIcon.Info);
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
            _menu.Items.Add(new ToolStripMenuItem(L.NoVisibleDevices) { Enabled = false });
        foreach (var d in visible)
        {
            var text = $"{d.Name}  —  {Theme.PercentText(d, true)} {Theme.StateText(d).ToLowerInvariant()}";
            _menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(L.RefreshNow, null, (_, _) => _monitor.RequestPoll());
        var settings = new ToolStripMenuItem(L.SettingsMenu, null, (_, _) => OpenSettings());
        settings.Font = new Font(settings.Font, FontStyle.Bold);
        _menu.Items.Add(settings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(L.Exit, null, (_, _) => ExitThread());
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
        _instanceListener?.Dispose();
        _instanceListener = null;
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _settingsWindow?.Close();
        if (!_savedForUpdate) _history.Save();
        _layoutTimer.Stop();
        _updateTimer.Stop();
        _monitor.Dispose();
        _taskbars.Dispose();
        _popup.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);
        base.ExitThreadCore();
    }
}
