using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.App;

public partial class MainWindow
{
    private readonly Dictionary<int, UIElement> _cursorBadges = [];

    // 地图带的高度区间和它占窗口高度的上限。三个数都是量出来的，不是估的：下限 140 是卡片还能同时
    // 容下标号、规格和一行窗口条目的高度，再矮就退化成一条色带，不如不画；32% 这道份额上限是让七张
    // 动作卡在默认窗口里两列四行全部落地、不出滚动条所反推回来的余额。
    //
    // 三个数按 min 依次生效，谁小谁说了算，所以哪个真正起作用取决于场合：
    //   · 一排横向排开的屏——长宽比说了算（本机一排三屏只"想要"约 214），另两个都够不到；
    //   · 上下堆叠的摆法（笔记本挂在外接屏下方）——算出来的带子又高又窄，改由份额或上限截断；
    //   · 320 这道上限只在窗口高过 1000 DIP 时才轮得到它（那之前 32% 一直更小），也就是用户把窗口
    //     拉大或最大化的时候。留着它是为了这种情形：窗口给了空间，堆叠桌面才有地方把带子长开；
    //     一排横向排开的桌面永远够不到它，那种摆法的带子由长宽比封顶，跟这个数无关。
    // 别把这三个数当三种口味挑，它们各管一种情形。
    //
    // 第一版这里写的是 250 / 34%，跑出来动作区只剩一张半卡片——把"地图被动作表饿死"原样翻了个面。
    // 这两块的高度不是各自独立的审美选择，是同一份余额的两头，改任何一个数都得回头验另一头。
    private const double MapMinHeight = 140, MapMaxHeight = 320, MapMaxWindowShare = 0.32;

    /// <summary>让位下限：地图带被动作区挤到最后也不能再矮于此。比 <see cref="MapMinHeight"/> 低——
    /// 那是"正常情况下想要多高"的下限，这是"实在挤不下时还剩多少"的底线。
    ///
    /// 88 是被最矮的那扇窗定死的，不是估的：窗口下限 480（= 1366×768@150% 的工作区高度，见
    /// MinHeightDip 的说明）减去约三百像素的固定开销，正好要地图让到这里，第一张动作卡的描述那行
    /// 才不会被切掉。96 试过，差 8 像素，描述被拦腰截断。再往下地图卡片里的屏号和分辨率就糊了，
    /// 所以这两头之间没有余量可挑——这个数改了就得两边都重新实测。</summary>
    public const double MapFloorWhenCrowded = 88;

    /// <summary>动作卡还没完成布局时的高度兜底（首帧）。真实值一律现量，见 OneActionCardHeight。</summary>
    private const double AssumedActionCardHeight = 70;
    private bool _resizingCanvas;

    // ---------- 布局画布 ----------
    private void OnRefresh(object s, RoutedEventArgs e) => Refresh();

    private void OnIdentify(object s, RoutedEventArgs e) => OverlayWindow.ShowAll(shutdownAfter: false);

    /// <summary>项目仓库地址。ToolTip 直接绑它，省得图标底下还要再写一份同样的字符串。</summary>
    public const string RepoUrl = "https://github.com/rockbenben/WindowShuttle";

    /// <summary>用系统默认浏览器打开仓库。UseShellExecute=true 是必须的：.NET 的 Process.Start
    /// 默认不走 shell，直接喂一个 http URL 会抛 Win32Exception（"系统找不到指定的文件"）。
    ///
    /// 包一层 try：浏览器关联坏掉、或者组策略禁掉了 shell 执行，都不该把整个应用带走——
    /// 打不开一个链接是小事，为它崩掉不是。</summary>
    private void OnOpenRepo(object s, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepoUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception) { /* 打不开就算了 */ }
    }

    /// <summary>GitHub 的猫爪标记，自绘路径。不引字体图标包也不塞位图：这是全应用唯一一处需要
    /// 品牌图形的地方，为它多一个依赖或多一个资源文件都不划算；矢量路径还能跟着主题换色。</summary>
    private static System.Windows.Shapes.Path MakeGitHubGlyph(Brush fill) => new()
    {
        Width = 16, Height = 16, Fill = fill, Stretch = Stretch.Uniform,
        Data = Geometry.Parse(
            "M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49"
            + "-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82"
            + ".72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15"
            + "-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82"
            + ".44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48"
            + " 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z"),
    };

    private void OnCanvasSized(object s, SizeChangedEventArgs e)
    {
        if (!_resizingCanvas) DrawMonitors();
    }

    /// <summary>把画布高度写下去并**当场**把布局跑完，让紧接着的测量（卡片缩放、动作区视口）读到的
    /// 是真值而不是上一帧的。NaN 那一支不能省：Height 初值是 NaN，而 <c>Math.Abs(NaN - want) > 0.5</c>
    /// 恒为 false（NaN 参与的任何比较都是 false），漏掉它高度就一次也赋不上，画布永远停在 MinHeight。</summary>
    private void SetBandHeight(double want)
    {
        if (!double.IsNaN(LayoutCanvas.Height) && Math.Abs(LayoutCanvas.Height - want) <= 0.5) return;
        _resizingCanvas = true;                  // UpdateLayout 会同步回调 OnCanvasSized，挡掉重入
        try
        {
            LayoutCanvas.Height = want;
            LayoutCanvas.UpdateLayout();
        }
        finally { _resizingCanvas = false; }
    }

    /// <summary>一张动作卡连同外边距占多高。现量而不是写死：卡高随字号、语言（描述换行成两行）和
    /// 主题的行高一起变，写死的数字在最需要它的那门语言上正好是错的。</summary>
    private double OneActionCardHeight()
    {
        if (ActionList.Items.Count == 0 || ActionList.Items[0] is not FrameworkElement first
            || first.ActualHeight < 1)
            return AssumedActionCardHeight;
        return first.ActualHeight + first.Margin.Top + first.Margin.Bottom;
    }

    /// <summary>把每块屏换算成「桌面上看起来多大、摆在哪」的坐标系：尺寸按有效像素（物理 ÷ 缩放比），
    /// 位置保留真实的上下错位、横向按物理 Left 顺序紧贴排列。
    ///
    /// 为什么不是纯物理坐标：这台机器上 4K@150% 的屏物理像素比 2560@125% 的宽 50%，可它俩摆在桌上
    /// 一样宽——按物理像素画，地图会把一块屏画成它实际观感的 1.5 倍，标题旁那句"按实际观感大小绘制"
    /// 就成了谎话。为什么横向要紧贴而不用真实 Left：有效尺寸配物理位置必然对不齐（各屏缩放比不同，
    /// 同一段物理距离换算出的有效距离也不同），硬摆会出现凭空的缝隙和重叠。纵向没有这个问题——上下
    /// 错位是各屏各自的偏移量，不需要跟邻居对缝，所以纵向保真、横向保序，两边各取能保住的那一半。
    /// 上一版把纵向也压平成顶对齐，笔记本屏挂在外接屏下方这种最常见的摆法在图上完全看不出来。</summary>
    private static List<(MonitorInfo M, double X, double Y, double W, double H)> EffectiveLayout(
        IReadOnlyList<MonitorInfo> monitors)
    {
        // 必须跟 SwapPlanner.ByPosition 用**同一套**排序（Left 再 Top）——卡片上印的号是
        // PositionOf 给的，画序要是只按 Left，两块 Left 相同的屏（外接在上、笔记本在下这种
        // 最常见的竖排）就会画成 2 | 1，跟自己印在上面的号对不上。
        var ordered = SwapPlanner.ByPosition(monitors);
        double vTop = monitors.Min(m => m.MonitorRect.Top / m.DpiScale);
        var result = new List<(MonitorInfo, double, double, double, double)>();
        double x = 0;
        foreach (var m in ordered)
        {
            double w = m.MonitorRect.Width / m.DpiScale, h = m.MonitorRect.Height / m.DpiScale;
            result.Add((m, x, m.MonitorRect.Top / m.DpiScale - vTop, w, h));
            x += w;
        }
        return result;
    }

    /// <summary>定下地图带该多高，并让布局当场落定。三条约束依次收紧，每一条都是实测踩出来的：
    ///
    /// ① <b>想要多高 = 桌面自己的长宽比。</b>一排三屏得到矮带，2×2 或上下错位的摆法得到高带——地图跟
    ///    真实桌面同形，而不是所有人都摊上同一个固定高度。上一版这里是 Grid 的 <c>*</c>，跟动作表抢
    ///    同一份剩余高度，720px 高的窗口里被压到 103px，卡片上的文字直接被截断。
    ///
    /// ② <b>上限 = 窗口高度的一个百分比。</b>上下错位很大的桌面算出来的带子会很高，窗口一矮就轮到
    ///    动作卡只剩十几个像素。
    ///
    /// ③ <b>让位 = 动作区至少留得下一张卡。</b>②按整扇窗算，管不住这件事——标题栏、两个区标题、
    ///    拖放提示、设置栏加起来约三百像素是**固定**开销，不随窗口变矮而变小。于是窗口一矮，归零的
    ///    永远是排在 DockPanel 填充位、一条下限都没有的动作区：实测 760×480（XAML 里的拖拽下限）下
    ///    动作区可视高度只剩 29px，而一张卡 70px——"动作"标题底下空无一物，整个主功能面消失。
    ///    反过来给动作区设 MinHeight 不行：它是填充元素，MinHeight 超过分到的空间时会照 MinHeight
    ///    画出去、盖在设置栏上，把"看不见"换成"画错位"。只能让地图让位，扣到 MapFloorWhenCrowded 为止。
    ///
    /// <b>③ 为什么写成"解终值"而不是"按差额回扣"</b>——两种错法都踩过：
    ///   · 按差额回扣不幂等：扣完动作区够高了，下一趟重新写回 want、量到的差额变 0，又停回 want，
    ///     实测在 140 和 99 之间反复横跳；
    ///   · 即便解终值，只要中间**多写一次** want，画布高度每趟都会先跳回 140 再落到 112，每次变化
    ///     都在闸释放后补一个 SizeChanged → 回到这里 → 再跳一次，无限自激，实测把 --shots 整个挂死。
    /// 关键性质：地图带和动作区分的是同一份余额，而这份余额跟怎么切无关（available = 带高 + 视口高）。
    /// 所以能拿当前这一帧的切法直接解出终值，一次写到位。
    ///
    /// <b>为什么跑两趟</b>：available 只有在布局落定之后才是真值，而 SetBandHeight 里那次 UpdateLayout
    /// 正是让它落定的那一步——第一趟往往读到上一帧的切法，算出的目标差一档（实测停在带高 110、视口 65，
    /// 而一张卡要 71，描述那行还是被切掉）。两趟就够也必须只有两趟：终值只依赖 available，第二趟必然
    /// 算出同一个值、不再写第三次；写死上限同时也是防自激的保险。
    ///
    /// 那 2px 是布局取整余量：DIP 高度要落到整数设备像素上，非整数缩放比（本机 150%/125%）下会来回
    /// 差一个像素——实测正好卡在"视口 69、卡片 70"，只差 1px 就又是一张卡都露不全。</summary>
    /// <param name="cw">画布可用宽度。</param>
    /// <param name="desktopAspect">桌面自身的宽高比（总宽 ÷ 总高）。</param>
    private void ResolveBandHeight(double cw, double desktopAspect)
    {
        double cap = Math.Clamp(Math.Max(ActualHeight, MinHeight) * MapMaxWindowShare, MapMinHeight, MapMaxHeight);
        double want = Math.Clamp(cw / desktopAspect, MapMinHeight, cap);
        double one = OneActionCardHeight();
        for (int pass = 0; pass < 2; pass++)
        {
            double available = LayoutCanvas.ActualHeight + ActionsScroller.ViewportHeight;
            // 目标是**两**张卡，不是一张。一张只证明"这里有东西"，两张才传达"动作是一张可以往下翻的
            // 列表"——而这正是主功能面要传达的第一件事。实测：1920×1080@150%（最常见的笔记本，启动
            // 尺寸 1120×632）上，只留一张时非中文语言的视口是 133、一张卡 85，第二张从中间被切断；
            // 中文卡只有 70 所以侥幸露得出两张，于是这条缺陷在中文下完全看不见。
            //
            // 第二张只在**地图仍不低于 MapMinHeight** 时才买：地图矮过那条线就开始画不清屏与屏的相对
            // 大小，而它是拖放的落点参照，赔掉它去换一行文字不划算。买不起就退回一张卡的老规矩，
            // 再买不起才压到 MapFloorWhenCrowded。
            //
            // 三支都只依赖 available（＝带高＋视口高，与怎么切无关），所以"解终值"的幂等性不变：
            // 第二趟必然算出同一个值，不会自激。
            double roomy = Math.Min(want, available - one * 2 - 2);
            SetBandHeight(roomy >= MapMinHeight
                ? roomy
                : Math.Max(Math.Min(want, available - one - 2), MapFloorWhenCrowded));
        }
    }

    private List<WindowFacts>? _windowCache;
    private DateTime _windowCacheAt;
    private static readonly TimeSpan WindowCacheTtl = TimeSpan.FromMilliseconds(300);

    /// <summary>地图上那些窗口条目的数据源，带一个很短的缓存。
    ///
    /// 缓存是为拖动窗口边缘那一下加的：DrawMonitors 现在被三个尺寸事件驱动（画布、窗口高度、动作区
    /// 视口），而它每次都要 EnumWindows 枚举整个桌面、逐窗口取标题/类名/DPI/cloaked，再把整块画布
    /// 重建一遍。一次拖拽会抬起几十上百个 SizeChanged，桌面上开着几十扇窗时肉眼可见地卡。
    ///
    /// 300ms 是"拖动过程中不重复枚举，但松手后一眼就能看到新窗口"的折中；点「刷新」和显示器变化
    /// 都会主动作废它（见 Refresh），所以用户要的"现在就重新看一眼"永远是准的。</summary>
    private List<WindowFacts> ProbeWindowsCached()
    {
        if (_windowCache is not null && DateTime.UtcNow - _windowCacheAt < WindowCacheTtl) return _windowCache;
        _windowCacheAt = DateTime.UtcNow;
        // 走 SelectMovable 而不是只按 IsMovable 筛：那才是这个应用对"哪些窗口允许搬"的唯一口径，
        // 它比 IsMovable 多两道闸——无响应窗口、以及开着「跳过全屏」时的全屏应用。
        //
        // 少了这两道，地图会把它们画成可拖的卡片，而拖过去必然落空：落点那条路（RunMoveWindowTo）
        // 会照 SelectMovable 把它们滤掉，用户得到的是一句「没有可搬运的窗口」——指着自己眼前那张
        // 卡片说没有窗口。与其事后解释，不如一开始就别画：画出来的每一张卡片都真的搬得动。
        return _windowCache = SwapPlanner.SelectMovable(
            WindowProbe.GetWindows(), _monitors, App.Cfg.SkipFullscreen).Movable;
    }

    private void DrawMonitors()
    {
        if (!_loaded || _monitors.Count == 0) return;
        double cw = LayoutCanvas.ActualWidth;
        if (cw < 50) return;

        var eff = EffectiveLayout(_monitors);
        double totalW = eff.Max(e => e.X + e.W), totalH = eff.Max(e => e.Y + e.H);

        ResolveBandHeight(cw, totalW / totalH);

        double h = LayoutCanvas.ActualHeight;
        if (h < 20) return;

        LayoutCanvas.Children.Clear();
        _cards.Clear();
        _cursorBadges.Clear();
        _previewGlyph = null;

        double scale = Math.Min(cw / totalW, h / totalH);
        double xPad = (cw - totalW * scale) / 2;              // 整块地图在画布里居中，不左贴边

        var windows = ProbeWindowsCached();
        foreach (var (m, ex, ey, ew, eh) in eff)
        {
            double cardW = ew * scale, cardH = eh * scale;
            var card = BuildCard(m, cardW, cardH, windows.Where(win => SwapPlanner.OwnerIndex(win, _monitors) == m.Index));
            card.Width = cardW;
            card.Height = cardH;
            Canvas.SetLeft(card, xPad + ex * scale);
            Canvas.SetTop(card, ey * scale);
            LayoutCanvas.Children.Add(card);
            _cards[m.Index] = card;
        }
        HighlightCursorMonitor();
        ShowActionPreview(_previewAction);                     // 重画会清空画布，预览态得自己接回来
    }

    /// <summary>卡片内容按自己算出来的尺寸逐级降级，而不是画完让 ClipToBounds 去裁。上一版固定画
    /// 标号+分辨率+缩放比+刷新率，卡片一窄就把"2560 × 1440"截成"2560 × 1"——半截数字比没有数字更糟，
    /// 它看起来像一个真实存在的、错的值。</summary>
    private Border BuildCard(MonitorInfo m, double cardW, double cardH, IEnumerable<WindowFacts> wins)
    {
        var header = new Grid { Margin = new Thickness(10, 7, 8, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 唯一用 Bahnschrift 的地方：屏幕编号。这是 Windows 自带的 DIN 系标牌字，路牌和站台号用的
        // 就是这一类——而屏幕编号恰好就是一个指路用的数字。整个界面只在这一处用它，别处一概不用，
        // 它才保得住"看见这个字形＝这是屏几"的辨识度。ASCII 数字，不存在缺中文字形的问题。
        //
        // 这个数字是**排列位次**（从左到右），不是 Windows 的显示设备编号。全应用寻址都用位次：
        // 「送去第 N 块屏」数的是它、`--to` 收的是它、「屏幕序号」闪的也是它。系统编号跟屏幕摆在
        // 哪儿没关系（这台机器物理上就是 2 | 1 | 3），拿它当门牌号，用户得先在脑子里做一次翻译。
        // 系统编号没有丢，降到下面那行小字里，方便跟 Windows 显示设置对号。
        var idx = new TextBlock
        {
            Text = SwapPlanner.PositionOf(m, _monitors).ToString(),
            FontFamily = SignFont, FontWeight = FontWeights.SemiBold,
            FontSize = cardH >= 96 ? 30 : 24, Foreground = m.IsPrimary ? AppTheme.Beam : AppTheme.Ink,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0),
        };
        Grid.SetColumn(idx, 0);
        header.Children.Add(idx);

        // 两道门槛，不是一道——"显示不全就整条不显示"这条规矩要按**行**来执行，不是按整块。
        //
        // 原来只有一道 200：低于它，分辨率和缩放比一起消失。那是从一个真问题上矫枉过正来的——
        // 门槛曾是 118，而 760 宽的三屏桌面上中间那张卡约 150 宽，渲染出来是 "384…" 和 "150…"，
        // 一截没有意义的数字碎片。可这两行的长度差着一倍：`1920 × 1080` 约 73px，而
        // `150% · 100 Hz · 系统 2` 要 120px 上下。一刀切的代价在**六屏一字排开**时才看清楚：
        // 卡宽约 176，六张卡的规格全没了，只剩编号——而分辨率明明放得下。屏越多越需要分辨哪块是哪块，
        // 偏偏在那时候信息掉得最干净。
        //
        // 168 这个数是量出来的：屏号约 25 + 左右边距 15 + 分辨率 73 = 113，加余量。它**高于**当年
        // 出问题的那个 150，所以 760 宽的三屏桌面上那两张窄卡照旧整条隐藏，不是把旧缺陷放回来。
        //
        // 主屏那张卡要求更宽（220），因为它常驻一个徽章，而**徽章的宽度随语言差得很远**：中文「主屏」
        // 两个字，德语「Hauptmonitor」十二个。第一版只写了一个 168，德语六屏混合 DPI 下当场把主屏卡的
        // 规格挤成了 `2560 × 1…`——正是这道闸本来要防的那种碎片，换了个语言复发。非主屏的「光标」
        // 徽章短得多（且只在光标停在那块屏时才现身），不必为它抬门槛。
        double specFloor = m.IsPrimary ? 220 : 168;
        if (cardW >= specFloor)
        {
            var meta = new StackPanel { Margin = new Thickness(9, 2, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock
            {
                Text = $"{m.MonitorRect.Width} × {m.MonitorRect.Height}",
                FontFamily = MonoFonts, FontSize = 11, Foreground = AppTheme.Ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            // 第二行比第一行长一倍，所以它自己还要再过一道宽度闸——放不下就只掉这一行，
            // 分辨率留着。
            if (cardH >= 88 && cardW >= 200)
                meta.Children.Add(new TextBlock
                {
                    Text = $"{m.DpiScale:P0} · {m.RefreshHz} Hz · {Strings.Lf("Map_SystemIndex", m.Index)}",
                    FontFamily = MonoFonts, FontSize = 10, Foreground = AppTheme.Faint,
                    Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                });
            Grid.SetColumn(meta, 1);
            header.Children.Add(meta);
        }

        // 徽章位：主屏固定显示"主屏"；非主屏的位置留给光标徽章，由 HighlightCursorMonitor 按需切换
        // 可见性——不能同时给两块屏挂徽章又都常驻，那样余光里就分不清哪个信号是活的。
        var badge = m.IsPrimary
            ? MakeBadge(Strings.Get("Badge_Primary"), AppTheme.Beam)
            : MakeBadge(Strings.Get("Badge_Cursor"), AppTheme.Live);
        if (!m.IsPrimary)
        {
            badge.Visibility = Visibility.Collapsed;
            _cursorBadges[m.Index] = badge;
        }
        Grid.SetColumn(badge, 2);
        header.Children.Add(badge);

        var chips = new WrapPanel();
        foreach (var win in wins) chips.Children.Add(BuildChip(win));
        var chipsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = chips, Padding = new Thickness(9, 0, 7, 7),
        };

        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(chipsScroll);

        var card = new Border
        {
            Background = AppTheme.Panel, BorderBrush = m.IsPrimary ? AppTheme.PrimaryEdgeBrush : AppTheme.Edge,
            BorderThickness = new Thickness(m.IsPrimary ? 1.5 : 1), CornerRadius = new CornerRadius(7),
            ClipToBounds = true, Tag = m.Index, AllowDrop = true, Child = body,
        };
        card.Drop += OnCardDrop;
        // Border has no automation peer of its own by default; an AutomationId lets an external,
        // DPI-aware UIA harness find each card and read its real screen bounds for a layout check
        // (see docs/manual-checks.md) without adding any test-only UI or behaviour.
        AutomationProperties.SetAutomationId(card, $"MonitorCard_{m.Index}");
        return card;
    }

    private static Border MakeBadge(string text, SolidColorBrush color)
    {
        return new Border
        {
            Background = AppTheme.Tint(color, 0x24),
            // 0xB3 而不是更淡的 alpha：低 alpha 的染色边框混到 Panel 底色里会退回背景，两个主题下
            // 实测都到不了 3:1 的非文本底线。
            BorderBrush = AppTheme.Tint(color, 0xB3),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(99),
            Padding = new Thickness(7, 2, 7, 2), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = color },
        };
    }

    private Border BuildChip(WindowFacts w)
    {
        var dot = new Ellipse
        {
            Width = 5, Height = 5, Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = w.ShowState == ShowState.Maximized ? AppTheme.Beam : AppTheme.Faint,
        };
        var text = new TextBlock
        {
            Text = w.Title.Length > 40 ? w.Title[..40] + "…" : w.Title,
            FontSize = 11, Foreground = AppTheme.Dim, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var chip = new Border
        {
            Background = AppTheme.ChipBg, BorderBrush = AppTheme.ChipBorder,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), MaxWidth = 220,
            Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand,
            Opacity = w.ShowState == ShowState.Minimized ? 0.5 : 1.0, Tag = w.Hwnd,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } },
        };
        chip.MouseMove += OnWindowItemDrag;
        return chip;
    }

    private void OnWindowItemDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var chip = (Border)sender;
        DragDrop.DoDragDrop(chip, new DataObject("windowshuttle-hwnd", (nint)chip.Tag), DragDropEffects.Move);
    }

    private void OnCardDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("windowshuttle-hwnd")) return;
        var hwnd = (nint)e.Data.GetData("windowshuttle-hwnd")!;
        // 带上自己的句柄：搬过去的窗该露出来，但不能盖住用户此刻还拖着的这张地图（MovePlan.KeepBelow）。
        App.Router.MoveWindowTo(hwnd, (int)((Border)sender).Tag,
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Refresh();
    }

    private void HighlightCursorMonitor()
    {
        if (_cards.Count == 0) return;
        var cur = SwapPlanner.MonitorAt(WindowProbe.GetCursor(), _monitors);
        _cursorMonitorIndex = cur.Index;
        foreach (var (idx, card) in _cards)
        {
            var m = _monitors.First(mm => mm.Index == idx);
            bool isCursor = idx == cur.Index;
            card.BorderBrush = isCursor ? AppTheme.Live : m.IsPrimary ? AppTheme.PrimaryEdgeBrush : AppTheme.Edge;
            card.BorderThickness = new Thickness(isCursor ? 2 : m.IsPrimary ? 1.5 : 1);
            if (_cursorBadges.TryGetValue(idx, out var badge))
                badge.Visibility = isCursor ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
