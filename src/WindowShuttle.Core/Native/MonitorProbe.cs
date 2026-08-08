using System.Globalization;
using System.Runtime.InteropServices;

namespace WindowShuttle.Core.Native;

public static class MonitorProbe
{
    /// <summary>假的显示器清单，只给 <c>--shots</c> 那条自查路径用；生产永远是 null，走真实硬件。
    ///
    /// 加这个接缝的理由很具体：这个应用的地图是**按显示器数量和摆位画出来的**，而所有渲染验证都只能
    /// 在开发者那台机器的那一套屏上做。2 屏、4 屏、5 屏、6 屏长什么样，在此之前一次都没被渲染过——
    /// 卡片被压到多窄才放不下"2560 × 1440"、6 块屏时窗口条目还剩几行、上下两排的桌面地图会不会
    /// 挤成一条，全靠想象。而这些恰恰是这扇窗唯一的核心内容。
    ///
    /// 跟 SettingsStore.HomeOverride 同一类东西：生产代码里留一个只在自查/测试时非 null 的口子，
    /// 是因为要验的行为**发生在生产代码内部**，测试没法在自己那边绕开。</summary>
    public static List<MonitorInfo>? Override { get; set; }

    public static List<MonitorInfo> GetMonitors()
    {
        if (Override is { } fake) return fake;

        var result = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(0, 0, (nint hMon, nint _, ref Win32.RECT _, nint _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(hMon, ref info)) return true;

            // §1 屏号：\\.\DISPLAYn 的 n。解析失败给 0——不该发生，但别崩。
            int index = int.TryParse(info.szDevice.TrimStart('\\', '.').Replace("DISPLAY", ""),
                NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

            double dpi = Win32.GetDpiForMonitor(hMon, Win32.MDT_EFFECTIVE_DPI, out var dx, out _) == 0
                ? dx / 96.0 : 1.0;

            var dev = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
            int hz = Win32.EnumDisplaySettings(info.szDevice, Win32.ENUM_CURRENT_SETTINGS, ref dev)
                ? (int)dev.dmDisplayFrequency : 0;

            result.Add(new MonitorInfo(index, info.szDevice,
                Win32.ToRect(info.rcMonitor), Win32.ToRect(info.rcWork),
                (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0, dpi, hz));
            return true;
        }, 0);
        return [.. result.OrderBy(m => m.Index)];
    }
}
