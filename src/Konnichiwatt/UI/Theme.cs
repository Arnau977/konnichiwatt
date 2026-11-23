using System.Drawing.Text;
using Microsoft.Win32;
using Konnichiwatt.Core;

namespace Konnichiwatt.UI;

/// <summary>Colors, fonts and Segoe Fluent glyphs that match the Windows 11 taskbar.</summary>
internal static class Theme
{
    public static bool IsLightTaskbar()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>App theme (Settings › Personalización › Colores › modo de las aplicaciones).</summary>
    public static bool IsLightApps()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// Windows accent colour as used by Windows 11 controls: the lighter "Light 2" shade on dark theme,
    /// the darker "Dark 1" shade on light theme (from Explorer\Accent\AccentPalette).
    /// </summary>
    public static Color Accent(bool light)
    {
        var c = light ? Color.FromArgb(0x00, 0x5F, 0xB8) : Color.FromArgb(0x4C, 0xC2, 0xFF);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
            {
                int i = (light ? 4 : 1) * 4;
                c = Color.FromArgb(p[i], p[i + 1], p[i + 2]);
            }
        }
        catch { }
        // Very dark accents (e.g. navy) vanish on the dark surfaces: lighten them, keeping the hue.
        if (!light)
            for (int step = 0; step < 10 && Luminance(c) < 0.28; step++)
                c = Blend(c, Color.White, 0.18);
        else
            for (int step = 0; step < 10 && Luminance(c) > 0.45; step++)
                c = Blend(c, Color.Black, 0.18);
        return c;
    }

    /// <summary>Text colour readable on top of <paramref name="background"/>.</summary>
    public static Color OnColor(Color background) => Luminance(background) > 0.45 ? Color.Black : Color.White;

    private static double Luminance(Color c)
    {
        static double Lin(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static Color Foreground(bool light) => light ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
    public static Color Secondary(bool light) => light ? Color.FromArgb(0x60, 0x60, 0x60) : Color.FromArgb(0xC5, 0xC5, 0xC5);
    public static Color Low(bool light) => light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0xA4);
    public static Color Charging(bool light) => light ? Color.FromArgb(0x0F, 0x7B, 0x0F) : Color.FromArgb(0x6C, 0xCB, 0x5F);
    public static Color Hover(bool light) => light ? Color.FromArgb(0x10, 0, 0, 0) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
    public static Color PopupBack(bool light) => light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x2B, 0x2B, 0x2B);

    private static readonly Lazy<string> IconFontName = new(() =>
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    });

    private static readonly Lazy<string> TextFontName = new(() =>
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    });

    private static readonly Lazy<string> SemiboldFontName = new(() =>
    {
        using var fonts = new InstalledFontCollection();
        foreach (var name in new[] { "Segoe UI Variable Text Semibold", "Segoe UI Variable Display Semibold", "Segoe UI Semibold" })
            if (fonts.Families.Any(f => f.Name == name)) return name;
        return "";
    });

    public static Font IconFont(float px) => new(IconFontName.Value, px, GraphicsUnit.Pixel);
    public static Font TextFont(float px, FontStyle style = FontStyle.Regular) => new(TextFontName.Value, px, style, GraphicsUnit.Pixel);

    /// <summary>Semibold if available (looks like the Windows 11 flyouts), otherwise GDI+ bold.</summary>
    public static Font TextFont(float px, bool heavy) =>
        !heavy ? TextFont(px)
        : SemiboldFontName.Value.Length > 0 ? new Font(SemiboldFontName.Value, px, FontStyle.Regular, GraphicsUnit.Pixel)
        : TextFont(px, FontStyle.Bold);

    public const string Bolt = "";

    public static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Headset => "",
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Controller => "",
        DeviceKind.Speaker => "",
        _ => "", // Bluetooth
    };

    /// <summary>Battery0..Battery10 / BatteryCharging0..10 glyphs.</summary>
    public static string BatteryGlyph(int? percent, bool charging)
    {
        if (percent == null) return charging ? "" : "";
        int step = Math.Clamp((int)Math.Round(percent.Value / 10.0), 0, 10);
        if (charging) return step == 10 ? "" : ((char)(0xE85A + step)).ToString();
        return step == 10 ? "" : ((char)(0xE850 + step)).ToString();
    }

    public static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Headset => "Auriculares",
        DeviceKind.Mouse => "Ratón",
        DeviceKind.Keyboard => "Teclado",
        DeviceKind.Controller => "Mando",
        DeviceKind.Speaker => "Altavoz",
        _ => "Dispositivo",
    };

    public static string StateText(DeviceBattery d)
    {
        if (d.Stale)
        {
            var mins = (int)(DateTime.Now - (d.LastSeen ?? DateTime.Now)).TotalMinutes;
            return mins < 1 ? "En reposo" : $"En reposo · dato de hace {mins} min";
        }
        return d.State switch
        {
            ChargeState.Charging => "Cargando",
            ChargeState.Full => "Cargado",
            ChargeState.Discharging => "Con batería",
            _ => "Conectado",
        };
    }

    public static string PercentText(DeviceBattery d, bool allowApprox) =>
        d.Percent is int p ? (allowApprox && d.Approximate ? $"≈{p}%" : $"{p}%") : (d.IsCharging ? "" : "--");
}
