using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.App;

// 校验这扇窗的物理坐标（GetWindowRect 等）时，量的那一头（脚本/工具进程）本身也必须是 DPI-aware 的，
// 不然 Windows 会按系统 DPI 把返回的坐标悄悄除一遍，看起来像是这扇窗自己歪了——其实是量的人瞎。
// 排查过：一个 DPI-unaware 的 PowerShell measured 出跟本窗口物理矩形完全对不上号的坐标，改用
// SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) 之后数字才对上。
public partial class OverlayWindow : Window
{
    private const int WM_DPICHANGED = 0x02E0;
    // 边界设置和物理像素落位跟通知卡共用，见 OverlayChrome。这里只留本窗口独有的两件：
    // 兜 WM_DPICHANGED 的消息号，和量自己真实物理矩形用的 GetWindowRect。
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT r);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // 全部屏幕矩形的并集，物理像素；原点不一定是 (0,0)——副屏可能在主屏左边/上边，坐标为负。
    private readonly RectPx _virtual;
    private readonly List<(MonitorInfo Monitor, Border Badge)> _badges = [];
    private HwndSource? _source;

    public OverlayWindow(List<MonitorInfo> monitors)
    {
        InitializeComponent();
        // 排版方向逐窗口设：不能用 OverrideMetadata 一次性覆盖 typeof(Window)，
        // 那会撞坏 WPF 自己的 Window 静态构造函数（见 App.OnStartup 的注释）。
        FlowDirection = Strings.Flow;
        _virtual = new RectPx(
            monitors.Min(m => m.MonitorRect.Left), monitors.Min(m => m.MonitorRect.Top),
            monitors.Max(m => m.MonitorRect.Right), monitors.Max(m => m.MonitorRect.Bottom));

        foreach (var m in monitors)
        {
            var number = new TextBlock
            {
                Text = SwapPlanner.PositionOf(m, monitors).ToString(),
                FontSize = 160, FontWeight = FontWeights.SemiBold,
                // 屏号用 Bahnschrift，跟主窗口地图卡片上的屏号是同一支字——"看见这个字形＝这是屏几"
                // 只有全应用一致才立得住，这里曾经用的是默认正文字体。纯 ASCII 数字，不存在缺字形问题。
                FontFamily = MainWindow.SignFont, Foreground = AppTheme.Ink,
                HorizontalAlignment = HorizontalAlignment.Center,
                // 把行盒收到贴着字身高。160px 的字，默认行盒约 187 高，而数字本身只有 115 上下——
                // 多出来的七十几像素全变成上下留白，加上 26/28 的内边距，单个数字的徽章被撑成一块
                // 明显的竖长方形（实测约 184×241），左右却是紧的。收完之后内边距才真的说了算，
                // 徽章回到接近正方；主屏那块多一行"主屏"，自然略高，也还是协调的。
                LineHeight = 124, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            var badgeText = new TextBlock
            {
                FontSize = 26, Foreground = AppTheme.Beam,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                Visibility = m.IsPrimary ? Visibility.Visible : Visibility.Collapsed
            };
            if (m.IsPrimary) badgeText.Text = Strings.Get("Badge_Primary");
            // round4: the UIA harness (tools/UiaHarness) measured this overlay's automation surface as
            // 4 disconnected Text elements — a screen reader hears "1" then, separately, "Primary",
            // with nothing tying them together. AutomationProperties.Name on the numeral (confirmed
            // reachable via UI Automation — Border, two lines up, is not) gives one coherent
            // announcement per monitor instead of two orphaned fragments.
            string spoken = SwapPlanner.PositionOf(m, monitors).ToString();
            AutomationProperties.SetName(number,
                m.IsPrimary ? $"{spoken}, {Strings.Get("Badge_Primary")}" : spoken);
            var stack = new StackPanel();
            stack.Children.Add(number);
            stack.Children.Add(badgeText);
            // 主屏那一块用 Beam 描边，其余不描——「屏幕编号」这条动作的用处就是把图上的编号对到
            // 桌上的机器，顺手让主屏在这一眼里就认得出来，省掉再回主窗口对一次。底色跟通知卡同一
            // 个做法：Panel 加透明度，浮在别人的内容上而不是盖一块实心补丁。
            var border = new Border
            {
                CornerRadius = new CornerRadius(26),
                Background = AppTheme.Tint(AppTheme.Panel, 0xD9),
                BorderBrush = m.IsPrimary ? AppTheme.Beam : AppTheme.Edge,
                BorderThickness = new Thickness(m.IsPrimary ? 2.5 : 1.5),
                Padding = new Thickness(52, 26, 52, 28),
                Child = stack
            };
            Root.Children.Add(border);
            _badges.Add((m, border));
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) => { _source?.RemoveHook(DpiChangedHook); _source = null; };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var h = new WindowInteropHelper(this).Handle;
        // 点击穿透 + 不抢焦点 + 不进 Alt-Tab（§10）——这扇窗纯展示，全程穿透。
        OverlayChrome.MakeOverlay(h, clickThrough: true);

        // round1/round2 的坑都来自"窗口跨越 DPI 不同的屏"这一下：WM_DPICHANGED 一到，WPF 自己的默认
        // 处理就会按它当时的缓存把 SetWindowPos 刚设的矩形覆盖掉，且钩子必须在触发它的 SetWindowPos
        // 之前挂好（round2 实测：晚一步挂等于没挂）。round3 把窗口本身改成了铺满整个虚拟桌面、创建后
        // 再也不移动的一扇窗——它这辈子只有一个 DPI，理论上不会再收到这条消息。这里留着钩子纯当兜底：
        // 万一某个 Windows 版本仍然发了这条消息，也不会被 WPF 的默认处理覆盖，我们的矩形才是权威
        // （这扇窗的唯一任务就是精确铺满量出来的虚拟桌面矩形，不听 lParam 里 Windows 建议的那个）。
        _source = HwndSource.FromHwnd(h);
        _source.AddHook(DpiChangedHook);

        // 用物理像素直接定位铺满整个虚拟桌面，绕开 WPF 的 DIP 换算。自查时跳过——铺满整个桌面
        // 的窗口即便 Opacity=0 也会吃掉一整块区域的命中测试，没必要在检查进程里做这件事。
        if (!App.InSelfCheck)
            OverlayChrome.Place(h, _virtual.Left, _virtual.Top, _virtual.Width, _virtual.Height, flags: 0);
    }

    private nint DpiChangedHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_DPICHANGED) return 0;
        OverlayChrome.Place(hwnd, _virtual.Left, _virtual.Top, _virtual.Width, _virtual.Height);
        handled = true;
        return 0;
    }

    // 不猜任何一块屏的 DPI——用真实测出来的窗口物理宽度 / DIP 宽度，得到这扇窗实际落地的缩放比。
    // 必须等布局跑完一遍再读 ActualWidth（构造函数里读到的是 0），Loaded 是最早能读到真值的地方。
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var h = new WindowInteropHelper(this).Handle;
        GetWindowRect(h, out var r);
        double windowScale = (r.Right - r.Left) / ActualWidth;

        // 顺手把每块屏的徽章按"目标屏自己的 DPI / 这扇窗量出来的缩放比"再缩放一次，不然 150% 屏和
        // 125% 屏上的数字物理大小会明显不一样。LayoutTransform（不是 RenderTransform）改了会参与
        // 度量，所以要先应用、再走一次同步布局，才能拿到变换后的 ActualWidth/Height 去居中。
        foreach (var (m, badge) in _badges)
        {
            double s = m.DpiScale / windowScale;
            badge.LayoutTransform = new ScaleTransform(s, s);
        }
        UpdateLayout();

        foreach (var (m, badge) in _badges)
        {
            double dipX = (m.MonitorRect.CenterX - _virtual.Left) / windowScale;
            double dipY = (m.MonitorRect.CenterY - _virtual.Top) / windowScale;
            Canvas.SetLeft(badge, dipX - badge.ActualWidth / 2);
            Canvas.SetTop(badge, dipY - badge.ActualHeight / 2);
        }
    }

    /// <summary>300ms 淡入 → 700ms 停留 → 300ms 淡出（§10）。</summary>
    public void Run(Action? onDone)
    {
        Opacity = 0;
        Show();
        var sb = new Storyboard();
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);
        sb.Completed += (_, _) => { Close(); onDone?.Invoke(); };
        sb.Begin();
    }

    public static void ShowAll(bool shutdownAfter = true)
    {
        var monitors = MonitorProbe.GetMonitors();
        new OverlayWindow(monitors).Run(() =>
        {
            if (shutdownAfter) Application.Current.Shutdown(0);
        });
    }
}
