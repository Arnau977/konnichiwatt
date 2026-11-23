namespace Konnichiwatt.Core;

/// <summary>
/// Where the app keeps its files. Normally settings go to %APPDATA%\Konnichiwatt and everything else to
/// %LOCALAPPDATA%\Konnichiwatt. In portable mode (a <c>portable.txt</c> file next to the .exe) everything stays in a
/// <c>data</c> folder beside the .exe and nothing is written elsewhere.
/// </summary>
internal static class AppPaths
{
    public const string PortableMarker = "portable.txt";

    public static string? ExeDirectory { get; } = Path.GetDirectoryName(Environment.ProcessPath);

    public static bool IsPortable { get; } = DetectPortable();

    /// <summary>Log, last readings, history, downloaded updates.</summary>
    public static string DataDirectory { get; } = IsPortable
        ? Path.Combine(ExeDirectory!, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Konnichiwatt");

    public static string SettingsDirectory { get; } = IsPortable
        ? DataDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Konnichiwatt");

    private static bool DetectPortable()
    {
        try { return ExeDirectory != null && File.Exists(Path.Combine(ExeDirectory, PortableMarker)); }
        catch { return false; }
    }
}
