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
