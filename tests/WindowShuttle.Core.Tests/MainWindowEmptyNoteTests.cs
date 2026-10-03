using MainWindow = WindowShuttle.App.MainWindow;

namespace WindowShuttle.Core.Tests;

/// <summary>地图零窗口空态那行的**合成规则**（batch1 B7）。画布落位依赖真实桌面枚举，测不到；
/// 能测且必须测的是那句话本身的组成——括号段跟着「跳过全屏」开关走，不该出现时必须一个字符
/// 都不多。断言全部相对基准句成立，不逐字钉译文（译文归 resx 覆盖闸与人工眼）。</summary>
public class MainWindowEmptyNoteTests
{
    [Fact]
    public void Skip_clause_appears_only_when_skip_is_on()
    {
        var plain = MainWindow.NoMovableNoteText(skipFullscreen: false);
        var withSkip = MainWindow.NoMovableNoteText(skipFullscreen: true);

        Assert.False(string.IsNullOrWhiteSpace(plain));
        Assert.StartsWith(plain, withSkip);                 // 基准句原样在前
        Assert.True(withSkip.Length > plain.Length, "开关开着时括号段没接上——两态同文等于没有解释");
    }

    [Fact]
    public void Both_segments_resolve_through_real_resource_lookup_not_raw_keys()
    {
        // Strings.Get 缺键回退成键名本身；合成句里出现裸键名 = 至少一门语言的键坏了。
        Assert.DoesNotContain("Main_NoMovableChips", MainWindow.NoMovableNoteText(skipFullscreen: true));
    }
}
