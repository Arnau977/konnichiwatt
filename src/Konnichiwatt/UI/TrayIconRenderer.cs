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

    /// <summary>Application icon: the same bolt, amber, on a dark rounded tile (readable on light and dark backgrounds).</summary>
    public static Bitmap RenderAppIcon(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float inset = size <= 20 ? 0 : size * 0.04f;
        var tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        using (var tilePath = Rounded(tile, tile.Width * 0.22f))
        using (var tileBrush = new LinearGradientBrush(tile, Color.FromArgb(0x34, 0x34, 0x3A), Color.FromArgb(0x18, 0x18, 0x1C), 90f))
            g.FillPath(tileBrush, tilePath);

        float pad = tile.Width * 0.17f, span = tile.Width - 2 * pad;
        using var path = new GraphicsPath();
        path.AddPolygon(Bolt.Select(p => new PointF(tile.Left + pad + p.X * span, tile.Top + pad + p.Y * span)).ToArray());
        var b = path.GetBounds();
        using (var bolt = new LinearGradientBrush(b, Color.FromArgb(0xFF, 0xDD, 0x55), Color.FromArgb(0xFF, 0xA8, 0x00), 90f))
            g.FillPath(bolt, path);
        return bmp;
    }

    /// <summary>
    /// Writes a multi-size .ico. Sizes below 256 use classic 32-bit DIB entries (some Windows components, like
    /// the .NET Framework Icon class, misread PNG entries); 256 px is PNG, as Windows expects.
    /// </summary>
    public static void SaveAppIcon(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = sizes.Select(s =>
        {
            using var bmp = RenderAppIcon(s);
            if (s < 256) return Dib(bmp);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);      // ICONDIR
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)                                    // ICONDIRENTRY
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }

    /// <summary>Icon-resource DIB: BITMAPINFOHEADER (double height), bottom-up BGRA pixels, then an all-zero AND mask.</summary>
    private static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = ((w + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
            }
        bw.Write(new byte[maskStride * h]);
        return ms.ToArray();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
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
}
