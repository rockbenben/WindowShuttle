using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>方向手势「那个方向上是哪块屏」的判据。</summary>
public class MonitorTowardTests
{
    // 本机的真实摆法：三屏并排、顶部对齐、高度各不相同。
    // 中心点 y 分别是 720 / 1080 / 540 —— 正是它让"按中心点判方向"翻了车。
    private static readonly MonitorInfo Left = TestData.Mon(1, -2560, 0, 2560, 1440);
    private static readonly MonitorInfo Mid = TestData.Mon(2, 0, 0, 3840, 2160, primary: true);
    private static readonly MonitorInfo Right = TestData.Mon(3, 3840, 0, 1920, 1080);
    private static readonly MonitorInfo[] Row = [Left, Mid, Right];

    [Fact] public void Flicking_right_finds_the_screen_on_the_right()
        => Assert.Equal(Right.Index, SwapPlanner.MonitorToward(Mid, 500, 0, Row)!.Index);

    [Fact] public void Flicking_left_finds_the_screen_on_the_left()
        => Assert.Equal(Left.Index, SwapPlanner.MonitorToward(Mid, -500, 0, Row)!.Index);

    /// <summary>45° 线上的平手归左右：与水平的夹角**不超过** 45° 都算左右（用户定的规则，也是
    /// 横排桌面上的常识——边界上的犹豫该判给压倒性常用的那个方向）。写成 |dx| &gt; |dy| 的话，
    /// 正对角线会静默归竖直，在并排桌面上表现为"划了没反应"。</summary>
    [Fact] public void The_exact_diagonal_counts_as_horizontal()
    {
        Assert.Equal(Right.Index, SwapPlanner.MonitorToward(Mid, 300, 300, Row)!.Index);
        Assert.Equal(Left.Index, SwapPlanner.MonitorToward(Mid, -300, -300, Row)!.Index);
    }

    /// <summary>并排的桌面上，上下就是没有屏——哪怕邻居的**中心点**更高或更低。
    ///
    /// 这条是实测撞出来的缺陷：三块屏顶部对齐但高度不同（1440/2160/1080），左边那块的中心
    /// y=720 比中间那块的 y=1080 高，于是"往上划"按中心点判会选中**左边**那块，窗口横着飞走，
    /// 而用户比划的是向上。判据必须是"整块屏都在那一侧"。</summary>
    [Fact] public void A_row_of_screens_has_nothing_above_or_below()
    {
        Assert.Null(SwapPlanner.MonitorToward(Mid, 0, -500, Row));
        Assert.Null(SwapPlanner.MonitorToward(Mid, 0, 500, Row));
        Assert.Null(SwapPlanner.MonitorToward(Left, 0, -500, Row));
        Assert.Null(SwapPlanner.MonitorToward(Right, 0, 500, Row));
    }

    [Fact] public void The_edges_of_the_row_have_nothing_further_out()
    {
        Assert.Null(SwapPlanner.MonitorToward(Left, -500, 0, Row));
        Assert.Null(SwapPlanner.MonitorToward(Right, 500, 0, Row));
    }

    /// <summary>方向手势开着 wrap（跟「送去下一块屏」到头绕回最左是同一个约定）：
    /// 到头再划就绕到同一轴的**另一头**——不是相邻的下一块，是最远那块。</summary>
    [Fact] public void With_wrap_the_edges_cycle_to_the_far_end_of_the_axis()
    {
        Assert.Equal(Left.Index, SwapPlanner.MonitorToward(Right, 500, 0, Row, wrap: true)!.Index);
        Assert.Equal(Right.Index, SwapPlanner.MonitorToward(Left, -500, 0, Row, wrap: true)!.Index);
        // 中间那块屏右边有屏：wrap 不改变正常命中，仍去右边相邻那块。
        Assert.Equal(Right.Index, SwapPlanner.MonitorToward(Mid, 500, 0, Row, wrap: true)!.Index);
    }

    /// <summary>wrap 只在同一轴上有别的屏时才绕：并排桌面上往上划，竖轴上根本没有第二块屏，
    /// 照样报「那边没有屏幕」——绕到横轴上属于替用户改主意。</summary>
    [Fact] public void Wrap_never_crosses_to_the_other_axis()
    {
        Assert.Null(SwapPlanner.MonitorToward(Mid, 0, -500, Row, wrap: true));
        Assert.Null(SwapPlanner.MonitorToward(Mid, 0, 500, Row, wrap: true));
    }

    /// <summary>真的上下堆叠时当然要认出来——上面那条不能是"永远返回 null"。</summary>
    [Fact] public void Stacked_screens_are_found_vertically()
    {
        var top = TestData.Mon(1, 0, -1080, 1920, 1080);
        var bottom = TestData.Mon(2, 0, 0, 1920, 1080, primary: true);
        MonitorInfo[] stack = [top, bottom];
        Assert.Equal(top.Index, SwapPlanner.MonitorToward(bottom, 0, -500, stack)!.Index);
        Assert.Equal(bottom.Index, SwapPlanner.MonitorToward(top, 0, 500, stack)!.Index);
        // 堆叠的桌面上，左右同样什么都没有。
        Assert.Null(SwapPlanner.MonitorToward(bottom, 500, 0, stack));
        Assert.Null(SwapPlanner.MonitorToward(bottom, -500, 0, stack));
    }

    /// <summary>L 形：右边那块又靠下。往右划要找到它（水平方向确实整块在右边），
    /// 而往下划**也**要找到它——它整块都在下方。两条都成立不矛盾，方向手势本来就按主轴取。</summary>
    [Fact] public void An_L_shaped_desktop_answers_on_both_axes()
    {
        var main = TestData.Mon(1, 0, 0, 1920, 1080, primary: true);
        var lower = TestData.Mon(2, 1920, 1080, 1920, 1080);
        MonitorInfo[] l = [main, lower];
        Assert.Equal(lower.Index, SwapPlanner.MonitorToward(main, 500, 0, l)!.Index);
        Assert.Equal(lower.Index, SwapPlanner.MonitorToward(main, 0, 500, l)!.Index);
    }
}
