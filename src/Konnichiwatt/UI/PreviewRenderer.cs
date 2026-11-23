using Konnichiwatt.Core;

namespace Konnichiwatt.UI;

/// <summary>Renders every display style with sample devices onto a taskbar-coloured PNG (dev aid: <c>--preview out.png</c>).</summary>
internal static class PreviewRenderer
{
    public static void Save(string path)
    {
        var now = DateTime.Now;
        var devices = new List<DeviceBattery>
        {
            new("h", "Headset", DeviceKind.Headset, 28, ChargeState.Discharging, "x", LastSeen: now),
            new("m", "Mouse", DeviceKind.Mouse, 62, ChargeState.Discharging, "x", LastSeen: now),
            new("c", "Pad", DeviceKind.Controller, 100, ChargeState.Charging, "x", LastSeen: now),
            new("k", "Pad 2", DeviceKind.Controller, 12, ChargeState.Discharging, "x", LastSeen: now),
        };

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

        int width = 260 + rows.Max(r => r.Bmp.Width) + 20;
        using var sheet = new Bitmap(width, rows.Count * h);
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
        }
        sheet.Save(path);
    }
}
