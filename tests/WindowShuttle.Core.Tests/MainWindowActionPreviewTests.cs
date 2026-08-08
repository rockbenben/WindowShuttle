using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MainWindow = WindowShuttle.App.MainWindow;
using Settings = WindowShuttle.App.Settings;

namespace WindowShuttle.Core.Tests;

/// <summary>悬停一条动作，地图就只留下这条动作真正会碰到的那几块屏（其余调暗），中间标一个方向
/// 记号——这是这一版界面唯一的新行为，而它整个活在 UI 层，没有别的东西覆盖它。
///
/// 断言挑的是跟光标当前在哪块屏无关的那些不变量：真机上光标位置不可控，"悬停整屏互换必然出现记号"
/// 这种断言会随光标恰好停在主屏而随机失败。这里锁三条：撤销这类不涉及特定屏的动作永远不出记号；
/// 出了记号就必然同时有屏被调暗（两个信号要么一起在、要么一起不在）；以及最要紧的一条——鼠标移开
/// 之后每块屏都必须回到不透明，一张卡片卡在半透明是肉眼立刻看得见的坏状态。
///
/// 跟 MainWindowBoundsTests 一样真的 Show()（Opacity=0 + ShowInTaskbar=false）：地图卡片只有在窗口
/// 真正上屏、跑过一遍真实布局之后才存在，纯构造拿不到。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class MainWindowActionPreviewTests(WpfTestHost host)
{
    private static void Hover(Border row, bool enter) => row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
    {
        RoutedEvent = enter ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent,
    });

    private static Border Row(MainWindow w, int index) => (Border)w.ActionList.Items[index]!;

    /// <summary>行号问 ActionKeys 要，不写死。原来是三个手写的常量加一句"顺序是 Swap, SwapTop…"的
    /// 注释——加「送去 N 号屏」时它们插在了 ToNext 和 Gather 之间，常量当场指错行，而报错是
    /// 「Expected 1, Actual 0」这种看不出跟顺序有关的样子。</summary>
    private static int RowOf(string actionKey) => Array.IndexOf(MainWindow.ActionKeys, actionKey);
    private static readonly int SwapRow = RowOf("Swap"), GatherRow = RowOf("Gather"), UndoRow = RowOf("Undo");

    private void WithWindow(Action<MainWindow> body) => host.Invoke(() =>
    {
        global::WindowShuttle.App.App.Cfg = new Settings();
        var w = new MainWindow { Width = 960, Height = 760, Opacity = 0, ShowInTaskbar = false };
        w.Show();
        w.UpdateLayout();
        try { body(w); }
        finally { w.Close(); }
    });

    private static (int Glyphs, int Dimmed, int Total) MapState(MainWindow w)
    {
        var cards = w.LayoutCanvas.Children.OfType<Border>().Where(b => b.Tag is int).ToList();
        // 方向记号是画布上唯一一个 Tag 不是屏号的 Border（卡片的 Tag 一律是 int 屏号）。
        int glyphs = w.LayoutCanvas.Children.OfType<Border>().Count(b => b.Tag is null);
        return (glyphs, cards.Count(c => c.Opacity < 1), cards.Count);
    }

    [Fact]
    public void Hovering_an_action_that_targets_no_particular_monitor_draws_no_preview()
    {
        WithWindow(w =>
        {
            Hover(Row(w, UndoRow), enter: true);
            var (glyphs, dimmed, total) = MapState(w);
            Assert.True(total > 0, "地图上一块屏都没有，这条测试什么也没验到");
            Assert.Equal(0, glyphs);
            Assert.Equal(0, dimmed);
        });
    }

    /// <summary>「全部收拢」是唯一一条跟光标在哪无关的可预览动作（终点永远是主屏，起点是其余全部），
    /// 所以它能被确定性地断言"记号一定出现"——多屏机器上必然出现，单屏机器上必然不出现。上一版这条
    /// 跟 Swap 合在一个 Theory 里，写成"要么都出现要么都不出现"，结果在光标恰好停在主屏时两边都是 0，
    /// 断言照样通过——一条永远绿、但什么也没验到的测试。</summary>
    [Fact]
    public void Gather_marks_the_primary_as_the_destination_whenever_there_is_more_than_one_monitor()
    {
        WithWindow(w =>
        {
            Hover(Row(w, GatherRow), enter: true);
            var (glyphs, dimmed, total) = MapState(w);
            Assert.True(total > 0, "地图上一块屏都没有，这条测试什么也没验到");
            Assert.Equal(total >= 2 ? 1 : 0, glyphs);
            // 收拢是所有屏都参与，没有该被排除的——一块都不该调暗。
            Assert.Equal(0, dimmed);
        });
    }

    /// <summary>「整屏互换」的两端是光标所在屏和主屏，光标在真机上不可控（它就在使用者手里），所以
    /// 这里只能锁跟光标无关的那部分：记号和调暗必须同进同退，且不能把所有屏都调暗。光标恰好停在
    /// 主屏时这条动作无图可画，两个信号都不出现，也是合法结果。</summary>
    [Fact]
    public void Swap_never_leaves_a_marker_without_dimming_or_dims_every_monitor()
    {
        WithWindow(w =>
        {
            Hover(Row(w, SwapRow), enter: true);
            var (glyphs, dimmed, total) = MapState(w);
            Assert.True(total > 0, "地图上一块屏都没有，这条测试什么也没验到");
            Assert.True(glyphs == 0 ? dimmed == 0 : dimmed < total,
                $"记号 {glyphs} 个、调暗 {dimmed}/{total} 块：两个信号必须同进同退，且不能把所有屏都调暗");
        });
    }

    [Fact]
    public void Leaving_the_row_restores_every_monitor_to_full_opacity()
    {
        WithWindow(w =>
        {
            foreach (int row in new[] { SwapRow, GatherRow, UndoRow })
            {
                Hover(Row(w, row), enter: true);
                Hover(Row(w, row), enter: false);
                var (glyphs, dimmed, _) = MapState(w);
                Assert.Equal(0, glyphs);
                Assert.Equal(0, dimmed);
            }
        });
    }
}
