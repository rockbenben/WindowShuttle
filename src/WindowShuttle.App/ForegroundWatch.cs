using System.Runtime.InteropServices;

namespace WindowShuttle.App;

/// <summary>盯着前台窗口换到谁头上，只为回答一件事：**是不是又进了"手势整个哑掉"的那个状态**。
///
/// 前台窗口属于一个完整性级别比我们高的进程时，Windows 就不再把任何鼠标事件送给我们这种普通权限
/// 的全局低级钩子——不是"那扇窗搬不动"，是**所有**手势一起沉默，跟光标停在哪块屏、指着哪扇窗
/// 完全无关。实测过：前台是普通窗口时连做 6 次手势 6 次命中，把一个管理员窗口切成前台之后，同样
/// 去点普通窗口，5 次 0 命中。
///
/// 这条限制以前只写在文档里，因为手势那时是辅助入口。方向手势成了主交互之后，它的性质变了：
/// 用户看到的是"这程序时好时坏"，而唯一的自查线索是"手势没反应但快捷键正常"——没有人会往这上面想。
/// 所以要主动说一次，并且给出那条唯一的出路（以管理员身份重启，托盘里本来就有）。
///
/// **判据只能用完整性级别，这是量出来的，不是推的。** 另外两个看着更省事的候选都不成立：
///   · OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) 被拒 —— 对提权进程照样**成功**，分不出来；
///   · OpenProcessToken(TOKEN_QUERY) 被拒 —— 会误报：本机上微信拒绝令牌访问，但它根本没提权，
///     手势在它前台时工作正常。
/// 只有 TokenIntegrityLevel 的 RID 干净地分开了两边（管理员程序 0x3000 / 普通程序 0x2000）。
/// 读不到就当作"不比我高"——宁可漏报也不能误报，一张说不通的提权邀约比不说更糟。
///
/// **用 WinEvent 而不是轮询，也不碰鼠标钩子那条路。** EVENT_SYSTEM_FOREGROUND 是事件驱动的，
/// 而且 WINEVENT_OUTOFCONTEXT 的回调是系统异步投递到我们自己的消息泵上——它慢了只耽误自己，
/// 不像 WH_MOUSE_LL 那样会把全系统的鼠标输入卡在 300ms 预算上（见 MouseChordService 类注释）。
/// 所以这一个挂在 UI 线程上是安全的。</summary>
internal sealed class ForegroundWatch : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint OutOfContext = 0x0000, SkipOwnProcess = 0x0002;
    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int IntegrityHigh = 0x3000;

    private delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint dll, WinEventProc cb, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int cls, nint buf, int len, out int need);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    /// <summary>提权窗口要**停在前台**多久才算数。
    ///
    /// 这是唯一那道防打扰的闸，所以它比看上去重要：配套的提示卡**每次**进入这个状态都会说
    /// （TrayService.NotifyGesturesBlocked 里有为什么不做"只说一次"）。没有这道闸的话，一个抢
    /// 200ms 焦点就走的弹窗——本机上那个 ReminderWindow 就是——每冒一次就弹一张卡，而那种情形
    /// 用户既没看见、也没被卡住：他压根没停在那个程序里。
    ///
    /// 1.5 秒挑在两件事中间：Alt-Tab 掠过一扇窗通常不到 1 秒，而"坐下来在这个程序里干活"是以秒计的。
    /// 判据是"这一刻的前台还是不是它"，不是"这 1.5 秒里没换过"——中途换走又换回来同样算数，
    /// 用户此刻确实卡在这个状态里。
    ///
    /// 一直停着不动只会报一次：计时器只在**前台切换事件**上重起，窗口没换过就没有新事件。</summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(1500);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    private readonly WinEventProc _proc;      // 必须留引用：回调是系统持有的裸指针，被 GC 掉就是崩溃
    private readonly System.Windows.Threading.DispatcherTimer _dwell;
    private nint _hook;

    /// <summary>前台换到了一个完整性级别比我们高的程序上**并且停住了**——从这一刻起手势收不到事件。</summary>
    public event Action? BlockedByElevated;

    public ForegroundWatch()
    {
        _proc = OnForeground;
        _dwell = new System.Windows.Threading.DispatcherTimer { Interval = Dwell };
        _dwell.Tick += (_, _) =>
        {
            _dwell.Stop();
            // 到点再问一次"现在的前台是谁"：这 1.5 秒里可能已经换走了，那就不算。
            nint fg = GetForegroundWindow();
            if (fg == 0 || GetWindowThreadProcessId(fg, out uint pid) == 0 || pid == 0) return;
            if (!BlocksUs(pid)) return;
            if (ChordDebug.Enabled) ChordDebug.Log($"fg     停满 {Dwell.TotalMilliseconds:0}ms，报警 pid={pid}");
            BlockedByElevated?.Invoke();
        };
        // 自己已经是高完整性（用户点过"以管理员身份重启"）就不用挂：比我们更高的只剩 SYSTEM，
        // 那种情形再提权也没有出路，说了等于给一条走不通的路。
        if (IntegrityOf(GetCurrentProcessId()) is >= IntegrityHigh) return;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _proc, 0, 0,
            OutOfContext | SkipOwnProcess);
    }

    /// <summary>只给测试/诊断看：钩子有没有真的挂上（自己已提权时不挂，见构造函数）。</summary>
    public bool Watching => _hook != 0;

    private void OnForeground(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // idObject 必须判：同一个事件也会为窗口内的子对象（光标、插入符）投递，只有 OBJID_WINDOW(0)
        // 是"整扇窗成了前台"。不判的话一次切换会连报好几遍。
        if (hwnd == 0 || idObject != 0) return;
        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return;
        bool blocks = BlocksUs(pid);
        // 每一次前台切换都记一行，不只记会报警的那些。"卡为什么没弹"有三种断法——事件没来、
        // 完整性判低了、判对了但被"一个会话只说一次"压掉——隔着屏幕分不出是哪一种，猜过一次就够了。
        if (ChordDebug.Enabled)
            ChordDebug.Log($"fg     pid={pid} integrity=0x{IntegrityOf(pid)?.ToString("X") ?? "?"} blocks={blocks}");
        // 换到别处就撤销计时：只有**停住**才算，见 Dwell。
        _dwell.Stop();
        if (blocks) _dwell.Start();
    }

    /// <summary>这个进程的完整性级别高到会把我们的低级鼠标钩子挡在外面吗。
    ///
    /// 拿出来单独一个方法（而不是埋在回调里）是为了能测：拿本进程的 pid 问它必须得到 false，
    /// 拿一个根本打不开的 pid（比如 System 的 4）问它也必须得到 false——后者钉的是"读不到就当作
    /// 不比我高"这条兜底，误报一次就是给用户一张说不通的提权邀约。</summary>
    internal static bool BlocksUs(uint pid)
        => IntegrityOf(pid) is int other && other >= IntegrityHigh
           && (IntegrityOf(GetCurrentProcessId()) ?? IntegrityHigh) < other;

    /// <summary>进程的完整性级别 RID（0x2000 中 / 0x3000 高 / 0x4000 系统）。任何一步读不到就返回
    /// null＝"不比我高"，见类注释里为什么宁可漏报。</summary>
    private static int? IntegrityOf(uint pid)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token)) return null;
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out int need);
                if (need <= 0) return null;
                nint buf = Marshal.AllocHGlobal(need);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buf, need, out need)) return null;
                    // TOKEN_MANDATORY_LABEL 的第一个字段就是 SID_AND_ATTRIBUTES.Sid 那个指针。
                    nint sid = Marshal.ReadIntPtr(buf);
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    if (count <= 0) return null;
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    public void Dispose()
    {
        _dwell.Stop();
        if (_hook != 0) { UnhookWinEvent(_hook); _hook = 0; }
    }
}
