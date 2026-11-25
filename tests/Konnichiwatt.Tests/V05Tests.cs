using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Konnichiwatt.UI;
using Konnichiwatt.UI.Fluent;

namespace Konnichiwatt.Tests;

public class AlertTests
{
    private readonly AppSettings _s = new();
    private readonly BatteryHistory _h = new();

    private static DeviceBattery Dev(int pct, ChargeState state = ChargeState.Discharging, bool stale = false, string id = "x") =>
        new(id, id.ToUpperInvariant(), DeviceKind.Headset, pct, state, "t", Stale: stale);

    private static AlertKind[] Kinds(AlertEngine e, params DeviceBattery[] d) => e.Check(d).Select(a => a.Kind).ToArray();

    [Fact]
    public void New_alerts_are_off_by_default()
    {
        Assert.False(_s.CriticalNotifications || _s.PredictiveNotifications || _s.ChargeLimitNotifications);
        var e = new AlertEngine(_s, _h);
        Assert.Equal(new[] { AlertKind.Low }, Kinds(e, Dev(18)));
        Assert.Empty(Kinds(e, Dev(3)));
        Assert.Empty(Kinds(e, Dev(50, ChargeState.Charging)));
        Assert.Empty(Kinds(e, Dev(85, ChargeState.Charging)));
        Assert.Equal(new[] { AlertKind.ChargeComplete }, Kinds(e, Dev(100, ChargeState.Full)));
    }

    [Fact]
    public void Every_alert_can_be_turned_off()
    {
        _s.LowBatteryNotifications = _s.ChargeCompleteNotifications = false;
        var e = new AlertEngine(_s, _h);
        foreach (var d in new[] { Dev(50), Dev(10), Dev(1), Dev(40, ChargeState.Charging), Dev(90, ChargeState.Charging), Dev(100, ChargeState.Full) })
            Assert.Empty(Kinds(e, d));
    }

    [Fact]
    public void Low_then_critical_once_each_and_again_after_charging()
    {
        _s.CriticalNotifications = true;
        var e = new AlertEngine(_s, _h);
        Assert.Empty(Kinds(e, Dev(30)));
        Assert.Equal(new[] { AlertKind.Low }, Kinds(e, Dev(20)));
        Assert.Empty(Kinds(e, Dev(19)));
        Assert.Equal(new[] { AlertKind.Critical }, Kinds(e, Dev(5)));
        Assert.Empty(Kinds(e, Dev(4)));
        Assert.Empty(Kinds(e, Dev(4, ChargeState.Charging)));
        Assert.Equal(new[] { AlertKind.Critical }, Kinds(e, Dev(4))); // unplugged still critical: both due, one shown
    }

    [Fact]
    public void Changing_one_alert_does_not_repeat_the_others()
    {
        var e = new AlertEngine(_s, _h);
        Assert.Equal(new[] { AlertKind.Low }, Kinds(e, Dev(4)));
        _s.CriticalNotifications = true;
        Assert.Equal(new[] { AlertKind.Critical }, Kinds(e, Dev(4)));
        _s.LowBatteryThreshold = 25;
        Assert.Equal(new[] { AlertKind.Low }, Kinds(e, Dev(4)));
        _s.ChargeLimit = 70;
        Assert.Empty(Kinds(e, Dev(4)));
    }

    [Fact]
    public void Hidden_and_stale_devices_give_no_low_alert()
    {
        _s.CriticalNotifications = true;
        _s.HiddenDevices.Add("h");
        var e = new AlertEngine(_s, _h);
        Assert.Empty(Kinds(e, Dev(3, id: "h"), Dev(3, stale: true)));
    }

    [Fact]
    public void Charge_limit_once_per_charge_seen_from_below()
    {
        _s.ChargeLimitNotifications = true;
        var e = new AlertEngine(_s, _h);
        Assert.Empty(Kinds(e, Dev(85, ChargeState.Charging, id: "late"))); // already past it at start
        Assert.Empty(Kinds(e, Dev(78, ChargeState.Charging)));
        Assert.Equal(new[] { AlertKind.ChargeLimit }, Kinds(e, Dev(80, ChargeState.Charging)));
        Assert.Empty(Kinds(e, Dev(81, ChargeState.Charging)));
        Assert.Empty(Kinds(e, Dev(75)));
        Assert.Empty(Kinds(e, Dev(76, ChargeState.Charging)));
        Assert.Equal(new[] { AlertKind.ChargeLimit }, Kinds(e, Dev(82, ChargeState.Charging)));
    }

    /// <summary>Four past days of 8 h at 2.5 %/h (40 h per charge), charged each night, and today's level.</summary>
    private void Use(int today)
    {
        var now = DateTime.Now;
        var list = new List<BatterySample>();
        for (int d = 4; d >= 1; d--)
        {
            var start = now.Date.AddDays(-d).AddHours(9);
            list.Add(new BatterySample(start.AddHours(-1), 100, true));
            for (int i = 0; i <= 16; i++) list.Add(new BatterySample(start.AddHours(i / 2.0), 100 - (int)Math.Round(i * 1.25), false));
        }
        list.Add(new BatterySample(now, today, false));
        _h.Replace("x", list);
        _h.UpdateDaily(now);
    }

    [Fact]
    public void Predictive_alert_when_less_left_than_a_usual_day()
    {
        _s.PredictiveNotifications = true;
        var e = new AlertEngine(_s, _h);
        Use(80);
        Assert.Empty(Kinds(e, Dev(80)));   // 32 h left
        Use(22);                           // 8.8 h left, 8 h a day
        Assert.Empty(Kinds(e, Dev(22)));
        _s.LowBatteryThreshold = 10;
        Use(18);                           // 7.2 h left
        var a = Assert.Single(e.Check(new[] { Dev(18) }));
        Assert.Equal(AlertKind.WontLast, a.Kind);
        Assert.InRange(a.Left!.Value.TotalHours, 6.5, 8);
        Assert.InRange(a.Daily!.Value.TotalHours, 7.9, 8.1);
        Use(17);
        Assert.Empty(Kinds(e, Dev(17)));   // only once
    }

    [Fact]
    public void Predictive_alert_stays_quiet_when_the_low_one_covers_it_or_history_is_short()
    {
        _s.PredictiveNotifications = true;
        var e = new AlertEngine(_s, _h);
        Assert.Empty(Kinds(e, Dev(60)));   // no history
        Use(18);
        Assert.Equal(new[] { AlertKind.Low }, Kinds(e, Dev(18)));
    }
}

public class DailyLogTests
{
    private static readonly DateTime Day = new(2026, 11, 20, 9, 0, 0);

    private static List<BatterySample> Days(int count, double perHour, DateTime first)
    {
        var l = new List<BatterySample>();
        for (int d = 0; d < count; d++)
            for (int i = 0; i <= 10; i++) l.Add(new BatterySample(first.AddDays(d).AddHours(i), 90 - (int)Math.Round(i * perHour), false));
        return l;
    }

    [Fact]
    public void Summarizes_finished_days_only_and_never_twice()
    {
        var log = new DailyLog();
        var s = Days(3, 2, Day);
        log.Update("x", s, new DateOnly(2026, 11, 22));
        Assert.Equal(new[] { 20, 21 }, log.For("x").Select(d => d.Day.Day));
        Assert.All(log.For("x"), d => { Assert.Equal(10, d.Hours, 2); Assert.Equal(20, d.Drop); });
        log.Update("x", s.Take(3).ToList(), new DateOnly(2026, 11, 23)); // thinned since: the stored days stay
        Assert.Equal(2, log.For("x").Count);
        Assert.Equal(20, log.For("x")[0].Drop);
    }

    [Fact]
    public void Nights_and_charges_do_not_count_as_use()
    {
        var s = new List<BatterySample>
        {
            new(Day, 50, false), new(Day.AddHours(1), 48, false),
            new(Day.AddHours(1.5), 60, true), new(Day.AddHours(2), 80, true),
            new(Day.AddHours(2.5), 79, false), new(Day.AddHours(3.5), 77, false),
            new(Day.AddDays(1), 60, false),
        };
        var d = Assert.Single(DailyLog.Summarize(s));
        Assert.Equal(2, d.Hours, 2);
        Assert.Equal(4, d.Drop);
        Assert.Equal(1, d.Charges);
    }

    [Fact]
    public void Months_are_compared_once_there_are_two()
    {
        {
            var es = CultureInfo.GetCultureInfo("es-ES");
            var oct = DailyLog.Summarize(Days(5, 2, new DateTime(2026, 10, 5, 9, 0, 0)));   // 50 h
            var nov = DailyLog.Summarize(Days(5, 2.5, new DateTime(2026, 11, 5, 9, 0, 0))); // 40 h
            Assert.DoesNotContain("≈", HistoryChartCard.MonthsText(DailyLog.Months(nov), es));
            var months = DailyLog.Months(oct.Concat(nov));
            Assert.Equal(new[] { 50.0, 40.0 }, months.Select(m => Math.Round(m.PerCharge.TotalHours)));
            Assert.Equal("oct ≈ 50 h  ·  nov ≈ 40 h  (−20 %)", HistoryChartCard.MonthsText(months, es));
            Assert.Empty(DailyLog.Months(nov.Take(1))); // 25 % isn't enough for a month
        }
    }
}

public class SettingsLayoutTests
{
    private static void Sta(Action a)
    {
        Exception? error = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static SettingsWindow Window(AppSettings s, Action? changed = null)
    {
        var samples = PreviewRenderer.Samples();
        return new SettingsWindow(s, new SettingsWindow.Callbacks(() => samples, SettingsWindow.SampleEstimate, changed ?? (() => { }),
            () => { }, () => { }, () => { }, History: SettingsWindow.SampleHistory(samples), ReportDevice: () => { }), null)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-9000, -9000), ShowInTaskbar = false,
        };
    }

    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));

    [Fact]
    public void Cards_follow_the_window_on_every_page_without_sideways_scrolling() => Sta(() =>
    {
        using var w = Window(new AppSettings());
        w.Show();
        for (int page = SettingsWindow.DevicesPage; page <= SettingsWindow.NewsPage; page++)
        {
            w.ShowPage(page);
            var content = All(w).OfType<FlowLayoutPanel>().Single(p => p.AutoScroll);
            foreach (int width in new[] { 800, 1100, 1800, 800 })
            {
                w.ClientSize = new Size(F.Px(width), F.Px(600));
                Application.DoEvents();
                Assert.False(content.HorizontalScroll.Visible, $"page {page} at {width}");
                var widths = content.Controls.Cast<Control>().Select(c => c.Width).Distinct().ToList();
                Assert.Single(widths);
                Assert.True(widths[0] <= F.Px(1000) && widths[0] <= content.ClientSize.Width);
                if (content.Width >= F.Px(1100)) Assert.Equal(F.Px(1000), widths[0]);
                foreach (var list in content.Controls.OfType<DeviceListPanel>())
                    Assert.All(list.Controls.Cast<Control>(), r => Assert.Equal(list.Width, r.Width));
            }
        }
        w.Close();
    });

    [Fact]
    public void Window_size_is_remembered_and_never_below_the_minimum() => Sta(() =>
    {
        var s = new AppSettings();
        int saved = 0;
        using (var w = Window(s, () => saved++))
        {
            w.Show();
            w.Close();
        }
        Assert.Equal(0, saved); // untouched: nothing to remember
        using (var w = Window(s, () => saved++))
        {
            w.Show();
            w.ClientSize = new Size(F.Px(1000), F.Px(650));
            w.Close();
        }
        Assert.Equal(1, saved);
        Assert.Equal((1000, 650), (s.WindowWidth, s.WindowHeight));
        using (var w = Window(s))
        {
            w.Show();
            Assert.Equal(new Size(F.Px(1000), F.Px(650)), w.ClientSize);
            w.Close();
        }
        s.WindowWidth = 10;
        using (var w = Window(s))
        {
            w.Show();
            Assert.Equal(F.Px(800), w.ClientSize.Width);
            w.Close();
        }
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void Long_descriptions_wrap_and_what_still_does_not_fit_is_available_on_hover(float scale) => Sta(() =>
    {
        F.Init(light: false, scale);
        try
        {
            SettingCard Card(string title, string description) => new(null, title, description, null, F.Px(300));
            IEnumerable<string> Cut(SettingCard c)
            {
                using var bmp = new Bitmap(c.Width, c.Height);
                c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
                return c.CutTexts.ToList();
            }

            using var one = Card("Título", "Corta");
            Assert.Equal(F.Px(70), one.Height);
            Assert.Empty(Cut(one));

            using var two = Card("Título", "Una descripción bastante más larga que no cabe de ninguna manera en una línea");
            Assert.True(two.Height > F.Px(70));
            Assert.Empty(Cut(two));
            two.Width = F.Px(900);
            Assert.Equal(F.Px(70), two.Height); // wide enough again

            var endless = string.Join(" ", Enumerable.Repeat("palabra", 60));
            using var three = Card(endless, endless);
            Assert.Equal(new[] { endless, endless }, Cut(three));

            void Hover(int x, int y) => typeof(Control).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(three, new object[] { new MouseEventArgs(MouseButtons.None, 0, x, y, 0) });
            Hover(F.Px(40), three.Height / 2);
            Assert.Equal(endless, three.HoverText);
            Hover(F.Px(2), F.Px(2)); // the card's margin
            Assert.Equal("", three.HoverText);
        }
        finally { F.Init(light: false, 1); }
    });
}
