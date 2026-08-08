using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;
using Wpf.Ui.Controls;

namespace WindowShuttle.App;

public partial class MainWindow : FluentWindow
{
    // round4 Part 1: the app's own semantic palette (Beam = primary monitor, Live = cursor's monitor,
    // plus the neutral Room/Panel/Ink/Dim/Faint scale) moved to AppTheme.cs, theme-aware and rebuilt
    // live on a system theme change — see that file's doc comment for why it's plain static fields
    // rather than a ResourceDictionary. Map.cs/Hotkeys.cs read AppTheme.* directly at draw time instead
    // of a field on this class.

    /// <summary>标牌字：Windows 自带的 DIN 系 Bahnschrift，路牌和站台号用的就是这一类，而屏幕编号
    /// 恰好就是一个指路用的数字。全应用只在两个地方用它——地图卡片上的屏号、以及「屏幕序号」那块
    /// 全屏浮层上的大号数字（OverlayWindow 引的就是这个字段），加上键帽词条。internal 是为了让
    /// OverlayWindow 引同一支而不是自己再 new 一个同名字符串：这个字形的辨识度靠的就是"只在这几处
    /// 出现"，两处各写各的迟早会漂。绝不能喂中文——它没有中文字形。</summary>
    internal static readonly FontFamily SignFont = new("Bahnschrift, Segoe UI");

    /// <summary>数据字：分辨率、缩放比、刷新率这类要对齐的数字。同样绝不喂中文。</summary>
    private static readonly FontFamily MonoFonts = new("Cascadia Mono, Consolas");

    /// <summary>首次启动的窗口尺寸与下限，DIP。XAML 用 x:Static 引这四个常量，MainWindowBoundsTests
    /// 也引它们——之前两边各写了一份字面量，改了 XAML 那份，测试就会继续按一个应用早已不用的尺寸
    /// 去验"六档分辨率都放得下"，绿着但验的是别的东西。
    ///
    /// 高度给得比内容实际需要的多一截：默认宽度下动作区是**两列**，现在这份清单（六个固定动作 +
    /// 最多三行「送去第 N 块屏」，主屏那一块不单列）就是四到五行卡，加上地图带，紧巴巴算下来
    /// 七百出头就够，但那点余量经不起任何变化——多接一块屏、换一种更长的语言，就得开始滚动。
    /// 首次启动那一眼要能一次看全，这是这扇窗唯一的任务。真放不下的小屏由 ComputeStartupBounds
    /// 按工作区夹取兜底，给大了不会溢出屏幕。
    ///
    /// 宽高是一起定的，不能分开调。这扇窗的内容有天花板：动作卡数量封顶，地图带的高度由桌面自身的
    /// 长宽比算出来——一排三屏的桌面"想要"的带子就是矮的，窗口再高它也不长。所以单加高度必然多出
    /// 一片空洞（试过 960×880，多出的一百多像素全落在最后一行卡片和设置条之间，看着像没画完）。
    ///
    /// **别拿最大化的截图来判断这个数。** 砍掉三个动作之后，2512×1352 那张最大化的图下半空了近一半，
    /// 照着它把 900 调到 720，默认尺寸下最后一行卡片当场被切掉——最大化时是三列三行，默认尺寸是
    /// 两列四行，两者的高度需求根本不是一回事。要看就看 --shots 的 1080p100 那一档，它现在直接引
    /// 这两个常量。
    ///
    /// 加宽才是喂得进去的那一头：带高按 cw × 长宽比 算，窗口越宽地图越高，同时动作卡也更宽。
    /// 1120 是两列的上界附近——再宽到约 1230 就切三列，卡片行数减少，内容反而更矮，又空出来。
    /// 900 是配合 1120 之后内容自然长到的高度。改任何一个都要回头把另一个实测一遍。</summary>
    public const double DefaultWidthDip = 1120, DefaultHeightDip = 900;

    /// <summary>拖拽下限。480 不是随手挑的富余值，是被一块真实屏幕逼出来的硬下限：
    /// 1366×768 @150%（Windows 确实允许，视力不好的人会这么设）的工作区换算成 DIP 只有 480 高。
    ///
    /// 试过提到 560——理由是 480 上这扇窗怎么摆都不体面——当场被 MainWindowBoundsTests 那档
    /// "1366x768@150%" 顶回来：下限比该屏能给出的最大高度还大，Clamp 的 min 超过 max 直接抛。
    /// 所以 480 只能留着，代价由布局那边承担：地图带让位到 MapFloorWhenCrowded，把空间让给动作卡，
    /// 保证最矮的窗口里第一张动作卡仍然完整可见（见 MainWindow.Map.cs 那段让位逻辑）。</summary>
    public const double MinWidthDip = 760, MinHeightDip = 480;

    /// <summary>恢复上次尺寸的下限，比拖拽下限高——见 <see cref="ComputeStartupBounds"/> 的说明。
    ///
    /// 两个数的来历：860 宽是动作区还能切两列的窗口宽度（两列门槛量的是动作区自己的 760，加上左右
    /// 边距和滚动条约 90）；620 高是地图带落在它的下限 140 时，动作区还能露出三张卡的高度。低于
    /// 任一条，这扇窗就摆不下它自己的内容，那不是一个值得在下次启动时重现的"偏好"。</summary>
    public const double RestoreMinWidthDip = 860, RestoreMinHeightDip = 620;

    /// <summary>设置栏最多占窗口高度的多少，超出部分它自己滚。
    ///
    /// 这条是实测逼出来的，量得很具体：拖到下限 760×480 时，中文的设置栏是 119px、动作区视口 71px，
    /// 刚够露出一张 70px 的动作卡；换成英语/德语/俄语/印尼语，同样五个复选框折成四行、设置栏涨到
    /// 191px，动作区只剩 23px——**除中文外每一门语言，最矮的窗口里主功能面都是空的**。而中文那 71
    /// 只比一张卡多 1px，所以只跑默认语言的测试一直是绿的（见 MainWindowCompactLayoutTests）。
    ///
    /// 三条路都堵死了才轮到这里：地图带此时已经让到 <see cref="MapFloorWhenCrowded"/>；
    /// <see cref="MinHeightDip"/> 抬不了（1366×768@150% 的工作区就只有 480 DIP 高，见它的说明）；
    /// 给动作区设 MinHeight 会让它照 MinHeight 画出去、盖在设置栏上。剩下的唯一来源就是设置栏自己。
    ///
    /// 0.28 是解出来的，不是拍的：480 × 0.28 = 134，比 191 少 57，正好把视口从 23 抬到 80 —— 一张
    /// 70px 的卡加 10px 余量。而常见档位上它不生效（632 × 0.28 = 177 > 119），滚动条也就不会出现。</summary>
    private const double FooterMaxShare = 0.28, FooterMinHeightDip = 96;

    /// <summary>只在值真的变了才写：这个方法从窗口的 SizeChanged 里调，而那是在布局过程中抬起来的，
    /// 无条件改 MaxHeight 会再抬一次同一个事件。</summary>
    private void CapFooter()
    {
        double cap = Math.Max(FooterMinHeightDip, ActualHeight * FooterMaxShare);
        if (Math.Abs(FooterBorder.MaxHeight - cap) > 0.5) FooterBorder.MaxHeight = cap;
    }

    private List<MonitorInfo> _monitors = [];
    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<int, Border> _cards = [];
    private int _cursorMonitorIndex = -1;
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        // 排版方向逐窗口设：不能用 OverrideMetadata 一次性覆盖 typeof(Window)，
        // 那会撞坏 WPF 自己的 Window 静态构造函数（见 App.OnStartup 的注释）。
        FlowDirection = Strings.Flow;
        BuildActionRows();
        LoadToggles();
        ApplyTheme();
        AppTheme.Changed += ApplyTheme;
        Closed += (_, _) => AppTheme.Changed -= ApplyTheme;   // 不摘掉的话，静态事件会一直攥着这扇已经关掉的窗口
        _cursorTimer.Tick += (_, _) => HighlightCursorMonitor();
        Loaded += (_, _) => { _loaded = true; Refresh(); _cursorTimer.Start(); };
        // 指针进出**整扇窗**都要重算录制态——SyncChordCapture 现在把 IsMouseOver 当作安全前提之一
        // （理由写在那里）。键位区自己的 MouseEnter/MouseLeave 顶不了这个班：从键位区滑到窗口内的
        // 别处会触发它，而真正危险的那一步"指针离开整扇窗"不会，于是捕获会一直武装到用户去别的
        // 程序里点一下为止。
        MouseEnter += (_, _) => SyncChordCapture();
        MouseLeave += (_, _) => SyncChordCapture();
        // 地图带的高度是按窗口高度算出来的（见 MainWindow.Map.cs 的 cap），可它此前只在**画布**尺寸
        // 变化时才重算——而画布的高度正是那次计算写下去的，于是拖动窗口下边缘改高度时，画布尺寸
        // 不变、事件不来、带高原样卡住。实测 1120×900 和 1120×632 两扇窗量到的带高一模一样都是
        // 251px：在矮的那扇里它占了 40%（本该 32%），多吃的那 49px 正是动作区缺的。
        // 宽度那条路一直是通的（改宽必然改画布宽），所以这个缺陷只在纵向出现。
        // 必须跟 OnCanvasSized 共用同一个重入闸：DrawMonitors 内部会同步 UpdateLayout，而这个处理器
        // 本身就是在布局过程中被抬起来的——不挡，UpdateLayout 会在嵌套布局里再抬一次同一个事件，
        // 无限递归。实测就是 --shots 一张图都产不出来、整个进程挂死在第一扇窗上。
        SizeChanged += (_, e) =>
        {
            CapFooter();
            if (!_resizingCanvas && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5) DrawMonitors();
        };
        // 还要盯动作区视口自己的尺寸变化。上面那条只在窗口高度变的**那一瞬**抬起来，而它是在布局
        // 过程中抬的——此时 DrawMonitors 内部那次 UpdateLayout 是重入的、WPF 直接当空操作，于是它读到
        // 的视口高度还是上一帧的，算出来的带高差一档就此定格，没有任何东西会再纠它（实测 760×480 下
        // 带高停在 110、动作卡的描述那行被切掉）。视口尺寸落定时再算一次，读到的才是真值。
        // 同样走 _resizingCanvas 闸：我们自己改带高必然会改视口，不挡就是自激。
        ActionsScroller.SizeChanged += (_, _) => { if (!_resizingCanvas) DrawMonitors(); };
        SizeChanged += (_, _) => SyncHeaderHint();
        Loaded += (_, _) => SyncHeaderHint();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); _cursorTimer.Start(); } else _cursorTimer.Stop(); };
        // 手势录制态挂住的代价是全系统的（见 MainWindow.MouseChords.cs 的说明），所以除了键位区
        // 自己的进出，窗口一级的每一次"不再在前台"也要去重算一遍：切走、藏进托盘、被最小化。
        Deactivated += (_, _) => SyncChordCapture();
        Activated += (_, _) => SyncChordCapture();
        IsVisibleChanged += (_, _) => SyncChordCapture();
    }

    // round4 Part 1: pushes AppTheme's current values into everything this window set at construction
    // time (WPF-UI's own controls restyle themselves via DynamicResource — this is only for the
    // hand-drawn parts and the handful of named XAML elements that used to carry hardcoded hex).
    // Called once up front and again every time AppTheme.Changed fires (system theme flip while this
    // window is open, shown or hidden-to-tray).
    /// <summary>窄到一定程度就把地图那行说明整句收起来。
    ///
    /// 它跟标题、以及右边三个按钮（GitHub / 屏幕序号 / 刷新）挤在同一行。760 宽的德语下位置根本不够，
    /// TextTrimming 只会留下一截切在词中间的残句——"Gezeichnet nach der gefühlten Größe und Lag…"
    /// 读不出任何意思，还占着位置。说明文字是补充，按钮是功能：地方不够时该让路的是前者，
    /// 而且要整句让路，不是留个残句充数。</summary>
    private const double HeaderHintMinWidth = 900;

    private void SyncHeaderHint()
        => MonitorsHint.Visibility = ActualWidth >= HeaderHintMinWidth
            ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyTheme()
    {
        // RootSurface 才是真正上屏的那块地，见 XAML 里它的注释——Window.Background 会被 FluentWindow
        // 的模板背景盖掉。两个都设：模板之下还有窗口自身（缩放边框、最大化过场那一瞬）会用到。
        RootSurface.Background = AppTheme.Room;
        Background = AppTheme.Room; Foreground = AppTheme.Ink;
        // 两个章节标题的前景必须显式给。TextBlock 不写 Foreground 时并不会继承 Window.Foreground——
        // WPF-UI 的 ControlsDictionary 带了一条 TextBlock 隐式样式，隐式样式优先于继承值，于是标题
        // 的颜色实际归 WPF-UI 的主题管、而不是 AppTheme。生产里两者由 App.xaml.cs 一起翻转所以看不
        // 出来，可一旦有哪条路径只翻了一边，标题就是深色底上的深色字——直接隐形。四行钉死，不赌同步。
        MonitorsTitle.Foreground = AppTheme.Ink;
        ActionsTitle.Foreground = AppTheme.Ink;
        ActionsDivider.Background = AppTheme.Rule;      // 标题旁的装饰线，不是组件边界，走 Rule 不走 Edge
        StartupTie.Background = AppTheme.Rule;         // 「以管理员身份」从属于「开机自启」的那条竖线，同一档
        MonitorsHint.Foreground = AppTheme.Dim;
        ActionsHint.Foreground = AppTheme.Dim;
        LangLabel.Foreground = AppTheme.Dim;
        FooterBorder.BorderBrush = AppTheme.Rule;
        MouseChordCaution.Foreground = AppTheme.Faint;
        MouseCautionGlyph.Content = MakeMouseGlyph(AppTheme.Faint, 12);
        DropHint.Foreground = AppTheme.Faint;
        RepoButton.Content = MakeGitHubGlyph(AppTheme.Faint); // 自绘图形，翻主题得重画；
                                                             // 用 Faint 跟同一行的提示文字同级
        SetDarkTitlebar();
        DrawMonitors();       // 重建地图卡片——它们的颜色是 BuildCard 建的时候当场从 AppTheme 读的，不是绑定
        // 动作卡的底色/描边同样是建的时候读死的，翻主题得重新刷一遍——上一版卡片常态全透明，
        // 没有这一步也看不出来；现在它们有了自己的面，漏刷就会留着上一个主题的底色。
        foreach (var row in ActionList.Items.OfType<Border>()) UpdateRowChrome(row);
        RefreshAllCaps();
        RefreshAllMouseCaps();
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    // ExtendsContentIntoTitleBar 拿掉了原生标题栏，但 DWM 仍然用这个属性决定窗口边框/Snap 布局
    // 悬浮面板走哪套配色——同一个 API，只是判断依据从"永远深色"改成跟 AppTheme 走。SourceInitialized
    // 之前句柄还没有，第一次调用挪到 OnSourceInitialized；ApplyTheme 之后每次主题翻转再补一次。
    private void SetDarkTitlebar()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == 0) return;
        int dark = AppTheme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

    }

    // §window-bounds 起手式几何全用物理像素直接 SetWindowPos，不假手 WPF 的 DIP 换算——跟
    // OverlayWindow.xaml.cs 顶部那段三轮踩坑的注释同一套纪律：WPF 创建 HWND 时只按系统/主屏 DPI
    // 算了一版初始矩形，等它反应过来这扇窗实际落在哪块屏、该用哪个缩放比，已经晚了一拍（WM_
    // DPICHANGED 补一次是常见做法，但会有一帧可见的"先摆错再纠正"）。这里在 HWND 刚创建、还没
    // 上屏（OnSourceInitialized 在 Show() 真正显形之前触发）的时候就把物理矩形钉死，不留缝隙让
    // WPF 的默认路径插手。
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>纯计算——不摸 HWND，只吃屏幕列表 + 上次存的设置 + 默认/最小 DIP 尺寸，吐物理像素
    /// 矩形。拆出来是为了能测：六档分辨率的夹取结果都能直接断言，不用真的接一块 125%/150% 的屏
    /// （见 tests/.../MainWindowBoundsTests.cs）。
    ///
    /// 有存档、那块屏还在（按矩形中心点落在哪块屏的工作区判断）、且存的尺寸不小于可用下限：原样
    /// 恢复那次的位置/尺寸，只做安全夹取（工作区可能因为任务栏挪动/变了缩放比而比当初小）。三条
    /// 任一不成立：回落到主屏居中、默认 DIP 尺寸。夹取本身留一圈 marginDip 的余量，不顶到工作区边缘。
    ///
    /// 「可用下限」跟 minWidthDip/minHeightDip 是两回事，别混：后者是**拖拽**下限，正在动手调整
    /// 窗口的人想拖多小是他的自由；前者是**恢复**下限，一个连内容都摆不下的尺寸不该被当成"用户偏好"
    /// 在下次启动时重现。两者必须不同，否则就会出现实际踩到的那一幕：某次窗口被弄成正好等于拖拽
    /// 下限（760×480 DIP）并落了盘，此后每次打开都是那个尺寸——动作区退回单列、只露一张卡，
    /// 而"默认高度"改成多少都无所谓，因为存档分支根本轮不到默认值。</summary>
    public static (int X, int Y, int W, int H) ComputeStartupBounds(
        IReadOnlyList<MonitorInfo> monitors, Settings cfg,
        double defaultWidthDip, double defaultHeightDip, double minWidthDip, double minHeightDip)
    {
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        (int Left, int Top, int Width, int Height)? saved =
            cfg.WindowLeft is int sl && cfg.WindowTop is int st
                && cfg.WindowWidth is int sw && cfg.WindowHeight is int sh
                ? (sl, st, sw, sh) : null;

        var target = primary;
        int x, y, w, h;
        if (saved is { } s && monitors.FirstOrDefault(m => m.WorkArea.Contains(
                new PointPx(s.Left + s.Width / 2, s.Top + s.Height / 2))) is { } found
            && s.Width >= RestoreFloorPx(RestoreMinWidthDip, minWidthDip, found.DpiScale, found.WorkArea.Width)
            && s.Height >= RestoreFloorPx(RestoreMinHeightDip, minHeightDip, found.DpiScale, found.WorkArea.Height))
        {
            target = found;
            (x, y, w, h) = (s.Left, s.Top, s.Width, s.Height);
        }
        else
        {
            w = (int)Math.Round(defaultWidthDip * target.DpiScale);
            h = (int)Math.Round(defaultHeightDip * target.DpiScale);
            x = target.WorkArea.Left + (target.WorkArea.Width - w) / 2;
            y = target.WorkArea.Top + (target.WorkArea.Height - h) / 2;
        }

        int marginPx = (int)Math.Round(MarginDip * target.DpiScale);
        int minWPx = (int)Math.Round(minWidthDip * target.DpiScale);
        int minHPx = (int)Math.Round(minHeightDip * target.DpiScale);
        int maxW = Math.Max(target.WorkArea.Width - marginPx * 2, minWPx);
        int maxH = Math.Max(target.WorkArea.Height - marginPx * 2, minHPx);
        w = Math.Clamp(w, minWPx, maxW);
        h = Math.Clamp(h, minHPx, maxH);
        x = Math.Clamp(x, target.WorkArea.Left, target.WorkArea.Left + target.WorkArea.Width - w);
        y = Math.Clamp(y, target.WorkArea.Top, target.WorkArea.Top + target.WorkArea.Height - h);
        return (x, y, w, h);
    }

    private const int MarginDip = 24;

    /// <summary>恢复下限，但**不得超过那块屏实际给得出的最大尺寸**——表达式跟
    /// <see cref="ComputeStartupBounds"/> 末尾那两句 Clamp 同源，两处必须一起改。
    ///
    /// 少了这道「跟着屏幕一起夹」，下限在矮屏上就是一道永远迈不过去的门槛：1920×1080@175% 的
    /// 工作区只有约 617 DIP 高，而 Clamp 会把任何高度压到 maxH 以下——于是每次存盘存进去的高度
    /// 必然小于 RestoreMinHeightDip，下次启动这个分支永远不成立，「记住窗口位置」在这类屏上
    /// 永久失效，而且用户无论怎么拖都够不到那个门槛。原来的 1366×768@100% / 1920×1080@150%
    /// 两组测试都够得着（后者只富余 6px），所以没人发现。</summary>
    private static int RestoreFloorPx(double restoreDip, double minDip, double scale, int workAreaExtent)
    {
        int marginPx = (int)Math.Round(MarginDip * scale);
        int minPx = (int)Math.Round(minDip * scale);
        int maxPx = Math.Max(workAreaExtent - marginPx * 2, minPx);
        return Math.Min((int)Math.Round(restoreDip * scale), maxPx);
    }

    private void ApplyStartupBounds(nint h)
    {
        var monitors = MonitorProbe.GetMonitors();
        if (monitors.Count == 0) return;               // 不该发生，但别在起手这一步崩掉整个启动
        var (x, y, w, hgt) = ComputeStartupBounds(monitors, App.Cfg, Width, Height, MinWidth, MinHeight);
        SetWindowPos(h, 0, x, y, w, hgt, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        // 自查（--smoke/--shots）时不要落位：那两个开关已经把窗口停在屏幕外了，再按真实屏幕算一遍
        // 就等于把它搬到用户眼前。
        if (!App.InSelfCheck) ApplyStartupBounds(h);
        SetDarkTitlebar();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowBounds();
        // §10: 关闭按钮"最小化到托盘而非退出"是 CloseToTray 打开时才有的行为。关掉之后，ShutdownMode
        // 是 OnExplicitShutdown（没有主窗口时也不能因为窗口没了就自动退出——托盘图标还得活着），
        // 所以这里必须显式 Shutdown，否则进程会在没有任何窗口、也没有人叫它退出的状态下裸奔在后台。
        // 自查（--smoke/--shots）下两条分支都不能走：CloseToTray 默认是 true，照产品路径走会把
        // 每次 Close() 变成 Hide()——--shots 一轮下来十几扇 MainWindow 全都活着、全都还挂在
        // AppTheme.Changed 上，每翻一次主题就全体重绘一遍，越跑越慢；而 --smoke 声称的
        // 「构造并布局一遍就退出」其实从没真的关掉过主窗口。也不能顺势落到下面的 Shutdown：
        // 那会在截图跑到一半时把自查进程本身关掉。自查就是要 Close() 真的把这扇窗关掉。
        if (!App.InSelfCheck)
        {
            if (App.Cfg.CloseToTray) { e.Cancel = true; Hide(); }
            else Application.Current.Shutdown(0);
        }
        base.OnClosing(e);
    }

    // §window-bounds 只在 Normal 态存：最大化/最小化时的物理矩形不是用户想记住的"大小"，存下来
    // 下次会把窗口诡异地摆成铺满旧屏那么大。跳过这两态时，上一次 Normal 态存的值原样留着——不清空。
    private void SaveWindowBounds()
    {
        // 自查进程绝不写用户配置。这一条是承重的：--shots 会连开十几扇窗，每扇关闭时都会走到这里，
        // 少了它，一次截图就把用户的"上次窗口位置"覆盖成屏幕外的 -32000。
        if (App.InSelfCheck) return;
        if (WindowState != WindowState.Normal) return;
        var h = new WindowInteropHelper(this).Handle;
        if (h == 0 || !GetWindowRect(h, out var r)) return;
        App.Cfg.WindowLeft = r.Left; App.Cfg.WindowTop = r.Top;
        App.Cfg.WindowWidth = r.Right - r.Left; App.Cfg.WindowHeight = r.Bottom - r.Top;
        SettingsStore.Save(SettingsStore.DefaultPath, App.Cfg);
    }

    private void Refresh()
    {
        _monitors = MonitorProbe.GetMonitors();
        _windowCache = null;                    // 用户明确要求"重新看一眼"，缓存必须让路
        DrawMonitors();
    }

    /// <summary>显示器增减时由 App 的 DisplaySettingsChanged 调。窗口没显示就不用管——重新显示时
    /// IsVisibleChanged 会自己刷一次。</summary>
    internal void RefreshMonitors()
    {
        if (IsVisible) Refresh();
    }

    // ---------- 开关与语言 ----------
    private void LoadToggles()
    {
        // 自启有两种落地（HKCU\Run / 提权计划任务，见 StartupRegistration 的类注释），
        // 「开机自启」这一格反映的是"有没有任何一种"，「以管理员身份」反映的是"是不是提权那种"。
        bool elevated = StartupRegistration.GetElevated();
        StartupBox.IsChecked = elevated || StartupRegistration.Get();
        ElevatedStartupBox.IsChecked = elevated;
        ElevatedStartupBox.IsEnabled = StartupBox.IsChecked == true;
        SkipFsBox.IsChecked = App.Cfg.SkipFullscreen;
        RescueBox.IsChecked = App.Cfg.RescueOnDisplayChange;
        TrayBox.IsChecked = App.Cfg.CloseToTray;

        LangBox.Items.Add(Strings.Get("Settings_Language_System"));
        foreach (var (native, _) in Languages.All) LangBox.Items.Add(native);
        LangBox.SelectedIndex = App.Cfg.Language is null ? 0
            : Array.FindIndex(Languages.All, x => x.Code == Languages.Normalize(App.Cfg.Language)) + 1;
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        // 不 await：这个处理器是 UI 事件，而 ApplyStartupMode 里可能要等用户答 UAC。下面几行
        // 跟自启无关，不该被那个对话框挡住。_applyingStartup 挡住了这期间的重入。
        _ = ApplyStartupMode();                                  // 注册表/任务计划器才是唯一事实来源，Settings 不镜像
        App.Cfg.SkipFullscreen = SkipFsBox.IsChecked == true;
        App.Cfg.RescueOnDisplayChange = RescueBox.IsChecked == true;
        App.Cfg.CloseToTray = TrayBox.IsChecked == true;
        SettingsStore.Save(SettingsStore.DefaultPath, App.Cfg);
    }

    /// <summary>把两个自启复选框读成三态（关 / 普通 / 提权）落到系统里。提权那档要过 UAC，
    /// 用户点「否」时必须把复选框拨回真实状态——界面上勾着、系统里没有，是最阴的一种谎。
    ///
    /// _applyingStartup 挡重入：拨回复选框会再触发 OnToggle。</summary>
    private bool _applyingStartup;

    /// <summary>把 SetElevated 挪出 UI 线程跑，并等它的结果。
    ///
    /// 它内部是 <c>WaitForExit(30000)</c>——UAC 同意框弹着的这段时间**全在这一句里**。放在 UI 线程上
    /// 等于让刚打开的主窗口停止泵消息：窗口变白、标题被 Windows 加上「未响应」，用户犹豫多久就白多久
    /// （最长 30 秒）。首次运行那条路更难看，窗口刚出来就僵在那儿。
    ///
    /// 用显式 STA 线程而不是 Task.Run：SetElevated 走的是 ShellExecute（UseShellExecute + runas），
    /// 那是 COM 的地盘，STA 才是它的正经居所。线程池给的是 MTA，虽然 .NET 会在内部帮忙转一道，
    /// 但这种事没必要赌——几行代码换一个不用查文档就能确信的前提。</summary>
    private static Task<bool> SetElevatedOffUiThread(bool on)
    {
        var done = new TaskCompletionSource<bool>();
        var t = new Thread(() =>
        {
            try { done.SetResult(StartupRegistration.SetElevated(on)); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    private async Task ApplyStartupMode()
    {
        if (_applyingStartup) return;
        _applyingStartup = true;
        try
        {
            bool wantStartup = StartupBox.IsChecked == true;
            bool wantElevated = wantStartup && ElevatedStartupBox.IsChecked == true;
            bool haveElevated = StartupRegistration.GetElevated();

            if (wantElevated && !haveElevated)
            {
                if (!await SetElevatedOffUiThread(true))             // UAC 被拒：拨回去
                    ElevatedStartupBox.IsChecked = false;
            }
            else if (!wantElevated && haveElevated)
            {
                if (await SetElevatedOffUiThread(false))
                    // 任务真的删掉了，复选框也得跟上。原来这里什么都不做——取消「开机自启」会连带
                    // 删掉提权任务，而「以管理员身份」那一格仍然勾着（只是变灰）。用户接着重新勾上
                    // 「开机自启」、只想要普通自启，wantElevated 却从那个陈旧的勾算出 true，于是**冒出
                    // 一个他没要求的 UAC**，还把提权任务又建了回去。重启一次 LoadToggles 会读出真实
                    // 状态，于是同一串操作的结果取决于中间有没有重启过——最难查的那种不一致。
                    ElevatedStartupBox.IsChecked = false;
                else                                                 // 删任务也要 UAC；被拒就拨回真实状态
                {
                    ElevatedStartupBox.IsChecked = true;
                    StartupBox.IsChecked = true;
                }
            }

            // 普通 Run 值：只在"要自启且不是提权那种"时存在（两种方式互斥，否则开机起两个实例）。
            bool elevatedNow = StartupRegistration.GetElevated();
            StartupRegistration.Set(StartupBox.IsChecked == true && !elevatedNow);
            ElevatedStartupBox.IsEnabled = StartupBox.IsChecked == true;
        }
        finally { _applyingStartup = false; }
    }

    /// <summary>首次运行的出厂动作：**只**打开普通的开机自启，不碰提权那一档。
    ///
    /// 这里曾经把两格一起勾上、当场请求一次 UAC。那是为作者自己那台机器定的，放到公开发布上代价变了：
    /// 新用户下载的是一个**没有代码签名**的 exe，先要过 SmartScreen 的「更多信息 → 仍要运行」，窗口
    /// 刚出来又立刻弹一个 UAC 同意框——两道拦截连着来，对一个陌生小工具是很强的劝退信号。而这笔成本
    /// 只有一部分人需要付：提权解决的是「管理员程序的窗口搬不动 / 它占前台时手势整体失效」，撞不到
    /// 这两条的人根本用不着。
    ///
    /// 不做等于不告诉用户吗？不是——**发现路径本来就在**：真撞上限制时会弹一张可点的提示卡（见
    /// TrayService.NotifyGesturesBlocked / Toast_AccessDenied），托盘菜单里也常驻「以管理员身份重启」，
    /// 设置页那一格随时可勾。让需要的人在需要的那一刻自己开，比向所有人预收一次权限要诚实。
    ///
    /// 仍然走 ApplyStartupMode 而不是直接写注册表：互斥（Run 值与提权任务只能存一个）和 UAC 被拒回滚
    /// 的规矩都在那里，这条路只是不去触发需要 UAC 的那个分支。已经有提权任务的机器（用户自己开过）
    /// 也不会被这一句改回去——ElevatedStartupBox 由 LoadToggles 按系统真实状态填好，这里不动它。</summary>
    internal void EnableStartupOnFirstRun()
    {
        StartupBox.IsChecked = true;
        _ = ApplyStartupMode();     // _loaded 还没置位时 OnToggle 会早退，这一句保证动作照样发生
    }

    private void OnLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        string? lang = LangBox.SelectedIndex <= 0 ? null : Languages.All[LangBox.SelectedIndex - 1].Code;
        if (lang == App.Cfg.Language) return;
        App.Cfg.Language = lang;
        SettingsStore.Save(SettingsStore.DefaultPath, App.Cfg);
        if (ConfirmDialog.Confirm(this, Strings.Get("Settings_RestartPrompt")))
            App.RestartSelf();                                 // §12 语言切换经重启生效
    }
}
