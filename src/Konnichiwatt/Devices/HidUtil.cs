using System.Diagnostics;
using HidSharp;

namespace Konnichiwatt.Devices;

internal static class HidUtil
{
    /// <summary>Reads input reports until one matches or the deadline passes. Other reports are discarded.</summary>
    public static byte[]? ReadUntil(HidStream stream, Func<byte[], bool> match, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int left = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (left <= 0) return null;
            stream.ReadTimeout = left;
            byte[] r;
            try { r = stream.Read(); }
            catch (TimeoutException) { return null; }
            catch (IOException) { return null; }
            if (r.Length > 0 && match(r)) return r;
        }
    }

    public static HidStream? TryOpen(HidDevice device)
    {
        try
        {
            return device.TryOpen(out var s) ? s : null;
        }
        catch { return null; }
    }

    public static int OutLen(HidDevice d) { try { return d.GetMaxOutputReportLength(); } catch { return 0; } }
    public static int InLen(HidDevice d) { try { return d.GetMaxInputReportLength(); } catch { return 0; } }
    public static int FeatureLen(HidDevice d) { try { return d.GetMaxFeatureReportLength(); } catch { return 0; } }

    /// <summary>Top-level collection usage as (page &lt;&lt; 16 | usage), or 0.</summary>
    public static uint TopUsage(HidDevice d)
    {
        try { return d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).FirstOrDefault(); }
        catch { return 0; }
    }

    /// <summary>USB interface number parsed from the device path (&amp;mi_XX), or -1.</summary>
    public static int Interface(HidDevice d)
    {
        var m = System.Text.RegularExpressions.Regex.Match(d.DevicePath, "&mi_([0-9a-f]{2})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;
    }

    public static bool IsBluetooth(HidDevice d) =>
        d.DevicePath.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ||
        d.DevicePath.Contains("bthledevice", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the collection uses numbered reports. Windows always prefixes a report-ID byte
    /// (0 when unnumbered); hidapi strips that 0, so protocols documented from hidapi code need it removed.</summary>
    public static bool UsesReportIds(HidDevice d)
    {
        try { return d.GetReportDescriptor().Reports.Any(r => r.ReportID != 0); }
        catch { return true; }
    }

    /// <summary>
    /// Writes <paramref name="request"/> (first byte = report ID, hidapi convention) zero-padded to the output
    /// report length, then reads until <paramref name="match"/> accepts a reply. Replies are returned in hidapi
    /// form: without the leading 0 byte for unnumbered reports.
    /// </summary>
    public static byte[]? Transact(HidStream s, HidDevice d, byte[] request, Func<byte[], bool> match, int timeoutMs = 1000)
    {
        int outLen = OutLen(d);
        if (outLen <= 0) return null;
        var buf = new byte[outLen];
        Array.Copy(request, buf, Math.Min(request.Length, outLen));
        try { s.Write(buf); }
        catch { return null; }
        bool strip = !UsesReportIds(d);
        return ReadUntil(s, r =>
        {
            var n = strip && r.Length > 0 ? r[1..] : r;
            return match(n);
        }, timeoutMs) is byte[] raw ? (strip ? raw[1..] : raw) : null;
    }

    /// <summary>FNV-1a; unlike string.GetHashCode it is stable across runs (used in persisted device ids).</summary>
    public static string StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s.ToLowerInvariant()) { h ^= c; h *= 16777619; }
        return h.ToString("X8");
    }

    public static string SafeProductName(HidDevice d, string fallback)
    {
        try
        {
            var n = d.GetProductName();
            return string.IsNullOrWhiteSpace(n) ? fallback : n.Trim();
        }
        catch { return fallback; }
    }
}
