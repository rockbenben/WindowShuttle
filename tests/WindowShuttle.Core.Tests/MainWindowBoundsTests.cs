using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;
using MainWindow = WindowShuttle.App.MainWindow;
using Settings = WindowShuttle.App.Settings;

namespace WindowShuttle.Core.Tests;

/// <summary>Part 2 verification (§window-bounds-fit): the six real-world resolutions named in the
/// task, checked three ways —
///  (a) <see cref="MainWindow.ComputeStartupBounds"/> is pure math, exercised directly against
///      synthetic <see cref="MonitorInfo"/> at each DPI (no real monitor at these exact six
///      DPI/resolution pairs exists on the machine running this suite, so this part is necessarily
///      simulated — see the report for which numbers are simulated vs. real);
///  (b) for each of the same six, a real window — technically on-screen but Opacity=0 and
///      ShowInTaskbar=false, so nothing is ever visible to anyone (see the in-method comment for why
///      EnsureHandle alone isn't enough: WPF's compositor only lays out windows that are actually
///      shown) — is sized in DIP to what (a) computed and its real WPF layout is checked to confirm
///      the map/action-list/footer are all present, non-zero, and nothing overflows;
///  (c) one fully real, zero-simulation pass: the actual <see cref="MonitorProbe"/> reading of
///      whatever monitors are really attached to the machine running this suite feeds the real
///      <c>ApplyStartupBounds</c> codepath, and a DPI-aware <c>GetWindowRect</c> confirms the
///      resulting physical rect against (a) run on that same real monitor list.</summary>
[Collection(MainWindowWpfCollection.Name)]
public class MainWindowBoundsTests(WpfTestHost host)
{
    // 六档分辨率，物理像素 + 缩放比 + 任务栏物理高度（100% 用 40px，125%/150% 用 48px——
    // 都是 Windows 任务栏的典型值，凑不到真机就只能这样估，跟任务里给的工作区数字对得上）。
    public static readonly (string Label, int PhysW, int PhysH, double Dpi, int TaskbarPx)[] Resolutions =
    [
        ("1366x768@100%", 1366, 768, 1.00, 40),
        ("1920x1080@100%", 1920, 1080, 1.00, 40),
        ("1920x1080@125%", 1920, 1080, 1.25, 48),
        ("1920x1080@150%", 1920, 1080, 1.50, 48),
        ("2560x1440@125%", 2560, 1440, 1.25, 48),
        ("3840x2160@150%", 3840, 2160, 1.50, 48),
    ];

    public static IEnumerable<object[]> ResolutionData()
        => Resolutions.Select(r => new object[] { r.Label, r.PhysW, r.PhysH, r.Dpi, r.TaskbarPx });

    // 直接引应用自己的常量，不再各写一份字面量：这几条测的就是"应用首次启动的那个尺寸在六档
    // 分辨率下都放得下"，一旦两边能各走各的，改了 XAML 这里照样绿，验的却是一个早已不存在的尺寸。
    private const double DefaultWidthDip = MainWindow.DefaultWidthDip, DefaultHeightDip = MainWindow.DefaultHeightDip;
    private const double MinWidthDip = MainWindow.MinWidthDip, MinHeightDip = MainWindow.MinHeightDip;

    private static MonitorInfo Mon(int physW, int physH, double dpi, int taskbarPx)
        => TestData.Mon(1, 0, 0, physW, physH, primary: true, taskbar: taskbarPx, dpi: dpi);

    // ---------- (a) 纯数学：物理像素夹取 ----------

    [Theory]
    [MemberData(nameof(ResolutionData))]
    public void First_run_size_fits_work_area_and_respects_the_new_minimums(
        string label, int physW, int physH, double dpi, int taskbarPx)
    {
        var mon = Mon(physW, physH, dpi, taskbarPx);
        var (x, y, w, h) = MainWindow.ComputeStartupBounds(
            [mon], new Settings(), DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);

        // 落在工作区物理矩形内，不越界。
        Assert.InRange(x, mon.WorkArea.Left, mon.WorkArea.Left + mon.WorkArea.Width - w);
        Assert.InRange(y, mon.WorkArea.Top, mon.WorkArea.Top + mon.WorkArea.Height - h);
        Assert.True(x + w <= mon.WorkArea.Right, $"{label}: right edge {x + w} > work area right {mon.WorkArea.Right}");
        Assert.True(y + h <= mon.WorkArea.Bottom, $"{label}: bottom edge {y + h} > work area bottom {mon.WorkArea.Bottom}");

        // 从不小于新的最小值（物理像素）。
        Assert.True(w >= (int)Math.Round(MinWidthDip * dpi) - 1, $"{label}: width {w} under the minimum");
        Assert.True(h >= (int)Math.Round(MinHeightDip * dpi) - 1, $"{label}: height {h} under the minimum");
    }

    // 1920×1080@150% 是任务里点名的最坏档：默认 760 DIP 高会溢出工作区 72px。夹取后必须严格
    // 小于等于工作区（不是"差不多"）。
    [Fact]
    public void Worst_case_1920x1080_at_150pct_no_longer_overflows()
    {
        var mon = Mon(1920, 1080, 1.50, 48);
        var (_, _, w, h) = MainWindow.ComputeStartupBounds(
            [mon], new Settings(), DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);
        Assert.True(h <= mon.WorkArea.Height, $"height {h}px still overflows the {mon.WorkArea.Height}px work area");
        Assert.True(w <= mon.WorkArea.Width);
    }

    [Fact]
    public void Restoring_a_saved_position_on_a_still_connected_monitor_keeps_it_unchanged()
    {
        var mon = Mon(1920, 1080, 1.25, 48);
        // 存的尺寸必须同时高过两条线，这条测试才测的是"原样恢复"：物理最小值（760×480 DIP × 1.25）
        // 和恢复下限（860×620 DIP × 1.25 = 1075×775）。1200×900 物理 = 960×720 DIP，两条都过。
        var cfg = new Settings { WindowLeft = 100, WindowTop = 80, WindowWidth = 1200, WindowHeight = 900 };
        var (x, y, w, h) = MainWindow.ComputeStartupBounds(
            [mon], cfg, DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);
        Assert.Equal((100, 80, 1200, 900), (x, y, w, h));
    }

    // ---------- 恢复下限：一个摆不下内容的存档尺寸不该被当成"用户偏好"重现 ----------
    // 真实踩到过：某次窗口被弄成正好等于拖拽下限（760×480 DIP）并落了盘，此后每次打开都是那个尺寸,
    // 动作区退回单列、只露一张卡，而改默认高度完全无效——存档分支根本轮不到默认值。

    [Theory]
    [InlineData(760, 480, "正好等于拖拽下限")]
    [InlineData(859, 700, "只是宽度不够")]
    [InlineData(1000, 619, "只是高度不够")]
    public void A_saved_size_below_the_restore_floor_falls_back_to_the_default(
        int savedDipW, int savedDipH, string why)
    {
        const double Dpi = 1.5;
        var mon = Mon(3840, 2160, Dpi, 72);
        var cfg = new Settings
        {
            WindowLeft = 400, WindowTop = 300,
            WindowWidth = (int)(savedDipW * Dpi), WindowHeight = (int)(savedDipH * Dpi),
        };
        var (x, y, w, h) = MainWindow.ComputeStartupBounds(
            [mon], cfg, DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);

        Assert.Equal((int)Math.Round(DefaultWidthDip * Dpi), w);
        Assert.Equal((int)Math.Round(DefaultHeightDip * Dpi), h);
        // 尺寸不采信，位置也一并不采信——否则会得到一个"默认大小、但摆在上次那个角落"的怪组合。
        Assert.NotEqual(400, x);
        int leftGap = x - mon.WorkArea.Left, rightGap = mon.WorkArea.Right - (x + w);
        Assert.True(Math.Abs(leftGap - rightGap) <= 1, $"{why}: 回落后没有居中");
    }

    [Fact]
    public void A_saved_size_exactly_on_the_restore_floor_is_still_honoured()
    {
        const double Dpi = 1.5;
        var mon = Mon(3840, 2160, Dpi, 72);
        int w0 = (int)Math.Ceiling(MainWindow.RestoreMinWidthDip * Dpi);
        int h0 = (int)Math.Ceiling(MainWindow.RestoreMinHeightDip * Dpi);
        var cfg = new Settings { WindowLeft = 400, WindowTop = 300, WindowWidth = w0, WindowHeight = h0 };
        var (x, y, w, h) = MainWindow.ComputeStartupBounds(
            [mon], cfg, DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);
        Assert.Equal((400, 300, w0, h0), (x, y, w, h));
    }

    // 存得进去的尺寸，下次就必须认得——这是"记住窗口位置"唯一的不变量，也是恢复下限最容易破的地方。
    //
    // 下限是一个固定的 DIP 数，而 ComputeStartupBounds 末尾会按工作区把尺寸夹小。工作区矮到一定
    // 程度，它自己吐出来的尺寸就永远低于自己的下限：存档分支从此永远不成立，「记住窗口位置」在
    // 那类屏上永久失效，用户还没有任何办法把窗口调到"够大"。1920×1080@175%（13 寸笔记本的出厂
    // 默认缩放）就是这样一块屏——工作区 1010px，能给出的最大高度 926px，而下限是 620×1.75=1085px。
    //
    // 断言必须落在**位置**上，不能只比尺寸：坏掉的那条路径回落到"默认尺寸、主屏居中"，而在这类
    // 矮屏上默认尺寸本来就被夹成同一个值，只比尺寸的话有 bug 也照样绿。
    [Theory]
    [InlineData("1920x1080@175%", 1920, 1080, 1.75, 70)]
    [InlineData("1920x1080@150%", 1920, 1080, 1.50, 48)]
    [InlineData("1366x768@125%", 1366, 768, 1.25, 48)]
    [InlineData("1366x768@150%", 1366, 768, 1.50, 48)]
    [InlineData("2560x1440@125%", 2560, 1440, 1.25, 48)]
    public void A_size_this_screen_can_actually_produce_is_honoured_next_launch(
        string label, int physW, int physH, double dpi, int taskbarPx)
    {
        var mon = Mon(physW, physH, dpi, taskbarPx);
        var first = MainWindow.ComputeStartupBounds(
            [mon], new Settings(), DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);

        // 用户把窗口往右挪了 11px，其余不动——SaveWindowBounds 存下来的就是这个物理矩形。
        var cfg = new Settings
        {
            WindowLeft = first.X + 11, WindowTop = first.Y,
            WindowWidth = first.W, WindowHeight = first.H,
        };
        var again = MainWindow.ComputeStartupBounds(
            [mon], cfg, DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);

        Assert.Equal((first.X + 11, first.Y, first.W, first.H), again);
        Assert.True(again.X != first.X, $"{label}: 存档被丢弃，窗口又回到了居中的默认位置");
    }

    // 拖拽下限和恢复下限必须是两个不同的数：相等的话，"拖到最小"就会变成一个能被存下来、
    // 下次原样重现的尺寸，正是这条约束要防的那一幕。
    [Fact]
    public void The_restore_floor_sits_above_the_drag_floor()
    {
        Assert.True(MainWindow.RestoreMinWidthDip > MainWindow.MinWidthDip);
        Assert.True(MainWindow.RestoreMinHeightDip > MainWindow.MinHeightDip);
        Assert.True(MainWindow.RestoreMinWidthDip <= MainWindow.DefaultWidthDip);
        Assert.True(MainWindow.RestoreMinHeightDip <= MainWindow.DefaultHeightDip);
    }

    // 存档的那块屏已经拔掉（比如笔记本上次带了个外接屏，这次没接）：不能把窗口摆在一块不存在的
    // 屏的坐标上——必须回落到"主屏居中、默认尺寸"，这正是本轮要修的"重开时开在屏幕外面"的坏体验。
    [Fact]
    public void Restoring_onto_a_now_disconnected_monitor_falls_back_to_primary_centered()
    {
        var primary = Mon(1920, 1080, 1.0, 40);
        // 存档位置落在 (3000,0) 附近，那块屏已经不在当前的屏幕列表里了。
        var cfg = new Settings { WindowLeft = 3000, WindowTop = 100, WindowWidth = 900, WindowHeight = 700 };
        var (x, y, w, h) = MainWindow.ComputeStartupBounds(
            [primary], cfg, DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);

        Assert.InRange(x, primary.WorkArea.Left, primary.WorkArea.Left + primary.WorkArea.Width - w);
        Assert.InRange(y, primary.WorkArea.Top, primary.WorkArea.Top + primary.WorkArea.Height - h);
        // 居中：左右/上下留白应大致相等。
        int leftGap = x - primary.WorkArea.Left, rightGap = primary.WorkArea.Right - (x + w);
        Assert.True(Math.Abs(leftGap - rightGap) <= 1, $"not centered: left={leftGap} right={rightGap}");
    }

    [Fact]
    public void Restoring_a_saved_position_that_no_longer_fits_the_shrunk_work_area_is_clamped()
    {
        // 上次存的时候工作区比现在大（比如任务栏挪到了侧边，或者换了块小屏但没被判定为"新屏"——
        // 这里直接构造"当年那块屏缩水了"的场景：同一块屏，工作区比存档尺寸还小）。
        var mon = Mon(1024, 768, 1.0, 40);   // work area 1024x728
        var cfg = new Settings { WindowLeft = 0, WindowTop = 0, WindowWidth = 1200, WindowHeight = 900 };
        var (x, y, w, h) = MainWindow.ComputeStartupBounds(
            [mon], cfg, DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);
        Assert.True(w <= mon.WorkArea.Width);
        Assert.True(h <= mon.WorkArea.Height);
        Assert.InRange(x, mon.WorkArea.Left, mon.WorkArea.Left + mon.WorkArea.Width - w);
        Assert.InRange(y, mon.WorkArea.Top, mon.WorkArea.Top + mon.WorkArea.Height - h);
    }

    // ---------- (b) 六档分辨率的真实布局验证。EnsureHandle 建句柄不够——WPF 的合成/布局管线只
    // 处理真正"上屏"（WS_VISIBLE）的窗口，纯 Measure/Arrange 或只建句柄不 Show() 会让 DesiredSize/
    // ActualWidth 停在 (0,0)（本轮踩的另一个坑）。所以这里真的 Show()，但 Opacity=0 + ShowInTaskbar
    // =false：窗口技术上"在屏幕上"（合成器照常跑一遍真布局），肉眼却什么都看不见、任务栏也不会
    // 冒出新条目——不是"开在屏幕外"那种意义上的不可见，是"开着但透明"，效果一样：没有人会看见它。
    // 用完立刻 Close()（Settings.CloseToTray 默认 true，Close() 只会 Hide()，不会碰 Application）。
    //
    // Width/Height 设成 ComputeStartupBounds 换算回来的 DIP 值——EnsureHandle/Show 会顺带触发生产
    // 代码里 OnSourceInitialized → ApplyStartupBounds 那条自动路径，走的是这台机器真实的屏幕列表
    // （三屏 150%/125%/125%，一次性诊断量出来的——巧的是这正是 docs/manual-checks.md 建议的
    // "最接近真实用户环境"配置）。这条自动路径不会覆盖我要的目标尺寸：这两个 DIP 值已经满足
    // MinWidth/MinHeight，DIP 层面的下限判定跟"物理层面用哪块真屏的缩放比去换算"无关，两者不会
    // 打架——早先的写法直接拿合成矩形砸一次物理像素版 SetWindowPos，结果在真主屏 150% 的
    // WM_GETMINMAXINFO 强制下限面前被顶成了 1140×720（不是要的 960×680）；教训是：凡要跟真 HWND
    // 打交道，宁可把 DIP 值交给 WPF 自己按它认定的当前 DPI 去换算，不要越过它直接怼一个跟合成
    // DPI 绑定的物理像素矩形。
    [Fact]
    public void SixResolutions_layout_keeps_map_actions_and_footer_present_and_unclipped()
    {
        host.Invoke(() =>
        {
            foreach (var (label, physW, physH, dpi, taskbarPx) in Resolutions)
            {
                // 每档分辨率都要重新回到"从没存过窗口位置"这个起点——上一次迭代 Close() 时，
                // MainWindow.OnClosing 会把那一次的真实收尾位置存回 App.Cfg（这正是 Part 2 要
                // 交付的"记住上次位置"那部分功能本身），不重置的话，第二档开始测的就不再是
                // "首次启动"分支，而是意外撞上了"恢复上次位置"分支——同一个共享的 App.Cfg
                // 是这条坑的根源，不是 ComputeStartupBounds 算错了。
                global::WindowShuttle.App.App.Cfg = new Settings();
                var mon = Mon(physW, physH, dpi, taskbarPx);
                var (_, _, physW2, physH2) = MainWindow.ComputeStartupBounds(
                    [mon], global::WindowShuttle.App.App.Cfg, DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);
                double dipW = physW2 / dpi, dipH = physH2 / dpi;

                var window = new MainWindow { Opacity = 0, ShowInTaskbar = false };
                // 屏幕装不下这一档就跳过它：一扇真实的顶级窗口越不过屏幕宽度，那时量到的不是
                // 布局算错了，是这台机器摆不下（GitHub runner 的虚拟显示器只有约 1044 DIP 宽）。
                if (!window.ShowAtExactly(dipW, dipH)) { window.Close(); continue; }
                Assert.True(window.LayoutCanvas.ActualWidth > 0 && window.LayoutCanvas.ActualHeight > 0,
                    $"{label} ({dipW:0}x{dipH:0} DIP): monitor map collapsed to zero size");
                Assert.True(window.ActionList.ActualWidth > 0 && window.ActionList.ActualHeight > 0,
                    $"{label} ({dipW:0}x{dipH:0} DIP): action list collapsed to zero size");
                Assert.True(window.MouseChordCaution.ActualWidth > 0 && window.MouseChordCaution.ActualHeight > 0,
                    $"{label} ({dipW:0}x{dipH:0} DIP): footer collapsed to zero size");
                // 三处地标各自的宽度都不超过窗口自身——没有任何一处比窗口本身还宽（裁切/溢出的信号）。
                Assert.True(window.LayoutCanvas.ActualWidth <= window.ActualWidth + 0.5, $"{label}: map wider than window");
                Assert.True(window.ActionList.ActualWidth <= window.ActualWidth + 0.5, $"{label}: action list wider than window");
                window.Close();
            }

            // 最小尺寸本身也要过一遍同样的断言——"缩到最小也不能裁切/够不着"是 Part 2 的明确
            // 要求，不只是六档分辨率恰好没踩到最小值这一件事。
            //
            // 跟循环体内一样必须先重置 App.Cfg：上一轮迭代 Close() 时 OnClosing 会把那次的收尾位置
            // 存进这个共享的 Settings，不重置的话这里根本走不到"首次启动"分支——窗口会被恢复成
            // 960×760，这三条断言就在测最小尺寸之外的东西，等于空转。
            global::WindowShuttle.App.App.Cfg = new Settings();
            var atMin = new MainWindow
                { Width = MinWidthDip, Height = MinHeightDip, Opacity = 0, ShowInTaskbar = false };
            atMin.Show();
            atMin.UpdateLayout();
            Assert.True(atMin.LayoutCanvas.ActualWidth > 0 && atMin.LayoutCanvas.ActualHeight > 0,
                "at minimum size: monitor map collapsed to zero size");
            Assert.True(atMin.ActionList.ActualWidth > 0 && atMin.ActionList.ActualHeight > 0,
                "at minimum size: action list collapsed to zero size");
            Assert.True(atMin.MouseChordCaution.ActualWidth > 0 && atMin.MouseChordCaution.ActualHeight > 0,
                "at minimum size: footer collapsed to zero size");
            atMin.Close();
        });
    }

    // ---------- (c) 一次真正端到端、零模拟的验证：不碰 MonitorSource，直接用这台机器真实的
    // 三块屏（150%/125%/125%，跟 docs/manual-checks.md 建议的配置一致），让生产代码那条唯一的
    // ApplyStartupBounds 路径正常跑一次，再用 DPI-aware 的 GetWindowRect 读回物理矩形，核对
    // 跟 ComputeStartupBounds 用同一份真实屏幕列表算出来的完全一致。度量前必须先声明本线程的
    // DPI 感知并断言真的生效了——manual-checks.md 记录过三轮踩坑都是量的人（不是 WindowShuttle）
    // DPI-unaware 造成的。EnsureHandle 从不 Show()/映射，屏幕上什么都不会出现。 ----------

    private const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

    [Fact]
    public void Real_machine_startup_bounds_are_DPIaware_measured_end_to_end()
    {
        host.Invoke(() =>
        {
            var prevDpiContext = GetThreadDpiAwarenessContext();
            try
            {
                var newContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                Assert.NotEqual(0, newContext);   // 0 = 调用本身失败/不支持
                Assert.True(AreDpiAwarenessContextsEqual(
                    GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2),
                    "harness did not actually become PER_MONITOR_AWARE_V2 — the number below cannot be trusted");

                var realMonitors = MonitorProbe.GetMonitors();
                Assert.NotEmpty(realMonitors);
                global::WindowShuttle.App.App.Cfg = new Settings();
                var expected = MainWindow.ComputeStartupBounds(
                    realMonitors, global::WindowShuttle.App.App.Cfg, DefaultWidthDip, DefaultHeightDip, MinWidthDip, MinHeightDip);

                var window = new MainWindow();
                var hwnd = new WindowInteropHelper(window).EnsureHandle();   // 建句柄，绝不 Show()

                Assert.True(Win32.GetWindowRect(hwnd, out var r), "GetWindowRect failed");
                Assert.Equal((expected.X, expected.Y, expected.W, expected.H), (r.L, r.T, r.R - r.L, r.B - r.T));
            }
            finally { SetThreadDpiAwarenessContext(prevDpiContext); }
        });
    }
}
