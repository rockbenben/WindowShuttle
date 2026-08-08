using System.IO;

namespace WindowShuttle.App;

/// <summary>手势录制的现场记录，默认完全关闭。
///
/// 存在的理由很具体：录制这条路要同时对上三样东西——鼠标事件真的送到了那个键位区、按住的修饰键
/// 读得出来、以及读出来的组合能存下去。任何一环断了，用户看到的都是同一个现象："按了没反应"。
/// 隔着屏幕猜这三者哪一环断了，已经猜错过两次；留一条能开的记录，一次尝试就能定位。
///
/// 开关是一个文件而不是命令行参数：用户遇到问题时应用往往已经在托盘里跑着，让他建个空文件比
/// 让他带参数重启容易得多。文件不在，Log 的第一句就返回，日常运行不碰磁盘。</summary>
internal static class ChordDebug
{
    private static readonly string Flag = Path.Combine(
        Path.GetDirectoryName(SettingsStore.DefaultPath)!, "chord-debug.on");
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(SettingsStore.DefaultPath)!, "chord-debug.log");

    /// <summary>开关只在进程启动时读一次。不能每次调用都 File.Exists——这个方法会从 WH_MOUSE_LL 的
    /// 回调里调，那个回调有 300ms 的系统超时预算且必须便宜到可以忽略（见 MouseChordService 的类注释），
    /// 在里面做磁盘 I/O 正是它明令禁止的事。代价是开日志要重启一次应用，值得。</summary>
    /// <summary>公开而不是私有：调用点要用它把插值串本身也挡在门外（钩子回调里不许分配），
    /// 光在 Log 内部判断是拦不住的——那时候字符串早拼好了。</summary>
    public static readonly bool Enabled = SafeExists();

    private static bool SafeExists()
    {
        try { return File.Exists(Flag); } catch (IOException) { return false; }
    }

    /// <summary>写盘一律排队到后台线程，**绝不在调用方线程上碰磁盘**。
    ///
    /// 这条是买来的：Log 有一半调用点在 WH_MOUSE_LL 的回调里，而那个回调跑在钩子线程上、只有 300ms
    /// 预算。原来直接 File.AppendAllText，开着日志做几次手势之后，钩子就被系统静默摘掉了——之后所有
    /// 手势全部失效，进程还活着、_hook 还是非零，界面上一点异常都看不出来。实测抓到过：日志里两条
    /// 正常记录之后再无任何一行，而应用照常运行。
    ///
    /// 也就是说，开诊断这个动作本身，会把要诊断的东西弄坏——最坏的一类诊断工具。现在 Log 只做
    /// 一次入队（无磁盘、无锁竞争到 I/O 上），落盘交给一条后台线程。</summary>
    private static readonly System.Collections.Concurrent.BlockingCollection<string> Queue = Enabled ? [] : null!;

    static ChordDebug()
    {
        if (!Enabled) return;
        new Thread(Drain) { IsBackground = true, Name = "WindowShuttle chord log" }.Start();
    }

    private static void Drain()
    {
        var v = typeof(ChordDebug).Assembly.GetName().Version;
        // 头一行盖个版本戳：排查时最常见的假象是"改了没生效"，其实跑的是旧的那个 exe
        // （托盘里还驻留着老进程时，新启动的那个只会把命令转发过去）。
        Write($"{Environment.NewLine}=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} v{v} {Environment.ProcessPath}{Environment.NewLine}");
        foreach (var line in Queue.GetConsumingEnumerable()) Write(line + Environment.NewLine);
    }

    private static void Write(string text)
    {
        try { File.AppendAllText(LogPath, text); }
        catch (IOException) { /* 诊断不该反过来把应用弄崩 */ }
        catch (UnauthorizedAccessException) { }
    }

    public static void Log(string line)
    {
        if (!Enabled) return;
        try { Queue.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}"); }
        catch (InvalidOperationException) { /* 已经收尾了 */ }
    }
}
