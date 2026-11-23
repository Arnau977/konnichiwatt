namespace Konnichiwatt.Tests;

public class ReleaseNotesTests
{
    private const string Changelog = """
        # Novedades

        ## 0.4.0
        - Uno // One
        - Dos // Two
        - Solo español

        ## 0.3.0
        - Viejo // Old
        """;

    [Fact]
    public void Notes_of_a_version_follow_the_language()
    {
        var notes = ReleaseNotes.Parse(Changelog);
        L.Init("es");
        Assert.Equal(new[] { "Uno", "Dos", "Solo español" }, notes.For("0.4.0"));
        L.Init("en");
        Assert.Equal(new[] { "One", "Two", "Solo español" }, notes.For("v0.4.0"));
        Assert.Equal(new[] { "Old" }, notes.For("0.3.0.0"));
        Assert.Empty(notes.For("9.9.9"));
        L.Init("auto");
    }

    [Fact]
    public void Section_extracts_one_version_for_update_json()
    {
        var s = ReleaseNotes.Section(Changelog.Replace("\r", ""), "0.4.0");
        Assert.StartsWith("## 0.4.0", s);
        Assert.Contains("- Dos // Two", s);
        Assert.DoesNotContain("Viejo", s);
    }

    [Fact]
    public void Embedded_changelog_has_notes_for_the_current_version() =>
        Assert.NotEmpty(ReleaseNotes.Embedded.For(Updater.CurrentVersion.ToString(3)));

    [Fact]
    public void Balloon_text_stays_within_the_windows_limit()
    {
        var longNotes = Enumerable.Range(0, 10).Select(i => new string('x', 120)).ToList();
        L.Init("es");
        Assert.True(L.UpdatedBody("1.2.3", longNotes).Length <= 255);
        Assert.True(L.UpdateAvailableBody("1.2.3", longNotes).Length <= 255);
        Assert.Contains("clic", L.UpdateAvailableBody("1.2.3", Array.Empty<string>()));
        L.Init("auto");
    }
}

public class RollbackAndChannelTests
{
    private static UpdateManifest M(string v) => new(v, "https://github.com/x/y/releases/download/v" + v + "/Konnichiwatt.exe", "00");

    [Fact]
    public void A_version_that_was_rolled_back_is_not_offered_again()
    {
        Assert.False(Updater.IsAcceptable(M("0.5.0"), new Version(0, 4, 0), skipVersion: "0.5.0"));
        Assert.True(Updater.IsAcceptable(M("0.5.1"), new Version(0, 4, 0), skipVersion: "0.5.0"));
        Assert.True(Updater.IsAcceptable(M("0.5.0"), new Version(0, 4, 0), skipVersion: null));
        Assert.False(Updater.IsAcceptable(null, new Version(0, 4, 0), null));
    }
}

public class HistoryStatsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 20, 0, 0);

    [Fact]
    public void Old_samples_are_thinned_and_very_old_ones_dropped()
    {
        var list = new List<BatterySample>();
        for (var t = Now.AddDays(-40); t <= Now; t = t.AddMinutes(10)) list.Add(new BatterySample(t, 50, false));
        var thin = BatteryHistory.Thin(list, Now);
        Assert.All(thin, s => Assert.True(Now - s.Time <= BatteryHistory.Keep));
        var old = thin.Where(s => Now - s.Time > BatteryHistory.Detailed).ToList();
        var recent = thin.Where(s => Now - s.Time <= BatteryHistory.Detailed).ToList();
        Assert.InRange(old.Count, 27 * 48 - 2, 28 * 48 + 2);        // ~1 every 30 min for 28 days
        Assert.Equal(list.Count(s => Now - s.Time <= BatteryHistory.Detailed), recent.Count); // untouched
    }

    [Fact]
    public void Thinning_keeps_charge_start_and_end()
    {
        var t0 = Now.AddDays(-10);
        var list = new List<BatterySample>
        {
            new(t0, 40, false), new(t0.AddMinutes(10), 39, false),
            new(t0.AddMinutes(20), 41, true), new(t0.AddMinutes(25), 45, false),
        };
        var thin = BatteryHistory.Thin(list, Now);
        Assert.Contains(thin, s => s.Charging);
        Assert.Equal(3, thin.Count); // 39 % dropped (same state, < 30 min), state changes kept
    }

    [Fact]
    public void Battery_life_comes_from_observed_drain_only()
    {
        // Two days: 100 → 40 % in 12 h each day (5 %/h), PC off at night (gap), charge in between.
        var s = new List<BatterySample>();
        var day1 = Now.AddDays(-2).Date.AddHours(8);
        for (int h = 0; h <= 12; h++) s.Add(new BatterySample(day1.AddHours(h), 100 - 5 * h, false));
        var day2 = day1.AddDays(1);
        s.Add(new BatterySample(day2.AddMinutes(-30), 50, true));
        s.Add(new BatterySample(day2.AddMinutes(-10), 100, true));
        for (int h = 0; h <= 12; h++) s.Add(new BatterySample(day2.AddHours(h), 100 - 5 * h, false));

        var stats = BatteryHistory.Analyze(s, Now.AddDays(-7));
        Assert.Equal(2, stats.Discharges);
        Assert.Equal(1, stats.Charges);
        Assert.NotNull(stats.PerCharge);
        Assert.InRange(stats.PerCharge!.Value.TotalHours, 19.5, 20.5); // 100 % / 5 %/h
        Assert.Equal(day2.AddMinutes(-10), stats.LastCharge);
    }

    [Fact]
    public void Small_drops_are_not_enough_for_an_estimate()
    {
        var s = Enumerable.Range(0, 6).Select(i => new BatterySample(Now.AddHours(-6 + i), 80 - i, false)).ToList();
        var stats = BatteryHistory.Analyze(s, Now.AddDays(-2));
        Assert.Null(stats.PerCharge);
        Assert.Equal(0, stats.Discharges);
    }
}

public class RedactorTests
{
    [Fact]
    public void Mac_addresses_are_hidden_but_ids_kept()
    {
        var input = @"045E:0B13 in=64 \\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002045e_pid&0b13#9&2c1d&0&a4c13812ab9f#{4d1e55b2}" +
                    "\nBT address 98:B6:E9:12:34:56";
        var r = DiagnosticsRedactor.Redact(input);
        Assert.DoesNotContain("a4c13812ab9f", r);
        Assert.DoesNotContain("98:B6:E9:12:34:56", r);
        Assert.Contains("045E:0B13", r);
        Assert.Contains("00001124-0000-1000-8000-00805f9b34fb", r); // service GUID: 8-4-4-4-12, its last group is 12 hex
    }
}
