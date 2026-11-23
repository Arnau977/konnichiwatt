namespace Konnichiwatt.Core;

internal static class Log
{
    private static readonly object Gate = new();
    public static readonly string Directory =
        AppPaths.DataDirectory;
    private static readonly string FilePath = Path.Combine(Directory, "konnichiwatt.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }

    public static void Error(string context, Exception e) => Write($"[ERROR] {context}: {e.GetType().Name}: {e.Message}");
}
