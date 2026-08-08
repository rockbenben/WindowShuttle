using System.Text.Json;

namespace WindowShuttle.Core;

public sealed record UndoEntry(long Hwnd, ShowState ShowState, RectPx Target);
public sealed record UndoSnapshot(List<UndoEntry> Entries);

/// <summary>§11：撤销快照落盘。文件而非内存——CLI 一次性进程搬完就退，undo 只能从文件读回；
/// 驻留实例读写同一份文件，单一事实来源。HWND 是系统全局句柄，跨进程有效；
/// 失效句柄由 App 层在执行前用 IsWindow() 剔除。</summary>
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
            entries.Add(new UndoEntry(f.Hwnd, f.ShowState, rect));
        }
        return new UndoSnapshot(entries);
    }

    /// <summary>撤销落回的目标屏工作区：Target 落在哪块屏就用哪块——跟 SwapPlanner.OwnerIndex 同一条
    /// 归属规则（交叠最大，否则就近），不必知道这份快照原来是哪次搬运产生的。</summary>
    public static MovePlan ToPlan(UndoSnapshot s, IReadOnlyList<MonitorInfo> monitors)
        => new([.. s.Entries.Select(e => new PlannedMove((nint)e.Hwnd, e.ShowState, e.Target,
                    monitors.First(m => m.Index == SwapPlanner.OwnerIndex(e.Target, monitors)).WorkArea))],
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
        File.WriteAllText(path, Serialize(s));
    }

    public static UndoSnapshot? Load(string path)
    {
        try { return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null; }
        catch (IOException) { return null; }
    }
}
