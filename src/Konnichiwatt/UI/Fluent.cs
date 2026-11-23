using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Konnichiwatt.UI.Fluent;

/// <summary>
/// Colours, scale and drawing helpers shared by the hand-drawn Windows 11-style controls of the settings window.
/// Same surfaces as the widget's hover flyout: #202020 / #2B2B2B cards on dark, #F3F3F3 / #FBFBFB on light.
/// </summary>
internal static class F
{
    public static bool Light { get; private set; }
    public static float S { get; private set; } = 1f;

    public static Color Back, Card, CardHover, Stroke, Fg, Fg2, Accent, OnAccent, ControlBack, ControlHover, ControlStroke, Track;

    public static void Init(bool light, float scale)
    {
        Light = light;
        S = scale <= 0 ? 1 : scale;
        Back = light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x20, 0x20, 0x20);
        Card = light ? Color.FromArgb(0xFB, 0xFB, 0xFB) : Color.FromArgb(0x2B, 0x2B, 0x2B);
        CardHover = light ? Color.FromArgb(0xF6, 0xF6, 0xF6) : Color.FromArgb(0x32, 0x32, 0x32);
        Stroke = light ? Color.FromArgb(0xE5, 0xE5, 0xE5) : Color.FromArgb(0x1D, 0x1D, 0x1D);
        Fg = Theme.Foreground(light);
        Fg2 = Theme.Secondary(light);
        Accent = Theme.Accent(light);
        OnAccent = Theme.OnColor(Accent);
        ControlBack = light ? Color.White : Color.FromArgb(0x37, 0x37, 0x37);
        ControlHover = light ? Color.FromArgb(0xF9, 0xF9, 0xF9) : Color.FromArgb(0x3C, 0x3C, 0x3C);
        ControlStroke = light ? Color.FromArgb(0xD5, 0xD5, 0xD5) : Color.FromArgb(0x45, 0x45, 0x45);
        Track = light ? Color.FromArgb(0x8A, 0x8A, 0x8A) : Color.FromArgb(0x9A, 0x9A, 0x9A);
    }

    public static int Px(float v) => (int)Math.Round(v * S);
    public static float Pf(float v) => v * S;

    public static Font Text(float px, bool semibold = false) => Theme.TextFont(px * S, semibold);
    public static Font Glyphs(float px) => Theme.IconFont(px * S);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using var p = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, Color c, RectangleF r, float radius, float width = 1)
    {
        using var p = Round(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, p);
    }

    /// <summary>Draws text vertically centred in <paramref name="box"/>, honouring alignment; ellipsis if too long.</summary>
    public static void DrawText(Graphics g, string s, Font f, Color c, RectangleF box, StringAlignment align = StringAlignment.Near)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = align,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(s, f, b, box, fmt);
    }

    public static SizeF Measure(Graphics g, string s, Font f) => g.MeasureString(s, f);
}

/// <summary>Base for hand-drawn controls: double-buffered, transparent background, hover tracking.</summary>
internal abstract class FluentControl : Control
{
    protected bool Hover;
    protected bool Pressed;

    protected FluentControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

/// <summary>Windows 11 toggle switch (40×20).</summary>
internal sealed class ToggleSwitch : FluentControl
{
    private bool _on;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch(bool on)
    {
        _on = on;
        Size = new Size(F.Px(40), F.Px(20));
    }

    public bool Checked
    {
        get => _on;
        set { if (_on == value) return; _on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Updates the state without raising <see cref="CheckedChanged"/>.</summary>
    public void SetSilently(bool on) { _on = on; Invalidate(); }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float rad = r.Height / 2;
        float knob = _on ? r.Height - F.Pf(8) : r.Height - F.Pf(10);
        if (Hover) knob += F.Pf(2);
        if (_on)
        {
            F.FillRound(g, Enabled ? F.Accent : F.Track, r, rad);
            using var kb = new SolidBrush(F.OnAccent);
            float cx = r.Right - r.Height / 2;
            g.FillEllipse(kb, cx - knob / 2, r.Top + (r.Height - knob) / 2, knob, knob);
        }
        else
        {
            if (Hover) F.FillRound(g, Color.FromArgb(F.Light ? 0x0A : 0x14, F.Fg), r, rad);
            F.StrokeRound(g, F.Track, r, rad);
            using var kb = new SolidBrush(F.Track);
            float cx = r.Left + r.Height / 2;
            g.FillEllipse(kb, cx - knob / 2, r.Top + (r.Height - knob) / 2, knob, knob);
        }
    }
}

/// <summary>Row of mutually exclusive options (like the Windows 11 "segmented" selector).</summary>
internal sealed class Segmented : FluentControl
{
    private readonly string[] _items;
    private int _selected, _hoverIndex = -1;
    public event EventHandler? SelectedChanged;

    public Segmented(string[] items, int selected)
    {
        _items = items;
        _selected = selected;
        using var g = CreateGraphics();
        using var f = F.Text(13);
        int w = items.Sum(s => (int)F.Measure(g, s, f).Width + F.Px(24));
        Size = new Size(w + F.Px(4), F.Px(32));
    }

    public int Selected => _selected;

    private RectangleF Segment(int i)
    {
        float inner = Width - F.Pf(4);
        float x = F.Pf(2);
        using var g = CreateGraphics();
        using var f = F.Text(13);
        float total = _items.Sum(s => F.Measure(g, s, f).Width + F.Pf(24));
        for (int k = 0; k < i; k++) x += (F.Measure(g, _items[k], f).Width + F.Pf(24)) * inner / total;
        float w = (F.Measure(g, _items[i], f).Width + F.Pf(24)) * inner / total;
        return new RectangleF(x, F.Pf(2), w, Height - F.Pf(4));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Enumerable.Range(0, _items.Length).FirstOrDefault(i => Segment(i).Contains(e.Location), -1);
        if (h != _hoverIndex) { _hoverIndex = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hoverIndex = -1; base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = Enumerable.Range(0, _items.Length).FirstOrDefault(k => Segment(k).Contains(e.Location), -1);
        if (i >= 0 && i != _selected)
        {
            _selected = i;
            Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        F.FillRound(g, F.ControlBack, r, F.Pf(5));
        F.StrokeRound(g, F.ControlStroke, r, F.Pf(5));
        using var f = F.Text(13);
        using var fs = F.Text(13, semibold: true);
        for (int i = 0; i < _items.Length; i++)
        {
            var s = Segment(i);
            if (i == _selected) F.FillRound(g, F.Accent, s, F.Pf(4));
            else if (i == _hoverIndex) F.FillRound(g, F.ControlHover, s, F.Pf(4));
            F.DrawText(g, _items[i], i == _selected ? fs : f, i == _selected ? F.OnAccent : F.Fg, s, StringAlignment.Center);
        }
    }
}

/// <summary>Horizontal slider with the value printed on its right.</summary>
internal sealed class Slider : FluentControl
{
    private readonly int _min, _max, _step;
    private readonly Func<int, string> _format;
    private int _value;
    private bool _dragging;
    public event EventHandler? ValueChanged;

    public Slider(int min, int max, int step, int value, Func<int, string> format)
    {
        (_min, _max, _step, _format) = (min, max, step, format);
        _value = Math.Clamp(value, min, max);
        Size = new Size(F.Px(240), F.Px(32));
    }

    public int Value => _value;
    private float LabelW => F.Pf(52);
    private float TrackLeft => F.Pf(10);
    private float TrackRight => Width - LabelW - F.Pf(10);

    private void SetFromX(int x)
    {
        float t = Math.Clamp((x - TrackLeft) / (TrackRight - TrackLeft), 0, 1);
        int v = (int)Math.Round((_min + t * (_max - _min)) / _step) * _step;
        v = Math.Clamp(v, _min, _max);
        if (v == _value) return;
        _value = v;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { _dragging = true; SetFromX(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        float cy = Height / 2f, th = F.Pf(4);
        float t = (_value - _min) / (float)(_max - _min);
        float tx = TrackLeft + t * (TrackRight - TrackLeft);
        F.FillRound(g, Color.FromArgb(F.Light ? 0x60 : 0x70, F.Track), new RectangleF(TrackLeft, cy - th / 2, TrackRight - TrackLeft, th), th / 2);
        F.FillRound(g, F.Accent, new RectangleF(TrackLeft, cy - th / 2, Math.Max(th, tx - TrackLeft), th), th / 2);

        float outer = F.Pf(20), inner = Hover || _dragging ? F.Pf(12) : F.Pf(10);
        using (var ob = new SolidBrush(F.ControlBack)) g.FillEllipse(ob, tx - outer / 2, cy - outer / 2, outer, outer);
        using (var op = new Pen(F.ControlStroke)) g.DrawEllipse(op, tx - outer / 2, cy - outer / 2, outer, outer);
        using (var ib = new SolidBrush(F.Accent)) g.FillEllipse(ib, tx - inner / 2, cy - inner / 2, inner, inner);

        using var f = F.Text(13, semibold: true);
        F.DrawText(g, _format(_value), f, F.Fg, new RectangleF(Width - LabelW, 0, LabelW, Height), StringAlignment.Far);
    }
}

/// <summary>Standard Windows 11 button (rounded, subtle border).</summary>
internal sealed class FlatButton : FluentControl
{
    public FlatButton(string text)
    {
        Text = text;
        using var g = CreateGraphics();
        using var f = F.Text(13);
        Size = new Size((int)F.Measure(g, text, f).Width + F.Px(28), F.Px(32));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        F.FillRound(g, Pressed ? F.CardHover : Hover ? F.ControlHover : F.ControlBack, r, F.Pf(5));
        F.StrokeRound(g, F.ControlStroke, r, F.Pf(5));
        using var f = F.Text(13);
        F.DrawText(g, Text, f, Enabled ? F.Fg : F.Fg2, r, StringAlignment.Center);
    }
}

/// <summary>Borderless icon button (only shows a background on hover).</summary>
internal sealed class GlyphButton : FluentControl
{
    private readonly float _glyphPx;

    public GlyphButton(string glyph, float size = 32, float glyphPx = 14)
    {
        Text = glyph;
        _glyphPx = glyphPx;
        Size = new Size(F.Px(size), F.Px(size));
    }

    public Color? GlyphColor { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (Enabled && (Hover || Pressed)) F.FillRound(g, Pressed ? F.ControlBack : F.ControlHover, r, F.Pf(5));
        using var f = F.Glyphs(_glyphPx);
        var c = !Enabled ? Color.FromArgb(0x60, F.Fg2) : GlyphColor ?? F.Fg;
        F.DrawText(g, Text, f, c, r, StringAlignment.Center);
    }
}

/// <summary>Rounded card with optional glyph, title, description and a control aligned to the right.</summary>
internal class SettingCard : Panel
{
    private readonly string? _glyph;
    private readonly string _title;
    private string? _description;
    private readonly Control? _accessory;

    public SettingCard(string? glyph, string title, string? description, Control? accessory, int width)
    {
        _glyph = glyph;
        _title = title;
        _description = description;
        _accessory = accessory;
        DoubleBuffered = true;
        BackColor = F.Back;
        Margin = new Padding(0, 0, 0, F.Px(4));
        Size = new Size(width, F.Px(description == null ? 58 : 70));
        if (accessory != null) Controls.Add(accessory);
        LayoutAccessory();
    }

    public string? Description
    {
        get => _description;
        set { _description = value; Invalidate(); }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutAccessory(); }

    private void LayoutAccessory()
    {
        if (_accessory == null) return;
        _accessory.Location = new Point(Width - _accessory.Width - F.Px(18), (Height - _accessory.Height) / 2);
    }

    protected virtual float TextRight => _accessory != null ? _accessory.Left - F.Pf(16) : Width - F.Pf(18);
    protected virtual Color CardFill => F.Card;
    protected virtual Color CardBorder => F.Stroke;
    protected virtual float CardBorderWidth => 1;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        F.FillRound(g, CardFill, r, F.Pf(6));
        F.StrokeRound(g, CardBorder, r, CardBorderWidth);

        float x = F.Pf(18);
        if (_glyph != null)
        {
            using var gf = F.Glyphs(16);
            F.DrawText(g, _glyph, gf, F.Fg, new RectangleF(x, 0, F.Pf(20), Height));
            x += F.Pf(36);
        }
        using var tf = F.Text(14);
        using var df = F.Text(12);
        float w = TextRight - x;
        if (_description == null)
            F.DrawText(g, _title, tf, F.Fg, new RectangleF(x, 0, w, Height));
        else
        {
            F.DrawText(g, _title, tf, F.Fg, new RectangleF(x, Height / 2f - F.Pf(19), w, F.Pf(20)));
            F.DrawText(g, _description, df, F.Fg2, new RectangleF(x, Height / 2f + F.Pf(1), w, F.Pf(18)));
        }
    }
}

/// <summary>
/// The classic "grip" drag handle (2×3 dots). While the left button is held it reports the pointer's vertical
/// position in screen coordinates — the control keeps mouse capture, so it works even outside its bounds.
/// </summary>
internal sealed class GripHandle : FluentControl
{
    private bool _dragging;
    public event Action? DragStarted;
    public event Action<int>? DragMoved;   // screen Y
    public event Action? DragEnded;

    public GripHandle()
    {
        Size = new Size(F.Px(20), F.Px(36));
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        DragStarted?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) DragMoved?.Invoke(PointToScreen(e.Location).Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        End();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        End(); // e.g. Alt+Tab while dragging
    }

    private void End()
    {
        if (!_dragging) return;
        _dragging = false;
        DragEnded?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (Hover || _dragging) F.FillRound(g, F.ControlHover, r, F.Pf(4));
        float dot = F.Pf(3), gap = F.Pf(5.5f);
        float x0 = Width / 2f - gap / 2 - dot / 2, y0 = Height / 2f - gap - dot / 2;
        using var b = new SolidBrush(Hover || _dragging ? F.Fg : F.Fg2);
        for (int col = 0; col < 2; col++)
        for (int row = 0; row < 3; row++)
            g.FillEllipse(b, x0 + col * gap, y0 + row * gap, dot, dot);
    }
}

/// <summary>Left navigation entry (glyph + label, accent pill when selected).</summary>
internal sealed class NavItem : FluentControl
{
    private readonly string _glyph;
    private bool _selected;

    public NavItem(string glyph, string text, int width)
    {
        _glyph = glyph;
        Text = text;
        Size = new Size(width, F.Px(38));
        Margin = new Padding(0, 0, 0, F.Px(4));
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (_selected || Hover) F.FillRound(g, _selected ? F.Card : F.CardHover, r, F.Pf(5));
        if (_selected)
            F.FillRound(g, F.Accent, new RectangleF(0, Height / 2f - F.Pf(8), F.Pf(3), F.Pf(16)), F.Pf(1.5f));
        using var gf = F.Glyphs(16);
        using var tf = F.Text(14);
        F.DrawText(g, _glyph, gf, F.Fg, new RectangleF(F.Pf(14), 0, F.Pf(20), Height));
        F.DrawText(g, Text, tf, F.Fg, new RectangleF(F.Pf(46), 0, Width - F.Pf(50), Height));
    }
}
