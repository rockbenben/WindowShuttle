using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MainWindow = WindowShuttle.App.MainWindow;
using Settings = WindowShuttle.App.Settings;

namespace WindowShuttle.Core.Tests;

[Collection(MainWindowWpfCollection.Name)]
public class MainWindowTitleBarTests(WpfTestHost host)
{
    [Fact]
    public void The_repository_button_does_not_cover_the_updates_menu()
    {
        var laidOut = false;
        host.Invoke(() =>
        {
            global::WindowShuttle.App.App.Cfg = new Settings();
            var window = new MainWindow { Opacity = 0, ShowInTaskbar = false };
            try
            {
                // 真实排版：窗口得先显示出来，WPF 的合成器才会给这扇窗排版。
                if (!window.ShowAtExactly(900, 700)) return;

                var repo = FindRepoButton(window);
                var menu = window.UpdatesMenu;
                Assert.Contains(window.CheckUpdateBtn, menu.ContextMenu.Items.Cast<object>());
                Assert.Contains(window.UpdateChk, menu.ContextMenu.Items.Cast<object>());
                Assert.True(window.UpdateChk.IsCheckable);
                Assert.NotNull(repo);
                Assert.True(repo!.ActualWidth > 0 && repo.ActualHeight > 0, "GitHub 按钮没有排版出尺寸");
                Assert.True(menu.ActualWidth > 0 && menu.ActualHeight > 0, "更新菜单没有排版出尺寸");

                var a = RectIn(repo, window);
                var b = RectIn(menu, window);
                Assert.False(a.IntersectsWith(b),
                    $"GitHub 按钮与更新菜单重叠了：按钮 {a} 压住勾框 {b}——菜单无法打开");

                menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.True(menu.ContextMenu.IsOpen);
                Assert.Same(menu, menu.ContextMenu.PlacementTarget);
                Assert.True(window.CheckUpdateBtn.ActualHeight > 0);
                Assert.True(window.UpdateChk.ActualHeight > 0);
                menu.ContextMenu.IsOpen = false;
                laidOut = true;
            }
            finally { window.Close(); }
        });

        // 这台机器的屏幕摆不下 900×700（GitHub runner 的虚拟显示器只有约 1044 DIP 宽）时跳过，
        // 不假装验过。跳过必须写在 host.Invoke 外面：里面抛的异常会被 Dispatcher 包成
        // AggregateException，skip 就被当成失败了（见 WpfTestHost 的说明）。
        if (!laidOut) Assert.Skip("这台机器的屏幕摆不下 900×700 这一档，跳过");
    }

    /// <summary>GitHub 按钮：AutomationProperties.Name 是 "GitHub"（XAML 里设的），全窗口只有这一个。</summary>
    private static Button? FindRepoButton(DependencyObject root)
        => Descendants<Button>(root).FirstOrDefault(b => AutomationProperties.GetName(b) == "GitHub");

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>元素在窗口坐标系里的矩形——窗口坐标才是"谁压住谁"的判据，元素自己的局部坐标不是。</summary>
    private static Rect RectIn(FrameworkElement element, Window window)
        => element.TransformToAncestor(window)
                   .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
}
