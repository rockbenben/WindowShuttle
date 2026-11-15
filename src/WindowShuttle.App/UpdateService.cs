using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using WindowShuttle.Core;

namespace WindowShuttle.App;

/// <summary>What an update check came back with.</summary>
public enum UpdateStatus
{
    /// <summary>No newer release — there is nothing to do.</summary>
    UpToDate,
    /// <summary>A newer release was downloaded, verified and unpacked. <see cref="UpdateOutcome.ExePath"/>
    /// says where the new program is.</summary>
    Ready,
    /// <summary>The download did not match the checksum the release published, so it was thrown
    /// away. Nothing was unpacked.</summary>
    VerifyFailed,
    /// <summary>The check itself failed: no network, rate-limited, or a release that does not carry
    /// the files we need.</summary>
    Failed,
}

/// <summary>The result of one update check.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Version">The version that was found, without its leading 'v'.</param>
/// <param name="ExePath">The unpacked program, when <paramref name="Status"/> is
/// <see cref="UpdateStatus.Ready"/>.</param>
/// <param name="ReleaseUrl">The release page.</param>
/// <param name="Error">Why it failed, for the log and the message.</param>
public sealed record UpdateOutcome(UpdateStatus Status, string? Version, string? ExePath,
                                   string? ReleaseUrl, string? Error);

/// <summary>
/// The opt-in update check: ask GitHub for the latest release, and if it is newer than the
/// running build, download the self-contained zip, check it against the checksum the same release
/// published, and unpack it where the user can find it.
/// <para><b>It never replaces the running program.</b> These releases are single-file
/// self-contained executables, and Windows locks a running executable: overwriting it in place
/// fails with a sharing violation and leaves a half-written install behind. So the new build is
/// unpacked into a folder of its own and the user is told where it is — replacing the old one is
/// their call, made with the old one closed.</para>
/// </summary>
public static class UpdateService
{
    public const string ApiUrl = "https://api.github.com/repos/rockbenben/WindowShuttle/releases/latest";
    public const string ReleasePage = "https://github.com/rockbenben/WindowShuttle/releases";
    private const string ChecksumsAsset = "SHA256SUMS.txt";

    /// <summary>Where an unpacked update is put: %LOCALAPPDATA%\WindowShuttle\Updates\&lt;version&gt;\.
    /// Per-user and outside the program's own folder, so it survives the program being moved or
    /// replaced, and findable without knowing where the exe was installed.</summary>
    public static string UpdatesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowShuttle", "Updates");

    // The metadata and the checksum file are small; the zip is not. Two clients so a slow network
    // cannot turn the small calls into long waits, and so the big one is not cut off half-way.
    private static readonly HttpClient Api = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static readonly SemaphoreSlim RunGate = new(1, 1);

    /// <summary>
    /// Check for a newer release and, if there is one, fetch it. Never throws: every failure comes
    /// back as <see cref="UpdateStatus.Failed"/>, because a check that cannot reach the network is
    /// an ordinary outcome, not a reason to take the app down.
    /// </summary>
    public static async Task<UpdateOutcome> RunAsync(string currentVersion, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await RunGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { return await RunCoreAsync(currentVersion, deadline.Token).ConfigureAwait(false); }
            finally { RunGate.Release(); }
        }
        catch (Exception ex) { return new(UpdateStatus.Failed, null, null, null, ex.Message); }
    }

    private static async Task<UpdateOutcome> RunCoreAsync(string currentVersion, CancellationToken ct)
    {
        string? releaseUrl = null;
        string tempDir = "";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            req.Headers.UserAgent.ParseAdd("WindowShuttle-update-check");   // GitHub API rejects a missing UA
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Api.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            // A draft or a prerelease is not something to put in front of a user who is running a
            // released build. The "latest" endpoint already excludes drafts, but say so out loud:
            // the check is about finding a newer release, and an unfinished one is not that.
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                return new(UpdateStatus.UpToDate, null, null, null, null);
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                return new(UpdateStatus.UpToDate, null, null, null, null);

            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            releaseUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (!UpdateCheck.IsNewer(tag, currentVersion))
                return new(UpdateStatus.UpToDate, UpdateCheck.NormalizeVersion(tag), null, releaseUrl, null);

            string version = UpdateCheck.NormalizeVersion(tag);
            if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
                return new(UpdateStatus.Failed, null, null, releaseUrl, "invalid release version");

            var assets = ReadAssets(root);
            string? zipName = UpdatePackage.SelectZip(assets.Select(a => a.Name), "WindowShuttle", version);
            string? zipUrl = zipName is null ? null : assets.FirstOrDefault(a => a.Name == zipName).Url;
            string? sumsUrl = assets.FirstOrDefault(a => a.Name == ChecksumsAsset).Url;
            if (zipName is null || zipUrl is null)
                return new(UpdateStatus.Failed, null, null, releaseUrl, $"the release has no self-contained win-{UpdatePackage.Cpu()} zip");
            if (sumsUrl is null)
                return new(UpdateStatus.Failed, null, null, releaseUrl, "the release has no " + ChecksumsAsset);

            string destDir = UpdatePackage.CacheDirectory(UpdatesRoot, version);

            tempDir = Path.Combine(Path.GetTempPath(), "WindowShuttle-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, zipName);
            await DownloadFileAsync(zipUrl, zipPath, ct);

            string sums = await ReadTextAsync(sumsUrl, ct);
            var checksums = UpdateCheck.ParseChecksums(sums);
            if (!checksums.TryGetValue(zipName, out var expected))
                return new(UpdateStatus.Failed, version, null, releaseUrl, ChecksumsAsset + " does not list " + zipName);
            if (!UpdateCheck.MatchesChecksum(expected, UpdateCheck.ComputeSha256Hex(zipPath)))
                return new(UpdateStatus.VerifyFailed, version, null, releaseUrl, "the download did not match " + expected);

            string exe = ExtractVerifiedArchive(zipPath, destDir);
            return new(UpdateStatus.Ready, version, exe, releaseUrl, null);
        }
        catch (Exception ex)
        {
            return new(UpdateStatus.Failed, null, null, releaseUrl, ex.Message);
        }
        finally
        {
            // The download is a scratch copy: it is unpacked by now, and leaving tens of megabytes
            // in %TEMP% for every version ever fetched is not worth it.
            if (tempDir.Length > 0)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private static List<(string Name, string Url)> ReadAssets(JsonElement root)
    {
        var assets = new List<(string, string)>();
        if (!root.TryGetProperty("assets", out var arr) || arr.ValueKind != JsonValueKind.Array) return assets;
        foreach (var a in arr.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string url = a.TryGetProperty("browser_download_url", out var b) ? b.GetString() ?? "" : "";
            if (name.Length > 0 && url.Length > 0) assets.Add((name, url));
        }
        return assets;
    }

    internal static async Task DownloadFileAsync(string url, string path, CancellationToken ct, HttpClient? client = null)
    {
        using var resp = await (client ?? Downloads).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct);
    }

    private static async Task<string> ReadTextAsync(string url, CancellationToken ct)
    {
        using var resp = await Downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private const string CompletionMarker = ".complete-sha256";

    /// <summary>Inspect extraction completeness; online checks separately verify a fresh archive.</summary>
    internal static string? CachedExecutable(string dir)
    {
        string exe = Path.Combine(dir, "WindowShuttle.exe");
        string marker = Path.Combine(dir, CompletionMarker);
        if (!File.Exists(exe) || !File.Exists(marker)) return null;
        return UpdateCheck.MatchesChecksum(File.ReadAllText(marker), UpdateCheck.ComputeSha256Hex(exe))
            ? exe : null;
    }

    /// <summary>Unpack a checksum-verified archive; publish completion only after extraction succeeds.</summary>
    internal static string ExtractVerifiedArchive(string zipPath, string dir)
    {
        Directory.CreateDirectory(dir);
        string marker = Path.Combine(dir, CompletionMarker);
        File.Delete(marker);
        // Releases promise one root-level executable. Do not bless a leftover from an earlier
        // attempt if this archive has no program, or mistake a helper executable for the app.
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.GetEntry("WindowShuttle.exe") is null)
            throw new InvalidDataException("the downloaded archive holds no WindowShuttle.exe");
        archive.ExtractToDirectory(dir, overwriteFiles: true);
        string exe = Path.Combine(dir, "WindowShuttle.exe");
        File.WriteAllText(marker, UpdateCheck.ComputeSha256Hex(exe));
        return exe;
    }
}
