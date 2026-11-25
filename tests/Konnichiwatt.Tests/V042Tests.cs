using Konnichiwatt.Core;
using Xunit;

namespace Konnichiwatt.Tests;

public class ReadingFilterTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 9, 26, 0);

    private static DeviceBattery Dev(int? pct, ChargeState state = ChargeState.Discharging, bool stale = false) =>
        new("x", "X", DeviceKind.Headset, pct, state, "t", Stale: stale);

    private static DeviceBattery One(ReadingFilter f, DeviceBattery d, double seconds) =>
        f.Apply(new[] { d }, T0.AddSeconds(seconds)).Single();

    [Fact]
    public void A_wrong_reading_on_wake_up_is_never_published()
    {
        // Real Cloud Alpha sequence: 38 % the night before, 50 % for about a minute, then 37 %.
        var f = new ReadingFilter(new[] { Dev(38) });
        foreach (var s in new[] { 0, 31, 62, 90 })
        {
            var r = One(f, Dev(50), s);
            Assert.Equal(38, r.Percent);
            Assert.True(r.Stale && r.Verifying);
        }
        var ok = One(f, Dev(37), 93);
        Assert.Equal(37, ok.Percent);
        Assert.False(ok.Stale || ok.Verifying);
        Assert.False(f.HasPending(T0.AddSeconds(93)));
    }

    [Fact]
    public void A_jump_that_lasts_two_minutes_is_accepted()
    {
        var f = new ReadingFilter(new[] { Dev(20) });
        Assert.True(One(f, Dev(95), 0).Verifying);
        Assert.True(f.HasPending(T0));
        Assert.True(One(f, Dev(95), 60).Verifying);
        var r = One(f, Dev(94), 120);
        Assert.Equal(94, r.Percent);
        Assert.False(r.Stale);
    }

    [Fact]
    public void Small_changes_charging_and_first_readings_pass_straight_through()
    {
        var f = new ReadingFilter(Array.Empty<DeviceBattery>());
        Assert.Equal(60, One(f, Dev(60), 0).Percent);
        Assert.Equal(55, One(f, Dev(55), 30).Percent);
        Assert.Equal(80, One(f, Dev(80, ChargeState.Charging), 60).Percent);
        Assert.Equal(100, One(f, Dev(100, ChargeState.Full), 90).Percent);
    }

    [Fact]
    public void Stale_and_unknown_levels_are_left_alone()
    {
        var f = new ReadingFilter(new[] { Dev(50) });
        Assert.Equal(10, One(f, Dev(10, stale: true), 0).Percent);
        Assert.Null(One(f, Dev(null), 30).Percent);
        Assert.Equal(50, One(f, Dev(50), 60).Percent);
    }

    [Fact]
    public void Switching_the_device_off_starts_the_confirmation_over()
    {
        var f = new ReadingFilter(new[] { Dev(38) });
        Assert.True(One(f, Dev(50), 0).Verifying);
        Assert.True(One(f, Dev(50), 3600).Verifying); // an hour later: not two minutes of readings
    }

    [Fact]
    public void A_device_is_not_held_back_forever()
    {
        var f = new ReadingFilter(new[] { Dev(90) });
        DeviceBattery r = Dev(0);
        for (int s = 0; s <= 300; s += 30) r = One(f, Dev(s / 30 % 2 == 0 ? 20 : 60), s);
        Assert.False(r.Verifying);
    }
}

public class LearnedLifeTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 8, 0, 0);

    [Fact]
    public void Wobbling_levels_count_net_drain_only()
    {
        // 20 % down over 40 h, going up and down by 1 all the way: 1 % every 2 h → 200 h per charge.
        var s = Enumerable.Range(0, 81).Select(i =>
            new BatterySample(T0.AddMinutes(30 * i), 80 - i / 4 + (i % 2 == 1 ? 1 : 0), false)).ToList();
        var life = BatteryHistory.Analyze(s, DateTime.MinValue).PerCharge;
        Assert.InRange(life!.Value.TotalHours, 190, 210);
    }

    [Fact]
    public void Several_short_discharges_add_up()
    {
        // 6 % at 2 %/h, charge, 6 % at 2 %/h: neither reaches 10 % alone.
        var s = new List<BatterySample>();
        for (int h = 0; h <= 3; h++) s.Add(new BatterySample(T0.AddHours(h), 60 - 2 * h, false));
        s.Add(new BatterySample(T0.AddHours(3.5), 80, true));
        for (int h = 0; h <= 3; h++) s.Add(new BatterySample(T0.AddHours(4 + h), 90 - 2 * h, false));
        var stats = BatteryHistory.Analyze(s, DateTime.MinValue);
        Assert.Equal(2, stats.Discharges);
        Assert.InRange(stats.PerCharge!.Value.TotalHours, 49, 51);
    }

    [Fact]
    public void Without_a_usable_run_the_learned_life_gives_the_estimate()
    {
        var s = new List<BatterySample> { new(T0, 50, false) };
        Assert.Null(BatteryHistory.Estimate(s, out _));
        Assert.Equal(50, BatteryHistory.Estimate(s, out bool toFull, TimeSpan.FromHours(100))!.Value.TotalHours, 1);
        Assert.False(toFull);
    }

    [Fact]
    public void A_short_run_is_blended_and_a_long_one_wins()
    {
        List<BatterySample> Run(int drop) => Enumerable.Range(0, drop + 1) // 10 %/h
            .Select(i => new BatterySample(T0.AddMinutes(6 * i), 80 - i, false)).ToList();
        var learned = TimeSpan.FromHours(100); // 1 %/h
        var blended = BatteryHistory.Estimate(Run(6), out _, learned)!.Value.TotalHours;   // 40 %: 4.6 %/h
        Assert.InRange(blended, 15, 17);
        var own = BatteryHistory.Estimate(Run(20), out _, learned)!.Value.TotalHours;
        Assert.InRange(own, 5.8, 6.2);
    }

    [Fact]
    public void Charging_does_not_use_the_learned_life()
    {
        var s = new List<BatterySample> { new(T0, 50, true) };
        Assert.Null(BatteryHistory.Estimate(s, out bool toFull, TimeSpan.FromHours(100)));
        Assert.True(toFull);
    }

    [Fact]
    public void Old_wrong_readings_are_removed_but_real_changes_kept()
    {
        var s = new List<BatterySample>
        {
            new(T0.AddHours(-8), 38, false), new(T0, 50, false), new(T0.AddMinutes(1), 37, false), // spike
            new(T0.AddHours(1), 36, false), new(T0.AddHours(3), 90, false), new(T0.AddHours(4), 89, false), // charged unseen
        };
        Assert.Equal(new[] { 38, 37, 36, 90, 89 }, BatteryHistory.RemoveSpikes(s).Select(x => x.Percent));
    }
}

public class SafeFileTests
{
    [Fact]
    public void Writing_replaces_the_file_and_leaves_nothing_behind()
    {
        var dir = Directory.CreateTempSubdirectory("kw-test").FullName;
        try
        {
            var path = Path.Combine(dir, "a.json");
            SafeFile.WriteAllText(path, "uno");
            SafeFile.WriteAllText(path, "año ≈");
            Assert.Equal("año ≈", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(dir));
            SafeFile.KeepCorrupt(path);
            Assert.Equal("año ≈", File.ReadAllText(path + ".corrupt"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
