using System.Globalization;
using System.Windows;
using WindowShuttle.App.I18n;
using MainWindow = WindowShuttle.App.MainWindow;
using Settings = WindowShuttle.App.Settings;

namespace WindowShuttle.Core.Tests;

/// <summary>RTL（阿拉伯语）下整扇窗镜像是对的，但显示器地图**不能**跟着镜像——它不是文字，是显示器
/// 在桌面上的实物摆位图。
///
/// 这不是假想缺陷：加上 18 语言之后第一次用 <c>--shots</c> 渲染阿拉伯语，三屏「2 1 3」当场画成了
/// 「3 1 2」（Canvas.SetLeft 在 RTL 下从右边缘量起），屏 3 明明在用户右手边却画在地图左边，拖放的
/// 落点跟着一起反；卡片里的「2560 × 1440」也被 bidi 翻成「1440 × 2560」。修法是把这块画布的
/// FlowDirection 钉死成 LeftToRight，这条测试盯着那颗钉子。
///
/// 归 MainWindowWpfCollection：跟另外两个碰 MainWindow 的测试共用那把锁（见该 collection 的说明），
/// 同时也归 UI 文化那一档——它要切进程级 UI 文化，不能跟并行断言文案的测试撞上。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class MainWindowRtlTests(WpfTestHost host) : IDisposable
{
    private readonly CultureInfo _entry = CultureInfo.CurrentUICulture;

    /// <summary>文化必须在**它被改的那条线程上**还原。
    ///
    /// Strings.ApplyCulture 写的是调用线程的 CurrentUICulture 加上进程级的 DefaultThreadCurrentUICulture，
    /// 而这里的调用发生在 host.Invoke 里、也就是 WpfTestHost 那条共享 STA 线程上。此前 Dispose 在
    /// xunit 线程上还原，只还得回 xunit 线程自己那份和静态默认值——一条线程的 CurrentUICulture 一旦
    /// 被显式赋过值，静态默认值就不再管它，于是 STA 线程会一直停在 ar 或 en（看哪条测试最后跑）。
    /// 同一个 collection 里后面的 MainWindowCompactLayoutTests 按 Action_*_Desc 建动作卡再断言
    /// 视口高度，而描述在 760 DIP 下换几行是随语言变的——那条测试于是随 xunit 的调度顺序时绿时红。</summary>
    public void Dispose() => host.Invoke(() =>
    {
        CultureInfo.CurrentUICulture = _entry;
        CultureInfo.DefaultThreadCurrentUICulture = _entry;
    });

    [Fact]
    public void Monitor_map_never_mirrors_even_when_the_window_does()
    {
        host.Invoke(() =>
        {
            Strings.ApplyCulture("ar");
            Assert.True(Strings.IsRightToLeft, "阿拉伯语没被认成 RTL，这条测试后面什么都验不到");

            global::WindowShuttle.App.App.Cfg = new Settings();
            var window = new MainWindow { FlowDirection = FlowDirection.RightToLeft };   // 真实启动时 App 的元数据覆盖做的就是这件事
            // 必须 Close：MainWindow 的构造函数订阅了静态的 AppTheme.Changed，退订挂在 Closed 上
            // （见那边的注释）。不关就等于在共享 STA 线程上永久攥着一扇已经没人用的窗口。
            try
            {
                // 窗口自己镜像（文案、键位、复选框都该跟着阅读方向走）……
                Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
                // ……但地图不跟：它画的是物理摆位，跟人的脖子转向一致，不跟阅读方向。
                Assert.Equal(FlowDirection.LeftToRight, window.LayoutCanvas.FlowDirection);
            }
            finally { window.Close(); }
        });
    }

    // 反向守一次：LTR 语言下这颗钉子不该把别的东西也钉歪——窗口和地图必须都是 LeftToRight。
    [Fact]
    public void Left_to_right_languages_leave_both_the_window_and_the_map_unmirrored()
    {
        host.Invoke(() =>
        {
            Strings.ApplyCulture("en");
            Assert.False(Strings.IsRightToLeft);

            global::WindowShuttle.App.App.Cfg = new Settings();
            var window = new MainWindow();
            try
            {
                Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
                Assert.Equal(FlowDirection.LeftToRight, window.LayoutCanvas.FlowDirection);
            }
            finally { window.Close(); }
        });
    }
}
