using System.Text.Json;

namespace Konnichiwatt.Core;

/// <summary>One device's day, as far as it was seen running on battery.</summary>
/// <param name="Hours">Time seen on battery.</param>
/// <param name="Drop">Net % used in that time.</param>
/// <param name="Charges">Times it started charging.</param>
public sealed record DaySummary(DateOnly Day, double Hours, int Drop, int Charges);

/// <summary>Battery life of a full charge during a calendar month.</summary>
public sealed record MonthLife(DateOnly Month, TimeSpan PerCharge);

/// <summary>
/// One line per device and day, kept for two years in daily.json (<see cref="BatteryHistory"/> only goes back 30 days),
/// so battery life can be compared month by month.
/// </summary>
public sealed class DailyLog
{
    private const int KeepDays = 730;
    /// <summary>% used in a month for its battery life to mean something.</summary>
    private const int MinMonthDrop = 30;
    private static readonly TimeSpan MaxGap = TimeSpan.FromHours(2);
    private static readonly string FilePath = Path.Combine(Log.Directory, "daily.json");

    private Dictionary<string, List<DaySummary>> _days = new();
    private bool _dirty;

    public IReadOnlyList<DaySummary> For(string id) => _days.TryGetValue(id, out var l) ? l : Array.Empty<DaySummary>();

    public static DailyLog Load()
    {
        var log = new DailyLog();
        try
        {
            if (File.Exists(FilePath))
                log._days = JsonSerializer.Deserialize<Dictionary<string, List<DaySummary>>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) { Log.Error("Load daily log", e); SafeFile.KeepCorrupt(FilePath); }
        return log;
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Log.Directory);
            SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(_days));
            _dirty = false;
        }
        catch (Exception e) { Log.Error("Save daily log", e); }
    }

    /// <summary>Adds the finished days found in the samples that aren't in the log yet.</summary>
    public void Update(string id, IReadOnlyList<BatterySample> samples, DateOnly today)
    {
        if (!_days.TryGetValue(id, out var list)) list = new List<DaySummary>();
        var known = list.Select(d => d.Day).ToHashSet();
        var fresh = Summarize(samples).Where(d => d.Day < today && !known.Contains(d.Day)).ToList();
        if (fresh.Count == 0) return;
        _days[id] = list.Concat(fresh).Where(d => today.DayNumber - d.Day.DayNumber <= KeepDays).OrderBy(d => d.Day).ToList();
        _dirty = true;
    }

    /// <summary>
    /// Same rules as <see cref="BatteryHistory.Analyze"/>: net drain, only between readings less than 2 h apart, nothing
    /// while charging or across a jump up (charged unseen). Time around midnight counts for the new day.
    /// </summary>
    internal static List<DaySummary> Summarize(IReadOnlyList<BatterySample> samples)
    {
        var days = new SortedDictionary<DateOnly, DaySummary>();
        for (int i = 1; i < samples.Count; i++)
        {
            var (prev, cur) = (samples[i - 1], samples[i]);
            var day = DateOnly.FromDateTime(cur.Time);
            var d = days.TryGetValue(day, out var known) ? known : new DaySummary(day, 0, 0, 0);
            if (cur.Charging && !prev.Charging) d = d with { Charges = d.Charges + 1 };
            var gap = cur.Time - prev.Time;
            if (!cur.Charging && !prev.Charging && gap <= MaxGap && cur.Percent <= prev.Percent + ReadingFilter.MaxJump)
                d = d with { Hours = Math.Round(d.Hours + gap.TotalHours, 4), Drop = d.Drop + prev.Percent - cur.Percent };
            days[day] = d;
        }
        return days.Values.Where(d => d.Hours > 0 || d.Charges > 0).ToList();
    }

    /// <summary>Battery life per month, oldest first; months with too little use are left out.</summary>
    public static List<MonthLife> Months(IEnumerable<DaySummary> days) =>
        days.GroupBy(d => new DateOnly(d.Day.Year, d.Day.Month, 1))
            .Select(g => (Month: g.Key, Drop: g.Sum(d => d.Drop), Hours: g.Sum(d => d.Hours)))
            .Where(m => m.Drop >= MinMonthDrop && m.Hours > 0)
            .OrderBy(m => m.Month)
            .Select(m => new MonthLife(m.Month, TimeSpan.FromHours(100 * m.Hours / m.Drop)))
            .ToList();

    /// <summary>Average time on battery over the last two weeks' days with use, or null with fewer than two of them.</summary>
    public static TimeSpan? TypicalUse(IEnumerable<DaySummary> days, DateOnly today)
    {
        var used = days.Where(d => d.Day < today && today.DayNumber - d.Day.DayNumber <= 14 && d.Hours >= 0.5).ToList();
        return used.Count >= 2 ? TimeSpan.FromHours(used.Average(d => d.Hours)) : null;
    }
}
