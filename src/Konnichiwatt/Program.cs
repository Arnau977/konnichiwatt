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
        if (args.Length == 2 && args[0] == "--popup-preview")
        {
            ApplicationConfiguration.Initialize();
            UI.PreviewRenderer.SavePopup(args[1]);
            return;
        }
        if (args.Length == 1 && args[0] == "--settings-demo")
        {
            // Interactive settings window with sample devices and throw-away settings (nothing is saved).
            ApplicationConfiguration.Initialize();
            var samples = UI.PreviewRenderer.Samples();
            Application.Run(new UI.SettingsWindow(new AppSettings(),
                new UI.SettingsWindow.Callbacks(() => samples, UI.SettingsWindow.SampleEstimate, () => { }, () => { }, () => { }, () => { }), null));
            return;
        }
        if (args.Length >= 2 && args[0] == "--settings-shot")
        {
            if (args.Length > 2) L.Init(args[2]); // optional language: es / en
            ApplicationConfiguration.Initialize();
            UI.SettingsWindow.SaveScreenshots(args[1], new AppSettings());
            return;
        }

        if (args.Length >= 2 && args[0] == "--setup-shot")
        {
            if (args.Length > 2) L.Init(args[2]);
            ApplicationConfiguration.Initialize();
            UI.SetupWindow.SaveScreenshots(args[1]);
            return;
        }

        if (args.Contains("--uninstall"))
        {
            if (args.Contains("--quiet")) Installer.Uninstall(deleteData: false);
            else
            {
                L.Init(AppSettings.Load().Language);
                ApplicationConfiguration.Initialize();
                UI.SetupWindow.RunUninstall();
            }
            return;
        }

        // A plain double-click on a downloaded copy: offer to install it (or update the installed one).
        if (args.Length == 0)
        {
            var action = Installer.Decide();
            if (action == Installer.LaunchAction.OpenInstalled)
            {
                if (!AppInstance.SignalShow()) Installer.LaunchInstalled();
                return;
            }
            if (action is Installer.LaunchAction.OfferInstall or Installer.LaunchAction.OfferUpdate)
            {
                L.Init(AppSettings.Load().Language);
                ApplicationConfiguration.Initialize();
                if (!UI.SetupWindow.RunInstall(update: action == Installer.LaunchAction.OfferUpdate)) return;
                // "Run without installing": carry on as a normal launch.
            }
        }

        // Started by the updater: the previous version is still shutting down and holds the single-instance lock.
        int updated = Array.IndexOf(args, "--updated");
        string? updatedFrom = updated >= 0 && updated + 1 < args.Length ? args[updated + 1] : null;

        using var mutex = new Mutex(false, AppInstance.MutexName);
        bool owned;
        try { owned = mutex.WaitOne(updatedFrom != null || args.Contains("--installed") ? TimeSpan.FromSeconds(20) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; } // previous instance ended without releasing it
        if (!owned)
        {
            if (updatedFrom == null) AppInstance.SignalShow(); // already running: bring up its settings instead
            return;
        }
        if (updatedFrom != null) Updater.CleanUpAfterUpdate();

        if (!AppPaths.IsPortable)
        {
            AppSettings.MigrateFromOldName();
            Startup.MigrateFromOldName();
        }
        Installer.EnsureRegistered();

        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"[FATAL] {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // dark context menus follow the Windows theme
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        try
        {
            Application.Run(new TrayApplicationContext(openSettings: args.Contains("--settings"), updatedFrom: updatedFrom,
                                                       justInstalled: args.Contains("--installed")));
        }
        finally
        {
            mutex.ReleaseMutex(); // let an updated version start right away
        }
    }
}
