namespace WindowShuttle.Core;

/// <summary>一个顶层窗口的原始事实，由 WindowProbe 采集。所有判断放 WindowFilter/SwapPlanner。</summary>
/// <param name="HasFrame">含 WS_CAPTION 或 WS_THICKFRAME 任一（两者皆无才可能是全屏应用）。</param>
/// <param name="WindowRect">GetWindowRect；最小化窗口是 -32000 垃圾值，勿用（改用 NormalPosition）。</param>
/// <param name="ZOrder">枚举序，0 = 最前。</param>
public sealed record WindowFacts(
    nint Hwnd, string Title, string ClassName,
    bool IsVisible, bool HasOwner, bool IsToolWindow, bool IsCloaked,
    bool IsOwnProcess, bool IsHung, bool HasFrame,
    ShowState ShowState, RectPx WindowRect, RectPx NormalPosition, int ZOrder)
{
    /// <summary>"这个窗口现在视觉上在哪" —— 归属判定（OwnerIndex）该问的问题。就是 WindowRect，
    /// 唯独最小化时用 NormalPosition，因为最小化窗口的 WindowRect 是 (-32000,-32000) 垃圾值。
    /// 最大化窗口不特殊处理：它视觉上确实铺满了当前这块屏，WindowRect 就是对的答案。</summary>
    public RectPx EffectiveRect => ShowState == ShowState.Minimized ? NormalPosition : WindowRect;

    /// <summary>"搬运/撤销要带走的是哪份几何数据" —— Map/Capture 该问的问题，跟上面那题不是同一题：
    /// 最大化窗口的 WindowRect 是整块显示器，把它当成"要还原的大小"搬到新屏、写回 rcNormalPosition，
    /// 会把用户还原下来的窗口撑成新屏那么大（这正是 BLOCKER 2 的缺陷）。所以最小化 *和* 最大化都要用
    /// NormalPosition；只有 Normal 态时 WindowRect 本身就是要带走的几何。</summary>
    public RectPx RestoreRect => ShowState is ShowState.Minimized or ShowState.Maximized ? NormalPosition : WindowRect;
}
