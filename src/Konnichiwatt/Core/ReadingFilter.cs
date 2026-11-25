namespace Konnichiwatt.Core;

/// <summary>
/// Holds back implausible readings. Some devices answer with a made-up level for a minute or so when they wake up
/// (a Cloud Alpha reported 50 % between 38 % and 37 %). A sudden jump while not charging is shown as the previous
/// level, dimmed, until the new one has lasted <see cref="Confirm"/>; meanwhile it reaches neither history nor alerts.
/// </summary>
internal sealed class ReadingFilter
{
    /// <summary>Largest change between two readings accepted straight away.</summary>
    public const int MaxJump = 5;
    /// <summary>How long a jump has to last to be believed.</summary>
    public static readonly TimeSpan Confirm = TimeSpan.FromMinutes(2);
    /// <summary>Without readings for this long, a pending jump starts over (the device was switched off).</summary>
    private static readonly TimeSpan PendingGap = TimeSpan.FromMinutes(3);
    /// <summary>Never hold a device back longer than this, even if its level keeps changing.</summary>
    private static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(5);

    private sealed record Pending(int Percent, DateTime Since, DateTime HeldSince, DateTime LastAt);

    private readonly Dictionary<string, int> _accepted = new();
    private readonly Dictionary<string, Pending> _pending = new();

    private readonly Action<string>? _log;

    public ReadingFilter(IEnumerable<DeviceBattery> lastKnown, Action<string>? log = null)
    {
        _log = log;
        foreach (var d in lastKnown)
            if (d.Percent is int p) _accepted[d.Id] = p;
    }

    /// <summary>Some reading is waiting to be confirmed: worth polling again soon.</summary>
    public bool HasPending(DateTime now) => _pending.Values.Any(p => now - p.LastAt <= PendingGap);

    public List<DeviceBattery> Apply(IEnumerable<DeviceBattery> devices, DateTime now) =>
        devices.Select(d => Check(d, now)).ToList();

    private DeviceBattery Check(DeviceBattery d, DateTime now)
    {
        if (d.Stale || d.Percent is not int p) return d;

        if (d.IsCharging || !_accepted.TryGetValue(d.Id, out int last) || Math.Abs(p - last) <= MaxJump)
            return Accept(d, p);

        if (!_pending.TryGetValue(d.Id, out var held) || now - held.LastAt > PendingGap)
            held = new Pending(p, now, now, now);
        else if (Math.Abs(p - held.Percent) > MaxJump)
            held = new Pending(p, now, held.HeldSince, now); // yet another level: start counting again
        else
            held = held with { LastAt = now };

        if (now - held.Since >= Confirm || now - held.HeldSince >= MaxHold) return Accept(d, p);

        if (held.Since == now) _log?.Invoke($"Unconfirmed reading: {d.Name} {last}% -> {p}%");
        _pending[d.Id] = held;
        return d with { Percent = last, Stale = true, Verifying = true };
    }

    private DeviceBattery Accept(DeviceBattery d, int percent)
    {
        _accepted[d.Id] = percent;
        _pending.Remove(d.Id);
        return d;
    }
}
