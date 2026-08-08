using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.App;

/// <summary>两个开发期自查开关。都挂在 <c>OnStartup</c> 里、且在单实例互斥体之前——托盘里正在用的
/// 实例照常工作，检查进程自己开自己关，互不干扰。
///
/// <c>--smoke</c> 存在的理由：WPF 的 XAML 是懒加载的，某扇窗写错了，只要没人打开它就不报错，一路
/// 带到发版。这个仓库当时的实际状态是 <see cref="OverlayWindow"/>（「屏幕序号」那块全屏浮层）
/// 的 XAML 从未被任何测试解析过——它写错了要等用户按下那条动作的那一刻才炸。
///
/// <c>--shots</c> 存在的理由更具体：在它之前，多语言/多分辨率/双主题的比对靠一个外部 PowerShell
/// 脚本真的把应用开起来、截图、再把用户的 settings.json 还原回去。那条路子有两个躲不开的毛病——
/// 它必须动用户的真配置（实际上就把用户自己配的鼠标手势回滚过一次），而且改不了"窗口最大高度按
/// 哪块屏的工作区算"，只能改真窗口尺寸。进程内离屏渲染两个问题都不存在：不碰 %APPDATA%，
/// 而且 Show() 之后直接设 MaxHeight 就能模拟任意工作区高度。</summary>
public partial class App
{
    /// <summary>自查模式下窗口不要按真实屏幕居中/落位——它们只是被构造出来量一遍，不该跑到用户
    /// 眼前，也不该把"上次窗口位置"写回配置。</summary>
    internal static bool InSelfCheck { get; private set; }

    private const int OffScreen = -32000;

    /// <summary>把每扇窗构造并布局一遍就退出。判定成功用它写的 marker 文件，不要用退出码：
    /// WPF 应用经 Start-Process 拿到的 ExitCode 可能是 null。</summary>
    private void RunSmoke()
    {
        InSelfCheck = true;
        string report = Path.Combine(Path.GetTempPath(), "windowshuttle-smoke.txt");
        try
        {
            foreach (var (name, make) in AllWindows())
            {
                var w = make();
                Park(w);
                w.Show();
                w.UpdateLayout();
                if (w.ActualWidth < 1 || w.ActualHeight < 1)
                    throw new InvalidOperationException($"{name} 布局出来是 {w.ActualWidth}×{w.ActualHeight}");
                w.Close();
            }
            CheckTrayIcon();
            File.WriteAllText(report, "OK");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, ex.ToString());
            Shutdown(1);
        }
    }

    /// <summary>窗口类型 × 深浅主题 × 中英 × 三档工作区高度，离屏渲染成 PNG。
    /// 用法：<c>WindowShuttle.exe --shots &lt;输出目录&gt;</c></summary>
    private void RunShots(string[] args)
    {
        InSelfCheck = true;
        int i = Array.IndexOf(args, "--shots");
        string dir = i >= 0 && i + 1 < args.Length ? args[i + 1] : Path.GetTempPath();
        try
        {
            Directory.CreateDirectory(dir);
            // 按配置伪造一份热键注册结果。RunShots 在 OnStartup 里排在建 HotkeyService 之前（自查进程
            // 不该去抢注册全局热键，那会跟托盘里正在跑的实例打架），于是 HotkeyStates 是空字典，而
            // RenderCap 对查不到的动作一律画成"未绑定"——出厂就绑着 Ctrl+Alt+1 的整屏互换，在每一张
            // 截图里都显示成未绑定，README 首屏图和它下面那张"快捷键 Ctrl+Alt+1"的表当场自相矛盾。
            // 这里填的是"默认安装、无冲突"的那个真实结果：有值即已注册，空串即未绑定。
            HotkeyStates = Cfg.Hotkeys.ToDictionary(kv => kv.Key,
                kv => string.IsNullOrWhiteSpace(kv.Value) ? HotkeyState.Unbound : HotkeyState.Registered);
            // 三档工作区高度：1366×768@125% / 1920×1080@150% / 1920×1080@100%。窗口高度上限是按
            // 显示器工作区算的，不模拟它就只能量到本机这一种屏。内容自撑尺寸的窗口（确认框、通知卡）
            // 只吃这一档——它们的外形跟屏幕有多宽无关，只跟"最多能长多高"有关。
            double[] workAreas = [614, 720, 1040];

            // 主窗口另走一张真实分辨率表：它是唯一有响应式断点的窗口（动作区宽度过 760 切两列、
            // 过 1160 切三列，见 MainWindow.Hotkeys.cs），而上面那张高度表**只改高不改宽**，
            // 于是两个分列断点一次都没被渲染过——「三列长什么样」在这套工具里此前是纯盲区。
            //
            // 每一行的宽高是按 ComputeStartupBounds 的口径反推出来的真实值，不是随手挑的数：
            // 窗口 = clamp(默认 1120×900, 拖拽下限 760×480, 该屏工作区 DIP − 48 边距)。
            (string Label, double W, double H)[] mainSizes =
            [
                ("min",      760,  480),   // 拖拽下限（= 1366×768@150% 的工作区高度），最坏情况：单列
                ("1024x768", 976,  680),   // 还撑得住的最老桌面；宽度被工作区夹到 976
                ("1366x768", 1120, 680),   // 最常见的廉价笔记本
                ("1080p150", 1120, 632),   // 15" 1080p 按 Windows 推荐的 150%：最矮的一档
                // 默认尺寸：引常量而不是抄一份字面量（全限定——App 继承自 Application，
                // 裸写 MainWindow 会解析成 Application.MainWindow 那个实例属性）。抄的那份在动作清单变短、DefaultHeightDip
                // 从 900 调到 720 之后仍然是 900——这一档本该盯着"默认打开长什么样"，却在盯一个
                // 应用早已不用的尺寸，绿着但验的是别的东西。
                ("1080p100", WindowShuttle.App.MainWindow.DefaultWidthDip, WindowShuttle.App.MainWindow.DefaultHeightDip),
                // 三列**刚成立**的那一档，而且很矮。1920×1080@150%（Windows 对 15" 1080p 的推荐值，
                // 最常见的笔记本之一）最大化后工作区就是 1280×688 DIP，动作区约 1232 —— 刚过
                // ThreeColumnMinWidth(1160)。
                //
                // 补它的理由是这张表原来从 1120（两列）直接跳到 2512（很宽的三列），断点右边那一小段
                // 完全没人看过；而三列最挤、描述最容易折行的地方恰恰就在这里，再叠上德语/俄语的长文案，
                // 正是 MainWindowCompactLayoutTests 那条缺陷的同一种形状（在一个配置下验过，别的配置下坏掉）。
                ("1080p150max", 1280, 688),
                ("max1440",  2512, 1352),  // 在 2560×1440 上最大化：动作区越过 1160 切三列
            ];
            // 五门语言各守一件事，不是随便挑的：
            //   zh-CN 文案母版，也是最短的一门——排版最宽松，最不容易暴露问题，只能当基线；
            //   en    产品的第二母语，README 首屏图用它；
            //   de    最长的拉丁文案（"Rescue off-screen windows…" 到了德语要长三成），
            //         设置栏那排复选框和动作卡的描述会不会挤爆/截断，只有它说了算；
            //   ru    西里尔字母 + 长词，跟德语一起卡住"最宽文案"这一头；
            //   ar    唯一从右向左的语言。RTL 整个活在布局里，单测最多断言到 IsRightToLeft 这个布尔值，
            //         "窗口真的翻过来了、而地图没跟着翻"只有渲染出来才看得见。
            foreach (bool dark in new[] { true, false })
            foreach (string lang in new[] { "zh-CN", "en", "de", "ru", "ar" })
            {
                Strings.ApplyCulture(lang);
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark
                    ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                    : Wpf.Ui.Appearance.ApplicationTheme.Light);
                AppTheme.Apply(dark);
                string tag = $"{(dark ? "dark" : "light")}-{(lang == "zh-CN" ? "zh" : lang)}";
                foreach (var (name, make) in AllWindows())
                {
                    // 主窗口按真实分辨率表定宽定高；其余窗口（确认框、通知卡）自撑尺寸，宽度由内容决定，
                    // 只受"最多能多高"约束——所以它们的 Width 是 null，不是某个哨兵数值。
                    var variants = name == "main"
                        ? [.. mainSizes.Select(s => (s.Label, (double?)s.W, s.H))]
                        : workAreas.Select(h => ($"wa{h:0}", (double?)null, h));

                    foreach (var (label, width, height) in variants)
                    {
                        var w = make();
                        // 跟生产路径同一件事：每扇窗自己设排版方向（见 Strings.Flow）。
                        // 这里必须显式设——窗口是在这个循环里逐语言重建的，而 ApplyCulture 换语言之后
                        // 已经建好的窗口不会自己翻。
                        w.FlowDirection = Strings.Flow;
                        Park(w);
                        w.Show();
                        // 必须 Show 之后设，否则被真实显示器的值覆盖。
                        //
                        // 这一行同时是这套工具的已知盲区：它会把窗口**自己**在 SourceInitialized 里算出来
                        // 的运行时高度上限盖掉。于是"某扇 SizeToContent 的窗口压根没给自己封顶"这一类缺陷
                        // 在每一张截图里都完好无损，却会在真实的小屏上把确定按钮顶出屏幕。这类问题只能靠
                        // 别的东西守——ConfirmDialog 那个上限由 ConfirmDialogHeightCapTests 盯着。
                        w.MaxHeight = height;
                        if (width is double fixedWidth) { w.Width = fixedWidth; w.Height = height; }
                        w.UpdateLayout();
                        Save(w, Path.Combine(dir, $"{name}-{tag}-{label}.png"));
                        w.Close();
                    }
                }
            }
            // ── 屏数这一维：地图是按显示器数量和摆位画出来的，而上面每一张图都只有开发者这台机器
            //    的那一套屏。2/4/5/6 屏长什么样，在加这一段之前一次都没被渲染过——卡片压到多窄就
            //    放不下"2560 × 1440"、上下两排的桌面会不会挤成一条，全靠想象。而地图正是这扇窗的
            //    核心内容。
            //
            //    摆法取真实会遇到的，不是随手编的数字：双屏一左一右、四屏 2×2、五屏一字排开
            //    （监控/交易台）、六屏 3×2。语言只跑中文和德语两门——几何是这一维要验的东西，
            //    而德语负责回答"最长的文案在最窄的卡片里会怎样"。
            foreach (var (label, mons) in FakeMonitorSets())
            foreach (string lang in new[] { "zh-CN", "de" })
            {
                Strings.ApplyCulture(lang);
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                AppTheme.Apply(true);
                MonitorProbe.Override = mons;
                try
                {
                    var w = new WindowShuttle.App.MainWindow { FlowDirection = Strings.Flow };
                    Park(w);
                    w.Show();
                    w.MaxHeight = 1040;
                    w.Width = WindowShuttle.App.MainWindow.DefaultWidthDip;
                    w.Height = WindowShuttle.App.MainWindow.DefaultHeightDip;
                    w.UpdateLayout();
                    Save(w, Path.Combine(dir, $"screens-{label}-{(lang == "zh-CN" ? "zh" : lang)}.png"));
                    w.Close();
                }
                finally { MonitorProbe.Override = null; }
            }

            Console.WriteLine($"shots written to {dir}");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Shutdown(1);
        }
    }

    /// <summary>给 --shots 用的几套假显示器摆法。工作区一律比屏幕矩形矮 48px（任务栏），
    /// 跟真实桌面同形；DPI 混着给，因为混合 DPI 正是这个应用要处理的日常。</summary>
    private static IEnumerable<(string Label, List<MonitorInfo> Monitors)> FakeMonitorSets()
    {
        // **一字排开是这个应用的主场景**，所以这张表以横排为主：2/4/6 块，外加真实会遇到的三种变体。
        // 摆法不是随手编的数字——每一条各卡一件事：
        //   2-row       最少的多屏配置，两块屏高度不同、顶部对齐（笔记本外接显示器的常态）
        //   4-row       等宽等高的一排，检查卡片均分与 chips 排布
        //   6-row       **横排的极端**：六张卡挤在一条带子里，卡片宽度掉到最小
        //   6-row-mixed 同样六块，但 DPI 和物理尺寸都混着——地图按"观感大小"画，混合 DPI 才是它真正
        //               要表达的东西，等宽的那一版反而掩盖了它
        //   portrait    一块竖屏夹在中间（旋转过的副屏很常见）。带子的高度由桌面长宽比算出来，
        //               竖屏会把桌面"变高"，这是唯一能把带子撑到上限的横排布局
        //   ultrawide   带鱼屏 + 小屏：单块屏宽到 5120 时，其余卡片会被压到多窄
        // 另留一条 6-grid 作参照——两排桌面的编号是按列走的（见 SwapPlanner.ByPosition），
        // 那是已知的、待决定的问题，留一张图在这儿，讨论时有据可依。
        yield return ("2-row", [
            Mon(1, 0, 0, 1920, 1080, primary: true, dpi: 1.0),
            Mon(2, 1920, 0, 2560, 1440, dpi: 1.25)]);
        yield return ("4-row", [
            Mon(1, 0, 0, 1920, 1080, primary: true, dpi: 1.0),
            Mon(2, 1920, 0, 1920, 1080, dpi: 1.0),
            Mon(3, 3840, 0, 1920, 1080, dpi: 1.0),
            Mon(4, 5760, 0, 1920, 1080, dpi: 1.0)]);
        yield return ("6-row", [
            Mon(1, 0, 0, 1920, 1080, primary: true, dpi: 1.0),
            Mon(2, 1920, 0, 1920, 1080, dpi: 1.0),
            Mon(3, 3840, 0, 1920, 1080, dpi: 1.0),
            Mon(4, 5760, 0, 1920, 1080, dpi: 1.0),
            Mon(5, 7680, 0, 1920, 1080, dpi: 1.0),
            Mon(6, 9600, 0, 1920, 1080, dpi: 1.0)]);
        yield return ("6-row-mixed", [
            Mon(1, 0, 0, 2560, 1440, primary: true, dpi: 1.25),
            Mon(2, 2560, 0, 3840, 2160, dpi: 1.5),
            Mon(3, 6400, 0, 1920, 1080, dpi: 1.0),
            Mon(4, 8320, 0, 1280, 1024, dpi: 1.0),
            Mon(5, 9600, 0, 2560, 1440, dpi: 1.25),
            Mon(6, 12160, 0, 1920, 1080, dpi: 1.0)]);
        yield return ("portrait", [
            Mon(1, 0, 0, 1920, 1080, primary: true, dpi: 1.0),
            Mon(2, 1920, -420, 1080, 1920, dpi: 1.0),
            Mon(3, 3000, 0, 1920, 1080, dpi: 1.0)]);
        yield return ("ultrawide", [
            Mon(1, 0, 0, 5120, 1440, primary: true, dpi: 1.25),
            Mon(2, 5120, 180, 1920, 1080, dpi: 1.0)]);
        yield return ("6-grid", [
            Mon(1, 0, 0, 1920, 1080, primary: true, dpi: 1.0),
            Mon(2, 1920, 0, 1920, 1080, dpi: 1.0),
            Mon(3, 3840, 0, 1920, 1080, dpi: 1.5),
            Mon(4, 0, 1080, 1920, 1080, dpi: 1.0),
            Mon(5, 1920, 1080, 1920, 1080, dpi: 1.0),
            Mon(6, 3840, 1080, 1920, 1080, dpi: 1.5)]);
    }

    private static MonitorInfo Mon(int i, int x, int y, int w, int h,
        bool primary = false, double dpi = 1.0)
        => new(i, @"\.\DISPLAY" + i, new RectPx(x, y, x + w, y + h),
               new RectPx(x, y, x + w, y + h - 48), primary, dpi, 60);

    /// <summary>托盘图标必须真的建起来。这一条不是凑数：H.NotifyIcon 把 shell 图标的创建挂在
    /// <c>Loaded</c> 上，而这个 TaskbarIcon 从不进可视树，少了 <c>ForceCreate()</c> 就是"应用照常跑、
    /// 快捷键照常用，托盘里什么都没有"——一种不抛异常、不写日志、只有人眼看得见的失败。
    ///
    /// 顺带覆盖了 <c>IconSource</c> 那个 pack URI：图标资源哪天没被打进程序集，这里也会红。
    ///
    /// 建完立刻 <c>Dispose()</c> 把 shell 图标交还回去——自查进程不该在用户托盘里留下一个僵尸图标。
    /// 用一个真的 <see cref="TrayService"/> 而不是裸 TaskbarIcon：要验的就是产品那条路径本身。</summary>
    private static void CheckTrayIcon()
    {
        var tray = new TrayService(Router, openMain: () => { }, exit: () => { });
        try
        {
            if (!tray.IconCreated)
                throw new InvalidOperationException(
                    "托盘图标没有建起来——TaskbarIcon 不在可视树里，需要显式 ForceCreate()");
        }
        finally { tray.Dispose(); }
    }

    /// <summary>自查要覆盖的全部窗口。新加一扇窗就往这里添一行——这是 --smoke 唯一的清单，
    /// 漏了就等于那扇窗的 XAML 仍然没人验。</summary>
    private static IEnumerable<(string Name, Func<Window> Make)> AllWindows()
    {
        yield return ("main", () => new MainWindow());
        yield return ("dialog", () => new ConfirmDialog(Strings.Get("Elevate_Confirm")));
        // 「屏幕序号」浮层：喂真实屏幕列表，它的构造函数要靠这份列表算虚拟桌面矩形。
        yield return ("identify", () => new OverlayWindow(MonitorProbe.GetMonitors()));
        // 通知卡的三挡严重程度各来一张——描边、箭头、底色都按挡位取自 AppTheme，一挡一张才看得出
        // 哪一挡没跟着主题翻。三挡的参数组合照抄 TrayService.DecideNotification 的三条出口，不另编：
        // 截图要能代表用户真会看到的那三张卡。
        yield return ("toast-warn", () => new NotificationOverlay(
            Strings.Plural("Toast_AccessDenied", 2), actionable: true, isError: false, onClick: null));
        yield return ("toast-error", () => new NotificationOverlay(
            Strings.Plural("Toast_OtherFailed", 2), actionable: false, isError: true, onClick: null));
        yield return ("toast-info", () => new NotificationOverlay(
            Strings.Get("Toast_NoUndo"), actionable: false, isError: false, onClick: null));
        // 第四张不是第四个挡位（它跟 toast-warn 同挡），而是**最长的那句文案**：这张卡是
        // SizeToContent + MaxWidth=460 自动换行的，一句话有多长直接决定它折几行。全应用最长的
        // 一句现在是这条，德语/俄语下要折到三行——"折了会不会顶出工作区、会不会裁字"只有渲染
        // 出来才看得见，单测断言不到。
        yield return ("toast-longest", () => new NotificationOverlay(
            Strings.Get("Toast_GesturesBlocked"), actionable: true, isError: false, onClick: null));
    }

    /// <summary>停在屏幕外、不激活、不进任务栏：自查进程不该在用户眼前闪任何东西。
    ///
    /// 这里**不能**顺手加 <c>Opacity = 0</c>。屏幕外 −32000 已经足够不可见，而 Opacity 会被
    /// <see cref="RenderTargetBitmap.Render"/> 一起渲染进去——加上它，--shots 产出的每一张都是全透明
    /// （看图工具里显示为纯白）。这个错误不会以任何方式报错：图照常生成、张数正确、退出码 0，
    /// 只有真去看一眼图才发现是空的。</summary>
    private static void Park(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = OffScreen;
        w.Top = OffScreen;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
    }

    private static void Save(Window w, string path)
    {
        // 按**客户区**尺寸出图，不是 Window.ActualWidth/Height。
        //
        // 两者对带原生标题栏的窗口不是一回事：ConfirmDialog 是普通 Window（有系统标题栏），
        // ActualHeight 把那条标题栏也算进去，而 RenderTargetBitmap.Render 是从客户区原点开始画的——
        // 于是位图底部凭空多出一条跟标题栏等高的空带。审查时差点把它当成"确认框底部留白过多"去改版式，
        // 实际上那是这个截图工具自己编出来的。主窗口不受影响（FluentWindow 自绘标题栏，两者相等），
        // 正因如此这个偏差一直没被发现。
        // ActualWidth/Height 不含元素自身的 Margin，得补回来——否则确认框那 26/24/26/20 的外边距会被
        // 裁掉，位图又从"多一条"变成"少一圈"。
        var content = w.Content as FrameworkElement;
        double cw = w.ActualWidth, ch = w.ActualHeight;
        if (content is { ActualWidth: > 0, ActualHeight: > 0 })
        {
            cw = content.ActualWidth + content.Margin.Left + content.Margin.Right;
            ch = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        }
        int px = (int)Math.Ceiling(cw), py = (int)Math.Ceiling(ch);
        if (px < 1 || py < 1) return;
        var rtb = new RenderTargetBitmap(px, py, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
