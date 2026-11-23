using System.Text.Json;

namespace Konnichiwatt.Core;

/// <summary>One battery reading in the history.</summary>
public sealed record BatterySample(DateTime Time, int Percent, bool Charging);

/// <summary>
/// Battery level over time per device (last 48 h), persisted to history.json. Feeds the flyout sparkline and the
/// "time left" / "full in" estimates.
/// </summary>
public sealed class BatteryHistory
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(48);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    private static readonly string FilePath = Path.Combine(Log.Directory, "history.json");

    private readonly Dictionary<string, List<BatterySample>> _samples = new();
    private DateTime _savedAt = DateTime.Now;
    private bool _dirty;

    public IReadOnlyList<BatterySample> For(string id) =>
        _samples.TryGetValue(id, out var l) ? l : Array.Empty<BatterySample>();

    public static BatteryHistory Load()
    {
        var h = new BatteryHistory();
        try
        {
            if (File.Exists(FilePath) &&
                JsonSerializer.Deserialize<Dictionary<string, List<BatterySample>>>(File.ReadAllText(FilePath)) is { } data)
                foreach (var (id, list) in data)
                    h._samples[id] = list.Where(s => DateTime.Now - s.Time < Keep).OrderBy(s => s.Time).ToList();
        }
        catch (Exception e) { Log.Error("Load history", e); }
        return h;
    }

    /// <summary>Adds a sample when the level/charging state changed, or every 10 min while it stays the same.</summary>
    public void Record(IEnumerable<DeviceBattery> devices, DateTime now)
    {
        foreach (var d in devices)
        {
            if (d.Stale || d.Percent is not int p) continue;
            if (!_samples.TryGetValue(d.Id, out var list)) _samples[d.Id] = list = new List<BatterySample>();
            var last = list.Count > 0 ? list[^1] : null;
            if (last != null && last.Percent == p && last.Charging == d.IsCharging && now - last.Time < MinInterval) continue;
            list.Add(new BatterySample(now, p, d.IsCharging));
            list.RemoveAll(s => now - s.Time > Keep);
            _dirty = true;
        }
        if (_dirty && now - _savedAt > TimeSpan.FromMinutes(5)) Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_samples));
            _savedAt = DateTime.Now;
            _dirty = false;
        }
        catch (Exception e) { Log.Error("Save history", e); }
    }

    /// <summary>
    /// Time until empty (discharging) or full (charging), from a least-squares fit over the current run — the
    /// trailing samples with the same charging state, without gaps longer than 2 h (e.g. PC switched off).
    /// Null when there isn't enough data: under 30 min of run, or less than 3 points of change.
    /// </summary>
    public static TimeSpan? Estimate(IReadOnlyList<BatterySample> samples, out bool toFull)
    {
        toFull = false;
        if (samples.Count < 2) return null;
        var last = samples[^1];
        toFull = last.Charging;
        if (toFull ? last.Percent >= 100 : last.Percent <= 0) return null;

        int start = samples.Count - 1;
        while (start > 0 && samples[start - 1].Charging == last.Charging &&
               samples[start].Time - samples[start - 1].Time < TimeSpan.FromHours(2))
            start--;
        var run = samples.Skip(start).ToList();
        if (run.Count < 2 || last.Time - run[0].Time < TimeSpan.FromMinutes(30)) return null;
        if (Math.Abs(run[0].Percent - last.Percent) < 3) return null;

        // Least squares: percent = a + b·hours.
        double t0 = run[0].Time.Ticks;
        var xs = run.Select(s => (s.Time.Ticks - t0) / TimeSpan.TicksPerHour).ToArray();
        var ys = run.Select(s => (double)s.Percent).ToArray();
        double mx = xs.Average(), my = ys.Average();
        double sxx = xs.Sum(x => (x - mx) * (x - mx));
        if (sxx <= 0) return null;
        double slope = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / sxx; // % per hour

        double hours = toFull
            ? (slope > 0.1 ? (100 - last.Percent) / slope : double.NaN)
            : (slope < -0.1 ? last.Percent / -slope : double.NaN);
        if (double.IsNaN(hours) || hours < 0 || hours > 24 * 14) return null;
        return TimeSpan.FromHours(hours);
    }

    /// <summary>"≈ 6 h restantes" / "lleno en ≈ 40 min", or null.</summary>
    public string? EstimateText(string id)
    {
        var t = Estimate(For(id), out bool toFull);
        if (t is not TimeSpan span) return null;
        string amount = span.TotalMinutes < 60 ? $"{Math.Max(1, (int)Math.Round(span.TotalMinutes / 5) * 5)} min"
                      : span.TotalHours < 48 ? $"{Math.Round(span.TotalHours)} h"
                      : $"{Math.Round(span.TotalDays)} días";
        return toFull ? $"lleno en ≈ {amount}" : $"≈ {amount} restantes";
    }
}
