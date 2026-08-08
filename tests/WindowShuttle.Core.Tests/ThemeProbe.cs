using System.Windows.Media;
using AppTheme = WindowShuttle.App.AppTheme;

namespace WindowShuttle.Core.Tests;

/// <summary>两个对比度测试共用的取色和主题切换脚手架。
///
/// 抽出来不是为了省这十几行，是为了让 <see cref="Under"/> 的 try/finally 只存在一份。切主题改的是
/// 全局静态状态，忘了还原就会把它泄漏给后面每一个测试——本测试套件已经因为同一类问题（UI 语言
/// 泄漏，见 <see cref="UiCultureCollection"/>）出过一次约三次一红的偶发，那次红的还是一条跟语言
/// 毫无关系的测试。每处一份 try/finally 就是每处一次漏写的机会。</summary>
internal static class ThemeProbe
{
    public static (byte R, byte G, byte B) Rgb(SolidColorBrush b) => (b.Color.R, b.Color.G, b.Color.B);

    /// <summary>在指定主题下跑 <paramref name="body"/>，跑完无论成败都还原成进入时的主题。</summary>
    public static void Under(bool dark, Action body)
    {
        bool was = AppTheme.IsDark;
        try
        {
            AppTheme.Apply(dark);
            body();
        }
        finally { AppTheme.Apply(was); }
    }

    public static string Name(bool dark) => dark ? "深色" : "浅色";
}
