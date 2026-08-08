using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using MainWindow = WindowShuttle.App.MainWindow;
using Settings = WindowShuttle.App.Settings;

namespace WindowShuttle.Core.Tests;

/// <summary>Part 3 restrained pass (§a11y): a conflicted hotkey used to be told apart from a working
/// one only by color (red border/text on the keycap). Confirms the non-color marker (a "!" glyph)
/// actually renders for a conflicted cap and is absent for a normally-bound one — real MainWindow,
/// real visual tree, never shown (see MainWindowBoundsTests.cs for why EnsureHandle/pure construction
/// suffices here — this doesn't need real layout, just that BuildActionRows ran).
///
/// Shares MainWindowWpfCollection.Gate with MainWindowBoundsTests: see that class's doc comment —
/// MainWindow's static SolidColorBrush fields aren't Frozen, so any two of these tests running their
/// STA thread on a different System.Threading.Thread throw "a different thread owns it" unless a hard
/// lock keeps them strictly one-at-a-time.</summary>
[Collection(MainWindowWpfCollection.Name)]
public class MainWindowAccessibilityTests(WpfTestHost host)
{
    [Fact]
    public void Conflicted_hotkey_shows_a_non_color_marker_alongside_the_red_keycap()
    {
        host.Invoke(() =>
        {
            global::WindowShuttle.App.App.Cfg = new Settings();
            global::WindowShuttle.App.App.Cfg.Hotkeys["Swap"] = "Ctrl+Alt+1";
            global::WindowShuttle.App.App.Cfg.Hotkeys["Undo"] = "Ctrl+Alt+2";
            global::WindowShuttle.App.App.HotkeyStates = new()
            {
                ["Swap"] = WindowShuttle.App.HotkeyState.Conflict,
                ["Undo"] = WindowShuttle.App.HotkeyState.Registered,
            };

            var window = new MainWindow();
            var conflictPanel = (StackPanel)FindCap(window, "Swap").Child;
            var registeredPanel = (StackPanel)FindCap(window, "Undo").Child;

            // 冲突态：第一个子元素是个不掉色也认得出的字形标记，不是键帽本身。
            Assert.IsType<TextBlock>(conflictPanel.Children[0]);
            Assert.Equal("!", ((TextBlock)conflictPanel.Children[0]).Text);
            // 正常绑定：没有这个标记，第一个子元素就是键帽（Border）。
            Assert.IsType<Border>(registeredPanel.Children[0]);
        });
    }

    /// <summary>键位区不能是键盘陷阱（WCAG 2.1.2）。
    ///
    /// 录制器挂的是 <c>PreviewKeyDown</c>——隧道事件，早于 WPF 做 Tab 导航的冒泡 KeyDown。它原来
    /// 第一句就无条件 <c>e.Handled = true</c>，于是键盘焦点一旦落进任意一个键位区就再也 Tab 不出去，
    /// 唯一的出路是 Esc，而界面从没说过有这条路（录制提示只写着"按下组合键…"）。WCAG 允许非标准的
    /// 退出键，前提是告知用户，而这里没有告知。
    ///
    /// 断言口径是"Tab 没有被标记为已处理"而不是"焦点真的移走了"：真正的移动由 WPF 在冒泡阶段做，
    /// 而这条测试不显示窗口、没有真实的键盘焦点链。Handled 才是我们这段代码唯一说了算、也唯一
    /// 弄坏过的东西。
    ///
    /// 对照组取一个无修饰键的普通字母，而不是 Delete：Delete 那条分支会走到 ReapplyHotkeys()，
    /// 它把 Application.Current 强转成 App，而测试宿主里那是一个裸的 Application——测的就成了
    /// 宿主的类型，不是这段代码。字母键在 TryParse 那里因为缺修饰键返回 null 而提前退出，
    /// 一行 Application 都不碰，同样证明"该吞的还在吞"。</summary>
    [Theory]
    [InlineData(Key.Tab, false, "Tab 被吞掉了——焦点进了键位区就 Tab 不出来，这是键盘陷阱")]
    [InlineData(Key.K, true, "无修饰键的普通按键没被吞掉——录制态下杂键不该漏出去触发别处")]
    public void Tab_is_not_swallowed_by_the_hotkey_recorder(Key key, bool expectHandled, string because)
    {
        host.Invoke(() =>
        {
            global::WindowShuttle.App.App.Cfg = new Settings();
            var window = new MainWindow();
            var cap = FindCap(window, "Swap");

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new HwndSource(0, 0, 0, 0, 0, "t", 0), 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            cap.RaiseEvent(args);

            Assert.Equal(expectHandled, args.Handled);
            Assert.True(args.Handled == expectHandled, because);
        });
    }

    private static Border FindCap(MainWindow window, string actionKey)
        => window.ActionList.Items.Cast<Border>()
            .Select(row => (Grid)row.Child)
            .SelectMany(grid => grid.Children.OfType<Border>())
            .First(b => (string?)b.Tag == actionKey);
}
