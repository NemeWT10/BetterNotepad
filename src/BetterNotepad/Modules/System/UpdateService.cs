using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BetterNotepad.Core;

namespace BetterNotepad.Modules.Sys;

internal enum UpdateStatus
{
    UpToDate,
    Available,
    /// <summary>No connection / timeout: silent for automatic checks.</summary>
    Offline,
    Failed,
    /// <summary>No network in test mode (unless an explicit local fake is configured).</summary>
    Disabled,
}

internal sealed record ReleaseInfo(string Tag, Version Version, string Notes, string PageUrl, string? AssetUrl, long AssetSize, string? ChecksumUrl, string? Digest);

internal sealed record UpdateCheckResult(UpdateStatus Status, ReleaseInfo? Release = null, string? Error = null);

/// <summary>A failed download or verification; <see cref="Reason"/> is the suffix of an "update.err.*" string.</summary>
internal sealed class UpdateException(string reason, string? detail = null) : Exception(detail ?? reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Update logic without UI: asks GitHub Releases for the latest version (the program's only network traffic),
/// downloads BetterNotepad.exe, verifies its SHA-256 against the published .sha256 and swaps the exe by rename.
/// No telemetry: the request carries nothing but the program name and version.
/// </summary>
internal static class UpdateService
{
    public const string ExeAsset = "BetterNotepad.exe";

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static HttpClient? _http;

    private static HttpClient Http => _http ??= Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"BetterNotepad/{AppInfo.Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>
    /// The releases endpoint. In test mode only an explicit local fake (BETTERNOTEPAD_UPDATE_URL=http://127.0.0.1:port/…)
    /// is allowed; otherwise a test instance makes no network calls at all.
    /// </summary>
    public static string? ApiUrl
    {
        get
        {
            if (!AppEnv.TestMode) return AppInfo.ReleasesApi;
            var fake = Environment.GetEnvironmentVariable("BETTERNOTEPAD_UPDATE_URL");
            return IsLocalHttp(fake) ? fake : null;
        }
    }

    public static bool IsLocalHttp(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;

    /// <summary>Downloads only come from GitHub (or the local fake in test mode).</summary>
    public static bool IsAllowedDownload(string? url) => IsAllowedDownload(url, AppEnv.TestMode);

    internal static bool IsAllowedDownload(string? url, bool testMode)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (testMode) return IsLocalHttp(url);
        return u.Scheme == Uri.UriSchemeHttps
            && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                || u.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsNewer(string? tag, Version current)
        => AppInfo.TryParseSemVer(tag) is { } v && v > current;

    // ───────────── check ─────────────

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var url = ApiUrl;
        if (url is null) return new(UpdateStatus.Disabled);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.GetAsync(url, cts.Token).ConfigureAwait(true);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new(UpdateStatus.UpToDate); // no release published yet
            if (!resp.IsSuccessStatusCode) return new(UpdateStatus.Failed, null, "HTTP " + (int)resp.StatusCode);
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true);
            var release = ParseRelease(json);
            if (release is null) return new(UpdateStatus.Failed, null, "unreadable response");
            return IsNewer(release.Tag, AppInfo.SemVer) ? new(UpdateStatus.Available, release) : new(UpdateStatus.UpToDate, release);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(UpdateStatus.Failed, null, "cancelled");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new(UpdateStatus.Offline, null, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn("update check failed: " + ex.Message);
            return new(UpdateStatus.Failed, null, ex.Message);
        }
    }

    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var tag = Str(root, "tag_name");
            if (AppInfo.TryParseSemVer(tag) is not { } version) return null;

            string? exeUrl = null, sumUrl = null, digest = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name") ?? "";
                    var url = Str(a, "browser_download_url");
                    if (name.Equals(ExeAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        exeUrl = url;
                        digest = Str(a, "digest");
                        if (a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n)) size = n;
                    }
                    else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) && (sumUrl is null || name.StartsWith(ExeAsset, StringComparison.OrdinalIgnoreCase)))
                    {
                        sumUrl = url;
                    }
                }
            }
            return new ReleaseInfo(tag!, version, Str(root, "body") ?? "", Str(root, "html_url") ?? AppInfo.RepositoryUrl, exeUrl, size, sumUrl, digest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ───────────── download + verify ─────────────

    private static readonly Regex Hex64 = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])", RegexOptions.Compiled);

    /// <summary>First SHA-256 in a .sha256 / SHA256SUMS text (the line naming the exe wins).</summary>
    public static string? ParseChecksum(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? first = null;
        foreach (var line in text.Split('\n'))
        {
            var m = Hex64.Match(line);
            if (!m.Success) continue;
            first ??= m.Value.ToLowerInvariant();
            if (line.Contains(ExeAsset, StringComparison.OrdinalIgnoreCase)) return m.Value.ToLowerInvariant();
        }
        return first;
    }

    private static string? ExpectedHash(ReleaseInfo r, string? checksumText)
    {
        var fromFile = ParseChecksum(checksumText);
        if (fromFile is not null) return fromFile;
        var d = r.Digest;
        if (d is not null && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return d[7..].ToLowerInvariant();
        return null;
    }

    /// <summary>Downloads the release exe to <paramref name="stagedPath"/> and verifies it (throws <see cref="UpdateException"/>; nothing is left behind on failure).</summary>
    public static async Task DownloadAsync(ReleaseInfo r, string stagedPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!IsAllowedDownload(r.AssetUrl)) throw new UpdateException("noAsset");
        string? sumText = null;
        try
        {
            if (r.ChecksumUrl is not null)
            {
                if (!IsAllowedDownload(r.ChecksumUrl)) throw new UpdateException("noChecksum");
                using var sumCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sumCts.CancelAfter(TimeSpan.FromSeconds(30));
                sumText = await Http.GetStringAsync(r.ChecksumUrl, sumCts.Token).ConfigureAwait(true);
            }
            var expected = ExpectedHash(r, sumText) ?? throw new UpdateException("noChecksum");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            using var resp = await Http.GetAsync(r.AssetUrl!, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? r.AssetSize;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(true))
            await using (var dst = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(true)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await dst.WriteAsync(buffer.AsMemory(0, n), cts.Token).ConfigureAwait(true);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new UpdateException("hash");
            if (!LooksLikeExe(stagedPath)) throw new UpdateException("hash");
        }
        catch
        {
            TryDelete(stagedPath);
            throw;
        }
    }

    private static bool LooksLikeExe(string path)
    {
        using var f = File.OpenRead(path);
        return f.Length > 2 && f.ReadByte() == 'M' && f.ReadByte() == 'Z';
    }

    /// <summary>Verifies a file against a hash text (used by the download and by tests).</summary>
    public static bool VerifyFile(string path, string expectedHex)
    {
        using var f = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(f)), expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── swap ─────────────

    /// <summary>
    /// Replaces <paramref name="targetExe"/> with the staged file by rename (a running exe can be renamed):
    /// old → .old (deleted at the next start), staged → exe. Rolls back if the second step fails.
    /// </summary>
    public static void Swap(string stagedPath, string targetExe)
    {
        var old = targetExe + ".old";
        if (File.Exists(old) && !TryDelete(old)) old = targetExe + "." + Guid.NewGuid().ToString("N")[..6] + ".old";
        File.Move(targetExe, old);
        try
        {
            File.Move(stagedPath, targetExe);
        }
        catch
        {
            try { File.Move(old, targetExe); } catch { /* nothing more to do */ }
            throw;
        }
    }

    /// <summary>Deletes leftovers of earlier updates (exe.old, exe.new). Quiet when a file is still locked.</summary>
    public static void CleanupLeftovers(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            var dir = Path.GetDirectoryName(exe);
            if (dir is null || !Directory.Exists(dir)) return;
            var name = Path.GetFileName(exe);
            foreach (var f in Directory.EnumerateFiles(dir, name + "*.old").Concat(Directory.EnumerateFiles(dir, name + ".new")))
                TryDelete(f);
        }
        catch (Exception ex)
        {
            Log.Warn("update cleanup: " + ex.Message);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
