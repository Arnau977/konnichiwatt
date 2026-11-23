using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Konnichiwatt.Core;
using static Konnichiwatt.UI.Native;

namespace Konnichiwatt.UI;

/// <summary>
/// A per-pixel-alpha child window embedded in one Windows taskbar (Shell_TrayWnd or Shell_SecondaryTrayWnd),
/// placed just before its notification area / clock. Windows 11 has no API for taskbar toolbars any more,
/// so — like TrafficMonitor — we parent our own layered window to the taskbar and keep it positioned.
/// Horizontal taskbars get a row of items; vertical ones (ExplorerPatcher / StartAllBack) a column.
/// </summary>
internal sealed class TaskbarWidget : NativeWindow, IDisposable
{
    private IReadOnlyList<DeviceBattery> _devices = Array.Empty<DeviceBattery>();
    private readonly AppSettings _settings;
    private readonly IntPtr _taskbar;
    private readonly bool _primary;
    private readonly Func<IntPtr, Rectangle?> _anchorOf;
    private RECT _lastBounds;
    private bool _hover, _tracking, _vertical;
    private float _scale = 1f;

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action<TaskbarWidget, bool>? HoverChanged;

    public TaskbarWidget(AppSettings settings, IntPtr taskbar, bool primary, Func<IntPtr, Rectangle?> anchorOf)
    {
        _settings = settings;
        _taskbar = taskbar;
        _primary = primary;
        _anchorOf = anchorOf;
    }

    /// <summary>Offline instance for <c>--preview</c>.</summary>
    internal TaskbarWidget(AppSettings settings) : this(settings, IntPtr.Zero, true, _ => null) { }

    /// <summary>The taskbar window was destroyed (Explorer restart, monitor unplugged).</summary>
    public bool IsDead => !IsWindow(_taskbar);
    public float Scale => _scale;

    /// <summary>Screen rectangle of the widget (for anchoring the details popup).</summary>
    public Rectangle ScreenBounds
    {
        get
        {
            if (Handle == IntPtr.Zero || !GetWindowRect(Handle, out var r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    public Rectangle TaskbarBounds =>
        GetWindowRect(_taskbar, out var r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;

    public void SetDevices(IReadOnlyList<DeviceBattery> devices)
    {
        _devices = devices;
        Refresh(force: true);
    }

    /// <summary>Called every second: follows tray width / DPI / orientation changes.</summary>
    public void Refresh(bool force = false)
    {
        try
        {
            if (!_settings.ShowTaskbarWidget || _devices.Count == 0 || IsDead) { Detach(); return; }
            if (Handle != IntPtr.Zero && !IsWindow(Handle)) Detach();
            if (Handle == IntPtr.Zero) { Attach(); force = true; }

            if (!GetWindowRect(_taskbar, out var tb)) return;
            bool vertical = tb.Height > tb.Width;
            if (vertical != _vertical) { _vertical = vertical; force = true; }

            float scale = GetDpiForWindow(_taskbar) / 96f;
            if (scale <= 0) scale = 1;
            if (Math.Abs(scale - _scale) > 0.01f) { _scale = scale; force = true; }

            var anchor = Anchor(tb);
            var size = Measure(vertical ? tb.Width : 0, vertical ? 0 : tb.Height);
            int w = vertical ? tb.Width : (int)Math.Ceiling(size.Width);
            int h = vertical ? (int)Math.Ceiling(size.Height) : tb.Height;
            int gap = (int)(2 * _scale);
            int x = vertical ? 0 : anchor.Left - tb.Left - w - gap;
            int y = vertical ? anchor.Top - tb.Top - h - gap : 0;
            var bounds = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };

            bool moved = bounds.Left != _lastBounds.Left || bounds.Top != _lastBounds.Top ||
                         bounds.Width != _lastBounds.Width || bounds.Height != _lastBounds.Height;
            SetWindowPos(Handle, HWND_TOP, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            if (moved || force) Render(w, h);
            _lastBounds = bounds;
        }
        catch (Exception e) { Log.Error("TaskbarWidget.Refresh", e); }
    }

    /// <summary>Screen rect of the area we must stay clear of (tray / clock).</summary>
    private Rectangle Anchor(RECT tb)
    {
        if (_primary)
        {
            var tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && GetWindowRect(tray, out var tr) && tr.Width > 0)
                return Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom);
        }
        if (_anchorOf(_taskbar) is Rectangle r && r.Width > 0) return r;

        // Secondary taskbar whose clock we couldn't locate (yet): keep a margin for it.
        int margin = (int)((_primary ? 8 : 90) * _scale);
        return tb.Height > tb.Width
            ? Rectangle.FromLTRB(tb.Left, tb.Bottom - margin, tb.Right, tb.Bottom)
            : Rectangle.FromLTRB(tb.Right - margin, tb.Top, tb.Right, tb.Bottom);
    }

    private void Attach()
    {
        _lastBounds = default;
        var cp = new CreateParams
        {
            Caption = "KonnichiwattWidget",
            Parent = _taskbar,
            Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE,
            Width = 1, Height = 1,
        };
        CreateHandle(cp);
        Log.Write($"Widget attached to taskbar 0x{_taskbar:X}{(_primary ? " (principal)" : "")}");
    }

    private void Detach()
    {
        if (Handle == IntPtr.Zero) return;
        if (IsWindow(Handle)) DestroyHandle(); else ReleaseHandle();
        if (_hover) HoverChanged?.Invoke(this, false);
        _hover = _tracking = false;
    }

    // ---- Layout & drawing ----------------------------------------------------------

    private bool Bold => _settings.BoldStyle;
    private float IconPx => (Bold ? 17f : 16f) * _scale;
    private float TextPx => (Bold ? 13.5f : 12.5f) * _scale;
    private float BoltPx => (Bold ? 12f : 11f) * _scale;
    /// <summary>Extra outline that thickens the (single-weight) Fluent icon strokes.</summary>
    private float IconStroke => Bold ? 0.9f * _scale : 0f;
    private float Pad => 8 * _scale;
    private float Gap => 4 * _scale;
    private float ItemGap => 12 * _scale;
    private float BatteryW => (Bold ? 23f : 21f) * _scale;
    private float BatteryH => (Bold ? 12f : 11f) * _scale;

    /// <param name="fixedW">Taskbar width for vertical layout (0 for horizontal).</param>
    /// <param name="fixedH">Taskbar height for horizontal layout (0 for vertical).</param>
    private SizeF Measure(int fixedW, int fixedH)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        return Layout(g, light: false, fixedW > 0 ? fixedW : 1, fixedH > 0 ? fixedH : 1, draw: false);
    }

    private void Render(int w, int h)
    {
        if (Handle == IntPtr.Zero || w <= 0 || h <= 0) return;
        using var bmp = RenderBitmap(w, h, Theme.IsLightTaskbar());
        Present(bmp);
    }

    /// <summary>Offline rendering for <c>--preview</c> (no window needed).</summary>
    internal Bitmap RenderPreview(IReadOnlyList<DeviceBattery> devices, int h, float scale, bool light, int verticalWidth = 0)
    {
        _devices = devices;
        _scale = scale;
        _vertical = verticalWidth > 0;
        var size = Measure(verticalWidth, verticalWidth > 0 ? 0 : h);
        return _vertical
            ? RenderBitmap(verticalWidth, (int)Math.Ceiling(size.Height), light)
            : RenderBitmap((int)Math.Ceiling(size.Width), h, light);
    }

    private Bitmap RenderBitmap(int w, int h, bool light)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 1: invisible but still receives the mouse
            if (_hover)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float inset = 4 * _scale;
                var area = _vertical
                    ? new RectangleF(inset, 0, w - 2 * inset, h)
                    : new RectangleF(0, inset, w, h - 2 * inset);
                using var path = RoundRect(area, 4 * _scale);
                using var hb = new SolidBrush(Theme.Hover(light));
                g.FillPath(hb, path);
            }
            Layout(g, light, w, h, draw: true);
        }
        return bmp;
    }

    /// <summary>Single code path for measuring and drawing, so the window size always matches the content.</summary>
    private SizeF Layout(Graphics g, bool light, int w, int h, bool draw)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var icon = Theme.IconFont(IconPx);
        using var text = Theme.TextFont(TextPx, Bold);
        using var bolt = Theme.IconFont(BoltPx);

        var mode = _settings.Display;
        bool showIcon = mode != BatteryDisplay.Percent;
        bool showPct = mode != BatteryDisplay.Icon;
        float x = Pad, y = Pad, cy = h / 2f, cx = w / 2f;

        for (int i = 0; i < _devices.Count; i++)
        {
            var d = _devices[i];
            bool low = d.Percent is int p && p <= _settings.LowBatteryThreshold && !d.IsCharging;
            Color Dim(Color c) => d.Stale ? Color.FromArgb(110, c) : c;
            var fg = Dim(low ? Theme.Low(light) : Theme.Foreground(light));
            var accent = Dim(d.IsCharging ? Theme.Charging(light) : low ? Theme.Low(light) : Theme.Foreground(light));
            var chargeColor = Dim(Theme.Charging(light));
            var pct = showPct ? Theme.PercentText(d, allowApprox: false) : "";
            var glyph = Theme.Glyph(d.Kind);

            if (!_vertical)
            {
                if (i > 0) x += ItemGap;
                x += Piece(g, glyph, icon, fg, x, cy, IconStroke, draw) + Gap;
                if (showIcon)
                {
                    x += Battery(g, d.Percent, fg, accent, x, cy, draw);
                    if (showPct) x += Gap;
                }
                if (pct.Length > 0) x += Piece(g, pct, text, fg, x, cy, 0, draw);
                if (d.IsCharging) x += 1 * _scale + Piece(g, Theme.Bolt, bolt, chargeColor, x + 1 * _scale, cy, IconStroke * 0.6f, draw);
            }
            else
            {
                // Column: icon / battery / "62%⚡", each line centred.
                if (i > 0) y += ItemGap;
                var gs = Size(g, glyph, icon);
                Piece(g, glyph, icon, fg, cx - gs.Width / 2, y + gs.Height / 2, IconStroke, draw);
                y += gs.Height + Gap / 2;
                if (showIcon)
                {
                    Battery(g, d.Percent, fg, accent, cx - (BatteryW + 2 * _scale) / 2, y + BatteryH / 2, draw);
                    y += BatteryH + Gap;
                }
                if (pct.Length > 0 || d.IsCharging)
                {
                    var ts = pct.Length > 0 ? Size(g, pct, text) : SizeF.Empty;
                    var bs = d.IsCharging ? Size(g, Theme.Bolt, bolt) : SizeF.Empty;
                    float lineW = ts.Width + (d.IsCharging ? 1 * _scale + bs.Width : 0);
                    float lineH = Math.Max(ts.Height, bs.Height);
                    float lx = cx - lineW / 2, lcy = y + lineH / 2;
                    if (pct.Length > 0) lx += Piece(g, pct, text, fg, lx, lcy, 0, draw);
                    if (d.IsCharging) Piece(g, Theme.Bolt, bolt, chargeColor, lx + 1 * _scale, lcy, IconStroke * 0.6f, draw);
                    y += lineH;
                }
            }
        }
        return _vertical ? new SizeF(w, y + Pad) : new SizeF(x + Pad, h);
    }

    /// <summary>
    /// Hand-drawn horizontal battery: outline in <paramref name="outline"/>, fill proportional to the level in
    /// <paramref name="fill"/> (green when charging, red when low). Much more legible than the Fluent glyph at 16 px.
    /// </summary>
    private float Battery(Graphics g, int? percent, Color outline, Color fill, float x, float cy, bool draw)
    {
        float bw = BatteryW, bh = BatteryH;
        float stroke = (Bold ? 1.7f : 1.2f) * _scale;
        float nubW = 2f * _scale, nubH = bh * 0.42f;
        if (!draw) return bw + nubW;

        var body = new RectangleF(x + stroke / 2, cy - bh / 2 + stroke / 2, bw - stroke, bh - stroke);
        using (var path = RoundRect(body, 2.5f * _scale))
        using (var pen = new Pen(outline, stroke))
            g.DrawPath(pen, path);
        using (var nub = new SolidBrush(outline))
            g.FillRectangle(nub, x + bw, cy - nubH / 2, nubW, nubH);

        if (percent is int p && p > 0)
        {
            float inset = stroke + 1.2f * _scale;
            var inner = new RectangleF(x + inset, cy - bh / 2 + inset, bw - 2 * inset, bh - 2 * inset);
            inner.Width = Math.Max(1.5f * _scale, inner.Width * Math.Min(p, 100) / 100f);
            using var fb = new SolidBrush(fill);
            using var fp = RoundRect(inner, 1f * _scale);
            g.FillPath(fb, fp);
        }
        return bw + nubW;
    }

    private static SizeF Size(Graphics g, string s, Font font) =>
        g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic);

    /// <summary>Draws (or just measures) a string vertically centred at <paramref name="cy"/>; returns its advance width.</summary>
    private static float Piece(Graphics g, string s, Font font, Color color, float x, float cy, float stroke, bool draw)
    {
        var fmt = StringFormat.GenericTypographic;
        var size = g.MeasureString(s, font, PointF.Empty, fmt);
        if (!draw) return size.Width;

        float y = cy - size.Height / 2f;
        using var brush = new SolidBrush(color);
        if (stroke <= 0)
        {
            g.DrawString(s, font, brush, x, y, fmt);
        }
        else
        {
            using var path = new GraphicsPath();
            path.AddString(s, font.FontFamily, (int)font.Style, font.Size, new PointF(x, y), fmt);
            using var pen = new Pen(color, stroke) { LineJoin = LineJoin.Round };
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        return size.Width;
    }

    private void Present(Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            if (!UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, ULW_ALPHA))
                Log.Write($"UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---- Input -------------------------------------------------------------------------

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_MOUSEMOVE:
                if (!_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    _tracking = TrackMouseEvent(ref tme);
                }
                if (!_hover) { _hover = true; Refresh(force: true); HoverChanged?.Invoke(this, true); }
                break;
            case WM_MOUSELEAVE:
                _tracking = false;
                if (_hover) { _hover = false; Refresh(force: true); HoverChanged?.Invoke(this, false); }
                break;
            case WM_LBUTTONUP:
                LeftClick?.Invoke();
                break;
            case WM_RBUTTONUP:
                RightClick?.Invoke();
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose() => Detach();
}
