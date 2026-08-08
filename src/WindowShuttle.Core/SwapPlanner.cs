namespace WindowShuttle.Core;

public static class SwapPlanner
{
    /// <summary>窗口归属：交叠面积最大的屏；零交叠（屏外孤儿）取中心距离最近的屏。</summary>
    public static int OwnerIndex(WindowFacts f, IReadOnlyList<MonitorInfo> monitors)
        => OwnerIndex(f.EffectiveRect, monitors);        // "在哪" —— 最大化窗口按它视觉所在的屏判归属

    /// <summary>同一条归属规则，供只有落点矩形、没有 WindowFacts 的调用方复用（例如 UndoStore 解析
    /// 撤销目标落在哪块屏的工作区）——不重新发明一遍"交叠最大，否则就近"。</summary>
    public static int OwnerIndex(RectPx rect, IReadOnlyList<MonitorInfo> monitors)
    {
        var best = monitors.MaxBy(m => RectPx.OverlapArea(rect, m.MonitorRect))!;
        if (RectPx.OverlapArea(rect, best.MonitorRect) > 0) return best.Index;
        return monitors.MinBy(m =>
            Math.Abs((long)m.MonitorRect.CenterX - rect.CenterX)
          + Math.Abs((long)m.MonitorRect.CenterY - rect.CenterY))!.Index;
    }

    public static MonitorInfo MonitorAt(PointPx p, IReadOnlyList<MonitorInfo> monitors)
        => monitors.FirstOrDefault(m => m.MonitorRect.Contains(p))
           ?? monitors.MinBy(m =>
                Math.Abs((long)m.MonitorRect.CenterX - p.X)
              + Math.Abs((long)m.MonitorRect.CenterY - p.Y))!;

    public static MovePlan Plan(PlanRequest req) => req.Action switch
    {
        WindowShuttleAction.Swap => PlanSwap(req),
        WindowShuttleAction.SwapTop => PlanSwapTop(req),
        WindowShuttleAction.ToPrimary => PlanToPrimary(req),
        WindowShuttleAction.ToNext => PlanToNext(req),
        WindowShuttleAction.Gather => PlanGather(req),
        WindowShuttleAction.Rescue => PlanRescue(req),
        _ => throw new ArgumentOutOfRangeException(nameof(req), req.Action, "not a planning action"),
    };

    private static MovePlan PlanSwap(PlanRequest req)
    {
        var ms = req.Monitors;
        if (ms.Count < 2) return MovePlan.NoOpPlan(NoOpReason.OnlyOneMonitor);

        var src = SourceMonitor(req);
        bool named = req.TargetPosition is not null;             // 用户点名了目标屏（`swap --to n`）
        var dst = req.TargetPosition is int t
            ? AtPosition(ms, t) ?? ms.First(m => m.IsPrimary)
            : ms.First(m => m.IsPrimary);
        if (src.Index == dst.Index)
        {
            // 点名了目标却已经站在上面：这就是无事可做，不能替他换一个对象。
            // 原来这里不分来路一律退到"跟上次换过的那块换回来"——于是 `swap --to 3` 在第 3 块屏上
            // 执行时，换的是第 1 块，用户点了名却搬了别的屏，还报成功。没点名（默认换主屏）时那条
            // 回退才成立：那时"目标"本来就是隐含的，换回上次那块正是同键两按的撤销手感（§2.2）。
            if (named) return MovePlan.NoOpPlan(NoOpReason.AlreadyOnTarget);
            dst = (req.LastSwapPartner is int p ? ms.FirstOrDefault(m => m.Index == p) : null)
                  ?? NextMonitor(src, ms);
        }

        var (movable, skippedFs, skippedHung) = SelectMovable(req);
        var moves = new List<PlannedMove>();
        foreach (var f in movable)
        {
            int owner = OwnerIndex(f, ms);
            var (from, to) = owner == src.Index ? (src, dst)
                           : owner == dst.Index ? (dst, src) : default;
            if (from is null) continue;
            moves.Add(Map(f, from, to));
        }
        // §8：反 Z 序（后面的先动，前台窗口最后落位）。OrderByDescending 稳定排序。
        var ordered = moves.OrderByDescending(m => req.Windows.First(w => w.Hwnd == m.Hwnd).ZOrder).ToList();
        int partner = src.IsPrimary ? dst.Index : src.Index;
        return new MovePlan(ordered, skippedFs, skippedHung,
            ordered.Count > 0 ? partner : null,
            ordered.Count == 0 ? NoOpReason.NothingToDo : null);
    }

    private static MovePlan PlanSwapTop(PlanRequest req)
    {
        var ms = req.Monitors;
        if (ms.Count < 2) return MovePlan.NoOpPlan(NoOpReason.OnlyOneMonitor);
        var src = SourceMonitor(req);
        var dst = ms.First(m => m.IsPrimary);
        if (src.Index == dst.Index)
            dst = (req.LastSwapPartner is int p ? ms.FirstOrDefault(m => m.Index == p) : null)
                  ?? NextMonitor(src, ms);

        var (movable, fs, hung) = SelectMovable(req);
        var srcTop = movable.Where(f => OwnerIndex(f, ms) == src.Index).MinBy(f => f.ZOrder);
        var dstTop = movable.Where(f => OwnerIndex(f, ms) == dst.Index).MinBy(f => f.ZOrder);
        if (srcTop is null && dstTop is null)
            return new MovePlan([], fs, hung, null, NoOpReason.NothingToDo);

        var moves = new List<PlannedMove>();
        if (srcTop is not null)
            moves.Add(Map(srcTop, src, dst));
        if (dstTop is not null)
            moves.Add(Map(dstTop, dst, src));
        int partner = src.IsPrimary ? dst.Index : src.Index;
        return new MovePlan(moves, fs, hung, partner, null);
    }

    private static MovePlan PlanToPrimary(PlanRequest req)
    {
        var ms = req.Monitors;
        var primary = ms.First(m => m.IsPrimary);
        var (movable, fs, hung) = SelectMovable(req);
        var (hit, why) = Subject(req, movable);     // 手势 = 光标指着的那一扇；键盘/托盘/CLI = 焦点窗口
        if (hit is null) return new MovePlan([], fs, hung, null, why);
        if (OwnerIndex(hit, ms) == primary.Index)
            return new MovePlan([], fs, hung, null, NoOpReason.AlreadyOnTarget);
        var from = ms.First(m => m.Index == OwnerIndex(hit, ms));
        return new MovePlan([Map(hit, from, primary)], fs, hung, null, null, RaiseMoved: true);
    }

    /// <summary>光标下的窗口送往下一块屏（按排列顺序循环）；带 TargetPosition 时直送第 n 块——
    /// 这就是 CLI `to-next --to <n>` 的"移动到指定屏幕"形态，与 `swap --to` 同一套语法。</summary>
    private static MovePlan PlanToNext(PlanRequest req)
    {
        var ms = req.Monitors;
        if (ms.Count < 2) return MovePlan.NoOpPlan(NoOpReason.OnlyOneMonitor);
        var (movable, fs, hung) = SelectMovable(req);
        var (hit, why) = Subject(req, movable);
        if (hit is null) return new MovePlan([], fs, hung, null, why);
        var from = ms.First(m => m.Index == OwnerIndex(hit, ms));
        var dst = req.TargetPosition is int t
            ? AtPosition(ms, t) ?? NextMonitor(from, ms)      // App 层已校验 --to；兜底照循环走
            : NextMonitor(from, ms);
        if (dst.Index == from.Index)
            return new MovePlan([], fs, hung, null, NoOpReason.AlreadyOnTarget);
        return new MovePlan([Map(hit, from, dst)], fs, hung, null, null, RaiseMoved: true);
    }

    /// <summary>光标**指着**的那扇窗：先取最上面那一扇，再看它能不能搬；够不着就是够不着。
    ///
    /// 顺序很要紧，这正是修掉的那个缺陷：原来写的是"在能搬的窗口里找最上面那一扇"
    /// （<c>movable.Where(含光标).MinBy(ZOrder)</c>），于是挡在前面的窗口只要不可搬，就被直接跳过、
    /// 伸手去够它**背后**那一扇。实测：把光标停在 WindowShuttle 自己的窗口上按「送去下一块屏」，
    /// 被搬走的是藏在它背后、用户根本看不见也没指着的另一扇窗（我们从不搬自己，所以自己那扇不在
    /// movable 里）。同样的道理也适用于全屏应用（开着"跳过全屏"时）和无响应窗口：它们挡在最前面
    /// 时，用户指的是它们，不是它们后面那一扇。
    ///
    /// 现在最上面那扇不可搬就返回 null，调用方报「鼠标下没有窗口」——宁可什么都不做，也不搬一扇
    /// 用户没指的窗。</summary>
    private static WindowFacts? PointedAt(PlanRequest req, IReadOnlyList<WindowFacts> movable)
    {
        var top = req.Windows
            .Where(f => WindowFilter.IsPointable(f) && f.ShowState != ShowState.Minimized
                        && f.WindowRect.Contains(req.Cursor))
            .MinBy(f => f.ZOrder);
        return top is not null && movable.Any(m => m.Hwnd == top.Hwnd) ? top : null;
    }

    /// <summary>这一次要搬的那扇窗，以及搬不成时该报哪个原因。
    ///
    /// 两条路：给了 Referent 就认它（快捷键/托盘/命令行——手在键盘上，认的是焦点窗口），
    /// 没给就按光标找（鼠标手势）。理由写在 PlanRequest.Referent 上。
    ///
    /// 收敛成一个函数，是因为「送去主屏」和「送去下一块屏」此前各写了一遍取窗 + 各自报
    /// CursorNotOnWindow；加 referent 意味着要在两处同时加同一段分支和同一个新原因，而这类
    /// "同一个概念在两处各列一遍"的写法，这个文件里已经栽过两次（PointedAt 的越窗缺陷、
    /// 光标跟随的动作名白名单）。</summary>
    private static (WindowFacts? Hit, NoOpReason Reason) Subject(
        PlanRequest req, IReadOnlyList<WindowFacts> movable)
    {
        if (req.Referent is not nint h)
            return (PointedAt(req, movable), NoOpReason.CursorNotOnWindow);
        // 不回退到光标：焦点窗口搬不动就是搬不动，改搬别的等于替用户挑了一扇他没指的窗。
        return (movable.FirstOrDefault(f => f.Hwnd == h), NoOpReason.FocusNotMovable);
    }

    /// <summary>这一次动作的"源屏"。有 referent 时用它所在的屏，没有就用光标所在的屏。
    ///
    /// 整屏互换/对调最前窗口认的是**一块屏**而不是一扇窗，但"哪块屏"同样不该由停在别处的指针决定：
    /// 手在键盘上按下整屏互换时，想换的是眼前这块屏。</summary>
    private static MonitorInfo SourceMonitor(PlanRequest req)
    {
        if (req.Referent is nint h)
        {
            var f = req.Windows.FirstOrDefault(w => w.Hwnd == h);
            if (f is not null)
                return req.Monitors.First(m => m.Index == OwnerIndex(f, req.Monitors));
        }
        return MonitorAt(req.Cursor, req.Monitors);
    }

    private static MovePlan PlanGather(PlanRequest req)
    {
        var ms = req.Monitors;
        var primary = ms.First(m => m.IsPrimary);
        var (movable, fs, hung) = SelectMovable(req);
        var moves = movable
            .Where(f => OwnerIndex(f, ms) != primary.Index)
            .OrderByDescending(f => f.ZOrder)
            .Select(f => Map(f, ms.First(m => m.Index == OwnerIndex(f, ms)), primary))
            .ToList();
        return new MovePlan(moves, fs, hung, null,
            moves.Count == 0 ? NoOpReason.NothingToDo : null);
    }


    /// <summary>拔屏救援（无用户入口，见 WindowShuttleAction.Rescue）：只救「与所有屏零交叠」的窗口，
    /// 原尺寸钳进主屏工作区。已落在任何屏上的窗口一根手指都不碰——这是它与 Gather 的分界；
    /// 光标语义不参与，Cursor 字段无所谓传什么。</summary>
    private static MovePlan PlanRescue(PlanRequest req)
    {
        var ms = req.Monitors;
        var primary = ms.First(m => m.IsPrimary);
        var (movable, fs, hung) = SelectMovable(req);
        var moves = movable
            .Where(f => ms.All(m => RectPx.OverlapArea(f.EffectiveRect, m.MonitorRect) == 0))
            .OrderByDescending(f => f.ZOrder)
            .Select(f => new PlannedMove(f.Hwnd, f.ShowState,
                MonitorMapper.ClampInto(primary.WorkArea, f.RestoreRect), primary.WorkArea))
            .ToList();
        return new MovePlan(moves, fs, hung, null,
            moves.Count == 0 ? NoOpReason.NothingToDo : null);
    }

    /// <summary>一扇窗从一块屏到另一块屏的落点。公开出来是给拖放那条路（CommandRouter.MoveWindowTo）
    /// 复用的——"带哪份几何走"的判断只能有一处，它曾经在那边手抄了一份，贴边识别加进来时就会漏。</summary>
    public static PlannedMove Map(WindowFacts f, MonitorInfo from, MonitorInfo to)
    {
        // 贴边分屏的窗口带**格位**走（落在目标屏的同一个贴边位），其余带 RestoreRect 走等比缩放
        // ——最大化窗口带的是 NormalPosition，这一条不变。识别必须看 WindowRect 而不是 RestoreRect：
        // 贴边窗口的 ShowState 是 Normal，可见矩形是那半块屏，而 rcNormalPosition 存的是贴边**之前**
        // 的浮动尺寸——拿它去识别永远不会命中，这正是"搬一下就丢分屏"的根源。
        if (f.ShowState == ShowState.Normal
            && MonitorMapper.SnapSlotTarget(from.WorkArea, to.WorkArea, f.WindowRect) is RectPx slot)
            return new PlannedMove(f.Hwnd, f.ShowState, slot, to.WorkArea);

        var rect = f.RestoreRect;                        // "带哪份几何走" —— 最大化窗口带 NormalPosition
        return new PlannedMove(f.Hwnd, f.ShowState,
            MonitorMapper.MapRect(from.WorkArea, to.WorkArea, rect), to.WorkArea);
    }

    /// <summary>过滤 + 全屏/无响应跳过计数。Task 5 的其余动作复用。</summary>
    internal static (List<WindowFacts> Movable, int SkippedFullscreen, int SkippedHung)
        SelectMovable(PlanRequest req)
        => SelectMovable(req.Windows, req.Monitors, req.SkipFullscreen);

    /// <summary>「这一批窗口里，哪些是这个应用允许搬的」——唯一出处。
    ///
    /// 拆出这个不吃 PlanRequest 的重载，是因为有两条路不经过 planner 也要搬窗口：保存/恢复布局，
    /// 以及撤销。它们此前只过了 <see cref="WindowFilter.IsMovable"/>，漏掉了这里的全屏和无响应两道
    /// 闸——于是"跳过全屏应用"这个默认开的设置对恢复布局完全无效（README 承诺它排除在**所有**动作
    /// 之外），而一个无响应窗口还会被放进 DeferWindowPos 批次，卡住派发线程。</summary>
    public static (List<WindowFacts> Movable, int SkippedFullscreen, int SkippedHung) SelectMovable(
        IReadOnlyList<WindowFacts> windows, IReadOnlyList<MonitorInfo> monitors, bool skipFullscreen)
    {
        var movable = new List<WindowFacts>();
        int fs = 0, hung = 0;
        foreach (var f in windows.Where(WindowFilter.IsMovable))
        {
            if (f.IsHung) { hung++; continue; }
            if (skipFullscreen && WindowFilter.IsFullscreen(f, monitors)) { fs++; continue; }
            movable.Add(f);
        }
        return (movable, fs, hung);
    }

    /// <summary>屏幕的**排列顺序**：从左到右，同一列再从上到下。
    ///
    /// 这是全应用寻址显示器的唯一顺序——地图按它画（MainWindow.Map.cs 的 EffectiveLayout）、
    /// 「下一块屏」按它循环、「送去第 N 块屏」按它数、`屏幕编号` 按它闪、CLI 的 `--to` 也是它。
    ///
    /// 为什么不用 <see cref="MonitorInfo.Index"/>：那是 `\\.\DISPLAYn` 里的 n，Windows 给显示设备
    /// 的编号，**跟屏幕摆在哪儿没有关系**，而且换端口、插拔坞站、重装显卡驱动之后可能重排。实测的
    /// 三屏就是反例：物理上是 2 | 1 | 3（左中右），编号却是 1,2,3。曾经「下一块屏」按 Index 循环，
    /// 于是从最右那块跳回中间的主屏，用户看到的是"怎么又回主屏了"——同一个应用里两套顺序并存，
    /// 迟早会以这种形式发作。Index 现在只用来做身份（布局指纹、归属判定），不用来寻址。</summary>
    /// <summary>返回 List 而不是 IReadOnlyList：调用方要的往往是 FindIndex，接口上没有它，
    /// 于是每个调用点都得再 .ToList() 拷一遍——而集合表达式已经物化过一次了，那是白拷第二遍。</summary>
    public static List<MonitorInfo> ByPosition(IReadOnlyList<MonitorInfo> ms)
        => [.. ms.OrderBy(m => m.MonitorRect.Left).ThenBy(m => m.MonitorRect.Top)];

    /// <summary>这块屏排在第几位（1 起）。找不到返回 0。</summary>
    public static int PositionOf(MonitorInfo m, IReadOnlyList<MonitorInfo> ms)
        => ByPosition(ms).FindIndex(x => x.Index == m.Index) + 1;

    /// <summary>按排列顺序取第 <paramref name="position"/> 块（1 起）；没有那么多块就返回 null。</summary>
    public static MonitorInfo? AtPosition(IReadOnlyList<MonitorInfo> ms, int position)
    {
        var ordered = ByPosition(ms);
        return position >= 1 && position <= ordered.Count ? ordered[position - 1] : null;
    }

    /// <summary>从 <paramref name="from"/> 出发，往 (dx, dy) 指的方向找最近的一块屏；那个方向上
    /// 没有屏就返回 null。
    ///
    /// 只取主轴：|dx| &gt; |dy| 就是左右，否则上下。斜着划不做四象限判定——真实桌面上屏幕是排成行或
    /// 列的，斜向没有对应物，硬判只会把手抖解释成一次搬运。
    ///
    /// 「在那个方向上」的判据是**整块屏都在那一侧**（左边那块的右缘 ≤ 这块的左缘，以此类推），
    /// 不是"中心点在那一侧"。这条是实测撞出来的：本机三屏并排、高度分别是 1440/2160/1080 且顶部
    /// 对齐，于是第 1 块屏的中心（y=720）比第 2 块（y=1080）高——按中心点判，从第 2 块屏**往上划**
    /// 会得到左边那块屏，窗口横着飞走，而用户比划的是向上。并排的桌面上就该是"上面没有屏"。
    ///
    /// 「最近」按中心点在该轴上的距离算，而不是按排列位次的相邻关系：三块屏并排时两者一致，
    /// 但 L 形摆法（右边那块又高又靠下）下，位次上的"下一块"未必是眼睛看到的"右边那块"。
    /// 方向手势的全部价值就在于它跟眼睛看到的一致，这里必须按几何算。</summary>
    /// <param name="wrap">那个方向上没有屏时要不要绕到另一头（方向手势传 true，跟「送去下一块屏」
    /// 走到头绕回最左是同一个约定）：往右划到最右，就绕到**同一轴上最远的另一头**那块屏。
    /// 只在同一轴上有别的屏时才绕——并排桌面上往上划，竖轴上根本没有第二块屏，照样报「那边没有屏幕」，
    /// 绕横轴属于替用户改主意。</param>
    public static MonitorInfo? MonitorToward(MonitorInfo from, int dx, int dy, IReadOnlyList<MonitorInfo> ms,
        bool wrap = false)
    {
        // 45° 线：与水平的夹角不超过 45° 都算左右（>= 让正对角线也归左右）。左右是横排桌面上
        // 压倒性的常用方向，边界上的犹豫该判给它。
        bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
        int sign = horizontal ? Math.Sign(dx) : Math.Sign(dy);
        if (sign == 0) return null;

        var self = from.MonitorRect;
        var candidates = ms.Where(m => m.Index != from.Index).Where(m => horizontal
            ? (sign > 0 ? m.MonitorRect.Left >= self.Right : m.MonitorRect.Right <= self.Left)
            : (sign > 0 ? m.MonitorRect.Top >= self.Bottom : m.MonitorRect.Bottom <= self.Top));
        var hit = candidates.MinBy(m => horizontal
            ? Math.Abs(m.MonitorRect.CenterX - self.CenterX)
            : Math.Abs(m.MonitorRect.CenterY - self.CenterY));
        if (hit is not null || !wrap) return hit;

        // 绕圈：反方向上最远的那块（= 这一轴的另一头）。反方向也一块都没有，说明这一轴上就
        // 这一块屏，那才是真正的「那边没有屏幕」。
        return ms.Where(m => m.Index != from.Index).Where(m => horizontal
                ? (sign > 0 ? m.MonitorRect.Right <= self.Left : m.MonitorRect.Left >= self.Right)
                : (sign > 0 ? m.MonitorRect.Bottom <= self.Top : m.MonitorRect.Top >= self.Bottom))
            .MaxBy(m => horizontal
                ? Math.Abs(m.MonitorRect.CenterX - self.CenterX)
                : Math.Abs(m.MonitorRect.CenterY - self.CenterY));
    }

    /// <summary>桌面上「右边下一块」屏，到头绕回最左边。</summary>
    public static MonitorInfo NextMonitor(MonitorInfo cur, IReadOnlyList<MonitorInfo> ms)
    {
        var sorted = ByPosition(ms);
        int i = sorted.ToList().FindIndex(m => m.Index == cur.Index);
        return sorted[(i + 1) % sorted.Count];
    }
}
