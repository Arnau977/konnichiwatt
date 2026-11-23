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

    public void ShowAbove(Rectangle anchor, IReadOnlyList<DeviceBattery> devices, float scale)
    {
        _devices = devices;
        _scale = scale;
        _light = Theme.IsLightTaskbar();
        BackColor = Theme.PopupBack(_light);

        var size = Measure();
        var screen = Screen.FromRectangle(anchor).WorkingArea;
        int x = Math.Clamp(anchor.Left + anchor.Width / 2 - size.Width / 2, screen.Left + 8, screen.Right - size.Width - 8);
        int y = anchor.Top - size.Height - (int)(10 * scale);
        Bounds = new Rectangle(x, y, size.Width, size.Height);
        if (!Visible) Show();
        Invalidate();
    }

    private float RowH => 40 * _scale;
    private float Pad => 14 * _scale;

    private Size Measure()
    {
        using var g = CreateGraphics();
        using var name = Theme.TextFont(13 * _scale);
        using var pct = Theme.TextFont(14 * _scale, FontStyle.Bold);
        float nameW = 0, pctW = 0;
        foreach (var d in _devices)
        {
            nameW = Math.Max(nameW, g.MeasureString(d.Name, name).Width);
            pctW = Math.Max(pctW, g.MeasureString(Theme.PercentText(d, true), pct).Width);
        }
        float w = Pad + 20 * _scale + 12 * _scale + Math.Max(nameW, 140 * _scale) + 16 * _scale + Math.Max(pctW, 44 * _scale) + 18 * _scale + Pad;
        float h = Pad * 2 + Math.Max(1, _devices.Count) * RowH;
        return new Size((int)w, (int)h);
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
            bool low = d.Percent is int p && p <= 20 && !d.IsCharging;
            using var accent = new SolidBrush(d.IsCharging ? Theme.Charging(_light) : low ? Theme.Low(_light) : Theme.Foreground(_light));

            g.DrawString(Theme.Glyph(d.Kind), icon, fg, Pad, y + (RowH - 18 * _scale) / 2f - 2 * _scale);
            float tx = Pad + 32 * _scale;
            g.DrawString(d.Name, name, fg, tx, y + 3 * _scale);
            g.DrawString(Theme.StateText(d), sub, fg2, tx, y + 21 * _scale);

            var bg = Theme.BatteryGlyph(d.Percent, d.IsCharging);
            g.DrawString(bg, batt, accent, right - 18 * _scale, y + (RowH - 16 * _scale) / 2f);
            var pt = Theme.PercentText(d, true);
            var ps = g.MeasureString(pt, pct);
            g.DrawString(pt, pct, d.Stale ? fg2 : fg, right - 22 * _scale - ps.Width, y + (RowH - ps.Height) / 2f);
            y += RowH;
        }
    }
}
