using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MainWindow = WindowShuttle.App.MainWindow;
using Strings = WindowShuttle.App.I18n.Strings;
using Settings = WindowShuttle.App.Settings;

namespace WindowShuttle.Core.Tests;

/// <summary>矮窗口下"动作区不能被饿死"。
///
/// 这条测试来自一次真实的分辨率巡检：把 <c>--shots</c> 从"只改高"扩成真正的宽×高矩阵之后，
/// 头两张图就露馅——1920×1080@150%（极常见的 15" 笔记本）上五行动作只剩一行，第二行从中间被切断；
/// 拖到最小尺寸（760×480）时动作区**一行都不剩**，"动作"标题底下直接是设置栏，整个主功能面消失。
///
/// 成因是三块区域抢同一份高度，而优先级正好反了：地图带有一条无条件的下限（MapMinHeight=140），
/// 动作区是 DockPanel 的填充元素、一条下限都没有——于是被压缩到零的永远是用户真正来配置的那一块，
/// 而只作参照用的地图永远拿得到它那 140。
///
/// 断言口径刻意选"至少完整露出一张动作卡"而不是某个像素值：卡片高度会随字号/语言变，但"一张都看不到"
/// 这件事在任何语言下都是坏的。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class MainWindowCompactLayoutTests(WpfTestHost host)
{
    /// <summary>量的是 ScrollViewer 的**视口**高度，不是 ActionList 自己的 ActualHeight。
    ///
    /// 这个区别就是这类缺陷此前逃过一整套测试的原因：<c>MainWindowBoundsTests</c> 里那条
    /// "六档分辨率"断言写的是 <c>ActionList.ActualHeight > 0</c>，而 ActionList 是 ScrollViewer
    /// **里面**那个 ItemsControl——它永远是十张卡摞起来的自然高度，视口被压到零它照样几百像素高。
    /// 那条断言恒真，跟用户看不看得见动作卡毫无关系。视口才是"用户能看到多少"。</summary>
    private static (double Viewport, double FirstCard) Measure(MainWindow w)
    {
        DependencyObject node = w.ActionList;
        while (node is not ScrollViewer) node = VisualTreeHelper.GetParent(node)!;
        var first = (FrameworkElement)w.ActionList.Items[0]!;
        return (((ScrollViewer)node).ViewportHeight,
                first.ActualHeight + first.Margin.Top + first.Margin.Bottom);
    }

    private double _band;

    /// <summary>每条断言都要逐语言跑一遍，而不是只跑默认那一门。
    ///
    /// 这一条是实测补上的：中文下最小尺寸能完整露出一张动作卡，德语下露不出来，而这条测试原来只跑
    /// 中性资源（＝中文），于是**永远是绿的**。两处随语言变的高度叠在一起：卡片描述在窄窗口下德语要
    /// 折成两行，而设置栏的复选框在德语下要排四行、中文只排两行——光设置栏就多吃掉约 100 DIP。
    ///
    /// 语言取的是三门"最长"的：德语（动作名与描述最长）、俄语和印尼语（设置栏那几个复选框最长，
    /// 见 MainWindow.xaml 里 StartupTie 旁边那段注释量过的余量）。null = 跟随系统，落到中性资源。</summary>
    public static TheoryData<string?> LongestLanguages()
    {
        var d = new TheoryData<string?>();
        foreach (var lang in new string?[] { null, "de", "ru", "id" }) d.Add(lang);
        return d;
    }

    private void At(string? lang, double width, double height, Action<double, double> check) =>
        host.Invoke(() =>
        {
            var restore = CultureInfo.CurrentUICulture;
            global::WindowShuttle.App.App.Cfg = new Settings();
            // 顺序是承重的：{loc:Loc} 在 XAML 加载那一刻取值，换语言换在建窗口之后就完全没有效果，
            // 测试会拿着一扇中文窗口报告"德语没问题"。
            Strings.ApplyCulture(lang);
            var w = new MainWindow { Opacity = 0, ShowInTaskbar = false };
            w.ShowAtExactly(width, height);
            try
            {
                _band = w.LayoutCanvas.ActualHeight;
                var (viewport, card) = Measure(w);
                check(viewport, card);
            }
            finally
            {
                w.Close();
                CultureInfo.CurrentUICulture = restore;
                CultureInfo.DefaultThreadCurrentUICulture = restore;
            }
        });

    // 用户拖得到的最小尺寸，直接引常量——写死数字的话，改了下限这条测试会继续验一个应用早已
    // 不允许的尺寸，绿着但验的是别的东西（MainWindowBoundsTests 的注释里记着同一个教训）。
    [Theory, MemberData(nameof(LongestLanguages))]
    public void At_the_minimum_drag_size_at_least_one_action_card_is_fully_visible(string? lang)
        => At(lang, MainWindow.MinWidthDip, MainWindow.MinHeightDip, (viewport, card) => Assert.True(viewport >= card,
            $"[{lang ?? "系统"}] 动作区可视高度只有 {viewport:0}，装不下一张 {card:0} 高的动作卡——整个主功能面在最小尺寸下消失了（地图带 {_band:0}）"));

    /// <summary>动作名在任何宽度下都不许被裁成省略号。
    ///
    /// 名字那个 TextBlock 带 <c>TextTrimming.CharacterEllipsis</c>，所以卡片一窄它就无声地截断——
    /// 界面看着仍然整齐，只是「Поменять экраны местами」变成了「Поменять экраны мест…」。
    ///
    /// 这条守的是**分列门槛**，而不是某个像素值：切一列会让每张卡变窄，门槛定低了就会出现
    /// "把窗口拉宽反而看到名字被截断"。实测扫过来的边界是卡宽约 420（384 截断、426 完整），
    /// 门槛因此解成 列数×420＋列间距。取的宽度是每一档**刚成立**的那一点——最窄的两列和最窄的
    /// 三列，也就是最容易截断的两处。
    ///
    /// 语言取俄语：最长的两条动作名都在它这儿（另见 LongestLanguages 的说明）。</summary>
    /// <remarks>宽度**由门槛常量算出**，不写死：写死的话，谁把门槛调低，探针仍然停在安全宽度上，
    /// 这条测试就永远绿——第一版正是这么写的，把门槛改回旧值 760/1160 之后它照样通过。</remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Action_names_are_never_trimmed_to_an_ellipsis(int columns)
        => host.Invoke(() =>
        {
            // 每一档**刚成立**的那一点，也就是该档最窄、最容易截断的地方。
            double target = (columns == 2 ? MainWindow.TwoColumnMinWidth
                                          : MainWindow.ThreeColumnMinWidth) + 1;
            var restore = CultureInfo.CurrentUICulture;
            global::WindowShuttle.App.App.Cfg = new Settings();
            Strings.ApplyCulture("ru");
            var w = new MainWindow { Opacity = 0, ShowInTaskbar = false };
            w.ShowAtExactly(target + 60, 900);
            try
            {
                // 窗口宽和动作区宽之间那点差额（边距 + 滚动条）**实测**，不写死。第一版写死 48，
                // 结果窗口开得太窄、动作区掉到门槛以下，测试实际跑的是单列——单列每张卡几百像素宽，
                // 当然不截断，于是把门槛改回旧值它照样绿。
                w.Width = target + (w.ActualWidth - w.ActionList.ActualWidth);
                w.UpdateLayout();

                // 前提断言：没真的处在 N 列状态就当场报错，而不是安安静静地验一个别的东西。
                Assert.True(w.ActionColumns == columns,
                    $"前提没成立：想验 {columns} 列，实际是 {w.ActionColumns} 列" +
                    $"（动作区 {w.ActionList.ActualWidth:0}，门槛 {target - 1:0}）");
                var trimmed = new List<string>();
                foreach (Border row in w.ActionList.Items)
                {
                    var name = ((Grid)row.Child).Children.OfType<TextBlock>().First();
                    // 拿一个不受约束的副本量"这段文字本来要多宽"，而不是去 Measure 已经在布局里的那个
                    // ——对在树上的元素再调一次 Measure 会打乱当前这趟布局。
                    var probe = new TextBlock { Text = name.Text, FontSize = name.FontSize, FontWeight = name.FontWeight };
                    probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (probe.DesiredSize.Width > name.ActualWidth + 0.5)
                        trimmed.Add($"{name.Text}（要 {probe.DesiredSize.Width:0}，只给了 {name.ActualWidth:0}）");
                }
                Assert.True(trimmed.Count == 0,
                    $"{columns} 列刚成立时（动作区 {w.ActionList.ActualWidth:0} 宽）有 {trimmed.Count} 条动作名被裁成省略号——" +
                    $"回 MainWindow.Hotkeys.cs 重解分列门槛：\n  " + string.Join("\n  ", trimmed));
            }
            finally
            {
                w.Close();
                CultureInfo.CurrentUICulture = restore;
                CultureInfo.DefaultThreadCurrentUICulture = restore;
            }
        });

    // 1120×632 = 1920×1080@150% 上 ComputeStartupBounds 算出来的真实启动尺寸，最常见的笔记本档。
    // 这一档要求更高：首次启动那一眼至少要看得到两行，不然"动作是一张列表"这件事都传达不出去。
    [Theory, MemberData(nameof(LongestLanguages))]
    public void On_a_1080p150_laptop_at_least_two_rows_of_actions_are_visible(string? lang)
        => At(lang, 1120, 632, (viewport, card) => Assert.True(viewport >= card * 2,
            $"[{lang ?? "系统"}] 动作区可视高度只有 {viewport:0}，两行卡片要 {card * 2:0}——1080p@150% 是最常见的笔记本配置"));
}
