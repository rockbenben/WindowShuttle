using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WindowShuttle.Core.Native;

namespace WindowShuttle.Core.Tests;

/// <summary>「提到最前」跟**正持有焦点的那扇窗**之间的关系——两种入口要的是相反的东西，这里把
/// 两边一起钉住。
///
/// · 手势 / 送去主屏 / 送去下一块屏：**必须**压过前台窗口。目标屏上那扇铺满工作区的窗往往正好
///   持有焦点，压不过去就是用户报的"搬过去了，人没到最前"（WindowCommitter.Raise 的两步法就是
///   为此存在的）。
/// · 地图拖放：**不许**压过它——那一刻的前台正是 WindowShuttle 自己的主窗口，压过去就等于用户
///   刚放下的窗把他还拖着的地图盖掉。这条走 MovePlan.KeepBelow。
///
/// 代码审查在第二条上给出过两个相反的结论，所以这里不靠读代码，直接把两种参数各跑一次真实提交。
/// 拿不到前台就跳过而不是假装通过：这两条断言只有在"F 真的是前台"时才说明问题，而测试进程能不能
/// 抢到前台取决于跑它的人当时在干什么（前台锁）。骗过去的绿色比红色更糟。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class RaiseCoversForegroundTests(WpfTestHost host)
{
    private const uint GW_HWNDNEXT = 2;
    [DllImport("user32.dll")] private static extern nint GetWindow(nint h, uint cmd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    /// <summary>a 是不是压在 b 上面：从 a 沿 Z 序往下走，走得到 b 就说明 a 更靠前。</summary>
    private static bool IsAbove(nint a, nint b)
    {
        for (var h = GetWindow(a, GW_HWNDNEXT); h != 0; h = GetWindow(h, GW_HWNDNEXT))
            if (h == b) return true;
        return false;
    }

    private static Window Bare(int left, int top) => new()
    {
        Width = 400, Height = 300, Left = left, Top = top,
        WindowStyle = WindowStyle.None, ShowInTaskbar = false,
        Style = new Style(typeof(Window)),      // 挡掉 WPF-UI 的隐式样式，见 RaiseTests.Bare
    };

    [Fact]
    public void Raise_goes_over_the_foreground_window_unless_KeepBelow_says_otherwise()
    {
        var (gotFg, plainAbove, keptBelowAbove) = host.Invoke(() =>
        {
            var f = Bare(100, 100);      // 「正在用的那扇」——地图拖放时就是 WindowShuttle 主窗口
            var m = Bare(900, 100);      // 被搬的那扇
            try
            {
                f.Show(); m.Show();
                f.Activate();
                var fh = new WindowInteropHelper(f).Handle;
                var mh = new WindowInteropHelper(m).Handle;
                if (GetForegroundWindow() != fh) return (false, false, false);

                var work = MonitorProbe.GetMonitors()[0].WorkArea;
                var target = new RectPx(120, 120, 400, 300);          // 跟 F 重叠

                // ① 手势那条路：不带 KeepBelow
                WindowCommitter.Raise(mh, keepBelow: fh);             // 先沉到 F 下面，制造需要抬的局面
                WindowCommitter.Commit(new MovePlan(
                    [new PlannedMove(mh, ShowState.Normal, target, work)],
                    0, 0, null, null, RaiseMoved: true));
                bool plain = IsAbove(mh, fh);

                // ② 地图拖放那条路：带上"别盖住 F"
                WindowCommitter.Raise(mh, keepBelow: fh);
                WindowCommitter.Commit(new MovePlan(
                    [new PlannedMove(mh, ShowState.Normal, target, work)],
                    0, 0, null, null, RaiseMoved: true, KeepBelow: fh));
                bool kept = IsAbove(mh, fh);

                return (true, plain, kept);
            }
            finally { f.Close(); m.Close(); }
        });

        if (!gotFg)
            Assert.Skip("拿不到前台（前台锁），这两条断言在这种情况下说明不了问题");
        Assert.True(plainAbove,
            "不带 KeepBelow 时没能压过前台窗口——目标屏上那扇占着焦点的窗会把它挡住，" +
            "用户看到的就是「搬过去了，人没到最前」");
        Assert.False(keptBelowAbove,
            "带了 KeepBelow 还是压了上去——地图拖放时这就是主窗口，用户还拖着的地图会被盖掉");
    }
}
