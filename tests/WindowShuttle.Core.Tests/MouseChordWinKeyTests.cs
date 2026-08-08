using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary><c>Win</c> 这个**词**在手势字符串里必须一路无损：解析 → 位掩码 → ToString → 再解析。
///
/// 先说清楚这条测试**不**证明什么：它不证明 Win+右键 能用。实际上用不了——shell 在点击落地之前
/// 就把 Win 的按下状态收走了，录制和触发那一瞬读到的都是 0x0（作者机器上真手按出来的实测结论；
/// 用脚本合成的 Win 反而读得到 0x8，是个会骗人的假阳性，详见 MouseChordService.BeginCapture）。
/// 出厂默认因此不含 Win，README 也直说「当它不可用」。
///
/// 那还留着它做什么：<c>Win</c> 是 <see cref="HotkeyGesture"/> 位掩码里正经的一位，键盘快捷键那侧
/// 用得好好的，而两侧共用同一套解析和同一个 ToString。这几条断言守的是那套共用代码在 Win 这一位上
/// 不会静默出错——真要哪天 shell 的行为变了、或者换一种录制途径，这条路是通的，不用重写解析。</summary>
public class MouseChordWinKeyTests
{
    [Theory]
    [InlineData("Win+Right", MouseChordButton.Right)]
    [InlineData("Win+Middle", MouseChordButton.Middle)]
    [InlineData("Win+X1", MouseChordButton.X1)]
    public void Win_is_a_valid_modifier_and_survives_a_round_trip(string text, MouseChordButton button)
    {
        var g = MouseChordGesture.TryParse(text);
        Assert.NotNull(g);
        Assert.Equal(button, g!.Button);
        Assert.Equal(HotkeyGesture.ModWin, g.Modifiers);
        Assert.Equal(text, g.ToString());              // 存盘用的就是 ToString，往返必须逐字相同
    }

    // 钩子那侧拿 GetAsyncKeyState 攒出来的掩码，要能精确命中录制存下来的那条。
    [Fact]
    public void A_recorded_win_chord_resolves_when_the_win_key_is_held()
    {
        var chords = new Dictionary<WindowShuttleAction, MouseChordGesture>
        {
            [WindowShuttleAction.ToNext] = MouseChordGesture.TryParse("Win+Right")!,
        };
        Assert.Equal(WindowShuttleAction.ToNext,
            MouseChordGesture.Resolve(MouseChordButton.Right, HotkeyGesture.ModWin, chords));
        // 只按右键、没按 Win：不该命中（否则就是吞掉了所有人的右键菜单）
        Assert.Null(MouseChordGesture.Resolve(MouseChordButton.Right, 0, chords));
        // Ctrl+Win 一起按是另一个组合，精确匹配语义下不该命中
        Assert.Null(MouseChordGesture.Resolve(
            MouseChordButton.Right, HotkeyGesture.ModWin | HotkeyGesture.ModControl, chords));
    }

    [Fact]
    public void Win_combines_with_the_other_modifiers()
    {
        var g = MouseChordGesture.TryParse("Ctrl+Shift+Win+Middle");
        Assert.NotNull(g);
        Assert.Equal(HotkeyGesture.ModControl | HotkeyGesture.ModShift | HotkeyGesture.ModWin, g!.Modifiers);
        Assert.Equal("Ctrl+Shift+Win+Middle", g.ToString());
    }
}
