using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WindowShuttle.App;

/// <summary>未捕获异常的三个入口，以及它们唯一的去处：一个追加式的文本日志。
///
/// 为什么托盘应用尤其需要这个：WPF 默认在 UI 线程抛出未捕获异常时直接结束进程。对一个有主窗口的
/// 应用，用户至少看得见窗口消失；对这个应用，消失的是托盘图标——快捷键和鼠标手势一起哑掉，而屏幕上
/// 什么都没发生过。没有日志的话，连"它崩过"这件事本身都无从知道，只会被记成"这软件有时候会失灵"。
///
/// 三个入口能做的事不一样，别指望它们对称：
///   · <see cref="Application.DispatcherUnhandledException"/> —— UI 线程。唯一能救回来的：设了
///     Handled 之后进程继续跑。这个应用把它设成 true，因为一次搬窗失败不该带走整个常驻进程。
///   · <see cref="AppDomain.UnhandledException"/> —— 其他线程。进来时 CLR 已经在往下走了，
///     拦不住，只能留个堆栈。
///   · <see cref="TaskScheduler.UnobservedTaskException"/> —— 没人 await 的 Task。同上，另外它
///     由 GC 触发，时机不确定，日志里的时间戳跟出错时刻可能差很远。
///
/// 日志不引框架：一个追加一行的文件就够，跟 settings.json 同目录（绿色版跟着 exe 走的是配置目录，
/// 不是 exe 目录——见 SettingsStore.AppDir）。写日志本身也包在 try 里：崩溃处理里再抛一次异常，
/// 会把一个可恢复的错误变成一次真正的进程终止。</summary>
public partial class App
{
    private static string LogPath => Path.Combine(
        Path.GetDirectoryName(SettingsStore.DefaultPath)!, "crash.log");

    private void HookCrashHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log("UI 线程", e.Exception);
            // 只有驻留起来之后才救。少了这道判断，"救"在启动期是最坏的结果：WPF 从 dispatcher
            // 里调 OnStartup，逃出去的异常也会走到这里，而 ShutdownMode 是 OnExplicitShutdown——
            // Handled=true 之后既没人 Shutdown、也没有窗口和托盘图标，进程就那样永远挂着。
            // 走 CLI 的调用方（脚本里一句 WindowShuttle.exe swap）会跟着一起卡死，而它本来
            // 只是拿一个非零退出码。启动没走完就让它照常终止：日志已经写下了。
            if (!_resident) return;
            // 救回来：一次动作失败不该让托盘图标凭空消失。通知走应用自己那张卡，不弹系统消息框——
            // 系统消息框会抢焦点，而这个进程平时是不抢焦点的。
            e.Handled = true;
            try { NotificationOverlay.Show(I18n.Strings.Get("NoOp_Error"), isError: true); }
            catch { /* 连通知都建不起来时不要再抛，日志已经写下了 */ }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("后台线程（进程即将结束）", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("未观察的 Task", e.Exception);
            e.SetObserved();
        };
    }

    private static void Log(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{where}]  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { /* 盘满/占用：崩溃处理不能再抛 */ }
        catch (UnauthorizedAccessException) { }
    }
}
