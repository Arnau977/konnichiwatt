using Konnichiwatt.Core;

namespace Konnichiwatt.UI;

/// <summary>Renders every display style with sample devices onto a taskbar-coloured PNG (dev aid: <c>--preview out.png</c>).</summary>
internal static class PreviewRenderer
{
    /// <summary>Example devices (used by the preview sheet and by the settings preview when nothing is connected).</summary>
    public static List<DeviceBattery> Samples()
    {
        var now = DateTime.Now;
        return new List<DeviceBattery>
        {
            new("h", "Headset", DeviceKind.Headset, 28, ChargeState.Discharging, "x", LastSeen: now),
            new("m", "Mouse", DeviceKind.Mouse, 62, ChargeState.Discharging, "x", LastSeen: now),
            new("c", "Pad", DeviceKind.Controller, 100, ChargeState.Charging, "x", LastSeen: now),
            new("k", "Pad 2", DeviceKind.Controller, 12, ChargeState.Discharging, "x", LastSeen: now),
        };
    }

    /// <summary>Dev aid (<c>--tray-preview out.png</c>): the tray bolt in every state, at tray sizes, on both taskbars.</summary>
    public static void SaveTrayIcons(string path)
    {
        var states = new (string Label, int? Pct, bool Charging, bool Low)[]
        {
            ("sin datos", null, false, false), ("5 %", 5, false, true), ("28 %", 28, false, false),
            ("62 %", 62, false, false), ("100 %", 100, false, false), ("cargando 40 %", 40, true, false),
        };
        int[] sizes = { 16, 24, 32 };
        const int cell = 110, rowH = 60;
        using var sheet = new Bitmap(cell * states.Length + 70, rowH * sizes.Length * 2);
        using var g = Graphics.FromImage(sheet);
        using var label = new Font("Segoe UI", 9, GraphicsUnit.Pixel);
        int y = 0;
        foreach (var light in new[] { false, true })
        foreach (var size in sizes)
        {
            using (var back = new SolidBrush(light ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x1C, 0x1C, 0x1C)))
                g.FillRectangle(back, 0, y, sheet.Width, rowH);
            using var lb = new SolidBrush(Color.Gray);
            g.DrawString($"{size} px", label, lb, 6, y + rowH / 2 - 6);
            for (int i = 0; i < states.Length; i++)
            {
                var s = states[i];
                // Render at 2× and downscale like the tray does.
                using var big = TrayIconRenderer.Render(size * 2, s.Pct, s.Charging, s.Low, light);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(big, 70 + i * cell, y + (rowH - size) / 2 - 6, size, size);
                if (size == 32) g.DrawString(s.Label, label, lb, 70 + i * cell, y + rowH - 13);
            }
            y += rowH;
        }
        sheet.Save(path);
    }

    /// <summary>Dev aid (<c>--popup-preview out.png</c>): the hover flyout with a synthetic 12 h history.</summary>
    public static void SavePopup(string path)
    {
        var devices = Samples();
        var history = new BatteryHistory();
        var now = DateTime.Now;
        for (int m = 12 * 60; m >= 0; m -= 10)
        {
            var t = now.AddMinutes(-m);
            double h = (12 * 60 - m) / 60.0;           // hours elapsed
            history.Record(new[]
            {
                devices[0] with { Percent = (int)Math.Round(70 - h * 3.5) },                      // headset: slow drain
                devices[1] with { Percent = (int)Math.Round(h < 4 ? 100 - h * 2 : 92 - (h - 4) * 3.75) }, // mouse
                devices[2] with { Percent = (int)Math.Round(h < 9 ? 60 - h * 6 : 6 + (h - 9) * 31), State = h < 9 ? ChargeState.Discharging : ChargeState.Charging },
                devices[3] with { Percent = (int)Math.Round(55 - h * 3.6) },
            }.Select(d => d with { Percent = Math.Clamp(d.Percent ?? 0, 0, 100) }), t);
        }

        using var popup = new DetailsPopup { History = history, IsLow = d => d.Percent <= 20 && !d.IsCharging };
        popup.ShowNear(new Rectangle(-4000, -3000, 200, 48), new Rectangle(-4400, -3000, 1000, 48), devices, 1f);
        Application.DoEvents();
        using var bmp = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(bmp, new Rectangle(Point.Empty, popup.Size));
        bmp.Save(path);
    }

    public static void Save(string path)
    {
        var devices = Samples();

        const int h = 48;
        var rows = new List<(string Label, Bitmap Bmp, bool Light)>();
        foreach (var light in new[] { false, true })
        foreach (var bold in new[] { false, true })
        foreach (var mode in Enum.GetValues<BatteryDisplay>())
        {
            var s = new AppSettings { Display = mode, BoldStyle = bold };
            var w = new TaskbarWidget(s);
            rows.Add(($"{(light ? "claro" : "oscuro")} · {(bold ? "grueso" : "normal")} · {mode}", w.RenderPreview(devices, h, 1f, light), light));
        }

        // Vertical taskbar (ExplorerPatcher / StartAllBack), 56 px wide, dark, bold.
        const int vw = 56;
        var columns = Enum.GetValues<BatteryDisplay>()
            .Select(mode => (Label: mode.ToString(), Bmp: new TaskbarWidget(new AppSettings { Display = mode, BoldStyle = true })
                .RenderPreview(devices, h, 1f, light: false, verticalWidth: vw)))
            .ToList();
        int columnsH = columns.Max(c => c.Bmp.Height) + 24;

        int width = Math.Max(260 + rows.Max(r => r.Bmp.Width) + 20, 260 + columns.Count * (vw + 40));
        using var sheet = new Bitmap(width, rows.Count * h + columnsH);
        using (var g = Graphics.FromImage(sheet))
        using (var label = new Font("Segoe UI", 11, GraphicsUnit.Pixel))
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var (text, bmp, light) = rows[i];
                var back = light ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x1C, 0x1C, 0x1C);
                using var bb = new SolidBrush(back);
                g.FillRectangle(bb, 0, i * h, width, h);
                using var lb = new SolidBrush(light ? Color.DimGray : Color.Gray);
                g.DrawString(text, label, lb, 8, i * h + 16);
                g.DrawImage(bmp, 260, i * h);
                bmp.Dispose();
            }

            int top = rows.Count * h;
            using var dark = new SolidBrush(Color.FromArgb(0x1C, 0x1C, 0x1C));
            using var strip = new SolidBrush(Color.FromArgb(0x2A, 0x2A, 0x2A));
            using var gray = new SolidBrush(Color.Gray);
            g.FillRectangle(dark, 0, top, width, columnsH);
            g.DrawString("barra vertical · grueso", label, gray, 8, top + 16);
            for (int i = 0; i < columns.Count; i++)
            {
                int x = 260 + i * (vw + 40);
                g.FillRectangle(strip, x, top + 4, vw, columnsH - 8);
                g.DrawImage(columns[i].Bmp, x, top + 4);
                g.DrawString(columns[i].Label, label, gray, x, top + columnsH - 18);
                columns[i].Bmp.Dispose();
            }
        }
        sheet.Save(path);
    }
}
