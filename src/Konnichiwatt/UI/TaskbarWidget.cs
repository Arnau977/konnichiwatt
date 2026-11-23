using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Konnichiwatt.Core;
using static Konnichiwatt.UI.Native;

namespace Konnichiwatt.UI;

/// <summary>
/// A per-pixel-alpha child window embedded in the Windows taskbar (Shell_TrayWnd), placed just left of
/// the notification area. Windows 11 has no API for taskbar toolbars any more, so — like TrafficMonitor —
/// we parent our own layered window to the taskbar and keep it positioned.
/// </summary>
internal sealed class TaskbarWidget : NativeWindow, IDisposable
{
    private IReadOnlyList<DeviceBattery> _devices = Array.Empty<DeviceBattery>();
    private readonly AppSettings _settings;
    private IntPtr _taskbar;
    private RECT _lastBounds;
    private bool _hover, _tracking;
    private float _scale = 1f;

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action<bool>? HoverChanged;

    /// <summary>Screen rectangle of the widget (for anchoring the details popup).</summary>
    public Rectangle ScreenBounds
    {
        get
        {
            if (Handle == IntPtr.Zero || !GetWindowRect(Handle, out var r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    public TaskbarWidget(AppSettings settings) => _settings = settings;

    public void SetDevices(IReadOnlyList<DeviceBattery> devices)
    {
        _devices = devices;
        Refresh(force: true);
    }

    /// <summary>Called periodically: (re)attaches to the taskbar (Explorer may restart), follows tray width changes.</summary>
    public void Refresh(bool force = false)
    {
        try
        {
            if (!_settings.ShowTaskbarWidget || _devices.Count == 0) { Detach(); return; }

            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) { Detach(); return; }
            if (Handle != IntPtr.Zero && (!IsWindow(Handle) || GetParent(Handle) != taskbar)) Detach();
            if (Handle == IntPtr.Zero) { Attach(taskbar); force = true; }

            if (!GetWindowRect(_taskbar, out var tb)) return;
            var tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero || !GetWindowRect(tray, out var tr)) return;
            if (tb.Width < tb.Height) { Detach(); return; } // vertical taskbar: not supported, tray icon still works

            float scale = GetDpiForWindow(_taskbar) / 96f;
            if (scale <= 0) scale = 1;
            if (Math.Abs(scale - _scale) > 0.01f) { _scale = scale; force = true; }

            int w = MeasureWidth();
            int h = tb.Height;
            int x = tr.Left - tb.Left - w - (int)(2 * _scale);
            var bounds = new RECT { Left = x, Top = 0, Right = x + w, Bottom = h };

            bool moved = bounds.Left != _lastBounds.Left || bounds.Width != _lastBounds.Width || bounds.Height != _lastBounds.Height;
            SetWindowPos(Handle, HWND_TOP, x, 0, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            if (moved || force) Render(w, h);
            _lastBounds = bounds;
        }
        catch (Exception e) { Log.Error("TaskbarWidget.Refresh", e); }
    }

    private void Attach(IntPtr taskbar)
    {
        _taskbar = taskbar;
        _lastBounds = default;
        var cp = new CreateParams
        {
            Caption = "KonnichiwattWidget",
            Parent = taskbar,
            Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE,
            Width = 1, Height = 1,
        };
        CreateHandle(cp);
        Log.Write($"Widget attached to taskbar 0x{taskbar:X}");
    }

    private void Detach()
    {
        if (Handle == IntPtr.Zero) return;
        if (IsWindow(Handle)) DestroyHandle(); else ReleaseHandle();
        _hover = _tracking = false;
        HoverChanged?.Invoke(false);
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

    private int MeasureWidth()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        return (int)Math.Ceiling(Layout(g, light: false, h: 1, draw: false));
    }

    private void Render(int w, int h)
    {
        if (Handle == IntPtr.Zero || w <= 0 || h <= 0) return;
        using var bmp = RenderBitmap(w, h, Theme.IsLightTaskbar());
        Present(bmp);
    }

    /// <summary>Offline rendering for <c>--preview</c> (no window needed).</summary>
    internal Bitmap RenderPreview(IReadOnlyList<DeviceBattery> devices, int h, float scale, bool light)
    {
        _devices = devices;
        _scale = scale;
        return RenderBitmap(MeasureWidth(), h, light);
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
                float vInset = 4 * _scale;
                using var path = RoundRect(new RectangleF(0, vInset, w, h - 2 * vInset), 4 * _scale);
                using var hb = new SolidBrush(Theme.Hover(light));
                g.FillPath(hb, path);
            }
            Layout(g, light, h, draw: true);
        }
        return bmp;
    }

    /// <summary>Single code path for measuring and drawing, so the window width always matches the content.</summary>
    private float Layout(Graphics g, bool light, int h, bool draw)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var icon = Theme.IconFont(IconPx);
        using var text = Theme.TextFont(TextPx, Bold);
        using var bolt = Theme.IconFont(BoltPx);

        var mode = _settings.Display;
        float x = Pad, cy = h / 2f;
        for (int i = 0; i < _devices.Count; i++)
        {
            var d = _devices[i];
            if (i > 0) x += ItemGap;

            bool low = d.Percent is int p && p <= _settings.LowBatteryThreshold && !d.IsCharging;
            Color Dim(Color c) => d.Stale ? Color.FromArgb(110, c) : c;
            var fg = Dim(low ? Theme.Low(light) : Theme.Foreground(light));
            var accent = Dim(d.IsCharging ? Theme.Charging(light) : low ? Theme.Low(light) : Theme.Foreground(light));

            x += Piece(g, Theme.Glyph(d.Kind), icon, fg, x, cy, IconStroke, draw) + Gap;

            bool showIcon = mode != BatteryDisplay.Percent;
            bool showPct = mode != BatteryDisplay.Icon;
            if (showIcon)
            {
                x += Battery(g, d.Percent, fg, accent, x, cy, draw);
                if (showPct) x += Gap;
            }
            if (showPct)
            {
                var t = Theme.PercentText(d, allowApprox: false);
                if (t.Length > 0) x += Piece(g, t, text, fg, x, cy, 0, draw);
            }
            if (d.IsCharging)
                x += 1 * _scale + Piece(g, Theme.Bolt, bolt, Dim(Theme.Charging(light)), x + 1 * _scale, cy, IconStroke * 0.6f, draw);
        }
        return x + Pad;
    }

    /// <summary>
    /// Hand-drawn horizontal battery: outline in <paramref name="outline"/>, fill proportional to the level in
    /// <paramref name="fill"/> (green when charging, red when low). Much more legible than the Fluent glyph at 16 px.
    /// </summary>
    private float Battery(Graphics g, int? percent, Color outline, Color fill, float x, float cy, bool draw)
    {
        float bw = (Bold ? 23f : 21f) * _scale, bh = (Bold ? 12f : 11f) * _scale;
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
                if (!_hover) { _hover = true; Refresh(force: true); HoverChanged?.Invoke(true); }
                break;
            case WM_MOUSELEAVE:
                _tracking = false;
                if (_hover) { _hover = false; Refresh(force: true); HoverChanged?.Invoke(false); }
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
