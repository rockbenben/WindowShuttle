namespace WindowShuttle.Core;

/// <summary>一块显示器。Index = 设备名 \\.\DISPLAYn 的 n（§1 屏号），叠加层/主窗口/CLI 三处同源。</summary>
public sealed record MonitorInfo(
    int Index, string DeviceName, RectPx MonitorRect, RectPx WorkArea,
    bool IsPrimary, double DpiScale, int RefreshHz);
