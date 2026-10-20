using System.IO;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>更新检查的纯逻辑：版本比较、挑资产、读校验值。
///
/// 联网那部分在 App 层（UpdateService），这里只管「挑哪个文件、算不算更新、校验值对不对」——
/// 这三样都不该靠真机跑到 GitHub 才验得了。</summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.0.1", "1.0.0")]   // patch bump, tag carries a leading 'v'
    [InlineData("1.1.0", "1.0.9")]    // minor beats a higher patch
    [InlineData("2.0.0", "1.9.9")]    // major beats everything below
    [InlineData("v1.0.1", "1.0")]     // more components: extra 1 beats the implicit trailing 0
    [InlineData("1.0.0", "0.9")]      // fewer components on the older side
    public void Newer_tag_is_an_update(string latest, string current)
        => Assert.True(UpdateCheck.IsNewer(latest, current));

    [Theory]
    [InlineData("v1.0.0", "1.0.0")]   // identical
    [InlineData("1.0.0", "1.0.0.0")]  // trailing zeros don't make it newer
    [InlineData("v1.0.0", "1.0.1")]   // older tag than current
    [InlineData("0.9.0", "1.0.0")]
    public void Same_or_older_tag_is_not_an_update(string latest, string current)
        => Assert.False(UpdateCheck.IsNewer(latest, current));

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("", "1.0.0")]
    [InlineData("nightly", "1.0.0")]   // non-numeric tag never reports a phantom update
    [InlineData("v1.0.0", "garbage")]
    public void Unparsable_input_is_never_an_update(string? latest, string? current)
        => Assert.False(UpdateCheck.IsNewer(latest, current));

    [Fact]
    public void Prerelease_suffix_is_ignored_for_comparison()
    {
        Assert.True(UpdateCheck.IsNewer("v1.2.0-beta", "1.1.0"));   // 1.2.0 > 1.1.0
        Assert.False(UpdateCheck.IsNewer("v1.0.0-rc1", "1.0.0"));   // 1.0.0 == 1.0.0
    }

    [Theory]
    [InlineData("v1.0.1", "1.0.1")]
    [InlineData("V1.0.1", "1.0.1")]
    [InlineData("1.0.1", "1.0.1")]
    public void Normalize_version_drops_the_leading_v(string tag, string expected)
        => Assert.Equal(expected, UpdateCheck.NormalizeVersion(tag));

    [Fact]
    public void The_self_contained_zip_is_picked_and_not_the_framework_dependent_one()
    {
        // A release carries both; the framework-dependent one needs .NET 10 installed, so handing
        // it to a user as "just run this" would give them an exe that refuses to start.
        string[] assets =
        [
            "WindowShuttle-v1.0.1-win-x64-needs-dotnet10.zip",
            "WindowShuttle-v1.0.1-win-x64.zip",
            "WindowShuttle.exe",
            "SHA256SUMS.txt",
        ];
        Assert.Equal("WindowShuttle-v1.0.1-win-x64.zip", UpdateCheck.SelectSelfContainedAsset(assets));
    }

    [Fact]
    public void No_self_contained_asset_means_no_pick()
    {
        string[] onlyFrameworkDependent =
        [
            "WindowShuttle-v1.0.1-win-x64-needs-dotnet10.zip",
            "WindowShuttle.exe",
            "SHA256SUMS.txt",
        ];
        Assert.Null(UpdateCheck.SelectSelfContainedAsset(onlyFrameworkDependent));
        Assert.Null(UpdateCheck.SelectSelfContainedAsset([]));
    }

    [Fact]
    public void Checksums_are_parsed_by_file_name()
    {
        // The format sha256sum writes: digest, whitespace, name. The '*' marks binary mode.
        string text =
            "724a0062821f8e606139a427ac73152b82fae6a8e8d5acf2c750d10be2f815d9  WindowShuttle-v1.0.1-win-x64-needs-dotnet10.zip\r\n"
          + "13ebe65d8948d75a01ad67252f80141608d0b5dca36bed5001483e46f1e15c76 *WindowShuttle-v1.0.1-win-x64.zip\r\n"
          + "09b8365ad727ba51b0d976f65f04b20f35f77210ff2d86f6ce09975f8549bd3f  WindowShuttle.exe\r\n";
        var sums = UpdateCheck.ParseChecksums(text);
        Assert.Equal(3, sums.Count);
        Assert.Equal("13ebe65d8948d75a01ad67252f80141608d0b5dca36bed5001483e46f1e15c76",
                     sums["WindowShuttle-v1.0.1-win-x64.zip"]);
        Assert.Equal("09b8365ad727ba51b0d976f65f04b20f35f77210ff2d86f6ce09975f8549bd3f",
                     sums["WindowShuttle.exe"]);
    }

    [Fact]
    public void Lines_that_are_not_a_digest_are_skipped()
    {
        // A release also ships a README and a licence; their lines are not ours to read.
        string text =
            "# SHA256SUMS.txt\r\n"
          + "not-a-digest  WindowShuttle.exe\r\n"
          + "09b8365ad727ba51b0d976f65f04b20f35f77210ff2d86f6ce09975f8549bd3f  WindowShuttle.exe\r\n";
        var sums = UpdateCheck.ParseChecksums(text);
        Assert.Single(sums);
        Assert.True(sums.ContainsKey("WindowShuttle.exe"));
    }

    [Fact]
    public void The_digest_of_a_file_is_computed_and_compared()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(path, "windowshuttle"u8.ToArray());
        try
        {
            string digest = UpdateCheck.ComputeSha256Hex(path);
            Assert.Equal(64, digest.Length);
            Assert.True(UpdateCheck.MatchesChecksum(digest.ToUpperInvariant(), digest));   // case carries no meaning
            string altered = (digest[0] == 'a' ? "b" : "a") + digest[1..];
            Assert.False(UpdateCheck.MatchesChecksum(altered, digest));
            Assert.False(UpdateCheck.MatchesChecksum(null, digest));
            Assert.False(UpdateCheck.MatchesChecksum(digest, ""));
        }
        finally { File.Delete(path); }
    }
}
