using System.Text;

namespace Konnichiwatt.Core;

/// <summary>Writes that can't leave a half-written file behind if the PC is switched off in the middle.</summary>
internal static class SafeFile
{
    private static readonly object Gate = new();

    /// <summary>Writes to a temporary file next to <paramref name="path"/>, flushes it to disk and swaps it in.</summary>
    public static void WriteAllText(string path, string text)
    {
        lock (Gate)
        {
            var tmp = path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(Encoding.UTF8.GetBytes(text));
                f.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Sets an unreadable file aside (<c>.corrupt</c>) so the next save doesn't erase what's left of it.</summary>
    public static void KeepCorrupt(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: true); }
        catch (Exception e) { Log.Error($"Keep corrupt {Path.GetFileName(path)}", e); }
    }
}
