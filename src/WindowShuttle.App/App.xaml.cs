using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.App;

public partial class App : Application
{
    public const string SinkTitle = "WindowShuttle.CommandSink";
    private const int WM_COPYDATA = 0x004A;

    public static Settings Cfg { get; internal set; } = null!;
    public static CommandRouter Router { get; } = new();
    public static nint SinkHwnd { get; private set; }

    /// <summary>OnStartup 一路走到底、真的进了托盘常驻模式才为 true。自查、CLI 一次性执行、
    /// 投递给驻留实例后退出这几条路径全都提前 return，于是保持 false——见 App.Crash.cs 里
    /// DispatcherUnhandledException 的注释：那些路径上"救回来"等于让进程永远挂住。</summary>
    private static bool _resident;

    private static Mutex? _mutex;
    private HwndSource? _sink;
    private MainWindow? _main;
    private HotkeyService? _hotkeys;
    private MouseChordService? _mouseChords;
    private TrayService? _tray;
    private ForegroundWatch? _foreground;

    /// <summary>上一次真的触发鼠标手势的时刻。只服务于"管理员程序占前台"那张提示卡的判定：
    /// 那条信息只在"你正在用手势"的语境下有意义，见下面 BlockedByElevated 的接线处。</summary>
    private DateTime _lastGesture = DateTime.MinValue;

    /// <summary>"最近用过手势"的时间窗。三分钟是"你还在这段工作流里"的量级——比它长就变成翻旧账
    /// （早上划过一次，下午点进密码管理器还要被提醒一遍），比它短又会漏掉正常的"划一下、去别处看看、
    /// 再回来划"的节奏。</summary>
    private static readonly TimeSpan GestureRecency = TimeSpan.FromMinutes(3);
    public static Dictionary<string, HotkeyState> HotkeyStates { get; internal set; } = [];
    public Dictionary<string, HotkeyState> ReapplyHotkeys() => HotkeyStates = _hotkeys!.Apply(Cfg);
    public void ReapplyMouseChords() => _mouseChords!.Apply(Cfg);

    /// <summary>录制鼠标手势期间，让钩子把按下截给录制界面（见 MouseChordService.BeginCapture）。
    /// 驻留模式之外（自查、测试里直接 new 出来的 MainWindow）没有 _mouseChords，静默跳过。</summary>
    public void BeginChordCapture(nint hwnd) => _mouseChords?.BeginCapture(hwnd);
    public void EndChordCapture() => _mouseChords?.EndCapture(Cfg);


    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);


    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookCrashHandlers();                           // 第一件事：之后每一步抛的异常都要有地方记
        // 首次运行的判据要在 Load 之前取：Load 不写盘，但后面首启初始化会写一份出来当"来过了"的戳。
        bool firstRun = !File.Exists(SettingsStore.DefaultPath);
        Cfg = SettingsStore.Load(SettingsStore.DefaultPath);
        Strings.ApplyCulture(Cfg.Language);            // 必须在建任何窗口之前（§12）
        // RTL **不能**走 FlowDirectionProperty.OverrideMetadata(typeof(Window), ...)。
        //
        // 那种写法到处都能查到，但它有个致命的次序前提：WPF 自己的 System.Windows.Window 静态构造
        // 函数也要为 typeof(Window) 覆盖一次元数据，而同一个类型只允许覆盖一次。我们在这里抢先调，
        // Window 的静态构造函数随后就抛 "PropertyMetadata is already registered for type 'Window'"，
        // 而且它是从类型初始化器里抛的——整个进程从此碰不得任何 Window/Popup/ContextMenu。
        // 实测把语言设成 ar 之后，连 `list` 和 `--smoke` 都以 127 退出，应用完全打不开。
        //
        // 所以镜像逐窗口设（Strings.Flow），谁建窗口谁负责。托盘菜单也一样——它压根不在任何窗口的
        // 可视树里，本来也继承不到。--shots 从一开始就是这么做的，这也是它没能替我发现这个缺陷的原因。

        // round4 Part 1: follow the system light/dark theme. ApplicationThemeManager.Apply swaps the
        // ui:ThemesDictionary merged in App.xaml, which restyles every WPF-UI control (and the plain
        // CheckBox/ComboBox this app still uses — WPF-UI restyles those too) via their own
        // DynamicResource bindings, no per-window code needed. AppTheme.Apply mirrors the same flip
        // into this app's bespoke palette (map cards, keycaps, badges) — subscribe first so the
        // initial ApplySystemTheme call below populates it immediately, before MainWindow ever exists.
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed +=
            (theme, _) => AppTheme.Apply(theme == Wpf.Ui.Appearance.ApplicationTheme.Dark);
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();

        // --tray：只有开机自启那条注册表项会带它（见 StartupRegistration）。手动双击 exe 的人
        // 期待的是"看见这个程序"，而不是它悄悄躲进托盘——那读起来就是"打开了却自己最小化"。
        // 开机自启则相反：谁也不想每次开机被一扇窗糊脸。用一个参数把这两种意图分开。
        // 先剥掉它再交给 CliParser：它不是命令，留着会被当成乱参数打用法退出码 3。
        bool startHidden = e.Args.Contains("--tray");
        var args = e.Args.Where(a => a != "--tray").ToArray();
        if (args.Length > 0) AttachConsole(-1);        // WinExe 默认没控制台（§2.3）

        // 自查开关必须排在单实例互斥体之前：托盘里正在用的实例照常工作，检查进程自己开自己关。
        // 排在之后的话，第二个实例会走 IPC 分流把命令投递给驻留实例，自查就永远跑不起来。
        if (args.Contains("--smoke")) { RunSmoke(); return; }
        if (args.Contains("--shots")) { RunShots(args); return; }

        var cmd = args.Length > 0 ? CliParser.Parse(args) : null;
        if (args.Length > 0 && cmd is null)            // 乱参数：打用法，退出码 3
        {
            Console.Error.WriteLine(CliParser.Usage);
            Shutdown(3);
            return;
        }

        // §own-decision：CliParser 不校验 --to（它没有屏幕列表）；这里是唯一有实时屏幕列表的地方。
        // 校验放在 IPC 分流之前——不管有没有驻留实例，同一条命令的退出码必须一致，
        // 且只有本进程自己的控制台能把出错信息打给调用脚本看（WM_COPYDATA 只能回传一个整数）。
        if (cmd is { ToMonitor: int target })          // swap/to-next 的 --to（CliParser 只对这两个放行）
        {
            var monitors = MonitorProbe.GetMonitors();
            // --to 收的是**排列位次**（从左到右，1 起），跟界面上和 `list` 的 position 一致，
            // 不是 Windows 的显示设备编号——后者跟屏幕摆在哪儿无关，两套并存必然有人踩错。
            if (SwapPlanner.AtPosition(monitors, target) is null)
            {
                var valid = string.Join(", ", Enumerable.Range(1, monitors.Count));
                Console.Error.WriteLine($"error: no monitor at position {target}; valid positions: {valid}");
                Shutdown(3);
                return;
            }
        }

        // ① list：只读，永远本进程直出，不做 IPC（§2.3）
        if (cmd?.Action == WindowShuttleAction.List)
        {
            Console.WriteLine(CommandRouter.ListJson());
            Shutdown(0);
            return;
        }

        // ② 驻留实例已在运行：投递（有命令）或唤起主窗口（无命令）后退出
        _mutex = new Mutex(true, @"Local\WindowShuttle.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            var sink = FindWindowW(null, SinkTitle);
            if (sink != 0)
            {
                // 带 --tray 的那次启动**什么都不要转发**：它的意图正是"别打扰"。原来这里把
                // --tray 剥掉之后转发裸的 "show"，于是开机自启项在已有驻留实例时反而把主窗口
                // 弹了出来——正是这个参数存在的目的的反面。
                if (startHidden && args.Length == 0) { Shutdown(0); return; }
                Shutdown(SendCommand(sink, args.Length > 0 ? string.Join(' ', args) : "show"));
                return;
            }
            // 驻留标记在但窗口没了（正在退出的竞态）：一次性兜底继续走 ③
        }

        // ③ 有命令、无驻留：一次性执行后退出，不进托盘（§2.3）
        if (cmd is not null)
        {
            try
            {
                if (cmd.Action == WindowShuttleAction.Identify)
                {
                    OverlayWindow.ShowAll();            // Task 13；1.4s 后自行 Shutdown(0)
                    return;
                }
                var r = Router.Execute(cmd.Action, cmd.ToMonitor);
                Console.WriteLine(Describe(r));
                Shutdown(r.ExitCode);
            }
            catch (Exception ex)
            {
                // BCL 异常消息按 CurrentUICulture 生成；CLI 输出恒英文（§12），读消息前先切回不变文化。
                CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                Console.Error.WriteLine($"error: {ex.Message}");
                Shutdown(3);
            }
            return;
        }

        // ④ 驻留模式：托盘常驻，不自动开主窗口
        // 自启项的内容体检，放在这里而不是等用户去动复选框：升级和"把 exe 挪个地方"这两件事都不会
        // 经过设置页，而它们都会让那一行失效或行为倒退（见 RepairRunValue）。一次注册表读取，
        // 不存在就立刻返回，不会替没开自启的人开自启。
        StartupRegistration.RepairRunValue();
        CreateSink();
        _hotkeys = new HotkeyService(SinkHwnd, _sink!);
        _hotkeys.Pressed += a => RunBound(a, fromMouseGesture: false);
        HotkeyStates = _hotkeys.Apply(Cfg);
        _mouseChords = new MouseChordService(Dispatcher);
        _mouseChords.Triggered += (a, x, y) =>
            { _lastGesture = DateTime.UtcNow; RunBound(a, fromMouseGesture: true, at: new PointPx(x, y)); };
        _mouseChords.Stroked += (sx, sy, dx, dy) => { _lastGesture = DateTime.UtcNow; RunStroke(sx, sy, dx, dy); };
        _mouseChords.Captured += (b, m) => _main?.OnChordCaptured(b, m);
        _mouseChords.Apply(Cfg);
        _tray = new TrayService(Router, ShowMain, () => Shutdown(0));
        // 管理员程序占前台时手势整个哑掉，而且毫无迹象可循——主动说一句，见 ForegroundWatch 类注释。
        _foreground = new ForegroundWatch();
        _foreground.BlockedByElevated += () =>
        {
            if (_mouseChords?.HookInstalled != true) return;      // 一个手势都没绑，这条限制跟他无关
            // **必须是"你正在用手势"，不能是"前台变了"。** 这一条是实测撞出来的：只判前台的那一版，
            // 用户什么都没做、只是点进密码管理器停了一会儿，就收到一张"要不要以管理员身份重启"——
            // 他压根没打算搬窗口，那张卡是纯粹的骚扰，而且看起来像程序坏了。
            //
            // 这条信息只在一个时刻有用：你刚才还在用手势，现在进了这个死区、再划就没反应了。
            // 所以门槛是"最近用过手势"。从没用过的人一辈子看不到这张卡；早上用过、下午才点进
            // 管理员程序的人也不会被翻旧账。
            if (DateTime.UtcNow - _lastGesture > GestureRecency) return;
            _tray?.NotifyGesturesBlocked();
        };
        HookDisplayChange();
        _resident = true;                              // 到这里才算真的驻留起来了，见字段注释
        if (!startHidden) ShowMain();                  // 手动启动就把窗口摆出来；开机自启走 --tray 不打扰

        // ── 首次运行：开机自启出厂就是勾上的，而且直接尝试提权那一档 ─────────────────────────
        // 这是明确的产品决定：这个工具是常驻型的（不自启等于每次开机都少一半功能），而在这类桌面上
        // （密码管理器/待办/播放器提权跑是常态）**不提权的实例本来就是残的**——提权程序占前台时手势
        // 整体失灵、它们的窗口也搬不动，用户看到的是"时好时坏"。所以首启就把话挑明：请求一次 UAC 建
        // 免打扰的提权自启任务；用户在 UAC 上点「否」，就退回普通自启（HKCU\Run），一样能用，只是
        // 保留上面那两条限制。只在首启问这一次，之后的改动都在设置条里，绝不反复纠缠。
        //
        // 放在 ShowMain 之后：主窗口先出来，UAC 对话框才有上下文——黑屏里凭空弹一个提权请求，
        // 任何谨慎的人都该点否。--tray 启动到不了这儿的首启分支（自启项存在说明早已不是首启）。
        // 注册这件事本身交给主窗口那个复选框去做（见 EnableStartupOnFirstRun）：那里是互斥与 UAC
        // 回滚的唯一实现，界面也才不会跟系统状态脱节。首启**只开普通自启、不请求提权**——理由写在
        // 那个方法上。窗口没起来（手工跑 --tray 的全新安装）就整个跳过，连"来过了"的戳都不盖。
        if (firstRun && _main is not null)
        {
            _main.EnableStartupOnFirstRun();
            SettingsStore.Save(SettingsStore.DefaultPath, Cfg);     // 盖"来过了"的戳，下次启动不再是首启
        }
    }

    private System.Windows.Threading.DispatcherTimer? _rescueDebounce;

    /// <summary>拔屏救援：显示器增减/换分辨率时驱动会连发好几次 DisplaySettingsChanged（逐屏重配），
    /// 2 秒无后续才真的跑一次 Rescue。SystemEvents 不保证在 UI 线程回调，先 BeginInvoke 回来再摸
    /// 计时器。只在驻留模式挂——一次性 CLI 进程活不到显示器变化那一刻。</summary>
    private void HookDisplayChange()
    {
        _rescueDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _rescueDebounce.Tick += (_, _) =>
        {
            _rescueDebounce!.Stop();
            Router.Execute(WindowShuttleAction.Rescue, null);
            // 主窗口开着的话，它那张地图还画着刚拔掉的那块屏——它只在 Loaded/显示/点刷新时重新探测。
            // 往一张已经不存在的屏卡上拖窗口会静默无效（MoveWindowTo 找不到那个 Index）。
            _main?.RefreshMonitors();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            // 地图刷新跟救援是两件事，别共用这道闸：计时器的 Tick 里既跑救援也调 RefreshMonitors，
            // 原来在这里直接 return，等于用户一关掉"拔屏自动救援"，主窗口那张地图也不再跟着
            // 显示器增减更新了——图上还画着已经拔掉的那块屏，往它上面拖窗口静默无效。
            _main?.RefreshMonitors();
            if (!Cfg.RescueOnDisplayChange) return;
            _rescueDebounce!.Stop();
            _rescueDebounce.Start();
        });

    /// <summary>键盘快捷键和鼠标手势共用的分发。两者绑定的动作集合完全相同，所以分流规则也必须相同。
    ///
    /// 两个入口只准共用这一个函数，差别只允许是传进来的参数。这条规矩是买来的：它们曾经各写一份
    /// lambda，鼠标那份漏了 Identify 的分流（Identify 不是可规划动作，SwapPlanner.Plan 对它直接抛，
    /// 被守卫接成退出码 3 的 Error），于是把它绑到手势上按一下只得到一张红色错误卡，走快捷键却正常。
    /// Identify 现在已经不可绑定（入口是地图栏的按钮和托盘），但"两个入口共用一处分发"这条留着。
    ///
    /// <paramref name="fromMouseGesture"/> 就是那唯一的差别：手已经在鼠标上时不做光标跟随，
    /// 见 CommandRouter.Execute 的参数说明。</summary>
    /// <summary>方向手势落地：把「往哪儿划了多远」换算成一块具体的屏，然后走 ToNext 的
    /// TargetPosition —— 那条路已经把"光标下那扇窗 → 指定屏"整套做完了（命中判定、抬到最前、
    /// 撤销栈、光标跟随），这里不该再有第二份。
    ///
    /// 门槛 40px。低于它的**不是取消，是不知道要划**——纯点击的位移只有两三像素，而真想取消的人
    /// 松手时早就划出去了（那时方向已经成立，动作照做）。所以"位移不够"这件事实际上只对应一种人：
    /// 他按了这个组合，以为点一下就行。对这种人保持沉默，他只会得出"这个功能是坏的"。
    ///
    /// 所以这里回一句该怎么用——而且直接借这个动作自己的描述（Action_ToDirection_Desc，18 种语言
    /// 现成的），不另写一条只在出错时才见得到的文案：能说明白它怎么用的那句话，本来就已经写好了。</summary>
    private static void RunStroke(int sx, int sy, int dx, int dy)
    {
        if (Math.Abs(dx) < MouseChordService.MinStroke && Math.Abs(dy) < MouseChordService.MinStroke)
        {
            ((App)Current)._tray?.NotifyStrokeNeedsDirection();
            return;
        }

        // 一切都锚在按下点：从哪块屏出发、搬哪扇窗，都按用户按下去的那一刻算。
        // 划到别的屏上再松手是很自然的动作（尤其是往那块屏的方向划），拿松手点去解释，
        // 搬走的会是目标屏上那扇毫不相干的窗。
        var press = new PointPx(sx, sy);
        var monitors = MonitorProbe.GetMonitors();
        var from = SwapPlanner.MonitorAt(press, monitors);
        var target = SwapPlanner.MonitorToward(from, dx, dy, monitors, wrap: true);
        if (target is null)
        {
            ((App)Current)._tray?.NotifyNoScreenThatWay();
            return;
        }

        // 四个方向都走自己的 SetWindowPos 路径。曾经左右两个方向改走系统的 Win+Shift+←/→（图它能
        // 保住贴边分屏、或许还能搬提权窗口），一天之内又删掉了，因为两个卖点一个都不成立：
        //   · 提权窗口——链条断在手势那一步之前：它占前台时我们的低级钩子一个事件都收不到，手势根本
        //     不会发生；它不在前台时，把它切到前台的那一刻我们的合成按键又被 UIPI 挡住（都实测过）。
        //   · 剩下的"保分屏"不值它的代价：每次左右划都要抢焦点（这个程序的规矩是搬窗不抢焦点），
        //     且上下两个方向系统压根没有对应键（Win+Shift+↑ 是最大化、↓ 是无操作，实测），行为不对称。
        // 分屏状态改由 MonitorMapper.MapRect 自己保：源矩形贴着源屏工作区的哪个贴边位，落地就贴目标屏
        // 的同一个贴边位。四个方向一致、不抢焦点、撤销和计数照旧。
        Router.Execute(WindowShuttleAction.ToNext,
            SwapPlanner.PositionOf(target, monitors), fromMouseGesture: true, at: press);
    }

    /// <param name="at">鼠标手势传按下点；键盘/托盘传 null（它们认焦点窗口，光标无关）。</param>
    private static void RunBound(WindowShuttleAction a, bool fromMouseGesture, PointPx? at = null)
    {
        if (a == WindowShuttleAction.Identify) OverlayWindow.ShowAll(shutdownAfter: false);
        else Router.Execute(a, null, fromMouseGesture, at);
    }


    public void ShowMain()
    {
        // belt and braces：即便 OnClosing 现在已经在 CloseToTray=false 时把整个进程带走，ShowMain 本身
        // 还是从托盘左键单击/托盘菜单「打开」/WM_COPYDATA 的裸 show 三处都能到达——任何一处出于时序或
        // 未来改动漏掉 Shutdown，都不该让这里在一扇已关闭的 Window 上调 Show() 炸掉整个驻留进程。
        // Closed（不是 Closing）才是窗口真正没了的时刻，在这里清空缓存，下次 ShowMain 会造一扇新的。
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        _main.Activate();
    }

    /// <summary>
    /// 驻留切换用；verb 为 null 时与旧版行为一致（语言切换重启，Task 14 用）。
    /// verb 非空走提权重启（TrayService 的 AccessDenied 气泡点击）——两条路径必须共用同一处放锁逻辑，
    /// 否则新进程的单实例检查会在旧进程放锁前跑到，误判"已有驻留实例"转而投递 show 后退出，UAC 白点了。
    /// 若 Process.Start 失败（提权被用户拒绝），旧进程还活着，必须把锁补回去——不能让它裸奔成没有
    /// 单实例保护的实例，否则用户再开一个 WindowShuttle.exe 会跑出第二个驻留进程。
    /// 热键必须走同一套"先放、失败再补"：新进程 RegisterHotKey 时若旧进程还占着注册，Win32 会把它判成
    /// 别人占用，新进程里每个热键都会变成 Conflict——用户刚提权重启就发现刚才还好用的热键全废了。
    /// </summary>
    public static void RestartSelf(string? verb = null)
    {
        ((App)Current)._hotkeys?.Dispose();             // 先放热键，新进程才能顺利 RegisterHotKey
        // 鼠标钩子不是排它资源（不像 RegisterHotKey 存在注册期冲突），新进程装钩子不需要
        // 旧进程先放手；这里提前 Dispose 纯粹是缩短"两个进程的钩子同时装着、同一次 chord 被
        // 双份触发"这个过渡窗口，不是为了避让谁。
        ((App)Current)._mouseChords?.Dispose();
        _mutex?.Dispose(); _mutex = null;               // 再放单实例锁，新进程才能成为驻留
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!);
            if (verb is not null) { psi.UseShellExecute = true; psi.Verb = verb; }
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _mutex = new Mutex(true, @"Local\WindowShuttle.SingleInstance", out _);
            ((App)Current).ReapplyHotkeys();            // 重启没成，旧进程要照常干活——热键补回去
            ((App)Current).ReapplyMouseChords();        // 鼠标 chord 也要补回去，否则悄悄失效到下次重启
            throw;
        }
        Current.Shutdown();
    }


    /// <summary>CLI 结果行，恒英文（§12）。</summary>
    private static string Describe(ExecResult r)
    {
        if (r.Plan.NoOp is NoOpReason n) return $"nothing to do: {n}";
        var c = r.Commit!;
        var s = $"moved {c.Moved} window(s)";
        if (c.AccessDenied > 0) s += $", {c.AccessDenied} need administrator rights";
        if (c.OtherFailed > 0) s += $", {c.OtherFailed} failed";
        if (c.Corrected > 0) s += $", {c.Corrected} corrected for DPI overflow";
        if (r.Plan.SkippedFullscreen > 0) s += $", skipped {r.Plan.SkippedFullscreen} fullscreen";
        if (r.Plan.SkippedHung > 0) s += $", skipped {r.Plan.SkippedHung} not responding";
        return s;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_rescueDebounce is not null)               // 挂过才解；SystemEvents 是进程级静态事件
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hotkeys?.Dispose();
        _mouseChords?.Dispose();
        _foreground?.Dispose();
        _tray?.Dispose();
        _sink?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
