using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WindowShuttle.App.I18n;



namespace WindowShuttle.App;


/// <summary>Replaces the two <c>MessageBox.Show</c> call sites (TrayService's elevation offer,
/// MainWindow's language-restart prompt) with a dialog that looks like this app, not like Windows.
/// Enter/Escape need no code here — <see cref="ConfirmButton"/>'s IsDefault and
/// <see cref="CancelButton"/>'s IsCancel are WPF's own Window-level key routing.</summary>
public partial class ConfirmDialog : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    // internal, not private: lets the regression tests construct one directly and drive it through a
    // real ShowDialog() modal pump (see ConfirmDialogTests.cs) instead of only exercising it through
    // the blocking static Confirm() facade. Production code only ever goes through Confirm().
    internal ConfirmDialog(string message)
    {
        InitializeComponent();
        // 排版方向逐窗口设：不能用 OverrideMetadata 一次性覆盖 typeof(Window)，
        // 那会撞坏 WPF 自己的 Window 静态构造函数（见 App.OnStartup 的注释）。
        FlowDirection = Strings.Flow;
        MessageText.Text = message;
        ApplyTheme();
        // 同 MainWindow：ThemeMode 管的是控件，原生标题栏跟不跟随由 DWM 这个属性决定，而且句柄要等
        // SourceInitialized 才有。深浅跟着 AppTheme 走，不再恒为深色。
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int dark = AppTheme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            CapHeightToThisMonitor(h);
        };
    }

    /// <summary>SizeToContent=Height 的窗口高度完全由内容决定，必须封顶，否则文案一长就长到屏幕外，
    /// 首当其冲的是最底下那一行「重启／取消」——按钮跑到任务栏底下就等于这个对话框没法用了。
    ///
    /// 封顶值不能写死：同一个数字要同时伺候 1366×768 的笔记本（工作区约 728 DIP）和 4K 屏，
    /// 写死的数一定在某一头是错的。改成按**这扇窗所在那块屏**的工作区算。
    ///
    /// 两处容易写错，都避开了：
    ///   · 不用 SystemParameters.WorkArea——它给的是主屏的工作区，而这个应用声明了 PerMonitorV2、
    ///     不存在"整个桌面一个尺寸"，何况这扇框是 CenterOwner，主窗口在副屏时就错了；
    ///   · DPI 换算直接用 VisualTreeHelper.GetDpi(this)，PerMonitorV2 下它返回的就是这块屏的缩放，
    ///     不必自己去 P/Invoke GetDpiForMonitor。
    ///
    /// XAML 里那个 MaxHeight="420" 保留，而且**故意留保守值**：探测成功会覆盖它，所以它唯一生效的
    /// 场合就是探测失败——而那正是最需要保守值兜底的时候。写成一个宽松的大数，等于在兜底路径上
    /// 亲手复活要修的这个 bug。</summary>
    private void CapHeightToThisMonitor(nint hwnd)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (scale <= 0) return;
        double cap = (mi.rcWork.Bottom - mi.rcWork.Top) / scale - 72;   // 上下各留点呼吸
        MaxHeight = Math.Max(cap, MinHeight);                            // 不能低于自己声明的下限
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    /// <summary>颜色一律从 AppTheme 取，XAML 里不留字面量。这扇框活得很短（弹出、点掉），不像
    /// MainWindow 那样需要订阅 AppTheme.Changed——它只在被创建的那一刻取一次当前主题就够了。</summary>
    private void ApplyTheme()
    {
        Background = AppTheme.Room;
        Foreground = AppTheme.Ink;
        MessageText.Foreground = AppTheme.Ink;
        // 主按钮反相：拿 Ink 当底、Room 当字。Ink 的定义就是"对底色最大对比的中性色"（深色主题里是
        // #EAEBF0，浅色里是 #242834），所以这一行在两个主题里自动给出同一种关系，不用写两套颜色。
        //
        // 这是实测出来的不对称：WPF-UI 的 Primary 外观在浅色下解出近黑填充、对 Room 有 11.29:1，在
        // 深色下却只解出一块中灰 (89,89,89)，对 Room 只有 2.79:1 —— **低于 WCAG 1.4.11 给非文本组件
        // 的 3:1**，全靠描边的 3.40:1 把它救回可感知线。于是同一个"主操作"在深色下的视觉权重只有浅色
        // 的四分之一，而这扇框问的是"要不要以管理员身份重启"，主操作必须一眼认得出。
        //
        // 用 Ink 而不是 Beam/Live：那两个色在这个产品里**有语义**（主屏 / 光标所在屏），借给按钮用
        // 就是把屏幕状态的信号稀释掉。中性反相既拿到最大对比，又不动用那两格。
        ConfirmButton.Background = AppTheme.Ink;
        ConfirmButton.Foreground = AppTheme.Room;
        ConfirmButton.BorderBrush = AppTheme.Ink;
        // WPF0001：ThemeMode 目前标着 [Experimental]。就地抑制而不是全局 NoWarn——这样万一它被改名
        // 或移除，编译器只会在这一行报，不会在整个项目里静默失效。上一版把 ThemeMode="Dark" 写在
        // XAML 里，同一个实验性 API，只是 XAML 不跑这个分析器，所以从没提示过。
#pragma warning disable WPF0001
        ThemeMode = AppTheme.IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        // 焦点环的描边色住在一个 ControlTemplate 里，拿不到名字，只能整支样式在这里重建。
        var ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
        ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.6);
        ring.SetValue(System.Windows.Shapes.Shape.StrokeProperty, AppTheme.Live);
        ring.SetValue(System.Windows.Shapes.Rectangle.RadiusXProperty, 5.0);
        ring.SetValue(System.Windows.Shapes.Rectangle.RadiusYProperty, 5.0);
        ring.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var style = new Style();
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate { VisualTree = ring }));
        ConfirmButton.FocusVisualStyle = style;
        CancelButton.FocusVisualStyle = style;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>owner may be null — <see cref="TrayService.OfferElevation"/> is raised from the tray
    /// icon, which has no window of its own to own this dialog. CenterScreen instead of CenterOwner
    /// when there is nothing to center over; WindowStartupLocation.CenterOwner with a null Owner would
    /// otherwise just fall back to whatever WPF's default placement happens to be, unlabeled as such.</summary>
    public static bool Confirm(Window? owner, string message)
    {
        var dlg = new ConfirmDialog(message)
        {
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        return dlg.ShowDialog() == true;
    }
}
