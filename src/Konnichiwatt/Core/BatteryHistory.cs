using System.Text.Json;

namespace Konnichiwatt.Core;

/// <summary>One battery reading in the history.</summary>
public sealed record BatterySample(DateTime Time, int Percent, bool Charging);

/// <summary>
/// Battery level over time per device (last 30 days, full detail for 48 h), persisted to history.json. Feeds the
/// flyout sparkline, the History page and the
/// "time left" / "full in" estimates.
/// </summary>
public sealed class BatteryHistory
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    /// <summary>Newer samples are kept as recorded; older ones are thinned to one per <see cref="ThinInterval"/>.</summary>
    public static readonly TimeSpan Detailed = TimeSpan.FromHours(48);
    private static readonly TimeSpan ThinInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    private static readonly string FilePath = Path.Combine(Log.Directory, "history.json");

    private readonly Dictionary<string, List<BatterySample>> _samples = new();
    private DateTime _savedAt = DateTime.Now;
    private bool _dirty;

    /// <summary>Long-term daily summary, fed from these samples.</summary>
    public DailyLog Daily { get; private set; } = new();

    /// <summary>Sets a device's whole history (demo data and tests).</summary>
    internal void Replace(string id, List<BatterySample> samples) => _samples[id] = samples;

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
                    h._samples[id] = Thin(RemoveSpikes(list.OrderBy(s => s.Time).ToList()), DateTime.Now);
        }
        catch (Exception e) { Log.Error("Load history", e); SafeFile.KeepCorrupt(FilePath); }
        h.Daily = DailyLog.Load();
        h.UpdateDaily(DateTime.Now);
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

    /// <summary>Drops samples older than <see cref="Keep"/> and thins those older than <see cref="Detailed"/>.</summary>
    internal static List<BatterySample> Thin(List<BatterySample> list, DateTime now)
    {
        var result = new List<BatterySample>(list.Count);
        BatterySample? kept = null;
        foreach (var s in list)
        {
            if (now - s.Time > Keep) continue;
            if (now - s.Time > Detailed && kept != null && s.Charging == kept.Charging && s.Time - kept.Time < ThinInterval)
                continue;
            result.Add(s);
            kept = s;
        }
        return result;
    }

    /// <summary>
    /// Drops single wrong readings recorded before <see cref="ReadingFilter"/> existed: a jump that is undone within
    /// five minutes, none of it while charging.
    /// </summary>
    internal static List<BatterySample> RemoveSpikes(List<BatterySample> list)
    {
        var result = new List<BatterySample>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (result.Count > 0 && i + 1 < list.Count)
            {
                var prev = result[^1];
                var next = list[i + 1];
                int up = s.Percent - prev.Percent, back = s.Percent - next.Percent;
                if (!prev.Charging && !s.Charging && !next.Charging &&
                    Math.Abs(up) > ReadingFilter.MaxJump && Math.Abs(back) > ReadingFilter.MaxJump && up > 0 == back > 0 &&
                    Math.Abs(next.Percent - prev.Percent) <= ReadingFilter.MaxJump &&
                    next.Time - s.Time <= TimeSpan.FromMinutes(5))
                    continue;
            }
            result.Add(s);
        }
        return result;
    }

    public void Save()
    {
        try
        {
            var now = DateTime.Now;
            UpdateDaily(now);
            Daily.Save();
            foreach (var id in _samples.Keys.ToList()) _samples[id] = Thin(_samples[id], now);
            Directory.CreateDirectory(Log.Directory);
            SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(_samples));
            _savedAt = DateTime.Now;
            _dirty = false;
        }
        catch (Exception e) { Log.Error("Save history", e); }
    }

    /// <summary>
    /// Time until empty (discharging) or full (charging), from a least-squares fit over the current run — the
    /// trailing samples with the same charging state, without gaps longer than 2 h (e.g. PC switched off).
    /// A run under 30 min or with less than 3 points of change isn't enough by itself; then, while discharging, the
    /// battery life learned from earlier discharges (<paramref name="perCharge"/>) is used. A short run is blended with
    /// it, so the estimate moves smoothly from what's usual to what's happening now. Null without either.
    /// </summary>
    public static TimeSpan? Estimate(IReadOnlyList<BatterySample> samples, out bool toFull, TimeSpan? perCharge = null)
    {
        toFull = false;
        if (samples.Count == 0) return null;
        var last = samples[^1];
        toFull = last.Charging;
        if (toFull ? last.Percent >= 100 : last.Percent <= 0) return null;

        double? run = RunRate(samples, out int change);
        if (toFull) return run is double up ? Within((100 - last.Percent) / up, 14) : null;

        double? learned = perCharge is TimeSpan life && life > TimeSpan.Zero ? 100 / life.TotalHours : null;
        double weight = Math.Min(1, change / 15.0);
        if (run is double r && learned is double l) return Within(last.Percent / (weight * r + (1 - weight) * l), 90);
        if (run is double only) return Within(last.Percent / only, 14);
        return learned is double usual ? Within(last.Percent / usual, 90) : null;

        static TimeSpan? Within(double hours, int days) =>
            hours >= 0 && hours <= 24 * days ? TimeSpan.FromHours(hours) : null;
    }

    /// <summary>Speed of the current run in % per hour (positive, draining or charging), or null if it's too short to tell.</summary>
    /// <param name="change">Points gained or lost over the run.</param>
    private static double? RunRate(IReadOnlyList<BatterySample> samples, out int change)
    {
        change = 0;
        var last = samples[^1];
        int start = samples.Count - 1;
        while (start > 0 && samples[start - 1].Charging == last.Charging &&
               samples[start].Time - samples[start - 1].Time < TimeSpan.FromHours(2))
            start--;
        var run = samples.Skip(start).ToList();
        if (run.Count < 2 || last.Time - run[0].Time < TimeSpan.FromMinutes(30)) return null;
        change = Math.Abs(run[0].Percent - last.Percent);
        if (change < 3) return null;

        // Least squares: percent = a + b·hours.
        double t0 = run[0].Time.Ticks;
        var xs = run.Select(s => (s.Time.Ticks - t0) / TimeSpan.TicksPerHour).ToArray();
        var ys = run.Select(s => (double)s.Percent).ToArray();
        double mx = xs.Average(), my = ys.Average();
        double sxx = xs.Sum(x => (x - mx) * (x - mx));
        if (sxx <= 0) return null;
        double slope = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / sxx; // % per hour

        double speed = last.Charging ? slope : -slope;
        return speed > 0.1 ? speed : null;
    }

    /// <summary>Summary of a period for the History page.</summary>
    /// <param name="PerCharge">Battery life of a full 0→100 % charge at the observed drain rate.</param>
    /// <param name="Discharges">Discharges that went into <paramref name="PerCharge"/> (those of at least 3 %).</param>
    /// <param name="Charges">Times it started charging.</param>
    /// <param name="LastCharge">End of the most recent charge.</param>
    public sealed record Stats(TimeSpan? PerCharge, int Discharges, int Charges, DateTime? LastCharge);

    /// <summary>
    /// Battery life per charge from every discharge since <paramref name="from"/>: total % used / total hours, only over
    /// stretches without gaps over 2 h (PC off), so time when the level wasn't seen doesn't count. The % used is net:
    /// approximate levels wobble up and down, and adding only the drops would count that as drain. Needs 10 % in total.
    /// </summary>
    public static Stats Analyze(IReadOnlyList<BatterySample> samples, DateTime from)
    {
        var s = samples.Where(x => x.Time >= from).ToList();
        double drop = 0, hours = 0;
        int discharges = 0, charges = 0;
        DateTime? lastCharge = null;
        double runDrop = 0, runHours = 0;

        void EndRun()
        {
            drop += runDrop;
            hours += runHours;
            if (runDrop >= 3) discharges++;
            runDrop = runHours = 0;
        }

        for (int i = 0; i < s.Count; i++)
        {
            var cur = s[i];
            if (cur.Charging) lastCharge = cur.Time;
            if (i == 0) continue;
            var prev = s[i - 1];
            if (cur.Charging && !prev.Charging) { charges++; EndRun(); continue; }
            if (cur.Charging || prev.Charging) continue;
            var gap = cur.Time - prev.Time;
            if (gap > TimeSpan.FromHours(2)) continue;           // not observed: neither time nor drop counts
            if (cur.Percent > prev.Percent + 5) { EndRun(); continue; } // charged while we weren't looking
            runDrop += prev.Percent - cur.Percent;
            runHours += gap.TotalHours;
        }
        EndRun();

        TimeSpan? perCharge = drop >= 10 && hours > 0 ? TimeSpan.FromHours(100 / (drop / hours)) : null;
        return new Stats(perCharge, perCharge == null ? 0 : Math.Max(1, discharges), charges, lastCharge);
    }

    internal void UpdateDaily(DateTime now)
    {
        foreach (var (id, list) in _samples) Daily.Update(id, list, DateOnly.FromDateTime(now));
    }

    /// <summary>Time left on battery, or null if it's charging or there isn't enough data.</summary>
    public TimeSpan? TimeLeft(string id)
    {
        var samples = For(id);
        var t = Estimate(samples, out bool toFull, Analyze(samples, DateTime.MinValue).PerCharge);
        return toFull ? null : t;
    }

    /// <summary>How long the device is usually on battery in a day.</summary>
    public TimeSpan? TypicalDailyUse(string id) => DailyLog.TypicalUse(Daily.For(id), DateOnly.FromDateTime(DateTime.Now));

    /// <summary>"≈ 6 h restantes" / "lleno en ≈ 40 min", or null.</summary>
    public string? EstimateText(string id)
    {
        var samples = For(id);
        var t = Estimate(samples, out bool toFull, Analyze(samples, DateTime.MinValue).PerCharge);
        if (t is not TimeSpan span) return null;
        return toFull ? L.FullIn(Amount(span)) : L.TimeLeft(Amount(span));
    }

    /// <summary>"40 min", "6 h", "3 días".</summary>
    public static string Amount(TimeSpan span) =>
        span.TotalMinutes < 60 ? L.Minutes(Math.Max(1, (int)Math.Round(span.TotalMinutes / 5) * 5))
        : span.TotalHours < 48 ? L.Hours(Math.Round(span.TotalHours))
        : L.Days(Math.Round(span.TotalDays));
}
