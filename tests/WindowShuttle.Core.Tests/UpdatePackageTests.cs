using System.IO;
using System.Runtime.InteropServices;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class UpdatePackageTests
{
    [Fact]
    public void Cpu_maps_known_architectures_and_throws_for_unsupported()
    {
        Assert.Equal("x64", UpdatePackage.Cpu(Architecture.X64));
        Assert.Equal("arm64", UpdatePackage.Cpu(Architecture.Arm64));
        Assert.Throws<PlatformNotSupportedException>(() => UpdatePackage.Cpu(Architecture.X86));
        Assert.Throws<PlatformNotSupportedException>(() => UpdatePackage.Cpu(Architecture.Arm));
    }

    [Fact]
    public void Cpu_defaults_to_process_architecture()
    {
        string expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "unsupported"
        };
        if (expected != "unsupported")
            Assert.Equal(expected, UpdatePackage.Cpu());
        else
            Assert.Throws<PlatformNotSupportedException>(() => UpdatePackage.Cpu());
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("V2.3.4", "2.3.4")]
    public void SelectZip_picks_exact_architecture_matching_package(string versionInput, string normalized)
    {
        string[] assets =
        [
            $"WindowShuttle-v{normalized}-win-x64-needs-dotnet10.zip",
            $"WindowShuttle-v{normalized}-win-x64.zip",
            $"WindowShuttle-v{normalized}-win-arm64-needs-dotnet10.zip",
            $"WindowShuttle-v{normalized}-win-arm64.zip",
            "WindowShuttle.exe",
            "SHA256SUMS.txt",
        ];

        string? x64Pick = UpdatePackage.SelectZip(assets, "WindowShuttle", versionInput, Architecture.X64);
        Assert.Equal($"WindowShuttle-v{normalized}-win-x64.zip", x64Pick);

        string? arm64Pick = UpdatePackage.SelectZip(assets, "WindowShuttle", versionInput, Architecture.Arm64);
        Assert.Equal($"WindowShuttle-v{normalized}-win-arm64.zip", arm64Pick);
    }

    [Fact]
    public void SelectZip_returns_null_when_package_for_architecture_is_missing()
    {
        string[] onlyX64 =
        [
            "WindowShuttle-v1.0.0-win-x64.zip",
            "SHA256SUMS.txt",
        ];

        Assert.Null(UpdatePackage.SelectZip(onlyX64, "WindowShuttle", "1.0.0", Architecture.Arm64));
    }

    [Fact]
    public void SelectZip_throws_on_invalid_version()
    {
        Assert.Throws<ArgumentException>(() =>
            UpdatePackage.SelectZip(["WindowShuttle-v1.0-win-x64.zip"], "WindowShuttle", "invalid"));
    }

    [Fact]
    public void CacheDirectory_isolates_by_cpu_and_version()
    {
        string root = @"C:\FakeAppData\Updates";
        string x64Dir = UpdatePackage.CacheDirectory(root, "v1.2.3", Architecture.X64);
        string arm64Dir = UpdatePackage.CacheDirectory(root, "1.2.3", Architecture.Arm64);

        Assert.Equal(Path.Combine(root, "x64", "1.2.3"), x64Dir);
        Assert.Equal(Path.Combine(root, "arm64", "1.2.3"), arm64Dir);
        Assert.NotEqual(x64Dir, arm64Dir);
    }
}
