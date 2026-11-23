using Windows.Gaming.Input;
using Konnichiwatt.Core;
using PowerStatus = Windows.System.Power.BatteryStatus;

namespace Konnichiwatt.Devices;

/// <summary>
/// Xbox (and any XInput / GIP) controllers through Windows.Gaming.Input – the same API the Xbox Game Bar uses.
/// Controllers without a battery (wired pads, Steam virtual pads) report NotPresent and are skipped.
/// </summary>
public sealed class XboxProvider : IBatteryProvider
{
    private static readonly HashSet<ushort> SkipVendors = new() { 0x3537 /* GameSir */, 0x057E /* Nintendo */, 0x054C /* Sony */ };

    public string Name => "Xbox (Windows.Gaming.Input)";
    public event Action? DevicesChanged;

    public XboxProvider()
    {
        try
        {
            Gamepad.GamepadAdded += OnChanged;
            Gamepad.GamepadRemoved += OnChanged;
            _ = Gamepad.Gamepads; // first access starts enumeration
        }
        catch (Exception e) { Log.Error("Windows.Gaming.Input init", e); }
    }

    private void OnChanged(object? sender, Gamepad e) => DevicesChanged?.Invoke();

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var gp in Gamepad.Gamepads)
        {
            var report = gp.TryGetBatteryReport();
            if (report == null || report.Status == PowerStatus.NotPresent) continue;

            int? pct = null;
            if (report.FullChargeCapacityInMilliwattHours is int full && full > 0 &&
                report.RemainingCapacityInMilliwattHours is int rem)
                pct = Math.Clamp((int)Math.Round(rem * 100.0 / full), 0, 100);

            var raw = RawGameController.FromGameController(gp);
            // Third-party pads emulating an Xbox 360 report a fake 100 %; they have their own providers.
            if (raw != null && SkipVendors.Contains(raw.HardwareVendorId)) continue;
            var name = string.IsNullOrWhiteSpace(raw?.DisplayName) ? "Mando Xbox" : raw!.DisplayName;
            var id = "xbox:" + (raw?.NonRoamableId ?? name);

            var state = report.Status switch
            {
                PowerStatus.Charging => ChargeState.Charging,
                PowerStatus.Idle => pct >= 100 ? ChargeState.Full : ChargeState.Discharging,
                _ => ChargeState.Discharging,
            };
            result.Add(new DeviceBattery(id, name, DeviceKind.Controller, pct, state, "xbox", LastSeen: DateTime.Now));
        }
        return result;
    }

    public void Dispose()
    {
        try
        {
            Gamepad.GamepadAdded -= OnChanged;
            Gamepad.GamepadRemoved -= OnChanged;
        }
        catch { }
    }
}
