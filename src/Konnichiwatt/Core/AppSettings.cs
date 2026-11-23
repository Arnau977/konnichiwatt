using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Konnichiwatt.Core;

[JsonConverter(typeof(JsonStringEnumConverter<BatteryDisplay>))]
public enum BatteryDisplay { Percent, Icon, IconAndPercent }

public sealed class AppSettings
{
    public BatteryDisplay Display { get; set; } = BatteryDisplay.Percent;
    /// <summary>Semibold text + stroked icons: the thin Fluent glyphs look faint at 100 % scaling (e.g. 1440p).</summary>
    public bool BoldStyle { get; set; } = true;
    public int PollSeconds { get; set; } = 30;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool LowBatteryNotifications { get; set; } = true;
    public bool ShowTaskbarWidget { get; set; } = true;
    public List<string> HiddenDevices { get; set; } = new();
    public bool ChargeCompleteNotifications { get; set; } = true;
    public bool HideWhileCharging { get; set; } = false;
    /// <summary>Also show the widget on the taskbars of secondary monitors.</summary>
    public bool ShowOnAllTaskbars { get; set; } = true;
    /// <summary>User overrides per device id (custom name / icon).</summary>
    public Dictionary<string, DeviceCustomization> Customizations { get; set; } = new();
    /// <summary>Display order chosen by the user (device ids); unknown devices go after these.</summary>
    public List<string> DeviceOrder { get; set; } = new();

    public DeviceBattery Customize(DeviceBattery d) =>
        Customizations.TryGetValue(d.Id, out var c)
            ? d with { Name = string.IsNullOrWhiteSpace(c.Name) ? d.Name : c.Name!, Kind = c.Kind ?? d.Kind }
            : d;

    public string DisplayName(string id, string fallback) =>
        Customizations.TryGetValue(id, out var c) && !string.IsNullOrWhiteSpace(c.Name) ? c.Name! : fallback;

    public DeviceCustomization CustomizationFor(string id)
    {
        if (!Customizations.TryGetValue(id, out var c)) Customizations[id] = c = new DeviceCustomization();
        return c;
    }

    /// <summary>Removes empty override entries so settings.json stays tidy.</summary>
    public void PruneCustomizations()
    {
        foreach (var id in Customizations.Where(kv => string.IsNullOrWhiteSpace(kv.Value.Name) && kv.Value.Kind == null)
                                         .Select(kv => kv.Key).ToList())
            Customizations.Remove(id);
    }

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Konnichiwatt", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Names this app shipped under before, newest first. Used to carry settings and autostart over.</summary>
    internal static readonly string[] PreviousNames = { "Denchi", "WirelessTray" };

    /// <summary>One-time move of the settings written under a previous app name; old data folders are removed.</summary>
    public static void MigrateFromOldName()
    {
        foreach (var name in PreviousNames)
        {
            try
            {
                var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);
                var old = Path.Combine(roaming, "settings.json");
                if (File.Exists(old) && !File.Exists(FilePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.Move(old, FilePath);
                    Log.Write($"Settings migrated from {name}");
                }
                if (Directory.Exists(roaming)) Directory.Delete(roaming, recursive: true);
                var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
                if (Directory.Exists(local)) Directory.Delete(local, recursive: true); // old logs only
            }
            catch (Exception e) { Log.Error($"Migrate settings from {name}", e); }
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) { Log.Error("Load settings", e); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) { Log.Error("Save settings", e); }
    }
}

public sealed class DeviceCustomization
{
    public string? Name { get; set; }
    public DeviceKind? Kind { get; set; }
}

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Konnichiwatt";

    /// <summary>If a previous version was set to start with Windows, keep that choice under the new name.</summary>
    public static void MigrateFromOldName()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            bool wasEnabled = false;
            foreach (var old in AppSettings.PreviousNames)
            {
                if (key.GetValue(old) == null) continue;
                key.DeleteValue(old, false);
                wasEnabled = true;
                Log.Write($"Startup entry migrated from {old}");
            }
            if (wasEnabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception e) { Log.Error("Migrate startup", e); }
    }

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, false);
        }
    }
}
