using System.Windows;
using WindowShuttle.Core;
using NotificationOverlay = WindowShuttle.App.NotificationOverlay;

namespace WindowShuttle.Core.Tests;

/// <summary>round4 Part 2 (§a11y): the actionable notification card used to be told apart from an
/// informational one only by border color (amber vs gray) — the same gap MainWindow's hotkey conflict
/// state had before it got a non-color "!" marker (see MainWindowAccessibilityTests.cs). Confirms the
/// chevron glyph actually renders for the actionable card and stays hidden for a plain one, using
/// ShowOn (the same test/harness-only per-monitor seam tools/UiaHarness uses) so this never has to
/// touch the real cursor position.</summary>
[Collection(MainWindowWpfCollection.Name)]
public class NotificationOverlayTests(WpfTestHost host)
{
    private static MonitorInfo Mon() => TestData.Mon(1, 0, 0, 1920, 1080, primary: true);

    [Fact]
    public void Actionable_card_shows_the_non_color_chevron()
    {
        host.Invoke(() =>
        {
            var overlay = NotificationOverlay.ShowOn(Mon(), "click me", actionable: true, isError: false);
            Assert.Equal(Visibility.Visible, overlay.Chevron.Visibility);
            overlay.Close();
        });
    }

    [Fact]
    public void Informational_card_hides_the_chevron()
    {
        host.Invoke(() =>
        {
            var overlay = NotificationOverlay.ShowOn(Mon(), "fyi", actionable: false, isError: false);
            Assert.Equal(Visibility.Collapsed, overlay.Chevron.Visibility);
            overlay.Close();
        });
    }

    /// <summary>batch2 U1：指针停在可操作卡上时倒计时必须让位给手。断言读的是承载倒计时的
    /// DispatcherTimer 的真实启用态（跑着 → 悬停停表 → 离开续跑），不量墙钟——量时间的测试
    /// 在这台机器上输不起 QuickEdit 冻结和调度抖动。前置断言（进悬停前表确实在跑）防的是
    /// "永远停着"这种假绿：没跑起来的表停它当然会"成功"。</summary>
    [Fact]
    public void Hovering_an_actionable_card_pauses_the_fade_clock_and_leaving_resumes_it()
    {
        host.Invoke(() =>
        {
            var overlay = NotificationOverlay.ShowOn(Mon(), "click me", actionable: true, isError: false);
            Assert.True(overlay.HoldRunning, "淡入 Loaded 后倒计时没在跑——后面全是空转");

            Enter(overlay, true);
            Assert.False(overlay.HoldRunning, "指针已停在卡上，倒计时还在走——淡出末尾会在指针底下消失");

            Enter(overlay, false);
            Assert.True(overlay.HoldRunning, "指针离开后倒计时没有续跑——这张卡永远不会自己走");
            overlay.Close();
        });
    }

    private static void Enter(NotificationOverlay overlay, bool enter) =>
        overlay.Card.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = enter
                ? System.Windows.UIElement.MouseEnterEvent
                : System.Windows.UIElement.MouseLeaveEvent,
        });
}
