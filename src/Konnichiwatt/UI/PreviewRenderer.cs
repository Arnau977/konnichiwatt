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
