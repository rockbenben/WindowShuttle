using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>贴边分屏的窗口跨屏搬运要落在目标屏的**同一个贴边位**上（MonitorMapper.SnapSlotTarget），
/// 而不是被等比缩放成一扇"大约一半"的浮动窗。</summary>
public class SnapSlotTests
{
    // 长宽比刻意不同的两块工作区：等比缩放和格位映射在这对屏上给出的答案必然不一样，
    // 混用了立刻露馅。
    private static readonly RectPx SrcWork = RectPx.FromLTWH(0, 0, 2560, 1400);
    private static readonly RectPx DstWork = RectPx.FromLTWH(2560, 0, 3840, 2120);

    [Fact] public void A_left_half_lands_on_the_left_half_of_the_target()
    {
        var snapped = RectPx.FromLTWH(0, 0, 1280, 1400);
        Assert.Equal(RectPx.FromLTWH(2560, 0, 1920, 2120),
            MonitorMapper.SnapSlotTarget(SrcWork, DstWork, snapped));
    }

    /// <summary>真实的贴边窗口 GetWindowRect 带着 DWM 的隐形边框悬出（实测 8~16px），
    /// 识别必须容得下——按精确相等判，真机上一次都不会命中。</summary>
    [Fact] public void The_dwm_border_overhang_still_counts_as_snapped()
    {
        var real = RectPx.FromLTWH(-8, 0, 1296, 1408);        // 左半 + 每边悬出 8
        Assert.Equal(RectPx.FromLTWH(2560, 0, 1920, 2120),
            MonitorMapper.SnapSlotTarget(SrcWork, DstWork, real));
    }

    [Fact] public void Quarters_and_thirds_map_to_the_same_slot()
    {
        // 右下四分之一
        Assert.Equal(RectPx.FromLTWH(2560 + 1920, 1060, 1920, 1060),
            MonitorMapper.SnapSlotTarget(SrcWork, DstWork, RectPx.FromLTWH(1280, 700, 1280, 700)));
        // 中间竖三分（超宽屏的常用布局）
        Assert.Equal(RectPx.FromLTWH(2560 + 1280, 0, 1280, 2120),
            MonitorMapper.SnapSlotTarget(SrcWork, DstWork, RectPx.FromLTWH(853, 0, 854, 1400)));
    }

    /// <summary>不在任何格位上的窗口必须返回 null——这一条是"格位识别不吸走普通窗口"的全部保证。
    /// 差得比容差远一点点的也算普通窗口。</summary>
    [Fact] public void A_floating_window_is_not_a_slot()
    {
        Assert.Null(MonitorMapper.SnapSlotTarget(SrcWork, DstWork, RectPx.FromLTWH(200, 150, 900, 600)));
        // 左半但每条边都差 40（超出 32 的容差）
        Assert.Null(MonitorMapper.SnapSlotTarget(SrcWork, DstWork, RectPx.FromLTWH(40, 40, 1280, 1400)));
    }

    /// <summary>整条链路：SwapPlanner.Map 对贴边窗口要带**格位**走，不带 RestoreRect。
    /// 贴边窗口的 rcNormalPosition 存的是贴边之前的浮动尺寸——拿它识别永远不会命中，
    /// 这正是"搬一下就丢分屏"的根源，所以这条测试的窗口刻意让两份矩形不同。</summary>
    [Fact] public void Map_carries_the_slot_not_the_restore_rect()
    {
        var from = TestData.Mon(1, 0, 0, 2560, 1440, taskbar: 40);
        var to = TestData.Mon(2, 2560, 0, 3840, 2160, taskbar: 40);
        var snapped = TestData.Win(1, RectPx.FromLTWH(0, 0, 1280, 1400),
            normalPos: RectPx.FromLTWH(300, 200, 900, 600));     // 贴边前是一扇 900x600 的浮动窗
        var move = SwapPlanner.Map(snapped, from, to);
        Assert.Equal(RectPx.FromLTWH(2560, 0, 1920, 2120), move.Target);

        // 同一扇窗不贴边时照旧走 RestoreRect 的等比缩放——格位识别不许劫持普通搬运。
        var floating = TestData.Win(2, RectPx.FromLTWH(300, 200, 900, 600),
            normalPos: RectPx.FromLTWH(300, 200, 900, 600));
        var plain = SwapPlanner.Map(floating, from, to);
        Assert.NotEqual(RectPx.FromLTWH(2560, 0, 1920, 2120), plain.Target);
        Assert.Equal(MonitorMapper.MapRect(from.WorkArea, to.WorkArea, floating.RestoreRect), plain.Target);
    }

    /// <summary>最大化窗口不进格位路径：它带 NormalPosition 走 placement 那条路（§7），
    /// 它的可见矩形铺满整块屏，跟"贴边"是两回事。</summary>
    [Fact] public void A_maximized_window_never_takes_the_slot_path()
    {
        var from = TestData.Mon(1, 0, 0, 2560, 1440, taskbar: 40);
        var to = TestData.Mon(2, 2560, 0, 3840, 2160, taskbar: 40);
        var maxed = TestData.Win(1, RectPx.FromLTWH(-8, -8, 2576, 1456),
            state: ShowState.Maximized, normalPos: RectPx.FromLTWH(300, 200, 900, 600));
        var move = SwapPlanner.Map(maxed, from, to);
        Assert.Equal(MonitorMapper.MapRect(from.WorkArea, to.WorkArea, maxed.RestoreRect), move.Target);
    }
}
