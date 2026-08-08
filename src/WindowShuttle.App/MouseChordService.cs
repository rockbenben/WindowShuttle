using System.Runtime.InteropServices;
using System.Windows.Threading;
using WindowShuttle.Core;

namespace WindowShuttle.App;

/// <summary>
/// 鼠标 chord：任意修饰键组合 + 右/中/侧键 1/2，每个动作各绑各的（Settings.MouseChords，见
/// SettingsStore），全部默认空。RegisterHotKey 绑不了鼠标按钮，只能上全局低级钩子 WH_MOUSE_LL——跟
/// HotkeyService 同构（IDisposable + Apply(Settings) + 事件），但没有它的三态返回：钩子没有
/// "注册期冲突"这个概念，SetWindowsHookEx 要么成功要么失败，不存在"这个组合被占了"的信号
/// （见 mouse-hook-spike.md §"The unavoidable part"）。
///
/// 安全边界都在 HookProc 里，逐条对应 spike 的结论：
/// - 回调必须便宜到可以忽略——先比一次消息类型（是不是 R/M/X 三种按钮按下之一），不是就直接落到
///   CallNextHookEx，连 GetAsyncKeyState 都不碰。这一条挡掉了绝大多数流量（WM_MOUSEMOVE
///   是压倒性大头），不分配、不加锁、不写日志、不做 I/O。XBUTTONDOWN 才需要多读一次 lParam 里的
///   MSLLHOOKSTRUCT 辨认是 X1 还是 X2——三种按钮里最少见的一种，多这一步不影响整体热路径成本。
/// - 只有 chord 真的判定出了动作（或正处于录制态）才吞事件——没命中的按下（没绑、修饰键对不上、
///   两个修饰键都按住导致跟任何单修饰键的 chord 都不精确匹配）一律落回 CallNextHookEx，右键菜单
///   / 中键 / 侧键的默认行为不受影响。
/// - **吞了按下就必须把配套的抬起一起吞**（见 _swallowedButtons）。这不是对称性洁癖：很多程序的
///   右键菜单是在抬起那一刻才弹的（WM_RBUTTONUP 之后才有 WM_CONTEXTMENU），中键的自动滚动同理。
///   只吞按下，应用就会收到一个没有配对按下的抬起，照样把菜单弹出来——用户看到的是"手势生效了，
///   但时不时冒出一个右键菜单"。
/// - 真正的动作执行（Router.Execute）绝不在回调里做——回调只把动作丢给 UI 线程的 BeginInvoke
///   就立刻返回。**但只做到这一步不够**，这是实测踩出来的：低级钩子的回调，是由「装钩子的那条
///   线程」在取消息时同步跑的。钩子原先装在 UI 线程上，于是 BeginInvoke 等于把整条动作排进了
///   「必须来服务这个回调的那条线程」的队列——回调确实没在里面跑，300ms 的预算却照样被它花光。
///   这台机器上光枚举窗口就要 10~30ms（983 扇窗），再加跨进程的 SetWindowPos/SetWindowPlacement
///   （同步等目标进程回应）、覆盖层窗口、配置落盘，一次动作轻易越过 300ms。超时的后果不是"慢
///   一点"：全系统的鼠标输入都卡在那儿等这个钩子，然后被静默跳过（spike：驱逐应用侧探测不到）。
///   所以钩子现在跑在自己的专用线程上（<see cref="HookThread"/>），那条线程除了泵消息什么都不干，
///   UI 线程再忙也压不到它。改这个类时唯一要守住的就是这条：别再往钩子线程上放任何会阻塞的活儿。
/// </summary>
public sealed class MouseChordService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
    private const int WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208, WM_XBUTTONUP = 0x020C;
    private const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT Pt; public uint MouseData; public uint Flags; public uint Time; public nint ExtraInfo; }

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, nint hMod, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public MOUSEINPUT Mi; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>补发的那次点击自己会再从钩子里过一遍。靠 ExtraInfo 上的这个签名把它认出来直接放行，
    /// 否则它会被当成一次新的裸右键按下，再扣一次、再补发一次，无限循环。</summary>
    private const nint ReplayTag = 0x57534452;   // 'WSDR'

    private static readonly uint[] DownFlag = [0x0008, 0x0020, 0x0080, 0x0080];   // Right / Middle / X1 / X2
    private static readonly uint[] UpFlag   = [0x0010, 0x0040, 0x0100, 0x0100];
    private static readonly uint[] XData    = [0, 0, 1, 2];

    private const uint MouseAbsolute = 0x8000, MouseVirtualDesk = 0x4000, MouseMove = 0x0001;
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>把刚才扣下的那次点击原样还回去：按下＋抬起，带签名，**落在按下的那个点上**。
    ///
    /// 坐标必须钉死，不能就地发。走到这里只说明位移不到 MinStroke，而那是 40px——右键点列表里的
    /// 某一行时，差 39px 足够点中相邻的另一行。用户按在哪儿，补发就必须落在哪儿。
    ///
    /// 绝对坐标是 0..65535 归一化到**整个虚拟桌面**（配 VIRTUALDESK），不是主屏——多屏下按主屏归一
    /// 会把副屏上的点算飞。减 1 是因为右/下边界那一格换算回去会溢出到下一像素。</summary>
    private static void ReplayDown(MouseChordButton b, int x, int y) => Replay(b, x, y, downOnly: true);

    private static void Replay(MouseChordButton b, int x, int y, bool downOnly = false)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);      // SM_XVIRTUALSCREEN / Y
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);      // SM_CXVIRTUALSCREEN / CY
        if (vw <= 1 || vh <= 1) return;
        int nx = (int)((x - vx) * 65535L / (vw - 1));
        int ny = (int)((y - vy) * 65535L / (vh - 1));

        int i = (int)b;
        uint pos = MouseAbsolute | MouseVirtualDesk | MouseMove;
        var seq = new INPUT[2];
        seq[0].Type = 0; seq[0].Mi.Dx = nx; seq[0].Mi.Dy = ny;
        seq[0].Mi.Flags = pos | DownFlag[i]; seq[0].Mi.MouseData = XData[i]; seq[0].Mi.ExtraInfo = ReplayTag;
        seq[1].Type = 0; seq[1].Mi.Dx = nx; seq[1].Mi.Dy = ny;
        seq[1].Mi.Flags = pos | UpFlag[i];   seq[1].Mi.MouseData = XData[i]; seq[1].Mi.ExtraInfo = ReplayTag;
        uint sent = SendInput(downOnly ? 1u : 2u, seq, Marshal.SizeOf<INPUT>());
        if (ChordDebug.Enabled)
            ChordDebug.Log($"replay btn={b} at {x},{y} downOnly={downOnly} sent={sent} err={Marshal.GetLastWin32Error()}");
    }

    // 必须跟 HotkeyService.Actions / MainWindow.ActionKeys / SettingsStore 的两张默认表逐字一致。
    // 加动作时这里被漏掉过一次，后果不是"新动作不能用"这么轻：给新动作录一个已被占用的手势，
    // AssignMouseChord 会先从原主人那儿收走，而这张表里没有新动作、Apply 也就不会装上它——
    // 那个组合从此谁都不响应，界面上两格却都显示已绑定（鼠标侧没有冲突态可标）。
    // ActionTablesAgreeTests 现在钉着这四张表。
    internal static readonly string[] Actions =
        ["Swap", "SwapTop", "ToPrimary", "ToNext", "ToDirection",
         "Gather", "Undo"];

    private readonly Dispatcher _dispatcher;
    // 强引用委托本身，保住它的命：SetWindowsHookEx 只存了函数指针，不会替委托续命。挂钩子的
    // 这段时间里若只把它当一次性表达式传进去、不额外持有引用，GC 有机会把委托收走，下一次
    // 系统从这个失效地址回调就会直接崩掉——这是这类钩子最经典的用法错误，字段化就是防它。
    private readonly LowLevelMouseProc _proc;
    private nint _hook;
    private Dictionary<WindowShuttleAction, MouseChordGesture> _chords = [];

    /// <summary>已经被我们吞掉「按下」的按钮，位掩码（1 &lt;&lt; (int)MouseChordButton）。
    ///
    /// 只吞按下、放走抬起是不够的——而且用户看得见：很多程序的右键菜单是在**抬起**那一刻弹的
    /// （WM_RBUTTONUP 之后才有 WM_CONTEXTMENU），中键的自动滚动同理。于是一个被我们完整拦截的手势，
    /// 应用那边收到的是一个没有配对按下的抬起，照样把菜单弹出来——症状就是"手势明明生效了，
    /// 却时不时冒出一个右键菜单"。按下吞了，配套的抬起就必须一起吞。
    ///
    /// 用位掩码而不是集合：这条路在钩子回调里，不能有分配。钩子只在装它的那条线程上回调，
    /// 而所有会改它的入口（Apply/BeginCapture/EndCapture/Dispose）都 Invoke 到那条线程上去，
    /// 于是仍然是单线程读写，不需要加锁。</summary>
    private int _swallowedButtons;

    /// <summary>取消划动的那一下**左键**按下被我们吞了，它配套的抬起也欠着。
    ///
    /// 左键单开一个 bool 而不是并进上面那个位掩码：那个掩码按 <see cref="MouseChordButton"/> 的位序
    /// 记账，而左键**故意**不在那个枚举里——它永远不是合法的手势按钮（见 MouseChordGesture.TryParse
    /// 里 "left" 那一支）。为了记一次取消就给枚举加个成员，等于让"左键可以被绑"这件事在类型上成立。
    /// 只在钩子线程上读写，跟 _swallowedButtons 同一条规矩。</summary>
    private bool _swallowedLeft;

    /// <summary>上一次**没命中任何 chord** 的按钮按下落在哪儿。纯诊断，只在 ChordDebug 开着时写。
    ///
    /// 存在的理由很窄：日志里一行 <c>mods=0x0</c> 有两种完全相反的含义——用户本来就只是右键点了
    /// 一下，还是他想做手势而修饰键在按下那一瞬没被读到。两者都长成同一行，据此判断过一次，判错了
    /// （把一串普通右键当成了"手势失败"）。松手时的位移能把它们分开：普通点击≈0，手势会有几百像素。</summary>
    private (int X, int Y)? _missFrom;

    // 方向手势的起点，以及它属于哪个按钮。只在钩子线程上读写，跟 _swallowedButtons 同一条规矩。
    private (int X, int Y)? _strokeFrom;

    /// <summary>这次划动在两个轴上各自到达过的**带符号峰值**位移（|值|最大的那个瞬时位移）。
    ///
    /// 方向 = 拿这对峰值过 45° 线：与水平的夹角不超过 45° 就算左右，否则算上下（判定在
    /// SwapPlanner.MonitorToward，平手归左右）。这是手势软件的标准做法——量的是**整条划动**
    /// 的形状，不是某一个点：
    ///   · 不拿松手点（最初的做法，实测被用户抓到"往左右划经常被判成上下"）——快速横甩总带着
    ///     往下收的尾巴，松手落在减速段上，竖直净位移经常反超横向；
    ///   · 也不拿出手头 40px 锁定（第二版的做法，被用户纠正）——出手那一瞬的角度未必代表整条
    ///     划动的走向，人的意图在整个动作里。
    /// 峰值对两类真实轨迹都稳：横甩带回收（净位移≈0，但 X 峰值完整留着）、横甩带下勾（勾的
    /// 幅度只要没超过横向幅度，45° 线就判对）。
    ///
    /// 只在钩子线程上读写，跟 _swallowedButtons 同一条规矩。</summary>
    private int _strokePeakX, _strokePeakY;
    private MouseChordButton? _strokeButton;
    private bool _strokeBare;      // 这次有没有修饰键 —— 决定没划出方向时要不要补发点击
    private System.Threading.Timer? _holdTimer;
    private bool _handedOver;      // 已经把按下补发给应用了：接下来的抬起要原样放行，别再吞

    /// <summary>算不算划出了方向。App 那边判「那个方向没有屏」前也要用同一个数，所以放这儿共用。</summary>
    internal const int MinStroke = 40;

    /// <summary>按住不动多久就判定"这不是手势"，把按下放给应用。
    ///
    /// 没有这条，任何依赖右键**长按**的程序在绑了裸按钮之后都会失灵——我们一直扣着那次按下等它划，
    /// 而用户等的是长按菜单/长按连发。600ms 比一次有意的划动慢得多（划动通常 100~250ms 就结束），
    /// 又比大多数程序的长按阈值短，落在两者中间。</summary>
    private const int HoldMs = 600;

    /// <summary>命中了一个点击类 chord。**带上按下点**，理由跟 <see cref="Stroked"/> 完全一样：
    /// 动作是排队到 UI 线程之后才执行的，那时候再去读光标读到的是"现在指针在哪"，而用户指的是
    /// "我按下去那一刻指着谁"。中间隔着钩子返回、消息队列排空、以及一次 10~30ms 的窗口枚举——
    /// 手在这段时间里完全来得及动，而这个 chord 把按下吞掉了、底下的程序没有任何点击反馈，
    /// 手不停下来才是自然的。按下点本来就在钩子手里，只是以前没往下传。</summary>
    public event Action<WindowShuttleAction, int, int>? Triggered;

    /// <summary>方向手势划完了：**按下点** (X, Y) 加上到抬起点的位移 (Dx, Dy)，都是屏幕像素。
    ///
    /// 按下点必须一起报。跨屏划动时松手点已经在另一块屏上了，拿它去找"光标下的窗口"会找到目标屏
    /// 上的另一扇窗——用户指的始终是他**按下去时**指着的那一扇。方向换算成哪块屏是 App 层的事，
    /// 钩子只报"从哪儿开始、往哪儿划了多远"。</summary>
    public event Action<int, int, int, int>? Stroked;

    /// <summary>录制态下捕到的一次手势：按钮 + 那一瞬按住的修饰键。</summary>
    public event Action<MouseChordButton, uint>? Captured;

    private nint _captureFor;                          // 非 0 = 正在为这个前台窗口录制

    /// <summary>进入录制态：接下来的一次右/中/侧键按下不再去匹配已绑定的动作，而是原样交给
    /// <see cref="Captured"/>，并且吞掉，不让它透给界面或别的程序。
    ///
    /// 录制走钩子的真实理由是**改绑**：录制期间钩子先把按下截走、不再走 Resolve，所以「把一个已经
    /// 绑给别的动作的组合改绑过来」不会被那个动作抢先吞掉并执行。这条独立成立，跟修饰键无关。
    ///
    /// 这里原来还写着第二条理由，说的是"钩子读得到 Win（mods=0x8）而 WPF 那侧读不到，所以走钩子
    /// 就能录 Win 组合"——**那条是错的，别再照它推理**。真实情况是 Win 在这两侧都用不了：shell 在
    /// 点击落地之前就把 Win 的按下状态收走了，钩子这一瞬读到的同样是 0x0（作者机器上真手按出来的
    /// 实测结论）。
    ///
    /// 之所以会写错，是因为**合成输入在这个问题上说谎**：SendInput 发出来的 Win 按下确实能被钩子
    /// 读成 0x8，跟真手按下的结果相反。凡是拿脚本验这条的，得到的都是那个假阳性——本仓库先后栽过
    /// 两次。要重验只能真手按，判据和步骤写在 docs/manual-checks.md 的「【真手】合成输入会说反话」。
    ///
    /// hwnd 是录制那扇窗：只有它在前台时才捕获，否则用户点别的程序会被我们吃掉一次右键。</summary>
    public void BeginCapture(nint hwnd)
    {
        _captureFor = hwnd;
        Install();
    }

    /// <summary>退出录制态。钩子该不该留着，交回 Apply 那条唯一的规则（没绑任何 chord 就不装钩子）。</summary>
    public void EndCapture(Settings s)
    {
        _captureFor = 0;
        // **不清 _swallowedButtons。** 清了会把"按下已经吞了、配套的抬起还没到"那笔债一起抹掉，
        // 那次抬起就会漏给底下的程序，变成一个没有配对按下的抬起——正是这个位掩码要防的事
        // （右键菜单凭空冒出来）。账本自己会愈合：放行的按下会勾销这个按钮的旧账，见 AfterDown。
        Apply(s);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    /// <summary>只给测试/诊断看：钩子有没有真的装进这个进程。跟私有的 _chords 不一样，这个状态
    /// 值得公开——它就是"没绑东西就不装钩子"这条承诺本身，值得能被验证，不只是靠读代码相信。</summary>
    public bool HookInstalled => _hook != 0;

    public MouseChordService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = HookProc;
    }

    private Dispatcher? _hookThread;

    /// <summary>钩子的专用线程，第一次真要装钩子时才起。低级钩子的回调由**装它的那条线程**在取
    /// 消息时同步跑，所以这条线程必须永远是闲的：它只泵消息，不做别的（见类注释里 300ms 那段）。
    ///
    /// 直接用 WPF 的 Dispatcher 而不是手写 GetMessage 循环：Dispatcher.Run 本身就是一个标准消息
    /// 泵，系统照样能在它上面回调钩子，而且顺带白拿到 Invoke/InvokeShutdown——省掉手写线程间调度
    /// 和退出握手的那几十行。IsBackground：这条线程绝不能拦着进程退出。</summary>
    internal Dispatcher HookThread()      // internal：这条"不在 UI 线程上"的性质本身就该能被测
    {
        if (_hookThread is not null) return _hookThread;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _hookThread = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "WindowShuttle mouse hook" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        return _hookThread!;
    }

    // 装和卸都得在钩子线程上做：钩子只能由装它的那条线程卸掉，而 _swallowedButtons 也就顺势
    // 保持单线程读写、不用加锁。同步 Invoke 不会死锁——钩子线程从不反过来同步等 UI 线程（只 BeginInvoke）。

    private void Install()
    {
        if (_hook != 0) return;
        HookThread().Invoke(() =>
        {
            if (_hook == 0) _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
            // 记一笔装钩子的结果：排查"手势整个没反应"时，第一件要分清的就是钩子到底装上没有，
            // 以及它是不是真的在自己那条线程上（thread 不该等于 UI 线程）。
            if (ChordDebug.Enabled)
                ChordDebug.Log($"install hook=0x{_hook:X} thread={Environment.CurrentManagedThreadId}");
        });
    }

    private void Uninstall()
    {
        StopHoldTimer();                           // 先停表：一个还在倒数的长按定时器不该活过拆卸
        if (_hook == 0) return;                    // 没装过就没线程可 Invoke，也没账本要清
        _hookThread!.Invoke(() =>
        {
            if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
            _swallowedButtons = 0;
            _swallowedLeft = false;   // 钩子都卸了，欠条没人还得起，留着只会在重装后误吞一次抬起
        });
    }

    /// <summary>
    /// 装/卸钩子跟"有没有绑任何一个 chord"对齐：一个都没绑就彻底不装钩子，进程里不存在这个钩子，
    /// 不是"装了但不触发"——没绑过任何 chord 的用户不该承担任何鼠标钩子相关的风险（§own-decision）。
    /// </summary>
    /// <summary>这条 chord 能不能真的装到钩子上。裸按钮只有「按方向送」放行，规则和理由都在
    /// <see cref="SettingsStore.AllowsBareButton"/>。
    ///
    /// 闸必须设在**这里**，不能只设在录制那一侧（MainWindow.MouseChords.OnChordCaptured）。
    /// TryParse 已经不再拒绝裸按钮了（划动类要用），于是 settings.json 才是真正的入口：手工改过的、
    /// 从别的机器拷来的、从备份里还原的配置，都绕开界面直接走到这里。一条 "ToPrimary": "Right"
    /// 装上去的后果是全系统级的——每一次右键按下都命中动作、被吞，配套的抬起也被 TakeUp 吞掉，
    /// 而点击类动作**没有补发路径**（补发只存在于划动那一支）。资源管理器、浏览器、编辑器里的
    /// 右键菜单从此全部不再弹出，应用里也没有任何一处会提示这件事。
    ///
    /// 键盘那侧早就有同构的一道闸（HotkeyService.Apply 查 AllowsHotkey），加它的理由正是同一条
    /// 手工改配置的路径。这里缺的是它的孪生兄弟。</summary>
    internal static bool Installable(string actionKey, MouseChordGesture g)
        => g.Modifiers != 0 || SettingsStore.AllowsBareButton(actionKey);

    /// <summary>settings.json 里那张表 → 真正装到钩子上的那张表。抽成静态纯函数只为一件事：能直接测。
    /// <see cref="Apply"/> 会去装/卸真的低级鼠标钩子，测试里不该碰它，而这道政策闸恰恰是最该被钉住的
    /// 一段（缺了它，一行配置就能让全系统的右键菜单消失）。
    ///
    /// 插入顺序按 <see cref="Actions"/> 走，这是有意义的而不是随手：MouseChordGesture.Resolve 取第一个
    /// 命中的，所以万一表里真有重复，赢的是 Actions 里靠前的那个。SettingsStore.Load 会先把重复清掉，
    /// 两处对同一个顺序的理解必须一致。</summary>
    internal static Dictionary<WindowShuttleAction, MouseChordGesture> Installed(Settings s)
    {
        var chords = new Dictionary<WindowShuttleAction, MouseChordGesture>();
        foreach (var name in Actions)
        {
            var raw = s.MouseChords.GetValueOrDefault(name, "");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var g = MouseChordGesture.TryParse(raw);
            if (g is not null && Installable(name, g)) chords[Enum.Parse<WindowShuttleAction>(name)] = g;
        }
        return chords;
    }

    public void Apply(Settings s)
    {
        _chords = Installed(s);

        // `|| _captureFor != 0`：录制现在完全依赖钩子（见 BeginCapture）。清掉最后一个绑定时
        // Apply 会走到这里，若照旧按"没有绑定就卸钩子"办，正在进行的那次录制当场哑掉——
        // 用户在同一格上想录个替代手势，按下去毫无反应。
        if (_chords.Count > 0 || _captureFor != 0) Install(); else Uninstall();
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        // 热路径：绝大多数事件是 WM_MOUSEMOVE，一次整数比较立刻放行，不碰 GetAsyncKeyState/lParam。
        // nCode<0 时文档要求必须原样转发、不得处理，同一分支一并挡住。
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            // 自己补发的那次点击：原样放行，不再走扣留逻辑，否则会无限自我循环。
            if (msg is >= 0x0201 and <= 0x020D
                && Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).ExtraInfo == ReplayTag)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            // 划动进行中：累计两个轴上的带符号峰值（理由见 _strokePeakX）。每次移动只是两个减法
            // 加两个比较——钩子的 300ms 预算花得起。注意 SetCursorPos 不产生输入流事件、到不了这里，
            // 真实鼠标和 SendInput 才会；写测试装置时踩过这个假阴性。
            if (_strokeButton is not null && msg == 0x0200 /* WM_MOUSEMOVE */
                && _strokeFrom is var (ox, oy))
            {
                var mv = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Pt;
                int mdx = mv.X - ox, mdy = mv.Y - oy;
                if (Math.Abs(mdx) > Math.Abs(_strokePeakX)) _strokePeakX = mdx;
                if (Math.Abs(mdy) > Math.Abs(_strokePeakY)) _strokePeakY = mdy;
            }

            // 诊断专用：一次**没命中**的按钮按下松开时，把这中间走了多远记下来。
            // 回答的是一个别处答不了的问题——日志里一行 mods=0x0 有两种完全不同的含义：用户本来就只是
            // 右键点了一下（位移≈0，一切正常），还是他想做手势而修饰键没被读到（拖出了几百像素，
            // 那才是缺陷）。光看 mods 分不开这两者，曾经据此得出过错误结论。
            if (ChordDebug.Enabled && _missFrom is var (qx, qy)
                && msg is WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP)
            {
                var q = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Pt;
                int qdx = q.X - qx, qdy = q.Y - qy;
                _missFrom = null;
                ChordDebug.Log(Math.Abs(qdx) >= MinStroke || Math.Abs(qdy) >= MinStroke
                    ? $"miss   没命中却拖了 ({qdx},{qdy}) —— **像是没读到修饰键的手势**"
                    : $"miss   没命中，位移 ({qdx},{qdy}) —— 就是一次普通点击");
            }

            // 抬起：只有当它配套的按下被我们吞过时才吞。_swallowedButtons != 0 这道前置判断让
            // 常态（没吞过任何东西）连消息号都不用逐个比，更不会为 XBUTTONUP 去 marshal 那个结构体。
            if (NeedsUpPass(_swallowedButtons, _strokeButton is not null))
            {
                MouseChordButton? up = msg switch
                {
                    WM_RBUTTONUP => MouseChordButton.Right,
                    WM_MBUTTONUP => MouseChordButton.Middle,
                    WM_XBUTTONUP => XButtonOf(lParam),
                    _ => null,
                };
                if (up is { } u)
                {
                    // 超时已经把按下交还给应用了：这次抬起原样放行，应用才收得到完整的一次按下+抬起。
                    if (_handedOver && _strokeButton == u)
                    {
                        _handedOver = false;
                        _strokeFrom = null;
                        _strokeButton = null;
                        _strokePeakX = 0; _strokePeakY = 0;
                        return CallNextHookEx(_hook, nCode, wParam, lParam);
                    }
                    if (_strokeButton == u && _strokeFrom is var (sx, sy))
                    {
                        StopHoldTimer();
                        var p = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Pt;
                        bool bare = _strokeBare;
                        // 松手点也折进峰值——它自己可能就是某个轴上的最远点。
                        int fx = p.X - sx, fy = p.Y - sy;
                        int ddx = Math.Abs(fx) > Math.Abs(_strokePeakX) ? fx : _strokePeakX;
                        int ddy = Math.Abs(fy) > Math.Abs(_strokePeakY) ? fy : _strokePeakY;
                        _strokeFrom = null;
                        _strokeButton = null;
                        _strokePeakX = 0; _strokePeakY = 0;
                        bool drew = Math.Abs(ddx) >= MinStroke || Math.Abs(ddy) >= MinStroke;
                        if (ChordDebug.Enabled)
                            ChordDebug.Log($"up     峰值=({ddx},{ddy}) 松手点位移=({fx},{fy})");
                        if (!drew && bare)
                            // 裸按钮 + 没划出方向 = 用户要的就是一次普通点击，原样还回去。
                            // 排进钩子线程的队列里发，不在回调内部发：SendInput 会立刻再引发一次回调，
                            // 在回调里重入是自找麻烦。
                            _hookThread?.BeginInvoke(() => Replay(u, sx, sy));
                        else
                            _dispatcher.BeginInvoke(() => Stroked?.Invoke(sx, sy, ddx, ddy));
                    }
                    (bool swallow, _swallowedButtons) = TakeUp(_swallowedButtons, u);
                    if (swallow) return 1;
                }
            }

            // ⑦ 划到一半按左键 = 取消。手势类软件的通用约定：反悔时不必想办法"划回原处"。
            // 取消就是彻底不做——不执行动作，也不补发那次右键（用户要的既不是菜单也不是搬运）。
            // 这一下左键本身也吞掉，否则取消动作会顺手在底下的程序里点一下。
            //
            // 左键的按下和抬起**必须成对处理**，这也是这里要单独记一个 _swallowedLeft 的全部原因：
            // 吞了按下却放行抬起，底下的程序就收到一个没有配对按下的抬起——跟本文件开头讲的那种
            // 孤儿事件是同一类错误，只是方向相反。列表的选中提交、画布的点击处理、网页上的 mouseup
            // 监听都会照做一次用户根本没做过的点击。左键进不了 _swallowedButtons：那个账本按
            // MouseChordButton 的位序记账，而左键**故意**不在那个枚举里（它永远不是合法的手势按钮）。
            if (msg == 0x0201 /* WM_LBUTTONDOWN */)
            {
                if (_strokeButton is not null)
                {
                    StopHoldTimer();
                    _strokeFrom = null;
                    _strokeButton = null;
                    _strokePeakX = 0; _strokePeakY = 0;
                    _handedOver = false;
                    _swallowedLeft = true;
                    return 1;
                }
                // 自愈，跟 AfterDown 里那一下同一个道理：这次按下没吞，就不该再欠着谁的抬起。
                // 钩子被系统跳过时会漏看一次抬起，没有这一行，那张欠条会留到之后某一次无辜的
                // 左键点击上，把它的抬起吞掉。
                _swallowedLeft = false;
            }
            if (msg == 0x0202 /* WM_LBUTTONUP */ && _swallowedLeft)
            {
                _swallowedLeft = false;
                return 1;
            }

            MouseChordButton? button = msg switch
            {
                WM_RBUTTONDOWN => MouseChordButton.Right,
                WM_MBUTTONDOWN => MouseChordButton.Middle,
                WM_XBUTTONDOWN => XButtonOf(lParam),
                _ => null,
            };
            if (button is { } b)
            {
                uint mods = HeldModifiers();
                // 插值串要在进 Log 之前就拼好，所以这道 Enabled 判断必须写在**调用点**：日志关着时
                // 连那个字符串都不该产生。钩子回调里不分配——一次 GC 停顿就够把 300ms 预算吃掉。
                if (ChordDebug.Enabled)
                {
                    // Flags 里 0x1=INJECTED（合成输入）、0x2=LOWER_IL_INJECTED。查"手势之后冒出诡异的
                    // 右键拖拽/复制"要靠它：这台机器上观察到过被吞掉的点击约 500ms 后被**别的软件**
                    // 重新注入（没有我们的 ReplayTag），那种事件在这里会显形为 flags=0x1 的按下。
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    ChordDebug.Log($"hook   msg=0x{msg:X4} btn={b} mods=0x{mods:X} flags=0x{info.Flags:X} extra=0x{(long)info.ExtraInfo:X} at {info.Pt.X},{info.Pt.Y} captureFor={_captureFor} mask={_swallowedButtons}");
                }
                // 录制态优先：截下这一次按下交给界面去登记，不再去匹配已绑定的动作。
                //
                // 这里**不能**再加一道 GetForegroundWindow() == 录制窗口 的判断。看着稳妥，实际是致命的：
                // 按住 Win 的那一刻前台窗口就归了 shell（实测日志里 fg=131806，不是主窗口的句柄），
                // 于是这道判断恰好在最需要捕获的时候把捕获挡掉。
                // 不需要它也是安全的：捕获是靠指针悬停在某个键位区上才武装起来的（见 MainWindow 的
                // MouseEnter/MouseLeave），指针既然在我们这一格上，那下面就没有别人的窗口可抢。
                if (_captureFor != 0)
                {
                    _swallowedButtons = AfterDown(_swallowedButtons, b, swallowing: true);
                    _dispatcher.BeginInvoke(() => Captured?.Invoke(b, mods));
                    return 1;
                }
                var action = MouseChordGesture.Resolve(b, mods, _chords);
                // 没命中的那些按下，记下起点——只为回答一个诊断问题，见 _missFrom。
                if (ChordDebug.Enabled && action is null)
                    _missFrom = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Pt is var mp ? (mp.X, mp.Y) : null;
                // 记账无论命中与否都要做：没命中时它清掉这个按钮可能残留的旧欠条（见 AfterDown）。
                _swallowedButtons = AfterDown(_swallowedButtons, b, swallowing: action is not null);
                // 方向手势是唯一一个**在抬起时**才发动作的：按下只记起点，划到哪儿要等松手才知道。
                // 按下照样吞掉（配套的抬起也会被吞，见 TakeUp），所以底下的程序全程什么都收不到，
                // 拖拽期间不会有人跟着起反应。
                if (action == WindowShuttleAction.ToDirection)
                {
                    var p = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Pt;
                    _strokeFrom = (p.X, p.Y);
                    _strokeButton = b;
                    _strokeBare = mods == 0;
                    _strokePeakX = 0; _strokePeakY = 0;
                    _handedOver = false;
                    StartHoldTimer(b, p.X, p.Y);
                    return 1;
                }
                if (action is { } a)
                {
                    // 吞事件路径：chord 真的命中了。真正执行丢给 UI 线程异步做（BeginInvoke 不等待），
                    // 钩子这一帧只做到"确认命中、记账、转发、返回"，不被下游动作的快慢拖住。
                    var tp = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Pt;
                    _dispatcher.BeginInvoke(() => Triggered?.Invoke(a, tp.X, tp.Y));
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>⑥ 长按放行：到点还没划出方向，就把**按下**补发给应用（只发按下，不发抬起），
    /// 随后那次真实的抬起原样放行——应用因此收到的是一次完整、时序正常的长按。
    ///
    /// 只补发按下这一半是关键。若像"没划方向"那样补发一次完整点击，应用拿到的是 down+up，
    /// 长按的语义当场丢失，而用户手还按着。</summary>
    private void StartHoldTimer(MouseChordButton b, int x, int y)
    {
        StopHoldTimer();
        if (ChordDebug.Enabled) ChordDebug.Log($"holdarm btn={b} bare={_strokeBare} at {x},{y}");
        if (!_strokeBare) return;                // 带修饰键的组合不存在"抢了别人的长按"这个问题
        _holdTimer = new System.Threading.Timer(_ =>
        {
            // 字段只读一次，读到什么就用什么。原来是"先判 null、再解引用"两次读同一个字段，而
            // Dispose 在 UI 线程上把它置空——正好卡在两次读之间，就是一个 ThreadPool 线程上的
            // 未捕获 NullReferenceException，进程当场崩。用户看到的是：我明明点了退出，它却弹了
            // 一个崩溃对话框。
            var thread = _hookThread;
            if (thread is null) return;
            // 拿到了引用也不等于它还活着：Dispose 可能已经 InvokeShutdown。往关掉的 dispatcher 上
            // 排队会抛，而这里是定时器回调，抛出去同样没人接。
            try
            {
                thread.BeginInvoke(() =>
            {
                // 交还没发生时，这四道闸里到底是哪一道拦下的——隔着屏幕猜过一次，猜错了。
                if (ChordDebug.Enabled)
                    ChordDebug.Log($"holdcb btn={b} stroke={_strokeButton} handed={_handedOver} " +
                                   $"peak=({_strokePeakX},{_strokePeakY}) from={_strokeFrom?.ToString() ?? "null"}");
                if (_strokeButton != b || _handedOver) return;
                // 已经划出方向 = 这是一次进行中的手势，只是中途停顿超过了 HoldMs——交还按下会把
                // 一次说了一半的手势腰斩成长按。"没划出方向"才轮得到长按放行。
                if (Math.Abs(_strokePeakX) >= MinStroke || Math.Abs(_strokePeakY) >= MinStroke) return;
                var moved = _strokeFrom;
                if (moved is null) return;
                _handedOver = true;
                // 账本跟着按钮一起交出去。从这一刻起这个按钮归应用了，它那次真实的抬起必须原样放行，
                // 所以欠条现在就要销掉——**不能**等抬起那一刻再说。不做这一步有两个后果，都很具体：
                //   ① 用户随后按左键取消：取消分支把 _handedOver 清成 false，真实抬起于是既不走交还
                //      分支也不走划动分支，落到 TakeUp 手里被吞掉。应用收到一个永远等不到抬起的按下，
                //      停在拖拽/框选/视角旋转里出不来——正是下面账本注释里那句"鼠标点击卡住了"。
                //   ② 就算用户不取消，交还那条抬起分支是直接 return 的，从不销账；那张欠条会留到
                //      下一次**毫不相干**的点击，把它的抬起吞掉。
                _swallowedButtons = AfterDown(_swallowedButtons, b, swallowing: false);
                ReplayDown(b, x, y);
                if (ChordDebug.Enabled) ChordDebug.Log($"hold   {HoldMs}ms 未划出方向，按下已交还应用");
                });
            }
            catch (InvalidOperationException) { /* dispatcher 已经收摊了，这次长按不必再交还 */ }
        }, null, HoldMs, System.Threading.Timeout.Infinite);
    }

    private void StopHoldTimer()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
    }

    private static int Bit(MouseChordButton b) => 1 << (int)b;

    // ══ 吞咽账本：两个纯函数 ══════════════════════════════════════════════════════════════════
    //
    // 拆出来是为了能直接测。这里的错法在真机上极难复现——要先让钩子超时一次才看得见——但后果
    // 很具体：账本里留下一个还不掉的欠条，下一次**毫不相干**的普通点击的抬起会被它吞掉。
    //
    // 抬起被吞、按下没被吞，目标程序就收到一个没有配对抬起的按下：它认为按键还按着，于是停在
    // 拖拽/框选/捕获状态里不出来——用户看到的就是"鼠标点击卡住了"。

    /// <summary>抬起这一帧要不要进那个"抬起处理块"。
    ///
    /// 两个条件缺一不可，第二个是买来的。原来只判 <paramref name="swallowedMask"/>≠0，理由是热路径
    /// 便宜——常态什么都没欠，连消息号都不用逐个比。但账本和划动状态是**两件事**：长按交还的那一刻
    /// 欠条就销了（按钮归应用了，见 StartHoldTimer），而划动状态还挂着，必须靠这一帧的抬起去清。
    /// 只判掩码的话，交还之后这一块整个被跳过，于是 _strokeButton / _strokeFrom / _handedOver 全部
    /// 停在旧值上——**下一次左键点击会被取消分支当场吞掉**（它的前提正是"_strokeButton 不为 null"），
    /// 用户莫名其妙丢一次点击。实测抓到过：交还之后的那一帧抬起，日志里连一行都没有。
    ///
    /// 拆成纯函数是为了能钉住它：这道闸被改窄回去的后果，在真机上要先让长按超时一次才看得见。</summary>
    internal static bool NeedsUpPass(int swallowedMask, bool strokeActive)
        => swallowedMask != 0 || strokeActive;

    /// <summary>一次抬起该不该吞：只吞我们记过账的那一个，吞完销账。</summary>
    internal static (bool Swallow, int Mask) TakeUp(int mask, MouseChordButton b)
        => (mask & Bit(b)) != 0 ? (true, mask & ~Bit(b)) : (false, mask);

    /// <summary>一次按下之后的账本。<paramref name="swallowing"/>=false（这次按下要放给应用）时
    /// **必须把这个按钮的旧账一笔勾销**，这就是自愈的那一下：
    ///
    /// 钩子超时被系统跳过时，我们会漏看一次抬起，欠条就永远挂在账上了（谁也不会来还）。没有这一
    /// 行，那张欠条会去吞掉之后某一次正常点击的抬起，把卡死转嫁给一次无辜的点击。有了它，最坏
    /// 情况也只是那一次手势少吞一个抬起（可能冒一下右键菜单），下一次按下就恢复正常——
    /// 用"偶尔多一个菜单"换掉"鼠标卡住"，这个交换在任何时候都值。</summary>
    internal static int AfterDown(int mask, MouseChordButton b, bool swallowing)
        => swallowing ? mask | Bit(b) : mask & ~Bit(b);

    private static MouseChordButton? XButtonOf(nint lParam)
    {
        var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        return (s.MouseData >> 16) switch { 1 => MouseChordButton.X1, 2 => MouseChordButton.X2, _ => null };
    }

    /// <summary>此刻按住的修饰键位掩码。**录制和触发必须共用这一个**——两边各读各的，录下来的组合
    /// 就可能永远触发不了。实测踩到的正是这一条：录制那侧原来用 WPF 的 Keyboard.Modifiers，而它
    /// 根本不报 Windows 键（ModifierKeys.Windows 这个枚举值存在，WPF 的键盘设备却从不置位），
    /// 于是同一次点击，录制那侧和触发那侧读出两个不同的值。改成两边都用这里，那类错配就不存在了。
    ///
    /// 但**别指望它能救 Win 键**：GetAsyncKeyState 读的是真实键盘状态没错，可 shell 在点击落地之前
    /// 就已经把 Win 的按下状态收走了（它就是靠这个抑制开始菜单），这一瞬读到的是 0x0。Win 组合因此
    /// 录不进也触发不了，见 BeginCapture 里那段"合成输入会说谎"的说明。</summary>
    internal static uint HeldModifiers()
    {
        uint m = 0;
        if (Down(VK_CONTROL)) m |= HotkeyGesture.ModControl;
        if (Down(VK_MENU)) m |= HotkeyGesture.ModAlt;
        if (Down(VK_SHIFT)) m |= HotkeyGesture.ModShift;
        if (Down(VK_LWIN) || Down(VK_RWIN)) m |= HotkeyGesture.ModWin;
        return m;
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public void Dispose()
    {
        StopHoldTimer();                    // Uninstall 里也停了一次；钩子没装过时那条路会提前 return
        Uninstall();
        _hookThread?.InvokeShutdown();      // 让那条线程的消息泵退出来；IsBackground 只是兜底
        _hookThread = null;
    }
}
