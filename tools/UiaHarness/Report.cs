using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WindowShuttle.Core;

namespace WindowShuttle.UiaHarness;

internal static class Report
{
    /// <summary>Genuine client-side UI Automation pass: AutomationElement.FromHandle + a full
    /// descendant walk, counted by control type, plus an explicit check of whether the
    /// AutomationId="MonitorCard_N" set in MainWindow.Map.cs actually surfaces to an external UIA
    /// client — the codebase comment there claims it does; this is the actual, measured answer.</summary>
    public static void UiaPass(nint hwnd, string windowLabel)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) { Console.WriteLine("[UIA] AutomationElement.FromHandle returned null"); return; }

        var all = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        var byType = new Dictionary<string, int>();
        bool anyMonitorCardId = false;
        foreach (AutomationElement el in all)
        {
            var ct = el.Current.ControlType.ProgrammaticName;
            byType[ct] = byType.GetValueOrDefault(ct) + 1;
            if (el.Current.AutomationId.StartsWith("MonitorCard_", StringComparison.Ordinal)) anyMonitorCardId = true;
        }
        Console.WriteLine($"[UIA] {windowLabel}: {all.Count} descendant automation elements — " +
            string.Join(", ", byType.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
        Console.WriteLine($"[UIA] MonitorCard_* AutomationId reachable via UI Automation: {(anyMonitorCardId ? "YES" : "NO (Border has no default automation peer — AutomationId alone does not create one; confirms/refutes the comment in MainWindow.Map.cs)")}");
    }

    public static void PrintFindings(List<Program.Finding> findings)
    {
        if (findings.Count == 0) { Console.WriteLine("[layout] no zero-size / overflow / overlap / small-target findings"); return; }
        Console.WriteLine($"[layout] {findings.Count} finding(s):");
        foreach (var f in findings)
            Console.WriteLine($"  {f.Note,-28} {f.Kind,-10} {Program.Fmt(f.Phys),-40} {f.Path}");
    }

    public static void TextMetric(string label, TextBlock tb, MonitorInfo mon)
    {
        double origW = tb.ActualWidth, origH = tb.ActualHeight;
        bool noWrap = tb.TextWrapping == System.Windows.TextWrapping.NoWrap;
        tb.Measure(noWrap
            ? new Size(double.PositiveInfinity, double.PositiveInfinity)
            : new Size(origW > 0 ? origW : double.PositiveInfinity, double.PositiveInfinity));
        bool clipsWidth = noWrap && tb.DesiredSize.Width > origW + 0.5;
        bool clipsHeight = !noWrap && tb.DesiredSize.Height > origH + 0.5;
        var parentRect = tb.Parent is FrameworkElement pfe ? Program.PhysicalRect(pfe) : (Rect?)null;
        var ownRect = Program.PhysicalRect(tb);
        bool overflowsParent = parentRect is { } pr && (ownRect.Right > pr.Right + 0.75 || ownRect.Bottom > pr.Bottom + 0.75);
        string verdict = clipsWidth || clipsHeight || overflowsParent ? "CLIP-CANDIDATE" : "ok";
        Console.WriteLine($"  {label,-26} chars={tb.Text.Length,3} wrap={tb.TextWrapping,-8} desired={tb.DesiredSize.Width:0}x{tb.DesiredSize.Height:0} actual={origW:0}x{origH:0} phys={Program.Fmt(ownRect)} -> {verdict}");
    }
}
