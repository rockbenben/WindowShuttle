using System.Runtime.InteropServices;
using System.Windows.Interop;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;

namespace WindowShuttle.App;

/// <summary>只读消息窗（message-only HwndSource）以及流经它的一切：另一个实例投递过来的
/// WM_COPYDATA 命令、以及系统主题变更通知。SendCommand 是同一条 IPC 的发送端，放在一起才能
/// 一眼看出两端对 cbData 的约定必须一致（§relay-bug）。</summary>
public partial class App
{
    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT { public nint dwData; public int cbData; public nint lpData; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hwnd, uint msg, nint wp, ref COPYDATASTRUCT lp);

    // round4 Part 1: WPF-UI's own theme-change notification hooks a real Window (SystemThemeWatcher.
    // Watch requires one — see the comment on CreateSink below for why that doesn't fit this app's
    // tray-resident shape). Subscribing here instead means AppTheme (the app's own bespoke palette;
    // WPF-UI's Changed event only restyles its own controls) follows every Apply/ApplySystemTheme call
    // regardless of whether any window currently exists.
    private const int WM_SYSCOLORCHANGE = 0x0015, WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint ext);

    private void CreateSink()
    {
        var p = new HwndSourceParameters(SinkTitle) { Width = 0, Height = 0, WindowStyle = 0 };
        _sink = new HwndSource(p);
        _sink.AddHook(SinkHook);
        SinkHwnd = _sink.Handle;
        // 放行低完整性发来的 WM_COPYDATA（MSGFLT_ALLOW=1）。没有这一句，驻留实例一旦以管理员身份
        // 运行（托盘的「以管理员身份重启」或提权自启那条计划任务），普通权限终端里的每一条 CLI 命令
        // 都会被 UIPI 静默丢弃——不报错、不超时，就是没反应，像程序坏了。第二个实例的"唤出主窗口"
        // 投递走的也是这条消息，一起修。非提权时这句是无害的空操作。
        ChangeWindowMessageFilterEx(SinkHwnd, WM_COPYDATA, 1, 0);
    }

    private nint SinkHook(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        // round4 Part 1: the same four messages SystemThemeWatcher.Watch listens for on a real Window
        // — this app's sink is a message-only HwndSource (CreateSink), not a Window, so it can't be
        // passed to that API. Re-deriving from the system here instead of hand-decoding wp/lp keeps
        // this in step with whatever WPF-UI itself considers "the theme" (dark/light + accent), and
        // fires ApplicationThemeManager.Changed -> AppTheme.Apply the same way a Watch()-ed window
        // would, so it works even while the app has no window open at all (dormant in the tray).
        if (msg is WM_THEMECHANGED or WM_DWMCOLORIZATIONCOLORCHANGED or WM_SYSCOLORCHANGE
            || (msg == WM_SETTINGCHANGE && Marshal.PtrToStringUni(lp) == "ImmersiveColorSet"))
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
            return 0;
        }
        if (msg != WM_COPYDATA) return 0;
        handled = true;
        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lp);
        string text = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) ?? "";
        try
        {
            // 整段分发都在守卫内——ShowMain() 会建窗口/加载 XAML，CliParser.Parse 和 Router.Execute
            // 都能抛；这是原生 WndProc 钩子，逃出去的异常会直接带走整个驻留进程（比一次性模式重得多）。
            if (text == "show")
            {
                ShowMain();
                return 0;
            }
            var cmd = CliParser.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (cmd is null) return 3;
            if (cmd.Action == WindowShuttleAction.Identify) { OverlayWindow.ShowAll(shutdownAfter: false); return 0; }
            return Router.Execute(cmd.Action, cmd.ToMonitor).ExitCode;   // SendMessage 返回值 = 退出码（§2.3）
        }
        catch (Exception)
        {
            return 3;
        }
    }

    private static int SendCommand(nint sink, string text)
    {
        // cbData 已经携带精确长度；不能再拼 NUL——两参数版 PtrToStringUni 按长度硬拷贝，不会在 NUL 处停，
        // 拼了就会在接收端多出一个字面 '\0' 字符，导致 verb 永远匹配不上任何 CliParser 分支（§relay-bug）。
        var bytes = text.ToCharArray();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var cds = new COPYDATASTRUCT
                { dwData = 1, cbData = bytes.Length * 2, lpData = handle.AddrOfPinnedObject() };
            return (int)SendMessageW(sink, WM_COPYDATA, 0, ref cds);
        }
        finally { handle.Free(); }
    }
}
