using System.Diagnostics;
using System.Runtime.InteropServices;
using Konnichiwatt.Core;
using Konnichiwatt.UI.Fluent;

namespace Konnichiwatt.UI;

/// <summary>
/// Settings window in the style of the widget's hover flyout / Windows 11 Settings: side navigation, cards,
/// toggle switches. Every change is applied (and saved) immediately through <see cref="Callbacks.Changed"/>.
/// </summary>
internal sealed class SettingsWindow : Form
{
    internal sealed record Callbacks(
        Func<IReadOnlyList<DeviceBattery>> RawDevices,
        Action Changed,
        Action PollNow,
        Action Diagnostics,
        Action TestNotification);

    private static readonly (string Glyph, string Title)[] Pages =
    {
        ("", "Dispositivos"), ("", "Apariencia"), ("", "Avisos"), ("", "General"),
    };

    private static readonly (string Label, DeviceKind Kind)[] IconChoices =
    {
        ("Auriculares", DeviceKind.Headset), ("Ratón", DeviceKind.Mouse), ("Teclado", DeviceKind.Keyboard),
        ("Mando", DeviceKind.Controller), ("Altavoz", DeviceKind.Speaker), ("Otro", DeviceKind.Other),
    };

    private readonly AppSettings _s;
    private readonly Callbacks _cb;
    private int _page;
    private FlowLayoutPanel _content = null!;
    private Label _pageTitle = null!;
    private readonly List<NavItem> _nav = new();
    private readonly List<DeviceRow> _rows = new();
    private PreviewCard? _preview;
    private string _rowsSignature = "";

    public SettingsWindow(AppSettings settings, Callbacks callbacks, Icon? icon)
    {
        _s = settings;
        _cb = callbacks;
        Text = "Ajustes de Konnichiwatt";
        if (icon != null) Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Build();
    }

    // ---- Construction ---------------------------------------------------------------

    private void Build()
    {
        SuspendLayout();
        Controls.Clear();
        _nav.Clear();
        _rows.Clear();
        _rowsSignature = "";
        _preview = null;

        F.Init(Theme.IsLightApps(), DeviceDpi / 96f);
        BackColor = F.Back;
        ClientSize = new Size(F.Px(800), F.Px(560));

        // Side bar: app name + navigation.
        var side = new Panel { Dock = DockStyle.Left, Width = F.Px(220), BackColor = F.Back, Padding = new Padding(F.Px(12)) };
        side.Paint += (_, e) =>
        {
            F.Prepare(e.Graphics);
            using var bolt = F.Glyphs(20);
            using var name = F.Text(16, semibold: true);
            using var sub = F.Text(12);
            F.DrawText(e.Graphics, Theme.Bolt, bolt, F.Accent, new RectangleF(F.Pf(18), F.Pf(18), F.Pf(26), F.Pf(28)));
            F.DrawText(e.Graphics, "Konnichiwatt", name, F.Fg, new RectangleF(F.Pf(48), F.Pf(14), F.Pf(170), F.Pf(24)));
            F.DrawText(e.Graphics, "konnichiwa + watt", sub, F.Fg2, new RectangleF(F.Pf(48), F.Pf(36), F.Pf(170), F.Pf(18)));
        };
        var navList = new FlowLayoutPanel
        {
            Location = new Point(F.Px(10), F.Px(76)),
            Size = new Size(F.Px(200), F.Px(200)),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = F.Back,
        };
        for (int i = 0; i < Pages.Length; i++)
        {
            int index = i;
            var item = new NavItem(Pages[i].Glyph, Pages[i].Title, F.Px(196)) { Selected = i == _page };
            item.Click += (_, _) => ShowPage(index);
            _nav.Add(item);
            navList.Controls.Add(item);
        }
        side.Controls.Add(navList);

        // Content: page title + scrolling list of cards.
        var main = new Panel { Dock = DockStyle.Fill, BackColor = F.Back, Padding = new Padding(F.Px(8), F.Px(14), F.Px(20), F.Px(14)) };
        _pageTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = F.Px(52),
            ForeColor = F.Fg,
            BackColor = F.Back,
            Font = F.Text(26, semibold: true),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = F.Back,
            Padding = new Padding(0, F.Px(6), 0, F.Px(6)),
        };
        main.Controls.Add(_content);
        main.Controls.Add(_pageTitle);

        Controls.Add(main);
        Controls.Add(side);
        ResumeLayout();
        ShowPage(_page);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFrame();
    }

    /// <summary>Title bar in the same colour as the window, rounded corners, dark frame on dark theme.</summary>
    private void ApplyFrame()
    {
        if (!IsHandleCreated) return;
        int dark = F.Light ? 0 : 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));       // DWMWA_WINDOW_CORNER_PREFERENCE = round
        int caption = ColorTranslator.ToWin32(F.Back);
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));     // DWMWA_CAPTION_COLOR
        int text = ColorTranslator.ToWin32(F.Fg);
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));        // DWMWA_TEXT_COLOR
    }

    /// <summary>Rebuilds everything with the current Windows theme (light/dark, accent colour).</summary>
    public void ApplyTheme()
    {
        Build();
        ApplyFrame();
    }

    private int CardWidth => _content.ClientSize.Width - F.Px(22);

    private void ShowPage(int index)
    {
        _page = index;
        for (int i = 0; i < _nav.Count; i++) _nav[i].Selected = i == index;
        _pageTitle.Text = Pages[index].Title;

        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList()) c.Dispose();
        _content.Controls.Clear();
        _rows.Clear();
        _rowsSignature = "";
        _preview = null;
        switch (index)
        {
            case 0: BuildDevices(); break;
            case 1: BuildAppearance(); break;
            case 2: BuildAlerts(); break;
            default: BuildGeneral(); break;
        }
        _content.ResumeLayout();
        if (IsHandleCreated && !F.Light) SetWindowTheme(_content.Handle, "DarkMode_Explorer", null); // dark scroll bar
    }

    private void Changed()
    {
        _cb.Changed();
        _preview?.Invalidate();
    }

    private SettingCard Card(string? glyph, string title, string? description, Control? accessory) =>
        new(glyph, title, description, accessory, CardWidth);

    private ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch(value);
        t.CheckedChanged += (_, _) => { set(t.Checked); Changed(); };
        return t;
    }

    private Label SectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = CardWidth,
        Height = F.Px(34),
        ForeColor = F.Fg,
        BackColor = F.Back,
        Font = F.Text(14, semibold: true),
        TextAlign = ContentAlignment.BottomLeft,
        Margin = new Padding(0, 0, 0, F.Px(6)),
    };

    // ---- Pages --------------------------------------------------------------------------

    private void BuildDevices()
    {
        RefreshDevices(force: true);
    }

    /// <summary>Called when a poll brings new data: rebuilds rows only if devices/order changed, so typing isn't interrupted.</summary>
    public void RefreshDevices(bool force = false)
    {
        if (_page != 0 || IsDisposed) return;
        if (_list?.IsDragging == true) return; // don't rebuild the row under the pointer; the next poll will
        var raw = _cb.RawDevices();
        var arranged = _s.Arrange(raw);
        var absentHidden = _s.HiddenDevices.Where(id => arranged.All(d => d.Id != id)).ToList();
        var signature = Signature(arranged.Select(d => d.Id), absentHidden);

        if (!force && signature == _rowsSignature)
        {
            foreach (var row in _rows)
            {
                var d = arranged.FirstOrDefault(a => a.Id == row.Id);
                var orig = raw.FirstOrDefault(a => a.Id == row.Id);
                if (d != null && orig != null) row.Update(d, orig);
            }
            return;
        }

        _rowsSignature = signature;
        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList()) c.Dispose();
        _content.Controls.Clear();
        _rows.Clear();

        if (arranged.Count == 0 && absentHidden.Count == 0)
            _content.Controls.Add(Card("", "Ningún dispositivo detectado",
                "Enciende tus dispositivos inalámbricos; aparecerán aquí en unos segundos.", null));

        // Connected devices live in a list that animates reordering (drag the grip or use the arrows).
        _list = null;
        if (arranged.Count > 0)
        {
            var rows = new List<DeviceRow>();
            foreach (var d in arranged)
            {
                var orig = raw.First(a => a.Id == d.Id);
                var row = new DeviceRow(d.Id, CardWidth, canUp: false, canDown: false, connected: true);
                row.Update(d, orig);
                row.Shown = !_s.HiddenDevices.Contains(d.Id);
                WireRow(row, orig);
                rows.Add(row);
                _rows.Add(row);
            }
            _list = new DeviceListPanel(CardWidth);
            _list.SetRows(rows);
            _list.ScrollRequested += ScrollContentBy;
            _list.OrderChanged += ids =>
            {
                _s.SetOrder(ids);
                _rowsSignature = Signature(ids, absentHidden); // already showing this order: no rebuild needed
                if (_resetOrder != null) _resetOrder.Enabled = true;
                Changed();
            };
            _content.Controls.Add(_list);
        }
        foreach (var id in absentHidden)
        {
            var name = _s.DisplayName(id, id);
            var kind = _s.Customizations.TryGetValue(id, out var c) ? c.Kind ?? DeviceKind.Other : DeviceKind.Other;
            var row = new DeviceRow(id, CardWidth, canUp: false, canDown: false, connected: false);
            row.UpdateAbsent(name, kind);
            row.Shown = false;
            row.ShownChanged += shown => { _s.ToggleHidden(id); Changed(); RefreshDevices(force: true); };
            _rows.Add(row);
            _content.Controls.Add(row);
        }

        _content.Controls.Add(SectionLabel("Opciones"));
        _content.Controls.Add(Card("", "Ocultar los que se están cargando",
            "Mientras un dispositivo carga no ocupa sitio en la barra", Toggle(_s.HideWhileCharging, v => _s.HideWhileCharging = v)));
        var reset = _resetOrder = new FlatButton("Restablecer") { Enabled = _s.DeviceOrder.Count > 0 };
        reset.Click += (_, _) => { _s.DeviceOrder.Clear(); Changed(); RefreshDevices(force: true); };
        _content.Controls.Add(Card("", "Orden original", "Vuelve al orden en que se detectan los dispositivos", reset));
        _content.ResumeLayout();
    }

    private static string Signature(IEnumerable<string> ids, IEnumerable<string> absentHidden) =>
        string.Join("|", ids) + "#" + string.Join("|", absentHidden);

    private void WireRow(DeviceRow row, DeviceBattery original)
    {
        row.ShownChanged += _ => { _s.ToggleHidden(row.Id); Changed(); };
        row.Renamed += name =>
        {
            _s.CustomizationFor(row.Id).Name = string.IsNullOrWhiteSpace(name) || name.Trim() == original.Name ? null : name.Trim();
            Changed();
        };
        row.IconRequested += anchor =>
        {
            var menu = new ContextMenuStrip();
            var custom = _s.Customizations.TryGetValue(row.Id, out var c) ? c.Kind : null;
            var auto = new ToolStripMenuItem($"Automático ({Theme.KindName(original.Kind).ToLowerInvariant()})") { Checked = custom == null };
            auto.Click += (_, _) => { _s.CustomizationFor(row.Id).Kind = null; Changed(); RefreshDevices(); };
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            foreach (var (label, kind) in IconChoices)
            {
                var item = new ToolStripMenuItem(label) { Checked = custom == kind };
                item.Click += (_, _) => { _s.CustomizationFor(row.Id).Kind = kind; Changed(); RefreshDevices(); };
                menu.Items.Add(item);
            }
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(anchor, new Point(0, anchor.Height));
        };
    }

    private DeviceListPanel? _list;
    private FlatButton? _resetOrder;

    private void ScrollContentBy(int dy)
    {
        var p = _content.AutoScrollPosition; // negative offsets
        _content.AutoScrollPosition = new Point(-p.X, Math.Max(0, -p.Y + dy));
    }

    private void BuildAppearance()
    {
        _preview = new PreviewCard(CardWidth, () =>
        {
            var arranged = _s.Arrange(_cb.RawDevices()).Where(_s.IsShown).ToList();
            return arranged.Count > 0 ? arranged : PreviewRenderer.Samples();
        }, _s);
        _content.Controls.Add(_preview);

        var display = new Segmented(new[] { "Porcentaje", "Icono", "Ambos" }, (int)_s.Display);
        display.SelectedChanged += (_, _) => { _s.Display = (BatteryDisplay)display.Selected; Changed(); };
        _content.Controls.Add(Card("", "Mostrar batería como", "Número, batería o ambos", display));

        _content.Controls.Add(Card("", "Texto e iconos gruesos", "Más legible en monitores con escala al 100 %",
            Toggle(_s.BoldStyle, v => _s.BoldStyle = v)));

        _content.Controls.Add(SectionLabel("Dónde se muestra"));
        _content.Controls.Add(Card("", "Mostrar en la barra de tareas", "Si lo desactivas, solo queda el icono de la bandeja",
            Toggle(_s.ShowTaskbarWidget, v => _s.ShowTaskbarWidget = v)));
        AddMonitorCards();
    }

    /// <summary>One switch per connected monitor; at least one must stay on.</summary>
    private void AddMonitorCards()
    {
        var monitors = Monitors.All();
        var primary = monitors.FirstOrDefault(m => m.Primary);
        var withTaskbar = Monitors.WithTaskbar(monitors);
        var usable = monitors.Where(m => m.Primary || withTaskbar.Contains(m.Id)).ToList();
        foreach (var m in monitors)
        {
            bool hasTaskbar = m.Primary || withTaskbar.Contains(m.Id);
            var toggle = new ToggleSwitch(hasTaskbar && _s.ShowsOnMonitor(m.Id, m.Primary)) { Enabled = hasTaskbar && usable.Count > 1 };
            toggle.CheckedChanged += (_, _) =>
            {
                var chosen = usable.Where(x => _s.ShowsOnMonitor(x.Id, x.Primary)).Select(x => x.Id).ToHashSet();
                if (toggle.Checked) chosen.Add(m.Id); else chosen.Remove(m.Id);
                if (chosen.Count == 0) { toggle.SetSilently(true); return; } // the widget must live somewhere
                // Remember choices for monitors that are unplugged right now.
                var unplugged = (_s.TaskbarMonitors ?? new()).Where(id => monitors.All(x => x.Id != id));
                _s.TaskbarMonitors = chosen.Concat(unplugged).Distinct().ToList();
                Changed();
            };

            bool friendly = !m.Name.StartsWith("Pantalla ", StringComparison.Ordinal);
            var title = friendly ? $"{m.Name}  ·  pantalla {m.Number}" : m.Name;
            var parts = new List<string>();
            if (m.Primary) parts.Add("Principal");
            parts.Add($"{m.Bounds.Width} × {m.Bounds.Height}");
            if (!m.Primary && primary != null) parts.Add(RelativePosition(m.Bounds, primary.Bounds));
            if (monitors.Count == 1) parts.Add("única pantalla");
            var description = hasTaskbar
                ? string.Join(" · ", parts)
                : "Sin barra de tareas (actívala en los ajustes de Windows)";
            _content.Controls.Add(Card("", title, description, toggle));
        }
    }

    private static string RelativePosition(Rectangle r, Rectangle primary)
    {
        if (r.Right <= primary.Left) return "a la izquierda";
        if (r.Left >= primary.Right) return "a la derecha";
        if (r.Bottom <= primary.Top) return "arriba";
        if (r.Top >= primary.Bottom) return "abajo";
        return "superpuesta";
    }

    /// <summary>Monitors were plugged/unplugged or rearranged: refresh the list if it's on screen.</summary>
    public void DisplaysChanged()
    {
        if (_page == 1 && !IsDisposed) ShowPage(1);
    }

    private void BuildAlerts()
    {
        var threshold = new Slider(5, 50, 5, _s.LowBatteryThreshold, v => $"{v} %");
        threshold.ValueChanged += (_, _) => { _s.LowBatteryThreshold = threshold.Value; Changed(); };
        threshold.Enabled = _s.LowBatteryNotifications;

        var low = new ToggleSwitch(_s.LowBatteryNotifications);
        low.CheckedChanged += (_, _) => { _s.LowBatteryNotifications = low.Checked; threshold.Enabled = low.Checked; Changed(); };

        _content.Controls.Add(Card("", "Aviso de batería baja", "Una notificación cuando un dispositivo se está quedando sin batería", low));
        _content.Controls.Add(Card("", "Avisar al bajar del", "Se marca en rojo en la barra", threshold));
        _content.Controls.Add(Card("", "Aviso de carga completa", "Cuando un dispositivo que estaba cargando llega al 100 %",
            Toggle(_s.ChargeCompleteNotifications, v => _s.ChargeCompleteNotifications = v)));

        var test = new FlatButton("Enviar aviso de prueba");
        test.Click += (_, _) => _cb.TestNotification();
        _content.Controls.Add(SectionLabel("Comprobar"));
        _content.Controls.Add(Card("", "Probar las notificaciones", "Si no aparece, revisa «No molestar»", test));
    }

    private void BuildGeneral()
    {
        bool startup;
        try { startup = Startup.IsEnabled; } catch { startup = false; }
        var start = new ToggleSwitch(startup);
        start.CheckedChanged += (_, _) =>
        {
            try { Startup.IsEnabled = start.Checked; }
            catch (Exception e) { Log.Error("Startup toggle", e); start.SetSilently(!start.Checked); }
        };
        _content.Controls.Add(Card("", "Iniciar con Windows", "Konnichiwatt se abre solo al iniciar sesión", start));

        var secs = new[] { 15, 30, 60, 300 };
        var freq = new Segmented(new[] { "15 s", "30 s", "1 min", "5 min" }, Math.Max(0, Array.IndexOf(secs, _s.PollSeconds)));
        freq.SelectedChanged += (_, _) => { _s.PollSeconds = secs[freq.Selected]; Changed(); };
        _content.Controls.Add(Card("", "Frecuencia de lectura", "Al conectar uno, al momento", freq));

        var refresh = new FlatButton("Actualizar ahora");
        refresh.Click += (_, _) => _cb.PollNow();
        _content.Controls.Add(Card("", "Leer la batería ahora", null, refresh));

        _content.Controls.Add(SectionLabel("Ayuda"));
        var diag = new FlatButton("Generar informe");
        diag.Click += (_, _) => _cb.Diagnostics();
        _content.Controls.Add(Card("", "Diagnóstico", "Útil si un dispositivo no aparece", diag));

        var open = new FlatButton("Abrir");
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true }); }
            catch (Exception e) { Log.Error("Open folder", e); }
        };
        _content.Controls.Add(Card("", "Carpeta de registros", Log.Directory, open));

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        _content.Controls.Add(new Label
        {
            Text = $"Konnichiwatt {version?.ToString(3)} · konnichiwa (こんにちは, hola) + watt",
            AutoSize = false,
            Width = CardWidth,
            Height = F.Px(40),
            ForeColor = F.Fg2,
            BackColor = F.Back,
            Font = F.Text(12),
            TextAlign = ContentAlignment.MiddleCenter,
        });
    }

    /// <summary>Dev aid (<c>--settings-shot dir</c>): renders every page with sample devices to PNG files.</summary>
    public static void SaveScreenshots(string dir, AppSettings settings)
    {
        Directory.CreateDirectory(dir);
        var samples = PreviewRenderer.Samples();
        using var w = new SettingsWindow(settings, new Callbacks(() => samples, () => { }, () => { }, () => { }, () => { }), null)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-5000, -5000),
            ShowInTaskbar = false,
        };
        w.Show();
        w.ClientSize = new Size(w.ClientSize.Width, F.Px(760)); // tall enough to see whole pages without scrolling
        for (int i = 0; i < Pages.Length; i++)
        {
            w.ShowPage(i);
            Application.DoEvents();
            using var bmp = new Bitmap(w.ClientSize.Width, w.ClientSize.Height);
            w.DrawToBitmap(bmp, new Rectangle(Point.Empty, w.ClientSize));
            bmp.Save(Path.Combine(dir, $"ajustes-{i + 1}-{Pages[i].Title.ToLowerInvariant()}.png"));
        }
        w.Close();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr h, string app, string? id);
}

/// <summary>
/// One device in the "Dispositivos" page:
/// [grip ⠿][▴▾] [icon] name / status ...... percent [switch]
/// Drag the grip (or use the small arrows) to reorder; click the icon to change it; type over the name to rename.
/// </summary>
internal sealed class DeviceRow : SettingCard
{
    private readonly GripHandle _grip;
    private readonly GlyphButton _up, _down;
    private readonly GlyphButton _icon;
    private readonly TextBox _name;
    private readonly ToggleSwitch _shown;
    private string _subtitle = "";
    private string _percent = "";
    private string _committed = "";
    private bool _low, _charging, _dragging;

    public string Id { get; }
    public bool Connected { get; }
    public event Action<bool>? ShownChanged;
    public event Action<string>? Renamed;
    public event Action<Control>? IconRequested;
    public event Action<int>? MoveRequested;
    public event Action<DeviceRow>? DragStarted;
    public event Action<DeviceRow, int>? DragMoved;
    public event Action<DeviceRow>? DragEnded;

    private int TextLeft => F.Px(104);

    public DeviceRow(string id, int width, bool canUp, bool canDown, bool connected)
        : base(null, "", null, null, width)
    {
        Id = id;
        Connected = connected;
        Height = F.Px(68);

        _grip = new GripHandle { Visible = connected };
        _grip.DragStarted += () => DragStarted?.Invoke(this);
        _grip.DragMoved += y => DragMoved?.Invoke(this, y);
        _grip.DragEnded += () => DragEnded?.Invoke(this);

        _up = new GlyphButton("", 22, 9) { Enabled = canUp, Visible = connected };
        _down = new GlyphButton("", 22, 9) { Enabled = canDown, Visible = connected };
        _up.Height = _down.Height = F.Px(20);
        _up.Click += (_, _) => MoveRequested?.Invoke(-1);
        _down.Click += (_, _) => MoveRequested?.Invoke(+1);

        _icon = new GlyphButton("", 40, 18) { Enabled = connected };
        _icon.Click += (_, _) => IconRequested?.Invoke(_icon);

        _name = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = F.Card,
            ForeColor = F.Fg,
            Font = F.Text(14),
        };
        _name.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); Parent?.Focus(); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _name.Text = _committed; Parent?.Focus(); }
        };
        _name.Leave += (_, _) => Commit();
        _name.GotFocus += (_, _) => Invalidate();
        _name.LostFocus += (_, _) => Invalidate();

        _shown = new ToggleSwitch(true);
        _shown.CheckedChanged += (_, _) => ShownChanged?.Invoke(_shown.Checked);

        Controls.AddRange(new Control[] { _grip, _up, _down, _icon, _name, _shown });
        LayoutParts();
    }

    public bool Shown
    {
        get => _shown.Checked;
        set => _shown.SetSilently(value);
    }

    /// <summary>Highlighted (accent border) while being dragged.</summary>
    public bool Dragging
    {
        get => _dragging;
        set { _dragging = value; _name.BackColor = CardFill; Invalidate(); }
    }

    protected override Color CardFill => _dragging ? F.CardHover : F.Card;
    protected override Color CardBorder => _dragging ? F.Accent : F.Stroke;
    protected override float CardBorderWidth => _dragging ? F.Pf(1.5f) : 1;

    private void LayoutParts()
    {
        if (_shown == null) return;
        int mid = Height / 2;
        _grip.Location = new Point(F.Px(8), mid - _grip.Height / 2);
        _up.Location = new Point(F.Px(30), mid - _up.Height);
        _down.Location = new Point(F.Px(30), mid);
        _icon.Location = new Point(F.Px(56), mid - _icon.Height / 2);
        _name.Location = new Point(TextLeft, F.Px(14));
        _name.Width = Math.Max(F.Px(80), Width - TextLeft - F.Px(190));
        _shown.Location = new Point(Width - F.Px(18) - _shown.Width, mid - _shown.Height / 2);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutParts(); }

    private void Commit()
    {
        if (_name.Text == _committed) return;
        _committed = _name.Text;
        Renamed?.Invoke(_name.Text);
    }

    public void SetMoveEnabled(bool canUp, bool canDown)
    {
        _up.Enabled = canUp;
        _down.Enabled = canDown;
    }

    public void Update(DeviceBattery shown, DeviceBattery original)
    {
        _icon.Text = Theme.Glyph(shown.Kind);
        _icon.Invalidate();
        if (!_name.Focused)
        {
            _name.Text = shown.Name;
            _committed = shown.Name;
        }
        _name.PlaceholderText = original.Name;
        _subtitle = shown.Name == original.Name
            ? Theme.StateText(shown)
            : $"{original.Name} · {Theme.StateText(shown).ToLowerInvariant()}";
        _percent = Theme.PercentText(shown, allowApprox: true);
        _charging = shown.IsCharging;
        _low = shown.Percent is int p && p <= 20 && !shown.IsCharging;
        Invalidate();
    }

    public void UpdateAbsent(string name, DeviceKind kind)
    {
        _icon.Text = Theme.Glyph(kind);
        _name.Text = _committed = name;
        _name.ReadOnly = true;
        _subtitle = "No conectado · oculto";
        _percent = "";
        Invalidate();
    }

    protected override float TextRight => _shown.Left;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using var sub = F.Text(12);
        F.DrawText(g, _subtitle, sub, F.Fg2, new RectangleF(TextLeft, F.Pf(38), _name.Width, F.Pf(18)));
        if (_name.Focused)
        {
            using var pen = new Pen(F.Accent, F.Pf(2));
            g.DrawLine(pen, _name.Left, _name.Bottom + F.Pf(2), _name.Right, _name.Bottom + F.Pf(2));
        }
        if (_percent.Length > 0)
        {
            using var pf = F.Text(15, semibold: true);
            var color = _charging ? Theme.Charging(F.Light) : _low ? Theme.Low(F.Light) : F.Fg;
            F.DrawText(g, _percent + (_charging ? " ⚡" : ""), pf, color,
                new RectangleF(_shown.Left - F.Pf(110), 0, F.Pf(90), Height), StringAlignment.Far);
        }
    }
}

/// <summary>Live preview of the widget on a strip coloured like the taskbar.</summary>
internal sealed class PreviewCard : SettingCard
{
    private readonly Func<IReadOnlyList<DeviceBattery>> _devices;
    private readonly AppSettings _settings;

    public PreviewCard(int width, Func<IReadOnlyList<DeviceBattery>> devices, AppSettings settings)
        : base(null, "", null, null, width)
    {
        _devices = devices;
        _settings = settings;
        Height = F.Px(112);
        Margin = new Padding(0, 0, 0, F.Px(16));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using var tf = F.Text(12);
        F.DrawText(g, "Vista previa", tf, F.Fg2, new RectangleF(F.Pf(18), F.Pf(8), F.Pf(200), F.Pf(20)));

        bool lightBar = Theme.IsLightTaskbar();
        var strip = new RectangleF(F.Pf(18), F.Pf(36), Width - F.Pf(36), F.Pf(56));
        F.FillRound(g, lightBar ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x1C, 0x1C, 0x1C), strip, F.Pf(6));

        using var bmp = new TaskbarWidget(_settings).RenderPreview(_devices(), F.Px(48), F.S, lightBar);
        float x = strip.Right - bmp.Width - F.Pf(110);
        g.DrawImage(bmp, Math.Max(strip.Left + F.Pf(8), x), strip.Top + (strip.Height - bmp.Height) / 2);

        // A hint of the tray next to it (chevron + clock), for context.
        using var gf = F.Glyphs(12);
        using var clock = F.Text(12);
        var fg = Theme.Foreground(lightBar);
        F.DrawText(g, "", gf, fg, new RectangleF(strip.Right - F.Pf(104), strip.Top, F.Pf(20), strip.Height), StringAlignment.Center);
        F.DrawText(g, DateTime.Now.ToString("HH:mm"), clock, fg, new RectangleF(strip.Right - F.Pf(70), strip.Top, F.Pf(60), strip.Height), StringAlignment.Center);
    }
}
