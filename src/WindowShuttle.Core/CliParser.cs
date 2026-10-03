namespace WindowShuttle.Core;

public sealed record CliCommand(WindowShuttleAction Action, int? ToMonitor);

public static class CliParser
{
    // §12：CLI 输出恒英文
    public const string Usage = """
        WindowShuttle — move windows between screens without dragging, on multi-monitor Windows.

        Every command acts on the CURRENT FOREGROUND WINDOW, not on wherever the mouse is
        parked -- run to-primary from a terminal and the terminal itself is what moves.
        Only the mouse gestures go by the pointer.

        usage: WindowShuttle.exe <command>
          swap [--to <n>]     swap the foreground window's monitor with the primary (or the nth)
          swap-top            swap the frontmost window of each of the two monitors
          to-primary          send the foreground window to the primary monitor
          to-next [--to <n>]  send the foreground window to the next monitor (or the nth screen)
                              --to <n> counts screens left to right, 1-based, matching the
                              app's map and the "position" field of `list` -- not the Windows
                              display number, which has no relation to where a screen sits
          gather              pull every window back to the primary monitor
          undo                restore positions from before the last move
          identify            flash each screen's number on that screen
          list                print monitors and movable windows as JSON
          --help / -h         print this help and exit 0

        exit codes: 0 done, 1 nothing to do, 2 some windows failed, 3 error
        """;

    /// <summary>「问问题」和「打错命令」必须是两种结局（batch4 R3）：--help/-h 单独出现时
    /// 打 Usage 到 stdout、退出码 0。纯函数放这里是为了能测；分流在 App.OnStartup。</summary>
    public static bool IsHelp(string[] args)
        => args.Length == 1 && args[0] is "--help" or "-h";

    public static CliCommand? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        WindowShuttleAction? action = args[0].ToLowerInvariant() switch
        {
            "swap" => WindowShuttleAction.Swap,
            "swap-top" => WindowShuttleAction.SwapTop,
            "to-primary" => WindowShuttleAction.ToPrimary,
            "to-next" => WindowShuttleAction.ToNext,
            "gather" => WindowShuttleAction.Gather,
            "undo" => WindowShuttleAction.Undo,
            "identify" => WindowShuttleAction.Identify,
            "list" => WindowShuttleAction.List,
            _ => null,
        };
        if (action is null) return null;

        if (args.Length == 1) return new CliCommand(action.Value, null);
        // 唯一合法的附加参数：swap/to-next 的 --to <n>
        if (action is WindowShuttleAction.Swap or WindowShuttleAction.ToNext
            && args.Length == 3 && args[1] == "--to" && int.TryParse(args[2], out int n))
            return new CliCommand(action.Value, n);
        return null;
    }
}
