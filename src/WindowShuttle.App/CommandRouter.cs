using System.Text.Json;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.App;

public sealed record ExecResult(int ExitCode, MovePlan Plan, CommitResult? Commit);

/// <summary>热键/托盘/CLI 三个入口收敛到这里（§3）。撤销栈只有这一处写入。</summary>
public sealed class CommandRouter
{
    public int? LastSwapPartner { get; private set; }
    public event Action<ExecResult, WindowShuttleAction>? Executed;
    private readonly object _gate = new();          // 热键连按时禁止并发搬运

    /// <param name="fromMouseGesture">这一次是不是鼠标手势触发的——也就是**认光标还是认焦点**。
    ///
    /// 手势的指点设备就是指针：你按住右键那一刻已经在指着一扇窗了。其余入口（快捷键/托盘/命令行）
    /// 手不在鼠标上，指针停在上次放下它的地方，拿它当依据是把一个无关的残留状态读成意图；那几条路
    /// 认的是**焦点窗口**。完整理由写在 PlanRequest.Referent 上。</param>
    /// <param name="at">以哪一点当作"光标位置"来规划。只有方向手势会传：它必须锚在**按下点**上，
    /// 否则跨屏划动会拿松手点去找窗口，搬走目标屏上那扇毫不相干的。其余入口传 null＝读当前光标。</param>
    /// <param name="referent">显式指定要搬的那扇窗，覆盖"当场读前台窗口"。只有托盘菜单会传：
    /// 菜单一弹出来，前台就变成菜单自己了，当场读到的是它，不是用户刚才在用的那扇窗。</param>
    public ExecResult Execute(WindowShuttleAction action, int? toMonitor, bool fromMouseGesture = false,
        PointPx? at = null, nint? referent = null)
        => Guarded(action, () => action switch
        {
            WindowShuttleAction.Undo => RunUndo(),
            _ => RunPlanned(action, toMonitor, fromMouseGesture, at, referent),
        });

    /// <summary>所有对外入口的唯一出口：一把锁、一层 try/catch、一次 <see cref="Executed"/>。
    ///
    /// 五个入口（热键 lambda / 鼠标手势 / 托盘菜单 / CLI 一次性路径 / WM_COPYDATA）里，CLI 和
    /// WM_COPYDATA 各自还包着一层 try/catch，其余三个是裸调用。RunPlanned 经 UndoStore.Save 落盘，
    /// %APPDATA%\WindowShuttle 不可写就会抛——不受保护的入口会直接带走整个驻留进程，而同一条命令
    /// 走 CLI 却能干净退出码 3。
    ///
    /// 写成方法而不是「在 Execute 里放一段注释」是有代价教训的：<see cref="MoveWindowTo"/>（主窗口
    /// 拖放）曾经是第六个入口，它自己开锁、自己 Invoke，却漏了这层 catch——注释声称守卫收在一处、
    /// 所有入口一起受益，实际上有一个入口从来没被覆盖到。让它们共用同一个壳，漏不掉。</summary>
    private ExecResult Guarded(WindowShuttleAction action, Func<ExecResult> run)
    {
        lock (_gate)
        {
            ExecResult result;
            try { result = run(); }
            catch (Exception) { result = new ExecResult(3, MovePlan.NoOpPlan(NoOpReason.Error), null); }
            Executed?.Invoke(result, action);
            return result;
        }
    }

    private ExecResult RunPlanned(WindowShuttleAction action, int? toMonitor, bool fromMouseGesture = false,
        PointPx? at = null, nint? referent = null)
    {
        var monitors = MonitorProbe.GetMonitors();
        var windows = WindowProbe.GetWindows();
        var settings = App.Cfg;
        var cursor = at ?? WindowProbe.GetCursor();
        // 认光标还是认焦点，就在这一行分开。手势不给 referent（它的指点设备是指针）；
        // 其余入口给前台窗口，除非调用方已经自己快照过（托盘菜单——见 Execute 的参数说明）。
        // 救援没有"用户指着谁"可言，它按几何筛全桌面，给了 referent 反而会把它变成单窗动作。
        nint? subject = fromMouseGesture || action == WindowShuttleAction.Rescue
            ? null
            : referent ?? WindowProbe.GetForeground();
        var plan = SwapPlanner.Plan(new PlanRequest(monitors, windows, action,
            cursor, toMonitor, LastSwapPartner, settings.SkipFullscreen, subject));

        if (plan.NoOp is not null) return new ExecResult(1, plan, null);

        // 救援不写撤销栈。撤销只有一格，而它此前只被**用户主动**的操作写过——救援是拔屏时机器自己
        // 触发的，让它改写这一格，等于用一次用户没要求的搬运顶掉用户正等着撤销的那一次：
        // 整屏互换 → 觉得不对 → 还没按撤销就拔了坞站 → 两秒后救援改写快照 → 撤销只能退回那两扇被
        // 救的窗口，整屏互换永久找不回来。不给它撤销位的代价很小：救援只碰"跟所有屏零交叠"的窗口，
        // 那些窗口撤销回去就是重新推回不可见处，本来也没人想要。
        var result = action == WindowShuttleAction.Rescue
            ? CommitOnly(plan)
            : CommitWithUndo(plan, windows);
        if (plan.NewSwapPartner is int p) LastSwapPartner = p;

        // 「鼠标跟随窗口」那个设置项连同这里的坐标映射一起删了。它存在的唯一理由是"连按同一热键
        // 能继续操作同一扇窗"——而那是键盘路径当年认光标才自造出来的问题：窗口搬走了、指针留在原地，
        // 下一次按键就指着别的东西了。现在键盘认的是焦点窗口，窗口搬到哪儿焦点跟到哪儿，连按天然
        // 作用于同一扇，不需要有人在背后偷偷传送指针。
        return result;
    }

    /// <summary>撤销 = 把上一次搬运前的那份位置快照放回去。
    ///
    /// 剔除失效句柄（§11）筛掉的不只是"关掉的那几扇窗"：走 SelectMovable 而不是只判存活，是因为
    /// "跳过全屏应用"这道闸此前对撤销完全无效——存快照之后才全屏起来的窗口照样会被搬出全屏，
    /// 无响应窗口还会卡住 DeferWindowPos 那一批。
    ///
    /// 放回去这件事本身也可撤销：提交前先把**当前**位置存进撤销栈，所以撤销连按两次回到原处。</summary>
    private static ExecResult RunUndo()
    {
        var monitors = MonitorProbe.GetMonitors();
        var windows = WindowProbe.GetWindows();
        var snap = UndoStore.Load(SettingsStore.UndoPath);
        if (snap is null || snap.Entries.Count == 0)
            return new ExecResult(1, MovePlan.NoOpPlan(NoOpReason.NothingToDo), null);

        // 一份窗口列表同时回答两个问题：句柄还活着吗、这扇窗现在允许搬吗。原来是先逐条 IsWindow()
        // P/Invoke、再在 CommitWithUndo 里把整个桌面重新枚举一遍，两次问的是同一批窗口。
        //
        // 但这两个问题的答案不能混用，混用会丢数据：不可搬（无响应、在别的虚拟桌面上被 cloak、
        // 或此刻正全屏）只是**这一次**搬不了，窗口还在、快照还有意义；而 CommitWithUndo 随后会拿
        // 新计划的快照覆盖 undo.json，于是被滤掉的那几扇窗，它们搬运前的位置就此永久消失——
        // 等那个应用缓过来再按一次撤销也回不去了。所以：不可搬的**原样留在新快照里**，
        // 只有句柄真的没了才丢弃。
        var movable = SwapPlanner.SelectMovable(windows, monitors, App.Cfg.SkipFullscreen)
            .Movable.Select(f => (long)f.Hwnd).ToHashSet();
        var live = snap.Entries.Where(e => WindowProbe.IsAlive((nint)e.Hwnd)).ToList();
        var (canMove, deferred) = (live.Where(e => movable.Contains(e.Hwnd)).ToList(),
                                   live.Where(e => !movable.Contains(e.Hwnd)).ToList());
        var plan = UndoStore.ToPlan(new UndoSnapshot(canMove), monitors);
        if (plan.NoOp is not null) return new ExecResult(1, plan, null);

        // 撤销的撤销：搬动的那几扇存**当前**位置（连按两次回到原处），搬不动的那几扇把原条目
        // 原样带过去，它们的目标位置一个字节都不能变。
        var next = new UndoSnapshot([.. UndoStore.Capture(plan, windows).Entries, .. deferred]);
        UndoStore.Save(SettingsStore.UndoPath, next);
        return CommitOnly(plan);
    }

    /// <summary>§8.1 先快照再动手、§11 快照永远落盘、§9 部分失败退出码 2——三条规矩绑在一起，
    /// 三个提交点（规划动作、拖放直送、撤销/恢复）此前各抄了一遍。抄一遍就多一个漏掉存盘的机会。</summary>
    private static ExecResult CommitWithUndo(MovePlan plan, IReadOnlyList<WindowFacts> windows)
    {
        UndoStore.Save(SettingsStore.UndoPath, UndoStore.Capture(plan, windows));
        return CommitOnly(plan);
    }

    /// <summary>提交但不动撤销栈。只给机器自己触发的搬运用（见 RunPlanned 里 Rescue 那一支）。</summary>
    private static ExecResult CommitOnly(MovePlan plan)
    {
        var commit = WindowCommitter.Commit(plan);
        return new ExecResult(commit.AccessDenied + commit.OtherFailed > 0 ? 2 : 0, plan, commit);
    }

    /// <summary>主窗口拖放（Task 14）：单窗口直送指定屏。</summary>
    /// <param name="keepBelow">正在被拖动的那扇窗（＝主窗口）的句柄。搬过去的窗要露出来，但不能盖住
    /// 用户此刻还按着鼠标、正在操作的那张地图——见 MovePlan.KeepBelow。</param>
    public ExecResult MoveWindowTo(nint hwnd, int monitorIndex, nint keepBelow = 0)
        => Guarded(WindowShuttleAction.ToPrimary, () => RunMoveWindowTo(hwnd, monitorIndex, keepBelow));

    private static ExecResult RunMoveWindowTo(nint hwnd, int monitorIndex, nint keepBelow)
    {
        var monitors = MonitorProbe.GetMonitors();
        var windows = WindowProbe.GetWindows();
        // 拖放也必须过 SelectMovable 这道闸。它自己的注释就写着"这是无响应/全屏窗口的唯一关口"，
        // 而这条路原来直接从 windows 里挑句柄，把闸绕过去了：
        //   · 无响应窗口——地图上那张卡片照样画着（ProbeWindowsCached 只按 IsMovable 筛，那个判据
        //     不含 IsHung），拖过去就走 DeferWindowPos，而它会在 UI 线程上一直等那个死掉的消息泵，
        //     直到系统的挂起超时——WindowShuttle 自己的窗口跟着冻住好几秒；
        //   · 「跳过全屏应用」这个开关在这条路上等于没有，从地图上照样能把全屏游戏拖走。
        var (movable, fs, hung) = SwapPlanner.SelectMovable(windows, monitors, App.Cfg.SkipFullscreen);
        var f = movable.FirstOrDefault(w => w.Hwnd == hwnd);
        var to = monitors.FirstOrDefault(m => m.Index == monitorIndex);
        // 计数带上：跳过的原因要能出声（气泡按 SkippedFullscreen/SkippedHung 报），
        // 否则用户看到的是"我拖了，它没动，也没人告诉我为什么"。
        if (f is null || to is null)
            return new ExecResult(1, new MovePlan([], fs, hung, null, NoOpReason.NothingToDo), null);
        var from = monitors.First(m => m.Index == SwapPlanner.OwnerIndex(f, monitors));
        if (from.Index == to.Index)
            return new ExecResult(1, MovePlan.NoOpPlan(NoOpReason.AlreadyOnTarget), null);

        // 落点交给 SwapPlanner.Map——"带哪份几何走"（最大化带 NormalPosition、贴边带格位、
        // 其余等比缩放）的判断只在那一处。这里曾经手抄过一份，贴边识别加进来时它就是漏网的那条路。
        var plan = new MovePlan([SwapPlanner.Map(f, from, to)], fs, hung, null, null,
            RaiseMoved: true, KeepBelow: keepBelow);
        return CommitWithUndo(plan, windows);
    }

    /// <summary>`list`：显示器与可搬窗口，JSON、恒英文（§12）。</summary>
    public static string ListJson()
    {
        var monitors = MonitorProbe.GetMonitors();
        // 跟地图、跟每一个动作同一个口径：`list` 对外声称输出的是"可搬运的窗口"，那就必须是
        // SelectMovable 说的那一批，而不是只过了 IsMovable 的那一批。差别是无响应窗口和（开着
        // 「跳过全屏」时的）全屏应用——它们会被每一个动作静默跳过，却出现在这份 JSON 里。
        // 脚本照着它去 gather，那几扇窗纹丝不动，退出码还是 0，JSON 里没有任何线索。
        var windows = SwapPlanner.SelectMovable(
            WindowProbe.GetWindows(), monitors, App.Cfg.SkipFullscreen).Movable;
        return JsonSerializer.Serialize(new
        {
            monitors = monitors.Select(m => new
            {
                position = SwapPlanner.PositionOf(m, monitors),   // --to 收的是这个（从左到右，1 起）
                index = m.Index, device = m.DeviceName, primary = m.IsPrimary,
                rect = new[] { m.MonitorRect.Left, m.MonitorRect.Top, m.MonitorRect.Width, m.MonitorRect.Height },
                workArea = new[] { m.WorkArea.Left, m.WorkArea.Top, m.WorkArea.Width, m.WorkArea.Height },
                dpiScale = m.DpiScale, refreshHz = m.RefreshHz,
            }),
            // position 跟 monitors 那边同一个口径（从左到右，1 起），monitor 仍是系统 index。
            // 两个都给：脚本要拿 `--to` 用的那个号，本来得自己拿 monitor 去 join monitors 表——
            // 而这个应用对外只承诺一套编号（位次），偏偏窗口这边只报了另一套，等于逼调用方做
            // 一次它不该知道的换算。
            windows = windows.Select(w => new
            {
                hwnd = (long)w.Hwnd, title = w.Title, className = w.ClassName,
                position = SwapPlanner.PositionOf(
                    monitors.First(m => m.Index == SwapPlanner.OwnerIndex(w, monitors)), monitors),
                monitor = SwapPlanner.OwnerIndex(w, monitors), state = w.ShowState.ToString(),
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
