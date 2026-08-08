namespace WindowShuttle.Core;

/// <summary>Pure WCAG 2.x contrast-ratio arithmetic — no UI, no I/O, so both the UIA measurement
/// harness (tools/UiaHarness) and the regression tests can share one implementation instead of each
/// copying the same formula. Colors are ARGB bytes; <see cref="Composite"/> handles the app's several
/// semi-transparent panels (cards/badges laid over a background that isn't itself part of the app).</summary>
public static class ColorContrast
{
    private static double Linearize(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG relative luminance, 0 (black) .. 1 (white).</summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
        => 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);

    /// <summary>Contrast ratio between two opaque colors, 1:1 .. 21:1 (WCAG 2.x formula).</summary>
    public static double Ratio((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        double la = RelativeLuminance(a.R, a.G, a.B) + 0.05;
        double lb = RelativeLuminance(b.R, b.G, b.B) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    /// <summary>Alpha-composites a translucent foreground (fg, alpha 0..255) over an opaque backdrop —
    /// e.g. the notification card's #E61E1E2E over whatever is on screen behind it.</summary>
    public static (byte R, byte G, byte B) Composite(byte fgA, byte fgR, byte fgG, byte fgB, (byte R, byte G, byte B) backdrop)
    {
        double a = fgA / 255.0;
        byte Mix(byte fg, byte bg) => (byte)Math.Round(fg * a + bg * (1 - a));
        return (Mix(fgR, backdrop.R), Mix(fgG, backdrop.G), Mix(fgB, backdrop.B));
    }
}
