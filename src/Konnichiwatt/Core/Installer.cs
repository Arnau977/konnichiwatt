using System.Diagnostics;
using Microsoft.Win32;

namespace Konnichiwatt.Core;

/// <summary>
/// Per-user install, like Discord or VS Code: no administrator rights. The .exe copies itself to
/// %LOCALAPPDATA%\Programs\Konnichiwatt, adds a Start menu shortcut and an entry in Settings › Apps › Installed apps
/// (with "Uninstall"). The installed copy keeps itself up to date with <see cref="Updater"/>.
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Konnichiwatt";
    public const string RepoUrl = "https://github.com/Arnau977/konnichiwatt";

    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Konnichiwatt");

    public static string InstalledExe => Path.Combine(InstallDirectory, "Konnichiwatt.exe");

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Konnichiwatt.lnk");

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static bool IsInstalledCopy => SamePath(Environment.ProcessPath, InstalledExe);

    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static Version? InstalledVersion
    {
        get
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(InstalledExe);
                return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
            }
            catch { return null; }
        }
    }

    public static Version ThisVersion => new(Updater.CurrentVersion.Major, Updater.CurrentVersion.Minor, Math.Max(0, Updater.CurrentVersion.Build));

    /// <summary>What a normal launch (no command-line options) of this copy should do.</summary>
    public enum LaunchAction { Run, OfferInstall, OfferUpdate, OpenInstalled }

    public static LaunchAction Decide() =>
        Decide(Updater.IsSingleFile, AppPaths.IsPortable, IsInstalledCopy, IsInstalled ? InstalledVersion : null, ThisVersion,
               SamePath(SafeRegisteredStartup(), Environment.ProcessPath));

    /// <param name="startsWithWindows">This exact copy is the one Windows launches at sign-in: an existing setup
    /// from before the installer existed, which must keep working as it is.</param>
    internal static LaunchAction Decide(bool singleFile, bool portable, bool isInstalledCopy, Version? installed,
                                        Version current, bool startsWithWindows)
    {
        if (!singleFile || portable || isInstalledCopy || startsWithWindows) return LaunchAction.Run;
        if (installed == null) return LaunchAction.OfferInstall;
        return current > installed ? LaunchAction.OfferUpdate : LaunchAction.OpenInstalled;
    }

    private static string? SafeRegisteredStartup()
    {
        try { return Startup.RegisteredExe; } catch { return null; }
    }

    /// <summary>Copies this .exe into place, registers it and turns "start with Windows" on/off. Runs off the UI thread.</summary>
    public static void Install(bool startWithWindows)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
        AppInstance.StopRunning();

        Directory.CreateDirectory(InstallDirectory);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Copy(source, InstalledExe, overwrite: true); break; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(250); } // the old copy is still exiting
        }
        try { File.Delete(InstalledExe + ".old"); } catch { /* not there */ }

        Register(createShortcut: true);
        Startup.Set(startWithWindows, InstalledExe);
        Log.Write($"Installed {ThisVersion.ToString(3)} from {source}");
    }

    /// <summary>Starts the installed copy (it opens the settings if one is already running).</summary>
    public static void LaunchInstalled(string arguments = "") =>
        Process.Start(new ProcessStartInfo(InstalledExe, arguments) { UseShellExecute = false, WorkingDirectory = InstallDirectory });

    /// <summary>
    /// Called by the installed copy at every start: keeps the "Installed apps" entry in step with the version after
    /// self-updates, and registers copies that were put in place by hand before the installer existed.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!IsInstalledCopy || !Updater.IsSingleFile) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            var registered = key?.GetValue("DisplayVersion") as string;
            if (registered == ThisVersion.ToString(3)) return;
            Register(createShortcut: key == null);
        }
        catch (Exception e) { Log.Error("Register install", e); }
    }

    private static void Register(bool createShortcut)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "Konnichiwatt");
            key.SetValue("DisplayVersion", ThisVersion.ToString(3));
            key.SetValue("Publisher", "Konnichiwatt");
            key.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
            key.SetValue("InstallLocation", InstallDirectory);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            key.SetValue("URLInfoAbout", RepoUrl);
            key.SetValue("HelpLink", RepoUrl);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); }
            catch { /* size is cosmetic */ }
        }
        if (createShortcut)
        {
            try { CreateShortcut(ShortcutPath, InstalledExe); }
            catch (Exception e) { Log.Error("Start menu shortcut", e); }
        }
    }

    private static void CreateShortcut(string lnk, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell missing");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(lnk);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "Konnichiwatt";
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>
    /// Removes the app: closes it, deletes the startup entry, the shortcut and the "Installed apps" entry, optionally the
    /// settings and history, and finally the program folder (from a short-lived helper, since this .exe is running).
    /// </summary>
    public static void Uninstall(bool deleteData)
    {
        AppInstance.StopRunning();
        try
        {
            if (Startup.RegisteredExe is string s && (SamePath(s, InstalledExe) || !File.Exists(s))) Startup.Set(false, InstalledExe);
        }
        catch (Exception e) { Log.Error("Uninstall startup", e); }
        try { File.Delete(ShortcutPath); } catch (Exception e) { Log.Error("Uninstall shortcut", e); }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
        catch (Exception e) { Log.Error("Uninstall registry", e); }

        if (deleteData)
        {
            foreach (var dir in new[] { AppPaths.SettingsDirectory, AppPaths.DataDirectory })
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
        else Log.Write("Uninstalled (settings kept)");

        if (Directory.Exists(InstallDirectory))
        {
            if (IsInstalledCopy)
            {
                // This .exe stays locked until the process exits (the window may still be open for a while), so a
                // helper retries every second for up to ~30 s. ping is the classic "sleep" that also works without a
                // console (timeout.exe refuses redirected input).
                var dir = InstallDirectory;
                Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c for /l %i in (1,1,30) do @(ping 127.0.0.1 -n 2 >nul & rmdir /s /q \"{dir}\" 2>nul & if not exist \"{dir}\" exit)")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            else
            {
                try { Directory.Delete(InstallDirectory, recursive: true); } catch (Exception e) { Log.Error("Uninstall folder", e); }
            }
        }
    }
}

/// <summary>
/// Talks to the running copy of the app through named events: "show yourself" (a second launch opens the settings
/// window instead of doing nothing) and "quit" (so the installer can replace or remove the .exe).
/// </summary>
internal static class AppInstance
{
    public const string MutexName = @"Local\Konnichiwatt.SingleInstance";
    private const string ShowEventName = @"Local\Konnichiwatt.Show";
    private const string QuitEventName = @"Local\Konnichiwatt.Quit";

    /// <summary>Asks the running copy to open its settings window. False if none is listening.</summary>
    public static bool SignalShow()
    {
        if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) return false;
        using (ev) ev.Set();
        return true;
    }

    public static bool IsRunning()
    {
        using var mutex = new Mutex(false, MutexName);
        return !TryTake(mutex, TimeSpan.Zero);
    }

    /// <summary>Asks the running copy to exit and waits for it; kills older versions that don't listen.</summary>
    public static void StopRunning()
    {
        using var mutex = new Mutex(false, MutexName);
        if (TryTake(mutex, TimeSpan.Zero)) return;
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var ev))
            using (ev) ev.Set();
        if (TryTake(mutex, TimeSpan.FromSeconds(8))) return;

        // Versions before 0.3.1 have no quit event.
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("Konnichiwatt"))
        {
            using (p)
            {
                if (p.Id == self) continue;
                try { p.Kill(); p.WaitForExit(5000); Log.Write($"Closed running copy (pid {p.Id})"); }
                catch (Exception e) { Log.Error("Close running copy", e); }
            }
        }
        TryTake(mutex, TimeSpan.FromSeconds(5));
    }

    private static bool TryTake(Mutex mutex, TimeSpan wait)
    {
        try
        {
            if (!mutex.WaitOne(wait)) return false;
        }
        catch (AbandonedMutexException) { }
        mutex.ReleaseMutex();
        return true;
    }

    /// <summary>Used by the running copy: calls <paramref name="show"/> / <paramref name="quit"/> on the UI thread.</summary>
    public static IDisposable Listen(Action show, Action quit)
    {
        var ctx = SynchronizationContext.Current ?? throw new InvalidOperationException("Needs a UI thread");
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var r1 = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ctx.Post(_ => show(), null), null, -1, executeOnlyOnce: false);
        var r2 = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => ctx.Post(_ => quit(), null), null, -1, executeOnlyOnce: true);
        return new Listener(() =>
        {
            r1.Unregister(null);
            r2.Unregister(null);
            showEvent.Dispose();
            quitEvent.Dispose();
        });
    }

    private sealed class Listener(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
