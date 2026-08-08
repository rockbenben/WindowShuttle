using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class HotkeyGestureTests
{
    [Fact] public void Parses_ctrl_alt_letter()
    {
        var g = HotkeyGesture.TryParse("Ctrl+Alt+X");
        Assert.NotNull(g);
        Assert.Equal(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, g!.Modifiers);
        Assert.Equal((uint)'X', g.VirtualKey);
    }

    [Fact] public void Parses_all_modifiers_and_function_keys()
    {
        var g = HotkeyGesture.TryParse("Ctrl+Shift+Win+F5");
        Assert.Equal(HotkeyGesture.ModControl | HotkeyGesture.ModShift | HotkeyGesture.ModWin,
                     g!.Modifiers);
        Assert.Equal(0x74u, g.VirtualKey);          // VK_F5
    }

    [Fact] public void Whitespace_and_case_are_forgiven()
        => Assert.NotNull(HotkeyGesture.TryParse(" ctrl + alt + p "));

    [Theory]
    [InlineData("X")]            // 无修饰键 —— 拒绝，避免吞掉普通按键
    [InlineData("Ctrl+Alt")]     // 无主键
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl+Alt+F99")]
    [InlineData("Ctrl+A+B")]     // 两个主键 —— 拒绝
    [InlineData("")]
    public void Invalid_returns_null(string s) => Assert.Null(HotkeyGesture.TryParse(s));

    [Fact] public void ToString_normalizes()
        => Assert.Equal("Ctrl+Alt+X", HotkeyGesture.TryParse("alt + ctrl + x")!.ToString());

    [Fact] public void Digits_parse()
        => Assert.Equal((uint)'3', HotkeyGesture.TryParse("Ctrl+Alt+3")!.VirtualKey);
}
