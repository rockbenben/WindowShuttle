using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class MouseChordGestureTests
{
    [Fact] public void Parses_ctrl_right()
    {
        var g = MouseChordGesture.TryParse("Ctrl+Right");
        Assert.NotNull(g);
        Assert.Equal(HotkeyGesture.ModControl, g!.Modifiers);
        Assert.Equal(MouseChordButton.Right, g.Button);
    }

    [Theory]
    [InlineData("Alt+Middle", MouseChordButton.Middle)]
    [InlineData("Alt+X1", MouseChordButton.X1)]
    [InlineData("Ctrl+Shift+X2", MouseChordButton.X2)]
    public void Parses_every_non_left_button(string s, MouseChordButton expected)
        => Assert.Equal(expected, MouseChordGesture.TryParse(s)!.Button);

    [Fact] public void Parses_all_four_modifiers_together()
    {
        var g = MouseChordGesture.TryParse("Ctrl+Shift+Alt+Win+Middle");
        Assert.Equal(HotkeyGesture.ModControl | HotkeyGesture.ModShift
                     | HotkeyGesture.ModAlt | HotkeyGesture.ModWin, g!.Modifiers);
    }

    [Fact] public void Whitespace_and_case_are_forgiven()
        => Assert.NotNull(MouseChordGesture.TryParse(" ctrl + right "));

    [Theory]
    [InlineData("Ctrl+Left")]       // 左键永不合法
    [InlineData("Left")]
    [InlineData("Ctrl+Alt")]        // 无按钮
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Right+Middle")] // 两个按钮
    [InlineData("")]
    public void Invalid_returns_null(string s) => Assert.Null(MouseChordGesture.TryParse(s));

    /// <summary>裸按钮现在解析得出来——但只有划动类动作准用（SettingsStore.AllowsBareButton 把关，
    /// SettingsStoreTests 钉着出厂表）。语法层面放行、语义层面由调用方把关，是因为"能不能吞掉一次
    /// 普通点击"取决于动作是点还是划，而 TryParse 只看得见字符串，看不见动作。</summary>
    [Fact] public void Bare_buttons_parse_because_stroke_actions_may_use_them()
    {
        var g = MouseChordGesture.TryParse("Right");
        Assert.NotNull(g);
        Assert.Equal(0u, g!.Modifiers);
        Assert.Equal(MouseChordButton.Right, g.Button);
        Assert.Equal("Right", g.ToString());
        Assert.Null(MouseChordGesture.TryParse("Left"));      // 左键仍然永不合法
    }

    [Fact] public void ToString_normalizes_modifier_order()
        => Assert.Equal("Ctrl+Shift+Alt+Win+Right",
            MouseChordGesture.TryParse("win + alt + right + shift + ctrl")!.ToString());

    [Fact] public void Round_trips_through_canonical_string()
    {
        var g = MouseChordGesture.TryParse("Alt+Right")!;
        Assert.Equal(g, MouseChordGesture.TryParse(g.ToString()));
    }

    private static Dictionary<WindowShuttleAction, MouseChordGesture> Chords(
        (WindowShuttleAction, string)[] bindings)
        => bindings.ToDictionary(b => b.Item1, b => MouseChordGesture.TryParse(b.Item2)!);

    [Fact] public void Resolves_bound_chord_to_its_action()
    {
        var chords = Chords([(WindowShuttleAction.ToPrimary, "Ctrl+Right"), (WindowShuttleAction.SwapTop, "Alt+Right")]);
        Assert.Equal(WindowShuttleAction.ToPrimary,
            MouseChordGesture.Resolve(MouseChordButton.Right, HotkeyGesture.ModControl, chords));
        Assert.Equal(WindowShuttleAction.SwapTop,
            MouseChordGesture.Resolve(MouseChordButton.Right, HotkeyGesture.ModAlt, chords));
    }

    [Fact] public void Unbound_button_does_not_resolve()
    {
        var chords = Chords([(WindowShuttleAction.ToPrimary, "Ctrl+Right")]);
        Assert.Null(MouseChordGesture.Resolve(MouseChordButton.Middle, HotkeyGesture.ModControl, chords));
    }

    [Fact] public void Wrong_modifier_does_not_resolve()
    {
        var chords = Chords([(WindowShuttleAction.ToPrimary, "Ctrl+Right")]);
        Assert.Null(MouseChordGesture.Resolve(MouseChordButton.Right, HotkeyGesture.ModAlt, chords));
    }

    // Ctrl+Alt+右键：两个修饰键都按住时，held 位掩码跟任何单修饰键的 chord 都不精确相等——
    // 两个都不触发，不猜一个赢家（跟 HotkeyGesture/RegisterHotKey 的精确匹配语义一致）。
    [Fact] public void Both_modifiers_held_is_ambiguous_and_resolves_to_null()
    {
        var chords = Chords([(WindowShuttleAction.ToPrimary, "Ctrl+Right"), (WindowShuttleAction.SwapTop, "Alt+Right")]);
        Assert.Null(MouseChordGesture.Resolve(
            MouseChordButton.Right, HotkeyGesture.ModControl | HotkeyGesture.ModAlt, chords));
    }

    [Fact] public void Empty_chord_table_never_resolves()
        => Assert.Null(MouseChordGesture.Resolve(
            MouseChordButton.Right, HotkeyGesture.ModControl, []));
}
