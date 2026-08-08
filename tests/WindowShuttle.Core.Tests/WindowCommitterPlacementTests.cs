using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.Core.Tests;

/// <summary>覆盖 WindowCommitter.BuildPlacement/BuildPlacementSteps——最大化窗口还原尺寸损坏（v1 报告
/// 的缺陷：真实机器上 OneCommander 1080×823→900×686、Chrome 3840×2088→4608×2186）的根因是 SetPlacement
/// 在 SetWindowPlacement(SW_SHOWNORMAL) 和 ShowWindow(SW_SHOWMAXIMIZED) 之间制造了一个真实的中间态，
/// 目标应用自己的 resize/WM_DPICHANGED 处理趁机把窗口尺寸改了，Windows 转最大化时把这份被污染的尺寸
/// 现场快照成新的还原尺寸。
///
/// 修复不是简单地把两步砍成一步（那样 rcNormalPosition 确实不再被污染，但真实 150%/125%/125% 三屏上
/// 实测：窗口已经处于最大化态时，Windows 把 showCmd=SW_SHOWMAXIMIZED 的单次 SetWindowPlacement 当成
/// "目标状态没变"的空操作，窗口视觉上根本不会挪到新显示器——跨屏搬运直接失效，3/3 case 全部原地不动，
/// 见 maximized-restore-fix-report.md）。真正的修复是两步都用 SetWindowPlacement、都带上同一个 target
/// rcNormalPosition：第一步强制转成 SW_SHOWNORMAL 逼 Windows 真的转一次状态（让窗口落到目标屏），第二
/// 步转回 SW_SHOWMAXIMIZED 时把 target 再传一遍，无条件覆盖掉中间态里可能发生的任何污染。
///
/// 这里只测 BuildPlacement/BuildPlacementSteps 产出的结构体本身（纯函数，不碰 Win32），不测 Win32
/// 落位效果——那部分只能靠真实窗口验证（见 maximized-restore-fix-report.md）。</summary>
public class WindowCommitterPlacementTests
{
    private static readonly RectPx Target = RectPx.FromLTWH(100, 200, 800, 600);

    [Fact]
    public void Maximized_move_ends_at_showcmd_maximized()
    {
        var move = new PlannedMove(1, ShowState.Maximized, Target, default);
        var wp = WindowCommitter.BuildPlacement(move);
        Assert.Equal(Win32.SW_SHOWMAXIMIZED, wp.showCmd);
    }

    [Fact]
    public void Maximized_move_carries_the_mapped_restore_rect_in_the_same_struct()
    {
        var move = new PlannedMove(1, ShowState.Maximized, Target, default);
        var wp = WindowCommitter.BuildPlacement(move);

        Assert.Equal(Target.Left, wp.rcNormalPosition.L);
        Assert.Equal(Target.Top, wp.rcNormalPosition.T);
        Assert.Equal(Target.Right, wp.rcNormalPosition.R);
        Assert.Equal(Target.Bottom, wp.rcNormalPosition.B);
    }

    [Fact]
    public void Minimized_move_still_writes_showcmd_minimized()
    {
        var move = new PlannedMove(1, ShowState.Minimized, Target, default);
        var wp = WindowCommitter.BuildPlacement(move);
        Assert.Equal(Win32.SW_SHOWMINIMIZED, wp.showCmd);
    }

    [Fact]
    public void Maximized_move_takes_exactly_two_placement_steps_normal_then_maximized()
    {
        var move = new PlannedMove(1, ShowState.Maximized, Target, default);
        var steps = WindowCommitter.BuildPlacementSteps(move);

        // 就是这两行防止 bounce 复活成"单步直接最大化": 单步在真实硬件上跨屏搬运不生效
        // (visibleBoundsOnDstMonitor=False，3/3 case)——两步、且第一步必须是 SW_SHOWNORMAL，
        // 才能逼 Windows 真的把窗口挪到目标屏。
        Assert.Equal(2, steps.Count);
        Assert.Equal(Win32.SW_SHOWNORMAL, steps[0].showCmd);
        Assert.Equal(Win32.SW_SHOWMAXIMIZED, steps[1].showCmd);
    }

    [Fact]
    public void Maximized_move_both_steps_carry_the_identical_target_rect()
    {
        // 第二步必须重新带上 target——不能靠 ShowWindow 之类不带矩形的调用收尾，否则 Windows 会把
        // "中间态里应用可能已经改过的实际尺寸"现场快照成还原尺寸，还原尺寸损坏的缺陷就会复发。
        var move = new PlannedMove(1, ShowState.Maximized, Target, default);
        var steps = WindowCommitter.BuildPlacementSteps(move);

        Assert.Equal(steps[0].rcNormalPosition.L, steps[1].rcNormalPosition.L);
        Assert.Equal(steps[0].rcNormalPosition.T, steps[1].rcNormalPosition.T);
        Assert.Equal(steps[0].rcNormalPosition.R, steps[1].rcNormalPosition.R);
        Assert.Equal(steps[0].rcNormalPosition.B, steps[1].rcNormalPosition.B);
        Assert.Equal(Target.Left, steps[1].rcNormalPosition.L);
    }

    [Fact]
    public void Minimized_move_takes_exactly_one_placement_step()
    {
        var move = new PlannedMove(1, ShowState.Minimized, Target, default);
        var steps = WindowCommitter.BuildPlacementSteps(move);
        Assert.Single(steps);
        Assert.Equal(Win32.SW_SHOWMINIMIZED, steps[0].showCmd);
    }
}
