using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WindowShuttle.Core.Native;

namespace WindowShuttle.Core.Tests;

/// <summary>「搬过去了，人却没到最前」的修复守卫。
///
/// 起因：抬升原来是一句 <c>SetWindowPos(HWND_TOP, SWP_NOACTIVATE)</c>。Windows 不让"不持有前台的
/// 进程"把窗口插到前台窗口上面，而且**照样返回 true**——没有错误码，看代码永远看不出来。真实三屏
/// 复现（目标屏被一扇铺满工作区的窗口占着且它持有前台，用手势把另一屏的窗划过去）：那扇刚搬过去的
/// 窗停在 Z 序第 3 位。改成 TOPMOST→NOTOPMOST 两步之后同场景稳定第 0 位。
///
/// 那条 Z 序断言依赖"别人持有前台"，测试进程里造不出来，留在 docs/manual-checks.md 里手工验。
/// 这里守的是两步法自己的两种**静默**失效——它们都不依赖前台，且一旦发生都是把用户的窗口搞坏：
/// 只走第一步（把别人的窗永久钉在最前），或者漏掉置顶豁免（把用户自己设的「总在最前」给撤了）。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class RaiseTests(WpfTestHost host)
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x8;
    private const uint GW_HWNDNEXT = 2;
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint h, int i);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint h, uint cmd);

    private static bool HasTopmostBit(nint h) => (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>a 是不是压在 b 上面：从 a 沿 Z 序往下走，走得到 b 就说明 a 更靠前。</summary>
    private static bool IsAbove(nint a, nint b)
    {
        for (var h = GetWindow(a, GW_HWNDNEXT); h != 0; h = GetWindow(h, GW_HWNDNEXT))
            if (h == b) return true;
        return false;
    }

    /// <summary>1×1 的无边框窗口：这两条测试只读扩展样式位，窗口长什么样无所谓，越不打扰跑测试的人
    /// 越好。
    ///
    /// 两处都是踩出来的，别照直觉改回去：
    /// ① **不能**用 Opacity=0 来藏。普通 Window 的 Opacity&lt;1 会去改 AllowsTransparency，
    ///    而它在 Show() 之后不允许改，当场抛 InvalidOperationException。
    /// ② 必须显式给一个空 Style。这个 fixture 的 Application 合并了 WPF-UI 的 ControlsDictionary，
    ///    里面有一条**以 Window 为键的隐式样式**，会在 Show() 解析样式时给窗口贴 AllowsTransparency
    ///    —— 同一个异常，但栈在 StyleHelper.ApplyStyleOrTemplateValue 里，跟 ① 长得完全不一样。
    ///    显式赋值任何 Style 都会让隐式样式不再命中。</summary>
    private static Window Bare() => new()
    {
        Width = 1, Height = 1, Left = 0, Top = 0,
        WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        Style = new Style(typeof(Window)),
    };

    /// <summary>两步法必须走完第二步。只走第一步的话窗口留在置顶层——用户随手划一下，那扇窗就
    /// 永远盖在别人上面了，而且他根本不知道是谁干的。</summary>
    [Fact]
    public void Raising_a_normal_window_does_not_leave_it_topmost()
        => host.Invoke(() =>
        {
            var w = Bare();
            try
            {
                w.Show();
                var h = new WindowInteropHelper(w).Handle;
                Assert.False(HasTopmostBit(h), "前提没成立：这扇窗一开始就不该是置顶的");
                WindowCommitter.Raise(h);
                Assert.False(HasTopmostBit(h), "抬完之后窗口还留在置顶层——两步法只走了第一步");
            }
            finally { w.Close(); }
        });

    /// <summary>带 keepBelow 时：抬到那一扇的**正下方**，而不是抬到最顶。
    ///
    /// 地图拖放靠这条：搬过去的窗要露出来，但不能盖住用户此刻还拖着的那张地图。这条能确定性地测，
    /// 恰恰因为它**往下**插——"插到前台之上"才需要抢前台权限，往下插不需要，所以不受前台锁影响
    /// （RaiseCoversForegroundTests 那条就只能跳过）。</summary>
    [Fact]
    public void Raising_with_a_keepBelow_lands_just_under_that_window()
        => host.Invoke(() =>
        {
            var keep = Bare();
            var moved = Bare();
            try
            {
                keep.Show();
                moved.Show();
                var kh = new WindowInteropHelper(keep).Handle;
                var mh = new WindowInteropHelper(moved).Handle;

                // 先把 moved 沉到 keep 下面，制造"需要抬"的局面
                WindowCommitter.Raise(kh);
                Assert.True(IsAbove(kh, mh), "前提没成立：keep 应该先在 moved 上面");

                WindowCommitter.Raise(mh, keepBelow: kh);
                Assert.True(IsAbove(kh, mh), "抬完之后 moved 盖住了 keep —— keepBelow 没起作用");
                Assert.Equal(mh, GetWindow(kh, GW_HWNDNEXT));   // 就在它下一格，不是沉在别处
            }
            finally { moved.Close(); keep.Close(); }
        });

    /// <summary>keepBelow 传 0 就是原来的行为：抬到最顶。上一条不能把默认路径也改掉。</summary>
    [Fact]
    public void Raising_without_a_keepBelow_still_goes_to_the_top()
        => host.Invoke(() =>
        {
            var other = Bare();
            var moved = Bare();
            try
            {
                other.Show();
                moved.Show();
                var oh = new WindowInteropHelper(other).Handle;
                var mh = new WindowInteropHelper(moved).Handle;
                WindowCommitter.Raise(oh);
                Assert.True(IsAbove(oh, mh), "前提没成立");
                WindowCommitter.Raise(mh);
                Assert.True(IsAbove(mh, oh), "抬完之后没到 other 上面");
            }
            finally { moved.Close(); other.Close(); }
        });

    /// <summary>本来就置顶的窗口要整个跳过。走一遍两步法会把它降级成普通窗口——那是在替用户
    /// 撤销他自己设的「总在最前」，而他做的只是把窗口挪到另一块屏。</summary>
    [Fact]
    public void Raising_an_already_topmost_window_keeps_it_topmost()
        => host.Invoke(() =>
        {
            var w = Bare();
            w.Topmost = true;
            try
            {
                w.Show();
                var h = new WindowInteropHelper(w).Handle;
                Assert.True(HasTopmostBit(h), "前提没成立：这扇窗应该是置顶的");
                WindowCommitter.Raise(h);
                Assert.True(HasTopmostBit(h), "抬完之后置顶位没了——把用户设的「总在最前」撤掉了");
                Assert.True(w.Topmost, "WPF 侧的 Topmost 属性也得还在");
            }
            finally { w.Close(); }
        });
}
