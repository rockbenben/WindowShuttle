using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ConfirmDialog = WindowShuttle.App.ConfirmDialog;

namespace WindowShuttle.Core.Tests;

/// <summary>ConfirmDialog 用 <c>SizeToContent="WidthAndHeight"</c>，高度完全由文案决定，所以它必须
/// 按自己所在那块屏的工作区给自己封顶——否则文案一长，最底下那行「重启／取消」就掉到任务栏底下，
/// 而对话框恰恰是 CenterOwner，主窗口在副屏时连"按主屏算"都是错的。
///
/// 为什么必须是一条测试、而不是靠 --shots 截图看：<c>--shots</c> 自己会在 Show 之后设
/// <c>MaxHeight</c> 来模拟不同的工作区高度，正好把窗口自己那个运行时上限盖掉。于是"窗口根本没给
/// 自己封顶"这一类缺陷在每一张截图里都完好无损，却会在真实的小屏笔记本上把确定按钮顶出屏幕。
/// 这是那套截图工具已知的盲区，只能由别的东西来守。
///
/// 断言挑的是跟机器无关的那部分：不去猜具体像素，只要求上限确实被收到了本机某块屏的工作区之内，
/// 且没有低于窗口自己声明的 MinHeight（那样会把对话框压到连按钮都放不下）。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class ConfirmDialogHeightCapTests(WpfTestHost host)
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint h, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [Fact]
    public void The_dialog_caps_its_own_height_to_the_monitor_it_lands_on()
        => host.Invoke(() =>
        {
            // 一段远超任何真实文案的长文本：没有上限的话 SizeToContent 会让窗口一路长下去。
            var dlg = new ConfirmDialog(string.Join(" ", Enumerable.Repeat("这是一段很长的确认文案。", 200)))
            {
                Opacity = 0, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            try
            {
                dlg.Show();
                dlg.UpdateLayout();

                var hwnd = new WindowInteropHelper(dlg).Handle;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                Assert.True(GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi),
                    "取不到这扇窗所在屏的信息，这条测试无法判断");
                double workAreaDip = (mi.rcWork.Bottom - mi.rcWork.Top) / VisualTreeHelper.GetDpi(dlg).DpiScaleY;

                // 这条测试只有在工作区明显高于 XAML 里那个保守兜底值时才分得清两种情况；屏幕太小的
                // 机器上"跟着屏幕算"和"用兜底值"会撞在一起，那时它没有判断力，应当说出来而不是蒙混过关。
                Assert.True(workAreaDip > 700,
                    $"这台机器的工作区只有 {workAreaDip:0} DIP，本条测试在这种屏上分不出真假，先当作无法判断");

                // 关键的一条：上限必须是**从这块屏的工作区算出来的**，不能是一个写死的数。
                // 第一版只断言了"上限 ≤ 工作区 且 ≥ MinHeight"，结果把运行时封顶整个删掉之后测试
                // 照样全绿——因为 XAML 里那个保守兜底 MaxHeight 同时满足这两条。一条不会因为它要防的
                // 缺陷而变红的断言，等于没有。
                Assert.InRange(dlg.MaxHeight, workAreaDip - 120, workAreaDip);
                Assert.True(dlg.MaxHeight >= dlg.MinHeight,
                    $"高度上限 {dlg.MaxHeight:0} 低于窗口自己的下限 {dlg.MinHeight:0} —— 会把按钮压没");
                // 真实高度必须落在上限之内：这才说明那个上限真的起了作用，而不只是被设了个值。
                Assert.True(dlg.ActualHeight <= dlg.MaxHeight + 0.5,
                    $"实际高度 {dlg.ActualHeight:0} 超出了上限 {dlg.MaxHeight:0}");
            }
            finally { dlg.Close(); }
        });
}
