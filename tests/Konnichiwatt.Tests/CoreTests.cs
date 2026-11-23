using Konnichiwatt.Devices;

namespace Konnichiwatt.Tests;

public class EstimateTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 8, 0, 0);

    private static List<BatterySample> Line(int from, int to, double hours, bool charging = false, int steps = 12) =>
        Enumerable.Range(0, steps + 1)
            .Select(i => new BatterySample(T0.AddHours(hours * i / steps), (int)Math.Round(from + (to - from) * i / (double)steps), charging))
            .ToList();

    [Fact]
    public void Steady_drain_of_10_percent_per_hour_from_50_leaves_5_hours()
    {
        var t = BatteryHistory.Estimate(Line(80, 50, 3), out bool toFull);
        Assert.False(toFull);
        Assert.NotNull(t);
        Assert.InRange(t!.Value.TotalHours, 4.7, 5.3);
    }

    [Fact]
    public void Charging_25_percent_per_hour_from_50_is_full_in_2_hours()
    {
        var t = BatteryHistory.Estimate(Line(0, 50, 2, charging: true), out bool toFull);
        Assert.True(toFull);
        Assert.InRange(t!.Value.TotalHours, 1.8, 2.2);
    }

    [Fact]
    public void Not_enough_time_gives_no_estimate() =>
        Assert.Null(BatteryHistory.Estimate(Line(80, 70, 0.25), out _));

    [Fact]
    public void Flat_level_gives_no_estimate() =>
        Assert.Null(BatteryHistory.Estimate(Line(60, 59, 3), out _));

    [Fact]
    public void Only_the_current_run_counts()
    {
        // Charged 20→100, then discharging 100→80 over 2 h: 10 %/h → ~8 h left (the charge must be ignored).
        var samples = Line(20, 100, 2, charging: true)
            .Concat(Line(100, 80, 2).Select(s => s with { Time = s.Time.AddHours(2.1) }))
            .ToList();
        var t = BatteryHistory.Estimate(samples, out bool toFull);
        Assert.False(toFull);
        Assert.InRange(t!.Value.TotalHours, 7, 9);
    }

    [Fact]
    public void A_long_gap_starts_a_new_run()
    {
        // Yesterday's drain, then 10 h off, then only 20 min today: not enough for an estimate.
        var samples = Line(90, 60, 3)
            .Concat(Line(60, 58, 1.0 / 3).Select(s => s with { Time = s.Time.AddHours(13) }))
            .ToList();
        Assert.Null(BatteryHistory.Estimate(samples, out _));
    }

    [Fact]
    public void Full_while_charging_gives_no_estimate() =>
        Assert.Null(BatteryHistory.Estimate(Line(60, 100, 2, charging: true), out _));

    [Fact]
    public void History_records_on_change_or_every_10_minutes()
    {
        var h = new BatteryHistory();
        var d = new DeviceBattery("x", "X", DeviceKind.Mouse, 50, ChargeState.Discharging, "t");
        h.Record(new[] { d }, T0);
        h.Record(new[] { d }, T0.AddMinutes(5));          // same value, too soon: skipped
        h.Record(new[] { d with { Percent = 49 } }, T0.AddMinutes(6)); // changed: kept
        h.Record(new[] { d with { Percent = 49 } }, T0.AddMinutes(17)); // same, 11 min later: kept
        h.Record(new[] { d with { Stale = true, Percent = 10 } }, T0.AddMinutes(30)); // stale: ignored
        Assert.Equal(new[] { 50, 49, 49 }, h.For("x").Select(s => s.Percent));
    }
}

public class SettingsTests
{
    private static DeviceBattery Dev(string id, int? pct = 50, bool charging = false) =>
        new(id, id.ToUpperInvariant(), DeviceKind.Mouse, pct, charging ? ChargeState.Charging : ChargeState.Discharging, "t");

    [Fact]
    public void Arrange_applies_user_order_then_detection_order()
    {
        var s = new AppSettings { DeviceOrder = new() { "c", "a" } };
        var ids = s.Arrange(new[] { Dev("a"), Dev("b"), Dev("c"), Dev("d") }).Select(d => d.Id);
        Assert.Equal(new[] { "c", "a", "b", "d" }, ids);
    }

    [Fact]
    public void Move_and_set_order_keep_unplugged_devices_remembered()
    {
        var s = new AppSettings { DeviceOrder = new() { "gone", "a", "b" } };
        s.MoveDevice(new[] { "a", "b" }, 0, +1);
        Assert.Equal(new[] { "b", "a", "gone" }, s.DeviceOrder);
        s.SetOrder(new[] { "a", "b" });
        Assert.Equal(new[] { "a", "b", "gone" }, s.DeviceOrder);
    }

    [Fact]
    public void Customization_overrides_name_and_icon()
    {
        var s = new AppSettings();
        s.CustomizationFor("a").Name = "Cascos";
        s.CustomizationFor("a").Kind = DeviceKind.Headset;
        var d = s.Customize(Dev("a"));
        Assert.Equal("Cascos", d.Name);
        Assert.Equal(DeviceKind.Headset, d.Kind);
    }

    [Fact]
    public void Device_threshold_overrides_general_one()
    {
        var s = new AppSettings { LowBatteryThreshold = 20 };
        s.CustomizationFor("pad").LowThreshold = 10;
        Assert.Equal(10, s.ThresholdFor("pad"));
        Assert.Equal(20, s.ThresholdFor("headset"));
        Assert.False(s.IsLow(Dev("pad", 15)));    // 15 % > its own 10 %
        Assert.True(s.IsLow(Dev("headset", 15))); // 15 % <= general 20 %
        Assert.False(s.IsLow(Dev("headset", 15, charging: true)));
    }

    [Fact]
    public void Prune_removes_only_empty_customizations()
    {
        var s = new AppSettings();
        s.CustomizationFor("empty");
        s.CustomizationFor("threshold").LowThreshold = 15;
        s.PruneCustomizations();
        Assert.False(s.Customizations.ContainsKey("empty"));
        Assert.True(s.Customizations.ContainsKey("threshold"));
    }

    [Fact]
    public void Discreet_mode_hides_devices_above_the_limit()
    {
        var s = new AppSettings { DiscreetMode = true, DiscreetBelow = 30 };
        Assert.True(s.ShowsOnTaskbar(Dev("a", 25)));
        Assert.False(s.ShowsOnTaskbar(Dev("a", 60)));
        Assert.False(s.ShowsOnTaskbar(Dev("a", null)));
        s.DiscreetMode = false;
        Assert.True(s.ShowsOnTaskbar(Dev("a", 60)));
    }

    [Fact]
    public void Hidden_and_hide_while_charging()
    {
        var s = new AppSettings { HiddenDevices = new() { "h" }, HideWhileCharging = true };
        Assert.False(s.IsShown(Dev("h")));
        Assert.False(s.IsShown(Dev("c", 50, charging: true)));
        Assert.True(s.IsShown(Dev("ok")));
    }

    [Fact]
    public void Monitor_choice_with_legacy_default()
    {
        var s = new AppSettings();                       // never chosen, legacy "all" on
        Assert.True(s.ShowsOnMonitor("tv", primary: false));
        s.ShowOnAllTaskbars = false;                     // legacy "main only"
        Assert.False(s.ShowsOnMonitor("tv", primary: false));
        Assert.True(s.ShowsOnMonitor("main", primary: true));
        s.TaskbarMonitors = new() { "tv" };              // explicit choice wins
        Assert.True(s.ShowsOnMonitor("tv", primary: false));
        Assert.False(s.ShowsOnMonitor("main", primary: true));
    }

    [Fact]
    public void Stable_hash_is_deterministic_and_case_insensitive()
    {
        Assert.Equal(HidUtil.StableHash(@"\\?\HID#VID_057E"), HidUtil.StableHash(@"\\?\hid#vid_057e"));
        Assert.Equal("811C9DC5", HidUtil.StableHash("")); // FNV-1a offset basis
    }
}
