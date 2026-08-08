using System.IO;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class UndoStoreTests
{
    [Fact] public void Capture_records_pre_move_state_of_planned_windows_only()
    {
        var moved = TestData.Win(1, RectPx.FromLTWH(100, 100, 400, 300));
        var untouched = TestData.Win(2, RectPx.FromLTWH(500, 100, 400, 300));
        var plan = new MovePlan(
            [new PlannedMove(1, ShowState.Normal, RectPx.FromLTWH(2000, 100, 400, 300), default)],
            0, 0, null, null);

        var snap = UndoStore.Capture(plan, [moved, untouched]);
        var e = Assert.Single(snap.Entries);
        Assert.Equal(1, e.Hwnd);
        Assert.Equal(RectPx.FromLTWH(100, 100, 400, 300), e.Target);   // 搬运前的位置
    }

    [Fact] public void Capture_uses_normal_position_for_minimized_windows()
    {
        var min = TestData.Win(1, RectPx.FromLTWH(-32000, -32000, 160, 30),
            state: ShowState.Minimized, normalPos: RectPx.FromLTWH(2000, 100, 800, 600));
        var plan = new MovePlan(
            [new PlannedMove(1, ShowState.Minimized, RectPx.FromLTWH(100, 100, 800, 600), default)],
            0, 0, null, null);
        var snap = UndoStore.Capture(plan, [min]);
        Assert.Equal(RectPx.FromLTWH(2000, 100, 800, 600), snap.Entries[0].Target);
    }

    [Fact] public void Capture_uses_normal_position_for_maximized_windows()
    {
        // BLOCKER 2: a maximized window's WindowRect is the whole monitor. Capture must record
        // NormalPosition (the restore size) — otherwise `undo` writes the source monitor's full
        // rect into rcNormalPosition instead of the size the user actually had.
        var max = TestData.Win(1, RectPx.FromLTWH(1920, 0, 2560, 1440),
            state: ShowState.Maximized, normalPos: RectPx.FromLTWH(2100, 200, 800, 600));
        var plan = new MovePlan(
            [new PlannedMove(1, ShowState.Maximized, RectPx.FromLTWH(100, 100, 800, 600), default)],
            0, 0, null, null);
        var snap = UndoStore.Capture(plan, [max]);
        Assert.Equal(RectPx.FromLTWH(2100, 200, 800, 600), snap.Entries[0].Target);
    }

    private static readonly MonitorInfo M1 = TestData.Mon(1, 0, 0, 1920, 1080, primary: true);
    private static readonly MonitorInfo M2 = TestData.Mon(2, 1920, 0, 2560, 1440);

    [Fact] public void ToPlan_reverses_the_snapshot()
    {
        var snap = new UndoSnapshot(
            [new UndoEntry(1, ShowState.Maximized, RectPx.FromLTWH(100, 100, 800, 600))]);
        var plan = UndoStore.ToPlan(snap, [M1, M2]);
        var m = Assert.Single(plan.Moves);
        Assert.Equal((nint)1, m.Hwnd);
        Assert.Equal(ShowState.Maximized, m.ShowState);
        Assert.Equal(RectPx.FromLTWH(100, 100, 800, 600), m.Target);
    }

    [Fact] public void ToPlan_resolves_destination_work_area_from_where_the_target_rect_lands()
    {
        // 撤销把窗口 1 送回屏 1，窗口 2 送回屏 2——DestWork 得跟着 Target 落点走，不能是固定值。
        var snap = new UndoSnapshot(
            [new UndoEntry(1, ShowState.Normal, RectPx.FromLTWH(100, 100, 400, 300)),
             new UndoEntry(2, ShowState.Normal, RectPx.FromLTWH(2000, 100, 400, 300))]);
        var plan = UndoStore.ToPlan(snap, [M1, M2]);
        Assert.Equal(M1.WorkArea, plan.Moves.Single(m => m.Hwnd == 1).DestWork);
        Assert.Equal(M2.WorkArea, plan.Moves.Single(m => m.Hwnd == 2).DestWork);
    }

    [Fact] public void Serialize_roundtrips()
    {
        var snap = new UndoSnapshot(
            [new UndoEntry(42, ShowState.Minimized, RectPx.FromLTWH(-5, 10, 300, 200)),
             new UndoEntry(7, ShowState.Normal, RectPx.FromLTWH(0, 0, 1, 1))]);
        var back = UndoStore.Deserialize(UndoStore.Serialize(snap));
        Assert.NotNull(back);
        Assert.Equal(snap.Entries, back!.Entries);
    }

    [Fact] public void Deserialize_garbage_returns_null()
        => Assert.Null(UndoStore.Deserialize("{not json"));

    [Fact] public void Load_missing_file_returns_null()
        => Assert.Null(UndoStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));

    [Fact] public void Save_then_load_roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"windowshuttle-undo-{Guid.NewGuid():N}.json");
        try
        {
            var snap = new UndoSnapshot([new UndoEntry(9, ShowState.Normal, RectPx.FromLTWH(1, 2, 3, 4))]);
            UndoStore.Save(path, snap);
            Assert.Equal(snap.Entries, UndoStore.Load(path)!.Entries);
        }
        finally { File.Delete(path); }
    }
}
