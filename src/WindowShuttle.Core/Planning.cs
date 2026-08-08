namespace WindowShuttle.Core;

/// <summary>Rescue 是唯一没有用户入口的成员：拔屏后由 App 的 DisplaySettingsChanged 监听自动触发，
/// 不进托盘菜单、不可绑定快捷键——它进这个枚举只是为了复用 CommandRouter 的守卫/撤销/通知一整条通路。</summary>
public enum WindowShuttleAction
{
    Swap, SwapTop, ToPrimary, ToNext,
    ToDirection,
    Gather, Undo, Rescue, Identify, List,
}

/// <summary>Error 不是任何 planner 产出的：它是 CommandRouter.Execute 的 catch 兜底用的占位值，
/// 复用既有的 NoOp 渲染通路（CLI 的 Describe、托盘的 NoOp_{reason} resx 查找）去报告一次执行期失败，
/// 不必为「出错了」另开一条 App 层专属的渲染路径。</summary>
/// <summary><see cref="CursorNotOnWindow"/> 和 <see cref="FocusNotMovable"/> 是同一件事的两种说法，
/// 必须分开：前者属于鼠标手势（"你指的那个位置上没有能搬的窗"），后者属于快捷键/托盘/命令行
/// （"你正在用的那扇窗搬不动"）。合并成一条的代价是至少有一条入口在说假话——键盘用户看到
/// 「鼠标下没有窗口」时，鼠标在哪儿跟他刚做的事毫无关系。</summary>
public enum NoOpReason
{
    OnlyOneMonitor, NothingToDo, CursorNotOnWindow, FocusNotMovable,
    AlreadyOnTarget, NoScreenThatWay, Error,
}

/// <param name="Cursor">规划时当作"光标在哪"的那一点。手势传按下点，其余入口传当前光标。
/// 它现在只负责**屏幕级**的判断（整屏互换/对调最前窗口的源屏）；"搬哪一扇窗"由
/// <paramref name="Referent"/> 决定。</param>
/// <param name="Referent">要搬的那扇窗，null = 按光标去找（鼠标手势的语义）。
///
/// 两条输入路径的 referent 天生不同，这个字段就是那条分界线：
///   · **鼠标手势**——指针就是指点设备。你按下右键的那一刻已经在指着一扇窗了，认焦点会闹出
///     "我指着浏览器划一下，结果我的编辑器飞走了"。而且手势把那次按下**吞掉了**，底下的程序
///     收不到、也就不会被激活，"先点一下再划"这条路本来就不存在。
///   · **快捷键 / 托盘 / 命令行**——手在键盘上，指针停在上次放下它的地方，可能在另一块屏、
///     另一扇窗上。这时候"搬光标下那扇"是把一个无关的残留状态读成了意图。系统的
///     Win+Shift+方向键、PowerToys 的 FancyZones，键盘路径一律认焦点，用户的预期也在那边。
///
/// 有 referent 时**不做回退**（它搬不动就报 FocusNotMovable，不改用光标下那扇）：回退会重新引入
/// "到底搬了哪一扇"的歧义，正是 SwapPlanner.PointedAt 那段注释里明确拒绝过的东西。</param>
public sealed record PlanRequest(
    IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowFacts> Windows,
    WindowShuttleAction Action, PointPx Cursor,
    int? TargetPosition, int? LastSwapPartner, bool SkipFullscreen,
    nint? Referent = null);

/// <param name="Target">屏幕坐标。ShowState=Normal 走 SetWindowPos；Minimized/Maximized 走 placement（§7）。</param>
/// <param name="DestWork">目标屏工作区。WindowCommitter 提交后拿它做第二遍测量纠偏——某些应用
/// （典型是 WPF 默认 WM_DPICHANGED 处理）会在我们落位后自己按 DPI 比例再缩放一次，见
/// overflow-diagnosis.md。只有 ShowState.Normal 的移动会用到它。</param>
public sealed record PlannedMove(nint Hwnd, ShowState ShowState, RectPx Target, RectPx DestWork);

/// <param name="RaiseMoved">提交后把搬动的那扇窗提到最前。只给「你指着一扇窗，把它送过去」这类动作用
/// （送去主屏 / 送去下一块屏 / 地图上拖放），批量动作绝不能开——§8 刻意保住相对层叠顺序。
///
/// 存在的理由：落位一律带 SWP_NOZORDER，窗口保持原来的层叠位置；目标屏上如果有别的窗口压在落点上，
/// 用户指着搬过去的那扇就藏在后面，看起来像"没反应"。批量搬运时这是对的（保序），单窗口时不是。</param>
/// <param name="KeepBelow">"抬到最前，但**不许盖住这一扇**"。0 = 没有这个限制。
///
/// 只有地图拖放会传：那条路上用户正拖着 WindowShuttle 自己的窗口，而抬升是会压过前台窗口的
/// （那正是它的设计——见 WindowCommitter.Raise 里两步法的理由），于是他刚放下的那扇窗会盖住
/// 他正在操作的地图。原来靠 <c>Moves[0].Hwnd != fg</c> 来防这件事，但那个条件防的是另一回事
/// （被搬的那扇自己就是焦点窗），拖放时它恒为真，形同虚设。
///
/// 传 0 关掉这个限制而不是"不抬"：窗口该露出来还是要露出来，只是别踩到你正在用的那扇。</param>
public sealed record MovePlan(
    IReadOnlyList<PlannedMove> Moves, int SkippedFullscreen, int SkippedHung,
    int? NewSwapPartner, NoOpReason? NoOp, bool RaiseMoved = false, nint KeepBelow = 0)
{
    public static MovePlan NoOpPlan(NoOpReason r) => new([], 0, 0, null, r);
}
