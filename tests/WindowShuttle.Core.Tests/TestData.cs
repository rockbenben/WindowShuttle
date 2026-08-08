using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

internal static class TestData
{
    // taskbar 高度从 MonitorRect 底部扣掉得 WorkArea
    public static MonitorInfo Mon(int index, int left, int top, int w, int h,
        bool primary = false, int taskbar = 40, double dpi = 1.0, int hz = 60)
        => new(index, $@"\\.\DISPLAY{index}", RectPx.FromLTWH(left, top, w, h),
               RectPx.FromLTWH(left, top, w, h - taskbar), primary, dpi, hz);

    public static WindowFacts Win(nint hwnd, RectPx rect, string title = "w", string cls = "c",
        bool visible = true, bool owner = false, bool tool = false, bool cloaked = false,
        bool own = false, bool hung = false, bool hasFrame = true,
        ShowState state = ShowState.Normal, RectPx? normalPos = null, int z = 0)
        => new(hwnd, title, cls, visible, owner, tool, cloaked, own, hung, hasFrame, state,
               rect, normalPos ?? rect, z);
}
