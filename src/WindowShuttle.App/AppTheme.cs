using System.Windows;
using System.Windows.Media;

namespace WindowShuttle.App;

/// <summary>The app's own semantic palette (bespoke — not WPF-UI's token system), theme-aware.
/// WPF-UI's ApplicationThemeManager restyles its own controls (FluentWindow chrome, ui:Button,
/// CheckBox, ComboBox) automatically; this bridges the same theme flip to the hand-drawn parts (map
/// cards, keycaps, badges, the drag-to-move chips) that no library control renders — App.xaml.cs calls
/// <see cref="Apply"/> from <c>ApplicationThemeManager.Changed</c>.
///
/// Deliberately plain static fields, not a ResourceDictionary/DynamicResource: MainWindowBoundsTests
/// and friends construct a real MainWindow with no System.Windows.Application running at all (see
/// MainWindowWpfCollection's doc comment — the test host is a bare xunit executable, not WindowShuttle.exe,
/// so App.xaml never runs and Application.Current is null there). A DynamicResource lookup against
/// Application.Resources would silently no-op in that context; these fields don't depend on it. Every
/// brush is Frozen — a frozen Freezable has no dispatcher-thread affinity, so any STA thread (including
/// the Gate-serialized test threads) can read the same instance safely, same discipline the
/// pre-migration static brushes already used.
///
/// The two colour values that carry meaning (Beam = primary monitor, Live = cursor's monitor) are
/// tuned per theme so both clear WCAG AA at every size they're actually drawn at. They are not derived
/// from WPF-UI's accent color: the accent is user-chosen and already means focus/selection; reusing it
/// here would collapse two distinct signals into one.
///
/// The neutral ramp is solved, not picked: every value is the quietest one that still clears the floor
/// its role owes (4.5:1 for Faint/Dim/Ink as text, 3:1 for Edge as a card boundary), on a single cool hue.
///
/// There are <b>three</b> backdrops to solve against, not two. Room and Panel are the obvious pair;
/// the third is <see cref="HoverBrush"/> — the action row's real fill while the pointer or the
/// keyboard focus is on it. In light mode it is the <i>worst</i> one (#E1E2E7). Missing it is how the light ramp shipped with Faint at
/// 3.91:1 and Edge at 2.62:1 on the one surface that appears exactly while someone is reading that row —
/// both cleared Room and Panel, so every existing assertion stayed green. Solving it that way is what caught the defect the previous
/// hand-picked ramp shipped with — Edge sat at 1.24:1 on Panel and Panel itself only 1.10:1 over Room,
/// so in dark mode neither the fill nor the outline marked where one monitor card ended and the next
/// began. Panel now clears Room by 1.26:1 and Edge clears both by 3:1. Change any of these and re-solve
/// rather than nudging by eye: the floors are the design, the hex values are only its output.
///
/// <b>HoverBrush is opaque and given per theme</b>, and that is load-bearing rather than tidiness.
/// It used to be <c>Tint(Ink, 0x14)</c> — one semi-transparent brush for both themes — and the row
/// swapped its opaque Panel fill for it, so the base underneath fell from Panel to Room. Light got
/// away with it because Panel (#FFF) and Room (#F1F2F6) are 14 apart; dark did not, because there
/// the gap is 21 and the 7.8% ink laid on top could not repay it. Measured: hovering a row in dark
/// mode moved it from (32,36,47) to (28,29,34) — <b>1.085:1</b>, i.e. all but invisible, and in the
/// wrong direction (the card sank toward the page instead of standing out from it).
///
/// Both themes now recess the row on hover, which is what light already did (its hover sits below
/// even Room). Dark's value is solved to match light's separation: 1.291:1 vs 1.294:1. Faint on the
/// hovered row improves as a side effect (4.90 → 5.82) because the surface got darker, not lighter —
/// every "make dark hover brighter" variant broke Faint's 4.5:1 floor instead, which is why the fix
/// went this way round.</summary>
public static class AppTheme
{
    public static bool IsDark { get; private set; } = true;

    public static SolidColorBrush Room { get; private set; } = null!;
    public static SolidColorBrush Panel { get; private set; } = null!;
    public static SolidColorBrush Edge { get; private set; } = null!;
    /// <summary>Decorative hairlines only (the rule trailing a section heading). Split from
    /// <see cref="Edge"/> because they answer to different floors: Edge outlines the monitor cards, and
    /// a card's outline is the only thing marking where one screen's region ends and the next begins, so
    /// it owes WCAG 1.4.11's 3:1 — Panel sits just 1.26:1 above Room and cannot carry that boundary on
    /// fill alone. A rule beside a heading identifies nothing and is exempt. Conflating the two is what
    /// left the old palette with a 1.24:1 card border: a value quiet enough for the decoration, applied
    /// where it had to be load-bearing, made the cards read as one continuous surface.</summary>
    public static SolidColorBrush Rule { get; private set; } = null!;
    public static SolidColorBrush Ink { get; private set; } = null!;
    public static SolidColorBrush Dim { get; private set; } = null!;
    public static SolidColorBrush Faint { get; private set; } = null!;
    public static SolidColorBrush Beam { get; private set; } = null!;
    public static SolidColorBrush Live { get; private set; } = null!;
    public static Color BeamColor { get; private set; }
    public static Color LiveColor { get; private set; }

    public static LinearGradientBrush KeycapBg { get; private set; } = null!;
    public static SolidColorBrush KeycapBorder { get; private set; } = null!;
    public static SolidColorBrush HoverBrush { get; private set; } = null!;
    public static SolidColorBrush ChipBg { get; private set; } = null!;
    public static SolidColorBrush ChipBorder { get; private set; } = null!;

    public static SolidColorBrush ConflictBg { get; private set; } = null!;
    public static SolidColorBrush ConflictBorder { get; private set; } = null!;
    public static SolidColorBrush ConflictFg { get; private set; } = null!;

    public static SolidColorBrush PrimaryEdgeBrush { get; private set; } = null!;
    public static SolidColorBrush CapHoverEdgeBrush { get; private set; } = null!;
    public static SolidColorBrush CapHoverKeycapBorder { get; private set; } = null!;

    // NotificationOverlay 自己的三档严重程度（提示/可操作/错误）——跟 Beam/Live 那套"主屏/光标屏"
    // 是两种含义，所以即便复用同一支色相也单独起名。
    //
    // NotifyNeutral 是唯一需要单独解的一支，不能复用 Edge：通知卡是半透明的，描边要对的是卡片跟
    // 任意背景合成之后的颜色，而深色主题下 Edge 跟那个合成色亮度太接近——把卡片不透明度一路提到
    // 99.6% 也只到 2.99:1，救不回来（实测，见 NotificationContrastTests）。这两个值按最坏背景
    // （纯白／纯黑）留了余量解出来，改之前先跑那条测试。旧值 #68688F/#8E8EB1 是换配色时漏掉的
    // 紫灰残留，实测 2.46:1 / 2.77:1，两个主题都不达标。
    public static SolidColorBrush NotifyError { get; private set; } = null!;
    public static SolidColorBrush NotifyWarn { get; private set; } = null!;
    public static SolidColorBrush NotifyNeutral { get; private set; } = null!;

    /// <summary>Fires after every field above has been reassigned to the new theme's values. Callers
    /// rebuild whatever they drew in code (map cards, keycaps) and reassign colors set at construction
    /// time (Window.Background/Foreground) — nothing here pushes into open windows on its own.</summary>
    public static event Action? Changed;

    // Populates a sensible default (dark, matching the pre-migration look) before App.OnStartup ever
    // runs — the test host never calls Apply() at all, and needs every field non-null regardless.
    static AppTheme() => Apply(dark: true);

    public static void Apply(bool dark)
    {
        IsDark = dark;
        if (dark)
        {
            Room = F("#0B0C11"); Panel = F("#20242F"); Edge = F("#646D8A"); Rule = F("#2C3141");
            Ink = F("#EAEBF0"); Dim = F("#A9AEBE"); Faint = F("#848A9E");
            // 悬停把这一行压到页面之下。往亮里走过不了 Faint 的 4.5:1（实测每一档都掉线），
            // 而往暗里走两个指标同时变好——这条不是配色偏好，是被两条底线夹出来的唯一方向。
            HoverBrush = F("#07080C");
            Beam = F("#FFD166"); Live = F("#6FC3F7");
            KeycapBg = Frozen(new LinearGradientBrush(Color.FromRgb(0x30, 0x35, 0x44), Color.FromRgb(0x25, 0x29, 0x36), 90));
            KeycapBorder = F("#454C63");
            var conflictBase = Color.FromRgb(0xE5, 0x48, 0x4D);
            ConflictBorder = Frozen(new SolidColorBrush(conflictBase));
            ConflictFg = F("#FF9CA0");
            ConflictBg = Tint(conflictBase, 0x1A);
            NotifyNeutral = F("#777F98");
        }
        else
        {
            // Edge/Faint 按 HoverBrush 合成出来的那块底（#E1E2E7）解，不是按 Room——见类注释里
            // 「三种背景」那段。旧值 #838BA5/#676E86 只按 Room 和 Panel 解，在悬停行上分别是
            // 2.62:1 和 3.91:1，双双掉到底线以下。
            Room = F("#F1F2F6"); Panel = F("#FFFFFF"); Edge = F("#798098"); Rule = F("#D2D6E0");
            Ink = F("#242834"); Dim = F("#4B5165"); Faint = F("#5E647A");
            // 就是 Tint(Ink,0x14) 叠在 Room 上一直以来的实效值，写死下来只是让它不再依赖合成——
            // 上面 Edge/Faint 那两个数正是按这块底解出来的，动它就要重解。
            HoverBrush = F("#E1E2E7");
            Beam = F("#805A00"); Live = F("#09659F");
            KeycapBg = Frozen(new LinearGradientBrush(Color.FromRgb(0xEC, 0xEE, 0xF4), Color.FromRgb(0xDE, 0xE1, 0xEA), 90));
            KeycapBorder = F("#B0B6C7");
            var conflictBase = Color.FromRgb(0xB3, 0x26, 0x1E);
            ConflictBorder = Frozen(new SolidColorBrush(conflictBase));
            ConflictFg = ConflictBorder;   // light mode's red already clears 4.5:1 as text, no second shade needed
            ConflictBg = Tint(conflictBase, 0x22);
            NotifyNeutral = F("#7D849D");
        }

        BeamColor = Beam.Color;
        LiveColor = Live.Color;
        ChipBg = F("#38000000");   // recessed-chip scrim: stays black in both themes, same convention as an inset shadow
        ChipBorder = Tint(Ink, 0x14);
        PrimaryEdgeBrush = LerpBrush(Edge.Color, Beam.Color, 0.42);
        CapHoverEdgeBrush = LerpBrush(Edge.Color, Live.Color, 0.5);
        CapHoverKeycapBorder = LerpBrush(KeycapBorder.Color, Live.Color, 0.5);
        NotifyError = ConflictBorder;
        NotifyWarn = Beam;

        // WPF-UI 自己那些控件（复选框、下拉框、焦点框）画"选中/激活"用的是**强调色**，而这个应用
        // 从没设过它——于是它跟随系统，在作者机器上是灰的：实测勾选框填 #3E3E3E、对钩 #6A6A6A，
        // 两者对比 1.6:1，"开着"和"关着"的差别几乎只剩"有没有一块灰"。设置条那排开关是这扇窗里
        // 唯一的状态显示，看不出状态就等于没显示。
        //
        // 接的是应用自己的 Live，不是另挑一个蓝：键帽描边、光标所在屏的卡片和徽章用的就是它。
        // 于是"这一项是开的"和界面别处的"这个是当前的"变成同一个颜色，而不是两套互不相干的高亮。
        //
        // 必须排在主题色赋值之后：这个方法是从 ApplicationThemeManager.Changed 里被调的，那时
        // 主题已经换完，而 Live 的取值本身就分明暗两套。
        // **不接管系统强调色。** 这里曾经有一句
        //     ApplicationAccentColorManager.Apply(Live.Color, …)
        // 用意是设置栏那排复选框：勾上和没勾上原来只差一块灰（对比 1.6:1），看不出状态。
        //
        // 但它是全局的，副作用远超那排复选框——实测隔离过：**整扇窗被围了一圈 2px 的亮蓝**。
        // 采样：接管时窗口边缘 #6FC3F7（正是 Live），停掉之后变成 #3F3F3F，而那正是这台机器注册表里的
        // 系统强调色（且 DWM\ColorPrevalence=1，用户开着「标题栏和窗口边框显示强调色」）。也就是说：
        // 本该由用户的系统设置决定的那圈边框，被一个搬窗小工具改掉了。
        //
        // 两条理由都指向"还回去"：
        //   · 越权。边框跟不跟随强调色、跟随哪个颜色，是用户在「个性化 → 颜色」里定的。
        //   · 稀释含义。Live 在这个应用里专指"光标现在在这块屏上"——地图高亮边、键帽描边、屏幕徽章
        //     都是它。整扇窗也涂成它，等于宣布"整个应用都是当前的"，那就什么都没说。
        //
        // 原来那个理由不成立：复选框的状态并不只靠颜色——勾上时有对勾字形，没勾是空框，两者在任何
        // 强调色下都分得清（改完逐主题渲染确认过）。用颜色**加强**状态可以，但不能是判断状态的唯一
        // 依据，那本来就是可访问性的基本要求。
        //
        // Window.BorderBrush 改不动这一圈（试过，纹丝不动），DWMWA_BORDER_COLOR 也改不动——它归
        // WPF-UI 的窗口模板管，而那个模板读的就是强调色。所以唯一干净的办法是不去接管它。

        Changed?.Invoke();
    }

    /// <summary>同一个颜色，换一档不透明度。八处调用点原本各自把 <c>Color.FromArgb(a, c.R, c.G, c.B)</c>
    /// 摊成通道算术写一遍，读的时候要先在脑子里把它还原成"这是那个颜色的半透明版"——通知卡和
    /// 「屏幕序号」浮层就是这么各自实现了一遍"Panel 加透明度"，字面上看不出是同一回事。
    ///
    /// 返回冻结画刷：冻结后没有派发线程亲和性，也省掉变更通知，跟这个类里其余画刷同一套纪律。</summary>
    public static SolidColorBrush Tint(Color c, byte alpha)
        => Frozen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));

    /// <inheritdoc cref="Tint(Color, byte)"/>
    public static SolidColorBrush Tint(SolidColorBrush b, byte alpha) => Tint(b.Color, alpha);

    private static SolidColorBrush F(string hex) => Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!));
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static SolidColorBrush LerpBrush(Color a, Color b, double t) => Frozen(new SolidColorBrush(Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t))));
}
