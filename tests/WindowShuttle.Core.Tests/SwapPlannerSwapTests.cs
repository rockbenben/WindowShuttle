using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class SwapPlannerSwapTests
{
    // 双屏标准布局：1=主屏 1920x1080 @ (0,0)，2=副屏 2560x1440 @ (1920,0)
    private static readonly MonitorInfo M1 = TestData.Mon(1, 0, 0, 1920, 1080, primary: true);
    private static readonly MonitorInfo M2 = TestData.Mon(2, 1920, 0, 2560, 1440);
    private static readonly MonitorInfo[] Both = [M1, M2];

    private static PlanRequest Req(IReadOnlyList<WindowFacts> wins, PointPx cursor,
        int? target = null, int? partner = null, bool skipFs = true)
        => new(Both, wins, WindowShuttleAction.Swap, cursor, target, partner, skipFs);

    // —— 归属判定 ——
    [Fact] public void Owner_is_monitor_with_largest_overlap()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(1600, 100, 800, 600)); // 320px 在屏1、480px 在屏2
        Assert.Equal(2, SwapPlanner.OwnerIndex(w, Both));
    }

    [Fact] public void Minimized_owner_uses_normal_position()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(-32000, -32000, 160, 30),
            state: ShowState.Minimized, normalPos: RectPx.FromLTWH(2000, 100, 800, 600));
        Assert.Equal(2, SwapPlanner.OwnerIndex(w, Both));
    }

    [Fact] public void Orphaned_offscreen_window_maps_to_nearest_monitor()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(9000, 9000, 400, 300)); // 拔掉的屏留下的
        Assert.Equal(2, SwapPlanner.OwnerIndex(w, Both));               // 距屏2更近
    }

    [Fact] public void Orphan_falls_back_to_nearest_center_not_last_or_nonprimary_monitor()
    {
        // 孤儿窗口偏向屏1这一侧更近，但屏2是列表里最后一个/索引更大/唯一非主屏——
        // “就近取巧”的三种偷懒判定全都会猜成屏2，只有真正按中心曼哈顿距离算才会选屏1。
        var w = TestData.Win(1, RectPx.FromLTWH(-5000, -5000, 400, 300));
        Assert.Equal(1, SwapPlanner.OwnerIndex(w, Both));
    }

    [Fact] public void Maximized_owner_uses_window_rect_not_normal_position()
    {
        // BLOCKER 2：归属问的是"现在视觉上在哪"。NormalPosition 留在屏1的坐标范围（还原后会落在那儿），
        // 但窗口此刻是最大化铺满屏2——答案必须是屏2，用 WindowRect 判；如果误用 NormalPosition（跟
        // "带哪份几何走"那道题混为一谈）就会错判成屏1。
        var w = TestData.Win(1, M2.MonitorRect, state: ShowState.Maximized,
            normalPos: RectPx.FromLTWH(100, 100, 800, 600));
        Assert.Equal(2, SwapPlanner.OwnerIndex(w, Both));
    }

    // —— Swap 计划 ——
    [Fact] public void Single_monitor_is_noop()
    {
        var r = SwapPlanner.Plan(new PlanRequest([M1], [], WindowShuttleAction.Swap,
            new PointPx(10, 10), null, null, true));
        Assert.Equal(NoOpReason.OnlyOneMonitor, r.NoOp);
    }

    [Fact] public void Cursor_on_secondary_swaps_both_directions()
    {
        var onPrimary = TestData.Win(1, RectPx.FromLTWH(100, 100, 800, 600), z: 1);
        var onSecondary = TestData.Win(2, RectPx.FromLTWH(2000, 100, 800, 600), z: 0);
        var r = SwapPlanner.Plan(Req([onPrimary, onSecondary], new PointPx(3000, 500)));

        Assert.Equal(2, r.Moves.Count);
        var m1 = r.Moves.Single(m => m.Hwnd == 1);   // 主屏窗口 -> 屏2工作区
        var m2 = r.Moves.Single(m => m.Hwnd == 2);   // 屏2窗口 -> 主屏工作区
        Assert.True(RectPx.OverlapArea(m1.Target, M2.WorkArea) == m1.Target.Area);
        Assert.True(RectPx.OverlapArea(m2.Target, M1.WorkArea) == m2.Target.Area);
        Assert.Equal(2, r.NewSwapPartner);           // 记住这次跟屏2换过
    }

    [Fact] public void Swap_target_stays_inside_workarea_not_monitor_rect()
    {
        // 窗口贴着屏1工作区下边界（任务栏正上方，但仍在屏1物理边框内）。
        // 若映射时误把 WorkArea 换成了 MonitorRect（少扣掉那 40px 任务栏），
        // 目标矩形会往下多探出一截，戳穿屏2工作区的下边界。
        var w = TestData.Win(1, RectPx.FromLTWH(100, 1030, 800, 50));
        var r = SwapPlanner.Plan(Req([w], new PointPx(3000, 500)));
        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M2.WorkArea) == r.Moves[0].Target.Area);
    }

    [Fact] public void Cursor_on_primary_swaps_with_last_partner()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 800, 600));
        var r = SwapPlanner.Plan(Req([w], new PointPx(500, 500), partner: 2));
        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M2.WorkArea) == r.Moves[0].Target.Area);
    }

    [Fact] public void Cursor_on_primary_without_partner_falls_back_to_next_monitor()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 800, 600));
        var r = SwapPlanner.Plan(Req([w], new PointPx(500, 500)));
        Assert.Single(r.Moves);                       // 落到屏2（唯一的下一块）
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M2.WorkArea) == r.Moves[0].Target.Area);
    }

    [Fact] public void Cursor_on_primary_wraps_to_lowest_indexed_monitor()
    {
        // 索引不连续（1、3、7）模拟真实 \\.\DISPLAYn 编号有空洞；主屏恰好是索引最大的那块。
        // 光标落在主屏又没有 partner 时，"下一块"必须是按 Index 排序后取后继、绕回最小的那块——
        // 不能是"列表最后一个"（就是主屏自己）也不能是"另一块里索引更大的"（屏3）。
        var mA = TestData.Mon(1, 0, 0, 1920, 1080);
        var mB = TestData.Mon(3, 1920, 0, 1920, 1080);
        var mC = TestData.Mon(7, 3840, 0, 1920, 1080, primary: true);
        var monitors = new MonitorInfo[] { mA, mB, mC };
        var w = TestData.Win(1, RectPx.FromLTWH(4000, 100, 800, 600)); // 落在主屏(索引7)上

        var r = SwapPlanner.Plan(new PlanRequest(monitors, [w], WindowShuttleAction.Swap,
            new PointPx(4800, 500), null, null, true));

        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, mA.WorkArea) == r.Moves[0].Target.Area);
        Assert.Equal(1, r.NewSwapPartner);
    }

    [Fact] public void Explicit_target_index_wins()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(2000, 100, 800, 600));
        var r = SwapPlanner.Plan(Req([w], new PointPx(3000, 500), target: 1));
        Assert.Single(r.Moves);                       // 屏2 -> 屏1（--to 1）
    }

    [Fact] public void Fullscreen_is_skipped_and_counted()
    {
        var fs = TestData.Win(1, M2.MonitorRect, hasFrame: false);
        var r = SwapPlanner.Plan(Req([fs], new PointPx(3000, 500)));
        Assert.Empty(r.Moves);
        Assert.Equal(1, r.SkippedFullscreen);
    }

    [Fact] public void Fullscreen_moves_when_setting_disabled()
    {
        var fs = TestData.Win(1, M2.MonitorRect, hasFrame: false);
        var r = SwapPlanner.Plan(Req([fs], new PointPx(3000, 500), skipFs: false));
        Assert.Single(r.Moves);
    }

    [Fact] public void Hung_is_skipped_and_counted()
    {
        var hung = TestData.Win(1, RectPx.FromLTWH(2000, 100, 800, 600), hung: true);
        var r = SwapPlanner.Plan(Req([hung], new PointPx(3000, 500)));
        Assert.Empty(r.Moves);
        Assert.Equal(1, r.SkippedHung);
    }

    [Fact] public void Moves_are_ordered_back_to_front()   // §8 反 Z 序提交
    {
        var front = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300), z: 0);
        var back = TestData.Win(2, RectPx.FromLTWH(2100, 200, 400, 300), z: 5);
        var r = SwapPlanner.Plan(Req([front, back], new PointPx(3000, 500)));
        Assert.Equal(2, r.Moves[0].Hwnd);   // z 大（更靠后）的先动
        Assert.Equal(1, r.Moves[1].Hwnd);
    }

    [Fact] public void Minimized_window_swaps_via_normal_position_and_stays_minimized()
    {
        var min = TestData.Win(1, RectPx.FromLTWH(-32000, -32000, 160, 30),
            state: ShowState.Minimized, normalPos: RectPx.FromLTWH(2000, 100, 800, 600));
        var r = SwapPlanner.Plan(Req([min], new PointPx(3000, 500)));
        Assert.Single(r.Moves);
        Assert.Equal(ShowState.Minimized, r.Moves[0].ShowState);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M1.WorkArea) == r.Moves[0].Target.Area);
    }

    [Fact] public void Maximized_window_swaps_via_normal_position_not_monitor_rect()
    {
        // BLOCKER 2: a maximized window's WindowRect is the whole monitor (M2.MonitorRect below).
        // Mapping THAT instead of NormalPosition would carry a ~2560x1440 rect into the swap and
        // WindowCommitter would write it into rcNormalPosition — the user's restored-down window
        // would fill the destination monitor instead of keeping its 800x600 restore size.
        var max = TestData.Win(1, M2.MonitorRect, state: ShowState.Maximized,
            normalPos: RectPx.FromLTWH(2100, 200, 800, 600));
        var r = SwapPlanner.Plan(Req([max], new PointPx(3000, 500)));
        Assert.Single(r.Moves);
        Assert.Equal(ShowState.Maximized, r.Moves[0].ShowState);

        // Exact expected value: MonitorMapper.MapRect(M2.WorkArea, M1.WorkArea, 800x600 @ (2100,200)).
        var target = r.Moves[0].Target;
        Assert.Equal(new RectPx(138, 148, 732, 594), target);

        // Sanity net: the pre-fix (WindowRect-based) mapping would have produced a rect close to
        // M1's whole work area (~1920x1040) fitted from a 2560x1440 source — nowhere near this size.
        Assert.True(target.Width < 700 && target.Height < 500);
    }
}
