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
    private IntPtr _trayIconHandle;

    private static readonly (string Label, DeviceKind Kind)[] IconChoices =
    {
        ("Auriculares", DeviceKind.Headset), ("Ratón", DeviceKind.Mouse), ("Teclado", DeviceKind.Keyboard),
        ("Mando", DeviceKind.Controller), ("Altavoz", DeviceKind.Speaker), ("Otro", DeviceKind.Other),
    };

    public TrayApplicationContext()
    {
        _monitor = new DeviceMonitor(Program.CreateProviders(), TimeSpan.FromSeconds(_settings.PollSeconds));
        _monitor.Updated += OnUpdated;

        _taskbars = new TaskbarHost(_settings);
        _taskbars.RightClick += ShowMenuAtCursor;
        _taskbars.LeftClick += () => _monitor.RequestPoll();
        _taskbars.HoverChanged += (widget, hover) =>
        {
            if (hover) { _hovered = widget; _hoverTimer.Start(); }
            else if (widget == _hovered) { _hoverTimer.Stop(); _popup.Hide(); _hovered = null; }
        };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowPopup(); };

        _menu.Opening += (_, _) => BuildMenu();
        _tray = new NotifyIcon { Text = "Konnichiwatt", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenuAtCursor(); };
        UpdateTrayIcon();

        _layoutTimer.Tick += (_, _) => _taskbars.Refresh();
        _layoutTimer.Start();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        _monitor.Start();
        Log.Write("Started");
    }

    // ---- Device list: customisation, order, visibility --------------------------------

    /// <summary>All detected devices with the user's names/icons applied, in the user's order.</summary>
    private List<DeviceBattery> Customized
    {
        get
        {
            var order = _settings.DeviceOrder;
            return _all.Select(_settings.Customize)
                .Select((d, i) => (d, i))
                .OrderBy(t => order.IndexOf(t.d.Id) is var k && k >= 0 ? k : int.MaxValue)
                .ThenBy(t => t.i)
                .Select(t => t.d)
                .ToList();
        }
    }

    private bool IsShown(DeviceBattery d) =>
        !_settings.HiddenDevices.Contains(d.Id) && !(_settings.HideWhileCharging && d.IsCharging);

    private List<DeviceBattery> Visible => Customized.Where(IsShown).ToList();

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
        var visible = Visible;
        _taskbars.SetDevices(visible);
        if (_popup.Visible) ShowPopup();
        UpdateTrayIcon();
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
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => _taskbars.Refresh(force: true);

    private void SaveAndRepublish()
    {
        _settings.PruneCustomizations();
        _settings.Save();
        Republish();
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
        if (visible.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("Ningún dispositivo visible") { Enabled = false });
        foreach (var d in visible)
        {
            var text = $"{d.Name}  —  {Theme.PercentText(d, true)} {Theme.StateText(d).ToLowerInvariant()}";
            _menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
        }
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Actualizar ahora", null, (_, _) => _monitor.RequestPoll());
        _menu.Items.Add(BuildDevicesMenu());

        var widget = new ToolStripMenuItem("Mostrar en la barra de tareas") { Checked = _settings.ShowTaskbarWidget };
        widget.Click += (_, _) => { _settings.ShowTaskbarWidget = !_settings.ShowTaskbarWidget; _settings.Save(); _taskbars.Refresh(force: true); };
        _menu.Items.Add(widget);

        var allScreens = new ToolStripMenuItem("Mostrar en todas las pantallas") { Checked = _settings.ShowOnAllTaskbars };
        allScreens.Click += (_, _) => { _settings.ShowOnAllTaskbars = !_settings.ShowOnAllTaskbars; _settings.Save(); _taskbars.Refresh(force: true); };
        _menu.Items.Add(allScreens);

        var display = new ToolStripMenuItem("Mostrar batería como");
        foreach (var (label, value) in new[]
                 {
                     ("Porcentaje", BatteryDisplay.Percent),
                     ("Icono de batería", BatteryDisplay.Icon),
                     ("Icono + porcentaje", BatteryDisplay.IconAndPercent),
                 })
        {
            var item = new ToolStripMenuItem(label) { Checked = _settings.Display == value };
            item.Click += (_, _) => { _settings.Display = value; _settings.Save(); _taskbars.Refresh(force: true); };
            display.DropDownItems.Add(item);
        }
        _menu.Items.Add(display);

        var bold = new ToolStripMenuItem("Texto e iconos gruesos") { Checked = _settings.BoldStyle };
        bold.Click += (_, _) => { _settings.BoldStyle = !_settings.BoldStyle; _settings.Save(); _taskbars.Refresh(force: true); };
        _menu.Items.Add(bold);

        _menu.Items.Add(new ToolStripSeparator());

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
                _lowNotified.Clear(); _taskbars.Refresh(force: true);
            };
            alerts.DropDownItems.Add(item);
        }
        _menu.Items.Add(alerts);

        var full = new ToolStripMenuItem("Aviso de carga completa") { Checked = _settings.ChargeCompleteNotifications };
        full.Click += (_, _) => { _settings.ChargeCompleteNotifications = !_settings.ChargeCompleteNotifications; _settings.Save(); };
        _menu.Items.Add(full);

        var interval = new ToolStripMenuItem("Frecuencia de lectura");
        foreach (var (label, secs) in new[] { ("15 segundos", 15), ("30 segundos", 30), ("1 minuto", 60), ("5 minutos", 300) })
        {
            var item = new ToolStripMenuItem(label) { Checked = _settings.PollSeconds == secs };
            item.Click += (_, _) => { _settings.PollSeconds = secs; _settings.Save(); _monitor.Interval = TimeSpan.FromSeconds(secs); };
            interval.DropDownItems.Add(item);
        }
        _menu.Items.Add(interval);

        var startup = new ToolStripMenuItem("Iniciar con Windows") { Checked = SafeStartupEnabled() };
        startup.Click += (_, _) =>
        {
            try { Startup.IsEnabled = !Startup.IsEnabled; }
            catch (Exception e) { Log.Error("Startup toggle", e); }
        };
        _menu.Items.Add(startup);

        _menu.Items.Add("Diagnóstico…", null, (_, _) => RunDiagnostics());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Salir", null, (_, _) => ExitThread());
    }

    /// <summary>"Dispositivos" submenu: per device show/hide, rename, icon and position.</summary>
    private ToolStripMenuItem BuildDevicesMenu()
    {
        var root = new ToolStripMenuItem("Dispositivos");
        var present = Customized;
        var ids = present.Select(d => d.Id).ToList();

        for (int i = 0; i < present.Count; i++)
        {
            var d = present[i];
            var original = _all.First(a => a.Id == d.Id);
            bool hidden = _settings.HiddenDevices.Contains(d.Id);
            var item = new ToolStripMenuItem(hidden ? $"{d.Name} (oculto)" : d.Name);

            var show = new ToolStripMenuItem("Mostrar") { Checked = !hidden };
            show.Click += (_, _) => ToggleHidden(d.Id);
            item.DropDownItems.Add(show);

            item.DropDownItems.Add("Cambiar nombre…", null, (_, _) =>
            {
                var current = _settings.DisplayName(d.Id, original.Name);
                var name = RenameDialog.Ask(original.Name, current);
                if (name == null) return;
                _settings.CustomizationFor(d.Id).Name = name.Length == 0 || name == original.Name ? null : name;
                SaveAndRepublish();
            });

            var icon = new ToolStripMenuItem("Icono");
            var custom = _settings.Customizations.TryGetValue(d.Id, out var c) ? c.Kind : null;
            var auto = new ToolStripMenuItem($"Automático ({Theme.KindName(original.Kind).ToLowerInvariant()})") { Checked = custom == null };
            auto.Click += (_, _) => { _settings.CustomizationFor(d.Id).Kind = null; SaveAndRepublish(); };
            icon.DropDownItems.Add(auto);
            icon.DropDownItems.Add(new ToolStripSeparator());
            foreach (var (label, kind) in IconChoices)
            {
                var k = new ToolStripMenuItem(label) { Checked = custom == kind };
                k.Click += (_, _) => { _settings.CustomizationFor(d.Id).Kind = kind; SaveAndRepublish(); };
                icon.DropDownItems.Add(k);
            }
            item.DropDownItems.Add(icon);

            item.DropDownItems.Add(new ToolStripSeparator());
            int index = i;
            var left = new ToolStripMenuItem("Mover a la izquierda") { Enabled = index > 0 };
            left.Click += (_, _) => Move(ids, index, -1);
            var right = new ToolStripMenuItem("Mover a la derecha") { Enabled = index < present.Count - 1 };
            right.Click += (_, _) => Move(ids, index, +1);
            item.DropDownItems.Add(left);
            item.DropDownItems.Add(right);

            root.DropDownItems.Add(item);
        }

        // Hidden devices that are not connected right now: allow un-hiding them.
        foreach (var id in _settings.HiddenDevices.Where(id => !ids.Contains(id)).ToList())
        {
            var name = _settings.DisplayName(id, id);
            var item = new ToolStripMenuItem($"{name} (oculto, no conectado)");
            var show = new ToolStripMenuItem("Mostrar") { Checked = false };
            show.Click += (_, _) => ToggleHidden(id);
            item.DropDownItems.Add(show);
            root.DropDownItems.Add(item);
        }

        if (root.DropDownItems.Count == 0)
            root.DropDownItems.Add(new ToolStripMenuItem("Ningún dispositivo detectado") { Enabled = false });

        root.DropDownItems.Add(new ToolStripSeparator());
        var whileCharging = new ToolStripMenuItem("Ocultar los que se están cargando") { Checked = _settings.HideWhileCharging };
        whileCharging.Click += (_, _) => { _settings.HideWhileCharging = !_settings.HideWhileCharging; SaveAndRepublish(); };
        root.DropDownItems.Add(whileCharging);
        var resetOrder = new ToolStripMenuItem("Restablecer orden") { Enabled = _settings.DeviceOrder.Count > 0 };
        resetOrder.Click += (_, _) => { _settings.DeviceOrder.Clear(); SaveAndRepublish(); };
        root.DropDownItems.Add(resetOrder);
        return root;
    }

    private void ToggleHidden(string id)
    {
        if (!_settings.HiddenDevices.Remove(id)) _settings.HiddenDevices.Add(id);
        SaveAndRepublish();
    }

    private void Move(List<string> ids, int index, int delta)
    {
        int j = index + delta;
        if (j < 0 || j >= ids.Count) return;
        (ids[index], ids[j]) = (ids[j], ids[index]);
        // Keep the remembered position of devices that are not connected right now, after the current ones.
        _settings.DeviceOrder = ids.Concat(_settings.DeviceOrder.Where(id => !ids.Contains(id))).ToList();
        SaveAndRepublish();
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
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
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
