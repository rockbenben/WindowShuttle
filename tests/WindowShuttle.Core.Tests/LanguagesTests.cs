using System.Globalization;
using WindowShuttle.App.I18n;

namespace WindowShuttle.Core.Tests;

public class LanguagesTests
{
    [Theory]
    [InlineData("en-US", "en")] [InlineData("en-GB", "en")]
    [InlineData("zh-CN", "zh-CN")] [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-TW", "zh-TW")] [InlineData("zh-HK", "zh-TW")] [InlineData("zh-Hans-HK", "zh-CN")]
    [InlineData("ja-JP", "ja")] [InlineData("pt-BR", "pt")] [InlineData("de-AT", "de")]
    [InlineData("pl-PL", "en")]      // 不支持的语言 → 英文
    public void ResolveFor_maps_to_supported(string culture, string expected)
        => Assert.Equal(expected, Languages.ResolveFor(CultureInfo.GetCultureInfo(culture)));

    [Fact] public void Normalize_null_follows_system()
        => Assert.Contains(Languages.Normalize(null), Languages.All.Select(x => x.Code).ToArray());

    [Fact] public void Normalize_canonicalizes_case()
        => Assert.Equal("en", Languages.Normalize("EN"));

    [Fact] public void Normalize_garbage_follows_system()
        => Assert.Contains(Languages.Normalize("xx-INVALID-!!"), Languages.All.Select(x => x.Code).ToArray());
}
