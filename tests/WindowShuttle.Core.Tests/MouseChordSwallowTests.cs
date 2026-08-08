using WindowShuttle.App;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>吞咽账本。这套规则的错法在真机上极难复现——要先让钩子超时一次、漏看一次抬起才看得见
/// ——但症状很唬人：一次毫不相干的普通点击被吞掉抬起，目标程序停在"键还按着"的状态里不出来，
/// 用户说的是"鼠标点击卡住了"。所以这里把它当纯函数钉死。</summary>
public class MouseChordSwallowTests
{
    private const int None = 0;

    [Fact] public void A_swallowed_press_makes_its_own_release_swallowed_too()
    {
        // 右键菜单是在抬起那一刻弹的，所以吞了按下就必须连抬起一起吞
        int mask = MouseChordService.AfterDown(None, MouseChordButton.Right, swallowing: true);
        var (swallow, after) = MouseChordService.TakeUp(mask, MouseChordButton.Right);
        Assert.True(swallow);
        Assert.Equal(None, after);            // 吞完销账，别赖着下一次
    }

    [Fact] public void A_release_with_no_matching_press_is_left_alone()
    {
        var (swallow, after) = MouseChordService.TakeUp(None, MouseChordButton.Right);
        Assert.False(swallow);
        Assert.Equal(None, after);
    }

    [Fact] public void Only_the_button_we_swallowed_is_affected()
    {
        int mask = MouseChordService.AfterDown(None, MouseChordButton.Right, swallowing: true);
        Assert.False(MouseChordService.TakeUp(mask, MouseChordButton.Middle).Swallow);
        Assert.False(MouseChordService.TakeUp(mask, MouseChordButton.X1).Swallow);
    }

    // ══ 这条是修的那个 bug ══════════════════════════════════════════════════════════════════
    [Fact] public void A_press_we_let_through_wipes_any_stale_debt_for_that_button()
    {
        // 场景：手势命中，按下被吞（记账）。接着钩子超时被系统跳过，那次抬起我们没看见——
        // 欠条留在账上，而且永远等不到它的抬起了。
        int stale = MouseChordService.AfterDown(None, MouseChordButton.Right, swallowing: true);

        // 稍后用户做一次**普通**右键（没按修饰键，没命中任何手势）：这次按下是放行的。
        int mask = MouseChordService.AfterDown(stale, MouseChordButton.Right, swallowing: false);

        // 那么它的抬起也必须放行。若不然：应用收到了按下、收不到抬起，就卡在拖拽/框选里出不来。
        Assert.False(MouseChordService.TakeUp(mask, MouseChordButton.Right).Swallow,
            "放行的按下必须清掉旧欠条，否则它会去吞掉这次无辜点击的抬起");
    }

    [Fact] public void Stale_debt_on_one_button_does_not_leak_into_another()
    {
        int stale = MouseChordService.AfterDown(None, MouseChordButton.Right, swallowing: true);
        int mask = MouseChordService.AfterDown(stale, MouseChordButton.Middle, swallowing: false);
        Assert.True(MouseChordService.TakeUp(mask, MouseChordButton.Right).Swallow);   // 右键的账还在
    }
}
