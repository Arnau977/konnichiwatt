using System.Globalization;
using Konnichiwatt.Core;
using Konnichiwatt.UI.Fluent;

namespace Konnichiwatt.UI;

internal sealed partial class SettingsWindow
{
    private static readonly int[] HistoryDays = { 2, 7, 30 };
    private int _historyDays = 2;

    /// <summary>History page: period selector, then one chart + battery-life summary per device with data.</summary>
    private void BuildHistory()
    {
        var period = new Segmented(new[] { L.Days2, L.Days7, L.Days30 }, Math.Max(0, Array.IndexOf(HistoryDays, _historyDays)));
        period.SelectedChanged += (_, _) => { _historyDays = HistoryDays[period.Selected]; ShowPage(HistoryPage); };
        _content.Controls.Add(Card(HistoryGlyph, L.Period, null, period));

        var history = _cb.History;
        var now = DateTime.Now;
        var from = now - TimeSpan.FromDays(_historyDays);
        var devices = history == null ? new List<DeviceBattery>()
            : _s.Arrange(_cb.RawDevices()).Where(_s.IsShown).Where(d => history.For(d.Id).Count(s => s.Time >= from) >= 2).ToList();

        if (devices.Count == 0)
        {
            _content.Controls.Add(new NoteText(L.NoHistory, CardWidth));
            return;
        }
        foreach (var d in devices)
            _content.Controls.Add(new HistoryChartCard(d, history!.For(d.Id), from, now, CardWidth));
    }

    /// <summary>
    /// Made-up 30 days for the demo / screenshots: the PC is on from 9:00 to 24:00, each device drains at its own pace
    /// while on and goes on charge below 15 %.
    /// </summary>
    internal static BatteryHistory SampleHistory(IEnumerable<DeviceBattery> devices)
    {
        var h = new BatteryHistory();
        var rng = new Random(7);
        var now = DateTime.Now;
        int n = 0;
        foreach (var d in devices.Where(d => d.Percent != null))
        {
            double drain = new[] { 5.5, 1.2, 3.0, 0.4, 2.2 }[n++ % 5]; // % per hour
            var list = new List<BatterySample>();
            double level = 90;
            bool charging = false;
            for (var t = now.AddDays(-30); t <= now; t = t.AddMinutes(10))
            {
                if (t.Hour < 9) continue; // PC off at night
                if (charging) { level += 30 / 6.0; if (level >= 100) { level = 100; charging = false; } }
                else { level -= drain / 6.0 * (0.8 + rng.NextDouble() * 0.4); if (level < 15) charging = true; }
                list.Add(new BatterySample(t, (int)Math.Round(level), charging));
            }
            h.Replace(d.Id, BatteryHistory.Thin(list, now));
        }
        return h;
    }

    /// <summary>The running version's changes count as seen once this version's notes were opened (or announced).</summary>
    public static bool HasUnseenNotes(AppSettings s)
    {
        var version = Updater.CurrentVersion.ToString(3);
        return s.NotesSeenVersion != version && ReleaseNotes.Embedded.For(version).Count > 0;
    }

    /// <summary>"What's new" page: this version's changes first, then every earlier version in the changelog.</summary>
    private void BuildNews()
    {
        var current = Updater.CurrentVersion.ToString(3);
        var notes = ReleaseNotes.Embedded;
        foreach (var v in notes.Versions)
        {
            var lines = notes.For(v);
            if (lines.Count == 0) continue;
            _content.Controls.Add(new NotesCard(NotesGlyph, v == current ? L.WhatsNew(v) : L.VersionHeading(v), lines, CardWidth));
        }
        if (_s.NotesSeenVersion != current)
        {
            _s.NotesSeenVersion = current;
            Changed();
        }
        _nav[NewsPage].Badge = false;
    }
}

/// <summary>Plain secondary text spanning the content width (empty states, hints).</summary>
internal sealed class NoteText : Control
{
    public NoteText(string text, int width)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = F.Back;
        Size = new Size(width, F.Px(48));
        Margin = new Padding(0, F.Px(8), 0, F.Px(4));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        F.Prepare(e.Graphics);
        using var f = F.Text(13);
        using var b = new SolidBrush(F.Fg2);
        e.Graphics.DrawString(Text, f, b, new RectangleF(F.Pf(4), 0, Width - F.Pf(8), Height));
    }
}

/// <summary>Card with a title and a bulleted, word-wrapped list; its height fits the text.</summary>
internal sealed class NotesCard : Panel
{
    private readonly string _glyph, _title;
    private readonly IReadOnlyList<string> _lines;

    public NotesCard(string glyph, string title, IReadOnlyList<string> lines, int width)
    {
        _glyph = glyph;
        _title = title;
        _lines = lines;
        DoubleBuffered = true;
        BackColor = F.Back;
        Margin = new Padding(0, 0, 0, F.Px(4));
        Width = width;
        using var g = CreateGraphics();
        Height = (int)Math.Ceiling(Measure(g, draw: false));
    }

    private float TextLeft => F.Pf(54);

    private float Measure(Graphics g, bool draw)
    {
        using var tf = F.Text(14);
        using var bf = F.Text(13);
        using var fg = new SolidBrush(F.Fg);
        using var fg2 = new SolidBrush(F.Fg2);
        float y = F.Pf(16), w = Width - TextLeft - F.Pf(18);
        if (draw)
        {
            using var gf = F.Glyphs(16);
            F.DrawText(g, _glyph, gf, F.Fg, new RectangleF(F.Pf(18), y, F.Pf(20), F.Pf(20)));
            F.DrawText(g, _title, tf, F.Fg, new RectangleF(TextLeft, y, w, F.Pf(20)));
        }
        y += F.Pf(28);
        foreach (var line in _lines)
        {
            var size = g.MeasureString(line, bf, (int)(w - F.Pf(14)));
            if (draw)
            {
                g.DrawString("•", bf, fg2, TextLeft, y);
                g.DrawString(line, bf, fg2, new RectangleF(TextLeft + F.Pf(14), y, w - F.Pf(14), size.Height + 2));
            }
            y += size.Height + F.Pf(4);
        }
        return y + F.Pf(12);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        F.FillRound(g, F.Card, r, F.Pf(6));
        F.StrokeRound(g, F.Stroke, r, 1);
        Measure(g, draw: true);
    }
}

/// <summary>One device on the History page: level over the period (green while charging) and battery-life figures.</summary>
internal sealed class HistoryChartCard : Panel
{
    private readonly DeviceBattery _device;
    private readonly List<BatterySample> _samples;
    private readonly DateTime _from, _to;
    private readonly BatteryHistory.Stats _stats;

    public HistoryChartCard(DeviceBattery device, IReadOnlyList<BatterySample> samples, DateTime from, DateTime to, int width)
    {
        _device = device;
        _from = from;
        _to = to;
        // Keep the sample just before the window so the line starts at the left edge.
        var before = samples.LastOrDefault(s => s.Time < from);
        _samples = (before != null ? new[] { before } : Array.Empty<BatterySample>()).Concat(samples.Where(s => s.Time >= from)).ToList();
        _stats = BatteryHistory.Analyze(samples, from);
        DoubleBuffered = true;
        BackColor = F.Back;
        Margin = new Padding(0, 0, 0, F.Px(4));
        Size = new Size(width, F.Px(250));
    }

    private static CultureInfo Culture => CultureInfo.GetCultureInfo(L.Language == "es" ? "es-ES" : "en-US");

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        F.Prepare(g);
        var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        F.FillRound(g, F.Card, card, F.Pf(6));
        F.StrokeRound(g, F.Stroke, card, 1);

        // Header: icon, name, current level.
        using (var gf = F.Glyphs(16))
            F.DrawText(g, Theme.Glyph(_device.Kind), gf, F.Fg, new RectangleF(F.Pf(18), F.Pf(12), F.Pf(20), F.Pf(24)));
        using (var nf = F.Text(14, semibold: true))
            F.DrawText(g, _device.Name, nf, F.Fg, new RectangleF(F.Pf(54), F.Pf(12), Width - F.Pf(160), F.Pf(24)));
        using (var pf = F.Text(14))
            F.DrawText(g, Theme.PercentText(_device, true), pf, F.Fg2, new RectangleF(Width - F.Pf(118), F.Pf(12), F.Pf(100), F.Pf(24)), StringAlignment.Far);

        var plot = new RectangleF(F.Pf(54), F.Pf(48), Width - F.Pf(54) - F.Pf(22), F.Pf(120));
        DrawGrid(g, plot);
        DrawLine(g, plot);

        // Summary under the chart.
        using var sf = F.Text(12);
        using var sfb = F.Text(12, semibold: true);
        float y = plot.Bottom + F.Pf(26), w = Width - F.Pf(72);
        var life = _stats.PerCharge is TimeSpan t
            ? L.BatteryLifeValue(BatteryHistory.Amount(t), _stats.Discharges)
            : L.NotEnoughData;
        var label = L.BatteryLife + ":  ";
        float lw = g.MeasureString(label, sfb).Width;
        F.DrawText(g, label, sfb, F.Fg, new RectangleF(F.Pf(54), y, lw, F.Pf(20)));
        F.DrawText(g, life, sf, F.Fg2, new RectangleF(F.Pf(54) + lw, y, w - lw, F.Pf(20)));

        var parts = new List<string>();
        if (_stats.Charges > 0) parts.Add(L.ChargesCount(_stats.Charges));
        if (_stats.LastCharge is DateTime last) parts.Add(L.LastFullCharge(last.ToString("d MMM HH:mm", Culture)));
        if (parts.Count > 0)
            F.DrawText(g, string.Join("  ·  ", parts), sf, F.Fg2, new RectangleF(F.Pf(54), y + F.Pf(20), w, F.Pf(20)));
    }

    private float X(RectangleF plot, DateTime t) =>
        plot.Left + (float)((t - _from).TotalSeconds / (_to - _from).TotalSeconds) * plot.Width;

    private static float Y(RectangleF plot, int percent) => plot.Bottom - percent / 100f * plot.Height;

    private void DrawGrid(Graphics g, RectangleF plot)
    {
        using var grid = new Pen(Color.FromArgb(0x28, F.Fg), 1);
        using var lf = F.Text(10);
        foreach (var p in new[] { 0, 50, 100 })
        {
            float y = Y(plot, p);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            F.DrawText(g, p.ToString(), lf, F.Fg2, new RectangleF(plot.Left - F.Pf(40), y - F.Pf(8), F.Pf(32), F.Pf(16)), StringAlignment.Far);
        }

        // Time ticks: every 12 h over 2 days, every day over a week, every 5 days over a month (at local midnight/noon).
        var span = _to - _from;
        var step = span.TotalDays <= 3 ? TimeSpan.FromHours(12) : span.TotalDays <= 8 ? TimeSpan.FromDays(1) : TimeSpan.FromDays(5);
        var t = _from.Date;
        while (t < _from) t += step;
        while (t <= _to)
        {
            float x = X(plot, t);
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            var label = step < TimeSpan.FromDays(1)
                ? t.ToString(t.Hour == 0 ? "ddd d" : "HH:mm", Culture)
                : t.ToString(step == TimeSpan.FromDays(1) ? "ddd d" : "d MMM", Culture);
            F.DrawText(g, label, lf, F.Fg2, new RectangleF(x - F.Pf(40), plot.Bottom + F.Pf(4), F.Pf(80), F.Pf(16)), StringAlignment.Center);
            t += step;
        }
    }

    private void DrawLine(Graphics g, RectangleF plot)
    {
        using var line = new Pen(F.Accent, 2f * F.S) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        using var charge = new Pen(Theme.Charging(F.Light), 2f * F.S) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        var clip = g.Clip;
        g.SetClip(new RectangleF(plot.Left, plot.Top - F.Pf(3), plot.Width, plot.Height + F.Pf(6)));
        for (int i = 1; i < _samples.Count; i++)
        {
            var a = _samples[i - 1];
            var b = _samples[i];
            if (b.Time - a.Time > TimeSpan.FromHours(2)) continue; // no data in between (PC off): leave a gap
            g.DrawLine(a.Charging && b.Charging ? charge : line, X(plot, a.Time), Y(plot, a.Percent), X(plot, b.Time), Y(plot, b.Percent));
        }
        g.Clip = clip;
    }
}
