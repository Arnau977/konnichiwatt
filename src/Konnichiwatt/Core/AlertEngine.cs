namespace Konnichiwatt.Core;

internal enum AlertKind { Low, Critical, WontLast, ChargeLimit, ChargeComplete }

/// <param name="Left">Estimated time left (<see cref="AlertKind.WontLast"/> only).</param>
/// <param name="Daily">Usual use per day (<see cref="AlertKind.WontLast"/> only).</param>
internal sealed record Alert(AlertKind Kind, DeviceBattery Device, TimeSpan? Left = null, TimeSpan? Daily = null);

/// <summary>
/// Decides which notifications are due after each poll. Each one fires once and is re-armed by charging (or by the
/// level recovering); changing the settings of one kind of alert re-evaluates only that kind.
/// </summary>
internal sealed class AlertEngine
{
    private const int Rearm = 5;

    private readonly AppSettings _settings;
    private readonly BatteryHistory _history;
    private readonly HashSet<string> _low = new(), _critical = new(), _chargingBelowLimit = new(), _chargingIncomplete = new();
    /// <summary>Level at which the device was announced as not lasting another day.</summary>
    private readonly Dictionary<string, int> _wontLast = new();
    private string _lowSignature, _criticalSignature;
    private bool _predictive;

    public AlertEngine(AppSettings settings, BatteryHistory history)
    {
        _settings = settings;
        _history = history;
        _lowSignature = LowSignature();
        _criticalSignature = CriticalSignature();
        _predictive = settings.PredictiveNotifications;
    }

    private string LowSignature() =>
        _settings.LowBatteryThreshold + ";" + string.Join(";", _settings.Customizations
            .Where(kv => kv.Value.LowThreshold != null).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.LowThreshold}"));

    private string CriticalSignature() => $"{_settings.CriticalNotifications};{_settings.CriticalThreshold}";

    private void ApplySettings()
    {
        if (LowSignature() is var low && low != _lowSignature) { _lowSignature = low; _low.Clear(); }
        if (CriticalSignature() is var critical && critical != _criticalSignature) { _criticalSignature = critical; _critical.Clear(); }
        if (_settings.PredictiveNotifications != _predictive) { _predictive = !_predictive; _wontLast.Clear(); }
    }

    /// <param name="devices">Every device, with the user's names. Hidden ones only get the charging alerts.</param>
    public List<Alert> Check(IEnumerable<DeviceBattery> devices)
    {
        ApplySettings();
        var due = new List<Alert>();
        foreach (var d in devices)
        {
            if (d.Stale) continue;
            if (!_settings.HiddenDevices.Contains(d.Id) && d.Percent is int p) CheckDraining(d, p, due);
            CheckCharging(d, due);
        }
        return due;
    }

    private void CheckDraining(DeviceBattery d, int p, List<Alert> due)
    {
        // Low and critical may be reached at once (first reading of the day): the critical one says it all.
        bool low = Crossed(_low, d, p, _settings.ThresholdFor(d.Id)) && _settings.LowBatteryNotifications;
        bool critical = Crossed(_critical, d, p, _settings.CriticalThreshold) && _settings.CriticalNotifications;
        if (critical) due.Add(new Alert(AlertKind.Critical, d));
        else if (low) due.Add(new Alert(AlertKind.Low, d));

        CheckWontLast(d, p, due);
    }

    /// <summary>True once when the level gets to the threshold; re-armed by charging or by climbing clearly above it.</summary>
    private static bool Crossed(HashSet<string> notified, DeviceBattery d, int p, int threshold)
    {
        if (p <= threshold && !d.IsCharging) return notified.Add(d.Id);
        if (d.IsCharging || p > threshold + Rearm) notified.Remove(d.Id);
        return false;
    }

    /// <summary>
    /// Less time left than the device is used on a usual day. Below the low-battery threshold that alert covers it, so
    /// this one stays quiet.
    /// </summary>
    private void CheckWontLast(DeviceBattery d, int p, List<Alert> due)
    {
        if (!_settings.PredictiveNotifications) return;
        if (d.IsCharging || _wontLast.TryGetValue(d.Id, out int at) && p > at + 2 * Rearm) { _wontLast.Remove(d.Id); if (d.IsCharging) return; }
        if (_wontLast.ContainsKey(d.Id)) return;
        bool lowCovers = _settings.LowBatteryNotifications && p <= _settings.ThresholdFor(d.Id);
        bool criticalCovers = _settings.CriticalNotifications && p <= _settings.CriticalThreshold;
        if (lowCovers || criticalCovers) return;
        if (_history.TimeLeft(d.Id) is TimeSpan left && _history.TypicalDailyUse(d.Id) is TimeSpan daily && left < daily)
        {
            _wontLast[d.Id] = p;
            due.Add(new Alert(AlertKind.WontLast, d, left, daily));
        }
    }

    /// <summary>
    /// Both only for a charge we saw from below: a device already past the limit (or full) when the app starts is not
    /// announced.
    /// </summary>
    private void CheckCharging(DeviceBattery d, List<Alert> due)
    {
        bool complete = d.State == ChargeState.Full || (d.IsCharging && d.Percent >= 100);
        if (d.IsCharging && !complete) _chargingIncomplete.Add(d.Id);
        else if (_chargingIncomplete.Remove(d.Id) && complete && _settings.ChargeCompleteNotifications)
            due.Add(new Alert(AlertKind.ChargeComplete, d));

        if (!d.IsCharging || complete) _chargingBelowLimit.Remove(d.Id);
        else if (d.Percent is int p)
        {
            if (p < _settings.ChargeLimit) _chargingBelowLimit.Add(d.Id);
            else if (_chargingBelowLimit.Remove(d.Id) && _settings.ChargeLimitNotifications)
                due.Add(new Alert(AlertKind.ChargeLimit, d));
        }
    }
}
