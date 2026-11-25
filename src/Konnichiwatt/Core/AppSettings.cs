using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Konnichiwatt.Core;

[JsonConverter(typeof(JsonStringEnumConverter<BatteryDisplay>))]
public enum BatteryDisplay { Percent, Icon, IconAndPercent }

public sealed class AppSettings
{
    /// <summary>Install new versions automatically (otherwise just notify).</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Receive test versions (pre-releases) before everyone else.</summary>
    public bool BetaUpdates { get; set; }
    /// <summary>A version that failed to start and was rolled back; it's not offered again.</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>Last version whose "what's new" was shown, so it's shown once per update.</summary>
    public string? NotesSeenVersion { get; set; }
    /// <summary>"auto" (follow Windows), "es" or "en".</summary>
    public string Language { get; set; } = "auto";
    public BatteryDisplay Display { get; set; } = BatteryDisplay.Percent;
    /// <summary>Semibold text + stroked icons: the thin Fluent glyphs look faint at 100 % scaling (e.g. 1440p).</summary>
    public bool BoldStyle { get; set; } = true;
    public int PollSeconds { get; set; } = 30;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool LowBatteryNotifications { get; set; } = true;
    public bool ShowTaskbarWidget { get; set; } = true;
    public List<string> HiddenDevices { get; set; } = new();
    public bool ChargeCompleteNotifications { get; set; } = true;
    /// <summary>Second, last-minute alert (off unless asked for, like the ones below).</summary>
    public bool CriticalNotifications { get; set; }
    public int CriticalThreshold { get; set; } = 5;
    /// <summary>Alert when the time left is shorter than a usual day of use.</summary>
    public bool PredictiveNotifications { get; set; }
    /// <summary>Alert when a charge reaches <see cref="ChargeLimit"/> %, to unplug it and look after the battery.</summary>
    public bool ChargeLimitNotifications { get; set; }
    public int ChargeLimit { get; set; } = 80;
    /// <summary>Size of the settings window in 96-dpi pixels; null = default.</summary>
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    /// <summary>Discreet mode: the taskbar only shows devices at or below <see cref="DiscreetBelow"/> %.</summary>
    public bool DiscreetMode { get; set; } = false;
    public int DiscreetBelow { get; set; } = 30;

    /// <summary>Low-battery threshold for a device: its own if set, otherwise the general one.</summary>
    public int ThresholdFor(string id) =>
        Customizations.TryGetValue(id, out var c) && c.LowThreshold is int t ? t : LowBatteryThreshold;

    public bool IsLow(DeviceBattery d) => d.Percent is int p && p <= ThresholdFor(d.Id) && !d.IsCharging;

    /// <summary>Whether the taskbar widget shows this (already visible) device, considering discreet mode.</summary>
    public bool ShowsOnTaskbar(DeviceBattery d) => !DiscreetMode || d.Percent is int p && p <= DiscreetBelow;
    public bool HideWhileCharging { get; set; } = false;
    /// <summary>Legacy (v0.2) switch, only used while <see cref="TaskbarMonitors"/> has never been set.</summary>
    public bool ShowOnAllTaskbars { get; set; } = true;
    /// <summary>
    /// Monitors (stable device-path ids) whose taskbar shows the widget. Null = never chosen: all monitors
    /// (or only the main one if the legacy switch was off). Ids of unplugged monitors are kept for when they return.
    /// </summary>
    public List<string>? TaskbarMonitors { get; set; }

    public bool ShowsOnMonitor(string? monitorId, bool primary) =>
        TaskbarMonitors == null
            ? ShowOnAllTaskbars || primary
            : monitorId != null && TaskbarMonitors.Contains(monitorId);
    /// <summary>User overrides per device id (custom name / icon).</summary>
    public Dictionary<string, DeviceCustomization> Customizations { get; set; } = new();
    /// <summary>Display order chosen by the user (device ids); unknown devices go after these.</summary>
    public List<string> DeviceOrder { get; set; } = new();

    public DeviceBattery Customize(DeviceBattery d) =>
        Customizations.TryGetValue(d.Id, out var c)
            ? d with { Name = string.IsNullOrWhiteSpace(c.Name) ? d.Name : c.Name!, Kind = c.Kind ?? d.Kind }
            : d;

    /// <summary>Devices with the user's names/icons applied, in the user's order (unknown ones keep detection order).</summary>
    public List<DeviceBattery> Arrange(IEnumerable<DeviceBattery> all) =>
        all.Select(Customize)
            .Select((d, i) => (d, i))
            .OrderBy(t => DeviceOrder.IndexOf(t.d.Id) is var k && k >= 0 ? k : int.MaxValue)
            .ThenBy(t => t.i)
            .Select(t => t.d)
            .ToList();

    public bool IsShown(DeviceBattery d) =>
        !HiddenDevices.Contains(d.Id) && !(HideWhileCharging && d.IsCharging);

    /// <summary>Swaps the device at <paramref name="index"/> of <paramref name="currentIds"/> with its neighbour.</summary>
    public void MoveDevice(IReadOnlyList<string> currentIds, int index, int delta)
    {
        var ids = currentIds.ToList();
        int j = index + delta;
        if (index < 0 || index >= ids.Count || j < 0 || j >= ids.Count) return;
        (ids[index], ids[j]) = (ids[j], ids[index]);
        // Keep the remembered position of devices that are not connected right now, after the current ones.
        DeviceOrder = ids.Concat(DeviceOrder.Where(id => !ids.Contains(id))).ToList();
    }

    /// <summary>Sets the order of the given (connected) devices, keeping remembered positions of the others after them.</summary>
    public void SetOrder(IReadOnlyList<string> ids) =>
        DeviceOrder = ids.Concat(DeviceOrder.Where(id => !ids.Contains(id))).ToList();

    public void ToggleHidden(string id)
    {
        if (!HiddenDevices.Remove(id)) HiddenDevices.Add(id);
    }

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
        foreach (var id in Customizations.Where(kv => string.IsNullOrWhiteSpace(kv.Value.Name) && kv.Value.Kind == null && kv.Value.LowThreshold == null)
                                         .Select(kv => kv.Key).ToList())
            Customizations.Remove(id);
    }

    private static readonly string FilePath =
        Path.Combine(AppPaths.SettingsDirectory, "settings.json");

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
        catch (Exception e) { Log.Error("Load settings", e); SafeFile.KeepCorrupt(FilePath); }
        // First run: this version's changes aren't news to someone who has never used an earlier one.
        return new AppSettings { NotesSeenVersion = Updater.CurrentVersion.ToString(3) };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) { Log.Error("Save settings", e); }
    }
}

public sealed class DeviceCustomization
{
    public string? Name { get; set; }
    public DeviceKind? Kind { get; set; }
    /// <summary>Own low-battery threshold (%), overriding the general one; null = use the general one.</summary>
    public int? LowThreshold { get; set; }
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
        set => Set(value, Environment.ProcessPath!);
    }

    /// <summary>Turns "start with Windows" on for <paramref name="exe"/>, or off.</summary>
    public static void Set(bool enabled, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{exe}\"");
        else key.DeleteValue(ValueName, false);
    }

    /// <summary>The program "start with Windows" currently launches, if any.</summary>
    public static string? RegisteredExe
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s ? s.Trim().Trim('"') : null;
        }
    }
}
