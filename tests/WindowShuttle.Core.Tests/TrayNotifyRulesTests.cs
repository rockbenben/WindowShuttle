using WindowShuttle.App;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.Core.Tests;

/// <summary>TrayService.DecideNotification 是通知规则本身：四种该响的情形（提权受阻、非权限失败、
/// no-op 带原因、错误）该响，其余（尤其是"成功搬运，哪怕带跳过/纠偏计数"）该静默。纯函数，不用真的
/// 拉一扇 NotificationOverlay 出来就能测。
///
/// 归 UiCultureCollection：断言里的文案两边都走 Strings，本该恒等，但只要有别的 collection 在
/// 并行地切进程级 UI 语言，就会在"取期望值"和"取实际值"这两步之间被换掉——见那个 collection 的注释。</summary>
[Collection(UiCultureCollection.Name)]
public class TrayNotifyRulesTests
{
    private static ExecResult NoOp(NoOpReason reason)
        => new(1, MovePlan.NoOpPlan(reason), null);

    private static ExecResult Success(int moved = 1, int accessDenied = 0, int otherFailed = 0,
        int corrected = 0, int skippedFullscreen = 0, int skippedHung = 0)
        => new(accessDenied + otherFailed > 0 ? 2 : 0,
            new MovePlan([], skippedFullscreen, skippedHung, null, null),
            new CommitResult(moved, accessDenied, otherFailed, corrected));

    [Fact] public void AccessDenied_is_actionable_and_not_an_error()
    {
        var n = TrayService.DecideNotification(Success(moved: 1, accessDenied: 2), WindowShuttleAction.ToPrimary);
        Assert.NotNull(n);
        Assert.True(n!.Value.Actionable);
        Assert.False(n.Value.IsError);
        Assert.Equal(Strings.Lf("Toast_AccessDenied", 2), n.Value.Text);
    }

    /// <summary>提权失败**不分入口**都要出声，拔屏自动救援也不例外。
    ///
    /// 这条是钉产品决定用的：代码审查提过"无人触发的救援不该弹提权邀约"，被明确驳回了——窗口卡在
    /// 屏外而应用一声不吭，比多一张可以忽略的卡片糟得多；而且点它还要再过一道确认框才会真的重启。
    /// 谁要是照着那条建议把 Rescue 的这一支静默掉，这里会红。</summary>
    [Fact] public void AccessDenied_still_notifies_even_for_unattended_rescue()
    {
        var n = TrayService.DecideNotification(Success(moved: 0, accessDenied: 3), WindowShuttleAction.Rescue);
        Assert.NotNull(n);
        Assert.True(n!.Value.Actionable);
        Assert.Equal(Strings.Lf("Toast_AccessDenied", 3), n.Value.Text);
    }

    [Theory]
    [InlineData(NoOpReason.OnlyOneMonitor)]
    [InlineData(NoOpReason.CursorNotOnWindow)]
    [InlineData(NoOpReason.AlreadyOnTarget)]
    public void NoOp_reason_notifies_with_its_own_text_and_is_not_actionable(NoOpReason reason)
    {
        var n = TrayService.DecideNotification(NoOp(reason), WindowShuttleAction.Swap);
        Assert.NotNull(n);
        Assert.False(n!.Value.Actionable);
        Assert.False(n.Value.IsError);
        Assert.Equal(Strings.Get($"NoOp_{reason}"), n.Value.Text);
    }

    [Fact] public void NoOp_Error_notifies_and_is_flagged_as_an_error()
    {
        var n = TrayService.DecideNotification(NoOp(NoOpReason.Error), WindowShuttleAction.Swap);
        Assert.NotNull(n);
        Assert.True(n!.Value.IsError);
        Assert.Equal(Strings.Get("NoOp_Error"), n.Value.Text);
    }

    // Undo 撞上 NothingToDo 有自己专用的文案（"没有可撤销的搬运"），不是通用的 NoOp_NothingToDo——
    // action 会改变同一个 reason 该显示哪句话。
    [Fact] public void Undo_NothingToDo_uses_the_dedicated_no_undo_text()
    {
        var n = TrayService.DecideNotification(NoOp(NoOpReason.NothingToDo), WindowShuttleAction.Undo);
        Assert.Equal(Strings.Get("Toast_NoUndo"), n!.Value.Text);
    }

    [Fact] public void Swap_NothingToDo_uses_the_generic_no_op_text()
    {
        var n = TrayService.DecideNotification(NoOp(NoOpReason.NothingToDo), WindowShuttleAction.Swap);
        Assert.Equal(Strings.Get("NoOp_NothingToDo"), n!.Value.Text);
    }

    [Fact] public void Plain_successful_move_stays_silent()
        => Assert.Null(TrayService.DecideNotification(Success(moved: 3), WindowShuttleAction.Swap));

    // 核心断言：跳过全屏/无响应/DPI 纠偏这三个计数——哪怕全部同时非零——单独都不足以弹出通知，
    // 它们只进 CLI 输出给脚本看（§notify-rules）。
    [Fact] public void Skip_and_correction_counts_alone_stay_silent()
    {
        var r = Success(moved: 2, skippedFullscreen: 3, skippedHung: 1, corrected: 4);
        Assert.Null(TrayService.DecideNotification(r, WindowShuttleAction.Gather));
    }

    // §own-decision 用户已批准：非权限原因的失败也要提示，不能静默——跟 AccessDenied 不同，
    // 这里没有"点它就能修"的出路，所以不可点击；但仍然是失败，标成错误态。
    [Fact] public void OtherFailed_alone_notifies_and_is_flagged_as_an_error()
    {
        var n = TrayService.DecideNotification(Success(moved: 1, otherFailed: 2), WindowShuttleAction.Swap);
        Assert.NotNull(n);
        Assert.False(n!.Value.Actionable);
        Assert.True(n.Value.IsError);
        Assert.Equal(Strings.Plural("Toast_OtherFailed", 2), n.Value.Text);
    }

    // 气泡一次只能显示一条（NotificationOverlay 不排队）：两种失败同时发生时，AccessDenied 优先——
    // 它是唯一带得出路（点它提权重启）的那条。
    [Fact] public void AccessDenied_takes_priority_over_OtherFailed_when_both_are_nonzero()
    {
        var n = TrayService.DecideNotification(Success(moved: 1, accessDenied: 1, otherFailed: 2), WindowShuttleAction.Swap);
        Assert.NotNull(n);
        Assert.True(n!.Value.Actionable);
        Assert.False(n.Value.IsError);
        Assert.Equal(Strings.Plural("Toast_AccessDenied", 1), n.Value.Text);
    }

    // 保存布局是唯一"窗口没动=成功"的动作：静默会让人分不清按没按到，成功必须出声。

    // 恢复布局撞上 NothingToDo 有专用文案（"这套显示器组合没存过"），与 Undo 的专用文案同一条规则。

    // 自动救援反转常规两条规则：no-op 静默（没人按键，"没有窗口卡在屏外"不需要交代），
    // 成功却必须出声（无人触发的移动要解释自己）。
    [Fact] public void Rescue_noop_stays_silent()
        => Assert.Null(TrayService.DecideNotification(NoOp(NoOpReason.NothingToDo), WindowShuttleAction.Rescue));

    [Fact] public void Rescue_success_notifies_with_the_rescued_count()
    {
        var n = TrayService.DecideNotification(Success(moved: 3), WindowShuttleAction.Rescue);
        Assert.NotNull(n);
        Assert.False(n!.Value.Actionable);
        Assert.False(n.Value.IsError);
        Assert.Equal(Strings.Plural("Toast_Rescued", 3), n.Value.Text);
    }
}
