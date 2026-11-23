using System.Text.Json;

namespace Konnichiwatt.Core;

/// <summary>
/// Last known reading of every device, persisted to %LOCALAPPDATA%\Konnichiwatt\last-readings.json so that
/// devices which only answer while awake (Logitech mice) can be shown — dimmed — right after the app starts.
/// </summary>
public sealed class ReadingStore
{
    private static readonly string FilePath = Path.Combine(Log.Directory, "last-readings.json");
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Dictionary<string, DeviceBattery> _readings = new();
    private DateTime _savedAt = DateTime.MinValue;
    private bool _dirty;

    public IReadOnlyCollection<DeviceBattery> All => _readings.Values;

    public static ReadingStore Load()
    {
        var store = new ReadingStore();
        try
        {
            if (File.Exists(FilePath))
                foreach (var d in JsonSerializer.Deserialize<List<DeviceBattery>>(File.ReadAllText(FilePath)) ?? new())
                    if (d.LastSeen is DateTime seen && DateTime.Now - seen < MaxAge)
                        store._readings[d.Id] = d with { Stale = false };
        }
        catch (Exception e) { Log.Error("Load last readings", e); }
        return store;
    }

    /// <summary>Records fresh (non-stale) readings; writes to disk when something changed, at most every few minutes.</summary>
    public void Update(IEnumerable<DeviceBattery> devices)
    {
        foreach (var d in devices.Where(d => !d.Stale && d.LastSeen != null))
        {
            if (!_readings.TryGetValue(d.Id, out var old) || old.Percent != d.Percent || old.State != d.State || old.Name != d.Name)
                _dirty = true;
            _readings[d.Id] = d;
        }
        if (_dirty && DateTime.Now - _savedAt > TimeSpan.FromMinutes(2)) Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            var keep = _readings.Values.Where(d => DateTime.Now - (d.LastSeen ?? DateTime.MinValue) < MaxAge).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(keep, Json));
            _savedAt = DateTime.Now;
            _dirty = false;
        }
        catch (Exception e) { Log.Error("Save last readings", e); }
    }
}
