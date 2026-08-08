using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.App;

/// <summary>DecideNotification 的判断结果：要说什么、可不可点、算不算错误（决定卡片描边颜色）。</summary>
public readonly record struct NotifyRequest(string Text, bool Actionable, bool IsError);

public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private nint _menuReferent;   // 见构造函数里 TrayRightMouseDown 那段
    private readonly CommandRouter _router;

    /// <summary>菜单里全部用户动作的顺序 + resx 表头键。出厂就绑了快捷键的 Swap/Undo 排在最前——
    /// 不然会被剩下那些未绑定的挤没存在感（§tray-order）；其余维持原来的相对顺序。拆成一个
    /// 独立的 static readonly 表（而不是散在构造函数里的 AddAction 调用序列）纯粹是为了能测：
    /// "前两个是不是 Swap/Undo""动作有没有一个不漏"都能直接断言表本身，不用真的拉一个
    /// TaskbarIcon（那个东西连着 pack://，脱离一个跑起来的 Application 未必立得住）。</summary>
    /// <summary>跟主窗口的动作卡同一个顺序（<see cref="MainWindow.ActionKeys"/>，按用得多少排），
    /// 少了按方向送屏、多了屏幕序号：
    ///   · **按方向送屏进不来**——菜单点一下带不出方向，它只有手势这一个入口；
    ///   · **屏幕序号只在这里和地图栏有**——它不是搬窗动作，不占键位，所以排在最后。
    /// 两处顺序必须一致：同一份清单在两个界面上排得不一样，用户会以为是两套功能。</summary>
    public static readonly (string ResxKey, WindowShuttleAction Action)[] ActionMenuSpec =
    [
        ("Action_Undo", WindowShuttleAction.Undo),
        ("Action_SwapTop", WindowShuttleAction.SwapTop),
        ("Action_Swap", WindowShuttleAction.Swap),
        ("Action_ToPrimary", WindowShuttleAction.ToPrimary),
        ("Action_Gather", WindowShuttleAction.Gather),
        ("Action_ToNext", WindowShuttleAction.ToNext),
        ("Action_Identify", WindowShuttleAction.Identify),
    ];

    public TrayService(CommandRouter router, Action openMain, Action exit)
    {
        _router = router;
        // 托盘菜单拿不到 App.OnStartup 那次 RTL 元数据覆盖：那次覆盖的是 typeof(Window)，而这个
        // ContextMenu 是 new 出来的、从不进任何 Window 的可视树，继承链上根本没有 Window。
        // 不显式设的话，阿拉伯语下整个应用都镜像了，唯独这一处还是从左向右——而它恰恰是主窗口
        // 关掉之后唯一还够得着的界面。
        var menu = new ContextMenu
        {
            FlowDirection = Strings.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };
        AddItem(menu, Strings.Get("Tray_Open"), openMain);
        menu.Items.Add(new Separator());
        foreach (var (key, action) in ActionMenuSpec)
        {
            Action act = action == WindowShuttleAction.Identify
                ? () => OverlayWindow.ShowAll(shutdownAfter: false)
                : () => _router.Execute(action, null,
                        referent: _menuReferent == 0 ? null : _menuReferent);
            AddItem(menu, Strings.Get(key), act, action);
        }
        menu.Items.Add(new Separator());
        // 「以管理员身份重启」：管理员程序的窗口，普通权限下**两条路都堵着**——鼠标手势根本收不到
        // （UIPI 不把高完整性窗口的输入交给我们的钩子，还顺手把钩子摘掉，见 MouseChordService 的
        // 看门狗），而就算靠快捷键绕过手势，SetWindowPos 照样会被拒（§9）。所以给这类用户加一个
        // 快捷键并不能解决问题，唯一真正管用的是整个进程提权。
        //
        // 原来这条路只能从"搬运失败"那张气泡点进去——非得先撞一次墙才找得到出口。放进托盘菜单，
        // 它就成了一个随时可选的选项。已经提权时不显示：那时候点它没有任何意义。
        if (!IsElevated())
            AddItem(menu, Strings.Get("Tray_RestartElevated"), OfferElevation);
        AddItem(menu, Strings.Get("Tray_Exit"), exit);
        // 菜单每次弹出前才刷新快捷键文案——绑定可以在主窗口设置页随时改，托盘菜单只在这一刻
        // 现读 App.Cfg.Hotkeys，不需要另开一条"热键变了通知托盘"的事件线路。
        menu.Opened += (_, _) => RefreshGestureText(menu);

        _icon = new TaskbarIcon
        {
            IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/windowshuttle.ico")),
            ToolTipText = Strings.Get("App_Name"),
            ContextMenu = menu,
        };
        _icon.TrayLeftMouseUp += (_, _) => openMain();
        // 菜单弹出**之前**把前台窗口记下来。晚一步都不行：菜单一显示，前台就变成它自己（H.NotifyIcon
        // 要靠这个才能让菜单在点击别处时正常消失），那时候再读只会读到菜单。
        //
        // 这一格修的是一个此前一直废着的功能：托盘菜单里那几个单窗动作（送去主屏 / 下一块屏）以前
        // 认光标，而点托盘菜单时光标必然停在任务栏上——任务栏在 WindowFilter 的类名黑名单里，
        // 于是十次有九次得到「鼠标下没有窗口」；剩下那次更糟，矩形命中判定会穿过任务栏打到背后
        // 某扇最大化窗口上，搬走一扇用户完全没在看的窗。
        _icon.TrayRightMouseDown += (_, _) => _menuReferent = WindowProbe.GetForeground();

        // 这一句不能省，否则整个应用没有托盘图标。H.NotifyIcon 把 shell 图标的创建挂在 Loaded 上，
        // 而这个 TaskbarIcon 是 new 出来的、从不进任何可视树，Loaded 一辈子不触发。旧库
        // Hardcodet.NotifyIcon.Wpf 是在构造函数里就创建的，所以换库之前这里什么都不用做——
        // 换库的人最容易在这里翻车，症状是应用照常跑、快捷键照常用，就是托盘里什么都没有。
        //
        // 参数不能省：官方签名是 ForceCreate(bool enablesEfficiencyMode = true)，裸调用会顺手把进程
        // 标成 Windows 效率模式（EcoQoS 降频）。这个工具常驻后台但靠热键和鼠标钩子抢响应，被降频
        // 正好打在它最要紧的地方。
        if (!_icon.IsCreated) _icon.ForceCreate(enablesEfficiencyMode: false);

        router.Executed += OnExecuted;
    }

    /// <summary>托盘图标到底建起来没有。给 --smoke 断言用——这是唯一能在自动化里区分
    /// 「有图标」和「进程活着但托盘是空的」的信号。</summary>
    internal bool IconCreated => _icon.IsCreated;

    /// <summary>Tag 挂的是这一项对应的 WindowShuttleAction（Open/Exit 没有，Tag 留 null）——
    /// RefreshGestureText 靠它决定该显示谁的快捷键，不用另建一张 MenuItem→Action 的映射表。</summary>
    private void AddItem(ContextMenu m, string header, Action act, WindowShuttleAction? gestureOf = null)
    {
        var item = new MenuItem { Header = header, Tag = gestureOf };
        item.Click += (_, _) => act();
        m.Items.Add(item);
    }

    // 菜单本身是"这个动作绑了什么"的提醒（§tray-order）：InputGestureText 显示当前实际绑定的
    // 快捷键，未绑定的留空——不是"未绑定"三个字那种占位文案，一个空白列比一句话更不抢眼，
    // 跟主窗口设置页的虚线占位是两种场合两种分寸，那边要邀请你去录，这里只是顺手一提。
    private static void RefreshGestureText(ContextMenu menu)
    {
        foreach (var obj in menu.Items)
            if (obj is MenuItem { Tag: WindowShuttleAction a } item)
                item.InputGestureText = RegisteredGesture(a.ToString());
    }

    /// <summary>这个动作**真的能按**的那个快捷键；注册失败或没绑定都返回空。
    ///
    /// 读 App.HotkeyStates（RegisterHotKey 的实际结果）而不是 Cfg.Hotkeys（用户想绑什么）：
    /// 组合被别的程序占了时 RegisterHotKey 会失败，设置页会把那一行标成冲突红——而托盘菜单原来
    /// 照着配置字典画，仍然理直气壮地写着 `Ctrl+Alt+1`。用户按下去没反应，去查唯一还够得着的界面
    /// （主窗口关掉之后就只剩托盘菜单），它还在坚持那个键是绑好的。手改配置写了个解析不了的值也
    /// 一样：Apply 同样报 Conflict。
    ///
    /// 空白而不是"（冲突）"之类的字：菜单这一列是顺手一提，不是诊断面板；要看究竟得去设置页，
    /// 那里有完整的三态。宁可少说，也不能说错。</summary>
    private static string RegisteredGesture(string actionKey)
        => App.HotkeyStates.GetValueOrDefault(actionKey, HotkeyState.Unbound) == HotkeyState.Registered
            ? App.Cfg.Hotkeys.GetValueOrDefault(actionKey, "")
            : "";

    private void OnExecuted(ExecResult r, WindowShuttleAction action)
    {
        var decided = DecideNotification(r, action);
        if (ChordDebug.Enabled)
            ChordDebug.Log($"exec   action={action} exit={r.ExitCode} noop={r.Plan.NoOp} " +
                           $"moved={r.Commit?.Moved} denied={r.Commit?.AccessDenied} failed={r.Commit?.OtherFailed} " +
                           $"=> notify={(decided is null ? "(silent)" : decided.Value.Text)}");
        if (decided is not { } n) return;
        NotificationOverlay.Show(n.Text, n.Actionable, n.IsError, n.Actionable ? OfferElevation : null);
    }

    /// <summary>只在用户能采取行动、或不然会一头雾水的四种情形下发一条通知（§notify-rules）：
    /// 需要提权才能搬的窗口（可点击，提供"点它重启"这条出路）；非权限原因的失败（§own-decision，
    /// 用户已批准：这类失败也不能静默吞掉，哪怕没有一个"点它就能修"的出路）；一次 no-op，带上
    /// 原因，让"什么都没发生"这件事本身有个交代；错误——它本身也走 NoOp（NoOpReason.Error，见
    /// CommandRouter 的 catch 兜底），所以跟 no-op 共用同一个分支，不用单独判。除此之外一律静默：
    /// 一次成功的搬运，哪怕伴随 SkippedFullscreen/SkippedHung/Corrected 这些计数，窗口动了就是
    /// 反馈本身——这些计数只进 CLI 输出（App.Describe）给脚本看，绝不在这里显形，也绝不出声、
    /// 绝不进操作中心。AccessDenied 和 OtherFailed 同时非零时只报 AccessDenied——它是唯一带得出路
    /// 的那条，气泡一次只能显示一条，不排队堆叠（见 NotificationOverlay.Show）。
    ///
    /// 拆成一个纯函数（不摸 NotificationOverlay/TaskbarIcon，只吃 ExecResult+WindowShuttleAction 吐
    /// NotifyRequest?）纯粹是为了能测：四种该响的情形、以及"成功搬运/只有跳过计数"不该响，都能
    /// 直接断言返回值，不用真的拉一扇 WPF 窗口出来。</summary>
    public static NotifyRequest? DecideNotification(ExecResult r, WindowShuttleAction action)
    {
        // 自动救援的 no-op 静默：没有人按键，"没有卡在屏外的窗口"不需要交代——每次分辨率变化都
        // 弹一句"没有可搬运的窗口"才是打扰。它成功时反而必须出声（下面），无人触发的移动要有解释。
        //
        // 但**只**静默 NothingToDo，不能连 Error 一起吞：Error 是 CommandRouter.Guarded 的兜底 catch
        // 造出来的占位值，代表救援真的抛了（典型是 %APPDATA% 写不进去）。原来写成"Rescue 的任何
        // NoOp 都不出声"，于是一次失败的自动救援彻底无声——窗口还在屏外，用户得不到任何交代。
        if (action == WindowShuttleAction.Rescue && r.Plan.NoOp == NoOpReason.NothingToDo) return null;
        // 提权这条**不分入口，一律出声**（含拔屏自动救援）。代码审查提过反对意见：拔个显示器就弹出
        // 一张"要不要以管理员身份重启"，属于用户没请求的提权邀约。这里是明确的产品决定，压过那条：
        // 搬不动就必须说。窗口卡在屏外、应用一声不吭，比多一张可以忽略的卡片糟得多；而且这张卡只是
        // 通知，点了还要再过一道确认框（Elevate_Confirm）才会真的重启，不存在"一不小心就提权了"。
        // 给一条出路也胜过给一句无处使力的死话——所以是 Actionable，不是纯告知。
        if (r.Plan.NoOp is NoOpReason reason)
        {
            string text = reason == NoOpReason.NothingToDo && action == WindowShuttleAction.Undo
                    ? Strings.Get("Toast_NoUndo")
                : Strings.Get($"NoOp_{reason}");
            return new NotifyRequest(text, Actionable: false, IsError: reason == NoOpReason.Error);
        }
        var c = r.Commit!;
        if (c.AccessDenied > 0)                        // §9 不能静默吞
            return new NotifyRequest(Strings.Plural("Toast_AccessDenied", c.AccessDenied), Actionable: true, IsError: false);
        if (c.OtherFailed > 0)                          // §9 非权限原因的失败也不能静默吞
            return new NotifyRequest(Strings.Plural("Toast_OtherFailed", c.OtherFailed), Actionable: false, IsError: true);
        if (action == WindowShuttleAction.Rescue)      // 无人触发的移动必须解释自己
            return new NotifyRequest(Strings.Plural("Toast_Rescued", c.Moved), Actionable: false, IsError: false);
        return null;                                    // 成功搬运（哪怕带跳过/纠偏计数）：静默
    }

    /// <summary>方向手势划向了一个没有屏幕的方向。走跟别的 no-op 同一张卡，措辞不同——
    /// 「没有可搬运的窗口」在这里是假话，窗口好好的，是那边没有屏。</summary>
    public void NotifyNoScreenThatWay()
        => NotificationOverlay.Show(Strings.Get($"NoOp_{NoOpReason.NoScreenThatWay}"),
            actionable: false, isError: false);

    /// <summary>按了方向手势却没划出方向——多半是当成点击用了。把这个动作自己的说明回给他，
    /// 一个会话只说一次。
    ///
    /// 这里跟 <see cref="NotifyGesturesBlocked"/> 的取舍相反，两者的差别是真的：那条讲的是一个
    /// **反复发生的外部状态**（又点进管理员程序了），你需要在它发生的那一刻知道；这条讲的是**你
    /// 自己手滑**，同一句"要划出一个方向"每次手滑都念一遍，是纯粹的絮叨。</summary>
    private bool _taughtStroke;

    public void NotifyStrokeNeedsDirection()
    {
        if (_taughtStroke) return;
        _taughtStroke = true;
        NotificationOverlay.Show(Strings.Get("Action_ToDirection_Desc"), actionable: false, isError: false);
    }

    /// <summary>前台换成了管理员程序——从这一刻起**所有**鼠标手势都收不到事件（不是某一扇窗搬不动，
    /// 见 <see cref="ForegroundWatch"/>）。每次进入这个状态都说，并给出唯一那条出路。
    ///
    /// **不做"一个会话只说一次"**，那是刻意放弃的一版。这条限制是**反复发生**的：早上说过一次，
    /// 下午再点进密码管理器发现手势不灵时，那句话早忘了，而那一刻恰恰最需要它。一句只说一次的话，
    /// 配不上一个反复出现的状态。而它并不会变成打扰——真正防打扰的是 ForegroundWatch 那道停留门槛
    /// （掠过一下不算），加上通知卡本身一次只显示一条、不排队堆叠（见 NotificationOverlay.Show），
    /// 所以最坏情况是同一张卡被刷新，不会摞成一片。
    ///
    /// 自己已经提权就闭嘴：那时手势本来就不受这条限制，还弹一张"点我提权"是彻底的假话。这道判断
    /// 跟 ForegroundWatch 构造函数里那道是重复的（它压根不会挂钩子），留着是因为这两处各自都该
    /// 说得通——这个方法是 public，将来别处调它时不该依赖调用方记得先判。</summary>
    public void NotifyGesturesBlocked()
    {
        if (IsElevated())
        {
            if (ChordDebug.Enabled) ChordDebug.Log("blocked 压下不报：自己已提权");
            return;
        }
        NotificationOverlay.Show(Strings.Get("Toast_GesturesBlocked"),
            actionable: true, isError: false, onClick: OfferElevation);
    }

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private void OfferElevation()
    {
        // 从托盘弹出，没有任何窗口能当它的 owner——ConfirmDialog.Confirm(null, ...) 会退到
        // CenterScreen，而不是假装有个父窗口。
        if (!ConfirmDialog.Confirm(null, Strings.Get("Elevate_Confirm"))) return;
        // 放锁+重启这一整套必须走 App.RestartSelf——它是唯一摸得到 _mutex 的地方（§race）。
        try { App.RestartSelf("runas"); }
        catch (System.ComponentModel.Win32Exception) { /* UAC 拒绝：保持现状 */ }
    }

    public void Dispose()
    {
        _router.Executed -= OnExecuted;
        _icon.Dispose();
    }
}
