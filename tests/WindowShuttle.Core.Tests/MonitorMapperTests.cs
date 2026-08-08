using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class MonitorMapperTests
{
    [Fact] public void Identical_workareas_keep_rect_unchanged()
    {
        var work = RectPx.FromLTWH(0, 0, 1920, 1040);
        var w = RectPx.FromLTWH(100, 200, 800, 600);
        Assert.Equal(w, MonitorMapper.MapRect(work, work, w));
    }

    [Fact] public void Center_relative_position_is_preserved()
    {
        // 源屏中心的窗口映射后仍在目标屏中心
        var src = RectPx.FromLTWH(0, 0, 1920, 1040);
        var dst = RectPx.FromLTWH(1920, 0, 2560, 1400);
        var w = RectPx.FromLTWH(760, 320, 400, 400);           // 中心 (960,520) = src 中心
        var r = MonitorMapper.MapRect(src, dst, w);
        Assert.Equal(dst.CenterX, r.CenterX);
        Assert.Equal(dst.CenterY, r.CenterY);
    }

    [Fact] public void Scale_is_uniform_min_of_both_axes()
    {
        // 1920x1040 -> 960x1040：scaleX=0.5 scaleY=1.0，取 0.5，窗口不变形
        var src = RectPx.FromLTWH(0, 0, 1920, 1040);
        var dst = RectPx.FromLTWH(0, 1080, 960, 1040);
        var w = RectPx.FromLTWH(0, 0, 800, 600);
        var r = MonitorMapper.MapRect(src, dst, w);
        Assert.Equal(400, r.Width);
        Assert.Equal(300, r.Height);
    }

    [Fact] public void Result_is_clamped_inside_destination()
    {
        // 贴右下角的窗口映射后不越界
        var src = RectPx.FromLTWH(0, 0, 1920, 1040);
        var dst = RectPx.FromLTWH(1920, 0, 1280, 680);
        var w = RectPx.FromLTWH(1520, 640, 400, 400);
        var r = MonitorMapper.MapRect(src, dst, w);
        Assert.True(r.Left >= dst.Left && r.Top >= dst.Top
                 && r.Right <= dst.Right && r.Bottom <= dst.Bottom);
    }

    [Fact] public void Window_larger_than_destination_is_shrunk_to_fit()
    {
        var src = RectPx.FromLTWH(0, 0, 3840, 2120);
        var dst = RectPx.FromLTWH(3840, 0, 1280, 680);
        var w = RectPx.FromLTWH(0, 0, 3800, 2100);
        var r = MonitorMapper.MapRect(src, dst, w);
        Assert.True(r.Width <= dst.Width && r.Height <= dst.Height);
        Assert.True(r.Left >= dst.Left && r.Top >= dst.Top);
    }

    [Fact] public void ClampInto_translates_without_resizing()
    {
        var work = RectPx.FromLTWH(0, 0, 1920, 1040);
        var w = RectPx.FromLTWH(1700, 900, 400, 300);          // 右下越界
        var r = MonitorMapper.ClampInto(work, w);
        Assert.Equal(400, r.Width);
        Assert.Equal(300, r.Height);
        Assert.True(r.Right <= work.Right && r.Bottom <= work.Bottom);
    }

    [Fact] public void ClampInto_shrinks_only_oversized_windows()
    {
        var work = RectPx.FromLTWH(0, 0, 1000, 800);
        var w = RectPx.FromLTWH(-100, -100, 1200, 900);
        var r = MonitorMapper.ClampInto(work, w);
        Assert.Equal(work, r);
    }

    // —— NeedsCorrection：WindowCommitter 第二遍要不要动手 ——
    [Fact] public void NeedsCorrection_is_false_when_measured_rect_fits_inside_work_area()
    {
        var work = RectPx.FromLTWH(0, 0, 1920, 1040);
        var measured = RectPx.FromLTWH(100, 100, 800, 600);
        Assert.False(MonitorMapper.NeedsCorrection(work, measured));
    }

    [Fact] public void NeedsCorrection_is_true_when_app_inflated_past_the_work_area()
    {
        // 125%→150%：WPF 落位后按 DPI 比例（×1.2）把自己撑大，右/下边界戳出目标屏工作区。
        var work = RectPx.FromLTWH(0, 0, 3840, 2088);
        var measured = RectPx.FromLTWH(2794, 1276, 1236, 812);   // Right=4030 > 3840, Bottom=2088 边界
        Assert.True(MonitorMapper.NeedsCorrection(work, measured));
    }

    [Fact] public void NeedsCorrection_is_false_for_a_rect_exactly_flush_with_the_edge()
    {
        // 诊断里的 F 用例：零像素余量但没有真正越界，不该多按一次 SetWindowPos。
        var work = RectPx.FromLTWH(0, 0, 3840, 2088);
        var measured = RectPx.FromLTWH(2604, 1276, 1236, 812);   // Right=3840, Bottom=2088，正好贴边
        Assert.False(MonitorMapper.NeedsCorrection(work, measured));
    }
}
