using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MainWindow = WindowShuttle.App.MainWindow;
using Settings = WindowShuttle.App.Settings;
using Strings = WindowShuttle.App.I18n.Strings;

namespace WindowShuttle.Core.Tests;

/// <summary>标题栏这一条带子上的东西不许互相压住。
///
/// 这条是被一个真缺陷逼出来的：标题栏那格 <c>Grid</c> 当时**没有列定义**，于是 GitHub 按钮和
/// "检查更新"那一栈落进同一个格子——格子宽度取两者较大值（后者），GitHub 按钮按左对齐贴在格子
/// 最左边，正好盖住 UpdateChk 的勾框方块。真机上看到的是：方块整个不见了，只剩一枚 GitHub 图标
/// 顶着"检查更新"四个字，**那个开关点不到**。
///
/// 为什么值得单钉一条：388 条测试当时全绿，程序一眼就能看出坏了。纯构造（不显示）测不出来——
/// 不做真实布局，所有元素的矩形都是 0×0，"相交"恒为假，断言成了空话。所以这里要真显示一扇窗
/// （Opacity=0、ShowInTaskbar=false，用户看不见），量它真实排版后的矩形。
///
/// 断言语义是"两个控件的矩形不相交"，不是"间距大于 N"：盖住和贴住是两种毛病，只钉住前者，
/// 免得布局微调就得改测试。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class MainWindowTitleBarTests(WpfTestHost host)
{
    /// <summary>GitHub 按钮与"检查更新"勾框不得重叠——盖住了那个开关就点不到。</summary>
    [Fact]
    public void The_repository_button_does_not_cover_the_update_checkbox()
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
                var chk = FindUpdateCheckBox(window);
                Assert.NotNull(repo);
                Assert.NotNull(chk);
                Assert.True(repo!.ActualWidth > 0 && repo.ActualHeight > 0, "GitHub 按钮没有排版出尺寸");
                Assert.True(chk!.ActualWidth > 0 && chk.ActualHeight > 0, "检查更新勾框没有排版出尺寸");

                var a = RectIn(repo, window);
                var b = RectIn(chk, window);
                Assert.False(a.IntersectsWith(b),
                    $"GitHub 按钮与检查更新勾框重叠了：按钮 {a} 压住勾框 {b}——那个开关点不到");

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

    /// <summary>检查更新勾框：标题栏那一个，认它的内容文案——设置条里另外五个复选框都不是这句。</summary>
    private static CheckBox? FindUpdateCheckBox(DependencyObject root)
    {
        var wanted = Strings.Get("Settings_CheckUpdates");
        return Descendants<CheckBox>(root).FirstOrDefault(c => c.Content as string == wanted);
    }

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
