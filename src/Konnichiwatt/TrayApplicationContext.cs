using System.Diagnostics;
using System.Drawing.Text;
using Microsoft.Win32;
using Konnichiwatt.Core;
using Konnichiwatt.Devices;
using Konnichiwatt.UI;

namespace Konnichiwatt;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DeviceMonitor _monitor;
    private readonly TaskbarWidget _widget;
    private readonly DetailsPopup _popup = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _layoutTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 350 };
    private readonly HashSet<string> _lowNotified = new();
    private IReadOnlyList<DeviceBattery> _all = Array.Empty<DeviceBattery>();
    private IntPtr _trayIconHandle;

    public TrayApplicationContext()
    {
        _monitor = new DeviceMonitor(Program.CreateProviders(), TimeSpan.FromSeconds(_settings.PollSeconds));
        _monitor.Updated += OnUpdated;

        _widget = new TaskbarWidget(_settings);
        _widget.RightClick += () => ShowMenuAtCursor();
        _widget.LeftClick += () => _monitor.RequestPoll();
        _widget.HoverChanged += hover =>
        {
            if (hover) _hoverTimer.Start();
            else { _hoverTimer.Stop(); _popup.Hide(); }
        };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            var b = _widget.ScreenBounds;
            if (!b.IsEmpty && Visible.Count > 0) _popup.ShowAbove(b, Visible, b.Height / 48f);
        };

        _menu.Opening += (_, _) => BuildMenu();
        _tray = new NotifyIcon { Text = "Konnichiwatt", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenuAtCursor(); };
        UpdateTrayIcon();

        _layoutTimer.Tick += (_, _) => _widget.Refresh();
        _layoutTimer.Start();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;

        _monitor.Start();
        Log.Write("Started");
    }

    private IReadOnlyList<DeviceBattery> Visible =>
        _all.Where(d => !_settings.HiddenDevices.Contains(d.Id)).ToList();

    private void OnUpdated(IReadOnlyList<DeviceBattery> devices)
    {
        _all = devices;
        var visible = Visible;
        _widget.SetDevices(visible);
        if (_popup.Visible) _popup.ShowAbove(_widget.ScreenBounds, visible, _widget.ScreenBounds.Height / 48f);
        UpdateTrayIcon();
        CheckLowBattery(visible);
    }

    private void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        _widget.Refresh(force: true);
        UpdateTrayIcon();
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
            : string.Join("\n", visible.Select(d => $"{Theme.KindName(d.Kind)}: {Theme.PercentText(d, true)}{(d.IsCharging ? " ⚡" : "")}{(d.Stale ? " (reposo)" : "")}"));
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    // ---- Notifications ---------------------------------------------------------------

    private void CheckLowBattery(IReadOnlyList<DeviceBattery> devices)
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

    // ---- Menu --------------------------------------------------------------------------

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
        if (_all.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("Ningún dispositivo detectado") { Enabled = false });
        foreach (var d in visible)
        {
            var text = $"{d.Name}  —  {Theme.PercentText(d, true)} {Theme.StateText(d).ToLowerInvariant()}";
            _menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
        }
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Actualizar ahora", null, (_, _) => _monitor.RequestPoll());

        var widget = new ToolStripMenuItem("Mostrar en la barra de tareas") { Checked = _settings.ShowTaskbarWidget };
        widget.Click += (_, _) => { _settings.ShowTaskbarWidget = !_settings.ShowTaskbarWidget; _settings.Save(); _widget.Refresh(force: true); };
        _menu.Items.Add(widget);

        var display = new ToolStripMenuItem("Mostrar batería como");
        foreach (var (label, value) in new[]
                 {
                     ("Porcentaje", BatteryDisplay.Percent),
                     ("Icono de batería", BatteryDisplay.Icon),
                     ("Icono + porcentaje", BatteryDisplay.IconAndPercent),
                 })
        {
            var item = new ToolStripMenuItem(label) { Checked = _settings.Display == value };
            item.Click += (_, _) => { _settings.Display = value; _settings.Save(); _widget.Refresh(force: true); };
            display.DropDownItems.Add(item);
        }
        _menu.Items.Add(display);

        var bold = new ToolStripMenuItem("Texto e iconos gruesos") { Checked = _settings.BoldStyle };
        bold.Click += (_, _) => { _settings.BoldStyle = !_settings.BoldStyle; _settings.Save(); _widget.Refresh(force: true); };
        _menu.Items.Add(bold);

        var startup = new ToolStripMenuItem("Iniciar con Windows") { Checked = SafeStartupEnabled() };
        startup.Click += (_, _) =>
        {
            try { Startup.IsEnabled = !Startup.IsEnabled; }
            catch (Exception e) { Log.Error("Startup toggle", e); }
        };
        _menu.Items.Add(startup);

        var alerts = new ToolStripMenuItem("Aviso de batería baja");
        var off = new ToolStripMenuItem("Desactivado") { Checked = !_settings.LowBatteryNotifications };
        off.Click += (_, _) => { _settings.LowBatteryNotifications = false; _settings.Save(); };
        alerts.DropDownItems.Add(off);
        foreach (var t in new[] { 10, 15, 20, 30 })
        {
            var item = new ToolStripMenuItem($"Al {t}%") { Checked = _settings.LowBatteryNotifications && _settings.LowBatteryThreshold == t };
            item.Click += (_, _) =>
            {
                _settings.LowBatteryNotifications = true; _settings.LowBatteryThreshold = t; _settings.Save();
                _lowNotified.Clear(); _widget.Refresh(force: true);
            };
            alerts.DropDownItems.Add(item);
        }
        _menu.Items.Add(alerts);

        var interval = new ToolStripMenuItem("Frecuencia de lectura");
        foreach (var (label, secs) in new[] { ("15 segundos", 15), ("30 segundos", 30), ("1 minuto", 60), ("5 minutos", 300) })
        {
            var item = new ToolStripMenuItem(label) { Checked = _settings.PollSeconds == secs };
            item.Click += (_, _) => { _settings.PollSeconds = secs; _settings.Save(); _monitor.Interval = TimeSpan.FromSeconds(secs); };
            interval.DropDownItems.Add(item);
        }
        _menu.Items.Add(interval);

        var hide = new ToolStripMenuItem("Dispositivos visibles");
        var known = _all.Select(d => (d.Id, d.Name))
            .Concat(_settings.HiddenDevices.Where(id => _all.All(d => d.Id != id)).Select(id => (Id: id, Name: id)))
            .ToList();
        if (known.Count == 0) hide.Enabled = false;
        foreach (var (id, name) in known)
        {
            var item = new ToolStripMenuItem(name) { Checked = !_settings.HiddenDevices.Contains(id) };
            item.Click += (_, _) =>
            {
                if (!_settings.HiddenDevices.Remove(id)) _settings.HiddenDevices.Add(id);
                _settings.Save();
                OnUpdated(_all);
            };
            hide.DropDownItems.Add(item);
        }
        _menu.Items.Add(hide);

        _menu.Items.Add("Diagnóstico…", null, (_, _) => RunDiagnostics());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Salir", null, (_, _) => ExitThread());
    }

    private static bool SafeStartupEnabled()
    {
        try { return Startup.IsEnabled; } catch { return false; }
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
        _layoutTimer.Stop();
        _monitor.Dispose();
        _widget.Dispose();
        _popup.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);
        base.ExitThreadCore();
    }
}
