using WindowShuttle.App;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>「搬哪一扇窗」的判据：鼠标手势认光标，快捷键/托盘/命令行认焦点窗口。
///
/// 这条分界线以前不存在——所有入口都认光标，于是手在键盘上时，搬走的是指针上次被放下的地方
/// 恰好压着的那扇窗。完整理由写在 <see cref="PlanRequest.Referent"/> 上。</summary>
public class ReferentTests
{
    private static readonly MonitorInfo M1 = TestData.Mon(1, 0, 0, 1920, 1080, primary: true);
    private static readonly MonitorInfo M2 = TestData.Mon(2, 1920, 0, 1920, 1080);
    private static readonly MonitorInfo[] Both = [M1, M2];

    private static PlanRequest Req(WindowShuttleAction a, IReadOnlyList<WindowFacts> wins,
        PointPx cursor, nint? referent = null)
        => new(Both, wins, a, cursor, null, null, true, referent);

    // 屏 2 上的两扇窗：cursorWin 在光标底下，focusWin 不在。
    private static readonly WindowFacts CursorWin = TestData.Win(1, RectPx.FromLTWH(2000, 100, 400, 300), z: 0);
    private static readonly WindowFacts FocusWin = TestData.Win(2, RectPx.FromLTWH(2600, 500, 400, 300), z: 1);
    private static readonly PointPx OnCursorWin = new(2100, 200);

    /// <summary>给了 referent 就搬它，哪怕光标正压在另一扇窗上。这正是键盘路径要的：
    /// 手在键盘上时，指针停在哪儿是上一次放下鼠标的残留，跟"我要搬哪扇窗"没有关系。</summary>
    [Fact] public void A_referent_wins_over_whatever_the_cursor_is_on()
    {
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary,
            [CursorWin, FocusWin], OnCursorWin, referent: FocusWin.Hwnd));
        Assert.Single(r.Moves);
        Assert.Equal(FocusWin.Hwnd, r.Moves[0].Hwnd);
    }

    /// <summary>没有 referent 就还是认光标——鼠标手势那条路一个字都没变。</summary>
    [Fact] public void Without_a_referent_the_cursor_still_decides()
    {
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary,
            [CursorWin, FocusWin], OnCursorWin));
        Assert.Single(r.Moves);
        Assert.Equal(CursorWin.Hwnd, r.Moves[0].Hwnd);
    }

    /// <summary>referent 搬不动就报 FocusNotMovable，**绝不回退去搬光标下那扇**。
    ///
    /// 回退会重新引入"到底搬了哪一扇"的歧义——这个程序在 PointedAt 那里已经明确拒绝过一次
    /// （挡在前面的窗口不可搬时，宁可什么都不做，也不伸手去够它背后那一扇）。这里是同一条原则：
    /// 用户按快捷键时看着的是他自己那扇窗，替他挑一扇别的搬走比不动更糟。</summary>
    [Fact] public void An_unmovable_referent_never_falls_back_to_the_cursor()
    {
        var ours = TestData.Win(9, RectPx.FromLTWH(2600, 500, 400, 300), own: true, z: 1);
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.ToPrimary,
            [CursorWin, ours], OnCursorWin, referent: ours.Hwnd));
        Assert.Empty(r.Moves);
        Assert.Equal(NoOpReason.FocusNotMovable, r.NoOp);
    }

    /// <summary>两条路径的"搬不成"必须是两条不同的话。合并成一条的代价是至少有一条入口在说假话：
    /// 键盘用户看到「鼠标下没有窗口」时，鼠标在哪儿跟他刚做的事毫无关系。</summary>
    [Fact] public void The_two_paths_report_different_reasons()
    {
        var empty = new PointPx(1000, 900);          // 主屏上没有窗口的一点
        var byCursor = SwapPlanner.Plan(Req(WindowShuttleAction.ToNext, [CursorWin], empty));
        var byFocus = SwapPlanner.Plan(Req(WindowShuttleAction.ToNext, [CursorWin], empty, referent: 12345));
        Assert.Equal(NoOpReason.CursorNotOnWindow, byCursor.NoOp);
        Assert.Equal(NoOpReason.FocusNotMovable, byFocus.NoOp);
    }

    /// <summary>整屏互换认的是**一块屏**，但"哪块屏"同样不该由停在别处的指针决定。
    /// 光标在屏 2、焦点窗口在屏 1（主屏）时，源屏应当是屏 1——于是它跟"上次换过的那块"对调，
    /// 而不是把屏 2 和主屏对调。</summary>
    [Fact] public void Whole_screen_actions_take_the_source_monitor_from_the_referent()
    {
        var onPrimary = TestData.Win(3, RectPx.FromLTWH(100, 100, 400, 300), z: 0);
        var onSecond = TestData.Win(4, RectPx.FromLTWH(2000, 100, 400, 300), z: 1);
        // 光标在屏 2，焦点窗口在主屏：源屏取焦点那扇窗的屏 = 主屏，于是 src == dst，
        // 走"跟上次换过的那块换回来"这条回退，屏 2 仍是对手，两扇窗都动。
        var r = SwapPlanner.Plan(Req(WindowShuttleAction.Swap,
            [onPrimary, onSecond], new PointPx(2100, 200), referent: onPrimary.Hwnd));
        Assert.Equal(2, r.Moves.Count);
    }

    /// <summary>点名了目标屏却已经站在上面 = 无事可做，不能替他换一个对象。
    ///
    /// 原来这里不分来路一律退到"跟上次换过的那块换回来"，于是 `swap --to 1` 在主屏上执行时
    /// 换的是别的屏——用户点了名，程序搬了另一个，还报成功。没点名（默认换主屏）时那条回退才成立。</summary>
    [Fact] public void Swap_to_a_named_screen_you_are_already_on_is_a_noop()
    {
        var onPrimary = TestData.Win(3, RectPx.FromLTWH(100, 100, 400, 300), z: 0);
        var onSecond = TestData.Win(4, RectPx.FromLTWH(2000, 100, 400, 300), z: 1);
        var named = new PlanRequest(Both, [onPrimary, onSecond], WindowShuttleAction.Swap,
            new PointPx(500, 500), TargetPosition: 1, LastSwapPartner: 2, SkipFullscreen: true);
        Assert.Equal(NoOpReason.AlreadyOnTarget, SwapPlanner.Plan(named).NoOp);

        // 没点名时同样的局面仍要走回退——那是同键两按的撤销手感，不能一起改掉。
        var unnamed = named with { TargetPosition = null };
        Assert.Null(SwapPlanner.Plan(unnamed).NoOp);
    }
}

/// <summary>可绑快捷键的动作，必须真的有人处理。
///
/// 这条是补票来的：「按方向送屏」曾经在快捷键表里可录，而 <see cref="SwapPlanner.Plan"/> 的 switch
/// 根本没有它的分支（手势路径在钩子层就换算成了 ToNext + 目标屏，压根到不了 Plan）。于是给它录一个
/// 快捷键、按下去，Plan 抛异常、被兜底 catch 接成退出码 3，用户看到一张红色错误卡——一个明明列在
/// 界面上的动作，绑了就报错。</summary>
public class BindableActionsAreDispatchableTests
{
    private static readonly MonitorInfo[] Two =
        [TestData.Mon(1, 0, 0, 1920, 1080, primary: true), TestData.Mon(2, 1920, 0, 1920, 1080)];

    [Fact] public void Every_hotkey_bindable_action_is_actually_dispatchable()
    {
        foreach (var name in HotkeyService.Actions.Where(SettingsStore.AllowsHotkey))
        {
            var a = Enum.Parse<WindowShuttleAction>(name);
            // Undo 不走 planner（CommandRouter.RunUndo 直接接管），其余都必须 Plan 得动。
            if (a == WindowShuttleAction.Undo) continue;
            var req = new PlanRequest(Two, [], a, new PointPx(10, 10), null, null, true);
            var ex = Record.Exception(() => SwapPlanner.Plan(req));
            Assert.True(ex is null, $"{name} 绑得上快捷键，SwapPlanner.Plan 却处理不了它：{ex}");
        }
    }

    /// <summary>反过来也要钉住：唯一不可绑快捷键的就是「按方向送屏」，而它不可绑的理由是
    /// 一个热键携带不了方向——不是随手加进黑名单的。哪天有人给它开了口子，上面那条会红。</summary>
    [Fact] public void Only_the_direction_gesture_refuses_a_hotkey()
    {
        var refused = HotkeyService.Actions.Where(k => !SettingsStore.AllowsHotkey(k)).ToArray();
        Assert.Equal([nameof(WindowShuttleAction.ToDirection)], refused);
    }
}
