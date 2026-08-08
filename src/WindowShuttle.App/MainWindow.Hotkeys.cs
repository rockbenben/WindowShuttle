using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;

namespace WindowShuttle.App;

public partial class MainWindow
{
    /// <summary>动作卡的排列顺序：**按用得多少排**，不按枚举定义顺序。
    ///
    /// 原来的顺序是围绕"整屏互换是主打"排的，于是最常用的按方向送屏排在第 5，而它上面两个
    /// （送去主屏、送去下一块屏）出厂根本没绑定——一屏列表里，最该先看见的那个反而在中间。
    ///
    /// 这个顺序还顺带把出厂手势表编码进了两列布局（默认宽度就是两列，见下面的分列门槛，行优先填充）：
    ///
    ///     按方向送屏   撤销        ← Ctrl+右键 / Ctrl+中键
    ///     对调最前窗口 整屏互换     ← Alt+右键  / Alt+中键
    ///     送去主屏     全部收拢     ← 出厂未绑；而且它俩本来就是同一件事的单扇版和整屏版
    ///     送去下一块屏
    ///
    /// 左列全是"对一扇窗做事"，右列全是"一次动一堆"，跟手势那两个轴是同一件事。所以
    /// 对调最前窗口排在整屏互换前面，不是因为它更常用（多半不是），而是因为拆开这一行会把
    /// 这张对照表打散——两者同在一行，谁都没被埋掉，代价比收益小。
    ///
    /// 出厂没绑定的三个沉到底部：它们不是次要功能，只是"你可以自己加"的那一类。</summary>
    internal static readonly string[] ActionKeys =
        ["ToDirection", "Undo", "SwapTop", "Swap",
         "ToPrimary", "Gather", "ToNext"];

    private readonly Dictionary<string, Border> _caps = [];

    // 分列门槛，量的是动作区自己的可用宽度，不是窗口宽度。多切一列是为了修"卡片太宽"这个毛病：
    // 卡宽到 780 DIP 时，动作名在最左、键位在最右，中间几百像素空白要横扫；切一列让每张回到
    // 500 上下这个"名字和键位互相够得着"的宽度。
    //
    // 踩过两次，都记在这儿：
    // ① 第一版按**窗口**宽度写成 940，而默认窗口恰好 912——门槛卡在默认尺寸上方一点点，默认打开
    //    就是单列。门槛要量真正被切分的那个容器。
    // ② 第二版（760 / 1160）只考虑了"卡片不能太宽"这一头，没考虑另一头。两个数都是解出来的，不是挑的：一张动作卡窄于约 420 DIP，最长的动作名就会被 TextTrimming
    // 切成省略号。实测（--shots 逐宽扫俄语，最长的两条是「Поменять экраны местами」和
    // 「Поменять передние окна」）：卡宽 384 截断、426 完整，边界落在 400–420 之间。
    //
    // 于是门槛 = 列数 × 420 + 列间距：两列 2×420+14 ≈ 854 → 870；三列 3×420+28 ≈ 1288 → 1300。
    //
    // 旧值 760 / 1160 是按"能塞下就切"定的，比这条边界低一大截，于是**两个档位各有一段会截断**：
    // 两列在动作区 760–854（卡 380–420）、三列在 1160–1288（卡 387–420）。三列那一段尤其阴险，
    // 因为切成三列反而让每张卡比两列时**更窄**（1232 处：两列 609 vs 三列 391），用户把窗口拉宽
    // 反而看到名字被截断。1920×1080@150% 最大化正好落在那一段里，是最常见的笔记本配置之一。
    // internal 而不是 private：MainWindowCompactLayoutTests 要拿它们**算出**探测宽度。写死数字的
    // 测试在这里等于没有——门槛调低时探针不会跟着挪进危险区，测试照样绿（实测过一次）。
    internal const double TwoColumnMinWidth = 870, ThreeColumnMinWidth = 1300;

    /// <summary>动作卡的列数，由 <see cref="OnActionsResized"/> 按可用宽度写，ItemsPanel 里的
    /// UniformGrid 绑它。做成依赖属性而不是在 SizeChanged 里顺着视觉树去摸那个 UniformGrid：
    /// ItemsPanel 是模板展开出来的，第一次 SizeChanged 触发时它在不在树上取决于布局时序，摸不到
    /// 就只能等下一次 SizeChanged——而尺寸不再变的话根本没有下一次，实测就是这样卡在单列的。
    /// 绑定没有这个时序问题：值先写在这里，模板什么时候展开、什么时候取。</summary>
    public static readonly DependencyProperty ActionColumnsProperty = DependencyProperty.Register(
        nameof(ActionColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(1));

    public int ActionColumns
    {
        get => (int)GetValue(ActionColumnsProperty);
        set => SetValue(ActionColumnsProperty, value);
    }

    // ---------- 动作卡 + 快捷键/鼠标手势录制 ----------
    private void BuildActionRows()
    {
        foreach (var key in ActionKeys)
        {
            var cap = BuildCap(key);
            var mouseCap = BuildMouseCap(key);

            var name = new TextBlock
            {
                Text = Strings.Get($"Action_{ShortKey(key)}"),
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var desc = new TextBlock
            {
                Text = Strings.Get($"Action_{ShortKey(key)}_Desc"),
                FontSize = 11.5, Foreground = AppTheme.Faint,
                Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
            };

            // 名字和键位同一行，描述在下一行通栏。上一版是通栏表格：动作名钉在最左、键位钉在最右，
            // 1140px 宽的窗口里两者隔着约 700px 的空白，读一行要横扫整个窗口。键位属于这个动作，
            // 就该挨着它的名字。
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(name, 0); Grid.SetColumn(cap, 1); Grid.SetColumn(mouseCap, 2);
            Grid.SetRow(desc, 1); Grid.SetColumnSpan(desc, 3);
            // 键位那两个 Border 必须留在 grid.Children 的直接一层——MainWindowAccessibilityTests
            // 靠 row.Child 转 Grid、再在它的直接子元素里按 Tag 找键位区。
            grid.Children.Add(name); grid.Children.Add(cap); grid.Children.Add(mouseCap); grid.Children.Add(desc);

            var row = new Border
            {
                Padding = new Thickness(12, 8, 10, 9), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), Child = grid, Margin = new Thickness(0, 0, 7, 7),
            };
            row.MouseEnter += (_, _) => { UpdateRowChrome(row); ShowActionPreview(key); };
            row.MouseLeave += (_, _) => { UpdateRowChrome(row); ShowActionPreview(null); };
            row.GotFocus += (_, _) => { UpdateRowChrome(row); ShowActionPreview(key); };
            row.LostFocus += (_, _) => { UpdateRowChrome(row); ShowActionPreview(null); };
            UpdateRowChrome(row);
            ActionList.Items.Add(row);
        }
        ActionList.SizeChanged += OnActionsResized;
    }

    // 卡片常态就有底色和描边——上一版是全透明、只在悬停时才显形，七条动作在页面上是一片没有边界的
    // 文字流，看不出"一条动作是一个整体、它带着自己的两个键位"。
    private static void UpdateRowChrome(Border row)
    {
        bool lit = row.IsMouseOver || row.IsKeyboardFocusWithin;
        row.Background = lit ? AppTheme.HoverBrush : AppTheme.Panel;
        row.BorderBrush = row.IsKeyboardFocusWithin ? AppTheme.Live : lit ? AppTheme.Edge : AppTheme.Rule;
    }

    private void OnActionsResized(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        ActionColumns = e.NewSize.Width >= ThreeColumnMinWidth ? 3
            : e.NewSize.Width >= TwoColumnMinWidth ? 2 : 1;
    }

    private static string ShortKey(string actionKey)
        => actionKey == "FixStraddling" ? "Fix" : actionKey;   // resx 键名映射

    internal Border BuildCap(string actionKey)
    {
        // 绑不了快捷键的那一个（「按方向送屏」，理由见 SettingsStore.AllowsHotkey）：给一格**不可聚焦、
        // 不可录**的占位，而不是让它长得跟别的空位一样。长成空位就是在邀请用户去录，而录完按下去只会
        // 得到一张错误卡——那正是这次要修的缺陷。不画虚线圈也是这个意思：虚线圈在这个界面里是"这里
        // 是空的、可以录"的固定词汇，用在这儿会说反话。
        if (!SettingsStore.AllowsHotkey(actionKey))
        {
            var na = NewCapShell(actionKey, new Thickness(10, 0, 0, 0));
            na.Focusable = false;
            na.Cursor = Cursors.Arrow;
            na.ToolTip = Strings.Get("Hotkey_GestureOnly");
            na.Child = new TextBlock
            {
                Text = "—", FontSize = 11, Foreground = AppTheme.Faint,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 4, 9, 4),
            };
            _caps[actionKey] = na;
            return na;
        }
        var cap = NewCapShell(actionKey, new Thickness(10, 0, 0, 0));
        cap.PreviewKeyDown += OnHotkeyRecord;
        cap.PreviewMouseLeftButtonDown += (_, _) => cap.Focus();
        // §hotkey-visibility 悬停态："这里能改"的提示，但录制态（聚焦）更响，悬停不能盖过它。
        cap.MouseEnter += (_, _) => { if (!cap.IsKeyboardFocusWithin) RenderCap(cap, actionKey); };
        cap.MouseLeave += (_, _) => { if (!cap.IsKeyboardFocusWithin) RenderCap(cap, actionKey); };
        cap.GotFocus += (_, _) => EnterRecording(cap);
        cap.LostFocus += (_, _) => ExitRecording(cap, actionKey);
        _caps[actionKey] = cap;
        RenderCap(cap, actionKey);
        return cap;
    }

    // 三态渲染（+ 悬停变体）。未绑定不是错误状态——默认就有五个动作是空的，全标红会让首次启动看起来
    // 像坏了：未绑定画成虚线空位，冲突画成红色键帽（仍显示实际按了什么组合，不是笼统一个"冲突"字）。
    private void RenderCap(Border cap, string actionKey)
    {
        string raw = App.Cfg.Hotkeys.GetValueOrDefault(actionKey, "");
        var state = App.HotkeyStates.GetValueOrDefault(actionKey, HotkeyState.Unbound);
        cap.ToolTip = state == HotkeyState.Conflict ? Strings.Get("Hotkey_Conflict") : null;   // §9 不静默失效
        bool hover = cap.IsMouseOver;                  // §hotkey-visibility：悬停只提亮键位区自己的描边/文字

        if (string.IsNullOrEmpty(raw) || state == HotkeyState.Unbound)
        {
            cap.Child = MakeUnboundPill(hover);
            return;
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        // §a11y 冲突态之前只靠红色跟正常键帽区分——色觉障碍用户在余光里（不是凑近看 ToolTip 的
        // 时候）分不出"绑好了"和"被占用了"。加一个不掉色也认得出的字形标记，跟 ToolTip/颜色
        // 三条线一起说同一件事，不是单靠颜色这一条。
        if (state == HotkeyState.Conflict)
            panel.Children.Add(new TextBlock
            {
                Text = "!", FontWeight = FontWeights.Bold, FontSize = 11.5,
                Foreground = AppTheme.ConflictFg, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
            });
        foreach (var token in raw.Split('+'))
            panel.Children.Add(MakeKeycap(token, state == HotkeyState.Conflict, hover));
        cap.Child = panel;
    }

    // ---------- 以下几件由键盘和鼠标两个录制器共用。抽出来不是为了省这几十行，是为了让两个录制态
    // 在视觉上**无法**漂移：它们是同一个交互的两种输入源，用户看到的"正在录制"必须是同一个东西。
    // 真正不同的部分（三态 vs 两态渲染、键盘 vs 鼠标的输入解析）各自留在自己文件里。 ----------

    /// <summary>键位区的空壳：常态全透明，录制态才由 <see cref="EnterRecordingChrome"/> 点亮。
    /// 固定 BorderThickness 是为了聚焦/失焦时外形不跳；Background=Transparent（不是 null）让整块
    /// 区域都能吃到点击和悬停，而不只是键帽/虚线圈那几笔描边。</summary>
    private static Border NewCapShell(string actionKey, Thickness margin = default) => new()
    {
        Tag = actionKey, Focusable = true, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
        Margin = margin, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        BorderBrush = Brushes.Transparent, Background = Brushes.Transparent,
    };

    /// <summary>录制态外观。文案键由调用方给（键盘/鼠标提示不同），其余一切必须一致。</summary>
    private static void EnterRecordingChrome(Border cap, string promptKey)
    {
        cap.ToolTip = null;
        cap.BorderBrush = AppTheme.Live;
        cap.Effect = new DropShadowEffect { Color = AppTheme.LiveColor, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
        cap.Child = new TextBlock
        {
            // 录制态文案可能是中文（"按下组合键…"），绝不能喂 Bahnschrift/Cascadia——留给默认正文字体继承。
            Text = Strings.Get(promptKey), FontSize = 11, Foreground = AppTheme.Ink,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 4, 9, 4),
        };
    }

    /// <summary>只复原外壳，不负责重画内容——调用方紧接着调自己那套 Render。</summary>
    private static void ExitRecordingChrome(Border cap)
    {
        cap.Effect = null;
        cap.BorderBrush = Brushes.Transparent;
    }

    /// <summary>空位。<paramref name="content"/> 给 null 就用默认的"未绑定 · 点击录制"文案（键盘那一侧），
    /// 鼠标那一侧传一个字形进来——但虚线圈两边必须是同一个，它是这个应用里"这里是空的、可以录"的
    /// 固定词汇，鼠标位换了写法就等于要用户再学一遍。</summary>
    private static Grid MakeUnboundPill(bool hover, UIElement? content = null)
    {
        // Border 不支持虚线边框，用一个带 StrokeDashArray 的 Rectangle 垫底代替。
        var rect = new Rectangle
        {
            Stroke = hover ? AppTheme.CapHoverEdgeBrush : AppTheme.Edge, StrokeThickness = 1,
            StrokeDashArray = [2, 2], RadiusX = 4, RadiusY = 4,
        };
        content ??= new TextBlock
        {
            // 未绑定文案可能是中文，绝不能喂 Bahnschrift——留给 TextBlock 继承窗口的正文字体。
            Text = Strings.Get("Hotkey_Unbound"), FontSize = 11, Foreground = hover ? AppTheme.Dim : AppTheme.Faint,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 4, 9, 4),
        };
        return new Grid { Children = { rect, content } };
    }

    /// <summary>鼠标记号，画出来而不是找一个字体码位：MDL2/emoji 在缺字形的机器上会掉成豆腐块，
    /// 而这个记号是鼠标手势位唯一的标识，掉了就没了。两个尺寸——动作卡上的小号，设置条那句警告
    /// 前面的同款，好让那句话跟它说的那个位置对得上。
    ///
    /// 三笔缺一不可：圆角外框 + 横向的按键分界 + 上半段的左右分界。第一版只画了外框和一条竖线，
    /// 实测在这个尺寸下读出来是个"0"——一个圆角矩形加一条竖线，本来就既像鼠标也像数字零，只有把
    /// 上半段横切出来、露出"两个按键"这个特征，它才只能是鼠标。</summary>
    private static Canvas MakeMouseGlyph(Brush stroke, double height = 14)
    {
        double w = Math.Round(height * 0.68), r = w * 0.42, split = Math.Round(height * 0.4);
        var body = new Rectangle
        {
            Width = w, Height = height, RadiusX = r, RadiusY = r,
            Stroke = stroke, StrokeThickness = 1.1,
        };
        var buttons = new Line { X1 = 0.5, Y1 = split, X2 = w - 0.5, Y2 = split, Stroke = stroke, StrokeThickness = 1.1 };
        var divide = new Line { X1 = w / 2, Y1 = 1, X2 = w / 2, Y2 = split, Stroke = stroke, StrokeThickness = 1.1 };
        return new Canvas { Width = w, Height = height, Children = { body, buttons, divide } };
    }

    private static Border MakeKeycap(string token, bool conflict, bool hover) => new()
    {
        Background = conflict ? AppTheme.ConflictBg : AppTheme.KeycapBg,
        BorderBrush = conflict ? AppTheme.ConflictBorder : hover ? AppTheme.CapHoverKeycapBorder : AppTheme.KeycapBorder,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), MinWidth = 22,
        Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 3, 0),
        Child = new TextBlock
        {
            // 键帽词条永远是 Ctrl/Alt/Shift/Win/字母/数字/F 键——HotkeyGesture.ToString() 保证纯 ASCII，
            // 这里用 Bahnschrift 才不会碰上中文缺字形的问题。
            Text = token, FontFamily = SignFont, FontSize = 11.5,
            Foreground = conflict ? AppTheme.ConflictFg : AppTheme.Ink, HorizontalAlignment = HorizontalAlignment.Center,
        },
    };

    // §hotkey-visibility 录制态：聚焦就得看得出来在等按键，否则用户按了组合键却毫无反馈，
    // 完全不知道刚才发生了什么。实边框 + DropShadowEffect 的柔光，跟未绑定的虚线圈、已绑定的键帽
    // 都不一样，三态之外单独一个"活着"的状态。
    private void EnterRecording(Border cap) => EnterRecordingChrome(cap, "Hotkey_Recording");

    // 失焦即退出录制态，不管是 Tab 走的、点别处走的，还是 Esc 取消——一律回落到三态渲染，
    // 绑定本身在这里绝不会被改动（改没改绑定由 OnHotkeyRecord 决定，这里只管界面复原）。
    private void ExitRecording(Border cap, string actionKey)
    {
        ExitRecordingChrome(cap);
        RenderCap(cap, actionKey);
    }

    private void OnHotkeyRecord(object sender, KeyEventArgs e)
    {
        var cap = (Border)sender;
        var actionKey = (string)cap.Tag!;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Tab / Shift+Tab 必须放行，其余一律吞掉。
        //
        // 这是一个真实的键盘陷阱（WCAG 2.1.2）：这个处理器挂的是 **PreviewKeyDown**，而 WPF 的 Tab
        // 导航发生在之后的冒泡 KeyDown 上——原来第一句就无条件 e.Handled = true，等于把导航整个吃掉。
        // 于是键盘焦点一旦落进任意一个键位区，就再也 Tab 不出去；唯一的出路是 Esc，而界面从没说过
        // 有这条路（录制提示只写着"按下组合键…"）。WCAG 允许非标准的退出键，前提是**告知用户**，
        // 而这里没有告知。
        //
        // Tab 本来也永远录不成绑定：下面的 keyName 过滤只收 F1–F24 / A–Z / 0–9，Tab 走到那儿就返回了
        // ——只是那时 Handled 已经设过，导航已经死了。
        //
        // 鼠标那一侧的 OnMouseChordKeyDown 从一开始就是对的：它只给自己消费的键（Esc、Delete）设
        // Handled。两个录制器共用外观（见 NewCapShell 上面那段），键处理却漂移了，这条修回来。
        if (key is Key.Tab) return;

        e.Handled = true;

        // Esc 取消录制：不碰绑定，只把焦点交出去——界面复原交给 LostFocus -> ExitRecording。
        // 单叫 Keyboard.ClearFocus() 不够：GotFocus/LostFocus 跟的是 FocusManager 的"逻辑焦点"，
        // 不是键盘焦点本身——纯清键盘焦点会让 IsKeyboardFocusWithin 变 false，但 LostFocus 事件
        // 不一定跟着触发，录制态的边框/文案会卡住不退。逻辑焦点也要一起清掉。
        if (key == Key.Escape)
        {
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(cap), null);
            Keyboard.ClearFocus();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        // 一个全局热键会从所有应用手里夺走这个组合键；能绑上就必须能释放回去，否则唯一的退路是
        // 手改 settings.json。Delete/Backspace 清空绑定——跟录制一样立即落盘、重新注册、刷新三态。
        if (key is Key.Delete or Key.Back)
        {
            App.Cfg.Hotkeys[actionKey] = "";
            SettingsStore.Save(SettingsStore.DefaultPath, App.Cfg);
            ((App)Application.Current).ReapplyHotkeys();
            RefreshAllCaps();
            return;
        }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        string keyName = key >= Key.F1 && key <= Key.F24 ? key.ToString()
            : key >= Key.A && key <= Key.Z ? key.ToString()
            : key >= Key.D0 && key <= Key.D9 ? key.ToString()[1..]
            : "";
        if (keyName == "") return;
        parts.Add(keyName);

        var gesture = HotkeyGesture.TryParse(string.Join("+", parts));
        if (gesture is null) return;                          // 无修饰键等非法组合

        App.Cfg.Hotkeys[actionKey] = gesture.ToString();
        SettingsStore.Save(SettingsStore.DefaultPath, App.Cfg);
        ((App)Application.Current).ReapplyHotkeys();
        // ReapplyHotkeys 返回全部动作的新三态——这次改动可能把另一行从 Registered 撞成 Conflict，
        // 只刷被编辑的那一行会让那一行停在旧状态，直到重启才会显形。
        RefreshAllCaps();
    }

    private void RefreshAllCaps()
    {
        // 跳过不可录的那一格：它的内容是 BuildCap 一次性画好的占位，RenderCap 会把它重画成
        // 普通的"未绑定"空位——那正是这里要避免的样子。
        foreach (var (key, cap) in _caps)
            if (SettingsStore.AllowsHotkey(key)) RenderCap(cap, key);
    }
}
