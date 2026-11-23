using System.Text.RegularExpressions;

namespace Konnichiwatt.Core;

/// <summary>
/// What changed in each version, from CHANGELOG.md (embedded in the .exe and also copied into update.json).
/// Format: a "## 0.3.1" heading per version, then one "- Spanish text // English text" bullet per change.
/// </summary>
internal sealed partial class ReleaseNotes
{
    private readonly Dictionary<string, List<(string Es, string En)>> _byVersion = new();

    public static ReleaseNotes Embedded { get; } = LoadEmbedded();

    public static ReleaseNotes Parse(string? text)
    {
        var notes = new ReleaseNotes();
        if (string.IsNullOrWhiteSpace(text)) return notes;
        List<(string, string)>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var heading = Heading().Match(line);
            if (heading.Success)
            {
                notes._byVersion[Normalize(heading.Groups[1].Value)] = current = new();
                continue;
            }
            if (current == null || !line.StartsWith("- ")) continue;
            var parts = line[2..].Split(" // ", 2);
            current.Add((parts[0].Trim(), (parts.Length > 1 ? parts[1] : parts[0]).Trim()));
        }
        return notes;
    }

    /// <summary>Every version in the changelog, newest first.</summary>
    public IReadOnlyList<string> Versions =>
        _byVersion.Keys.OrderByDescending(v => Version.TryParse(v, out var p) ? p : new Version(0, 0)).ToList();

    /// <summary>The changes of <paramref name="version"/> in the current language (empty if unknown).</summary>
    public IReadOnlyList<string> For(string version) =>
        _byVersion.TryGetValue(Normalize(version), out var l) ? l.Select(n => L.Language == "es" ? n.Es : n.En).ToList() : Array.Empty<string>();

    /// <summary>The CHANGELOG.md section of one version, as published in update.json and on the release page.</summary>
    public static string Section(string changelog, string version)
    {
        var lines = changelog.Split('\n');
        var sb = new System.Text.StringBuilder();
        bool inside = false;
        foreach (var line in lines)
        {
            var heading = Heading().Match(line.Trim());
            if (heading.Success)
            {
                if (inside) break;
                inside = Normalize(heading.Groups[1].Value) == Normalize(version);
            }
            if (inside) sb.Append(line.TrimEnd('\r')).Append('\n');
        }
        return sb.ToString().Trim();
    }

    private static string Normalize(string v) =>
        Version.TryParse(v.TrimStart('v'), out var p) ? $"{p.Major}.{Math.Max(0, p.Minor)}.{Math.Max(0, p.Build)}" : v.Trim();

    private static ReleaseNotes LoadEmbedded()
    {
        try
        {
            using var s = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("Konnichiwatt.CHANGELOG.md");
            if (s == null) return new ReleaseNotes();
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        }
        catch (Exception e)
        {
            Log.Error("Release notes", e);
            return new ReleaseNotes();
        }
    }

    [GeneratedRegex(@"^##\s+v?(\d+\.\d+(?:\.\d+)?)\b")]
    private static partial Regex Heading();
}
