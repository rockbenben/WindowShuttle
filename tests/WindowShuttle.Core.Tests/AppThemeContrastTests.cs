using System.Windows.Media;
using WindowShuttle.Core;
using AppTheme = WindowShuttle.App.AppTheme;

namespace WindowShuttle.Core.Tests;

/// <summary>AppTheme 的中性色阶不是挑出来的，是按每个角色欠的对比度底线解出来的（见 AppTheme 的类
/// 注释）。这里把那些底线写成断言。
///
/// 跟 <see cref="ColorContrastTests"/> 的分工：那边用已知参考值验算术本身，并且明确不验 app 的调色板，
/// 理由是"拿同一套算术再算一遍等于自己验自己"。对参考值这话没错，对调色板不成立——断言
/// `Ratio(Edge, Panel) >= 3` 验的不是算术，是一条设计约束能不能在后人改色时活下来。上一版的深色主题
/// 就是没人验：Edge 对 Panel 只有 1.24:1、Panel 对 Room 只有 1.10:1，卡片边界在深色下既没有描边也
/// 没有色块能分出来，一路发到了正式构建里。一句"改了要重新解"的注释拦不住任何人，断言可以。
///
/// 用哪个底线：作为正文的（Ink/Dim/Faint/Beam/Live）欠 WCAG AA 的 4.5:1；只作为组件边界的（Edge）
/// 欠非文本的 3:1。
///
/// Panel 对 Room 那一条两个主题不同，这不是为了把测试弄绿的妥协，是这条规矩本来就只对深色成立：
/// 卡片边界得有东西撑住，深色下近黑的底上一道 1px 描边很容易被屏幕眩光和低端面板的伽马吃掉，所以
/// 面自己也得帮忙浮起来（1.25:1，正是修掉旧深色主题那个 1.10:1 的那一步）；浅色下 Edge 对两侧都有
/// 3:1 出头的余量，边界不缺人撑。真按 1.25 去要求浅色，底得压到 #E2E5EE 那种明显发灰的程度——
/// Windows 自家浅色底是 #F3F3F3（对白卡片 1.11:1），为了一条自定规矩去跟平台观感对着干不划算。
///
/// 为什么整段要走 <see cref="WpfTestHost.Invoke(Action)"/>：AppTheme.Apply 会广播 Changed，而
/// CloseToTray 默认开着时 MainWindow.OnClosing 只是 Hide、不触发 Closed，所以前面测试造的窗口仍然
/// 挂在这个静态事件上、并且归宿主那条 STA 线程所有。在 xunit 自己的线程上直接调 Apply，第一件事就是
/// 去写那些窗口的 Background，当场炸 "a different thread owns it"。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class AppThemeContrastTests(WpfTestHost host)
{
    /// <summary>动作卡在悬停/获得焦点时的实际底色。这是**第三种**正文背景，跟 Panel、Room 平级，
    /// 必须一起解。
    ///
    /// <see cref="AppTheme.HoverBrush"/> 现在是**不透明**的（曾经是 Ink 的 0x14，靠压在 Room 上合成
    /// 出来——见 AppTheme 类注释里为什么改）。这里仍然走一遍 Composite：A=255 时它恒等于画刷自己，
    /// 留着是因为这道合成是"背景必须是合成后的不透明色"这条规矩的执行点，哪天再有半透明的底进来，
    /// 改的是 AppTheme，不该是这里。
    ///
    /// 漏掉它的后果很隐蔽：这块底只在鼠标停在那一行、或者键盘焦点落在那一行时才出现——恰好就是
    /// 用户正在读这行字的时刻。之前浅色主题下 Faint 对它只有 3.91:1（对 Room 有 4.52:1、对 Panel
    /// 有 5.06:1，两条都过），于是"色阶是解出来的"这句话在唯一真正被人盯着看的那块底上不成立。</summary>
    private static (byte R, byte G, byte B) HoverSurface()
    {
        var h = AppTheme.HoverBrush.Color;   // 现在是不透明的，A=255，合成后即它自己
        return ColorContrast.Composite(h.A, h.R, h.G, h.B, ThemeProbe.Rgb(AppTheme.Room));
    }

    /// <summary>(前景, 背景, 底线, 这个组合出现在哪) —— 每一条都对应界面上真实画出来的一处。
    /// 背景取的是**合成后**的不透明色，半透明的底（HoverBrush）不能直接拿它自己的 RGB 去算。</summary>
    private static IEnumerable<(Func<SolidColorBrush> Fg, Func<(byte R, byte G, byte B)> Bg, double Floor, string Where)> Pairs(bool dark)
    {
        static Func<(byte, byte, byte)> Of(Func<SolidColorBrush> b) => () => ThemeProbe.Rgb(b());
        return
        [
            (() => AppTheme.Ink,   Of(() => AppTheme.Panel), 4.5, "卡片上的正文"),
            (() => AppTheme.Ink,   Of(() => AppTheme.Room),  4.5, "底上的章节标题"),
            (() => AppTheme.Dim,   Of(() => AppTheme.Panel), 4.5, "窗口条目文字"),
            (() => AppTheme.Faint, Of(() => AppTheme.Panel), 4.5, "动作卡的描述"),
            (() => AppTheme.Faint, Of(() => AppTheme.Room),  4.5, "拖放提示、鼠标手势警告"),
            (() => AppTheme.Beam,  Of(() => AppTheme.Panel), 4.5, "「主屏」徽章文字、屏号"),
            (() => AppTheme.Live,  Of(() => AppTheme.Panel), 4.5, "「光标」徽章文字"),
            (() => AppTheme.Edge,  Of(() => AppTheme.Panel), 3.0, "显示器卡片描边"),
            (() => AppTheme.Edge,  Of(() => AppTheme.Room),  3.0, "动作卡描边、未绑定虚线圈"),
            (() => AppTheme.Beam,  Of(() => AppTheme.Room),  3.0, "主屏卡片描边"),
            (() => AppTheme.Live,  Of(() => AppTheme.Room),  3.0, "光标所在屏的卡片描边"),
            // 悬停/聚焦态的动作行——见 HoverSurface 的注释，这是第三种正文背景，不是装饰。
            (() => AppTheme.Ink,   HoverSurface, 4.5, "悬停行上的动作名"),
            (() => AppTheme.Faint, HoverSurface, 4.5, "悬停行上的动作描述"),
            (() => AppTheme.Edge,  HoverSurface, 3.0, "悬停行上的未绑定虚线圈"),
            // 见类注释：深色下面自己也得帮忙撑边界，浅色下交给 Edge，只保一条"不能跟底同色"的下限。
            (() => AppTheme.Panel, Of(() => AppTheme.Room), dark ? 1.25 : 1.08, "卡片的面要能从底上浮起来"),
            // 悬停/聚焦本身也得看得出来。这条是补的：此前**没有任何测试**量过"悬停态和常态差多少"，
            // 于是深色下它一路退化到 1.085:1（几乎不可见，且方向还是反的——卡片沉进页面而不是浮起来），
            // 而每一条既有断言都照样绿着，因为它们只问"文字在悬停底上够不够清楚"，从不问"用户看不看得
            // 出来自己悬停在哪一行"。两个主题解出来都是 1.29，下限取 1.25 留一点余量。
            // 写成 (Panel 在前景, HoverSurface 在背景) 而不是反过来——对比度是对称的，但只有背景那一侧
            // 会走 Composite。把 HoverBrush 放前景就是拿它**原始的** RGB 去算：万一它哪天又变回半透明，
            // 读到的是 Ink 的 (234,235,240)，比值大得离谱、断言照样绿。实测过：把 HoverBrush 改回
            // Tint(Ink,0x14)，前景写法下这条测试**不会失败**，正是这个文件开头警告过的那种自欺。
            (() => AppTheme.Panel, HoverSurface, 1.25, "悬停/聚焦的那一行要能跟常态分得开"),
        ];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_pair_clears_the_floor_its_role_owes(bool dark) => host.Invoke(
        () => ThemeProbe.Under(dark, () =>
        {
            var failures = Pairs(dark)
                .Select(p => (p.Where, p.Floor, Ratio: ColorContrast.Ratio(ThemeProbe.Rgb(p.Fg()), p.Bg())))
                .Where(x => x.Ratio < x.Floor)
                .Select(x => $"{x.Where}：{x.Ratio:0.00}:1，低于 {x.Floor}:1")
                .ToList();
            Assert.True(failures.Count == 0,
                $"{ThemeProbe.Name(dark)}主题有 {failures.Count} 处不达标——" +
                $"回 AppTheme 重新解，别按感觉微调：\n  " + string.Join("\n  ", failures));
        }));
}
