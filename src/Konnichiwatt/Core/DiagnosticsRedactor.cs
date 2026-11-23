using System.Text.RegularExpressions;

namespace Konnichiwatt.Core;

/// <summary>
/// Removes hardware addresses from a diagnostics report before the user attaches it to a public GitHub issue:
/// Bluetooth MAC addresses appear in device paths (as 12 hex digits) and sometimes as AA:BB:CC:DD:EE:FF.
/// Vendor/product IDs and interface numbers, which are what's needed to add support, are kept.
/// </summary>
internal static partial class DiagnosticsRedactor
{
    public static string Redact(string text) =>
        Colons().Replace(Bare().Replace(text, "xxxxxxxxxxxx"), "xx:xx:xx:xx:xx:xx");

    // Not preceded by "-": the last group of a GUID (…-00805f9b34fb) is also 12 hex digits and must stay.
    [GeneratedRegex(@"(?<![0-9a-fA-F-])[0-9a-fA-F]{12}(?![0-9a-fA-F])")]
    private static partial Regex Bare();

    [GeneratedRegex(@"(?<![0-9a-fA-F:-])(?:[0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}(?![0-9a-fA-F:-])")]
    private static partial Regex Colons();
}
