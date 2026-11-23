using Windows.Devices.Enumeration;
using Konnichiwatt.Core;

namespace Konnichiwatt.Devices;

/// <summary>
/// Any Bluetooth device whose battery Windows already knows (the percentage shown in
/// Settings › Bluetooth &amp; devices): BLE mice/keyboards/headphones with the Battery service,
/// hands-free headsets, Xbox controllers with BLE firmware…
/// Windows stores it in DEVPKEY_Bluetooth_Battery on the device node.
/// </summary>
public sealed class BluetoothProvider : IBatteryProvider
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.ContainerId";
    private const string ConnectedKey = "System.Devices.Connected";
    // AQS can't filter on a raw {fmtid} pid key, so we take present devices and filter in code.
    private const string PresentFilter = "System.Devices.Present:=System.StructuredQueryType.Boolean#True";

    public string Name => "Bluetooth (Windows)";
    public event Action? DevicesChanged { add { } remove { } }

    public IReadOnlyList<DeviceBattery> Poll()
    {
        var devices = DeviceInformation
            .FindAllAsync(PresentFilter, new[] { BatteryKey, ContainerKey }, DeviceInformationKind.Device)
            .AsTask().GetAwaiter().GetResult();

        var result = new List<DeviceBattery>();
        var containers = new HashSet<Guid>();
        foreach (var d in devices)
        {
            if (d.Properties.TryGetValue(BatteryKey, out var v) is false || v is not byte pct) continue;
            var container = d.Properties.TryGetValue(ContainerKey, out var c) && c is Guid g ? g : Guid.Empty;
            if (container != Guid.Empty && !containers.Add(container)) continue; // one entry per physical device

            string name = d.Name;
            if (container != Guid.Empty)
            {
                try
                {
                    var ci = DeviceInformation.CreateFromIdAsync($"{{{container}}}", new[] { ConnectedKey },
                        DeviceInformationKind.DeviceContainer).AsTask().GetAwaiter().GetResult();
                    if (ci.Properties.TryGetValue(ConnectedKey, out var con) && con is bool connected && !connected)
                        continue; // paired but not connected → stale value, hide it
                    if (!string.IsNullOrWhiteSpace(ci.Name)) name = ci.Name;
                }
                catch { /* keep device name */ }
            }

            result.Add(new DeviceBattery(
                Id: "bt:" + (container != Guid.Empty ? container.ToString() : d.Id),
                Name: name,
                Kind: GuessKind(name),
                Percent: Math.Min((int)pct, 100),
                State: ChargeState.Unknown,
                Source: "bluetooth",
                LastSeen: DateTime.Now));
        }
        return result;
    }

    private static DeviceKind GuessKind(string name)
    {
        bool Has(params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Has("controller", "gamepad", "mando", "xbox", "joy-con", "dualsense", "dualshock", "gamesir")) return DeviceKind.Controller;
        if (Has("mouse", "ratón", "raton", "mx master", "mx anywhere", "trackball")) return DeviceKind.Mouse;
        if (Has("keyboard", "teclado", "keys")) return DeviceKind.Keyboard;
        if (Has("head", "auricular", "buds", "airpods", "earbuds", "wh-", "wf-", "jbl", "bose", "cloud")) return DeviceKind.Headset;
        return DeviceKind.Other;
    }

    public void Dispose() { }
}
