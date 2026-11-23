using System.Drawing.Text;
using Konnichiwatt.Core;
using static Konnichiwatt.UI.Native;

namespace Konnichiwatt.UI;

/// <summary>Windows 11-style flyout shown while hovering the taskbar widget: one row per device.</summary>
internal sealed class DetailsPopup : Form
{
    private IReadOnlyList<DeviceBattery> _devices = Array.Empty<DeviceBattery>();
    private bool _light;
    private float _scale = 1;

    public DetailsPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
    }

    /// <summary>Per-device "low battery" rule (each device may have its own threshold).</summary>
    public Func<DeviceBattery, bool>? IsLow { get; set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    /// <summary>Shows the flyout next to <paramref name="anchor"/>, on the screen side of the taskbar it lives in.</summary>
    public void ShowNear(Rectangle anchor, Rectangle taskbar, IReadOnlyList<DeviceBattery> devices, float scale)
    {
        if (anchor.IsEmpty) return;
        _devices = devices;
        _scale = scale;
        _light = Theme.IsLightTaskbar();
        BackColor = Theme.PopupBack(_light);

        var size = Measure();
        var screen = Screen.FromRectangle(anchor).Bounds;
        int gap = (int)(10 * scale);
        int x, y;
        if (taskbar.Width >= taskbar.Height) // horizontal taskbar: above (bottom bar) or below (top bar)
        {
            x = anchor.Left + anchor.Width / 2 - size.Width / 2;
            y = taskbar.Top > screen.Top + screen.Height / 2 ? taskbar.Top - size.Height - gap : taskbar.Bottom + gap;
        }
        else // vertical taskbar: to the right (left bar) or to the left (right bar)
        {
            y = anchor.Top + anchor.Height / 2 - size.Height / 2;
            x = taskbar.Left < screen.Left + screen.Width / 2 ? taskbar.Right + gap : taskbar.Left - size.Width - gap;
        }
        x = Math.Clamp(x, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - size.Width - 8));
        y = Math.Clamp(y, screen.Top + 8, Math.Max(screen.Top + 8, screen.Bottom - size.Height - 8));
        Bounds = new Rectangle(x, y, size.Width, size.Height);
        if (!Visible) Show();
        Invalidate();
    }

    /// <summary>Battery history: sparkline per row and "time left" in the subtitle.</summary>
    public BatteryHistory? History { get; set; }

    private float RowH => 40 * _scale;
    private float Pad => 14 * _scale;
    private float SparkW => 84 * _scale;
    private static readonly TimeSpan SparkSpan = TimeSpan.FromHours(12);

    private string Subtitle(DeviceBattery d) =>
        History?.EstimateText(d.Id) is string est && !d.Stale ? $"{Theme.StateText(d)} · {est}" : Theme.StateText(d);

    private Size Measure()
    {
        using var g = CreateGraphics();
        using var name = Theme.TextFont(13 * _scale);
        using var sub = Theme.TextFont(11 * _scale);
        using var pct = Theme.TextFont(14 * _scale, FontStyle.Bold);
        float textW = 0, pctW = 0;
        foreach (var d in _devices)
        {
            textW = Math.Max(textW, g.MeasureString(d.Name, name).Width);
            textW = Math.Max(textW, g.MeasureString(Subtitle(d), sub).Width);
            pctW = Math.Max(pctW, g.MeasureString(Theme.PercentText(d, true), pct).Width);
        }
        float w = Pad + 32 * _scale + Math.Max(textW, 140 * _scale) + 14 * _scale + SparkW + 12 * _scale
                  + Math.Max(pctW, 44 * _scale) + 22 * _scale + Pad;
        float h = Pad * 2 + Math.Max(1, _devices.Count) * RowH;
        return new Size((int)w, (int)h);
    }

    /// <summary>Last 12 h of a device's level: a thin line (green while charging), gaps where there was no data.</summary>
    private void DrawSparkline(Graphics g, string id, RectangleF box)
    {
        var samples = History?.For(id);
        if (samples == null || samples.Count < 2) return;
        var now = DateTime.Now;
        var from = now - SparkSpan;

        using (var baseline = new Pen(Color.FromArgb(0x30, Theme.Secondary(_light)), 1 * _scale))
            g.DrawLine(baseline, box.Left, box.Bottom, box.Right, box.Bottom);

        PointF P(BatterySample s) => new(
            box.Left + (float)((s.Time - from).TotalSeconds / SparkSpan.TotalSeconds) * box.Width,
            box.Bottom - s.Percent / 100f * box.Height);

        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var line = new Pen(Theme.Secondary(_light), 1.6f * _scale) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        using var charge = new Pen(Theme.Charging(_light), 1.6f * _scale) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        BatterySample? prev = null;
        foreach (var s in samples.Where(s => s.Time >= from - TimeSpan.FromMinutes(30)))
        {
            if (prev != null && s.Time - prev.Time < TimeSpan.FromHours(2))
            {
                var a = P(prev); var b = P(s);
                a.X = Math.Max(a.X, box.Left);
                g.DrawLine(s.Charging && prev.Charging ? charge : line, a, b);
            }
            prev = s;
        }
        if (prev != null)
        {
            // Extend to "now" at the last known level, and mark it with a dot.
            var end = new PointF(box.Right, box.Bottom - prev.Percent / 100f * box.Height);
            var last = P(prev);
            if (now - prev.Time < TimeSpan.FromHours(2)) g.DrawLine(prev.Charging ? charge : line, last, end);
            using var dot = new SolidBrush(prev.Charging ? Theme.Charging(_light) : Theme.Foreground(_light));
            float r = 2.2f * _scale;
            g.FillEllipse(dot, end.X - r, end.Y - r, 2 * r, 2 * r);
        }
        g.SmoothingMode = smooth;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var icon = Theme.IconFont(18 * _scale);
        using var batt = Theme.IconFont(16 * _scale);
        using var name = Theme.TextFont(13 * _scale);
        using var sub = Theme.TextFont(11 * _scale);
        using var pct = Theme.TextFont(14 * _scale, FontStyle.Bold);
        using var fg = new SolidBrush(Theme.Foreground(_light));
        using var fg2 = new SolidBrush(Theme.Secondary(_light));

        float y = Pad;
        float right = Width - Pad;
        foreach (var d in _devices)
        {
            bool low = IsLow?.Invoke(d) ?? false;
            using var accent = new SolidBrush(d.IsCharging ? Theme.Charging(_light) : low ? Theme.Low(_light) : Theme.Foreground(_light));

            g.DrawString(Theme.Glyph(d.Kind), icon, fg, Pad, y + (RowH - 18 * _scale) / 2f - 2 * _scale);
            float tx = Pad + 32 * _scale;
            g.DrawString(d.Name, name, fg, tx, y + 3 * _scale);
            g.DrawString(Subtitle(d), sub, fg2, tx, y + 21 * _scale);

            var bg = Theme.BatteryGlyph(d.Percent, d.IsCharging);
            g.DrawString(bg, batt, accent, right - 18 * _scale, y + (RowH - 16 * _scale) / 2f);
            var pt = Theme.PercentText(d, true);
            var ps = g.MeasureString(pt, pct);
            float pctLeft = right - 22 * _scale - Math.Max(ps.Width, 44 * _scale);
            g.DrawString(pt, pct, d.Stale ? fg2 : fg, right - 22 * _scale - ps.Width, y + (RowH - ps.Height) / 2f);

            DrawSparkline(g, d.Id, new RectangleF(pctLeft - 12 * _scale - SparkW, y + 9 * _scale, SparkW, RowH - 18 * _scale));
            y += RowH;
        }
    }
}
