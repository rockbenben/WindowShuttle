using System.Globalization;
using System.Resources;

namespace WindowShuttle.App.I18n;

// 中性 Strings.resx = 中文源；Strings.en.resx = 英文。按 CurrentUICulture 取，缺键回退中性。
public static class Strings
{
    private static readonly ResourceManager Rm =
        new("WindowShuttle.App.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    /// <summary>当前语言的排版方向。每一扇窗口、以及托盘那个不在可视树里的 ContextMenu，都要在
    /// 构造时自己设一次——不能靠 OverrideMetadata 一次性覆盖 typeof(Window)，那会撞坏 WPF 自己的
    /// Window 静态构造函数（见 App.OnStartup 里那段注释，实测整个应用起不来）。</summary>
    public static System.Windows.FlowDirection Flow => IsRightToLeft
        ? System.Windows.FlowDirection.RightToLeft
        : System.Windows.FlowDirection.LeftToRight;

    public static string Lf(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>按数量取单复数：count 为 1 时用 <c>key_One</c>，否则用 <c>key</c>。
    ///
    /// 存在的理由很具体：英文文案原本写成 "{0} window(s) could not be moved"，"(s)" 这种写法在真实
    /// 产品里读起来就是没做完。中文没有单复数，两条键写同一句话即可，成本只在英文一侧。
    ///
    /// 只做 1 / 非 1 两档，不引入 ICU 复数规则——有 few/many 档的语言（俄语、阿拉伯语）由译文
    /// 措辞回避数词-名词变格（如俄语用「некоторые окна ({0})」），两档就够，不为此铺复数框架。</summary>
    public static string Plural(string key, int count)
        => string.Format(Get(count == 1 ? key + "_One" : key), count);

    /// <summary>必须在建任何窗口前调用（{loc:Loc} 在 XAML 加载时取值，§12 语言切换经重启生效）。</summary>
    public static void ApplyCulture(string? lang)
    {
        lang = Languages.Normalize(lang);
        try
        {
            var ci = CultureInfo.GetCultureInfo(lang);
            CultureInfo.CurrentUICulture = ci;
            CultureInfo.DefaultThreadCurrentUICulture = ci;
        }
        catch (CultureNotFoundException) { /* 保持当前文化 */ }
    }
}
