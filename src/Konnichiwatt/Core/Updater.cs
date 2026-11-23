using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Konnichiwatt.Core;

/// <summary>Contents of update.json, published next to the new .exe.</summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("notes")] string? Notes = null);

/// <summary>
/// Self-update for the single-file build. The manifest URL is baked in at publish time
/// (<c>-p:UpdateManifestUrl=…</c>); without it, updates are disabled.
/// Flow: read update.json → newer version? → download the .exe → verify SHA-256 → rename the running .exe to
/// .old (Windows allows renaming a running file) → put the new one in its place → start it with --updated → exit.
/// The new process deletes the .old file and announces the update.
/// </summary>
public sealed class Updater
{
    /// <summary>Stable channel: update.json of the latest full release.</summary>
    public static string? ManifestUrl { get; } = Metadata("UpdateManifestUrl");

    /// <summary>Beta channel: update.json of the newest build, including pre-releases. Falls back to stable.</summary>
    public static string? BetaManifestUrl { get; } = Metadata("UpdateManifestUrlBeta") ?? ManifestUrl;

    private static string? Metadata(string key) => typeof(Updater).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;

    public static Version CurrentVersion { get; } = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Updating replaces the .exe file, which only makes sense for the single-file published build.</summary>
    public static bool CanSelfUpdate => ManifestUrl != null && IsSingleFile;

    /// <summary>True for the published single .exe; false when running from a build folder (development).</summary>
    public static bool IsSingleFile =>
#pragma warning disable IL3000 // intentional: an empty Location is exactly how we detect the single-file build
        string.IsNullOrEmpty(typeof(Updater).Assembly.Location) && Environment.ProcessPath != null;
#pragma warning restore IL3000

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public UpdateManifest? Available { get; private set; }
    public DateTime? LastCheck { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Downloads and parses the manifest; sets <see cref="Available"/> if it's newer than this build.</summary>
    /// <param name="beta">Also take pre-releases (the beta channel).</param>
    /// <param name="skipVersion">A version that already failed to start here (rolled back): never offered again.</param>
    public async Task<UpdateManifest?> CheckAsync(bool beta = false, string? skipVersion = null, CancellationToken ct = default)
    {
        var url = beta ? BetaManifestUrl : ManifestUrl;
        if (url == null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var json = await Http.GetStringAsync(url, cts.Token);
            var m = JsonSerializer.Deserialize<UpdateManifest>(json);
            LastCheck = DateTime.Now;
            LastError = null;
            Available = IsAcceptable(m, CurrentVersion, skipVersion) ? m : null;
            return Available;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Log.Error("Update check", e);
            return null;
        }
    }

    internal static bool IsAcceptable(UpdateManifest? m, Version current, string? skipVersion) =>
        m != null && IsNewer(m.Version, current) && IsSafeUrl(m.Url) &&
        !(skipVersion != null && Version.TryParse(skipVersion, out var s) && Version.TryParse(m.Version, out var v) &&
          Normalize(s) == Normalize(v));

    internal static bool IsNewer(string candidate, Version current) =>
        Version.TryParse(candidate, out var v) && Normalize(v) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), 0);

    internal static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback && TestMode));

    /// <summary>
    /// End-to-end test hook (KONNICHIWATT_UPDATE_TEST=1): allows plain HTTP from localhost and checks after 5 s
    /// instead of 1 min. Has no effect on normal installs.
    /// </summary>
    public static bool TestMode => Environment.GetEnvironmentVariable("KONNICHIWATT_UPDATE_TEST") == "1";

    /// <summary>
    /// Downloads, verifies and swaps the executable, then starts the new one. Returns false (and leaves everything
    /// as it was) if anything fails; on success the caller must exit the app.
    /// </summary>
    /// <param name="beforeSwap">
    /// Runs right before the running .exe is renamed. Once renamed, a single-file app can no longer load code it
    /// hasn't loaded yet (it lives inside that file), so anything to persist must be saved here.
    /// </param>
    public async Task<bool> InstallAsync(UpdateManifest m, Action? beforeSwap = null, CancellationToken ct = default)
    {
        if (!CanSelfUpdate) return false;
        var exe = Environment.ProcessPath!;
        var dir = Path.Combine(Log.Directory, "update");
        var downloaded = Path.Combine(dir, "Konnichiwatt.new.exe");
        var old = exe + ".old";
        try
        {
            Directory.CreateDirectory(dir);
            await using (var src = await Http.GetStreamAsync(m.Url, ct))
            await using (var dst = File.Create(downloaded))
                await src.CopyToAsync(dst, ct);

            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(downloaded)));
            if (!hash.Equals(m.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                LastError = "SHA-256 mismatch";
                Log.Write($"Update {m.Version}: SHA-256 mismatch (got {hash}), discarded");
                File.Delete(downloaded);
                return false;
            }

            if (File.Exists(old)) File.Delete(old);
            beforeSwap?.Invoke();
            File.Move(exe, old);                 // allowed while running
            try { File.Move(downloaded, exe); }
            catch { File.Move(old, exe); throw; } // put the original back

            // Created before the new version starts, so it can always find it and report that it started fine.
            var healthy = new EventWaitHandle(false, EventResetMode.ManualReset, HealthyEventName);
            var process = Process.Start(new ProcessStartInfo(exe, $"--updated {CurrentVersion.ToString(3)}") { UseShellExecute = false })!;
            Log.Write($"Updated {CurrentVersion.ToString(3)} -> {m.Version}");
            Pending = new Handover(process, healthy, exe, old, m.Version);
            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Log.Error("Update install", e);
            return false;
        }
    }

    // ---- Rollback ----------------------------------------------------------------------
    //
    // After the swap the previous version doesn't just exit: once its UI is gone and the single-instance lock is
    // released, it waits for the new version to report that it started fine (HealthyAfter of normal running). If the
    // new process dies first, or never reports, the previous version moves itself back into place, starts again with
    // --rolledback <version> and that version is skipped from then on. Everything used here is already loaded: once
    // the running .exe has been renamed, a single-file app can't load new assemblies.

    private const string HealthyEventName = @"Local\Konnichiwatt.UpdateHealthy";
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(90);

    private sealed record Handover(Process Process, EventWaitHandle Healthy, string Exe, string Old, string Version);

    /// <summary>Set by a successful <see cref="InstallAsync"/>; the caller exits and then calls <see cref="SuperviseHandover"/>.</summary>
    private static Handover? Pending;

    /// <summary>
    /// Run by the previous version after its message loop ended and the single-instance lock was released.
    /// Returns once the new version is confirmed, or after rolling back.
    /// </summary>
    public static void SuperviseHandover()
    {
        var h = Pending;
        if (h == null) return;
        Pending = null;
        using (h.Healthy)
        using (h.Process)
        {
            var deadline = DateTime.UtcNow + HandoverTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (h.Healthy.WaitOne(250)) return;   // all good: the new version deletes the .old file
                if (h.Process.HasExited) break;
            }
            Log.Write($"Update to {h.Version} failed (exited: {h.Process.HasExited}), rolling back to {CurrentVersion.ToString(3)}");
            try
            {
                if (!h.Process.HasExited) { h.Process.Kill(); h.Process.WaitForExit(5000); }
                var failed = h.Exe + ".failed";
                for (int i = 0; ; i++)
                {
                    try { File.Move(h.Exe, failed, overwrite: true); break; }
                    catch (IOException) when (i < 20) { Thread.Sleep(250); }
                }
                File.Move(h.Old, h.Exe);
                try { File.Delete(failed); } catch { /* removed on next update */ }
                Process.Start(new ProcessStartInfo(h.Exe, $"--rolledback {h.Version}") { UseShellExecute = false });
            }
            catch (Exception e) { Log.Error("Rollback", e); }
        }
    }

    /// <summary>
    /// Called by the new version once it has run fine for <see cref="HealthyAfter"/>: tells the previous version
    /// (if it's watching) and deletes the previous executable.
    /// </summary>
    public static void ReportHealthy()
    {
        if (EventWaitHandle.TryOpenExisting(HealthyEventName, out var ev))
            using (ev) ev.Set();
        Task.Run(() =>
        {
            var old = Environment.ProcessPath + ".old";
            for (int i = 0; i < 40 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(250); }    // the previous version is still exiting
            }
            try { File.Delete(Environment.ProcessPath + ".failed"); } catch { }
        });
    }

    /// <summary>Test hook: makes this version fail to start, to exercise the rollback (only in <see cref="TestMode"/>).</summary>
    public static bool SimulateStartupCrash =>
        TestMode && Environment.GetEnvironmentVariable("KONNICHIWATT_SIMULATE_CRASH") == CurrentVersion.ToString(3);
}
