using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;
using WindowShuttle.App.I18n;



namespace WindowShuttle.App;


/// <summary>
/// 托盘气泡的替代品——不出声音、不进操作中心。跟 OverlayWindow 共用同一套 win32 边界，而且是真的
/// 共用同一份代码，不只是长得像：见 <see cref="OverlayChrome"/>。落位纪律读 OverlayWindow.xaml.cs
/// 顶部那段三轮踩坑攒下的注释——geometry 必须用物理像素落位，校验也必须来自一个 DPI-aware 的进程，
/// 不然量出来的坐标会跟这扇窗自己的物理矩形对不上号。
///
/// 跟 OverlayWindow 的差别只在于这扇窗不铺满虚拟桌面，只落在鼠标所在的那一块屏——落位前就已经
/// 从 MonitorInfo 知道目标屏的 DpiScale，物理宽高 = ActualWidth/Height（纯 DIP 度量）× 那个 DpiScale
/// 直接算完，不需要像 OverlayWindow 那样先量一遍"这扇窗实际渲染在哪块 DPI"再反推，也不需要
/// WM_DPICHANGED 兜底（这扇窗创建后不移动，一辈子只认一块屏）。
/// </summary>
public partial class NotificationOverlay : Window
{
    // 三档严重程度的颜色住在 AppTheme.NotifyError/NotifyWarn/NotifyNeutral，不在这里。这个文件
    // 曾经自己又定义了一份同名的三支画刷，于是 AppTheme 那三支成了没人读的死代码，两边各自演进——
    // 深浅主题翻转时这张卡纹丝不动，因为它读的是自己那份写死的值。静态画刷都是 Frozen 的（见
    // AppTheme），跨线程共享安全。
    private const int FadeMs = 300;

    /// <summary>卡片底色的不透明度。见构造函数里的注释和 NotificationContrastTests。</summary>
    internal const byte CardAlpha = 0xF0;

    private static NotificationOverlay? _current;

    private readonly bool _actionable;
    private readonly Action? _onClick;
    private readonly MonitorInfo? _forcedMonitor;
    private bool _dismissed;

    /// <summary>不是 private 只为了 <c>--shots</c>：自查要的是"把这张卡构造出来量一遍/渲一遍"，
    /// 而 <see cref="Show"/>/<see cref="ShowOn"/> 都自带 <c>Show()</c>，赶在 App 把窗口挪到屏幕外之前
    /// 就先在主屏左上角闪一下。产品代码仍然只走 <see cref="Show"/>——只有它维护 <c>_current</c>
    /// 那条"下一条顶替上一条"的不变量。</summary>
    internal NotificationOverlay(string text, bool actionable, bool isError, Action? onClick, MonitorInfo? forcedMonitor = null)
    {
        InitializeComponent();
        // 排版方向逐窗口设：不能用 OverrideMetadata 一次性覆盖 typeof(Window)，
        // 那会撞坏 WPF 自己的 Window 静态构造函数（见 App.OnStartup 的注释）。
        FlowDirection = Strings.Flow;
        _actionable = actionable;
        _onClick = onClick;
        _forcedMonitor = forcedMonitor;
        // _current 只在 Dismiss 里清是不够的：任何绕开 Dismiss 的关闭（外部 Close()、应用退出）
        // 都会留下一个指向已关窗口的静态引用，下一条通知再去 Dismiss 它。挂在 Closed 上，
        // 不变量就跟"窗口是否还活着"绑定，而不是跟"走了哪条关闭路径"绑定。
        Closed += (_, _) => { if (_current == this) _current = null; };

        var accent = isError ? AppTheme.NotifyError : actionable ? AppTheme.NotifyWarn : AppTheme.NotifyNeutral;
        Card.BorderBrush = accent;
        // 卡片底不是纯 Panel：这扇窗浮在别人的内容上，全不透明会像一块补丁，全透明又读不清字。
        // 0xF0 是实测下来两头都站得住的那一档——见 NotificationContrastTests，它按最坏情况（纯白
        // 和纯黑背景）合成后再验文字对比度。
        Card.Background = AppTheme.Tint(AppTheme.Panel, CardAlpha);
        Message.Foreground = AppTheme.Ink;
        Chevron.Foreground = accent;
        Message.Text = text;
        if (actionable)
        {
            Cursor = Cursors.Hand;
            Chevron.Visibility = Visibility.Visible;   // §a11y 不掉色也认得出"这张卡能点"（见 XAML 注释）
            Card.MouseLeftButtonUp += (_, _) => { _onClick?.Invoke(); Dismiss(); };
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 点击穿透只留给非可操作的那条——可操作的那条要能吃到点击（提权重启）。其余边界设置
        // 两扇浮层窗口完全一致，住在 OverlayChrome 里。
        OverlayChrome.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: !_actionable);
    }

    // 等布局跑完一遍才能读到真实的 ActualWidth/Height（构造函数里读到的是 0）——SizeToContent
    // 把窗口量成内容的大小，Loaded 是最早能读到那个真值的地方。
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // --smoke/--shots 下这两件事都不能做：Place 会把这扇窗从屏幕外搬到真实工作区（自查进程
        // 不该在用户眼前闪东西），FadeIn 第一帧把 Opacity 设成 0，而 RenderTargetBitmap 照单渲染——
        // 截图会张张全透明，退出码照样是 0，只有真去看图才发现是空的（同一个坑见 Park 的注释）。
        if (App.InSelfCheck) return;
        // _forcedMonitor 只给 round4 的 UIA harness/测试用（见 ShowOn）——挪鼠标去够某块屏是对
        // 使用者正在用的物理鼠标指手画脚，落位逻辑本身不该为了"测哪块屏"而依赖真的把光标搬过去。
        var m = _forcedMonitor ?? SwapPlanner.MonitorAt(WindowProbe.GetCursor(), MonitorProbe.GetMonitors());

        int w = (int)Math.Round(ActualWidth * m.DpiScale);
        int h = (int)Math.Round(ActualHeight * m.DpiScale);
        int margin = (int)Math.Round(28 * m.DpiScale);
        int x = Math.Clamp(m.WorkArea.CenterX - w / 2, m.WorkArea.Left, Math.Max(m.WorkArea.Left, m.WorkArea.Right - w));
        int y = m.WorkArea.Bottom - margin - h;                            // 工作区底部之上，任务栏之外

        // 留着这行：「提示没出现」是个很难自查的抱怨，而它有三种完全不同的成因——卡压根没决定要弹、
        // 弹在了另一块屏上（落点跟的是光标所在屏，不是主屏）、或者量的人用了 DPI-unaware 的工具。
        // 最后这种最坑：拿默认的 PowerShell 去 GetWindowRect，读回来的坐标被主屏 DPI 虚拟化过
        // （实测 150%/125% 屏上差 1.2 倍），看着就像卡跑到屏幕外面去了。有这行就能一眼分清。
        if (ChordDebug.Enabled)
        {
            var c = WindowProbe.GetCursor();
            ChordDebug.Log($"toast  cursor={c.X},{c.Y} mon={m.Index} dpi={m.DpiScale} " +
                $"work=[{m.WorkArea.Left},{m.WorkArea.Top} {m.WorkArea.Width}x{m.WorkArea.Height}] " +
                $"actual={ActualWidth:F0}x{ActualHeight:F0} => place {x},{y} {w}x{h}");
        }
        OverlayChrome.Place(new WindowInteropHelper(this).Handle, x, y, w, h);
        FadeIn();
    }

    private void FadeIn()
    {
        Opacity = 0;
        int hold = _actionable ? 6000 : 2400;          // 可操作的那条要留够时间被看见、被点
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(FadeMs))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(FadeMs + hold))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(FadeMs + hold + FadeMs))));
        var sb = new Storyboard();
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);
        sb.Completed += (_, _) => Dismiss();
        sb.Begin();
    }

    // 幂等：自然淡出完成、点击关闭、被下一条通知顶替，三条路径都可能到这——第二次进来直接没事发生。
    private void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        Close();   // _current 的清理挂在 Closed 上，见构造里的注释——任何关闭路径都得维护这个不变量
    }

    /// <summary>下一条通知顶替上一条，不排队堆叠——旧的直接关掉，不等它自己淡出完。</summary>
    public static void Show(string text, bool actionable = false, bool isError = false, Action? onClick = null)
    {
        _current?.Dismiss();
        var overlay = new NotificationOverlay(text, actionable, isError, onClick);
        _current = overlay;
        overlay.Show();
    }

    /// <summary>Harness/test-only seam (see <see cref="_forcedMonitor"/>): same card, same win32
    /// dance, but the monitor is a parameter instead of "wherever the cursor is" right now. Never
    /// called from product code — <see cref="Show"/> is what ships.</summary>
    internal static NotificationOverlay ShowOn(MonitorInfo monitor, string text, bool actionable = false, bool isError = false)
    {
        _current?.Dismiss();
        var overlay = new NotificationOverlay(text, actionable, isError, null, monitor);
        _current = overlay;
        overlay.Show();
        return overlay;
    }
}
