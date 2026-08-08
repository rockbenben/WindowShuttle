using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class WindowFilterTests
{
    private static readonly RectPx R = RectPx.FromLTWH(100, 100, 800, 600);

    [Fact] public void Plain_visible_titled_window_is_movable()
        => Assert.True(WindowFilter.IsMovable(TestData.Win(1, R)));

    [Fact] public void Invisible_is_not_movable()
        => Assert.False(WindowFilter.IsMovable(TestData.Win(1, R, visible: false)));

    [Fact] public void Owned_is_not_movable()
        => Assert.False(WindowFilter.IsMovable(TestData.Win(1, R, owner: true)));

    [Fact] public void ToolWindow_is_not_movable()
        => Assert.False(WindowFilter.IsMovable(TestData.Win(1, R, tool: true)));

    [Fact] public void Cloaked_is_not_movable()   // §4 条件 4：UWP 幽灵窗口
        => Assert.False(WindowFilter.IsMovable(TestData.Win(1, R, cloaked: true)));

    [Fact] public void Untitled_is_not_movable()
        => Assert.False(WindowFilter.IsMovable(TestData.Win(1, R, title: "")));

    [Theory]
    [InlineData("Progman")] [InlineData("WorkerW")] [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")] [InlineData("Windows.UI.Core.CoreWindow")]
    public void Blacklisted_class_is_not_movable(string cls)
        => Assert.False(WindowFilter.IsMovable(TestData.Win(1, R, cls: cls)));

    [Fact] public void Own_process_is_not_movable()
        => Assert.False(WindowFilter.IsMovable(TestData.Win(1, R, own: true)));

    [Fact] public void Borderless_monitor_sized_window_is_fullscreen()
    {
        var mon = new[] { TestData.Mon(1, 0, 0, 1920, 1080) };
        var w = TestData.Win(1, RectPx.FromLTWH(0, 0, 1920, 1080), hasFrame: false);
        Assert.True(WindowFilter.IsFullscreen(w, mon));
    }

    [Fact] public void Framed_monitor_sized_window_is_not_fullscreen()   // 普通最大化有边框
    {
        var mon = new[] { TestData.Mon(1, 0, 0, 1920, 1080) };
        var w = TestData.Win(1, RectPx.FromLTWH(0, 0, 1920, 1080), hasFrame: true);
        Assert.False(WindowFilter.IsFullscreen(w, mon));
    }

    [Fact] public void Borderless_smaller_window_is_not_fullscreen()
    {
        var mon = new[] { TestData.Mon(1, 0, 0, 1920, 1080) };
        var w = TestData.Win(1, RectPx.FromLTWH(0, 0, 800, 600), hasFrame: false);
        Assert.False(WindowFilter.IsFullscreen(w, mon));
    }

    [Fact] public void Borderless_monitor_sized_maximized_window_is_not_fullscreen()   // 普通最大化也能无边框，但不是全屏应用
    {
        var mon = new[] { TestData.Mon(1, 0, 0, 1920, 1080) };
        var w = TestData.Win(1, RectPx.FromLTWH(0, 0, 1920, 1080), hasFrame: false, state: ShowState.Maximized);
        Assert.False(WindowFilter.IsFullscreen(w, mon));
    }
}
