namespace WindowShuttle.Core;

public static class WindowFilter
{
    // §4 条件 6。ApplicationFrameWindow 不入名单：已 cloak 的宿主被条件 4 挡掉，未 cloak 的是真窗口。
    private static readonly string[] ClassBlacklist =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"];

    /// <summary>"这是桌面上一扇真窗户"——不含"能不能搬"的判断。</summary>
    private static bool IsRealWindow(WindowFacts f)
        => f.IsVisible && !f.HasOwner && !f.IsToolWindow && !f.IsCloaked
           && f.Title.Length > 0 && !ClassBlacklist.Contains(f.ClassName);

    /// <summary>§4 七个条件。IsHung 不在此判（要计入跳过统计，由 SwapPlanner 处理）。</summary>
    public static bool IsMovable(WindowFacts f) => IsRealWindow(f) && !f.IsOwnProcess;

    /// <summary>光标"指着"的那扇窗算不算数。跟 <see cref="IsMovable"/> 同一套条件，唯独**不排除
    /// 我们自己的窗口**——WindowShuttle 从不搬自己，但它挡在最上面时，用户指的就是它。
    ///
    /// 这两个谓词必须分开，否则"在能搬的里面找最上面那扇"会越过挡在前面的窗口去搬它背后那一扇。</summary>
    public static bool IsPointable(WindowFacts f) => IsRealWindow(f);

    /// <summary>§4 全屏应用：铺满某块 MonitorRect 且无标题栏/粗边框。</summary>
    public static bool IsFullscreen(WindowFacts f, IReadOnlyList<MonitorInfo> monitors)
        => !f.HasFrame && f.ShowState == ShowState.Normal
           && monitors.Any(m => m.MonitorRect == f.WindowRect);
}
