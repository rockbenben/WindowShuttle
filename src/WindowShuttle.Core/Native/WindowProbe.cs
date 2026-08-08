using System.Runtime.InteropServices;

namespace WindowShuttle.Core.Native;

public static class WindowProbe
{
    public static List<WindowFacts> GetWindows()
    {
        int ownPid = Environment.ProcessId;
        var result = new List<WindowFacts>();
        int z = 0;
        Win32.EnumWindows((hwnd, _) =>
        {
            // EnumWindows 自顶向下 —— 枚举序即 Z 序
            // 钳制到 512：len 来自任意进程的窗口标题长度，不可信——一个恶意/异常标题能把这行变成
            // 攻击者控制大小的 stackalloc，栈溢出是不可捕获的 StackOverflowException，而这条路径
            // 每次按热键都会跑一遍。UI 上标题本来就截到 40 字符，下游没人需要超过 512 的标题。
            int len = Math.Min(Win32.GetWindowTextLengthW(hwnd), 512);
            Span<char> title = len > 0 ? stackalloc char[len + 1] : default;
            int got = len > 0 ? Win32.GetWindowTextW(hwnd, title, len + 1) : 0;

            Span<char> cls = stackalloc char[256];
            int clsLen = Win32.GetClassNameW(hwnd, cls, 256);

            uint style = (uint)Win32.GetWindowLongW(hwnd, Win32.GWL_STYLE);
            uint ex = (uint)Win32.GetWindowLongW(hwnd, Win32.GWL_EXSTYLE);
            Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int));
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);

            var wp = new Win32.WINDOWPLACEMENT { length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
            Win32.GetWindowPlacement(hwnd, ref wp);
            Win32.GetWindowRect(hwnd, out var rect);

            result.Add(new WindowFacts(
                hwnd,
                got > 0 ? new string(title[..got]) : "",
                clsLen > 0 ? new string(cls[..clsLen]) : "",
                Win32.IsWindowVisible(hwnd),
                Win32.GetWindow(hwnd, Win32.GW_OWNER) != 0,
                (ex & Win32.WS_EX_TOOLWINDOW) != 0,
                cloaked != 0,
                pid == (uint)ownPid,
                Win32.IsHungAppWindow(hwnd),
                (style & (Win32.WS_CAPTION | Win32.WS_THICKFRAME)) != 0,
                wp.showCmd switch
                {
                    Win32.SW_SHOWMINIMIZED => ShowState.Minimized,
                    Win32.SW_SHOWMAXIMIZED => ShowState.Maximized,
                    _ => ShowState.Normal,
                },
                Win32.ToRect(rect),
                Win32.ToRect(wp.rcNormalPosition),
                z++));
            return true;
        }, 0);
        return result;
    }

    public static PointPx GetCursor()
    {
        Win32.GetCursorPos(out var p);
        return new PointPx(p.X, p.Y);
    }

    public static bool IsAlive(nint hwnd) => Win32.IsWindow(hwnd);

    /// <summary>当前前台窗口——键盘快捷键、托盘菜单、命令行这几条入口要搬的就是它。
    ///
    /// 手在键盘上时，"我要搬的那扇窗"就是我正在用的那扇；指针停在哪儿是上一次放下鼠标的残留，
    /// 拿它当依据是把一个无关的状态读成了意图（见 CommandRouter.RunPlanned 里 referent 那段）。</summary>
    public static nint GetForeground() => Win32.GetForegroundWindow();
}
