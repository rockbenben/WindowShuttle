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
}
