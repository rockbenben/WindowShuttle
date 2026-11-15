using System.IO;
using System.IO.Compression;
using WindowShuttle.App;

namespace WindowShuttle.Core.Tests;

public sealed class UpdateCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "windowshuttle-update-test-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(_root, "cache");

    public UpdateCacheTests() => Directory.CreateDirectory(Cache);

    [Fact]
    public void Interrupted_or_legacy_extraction_is_replaced_before_reuse()
    {
        File.WriteAllText(Path.Combine(Cache, "WindowShuttle.exe"), "partial");
        Assert.Null(UpdateService.CachedExecutable(Cache));
        string exe = UpdateService.ExtractVerifiedArchive(Archive(("WindowShuttle.exe", "complete")), Cache);
        Assert.Equal("complete", File.ReadAllText(exe));
        Assert.Equal(exe, UpdateService.CachedExecutable(Cache));
    }

    [Fact]
    public void Changed_or_missing_program_invalidates_a_completed_cache()
    {
        string exe = UpdateService.ExtractVerifiedArchive(Archive(("WindowShuttle.exe", "complete")), Cache);
        File.WriteAllText(exe, "truncated");
        Assert.Null(UpdateService.CachedExecutable(Cache));
        File.Delete(exe);
        Assert.Null(UpdateService.CachedExecutable(Cache));
    }

    [Fact]
    public void Missing_program_cannot_bless_a_leftover_executable()
    {
        UpdateService.ExtractVerifiedArchive(Archive(("WindowShuttle.exe", "old")), Cache);
        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractVerifiedArchive(
            Archive(("helper.exe", "other")), Cache));
        Assert.Null(UpdateService.CachedExecutable(Cache));
    }

    [Fact]
    public void Failure_after_writing_the_program_does_not_mark_the_cache_complete()
    {
        string zip = Archive(("WindowShuttle.exe", "complete"), ("blocked/file", "later"));
        File.WriteAllText(Path.Combine(Cache, "blocked"), "cannot create a directory here");
        Assert.ThrowsAny<IOException>(() => UpdateService.ExtractVerifiedArchive(zip, Cache));
        Assert.True(File.Exists(Path.Combine(Cache, "WindowShuttle.exe")));
        Assert.Null(UpdateService.CachedExecutable(Cache));
        File.Delete(Path.Combine(Cache, "blocked"));
        UpdateService.ExtractVerifiedArchive(zip, Cache);
        Assert.NotNull(UpdateService.CachedExecutable(Cache));
    }

    [Fact]
    public void Architecture_isolated_cache_does_not_reuse_other_architecture_or_legacy_cache()
    {
        string updatesRoot = Path.Combine(_root, "Updates");
        string x64Cache = UpdatePackage.CacheDirectory(updatesRoot, "1.0.0", System.Runtime.InteropServices.Architecture.X64);
        string arm64Cache = UpdatePackage.CacheDirectory(updatesRoot, "1.0.0", System.Runtime.InteropServices.Architecture.Arm64);
        string legacyCache = Path.Combine(updatesRoot, "1.0.0");

        // Extracting into x64 cache marks only x64 complete; arm64 cache and legacy un-isolated cache remain empty
        UpdateService.ExtractVerifiedArchive(Archive(("WindowShuttle.exe", "x64-payload")), x64Cache);
        Assert.NotNull(UpdateService.CachedExecutable(x64Cache));
        Assert.Null(UpdateService.CachedExecutable(arm64Cache));
        Assert.Null(UpdateService.CachedExecutable(legacyCache));

        // Conversely, a legacy un-isolated cache with a marker does not satisfy arm64 cache lookup
        Directory.CreateDirectory(legacyCache);
        File.WriteAllText(Path.Combine(legacyCache, "WindowShuttle.exe"), "legacy-payload");
        File.WriteAllText(Path.Combine(legacyCache, ".complete-sha256"), UpdateCheck.ComputeSha256Hex(Path.Combine(legacyCache, "WindowShuttle.exe")));
        Assert.NotNull(UpdateService.CachedExecutable(legacyCache));
        Assert.Null(UpdateService.CachedExecutable(arm64Cache));
    }

    private string Archive(params (string Name, string Content)[] files)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
