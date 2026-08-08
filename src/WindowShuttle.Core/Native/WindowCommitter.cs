using System.Runtime.InteropServices;

namespace WindowShuttle.Core.Native;

public sealed record CommitResult(int Moved, int AccessDenied, int OtherFailed, int Corrected);

/// <summary>唯一调用 SetWindowPos/SetWindowPlacement 的地方（§7/§8/§9）。</summary>
public static class WindowCommitter
{
    public static CommitResult Commit(MovePlan plan)
    {
        var fg = Win32.GetForegroundWindow();
        int moved = 0, denied = 0, failed = 0;

        // 普通窗口批量走 DeferWindowPos（§8.2）；最大化/最小化不能进批，随后逐个。
        var normals = plan.Moves.Where(m => m.ShowState == ShowState.Normal).ToList();
        var committedNormals = new List<PlannedMove>();   // 第二遍纠偏只测真正落位成功的
        if (normals.Count > 0)
        {
            nint hdwp = Win32.BeginDeferWindowPos(normals.Count);
            bool batchOk = hdwp != 0;
            foreach (var m in normals)
            {
                if (hdwp == 0) break;
                hdwp = Win32.DeferWindowPos(hdwp, m.Hwnd, 0,
                    m.Target.Left, m.Target.Top, m.Target.Width, m.Target.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            }
            batchOk = batchOk && hdwp != 0 && Win32.EndDeferWindowPos(hdwp);
            if (batchOk) { moved += normals.Count; committedNormals.AddRange(normals); }
            else
                // 批失败（个别句柄失效即整批失效）→ 退化为逐个 SetWindowPos，拿到每个的错误码
                foreach (var m in normals)
                {
                    int err = SetPos(m);
                    Tally(err, ref moved, ref denied, ref failed);
                    if (err == 0) committedNormals.Add(m);
                }
        }

        foreach (var m in plan.Moves.Where(m => m.ShowState != ShowState.Normal))
            Tally(SetPlacement(m), ref moved, ref denied, ref failed);

        // 第二遍：测量后纠偏。诊断（overflow-diagnosis.md）确认部分应用（典型是 WPF 默认的
        // WM_DPICHANGED 处理）会在我们上面的 SetWindowPos/DeferWindowPos 落位*之后*、在同一个消息
        // 循环里按新旧显示器的 DPI 比例把自己重新缩放一次，导致最终矩形比目标屏还大——这发生在目标
        // 进程内部，我们没法事先拦截，只能提交完再读一遍实际矩形，超了就钳回去。只对刚成功落位的
        // Normal 窗口做：最大化窗口全程走 SetWindowPlacement（BuildPlacementSteps），最终边界由
        // Windows 按目标屏直接算出来，不受这个机制影响；最小化窗口压根没有落在屏幕上的矩形可言，
        // 纠正无意义。
        int corrected = 0;
        foreach (var m in committedNormals)
        {
            if (!Win32.GetWindowRect(m.Hwnd, out var r)) continue;    // 量的时候窗口没了，不算纠偏失败
            var measured = Win32.ToRect(r);
            if (!MonitorMapper.NeedsCorrection(m.DestWork, measured)) continue;   // 没超，别多按一次
            var fixedRect = MonitorMapper.ClampInto(m.DestWork, measured);
            if (Win32.SetWindowPos(m.Hwnd, 0, fixedRect.Left, fixedRect.Top,
                    fixedRect.Width, fixedRect.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE))
                corrected++;
            else
            {
                // 纠偏本身失败：按跟主提交同一套口径归类（§9 UIPI 单独算），不计入 moved/corrected。
                int err = Marshal.GetLastWin32Error();
                if (err == Win32.ERROR_ACCESS_DENIED) denied++; else failed++;
            }
        }

        // §8.5：前台还原 best-effort。热键触发时本进程有前台权限，失败不算错误。
        if (fg != 0) Win32.SetForegroundWindow(fg);

        // 单窗口动作：把刚搬过去的那扇提到最前。落位一律带 SWP_NOZORDER（保住相对层叠顺序，
        // 批量搬运要靠它），代价是目标屏上有窗口压在落点时，用户指着搬过去的那扇会藏在后面——
        // 看起来就是"没反应"。排在前台还原之后：不抢焦点（SWP_NOACTIVATE），只保证看得见。
        // 两道前提，缺一不可：
        // 1) 这一次真的搬成了（moved > 0）。搬失败（典型是 UIPI 拒绝）还去抬，等于把一扇根本没动的
        //    窗口翻到最前——用户看到的是"没搬过去，倒是莫名其妙冒到前面来了"。
        // 2) 要抬的不是此刻持有焦点的那一扇——抬它是空操作，但会白白改一次 Z 序。
        //
        // "别盖住用户正在操作的那扇窗"是**另一件事**，由 plan.KeepBelow 管，不要再往 2) 上挂。
        // 这里曾经写着"2) 顺带防住了地图拖放盖住主窗口"，那是错的：拖放时 fg 是主窗口、Moves[0] 是被
        // 拖的那扇，两者本来就不相等，条件恒真，什么都没防住。而抬升偏偏是**会**压过前台窗口的
        // （见 Raise 的两步法），于是刚放下的窗正好盖在用户还拖着的地图上。
        if (plan.RaiseMoved && plan.Moves.Count > 0 && moved > 0 && plan.Moves[0].Hwnd != fg)
            Raise(plan.Moves[0].Hwnd, plan.KeepBelow);

        return new CommitResult(moved, denied, failed, corrected);
    }

    /// <summary>把一扇窗提到最前，且**不抢焦点**。
    ///
    /// 为什么不是一句 SetWindowPos(HWND_TOP)：Windows 不让"不持有前台的进程"把窗口插到前台窗口
    /// 上面。调用**照样返回 true**，窗口却被悄悄压在前台窗口下面——没有任何错误码可查，这正是
    /// 它当初被写成一句话的原因。实测复现（三屏，目标屏被一扇铺满工作区的窗口占着且它持有前台，
    /// 用手势把另一屏的窗口划过去）：HWND_TOP 之后那扇刚搬过去的窗排在 Z 序第 3 位，用户看到的
    /// 就是"搬是搬过去了，人没到最前"。同一场景下面这个两步法稳定拿到第 0 位。
    ///
    /// 两步法为什么绕得过去：往"置顶"那一层插不受前台限制，而从置顶层降回来时，窗口落在普通层的
    /// 最顶端。两步都带 SWP_NOACTIVATE，焦点全程不动——这正是这里要的语义（只保证看得见，不抢走
    /// 你正在打字的那扇窗）。
    ///
    /// 另一条同样有效的候选是 AttachThreadInput 到前台线程再 BringWindowToTop，实测也稳定拿第 0 位，
    /// 但它要把我们的输入队列挂到别人的线程上：对方卡死时会连我们一起拖住。这个应用本来就会遇到
    /// 无响应窗口（见 MovePlan.SkippedHung），为一次抬窗承担挂起风险不划算。
    ///
    /// 本来就置顶的窗口直接跳过：它已经在所有普通窗口上面，而走一遍两步法反倒会把它**降级**成普通
    /// 窗口——那是在替用户撤销他自己设的"总在最前"。</summary>
    /// <param name="keepBelow">不为 0 时：抬到**这一扇的正下方**，而不是抬到最顶。见 MovePlan.KeepBelow。
    /// 往下插不受前台限制（那条规矩只挡"插到前台之上"），所以这条路一步就够，不需要两步法。</param>
    internal static void Raise(nint hwnd, nint keepBelow = 0)
    {
        const uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE;
        if (((uint)Win32.GetWindowLongW(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOPMOST) != 0) return;
        if (keepBelow != 0 && keepBelow != hwnd)
        {
            Win32.SetWindowPos(hwnd, keepBelow, 0, 0, 0, 0, flags);
            return;
        }
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, flags);
        Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }

    private static int SetPos(PlannedMove m)
    {
        if (Win32.SetWindowPos(m.Hwnd, 0, m.Target.Left, m.Target.Top,
                m.Target.Width, m.Target.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE))
            return 0;
        return Marshal.GetLastWin32Error();
    }

    private static int SetPlacement(PlannedMove m)
    {
        foreach (var step in BuildPlacementSteps(m))
        {
            var wp = step;   // 结构体传值；ref 要求可写的局部变量，不能直接 ref 一个只读枚举项
            if (!Win32.SetWindowPlacement(m.Hwnd, ref wp)) return Marshal.GetLastWin32Error();
        }
        return 0;
    }

    /// <summary>纯函数：一个 Minimized/Maximized 移动要依次发给 SetWindowPlacement 的 WINDOWPLACEMENT
    /// 序列。ponytail: rcNormalPosition 按屏幕坐标写。文档口径是 workspace 坐标，但 Win11 任务栏锁死
    /// 底部时两者原点一致；顶部/侧边任务栏（第三方工具改的）会偏移一个任务栏厚度。若 manual-checks
    /// 实测偏了，这里按目标屏 WorkArea-MonitorRect 差值修正。
    ///
    /// §7 最小化：一步。只改还原位置，不打扰它，没有中间态可言。
    ///
    /// §7 最大化：两步，都是 SetWindowPlacement，都带同一个 target——这是 v1 缺陷（真实机器实测：
    /// OneCommander 1080×823→900×686、Chrome 3840×2088→4608×2186，比例正好是两屏 DPI 比）修复过程中
    /// 学到的两条教训叠加：
    /// 1) 若只发一步 showCmd=SW_SHOWMAXIMIZED：窗口已经处于最大化状态时，Windows 把这当成"目标状态
    ///    没变"的空操作——rcNormalPosition 确实会被正确写入（同屏修复够用，controller 修复损坏窗口时
    ///    验证过），但窗口视觉上不会挪到新显示器，跨屏搬运直接失效（真实 150%/125%/125% 三屏上实测：
    ///    单步方案 3/3 跨屏 case 全部原地不动，仅同屏 in-place 才凑巧"work"）。必须先真的转一次状态
    ///    才能让 Windows 按新的 rcNormalPosition 重新计算落到哪块屏——这也是 v1 那版 bounce
    ///    （SetWindowPlacement(SW_SHOWNORMAL) 再 ShowWindow(SW_SHOWMAXIMIZED)）当初这么写的真实原因，
    ///    不是随手写错。
    /// 2) 但 v1 的第二步用 ShowWindow，不带 rcNormalPosition——Windows 转成最大化时，会把"此刻窗口的
    ///    实际尺寸"现场快照成新的还原尺寸；两次调用之间窗口真的短暂处于非最大化态，目标应用自己的
    ///    resize/WM_DPICHANGED 处理看见这个中间态就会抢先把窗口缩放掉（同一类缺陷 8636314 也是"我们
    ///    制造了一个中间态，应用对它做出反应"），Windows 快照到的就是这份被污染的尺寸。
    /// 这里两步都用 SetWindowPlacement、都显式带上同一个 target rcNormalPosition：第一步（showCmd
    /// 强制改成 SW_SHOWNORMAL）解决 1)，逼真实转一次状态、把窗口落到目标屏；第二步把 rcNormalPosition
    /// 再传一遍、无条件覆盖掉中间态里可能发生的任何污染，不管目标应用做了什么反应，解决 2)。在真实
    /// WPF 窗口、真实 150%/125% 硬件上验证过 125→150、150→125、同 DPI 跨屏、来回搬运四种组合，
    /// rcNormalPosition 每次都精确落在 target，窗口也确实挪到了目标屏（细节见
    /// maximized-restore-fix-report.md）。</summary>
    internal static IReadOnlyList<Win32.WINDOWPLACEMENT> BuildPlacementSteps(PlannedMove m)
    {
        var target = BuildPlacement(m);
        if (m.ShowState != ShowState.Maximized) return [target];
        var forceNormal = target;
        forceNormal.showCmd = Win32.SW_SHOWNORMAL;
        return [forceNormal, target];
    }

    internal static Win32.WINDOWPLACEMENT BuildPlacement(PlannedMove m) => new()
    {
        length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>(),
        rcNormalPosition = new Win32.RECT
            { L = m.Target.Left, T = m.Target.Top, R = m.Target.Right, B = m.Target.Bottom },
        showCmd = m.ShowState == ShowState.Minimized ? Win32.SW_SHOWMINIMIZED : Win32.SW_SHOWMAXIMIZED,
    };

    private static void Tally(int err, ref int moved, ref int denied, ref int failed)
    {
        if (err == 0) moved++;
        else if (err == Win32.ERROR_ACCESS_DENIED) denied++;   // §9 UIPI：管理员窗口
        else failed++;
    }
}
