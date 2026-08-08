using System.Runtime.InteropServices;

namespace WindowShuttle.App;

/// <summary>两扇浮层窗口（<see cref="OverlayWindow"/> 的屏幕编号、<see cref="NotificationOverlay"/>
/// 的通知卡）共用的那套 win32 边界：不抢焦点、不进 Alt-Tab、按需点击穿透，以及用物理像素直接落位。
///
/// 抽出来之前，两个文件各自声明了同一批常量和同一组 DllImport，各自手写同一段样式位运算——而
/// NotificationOverlay 的类注释已经写着"跟 OverlayWindow 共用同一套 win32 边界"。当时那句话只是
/// 描述意图，代码层面一点没共用：改了一边另一边不会跟着变，而这类差异不会在编译期暴露，只会表现为
/// 某扇窗某天突然开始抢焦点或出现在 Alt-Tab 里。现在那句话是真的。
///
/// 为什么不做成基类：两扇窗的差异在别处（一扇铺满虚拟桌面且要兜 WM_DPICHANGED，一扇只落在一块屏、
/// 创建后不动），共用的恰好只有这几行边界设置。为了三个方法拉一条继承链，会把两者真正不同的部分
/// 也捆在一起。</summary>
internal static class OverlayChrome
{
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;

    /// <summary>默认落位标志：不动 Z 序、不激活。</summary>
    public const uint KeepZOrder = 0x0004 /* SWP_NOZORDER */ | 0x0010 /* SWP_NOACTIVATE */;

    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(nint h, int i, int v);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int hgt, uint flags);

    /// <summary>两扇窗都不抢焦点、都不进 Alt-Tab（WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW）。
    ///
    /// <paramref name="clickThrough"/> 是它们唯一的分歧：能被点的那种（通知卡上"点我提权重启"那一条）
    /// 必须吃得到点击，加了 WS_EX_TRANSPARENT 就形同虚设；纯展示的那种则要让点击落到底下的程序去。
    /// NOACTIVATE 本身已经保证"收到点击也不偷焦点"，所以可点和不抢焦点并不冲突。</summary>
    public static void MakeOverlay(nint hwnd, bool clickThrough)
    {
        uint style = (uint)GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (clickThrough) style |= WS_EX_TRANSPARENT;
        SetWindowLongW(hwnd, GWL_EXSTYLE, (int)style);
    }

    /// <summary>用物理像素直接落位，绕开 WPF 的 DIP 换算——理由见 OverlayWindow 顶部那段注释。
    /// <paramref name="flags"/> 留了口子：OverlayWindow 首次落位（窗口还没显形）用 0，其余场合用
    /// <see cref="KeepZOrder"/>。这个差异是原本就有的，抽取时原样保留，没有顺手统一。</summary>
    /// <remarks>hWndInsertAfter 固定传 0（HWND_TOP）。这不是随手填的默认值：传 HWND_NOTOPMOST(-2)
    /// 会把 WS_EX_TOPMOST 抹掉，而 WPF 的 Window.Topmost 属性不跟着变——属性没变化就不触发
    /// OnTopmostChanged，样式再也贴不回来，窗口在进程剩余的生命周期里都不再置顶，代码却读起来一切
    /// 正常。这两扇窗的全部意义就是浮在别人上面，掉了这一位等于静默失效。OverlayTopmostTests 读
    /// GetWindowLongW 的真实样式位守着这一条（变异验证过：改成 -2 立刻红）。</remarks>
    public static void Place(nint hwnd, int x, int y, int w, int h, uint flags = KeepZOrder)
        => SetWindowPos(hwnd, 0 /* HWND_TOP，绝不能是 HWND_NOTOPMOST，见 remarks */, x, y, w, h, flags);
}
