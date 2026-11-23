using System.Drawing.Drawing2D;

namespace Konnichiwatt.UI;

/// <summary>
/// Konnichiwatt's tray icon: a lightning bolt that fills from the bottom like a charge gauge, according to the
/// device with the least battery. Filled white/black (taskbar theme), red when low, green while charging;
/// the empty part stays faintly visible, and with no devices only the outline is drawn.
/// </summary>
internal static class TrayIconRenderer
{
    // Bolt outline in a unit square (classic zig-zag: flat top, sharp bottom tip). Deliberately chunky so the
    // charge level is still readable at 16 px.
    private static readonly PointF[] Bolt =
    {
        new(0.50f, 0.00f), new(0.08f, 0.60f), new(0.43f, 0.60f), new(0.32f, 1.00f),
        new(0.92f, 0.36f), new(0.57f, 0.36f), new(0.78f, 0.00f),
    };

    /// <param name="percent">Level 0-100, or null when there is nothing to show (outline only).</param>
    public static Bitmap Render(int size, int? percent, bool charging, bool low, bool lightTaskbar)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float pad = size * 0.03f, span = size - 2 * pad;
        using var path = new GraphicsPath();
        path.AddPolygon(Bolt.Select(p => new PointF(pad + p.X * span, pad + p.Y * span)).ToArray());
        var bounds = path.GetBounds();

        var fg = Theme.Foreground(lightTaskbar);
        var fill = charging ? Theme.Charging(lightTaskbar) : low ? Theme.Low(lightTaskbar) : fg;

        if (percent is int p)
        {
            // Empty part: faint, so the bolt keeps its shape even at 0 %.
            using (var faint = new SolidBrush(Color.FromArgb(lightTaskbar ? 0x30 : 0x40, fg)))
                g.FillPath(faint, path);

            // Charge: clip to the bolt and fill a rectangle rising from the bottom.
            float level = Math.Clamp(p, 0, 100) / 100f;
            if (level > 0)
            {
                // Keep a sliver visible for 1-5 % so "almost empty" doesn't look like "unknown".
                float h = Math.Max(bounds.Height * level, bounds.Height * 0.06f);
                var state = g.Save();
                g.SetClip(path);
                using var b = new SolidBrush(fill);
                g.FillRectangle(b, bounds.Left - 1, bounds.Bottom - h, bounds.Width + 2, h + 1);
                g.Restore(state);
            }
        }

        // Outline on top (in the fill colour when low/charging so the state reads at 16 px).
        using var pen = new Pen(percent == null ? Color.FromArgb(0xB0, fg) : fill, Math.Max(1f, size * 0.06f))
        {
            LineJoin = LineJoin.Round,
        };
        g.DrawPath(pen, path);
        return bmp;
    }
}
