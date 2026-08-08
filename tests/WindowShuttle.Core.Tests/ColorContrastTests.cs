using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>Pure-math checks for the WCAG helper tools/UiaHarness's contrast table (round4 Part 1)
/// is built on — known reference values, not the app's own palette (that table already lives in
/// Report.cs and is re-run by hand; duplicating every pair here would just be re-asserting the same
/// arithmetic against itself).</summary>
public class ColorContrastTests
{
    [Fact]
    public void Black_on_white_is_the_maximum_21_to_1()
        => Assert.Equal(21.0, ColorContrast.Ratio((0, 0, 0), (255, 255, 255)), 1);

    [Fact]
    public void Same_color_on_itself_is_1_to_1()
        => Assert.Equal(1.0, ColorContrast.Ratio((0x5A, 0x5A, 0x70), (0x5A, 0x5A, 0x70)), 3);

    [Fact]
    public void Ratio_is_order_independent()
        => Assert.Equal(
            ColorContrast.Ratio((0xE9, 0xE9, 0xF2), (0x1E, 0x1E, 0x2E)),
            ColorContrast.Ratio((0x1E, 0x1E, 0x2E), (0xE9, 0xE9, 0xF2)));

    [Fact]
    public void Fully_opaque_composite_is_just_the_foreground()
        => Assert.Equal(((byte)10, (byte)20, (byte)30),
            ColorContrast.Composite(255, 10, 20, 30, (99, 99, 99)));

    [Fact]
    public void Fully_transparent_composite_is_just_the_backdrop()
        => Assert.Equal(((byte)99, (byte)99, (byte)99),
            ColorContrast.Composite(0, 10, 20, 30, (99, 99, 99)));
}
