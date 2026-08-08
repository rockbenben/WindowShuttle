namespace WindowShuttle.Core;

public static class MonitorMapper
{
    /// <summary>贴边分屏的窗口，跨屏搬运时要落在目标屏的**同一个贴边位**上，不走等比缩放。
    ///
    /// 不保这一条的话，左半屏的窗口搬到长宽比不同的屏上会变成一扇"大约一半"的自由浮动窗——
    /// 分屏这个状态在搬运里被静默丢掉了。曾经试过把左右方向交给系统的 Win+Shift+←/→ 来保它，
    /// 一天内又删掉（代价是每次都抢焦点、上下两个方向系统没有对应键；见 App.RunStroke 的注释）；
    /// 在自己的映射里按几何保，四个方向一致、不抢焦点。
    ///
    /// 识别是纯几何的：窗口矩形的四条边都落在某个格位（左右半、上下半、四角、竖三分）的理想边
    /// 附近就算。容差 32px 盖住两样东西——DWM 的隐形拉伸边框（贴边窗口的 GetWindowRect 会比
    /// 格位各边多出 8~16px，实测）和高 DPI 下更宽的边框。代价是一扇碰巧被手动摆成精确半屏的
    /// 窗口也会被吸到格位上，但那种窗口本来就该被读成"贴着半屏"。
    ///
    /// 落点是**恰好**的格位矩形，不带源窗口那圈边框悬出——悬出会伸出工作区，提交后的第二遍
    /// 纠偏（WindowCommitter 按 DestWork 钳制）会把它推回来，两边打架。代价是比原生贴边多一圈
    /// 几像素的缝；Windows 内部的 Snap 分组账本我们本来就碰不到，几何对了就是这一层能给的全部。
    ///
    /// 返回 null = 不在任何格位上，调用方走 <see cref="MapRect"/> 的等比缩放。</summary>
    public static RectPx? SnapSlotTarget(RectPx srcWork, RectPx dstWork, RectPx w)
    {
        const int Tol = 32;
        // (x, y, w, h)：格位在工作区里的分数坐标。列出的就是支持的全部——
        // Win11 花式布局（1/4-1/2-1/4 三列之类）不在内，撞上了走等比缩放，不会出错只会不贴。
        ReadOnlySpan<(double X, double Y, double W, double H)> slots =
        [
            (0, 0, 0.5, 1), (0.5, 0, 0.5, 1),                    // 左右半
            (0, 0, 1, 0.5), (0, 0.5, 1, 0.5),                    // 上下半
            (0, 0, 0.5, 0.5), (0.5, 0, 0.5, 0.5),                // 四角
            (0, 0.5, 0.5, 0.5), (0.5, 0.5, 0.5, 0.5),
            (0, 0, 1 / 3.0, 1), (1 / 3.0, 0, 1 / 3.0, 1), (2 / 3.0, 0, 1 / 3.0, 1),   // 竖三分
        ];
        foreach (var s in slots)
        {
            var ideal = At(srcWork, s);
            if (Math.Abs(w.Left - ideal.Left) <= Tol && Math.Abs(w.Top - ideal.Top) <= Tol
                && Math.Abs(w.Right - ideal.Right) <= Tol && Math.Abs(w.Bottom - ideal.Bottom) <= Tol)
                return At(dstWork, s);
        }
        return null;

        static RectPx At(RectPx work, (double X, double Y, double W, double H) s) => new(
            work.Left + (int)Math.Round(work.Width * s.X),
            work.Top + (int)Math.Round(work.Height * s.Y),
            work.Left + (int)Math.Round(work.Width * (s.X + s.W)),
            work.Top + (int)Math.Round(work.Height * (s.Y + s.H)));
    }

    /// <summary>§6：等比缩放（min 两轴）+ 按窗口中心相对位置映射 + 钳制进目标工作区。
    /// 前提：srcWork、dstWork 宽高须为正——真实 MonitorInfo.WorkArea（来自 Win32 GetMonitorInfo）恒满足。</summary>
    public static RectPx MapRect(RectPx srcWork, RectPx dstWork, RectPx w)
    {
        double s = Math.Min((double)dstWork.Width / srcWork.Width,
                            (double)dstWork.Height / srcWork.Height);
        int newW = Math.Min((int)Math.Round(w.Width * s), dstWork.Width);
        int newH = Math.Min((int)Math.Round(w.Height * s), dstWork.Height);

        double cx = (w.CenterX - srcWork.Left) / (double)srcWork.Width;
        double cy = (w.CenterY - srcWork.Top) / (double)srcWork.Height;
        int newL = (int)Math.Round(dstWork.Left + cx * dstWork.Width - newW / 2.0);
        int newT = (int)Math.Round(dstWork.Top + cy * dstWork.Height - newH / 2.0);

        newL = Math.Clamp(newL, dstWork.Left, dstWork.Right - newW);
        newT = Math.Clamp(newT, dstWork.Top, dstWork.Bottom - newH);
        return RectPx.FromLTWH(newL, newT, newW, newH);
    }

    /// <summary>纠偏（§2.2）：尺寸不动，平移进工作区；比工作区大才缩到工作区。</summary>
    public static RectPx ClampInto(RectPx work, RectPx w)
    {
        int newW = Math.Min(w.Width, work.Width);
        int newH = Math.Min(w.Height, work.Height);
        int newL = Math.Clamp(w.Left, work.Left, work.Right - newW);
        int newT = Math.Clamp(w.Top, work.Top, work.Bottom - newH);
        return RectPx.FromLTWH(newL, newT, newW, newH);
    }

    /// <summary>WindowCommitter 提交后测量到的矩形要不要纠偏：整个矩形已经落在工作区内（ClampInto
    /// 是恒等变换）就不用——不该为了没超的窗口白白多按一次 SetWindowPos。</summary>
    public static bool NeedsCorrection(RectPx work, RectPx measured) => ClampInto(work, measured) != measured;
}
