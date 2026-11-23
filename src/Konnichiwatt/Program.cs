using Konnichiwatt.Core;

namespace Konnichiwatt;

internal static class Program
{
    public static IBatteryProvider[] CreateProviders() => new IBatteryProvider[]
    {
        new Devices.HyperXProvider(),
        new Devices.LogitechProvider(),
        new Devices.XboxProvider(),
        new Devices.SwitchProvider(),
        new Devices.GameSirProvider(),
        new Devices.PlayStationProvider(),
        new Devices.RazerProvider(),
        new Devices.SteelSeriesProvider(),
        new Devices.CorsairHeadsetProvider(),
        new Devices.BluetoothProvider(),
    };

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--diag")
        {
            Thread.Sleep(1500); // let Windows.Gaming.Input finish enumerating
            using var m = new DeviceMonitor(CreateProviders(), TimeSpan.FromMinutes(1));
            File.WriteAllText(args[1], m.BuildDiagnostics());
            return;
        }

        if (args.Length == 2 && args[0] == "--preview")
        {
            UI.PreviewRenderer.Save(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--make-icon")
        {
            // Regenerates Assets\konnichiwatt.ico (the .exe icon) from the same drawing code as the tray bolt.
            UI.TrayIconRenderer.SaveAppIcon(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--tray-preview")
        {
            UI.PreviewRenderer.SaveTrayIcons(args[1]);
            return;
        }
        if (args.Length == 1 && args[0] == "--settings-demo")
        {
            // Interactive settings window with sample devices and throw-away settings (nothing is saved).
            ApplicationConfiguration.Initialize();
            var samples = UI.PreviewRenderer.Samples();
            Application.Run(new UI.SettingsWindow(new AppSettings(),
                new UI.SettingsWindow.Callbacks(() => samples, () => { }, () => { }, () => { }, () => { }), null));
            return;
        }
        if (args.Length == 2 && args[0] == "--settings-shot")
        {
            ApplicationConfiguration.Initialize();
            UI.SettingsWindow.SaveScreenshots(args[1], new AppSettings());
            return;
        }

        using var mutex = new Mutex(true, @"Local\Konnichiwatt.SingleInstance", out bool first);
        if (!first) return;

        AppSettings.MigrateFromOldName();
        Startup.MigrateFromOldName();

        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"[FATAL] {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // dark context menus follow the Windows theme
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        Application.Run(new TrayApplicationContext(openSettings: args.Contains("--settings")));
    }
}
