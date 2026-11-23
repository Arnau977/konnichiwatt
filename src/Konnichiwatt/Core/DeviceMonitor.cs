using System.Diagnostics;
using System.Text;

namespace Konnichiwatt.Core;

/// <summary>
/// Polls every provider on a dedicated background thread and publishes the merged list
/// on the UI thread. Device hot-plug events trigger an early (debounced) poll.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    private readonly IBatteryProvider[] _providers;
    private readonly SynchronizationContext _ui;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly System.Threading.Timer _debounce;
    private readonly object _pollLock = new();
    private readonly ReadingStore _store = ReadingStore.Load();
    private volatile bool _stopping;

    public TimeSpan Interval { get; set; }
    public IReadOnlyList<DeviceBattery> Current { get; private set; } = Array.Empty<DeviceBattery>();
    public event Action<IReadOnlyList<DeviceBattery>>? Updated;

    public DeviceMonitor(IEnumerable<IBatteryProvider> providers, TimeSpan interval)
    {
        _providers = providers.ToArray();
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Interval = interval;
        _debounce = new System.Threading.Timer(_ => _wake.Set());
        foreach (var p in _providers) p.DevicesChanged += RequestPollSoon;
        foreach (var r in _providers.OfType<IRestorable>())
        {
            try { r.Restore(_store.All); }
            catch (Exception e) { Log.Error("Restore readings", e); }
        }
        HidSharp.DeviceList.Local.Changed += (_, _) => RequestPollSoon();
        _thread = new Thread(Loop) { IsBackground = true, Name = "BatteryPoll", Priority = ThreadPriority.BelowNormal };
    }

    public void Start() => _thread.Start();

    /// <summary>Persists the last readings now and skips doing it again on dispose (used right before self-update).</summary>
    public void SaveStateNow()
    {
        _store.Save();
        _stateSaved = true;
    }

    private bool _stateSaved;

    public void RequestPoll() => _wake.Set();

    /// <summary>Several hot-plug events usually arrive in bursts; wait for them to settle.</summary>
    public void RequestPollSoon() => _debounce.Change(1500, Timeout.Infinite);

    private void Loop()
    {
        while (!_stopping)
        {
            var list = PollAll(null);
            Current = list;
            _store.Update(list);
            _ui.Post(_ => { if (!_stopping) Updated?.Invoke(list); }, null);
            _wake.WaitOne(Interval);
        }
    }

    private List<DeviceBattery> PollAll(StringBuilder? diag)
    {
        lock (_pollLock)
        {
            var all = new List<DeviceBattery>();
            foreach (var p in _providers)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var r = p.Poll();
                    all.AddRange(r);
                    diag?.AppendLine($"[{p.Name}] {r.Count} dispositivo(s) en {sw.ElapsedMilliseconds} ms");
                    if (diag != null) foreach (var d in r) diag.AppendLine($"    {d}");
                }
                catch (Exception e)
                {
                    Log.Error($"Provider {p.Name}", e);
                    diag?.AppendLine($"[{p.Name}] ERROR tras {sw.ElapsedMilliseconds} ms: {e}");
                }
            }
            return Merge(all);
        }
    }

    /// <summary>The same physical device can show up through several providers; keep the most specific one.</summary>
    private static List<DeviceBattery> Merge(List<DeviceBattery> all)
    {
        bool hasXbox = all.Any(d => d.Source == "xbox");
        bool hasSwitch = all.Any(d => d.Source == "switch");
        bool hasPs = all.Any(d => d.Source == "playstation");
        bool Has(DeviceBattery d, string s) => d.Name.Contains(s, StringComparison.OrdinalIgnoreCase);
        all.RemoveAll(d => d.Source == "bluetooth" && (
            (hasXbox && Has(d, "Xbox")) ||
            (hasSwitch && (Has(d, "Pro Controller") || Has(d, "Joy-Con"))) ||
            (hasPs && (Has(d, "Wireless Controller") || Has(d, "DualSense") || Has(d, "DualShock")))));

        return all
            .GroupBy(d => d.Id).Select(g => g.First())
            .OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Konnichiwatt - diagnóstico {DateTime.Now}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine("== Dispositivos HID ==");
        try
        {
            foreach (var d in HidSharp.DeviceList.Local.GetHidDevices().OrderBy(d => d.VendorID).ThenBy(d => d.ProductID))
            {
                string name;
                try { name = d.GetProductName(); } catch { name = "?"; }
                sb.AppendLine($"{d.VendorID:X4}:{d.ProductID:X4} in={d.GetMaxInputReportLength(),-3} out={d.GetMaxOutputReportLength(),-3} {name,-40} {d.DevicePath}");
            }
        }
        catch (Exception e) { sb.AppendLine(e.ToString()); }
        sb.AppendLine();
        sb.AppendLine("== Proveedores ==");
        var merged = PollAll(sb);
        sb.AppendLine();
        sb.AppendLine("== Resultado final ==");
        foreach (var d in merged) sb.AppendLine(d.ToString());
        return sb.ToString();
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (!_stateSaved) _store.Save();
        _debounce.Dispose();
        foreach (var p in _providers)
        {
            try { p.Dispose(); } catch { }
        }
    }
}
