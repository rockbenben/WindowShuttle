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
