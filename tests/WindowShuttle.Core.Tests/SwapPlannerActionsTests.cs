using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class SwapPlannerActionsTests
{
    private static readonly MonitorInfo M1 = TestData.Mon(1, 0, 0, 1920, 1080, primary: true);
    private static readonly MonitorInfo M2 = TestData.Mon(2, 1920, 0, 2560, 1440);
    private static readonly MonitorInfo[] Both = [M1, M2];

    private static PlanRequest Req(WindowShuttleAction a, IReadOnlyList<WindowFacts> wins,
        PointPx cursor, int? partner = null)
        => new(Both, wins, a, cursor, null, partner, true);

    // —— SwapTop：只换两边各自最前的一个 ——
    [Fact] public void SwapTop_moves_only_the_frontmost_of_each_side()
    {
        var p1 = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300), z: 2);
        var p2 = TestData.Win(2, RectPx.FromLTWH(150, 150, 400, 300), z: 4);
        var s1 = TestData.Win(3, RectPx.FromLTWH(2000, 100, 400, 300), z: 0);
        var s2 = TestData.Win(4, RectPx.FromLTWH(2100, 200, 400, 300), z: 1);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.SwapTop, [p1, p2, s1, s2], new PointPx(3000, 500)));
        Assert.Equal(2, r.Moves.Count);
        Assert.Contains(r.Moves, m => m.Hwnd == 1);   // 主屏最前 (z=2)
        Assert.Contains(r.Moves, m => m.Hwnd == 3);   // 屏2最前 (z=0)
    }

    [Fact] public void SwapTop_degrades_to_one_way_when_one_side_is_empty()
    {
        var s1 = TestData.Win(3, RectPx.FromLTWH(2000, 100, 400, 300), z: 0);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.SwapTop, [s1], new PointPx(3000, 500)));
        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M1.WorkArea) == r.Moves[0].Target.Area);
    }

    [Fact] public void SwapTop_with_no_windows_is_noop()
    {
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.SwapTop, [], new PointPx(3000, 500)));
        Assert.Equal(NoOpReason.NothingToDo, r.NoOp);
    }

    [Fact] public void SwapTop_picks_frontmost_by_zorder_even_when_listed_last()
    {
        // 列表顺序刻意与 Z 序相反：若实现偷懒取"每侧第一个可移动窗口"而不是真按 ZOrder 找最前，
        // 这里会选错——只有真正 MinBy(ZOrder) 才能选对。
        var pBack = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300), z: 5);
        var pFront = TestData.Win(2, RectPx.FromLTWH(150, 150, 400, 300), z: 1);
        var sBack = TestData.Win(3, RectPx.FromLTWH(2000, 100, 400, 300), z: 3);
        var sFront = TestData.Win(4, RectPx.FromLTWH(2100, 200, 400, 300), z: 0);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.SwapTop, [pBack, pFront, sBack, sFront], new PointPx(3000, 500)));
        Assert.Equal(2, r.Moves.Count);
        Assert.Contains(r.Moves, m => m.Hwnd == 2);   // 主屏最前 (z=1)，非列表首个 (hwnd 1)
        Assert.Contains(r.Moves, m => m.Hwnd == 4);   // 屏2最前 (z=0)，非列表首个 (hwnd 3)
    }

    [Fact] public void SwapTop_skip_counts_survive_into_plan_with_moves()
    {
        var p1 = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300), z: 2);
        var s1 = TestData.Win(3, RectPx.FromLTWH(2000, 100, 400, 300), z: 0);
        var hung = TestData.Win(5, RectPx.FromLTWH(150, 150, 400, 300), z: 1, hung: true);
        var fsWin = TestData.Win(6, M2.MonitorRect, hasFrame: false);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.SwapTop, [p1, s1, hung, fsWin], new PointPx(3000, 500)));
        Assert.Equal(2, r.Moves.Count);
        Assert.Equal(1, r.SkippedHung);
        Assert.Equal(1, r.SkippedFullscreen);
    }

    [Fact] public void SwapTop_noop_still_reports_skip_counts()
    {
        var hung = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300), hung: true);
        var fsWin = TestData.Win(2, M2.MonitorRect, hasFrame: false);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.SwapTop, [hung, fsWin], new PointPx(3000, 500)));
        Assert.Equal(NoOpReason.NothingToDo, r.NoOp);
        Assert.Equal(1, r.SkippedHung);
        Assert.Equal(1, r.SkippedFullscreen);
    }

    // —— ToPrimary：鼠标下的窗口 ——
    [Fact] public void ToPrimary_moves_topmost_window_under_cursor()
    {
        var under = TestData.Win(1, RectPx.FromLTWH(2000, 100, 800, 600), z: 1);
        var above = TestData.Win(2, RectPx.FromLTWH(2200, 300, 400, 300), z: 0); // 也盖住光标
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary, [under, above], new PointPx(2300, 400)));
        Assert.Single(r.Moves);
        Assert.Equal(2, r.Moves[0].Hwnd);             // z 最小者胜
    }

    [Fact] public void ToPrimary_on_desktop_is_cursor_not_on_window()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300));
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary, [w], new PointPx(3500, 1200)));
        Assert.Equal(NoOpReason.CursorNotOnWindow, r.NoOp);
    }

    [Fact] public void ToPrimary_when_already_on_primary_is_noop()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300));
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary, [w], new PointPx(200, 200)));
        Assert.Equal(NoOpReason.AlreadyOnTarget, r.NoOp);
    }

    [Fact] public void ToPrimary_ignores_minimized_window_even_if_its_stale_rect_covers_cursor()
    {
        // 生产环境里最小化窗口的 WindowRect 是 -32000 垃圾值，天然不可能包住光标——
        // 这里故意给它一个"合理"的屏幕坐标矩形，只是为了让 ShowState != Minimized 这道判断
        // 成为唯一挡住它的东西；否则这个测试测不出该判断被删掉的回归。
        var minimized = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300), state: ShowState.Minimized, z: 0);
        var behind = TestData.Win(2, RectPx.FromLTWH(2000, 100, 400, 300), z: 1); // 同一块矩形，排在它后面
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary, [minimized, behind], new PointPx(2100, 200)));
        Assert.Single(r.Moves);
        Assert.Equal(2, r.Moves[0].Hwnd);
    }

    [Fact] public void ToPrimary_skip_counts_survive_into_plan_with_a_move()
    {
        var target = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300));
        var hung = TestData.Win(2, RectPx.FromLTWH(2000, 100, 400, 300), hung: true);
        var fsWin = TestData.Win(3, M2.MonitorRect, hasFrame: false);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary, [target, hung, fsWin], new PointPx(2100, 200)));
        Assert.Single(r.Moves);
        Assert.Equal(1, r.SkippedHung);
        Assert.Equal(1, r.SkippedFullscreen);
    }

    [Fact] public void ToPrimary_noop_still_reports_skip_counts()
    {
        var hung = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300), hung: true);
        var fsWin = TestData.Win(2, M2.MonitorRect, hasFrame: false);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary, [hung, fsWin], new PointPx(3500, 1200)));
        Assert.Equal(NoOpReason.CursorNotOnWindow, r.NoOp);
        Assert.Equal(1, r.SkippedHung);
        Assert.Equal(1, r.SkippedFullscreen);
    }

    // —— Gather：全收拢，含屏外孤儿 ——
    [Fact] public void Gather_pulls_everything_to_primary_including_orphans()
    {
        var onSecondary = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300));
        var orphan = TestData.Win(2, RectPx.FromLTWH(9000, 9000, 400, 300));   // §2.2 拔屏遗留
        var onPrimary = TestData.Win(3, RectPx.FromLTWH(100, 100, 400, 300));
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.Gather, [onSecondary, orphan, onPrimary], new PointPx(0, 0)));
        Assert.Equal(2, r.Moves.Count);                // 已在主屏的不动
        Assert.DoesNotContain(r.Moves, m => m.Hwnd == 3);
        Assert.All(r.Moves, m => Assert.True(RectPx.OverlapArea(m.Target, M1.WorkArea) == m.Target.Area));
    }

    [Fact] public void Gather_skip_counts_survive_into_plan_with_moves()
    {
        var onSecondary = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300));
        var hung = TestData.Win(2, RectPx.FromLTWH(2000, 100, 400, 300), hung: true);
        var fsWin = TestData.Win(3, M2.MonitorRect, hasFrame: false);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.Gather, [onSecondary, hung, fsWin], new PointPx(0, 0)));
        Assert.Single(r.Moves);
        Assert.Equal(1, r.SkippedHung);
        Assert.Equal(1, r.SkippedFullscreen);
    }

    [Fact] public void Gather_noop_still_reports_skip_counts()
    {
        var hung = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300), hung: true);
        var fsWin = TestData.Win(2, M2.MonitorRect, hasFrame: false);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.Gather, [hung, fsWin], new PointPx(0, 0)));
        Assert.Equal(NoOpReason.NothingToDo, r.NoOp);
        Assert.Equal(1, r.SkippedHung);
        Assert.Equal(1, r.SkippedFullscreen);
    }

    // —— ToNext：光标窗口送往下一块屏（循环）；带 TargetIndex 直送指定屏 ——
    private static readonly MonitorInfo M3 = TestData.Mon(3, 1920 + 2560, 0, 1920, 1080);

    private static PlanRequest Req3(WindowShuttleAction a, IReadOnlyList<WindowFacts> wins,
        PointPx cursor, int? target = null)
        => new([M1, M2, M3], wins, a, cursor, target, null, true);

    [Fact] public void ToNext_moves_cursor_window_to_the_next_monitor_to_the_right()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300));      // 屏1
        var r = SwapPlanner.Plan(Req3(WindowShuttleAction.ToNext, [w], new PointPx(200, 200)));
        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M2.WorkArea) == r.Moves[0].Target.Area);
    }

    [Fact] public void ToNext_wraps_around_from_the_last_monitor_to_the_first()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(1920 + 2560 + 100, 100, 400, 300));   // 屏3
        var r = SwapPlanner.Plan(Req3(WindowShuttleAction.ToNext, [w], new PointPx(1920 + 2560 + 200, 200)));
        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M1.WorkArea) == r.Moves[0].Target.Area);
    }

    [Fact] public void ToNext_with_target_index_goes_straight_to_that_monitor()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300));      // 屏1 → 直送屏3
        var r = SwapPlanner.Plan(Req3(WindowShuttleAction.ToNext, [w], new PointPx(200, 200), target: 3));
        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M3.WorkArea) == r.Moves[0].Target.Area);
    }

    [Fact] public void ToNext_with_target_index_of_current_monitor_is_already_on_target()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300));
        var r = SwapPlanner.Plan(Req3(WindowShuttleAction.ToNext, [w], new PointPx(200, 200), target: 1));
        Assert.Equal(NoOpReason.AlreadyOnTarget, r.NoOp);
    }

    [Fact] public void ToNext_without_a_window_under_the_cursor_is_noop()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300));
        var r = SwapPlanner.Plan(Req3(WindowShuttleAction.ToNext, [w], new PointPx(1000, 1000)));
        Assert.Equal(NoOpReason.CursorNotOnWindow, r.NoOp);
    }

    [Fact] public void ToNext_with_one_monitor_is_noop()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300));
        var r = SwapPlanner.Plan(new PlanRequest([M1], [w], WindowShuttleAction.ToNext,
            new PointPx(200, 200), null, null, true));
        Assert.Equal(NoOpReason.OnlyOneMonitor, r.NoOp);
    }

    // 真实三屏的反例：物理上是 2 | 1 | 3（左中右），系统编号却是 1,2,3。上面几条 ToNext 用例的
    // M1/M2/M3 恰好编号顺序＝摆放顺序，按哪种排都过，测不出这个区别——这一条专门补上。
    private static readonly MonitorInfo L2 = TestData.Mon(2, -2560, 0, 2560, 1440);
    private static readonly MonitorInfo C1 = TestData.Mon(1, 0, 0, 3840, 2160, primary: true);
    private static readonly MonitorInfo R3 = TestData.Mon(3, 3840, 0, 1920, 1080);
    private static readonly MonitorInfo[] Physical = [C1, L2, R3];   // 顺序故意打乱，排序自己去做

    private static PlanRequest ReqPhys(IReadOnlyList<WindowFacts> wins, PointPx cursor)
        => new(Physical, wins, WindowShuttleAction.ToNext, cursor, null, null, true);

    [Fact] public void ToNext_cycles_by_screen_position_not_by_monitor_index()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300));                  // 中间那块（编号 1）
        var r = SwapPlanner.Plan(ReqPhys([w], new PointPx(200, 200)));
        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, R3.WorkArea) == r.Moves[0].Target.Area,
            "中间那块的右边是屏 3，不是编号上紧挨着的屏 2");
    }

    [Fact] public void ToNext_wraps_from_the_rightmost_screen_to_the_leftmost()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(3940, 100, 400, 300));                 // 最右（编号 3）
        var r = SwapPlanner.Plan(ReqPhys([w], new PointPx(4040, 200)));
        Assert.Single(r.Moves);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, L2.WorkArea) == r.Moves[0].Target.Area,
            "到头绕回最左边那块（屏 2），而不是回主屏");
    }

    // ══ 只动光标"指着"的那一扇，绝不越过它去够背后那一扇 ═══════════════════════════════════
    //
    // 实测撞见的：把光标停在 WindowShuttle 自己的窗口上按「送去下一块屏」，被搬走的是藏在它背后、
    // 用户根本看不见的另一扇窗。原因是命中判定写成了"在**能搬的**窗口里找最上面那一扇"，而我们
    // 从不搬自己，于是自己那扇不在候选里、被直接跳过。挡在前面的窗口不可搬时，正确答案是什么都
    // 不做，不是伸手去够它后面的。

    [Fact] public void Our_own_window_in_front_blocks_the_one_behind_it()
    {
        var mine = TestData.Win(1, RectPx.FromLTWH(100, 100, 800, 600), own: true, z: 0);   // 我们自己，在最前
        var other = TestData.Win(2, RectPx.FromLTWH(100, 100, 800, 600), z: 5);             // 藏在后面
        var r = SwapPlanner.Plan(Req3(WindowShuttleAction.ToNext, [mine, other], new PointPx(300, 300)));
        Assert.Equal(NoOpReason.CursorNotOnWindow, r.NoOp);
        Assert.Empty(r.Moves);
    }

    [Fact] public void A_skipped_fullscreen_window_in_front_also_blocks_the_one_behind_it()
    {
        // 全屏窗口＝铺满 M1 且无边框；开着"跳过全屏"时它不可搬，但它确实挡在最前面
        var game = TestData.Win(1, M1.MonitorRect, hasFrame: false, z: 0);
        var other = TestData.Win(2, RectPx.FromLTWH(100, 100, 800, 600), z: 5);
        var r = SwapPlanner.Plan(Req3(WindowShuttleAction.ToPrimary, [game, other], new PointPx(300, 300)));
        Assert.Equal(NoOpReason.CursorNotOnWindow, r.NoOp);
    }

    [Fact] public void The_window_actually_pointed_at_still_moves_normally()
    {
        var front = TestData.Win(1, RectPx.FromLTWH(100, 100, 800, 600), z: 0);
        var behind = TestData.Win(2, RectPx.FromLTWH(100, 100, 800, 600), z: 5);
        var r = SwapPlanner.Plan(Req3(WindowShuttleAction.ToNext, [front, behind], new PointPx(300, 300)));
        Assert.Equal(front.Hwnd, Assert.Single(r.Moves).Hwnd);      // 搬的是最前那一扇，不是后面那扇
    }

    // —— 方向手势：划向哪边就送到那边的屏 ——
    [Fact] public void Toward_picks_the_nearest_screen_on_the_dominant_axis()
    {
        // Physical: 2 | 1 | 3（左中右）。从中间那块往左划 → 最左那块；往右 → 最右那块。
        Assert.Equal(L2.Index, SwapPlanner.MonitorToward(C1, -120, 5, Physical)!.Index);
        Assert.Equal(R3.Index, SwapPlanner.MonitorToward(C1, 120, -5, Physical)!.Index);
    }

    [Fact] public void Toward_returns_null_when_nothing_lies_that_way()
    {
        Assert.Null(SwapPlanner.MonitorToward(L2, -200, 0, Physical));   // 最左那块再往左，没有了
        Assert.Null(SwapPlanner.MonitorToward(C1, 0, 200, Physical));    // 一排横屏，下面没有
        Assert.Null(SwapPlanner.MonitorToward(C1, 0, 0, Physical));      // 没有位移就没有方向
    }

    /// <summary>只取主轴：斜着划按较大的那个分量算。真实桌面上屏幕排成行或列，斜向没有对应物，
    /// 硬做四象限只会把手抖解释成一次搬运。</summary>
    [Fact] public void Toward_uses_the_dominant_axis_only()
    {
        Assert.Equal(R3.Index, SwapPlanner.MonitorToward(C1, 100, 60, Physical)!.Index);   // 右下 → 右
        Assert.Null(SwapPlanner.MonitorToward(C1, 60, 100, Physical));                     // 下右 → 下，没有
    }

    /// <summary>竖排也要成立：上下摆的两块屏，往下划就该拿到下面那块。</summary>
    [Fact] public void Toward_works_for_stacked_screens()
    {
        var top = TestData.Mon(1, 0, 0, 1920, 1080, primary: true);
        var bottom = TestData.Mon(2, 0, 1080, 1920, 1080);
        MonitorInfo[] stack = [top, bottom];
        Assert.Equal(bottom.Index, SwapPlanner.MonitorToward(top, 0, 150, stack)!.Index);
        Assert.Equal(top.Index, SwapPlanner.MonitorToward(bottom, 0, -150, stack)!.Index);
        Assert.Null(SwapPlanner.MonitorToward(top, 150, 0, stack));      // 左右没有
    }

    // —— RaiseMoved：只有「指着一扇窗送过去」才提到最前；批量动作必须保住相对层叠顺序（§8）——
    [Fact] public void Single_window_sends_raise_the_moved_window()
    {
        var w = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300));
        Assert.True(SwapPlanner.Plan(Req3(WindowShuttleAction.ToNext, [w], new PointPx(2100, 200))).RaiseMoved);
        Assert.True(SwapPlanner.Plan(Req3(WindowShuttleAction.ToPrimary, [w], new PointPx(2100, 200))).RaiseMoved);
    }

    [Fact] public void Batch_actions_never_raise()
    {
        var a = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300));
        var b = TestData.Win(2, RectPx.FromLTWH(2100, 200, 400, 300));
        Assert.False(SwapPlanner.Plan(Req(WindowShuttleAction.Gather, [a, b], new PointPx(2100, 200))).RaiseMoved);
        Assert.False(SwapPlanner.Plan(Req(WindowShuttleAction.Swap, [a, b], new PointPx(2100, 200))).RaiseMoved);
        Assert.False(SwapPlanner.Plan(Req(WindowShuttleAction.SwapTop, [a, b], new PointPx(2100, 200))).RaiseMoved);
    }

    // —— Rescue：只救与所有屏零交叠的窗口，钳进主屏工作区；已在屏上的一根手指都不碰 ——
    [Fact] public void Rescue_pulls_only_fully_offscreen_windows_into_the_primary_work_area()
    {
        var stranded = TestData.Win(1, RectPx.FromLTWH(6000, 100, 800, 600));   // 拔掉的屏留下的坐标
        var onScreen = TestData.Win(2, RectPx.FromLTWH(100, 100, 400, 300));
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.Rescue, [stranded, onScreen], new PointPx(0, 0)));
        Assert.Single(r.Moves);
        Assert.Equal((nint)1, r.Moves[0].Hwnd);
        Assert.True(RectPx.OverlapArea(r.Moves[0].Target, M1.WorkArea) == r.Moves[0].Target.Area);
        Assert.Equal(800, r.Moves[0].Target.Width);                             // 救援不改尺寸，只平移
    }

    // 部分露出的窗口不算失踪——用户还够得着它，救援不该碰（碰了就超出救援的职责）。
    [Fact] public void Rescue_leaves_partially_visible_windows_alone()
    {
        var peeking = TestData.Win(1, RectPx.FromLTWH(-700, 100, 800, 600));    // 右侧 100px 还在屏1上
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.Rescue, [peeking], new PointPx(0, 0)));
        Assert.Equal(NoOpReason.NothingToDo, r.NoOp);
    }

    // 最小化窗口按 NormalPosition（还原后出现的位置）判失踪，不按 -32000 的占位矩形——
    // 不然每扇最小化窗口每次拔屏都会被"救"一遍。
    [Fact] public void Rescue_judges_minimized_windows_by_their_restore_position()
    {
        var minimizedOnScreen = TestData.Win(1, RectPx.FromLTWH(-32000, -32000, 160, 30),
            state: ShowState.Minimized, normalPos: RectPx.FromLTWH(100, 100, 400, 300));
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.Rescue, [minimizedOnScreen], new PointPx(0, 0)));
        Assert.Equal(NoOpReason.NothingToDo, r.NoOp);
    }
}
