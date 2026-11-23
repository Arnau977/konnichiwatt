using System.Text.Json.Serialization;

namespace Konnichiwatt.Core;

[JsonConverter(typeof(JsonStringEnumConverter<DeviceKind>))]
public enum DeviceKind { Headset, Mouse, Keyboard, Controller, Speaker, Other }

public enum ChargeState { Unknown, Discharging, Charging, Full }

/// <summary>Snapshot of one wireless device's battery.</summary>
/// <param name="Id">Stable identifier (used for hiding devices and low-battery alerts).</param>
/// <param name="Percent">0-100, or null if the device does not report a level.</param>
/// <param name="Approximate">The device only reports coarse steps (e.g. Switch controllers).</param>
/// <param name="Stale">Device stopped answering (probably asleep); this is the last known value.</param>
public sealed record DeviceBattery(
    string Id,
    string Name,
    DeviceKind Kind,
    int? Percent,
    ChargeState State,
    string Source,
    bool Approximate = false,
    bool Stale = false,
    DateTime? LastSeen = null)
{
    public bool IsCharging => State is ChargeState.Charging or ChargeState.Full;
}

public interface IBatteryProvider : IDisposable
{
    string Name { get; }

    /// <summary>Called on a background thread. Must not throw for "device off" situations.</summary>
    IReadOnlyList<DeviceBattery> Poll();

    /// <summary>Raised when the provider detects that devices were added/removed.</summary>
    event Action? DevicesChanged;
}

/// <summary>Providers that can show a device's last known value while it sleeps get the readings saved by the previous run.</summary>
public interface IRestorable
{
    void Restore(IEnumerable<DeviceBattery> lastKnown);
}
