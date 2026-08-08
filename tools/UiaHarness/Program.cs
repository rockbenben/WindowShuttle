using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;
// Bare "App"/"MainWindow"/"OverlayWindow"/"NotificationOverlay"/"Settings"/"Strings" are ambiguous
// from a namespace also nested under "WindowShuttle" (sibling-namespace lookup finds the *namespace*
// WindowShuttle.App before a `using`-imported type) — same reason the existing test project aliases
// these instead of a plain `using WindowShuttle.App;` (see MainWindowBoundsTests.cs).
using MainWindow = WindowShuttle.App.MainWindow;
using OverlayWindow = WindowShuttle.App.OverlayWindow;
using NotificationOverlay = WindowShuttle.App.NotificationOverlay;
using Settings = WindowShuttle.App.Settings;
using Strings = WindowShuttle.App.I18n.Strings;

namespace WindowShuttle.UiaHarness;

/// <summary>round4 Part 1 — a real UI Automation measurement tool, not a mock. Every rect it prints
/// comes from either (a) System.Windows.Automation.AutomationElement.BoundingRectangle, read from a
/// thread this program itself asserted is PER_MONITOR_AWARE_V2 before trusting any number (the exact
/// discipline OverlayWindow.xaml.cs's top comment and docs/manual-checks.md both describe — three
/// prior rounds were burned by a DPI-unaware measuring process), or (b) FrameworkElement.PointToScreen
/// on that same asserted thread, used only for the elements WPF gives no automation peer at all
/// (plain Border/Grid/Canvas/StackPanel — see the printed [UIA] section for whether that peer gap is
/// real on this build). Both are labeled in the output; neither is a guess.
///
/// Run directly (`dotnet run --project tools/UiaHarness -c Release`), not through the test suite —
/// it drives real, visible windows across whatever real monitors are attached right now, which is not
/// something that belongs in the regular `dotnet test` pass.</summary>
internal static class Program
{
    private const nint PmV2 = -4; // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint ctx);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

    [STAThread]
    private static int Main(string[] args)
    {
        var newCtx = SetThreadDpiAwarenessContext(PmV2);
        bool aware = newCtx != 0 && AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PmV2);
        Console.WriteLine(aware
            ? "[dpi-awareness] ASSERTED: this thread is PER_MONITOR_AWARE_V2 — numbers below are trustworthy."
            : "[dpi-awareness] FAILED to become PER_MONITOR_AWARE_V2 — refusing to print any measurement.");
        if (!aware) return 1;

        // 必须先有一个 Application。MainWindow 现在是 WPF-UI 的 FluentWindow，它和 ui:TitleBar /
        // ui:Button / 被 WPF-UI 重新定型的 CheckBox、ComboBox 一样，模板都从 Application.Current
        // .Resources 里查；进程里没有 Application 时这些控件量出来全是 (0,0)，连带外层 Grid 一起塌，
        // 于是这个工具印出的每一个矩形描述的都是一棵产品里根本不存在的可视树——而它的全部意义就是
        // "每个矩形都是真的"。测试那边由 WpfTestHost 建同样的东西，这里过去什么都没有。
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

        var monitors = MonitorProbe.GetMonitors();
        Console.WriteLine($"[monitors] {monitors.Count} real monitor(s) detected:");
        foreach (var m in monitors)
            Console.WriteLine($"  #{m.Index}{(m.IsPrimary ? " (primary)" : "")}: {m.MonitorRect.Width}x{m.MonitorRect.Height} phys @ {m.DpiScale:P0}, work area phys {Fmt(m.WorkArea)}, DIP {m.WorkArea.Width / m.DpiScale:0}x{m.WorkArea.Height / m.DpiScale:0}");
        if (monitors.Count == 0) { Console.WriteLine("no monitors — nothing to measure"); return 1; }

        // 对比度不在这里印。它归 AppThemeContrastTests / NotificationContrastTests 管，那两个测试
        // 直接读 AppTheme 的画刷、每次 dotnet test 都跑。这里原本另有一张写死调色板的表，号称
        // "copied from the same byte values the app freezes into its brushes"，实际停在换配色之前的
        // 那一版：它会把一个 6.4:1 的组合印成 FAIL，也会漏掉真正掉线的那些。同一件事在两处各写一遍，
        // 烂掉的永远是没人跑的那一份。
        Console.WriteLine("[contrast] 见 AppThemeContrastTests / NotificationContrastTests（dotnet test 里跑，读的是 AppTheme 的实际画刷）");

        // MainWindow.OnClosing() unconditionally calls SaveWindowBounds(), which writes
        // App.Cfg straight to the REAL SettingsStore.DefaultPath (%APPDATA%\WindowShuttle\settings.json)
        // regardless of which Settings object this harness constructed — window.Close() below would
        // otherwise silently overwrite the owner's real settings file with this run's throwaway test
        // bounds. Snapshot the real file now and restore it in `finally`, no matter how this run ends.
        string realSettingsPath = global::WindowShuttle.App.SettingsStore.DefaultPath;
        string? realSettingsBackup = File.Exists(realSettingsPath) ? File.ReadAllText(realSettingsPath) : null;
        try
        {
            var mode = args.Length > 0 ? args[0] : "all";

            if (mode is "all" or "main")
                foreach (var culture in new[] { "zh-CN", "en" })
                    foreach (var m in monitors)
                        MeasureMainWindow(m, culture);

            if (mode is "all" or "overlay")
                MeasureOverlay(monitors);

            if (mode is "all" or "notify")
            {
                foreach (var m in monitors)
                    MeasureNotifications(m);
                MeasureNotificationTiming(monitors[0]);
            }

            Console.WriteLine("\n[done]");
            return 0;
        }
        finally
        {
            if (realSettingsBackup is not null) File.WriteAllText(realSettingsPath, realSettingsBackup);
            else if (File.Exists(realSettingsPath)) File.Delete(realSettingsPath);
            Console.WriteLine($"[cleanup] restored real settings.json ({realSettingsPath})");
        }
    }

    // ---------------------------------------------------------------- MainWindow ----------------

    private static void MeasureMainWindow(MonitorInfo mon, string culture)
    {
        Strings.ApplyCulture(culture);
        global::WindowShuttle.App.App.Cfg = new Settings
        {
            WindowLeft = mon.WorkArea.Left + (int)(40 * mon.DpiScale),
            WindowTop = mon.WorkArea.Top + (int)(40 * mon.DpiScale),
            WindowWidth = Math.Min((int)(960 * mon.DpiScale), mon.WorkArea.Width - (int)(80 * mon.DpiScale)),
            WindowHeight = Math.Min((int)(760 * mon.DpiScale), mon.WorkArea.Height - (int)(80 * mon.DpiScale)),
        };
        var window = new MainWindow();
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();

        var hwnd = new WindowInteropHelper(window).Handle;
        Win32.GetWindowRect(hwnd, out var wr);
        var winPhys = new Rect(wr.L, wr.T, wr.R - wr.L, wr.B - wr.T);
        bool withinWork = winPhys.Left >= mon.WorkArea.Left - 0.5 && winPhys.Top >= mon.WorkArea.Top - 0.5
            && winPhys.Right <= mon.WorkArea.Right + 0.5 && winPhys.Bottom <= mon.WorkArea.Bottom + 0.5;

        Console.WriteLine($"\n=== MainWindow — monitor #{mon.Index} ({mon.MonitorRect.Width}x{mon.MonitorRect.Height}@{mon.DpiScale:P0}) culture={culture} ===");
        Console.WriteLine($"[layout] window phys rect {Fmt(winPhys)} vs monitor work area {Fmt(mon.WorkArea)} -> {(withinWork ? "within bounds" : "OVERFLOWS WORK AREA")}");

        Report.UiaPass(hwnd, "MainWindow");

        var findings = new List<Finding>();
        Walk(window, "Window", null, mon, findings);
        Report.PrintFindings(findings);

        Console.WriteLine("[text] DesiredSize (unconstrained/at-actual-width) vs ActualSize — culture-longest-string check:");
        Report.TextMetric("MouseChordCaution", window.MouseChordCaution, mon);
        foreach (var (key, strong, desc) in ActionRows(window))
        {
            Report.TextMetric($"Action_{key} (title)", strong, mon);
            Report.TextMetric($"Action_{key}_Desc", desc, mon);
        }

        window.Close();
        Pump();
    }

    /// <summary>动作行的直接子元素，顺序见 MainWindow.BuildActionRows：
    /// [0] 动作名 TextBlock、[1] 快捷键位 Border、[2] 鼠标手势位 Border、[3] 描述 TextBlock。
    ///
    /// 曾经是 [0] 一个包着名字和描述的 StackPanel——重排动作行时那一版没了，这里的强转没跟着改，
    /// 于是整个 harness 在第一块屏第一种语言上就 InvalidCastException，一条度量都印不出来。
    /// 按类型断言取，不按"我记得的形状"取：结构再变时报的是这行，不是一句无从下手的强转失败。</summary>
    private static IEnumerable<(string Key, TextBlock Strong, TextBlock Desc)> ActionRows(MainWindow window)
    {
        foreach (Border row in window.ActionList.Items)
        {
            var grid = (Grid)row.Child;
            var texts = grid.Children.OfType<TextBlock>().ToList();
            var cap = grid.Children.OfType<Border>().First();
            if (texts.Count < 2)
                throw new InvalidOperationException(
                    $"动作行的结构变了：Grid 里只有 {texts.Count} 个 TextBlock，预期 2（名字 + 描述）");
            yield return ((string)cap.Tag!, texts[0], texts[1]);
        }
    }

    // ---------------------------------------------------------------- OverlayWindow -------------

    private static void MeasureOverlay(List<MonitorInfo> monitors)
    {
        foreach (var culture in new[] { "zh-CN", "en" })
        {
            Strings.ApplyCulture(culture);
            var window = new OverlayWindow(monitors);
            window.Show();
            Pump();
            window.UpdateLayout();
            Pump();

            var hwnd = new WindowInteropHelper(window).Handle;
            Console.WriteLine($"\n=== OverlayWindow (identify, spans all monitors) culture={culture} ===");
            Report.UiaPass(hwnd, "OverlayWindow");

            var root = (Panel)window.Content;
            var findings = new List<Finding>();
            Walk(window, "Window", null, monitors[0], findings, useOwnWindowForPhysicalConversion: true);
            Report.PrintFindings(findings);

            Console.WriteLine("[per-monitor badge check] numeral physical size vs monitor DPI (should look equal-sized across monitors):");
            for (int i = 0; i < root.Children.Count && i < monitors.Count; i++)
            {
                var mon = monitors[i];
                if (root.Children[i] is not Border badge) continue;
                var numeral = (TextBlock)((StackPanel)badge.Child).Children[0];
                var badgeRect = PhysicalRect(badge);
                var numeralRect = PhysicalRect(numeral);
                double cx = mon.MonitorRect.CenterX, cy = mon.MonitorRect.CenterY;
                bool centered = Math.Abs(badgeRect.Left + badgeRect.Width / 2 - cx) < 3 && Math.Abs(badgeRect.Top + badgeRect.Height / 2 - cy) < 3;
                Console.WriteLine($"  monitor #{mon.Index} @{mon.DpiScale:P0}: badge phys {Fmt(badgeRect)} centered-on-monitor={centered}, numeral phys {numeral.ActualWidth * (numeralRect.Width / Math.Max(numeral.ActualWidth, 0.01)):0}x{numeralRect.Height:0}px (layout scale applied: {mon.DpiScale / (winScaleCache):0.00})");
            }

            window.Close();
            Pump();
        }

        // Real wall-clock timing of the fade cycle (300ms in / 700ms hold / 300ms out per source).
        Strings.ApplyCulture("zh-CN");
        var sw = Stopwatch.StartNew();
        bool closed = false;
        var timed = new OverlayWindow(monitors);
        timed.Closed += (_, _) => closed = true;
        timed.Run(null);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!closed && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
        sw.Stop();
        Console.WriteLine($"\n[timing] identify overlay: real elapsed from Show()+Run() to Closed = {sw.ElapsedMilliseconds}ms (source's own keyframes: 300 fade-in + 700 hold + 300 fade-out = 1300ms nominal)");
    }

    private static double winScaleCache = 1.0;

    // ---------------------------------------------------------------- NotificationOverlay --------

    private static void MeasureNotifications(MonitorInfo mon)
    {
        Strings.ApplyCulture("en");
        // Longest real strings the app ever puts in this card (English is the longer culture).
        string longestActionable = Strings.Lf("Toast_AccessDenied", 12);   // longest placeholder-bearing string
        string longestError = Strings.Lf("Toast_OtherFailed", 3);
        string informational = Strings.Get("NoOp_Error");

        Console.WriteLine($"\n=== NotificationOverlay — monitor #{mon.Index} ({mon.MonitorRect.Width}x{mon.MonitorRect.Height}@{mon.DpiScale:P0}) ===");
        MeasureOneNotification(mon, "actionable(EN, longest)", longestActionable, actionable: true, isError: false);
        MeasureOneNotification(mon, "error(EN)", longestError, actionable: false, isError: true);
        MeasureOneNotification(mon, "informational(EN)", informational, actionable: false, isError: false);

        Strings.ApplyCulture("zh-CN");
        MeasureOneNotification(mon, "actionable(ZH)", Strings.Lf("Toast_AccessDenied", 12), actionable: true, isError: false);

        // Replace-without-flicker check: does the outgoing card's HWND/visibility end before the
        // incoming one's Show() call returns? (Whether DWM itself paints one overlapping frame during
        // the swap is a compositor question a Stopwatch/UIA harness cannot see — flagged in the report.)
        Strings.ApplyCulture("en");
        var first = NotificationOverlay.ShowOn(mon, "first", actionable: false, isError: false);
        Pump();
        bool firstVisibleBefore = first.IsVisible;
        var second = NotificationOverlay.ShowOn(mon, "second", actionable: false, isError: false);
        Pump();
        Console.WriteLine($"[replace] first.IsVisible before second shown={firstVisibleBefore}, first.IsVisible after second shown={first.IsVisible} (false = Dismiss()/Close() on the outgoing card ran synchronously before the incoming card's Show())");
        second.Close();
        Pump();
    }

    /// <summary>Real wall-clock dwell time — does the coded 300/2400/300 (informational) and
    /// 300/6000/300 (actionable) ms actually elapse, and roughly how much of that is full-opacity
    /// reading time versus fade. "Long enough to read" itself is a human judgment this harness
    /// cannot make — it can only confirm the real numbers the judgment should be based on.</summary>
    private static void MeasureNotificationTiming(MonitorInfo mon)
    {
        Strings.ApplyCulture("en");
        foreach (var (label, text, actionable) in new[]
        {
            ("informational(EN, longest realistic)", Strings.Get("NoOp_Error"), false),
            ("actionable(EN, longest)", Strings.Lf("Toast_AccessDenied", 12), true),
        })
        {
            var sw = Stopwatch.StartNew();
            bool closed = false;
            var overlay = NotificationOverlay.ShowOn(mon, text, actionable, isError: false);
            overlay.Closed += (_, _) => closed = true;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!closed && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
            sw.Stop();
            int nominal = 300 + (actionable ? 6000 : 2400) + 300;
            Console.WriteLine($"[timing] {label}: real elapsed Show()->Closed = {sw.ElapsedMilliseconds}ms (nominal from source constants: {nominal}ms)");
        }
    }

    private static void MeasureOneNotification(MonitorInfo mon, string label, string text, bool actionable, bool isError)
    {
        var overlay = NotificationOverlay.ShowOn(mon, text, actionable, isError);
        Pump();
        overlay.UpdateLayout();
        Pump();

        var hwnd = new WindowInteropHelper(overlay).Handle;
        Win32.GetWindowRect(hwnd, out var wr);
        var winPhys = new Rect(wr.L, wr.T, wr.R - wr.L, wr.B - wr.T);
        int taskbarGuessPx = mon.MonitorRect.Bottom - mon.WorkArea.Bottom; // physical px reserved below work area
        bool collidesTaskbar = winPhys.Bottom > mon.WorkArea.Bottom + 0.5;
        bool withinWidth = winPhys.Left >= mon.WorkArea.Left - 0.5 && winPhys.Right <= mon.WorkArea.Right + 0.5;

        var message = overlay.Message;
        message.Measure(new Size(message.ActualWidth > 0 ? message.ActualWidth : double.PositiveInfinity, double.PositiveInfinity));
        bool textClips = message.DesiredSize.Height > message.ActualHeight + 0.5;
        var chevronRect = overlay.Chevron.Visibility == Visibility.Visible ? PhysicalRect(overlay.Chevron) : (Rect?)null;

        Console.WriteLine($"[{label}] text=\"{Trunc(text, 70)}\" chars={text.Length}");
        Console.WriteLine($"  card phys {Fmt(winPhys)} DIP {overlay.ActualWidth:0}x{overlay.ActualHeight:0} (MaxWidth=440 DIP) | work-area-bottom margin used, collides taskbar={collidesTaskbar}, within monitor width={withinWidth}, taskbar reserve~{taskbarGuessPx}px");
        Console.WriteLine($"  message wrap-desired {message.DesiredSize.Width:0}x{message.DesiredSize.Height:0} vs actual {message.ActualWidth:0}x{message.ActualHeight:0} -> {(textClips ? "CLIPS/TRUNCATES VERTICALLY" : "fits")}");
        Console.WriteLine($"  chevron (non-color actionable cue): {(chevronRect is { } cr ? $"visible, phys {Fmt(cr)}" : "not shown (non-actionable card)")}");
        if (actionable) Report.UiaPass(hwnd, "NotificationOverlay(actionable)");

        overlay.Close();
        Pump();
    }

    // ---------------------------------------------------------------- shared plumbing -------------

    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    internal static Rect PhysicalRect(FrameworkElement el)
    {
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(Math.Max(el.ActualWidth, 0), Math.Max(el.ActualHeight, 0)));
        return new Rect(tl, br);
    }

    internal static string Fmt(Rect r) => $"({r.Left:0},{r.Top:0})-({r.Right:0},{r.Bottom:0}) {r.Width:0}x{r.Height:0}";
    internal static string Fmt(RectPx r) => $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Width}x{r.Height}";
    internal static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    internal record Finding(string Path, string Kind, Rect Phys, string Note);

    private static readonly Type[] LeafTypes = [typeof(Button), typeof(CheckBox), typeof(ComboBox), typeof(TextBox)];

    /// <summary>Full structural walk via VisualTreeHelper + PointToScreen — covers every element
    /// (including Border/Grid/Canvas/StackPanel, which get no UI Automation peer at all; see the
    /// [UIA] pass printed separately for confirmation). Stops descending into standard controls'
    /// internal template chrome (Button/CheckBox/ComboBox/TextBox) since that's WPF's own plumbing,
    /// not this app's layout.</summary>
    internal static void Walk(FrameworkElement el, string path, Rect? parentPhys, MonitorInfo mon,
        List<Finding> findings, bool useOwnWindowForPhysicalConversion = false)
    {
        if (el.Visibility != Visibility.Visible) return;
        var rect = PhysicalRect(el);

        bool nearZero = rect.Width < 1 || rect.Height < 1;
        bool overflowsParent = parentPhys is { } pp && !pp.IsEmpty
            && (rect.Left < pp.Left - 0.75 || rect.Top < pp.Top - 0.75 || rect.Right > pp.Right + 0.75 || rect.Bottom > pp.Bottom + 0.75);

        var notes = new List<string>();
        if (nearZero) notes.Add("ZERO-SIZE");
        if (overflowsParent) notes.Add("OVERFLOWS-PARENT");

        bool interactive = el is Button or CheckBox or ComboBox || (el is Border { Focusable: true } b && b.Cursor == Cursors.Hand);
        if (interactive)
        {
            double min = 24 * mon.DpiScale;
            if (rect.Width > 0 && rect.Width < min || rect.Height > 0 && rect.Height < min)
                notes.Add($"SMALL-TARGET({rect.Width:0}x{rect.Height:0}px<{min:0}px)");
        }

        if (notes.Count > 0)
            findings.Add(new Finding(path, el.GetType().Name, rect, string.Join(' ', notes)));

        if (Array.IndexOf(LeafTypes, el.GetType()) >= 0 || LeafTypes.Any(t => t.IsInstanceOfType(el))) return;

        int n = VisualTreeHelper.GetChildrenCount(el);
        var canvasChildren = new List<(Rect Rect, string Path)>();
        for (int i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(el, i) is not FrameworkElement child) continue;
            string label = Label(child, i);
            string childPath = $"{path}/{label}";
            Walk(child, childPath, rect, mon, findings);
            if (el is Canvas) canvasChildren.Add((PhysicalRect(child), childPath));
        }

        // Overlap check limited to Canvas children (LayoutCanvas's monitor cards, OverlayWindow's
        // badge Canvas) — the one place in this app where two siblings are placed with raw
        // Canvas.Left/Top and genuinely should never intersect. Grid/StackPanel/DockPanel children
        // are either non-overlapping by construction or deliberately layered (e.g. the dashed
        // "unbound" pill's Rectangle+TextBlock share a Grid cell on purpose) — flagging those would
        // be noise, not a finding.
        for (int i = 0; i < canvasChildren.Count; i++)
            for (int j = i + 1; j < canvasChildren.Count; j++)
            {
                var inter = Rect.Intersect(canvasChildren[i].Rect, canvasChildren[j].Rect);
                if (!inter.IsEmpty && inter.Width > 0.5 && inter.Height > 0.5)
                    findings.Add(new Finding($"{canvasChildren[i].Path} ∩ {canvasChildren[j].Path}", "Overlap",
                        inter, $"OVERLAP {inter.Width:0}x{inter.Height:0}px"));
            }
    }

    private static string Label(FrameworkElement el, int index)
    {
        var autoId = AutomationProperties.GetAutomationId(el);
        if (!string.IsNullOrEmpty(autoId)) return $"{el.GetType().Name}#{autoId}";
        if (!string.IsNullOrEmpty(el.Name)) return $"{el.GetType().Name}#{el.Name}";
        if (el is TextBlock tb) return $"Text:\"{Trunc(tb.Text, 24)}\"";
        return $"{el.GetType().Name}[{index}]";
    }
}
