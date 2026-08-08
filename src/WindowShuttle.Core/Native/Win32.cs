using System.Runtime.InteropServices;

namespace WindowShuttle.Core.Native;

internal static partial class Win32
{
    // ---- 结构 ----
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    internal const uint MONITORINFOF_PRIMARY = 1;
    internal const int ENUM_CURRENT_SETTINGS = -1;
    internal const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    internal const uint WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_TOPMOST = 0x00000008;
    internal const uint GW_OWNER = 4;
    internal const int DWMWA_CLOAKED = 14;
    internal const int SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3;
    internal const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
    internal const nint HWND_TOP = 0, HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int MDT_EFFECTIVE_DPI = 0;

    // ---- 显示器 ----
    // ponytail: GetMonitorInfo/EnumDisplaySettings 的 ref 结构体含 ByValTStr 字段，非 blittable，
    // LibraryImport 源生成器拒绝封送（SYSLIB1051）—— 这两个仍用经典 DllImport。回调委托本身
    // （EnumDisplayMonitors/EnumWindows）在这版生成器下没问题，其余全部用 LibraryImport。
    internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- 窗口枚举与事实 ----
    internal delegate bool EnumWindowsProc(nint hwnd, nint lparam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc proc, nint lparam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsHungAppWindow(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial nint GetWindow(nint hwnd, uint cmd);
    [LibraryImport("user32.dll")] internal static partial int GetWindowTextLengthW(nint hwnd);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowTextW(nint hwnd, Span<char> text, int maxCount);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassNameW(nint hwnd, Span<char> name, int maxCount);
    [LibraryImport("user32.dll")] internal static partial int GetWindowLongW(nint hwnd, int index);
    [LibraryImport("user32.dll")] internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT wp);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT p);

    // ---- 提交 ----
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT wp);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint BeginDeferWindowPos(int count);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint DeferWindowPos(nint hdwp, nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndDeferWindowPos(nint hdwp);
    [LibraryImport("user32.dll")] internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    internal static RectPx ToRect(RECT r) => new(r.L, r.T, r.R, r.B);
}
