using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WindowShuttle.Core.Native;
using NotificationOverlay = WindowShuttle.App.NotificationOverlay;
using OverlayWindow = WindowShuttle.App.OverlayWindow;

namespace WindowShuttle.Core.Tests;

/// <summary>两扇浮层窗口都声明了 <c>Topmost="True"</c>，而它们的落位走的是 <c>SetWindowPos</c>
/// （物理像素直落，绕开 WPF 的 DIP 换算）。这两件事凑在一起有一个不会报错的失效模式：
/// <c>SetWindowPos</c> 直接改的是 <c>WS_EX_TOPMOST</c> 这个扩展样式位，而 <c>Window.Topmost</c>
/// 属性不跟着变——属性没变化就不触发 WPF 的 OnTopmostChanged，WPF 永远不会把样式贴回去。于是
/// 窗口在进程剩余的生命周期里都不再置顶，而代码里那句 <c>Topmost="True"</c> 读起来一切正常。
///
/// 对这个应用后果很直接：通知卡和「屏幕编号」浮层的全部意义就是浮在别人的窗口上面。掉了置顶位，
/// 它们会开在当前应用背后——用户什么也没看见，而代码认为自己提示过了。
///
/// 所以这条测试不信任何一方的说法，直接把两边都读出来比对：WPF 的属性值，和 <c>GetWindowLongW</c>
/// 读回来的真实样式位。只要 SetWindowPos 的 hWndInsertAfter 哪天从 HWND_TOP 变成 HWND_NOTOPMOST、
/// 或者有人给落位加上会改 Z 序的标志，这里就会红。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class OverlayTopmostTests(WpfTestHost host)
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x8;
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint h, int i);

    private static bool HasTopmostBit(Window w)
        => (GetWindowLongW(new WindowInteropHelper(w).Handle, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    [Fact]
    public void Identify_overlay_keeps_its_topmost_bit_after_being_positioned()
        => host.Invoke(() =>
        {
            var monitors = MonitorProbe.GetMonitors();
            Assert.NotEmpty(monitors);
            var w = new OverlayWindow(monitors) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                w.Show();                       // SourceInitialized 里就会走一次 OverlayChrome.Place
                w.UpdateLayout();
                Assert.True(w.Topmost, "WPF 侧的 Topmost 属性丢了");
                Assert.True(HasTopmostBit(w), "落位之后 WS_EX_TOPMOST 被抹掉了——窗口不再置顶，而属性还说它是");
            }
            finally { w.Close(); }
        });

    [Fact]
    public void Notification_card_keeps_its_topmost_bit_after_being_positioned()
        => host.Invoke(() =>
        {
            var primary = MonitorProbe.GetMonitors().First(m => m.IsPrimary);
            // ShowOn 是既有的 harness 入口：按指定屏落位，不跟着真实鼠标跑。FadeIn 一上来就把
            // Opacity 设成 0 再往上动画，这里在动画起步前就断言并关掉，用户屏幕上不会闪出来。
            var n = NotificationOverlay.ShowOn(primary, "topmost probe");
            try
            {
                n.UpdateLayout();               // Loaded 里走 OverlayChrome.Place
                Assert.True(n.Topmost, "WPF 侧的 Topmost 属性丢了");
                Assert.True(HasTopmostBit(n), "落位之后 WS_EX_TOPMOST 被抹掉了——通知会开在别人后面");
            }
            finally { n.Close(); }
        });
}
