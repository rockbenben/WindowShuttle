using System.Text.Json;

namespace WindowShuttle.Core;

public sealed record UndoEntry(long Hwnd, ShowState ShowState, RectPx Target, WindowIdentity? Identity = null);
public sealed record UndoSnapshot(List<UndoEntry> Entries);

/// <summary>§11：撤销快照落盘。文件而非内存——CLI 一次性进程搬完就退，undo 只能从文件读回；
/// 驻留实例读写同一份文件，单一事实来源。句柄会被复用，撤销还必须验证进程生命周期和窗口实例标记。
/// 旧条目允许读取，缺失身份时不能据此移动窗口。</summary>
public static class UndoStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>对计划里将被搬动的窗口，记下搬运前的位置与状态。</summary>
    public static UndoSnapshot Capture(MovePlan plan, IReadOnlyList<WindowFacts> facts)
    {
        var byHwnd = facts.ToDictionary(f => f.Hwnd);
        var entries = new List<UndoEntry>();
        foreach (var m in plan.Moves)
        {
            if (!byHwnd.TryGetValue(m.Hwnd, out var f)) continue;
            var rect = f.RestoreRect;                    // "带哪份几何走" —— 最大化窗口也要存 NormalPosition
            var identity = m.ExpectedIdentity;
            entries.Add(new UndoEntry(f.Hwnd, f.ShowState, rect, identity));
        }
        return new UndoSnapshot(entries);
    }

    /// <summary>撤销落回的目标屏工作区：Target 落在哪块屏就用哪块——跟 SwapPlanner.OwnerIndex 同一条
    /// 归属规则（交叠最大，否则就近），不必知道这份快照原来是哪次搬运产生的。</summary>
    /// <remarks>所有撤销计划要求原生提交器校验身份，包含直接读取的旧快照。</remarks>
    public static MovePlan ToPlan(UndoSnapshot s, IReadOnlyList<MonitorInfo> monitors)
        => new([.. s.Entries.Select(e => new PlannedMove((nint)e.Hwnd, e.ShowState, e.Target,
                    monitors.First(m => m.Index == SwapPlanner.OwnerIndex(e.Target, monitors)).WorkArea)
                    { ExpectedIdentity = e.Identity, RequireIdentity = true })],
               0, 0, null, s.Entries.Count == 0 ? NoOpReason.NothingToDo : null);

    public static string Serialize(UndoSnapshot s) => JsonSerializer.Serialize(s, Opts);

    public static UndoSnapshot? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<UndoSnapshot>(json); }
        catch (JsonException) { return null; }
    }

    public static void Save(string path, UndoSnapshot s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(s));
        File.Move(temporary, path, overwrite: true);
    }

    public static UndoSnapshot? Load(string path)
    {
        try
        {
            // A failed final replacement retains the pre-move snapshot for recovery.
            string pending = path + ".pending";
            if (File.Exists(pending) && Deserialize(File.ReadAllText(pending)) is { } recovery) return recovery;
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }
        catch (IOException) { return null; }
    }
}
