using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WindowShuttle.Core;

namespace WindowShuttle.App;

public sealed class Settings
{
    public string? Language { get; set; }              // null = 跟随系统（§12）
    public bool SkipFullscreen { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    /// <summary>拔屏后把"与所有屏零交叠"的窗口自动拉回主屏（见 SwapPlanner.PlanRescue / App 的显示器监听）。
    ///
    /// **默认关**。Windows 自己在你拔掉显示器时就会把那块屏上的窗口挪到剩下的屏上，主场景它接住了；
    /// 这条通路真正覆盖的只是残余——最小化的窗口被还原到记住的那个屏外矩形、应用启动时按存档坐标摆回
    /// 屏外、分辨率变小之后留在原坐标的窗口。为这点残余付的代价却不小：**在用户没按任何键的情况下移动
    /// 他的窗口**，还弹一张卡。无人触发的窗口移动是这个程序里意外感最强的一类动作，不该是所有人的默认。
    ///
    /// 功能保留，不是删掉：残余是真实存在的，撞上的人打开即可；而 `全部收拢` 本来就能按需干同一件事，
    /// 托盘菜单和命令行都够得着。</summary>
    public bool RescueOnDisplayChange { get; set; } = false;
    public Dictionary<string, string> Hotkeys { get; set; } = [];
    // 鼠标 chord 表，跟 Hotkeys 同构：键是 WindowShuttleAction 成员名，值是 MouseChordGesture.ToString()
    // 或空串（未绑定）。全部默认空（§own-decision，见 mouse-hook-spike.md）：一个低级鼠标钩子没有
    // RegisterHotKey 那种注册期冲突信号，装上就会静默拿走这个手势，不像热键那样能在设置页标红——
    // 默认空是保住"绝不悄悄占用组合"这条原则的唯一办法。没有单独的注册表/别的落地点，就老老实实
    // 待在 JSON 里。曾经是两个写死的 bool（MouseChordToPrimary/MouseChordSwapTop）；SettingsStore.Load
    // 里把老配置那两个 bool 迁移进这张表，不让已经打开过的用户悄悄丢绑定。
    public Dictionary<string, string> MouseChords { get; set; } = [];

    // 主窗口最后一次的位置与尺寸，物理像素——跟 MonitorInfo.WorkArea 同一套坐标系（§window-bounds，
    // 见 MainWindow.xaml.cs 的 ApplyStartupBounds）。四个都是 null：从没存过（首次启动）或者手改
    // 配置删掉了，一律回落到"主屏居中、默认 DIP 尺寸"。只在窗口处于 Normal 态时落盘——最大化/
    // 最小化时的物理矩形不是用户想记住的"大小"。
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
}

public static class SettingsStore
{
    /// <summary>测试/harness 用的落地根目录改写口。生产永远是 null，走 %APPDATA%。
    ///
    /// 不是可有可无的洁癖：任何真的建过 MainWindow 的测试，关窗时都会走 OnClosing → SaveWindowBounds
    /// → Save(DefaultPath, App.Cfg)，而测试里的 App.Cfg 是一份 <c>new Settings()</c>——于是开发机上
    /// 每跑一次 dotnet test，本机那份真配置就被测试状态覆盖一次，快捷键和鼠标手势绑定全丢（实测踩到，
    /// 覆盖后的文件里 Hotkeys 是空对象、窗口矩形正好是测试那扇 960×760 DIP 窗口的物理值）。
    /// 测试没法在自己这边绕开：写盘发生在生产代码的关窗路径里。</summary>
    internal static string? HomeOverride { get; set; }

    private static string AppDir => Path.Combine(
        HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowShuttle");
    public static string DefaultPath => Path.Combine(AppDir, "settings.json");
    public static string UndoPath => Path.Combine(AppDir, "undo.json");   // §11

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        // 默认只绑两个。一个全局热键是从所有应用手里夺走一个组合——Ctrl+Alt+<字母> 尤其糟，
        // Word 的 ©、IntelliJ 的 Extract Constant 都会静默失效。默认圈地越少越好，其余留空
        // ("" = 未绑定)，用户在设置页自己录。撤销必须默认可用：搬错了退不回去是最坏的第一印象。
        ["Swap"] = "Ctrl+Alt+1",
        ["SwapTop"] = "",
        ["ToPrimary"] = "",
        ["ToNext"] = "",
        ["ToDirection"] = "",
        ["Gather"] = "",
        ["Undo"] = "Ctrl+Alt+2",
    };

    /// <summary>出厂只绑四个手势——{Ctrl, Alt} × {右键, 中键} 那半张网格，Shift 整行留白。
    ///
    ///                        右键 = 这一扇窗          中键 = 一次动一堆窗
    ///     Ctrl  = 搬这一下     按方向送屏               撤销
    ///     Alt   = 两边对调     对调最前窗口              整屏互换
    ///     Shift = —          （留白）                 （留白）
    ///
    /// **最好的那一格给按方向送屏。** 它是日常使用里出现次数最多的动作：指着一扇窗，往目标屏的方向
    /// 划一下，连"那是第几块屏"都不用想。这样一个动作原来待在"其余"那一行——而那一行的定义就是
    /// "剩下的、凑不出统一语义的"，把主力放进去是本末倒置。
    ///
    /// 三个修饰键里 Ctrl+右键 抢走的东西最少，所以它归主力：Shift+右键 是资源管理器"复制为路径"
    /// 那套扩展菜单，Alt+右键 是 AltSnap 的默认缩放手势，而 Ctrl+右键 没有对得上号的通用约定。
    ///
    /// **撤销紧挨着它，同一个修饰键。** 用划的搬窗，迟早会划错一次；那一刻还要伸手去够键盘的话，
    /// 整个流程就断了。所以 Ctrl 这一行读作"我正在搬窗户"——右键搬出去，中键收回来。
    ///
    /// 两条线索仍然成立，而且比原来更干净：
    ///   · **按钮说「管多少」**——右键在手指下面，管光标下那一扇；中键要专门按一下，一次动一堆。
    ///     中键那一列现在三格全部如此（撤销退的往往正是整屏互换那种大动作），不再需要"撤销是唯一
    ///     不属于任何一个轴的例外"那段解释。误触代价最大的动作也因此都落在必须专门按一下的键上。
    ///   · **修饰键说「做什么」**——Ctrl 是"搬 / 收回来"，Alt 是"两边对调"（一扇 ↔ 整屏两个版本）。
    ///
    /// **Shift 整行留白，既是给你的，也是给资源管理器和浏览器的。** Shift+右键（复制为路径那套扩展
    /// 菜单）和 Shift+中键（浏览器前台开标签页）都是有人天天在用的东西，出厂不去动。想加自己的手势，
    /// 这一行是现成的干净格子。
    ///
    /// **送去主屏 / 全部收拢 / 送去下一块屏 出厂不给手势**——不是它们不好用，是**已经有别的路**：
    /// 送去主屏 ＝ 往主屏那个方向划一下；全部收拢 的主战场（拔屏之后窗口流落到屏外）默认由自动救援
    /// 接管，手动那次是兜底；送去下一块屏 ＝ 往右划。三个都在托盘菜单和命令行里，想要手势自己录一格，
    /// 一次点击的事。多绑一格的代价是从别的程序手里再拿走一个组合，而收益是一条已经有的路。
    ///
    /// 剩下这四格仍然是在抢东西——Ctrl+中键 是浏览器的后台开标签页，Alt+右键 是 AltSnap。这是明摆着的
    /// 取舍，不是疏忽：我们装的是全局低级鼠标钩子，命中就 return 1，事件根本到不了任何应用——**必然
    /// 抢赢**。所以"被某某程序占了"从来不是技术障碍，只是要不要拿走。哪一格不想要，点它按 Delete
    /// 清掉即可，一次按键的事。
    ///
    /// 仍然不碰的：
    ///   · 裸按键——**出厂一个都不用**。点击类绑了就是彻底吞掉，全系统右键菜单/中键自动滚动当场消失；
    ///     划动类虽然有归还路径（AllowsBareButton 因此只对 ToDirection 放行，实测记录在
    ///     DefaultMouseChords 里），但那是留给用户自己选的能力，不是出厂该替他冒的险；
    ///   · Win+任意键——**技术上就不成立**，不是取舍：系统在点击落地前就把 Win 的按下状态收走了，
    ///     录制和触发读到的都是"没按"（见 MouseChordService.BeginCapture 的实测记录）；
    ///   · 侧键 X1/X2——很多鼠标根本没有，不能进出厂默认（用户自己录随意）。
    ///
    /// 代价没有变，所以设置条底部那句警告常驻：低级鼠标钩子没有 RegisterHotKey 那种注册期冲突信号，
    /// 绑上就静默拿走这个组合，界面上没法像快捷键那样标红。</summary>
    /// <summary>只有划动类动作可以绑裸按钮。点击类绑了裸右键 = 全系统右键菜单当场消失，没有任何理由
    /// 值得这个代价；而划动有两条归还路径——没划出方向就把那次点击原样补发，按住不动超时就把按下交还，
    /// 两条都不吃菜单。</summary>
    public static bool AllowsBareButton(string actionKey) => actionKey == "ToDirection";

    /// <summary>这个动作能不能绑键盘快捷键。只有「按方向送屏」不能——**一个热键携带不了方向**。
    ///
    /// 这不是洁癖，是修一个真缺陷：它先前在快捷键表里是可录的，而 <see cref="SwapPlanner.Plan"/>
    /// 的 switch 根本没有它的分支（手势路径在钩子层就把方向换算成了 ToNext+目标屏，压根到不了
    /// Plan）。于是给它录一个快捷键、按下去，Plan 抛 ArgumentOutOfRangeException，被 CommandRouter
    /// 的兜底 catch 接成退出码 3，用户看到的是一张红色错误卡——一个明明列在界面上的动作，绑了就报错。
    ///
    /// 挡在两处，不是一处：界面上那一格画成不可录（<see cref="MainWindow.BuildCap"/>），
    /// <see cref="HotkeyService.Apply"/> 也拒绝注册——后者管的是手改 settings.json 那条路，
    /// 光靠界面挡不住。</summary>
    public static bool AllowsHotkey(string actionKey) => actionKey != "ToDirection";

    public static Dictionary<string, string> DefaultMouseChords() => new()
    {
        // 主力动作，拿抢得最少的那一格（理由见类注释）。
        //
        // 用的是 Ctrl+右键 而**不是**裸右键——尽管裸右键才是手势类软件的通行形态，而且在作者机器上是
        // 验过能用的，不是没敢试：判据取应用自己收到的消息，而不是"屏幕上看着有没有菜单"——挂一扇
        // 自建 Win32 窗口读它 WndProc 里的 WM_CONTEXTMENU（Win11 的菜单是 XAML，不是经典 #32768 类，
        // 数弹出窗口数不出来）。对照（本程序未运行）与实验（本程序运行中）都得到完整的
        // WM_RBUTTONDOWN → WM_RBUTTONUP → WM_CONTEXTMENU，落点与按下点逐像素一致，9 次里 8 次
        // （唯一一次失败是测试当中真人挪了鼠标，按下点已经不在探针窗上）。
        //
        // 不做默认是因为**代价与收益不对等**：省下的是一个修饰键，赌上的是全系统右键菜单——补发那条
        // 路一旦在某台机器上不成立（另一个低级钩子把补发吞了、某个程序只认真实输入），用户失去的是所有
        // 程序的右键菜单，而他多半不会想到是这个搬窗小工具干的。
        //
        // 能力保留：AllowsBareButton 仍然只对这个动作放行，想要的人自己把这一格录成裸右键即可。
        // 那条路上的两道兜底也都还在——没划出方向就原样补发那次点击，按住不动超过 HoldMs 就把按下
        // 交还给应用（HoldMs 只对裸按钮武装，带修饰键的组合不受影响）。
        ["ToDirection"] = "Ctrl+Right",   // 一扇窗 → 你划向的那块屏
        ["Undo"] = "Ctrl+Middle",         // 划错了就地收回来，不必去够键盘
        ["SwapTop"] = "Alt+Right",        // 一扇窗 ↔ 对调
        ["Swap"] = "Alt+Middle",          // 整屏  ↔ 对调
        // 下面三个出厂不给手势：都已经有别的路了，见类注释最后一段。
        ["ToPrimary"] = "",
        ["Gather"] = "",
        ["ToNext"] = "",
    };

    /// <summary>把一个手势判给某个动作，并从原主人那里收走——手势表里同一个组合只能出现一次。
    ///
    /// 重复的后果不是"两个动作都响应"，而是排在后面的那个**彻底不响应**：MouseChordService.Apply
    /// 按 Actions 的固定顺序把这张表灌进一个新字典，MouseChordGesture.Resolve 取第一个命中的，
    /// 后者永远轮不到。而它的键位区照样画着已绑定的键帽——鼠标钩子拿不到"这个组合已被占用"的信号，
    /// 所以这一侧根本没有冲突态可标（键盘那侧有，因为 RegisterHotKey 当场就会失败）。用户看到的是：
    /// 我给「整屏互换」录了 Alt+右键，结果我压根没碰过的「对调最前窗口」从此不动了，两边都显示正常。
    ///
    /// 收走而不是拒绝录制：旧的那格当场变回未绑定，用户看得见发生了什么，也知道该去哪儿重录。</summary>
    public static void AssignMouseChord(Dictionary<string, string> chords, string actionKey, string gesture)
    {
        if (!string.IsNullOrEmpty(gesture))
            foreach (var stolen in chords.Where(kv => kv.Key != actionKey && kv.Value == gesture)
                                         .Select(kv => kv.Key).ToList())
                chords[stolen] = "";
        chords[actionKey] = gesture;
    }

    public static Settings Load(string path)
    {
        Settings s;
        string? raw = null;
        try
        {
            raw = File.Exists(path) ? File.ReadAllText(path) : null;
            s = raw is not null ? JsonSerializer.Deserialize<Settings>(raw) ?? new Settings() : new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException) { s = new Settings(); }

        s.Hotkeys ??= [];                              // 显式 null（如手改配置）也回落到可补默认的状态
        foreach (var (k, v) in DefaultHotkeys())      // 老配置缺新动作时补默认
            s.Hotkeys.TryAdd(k, v);

        s.MouseChords ??= [];
        MigrateLegacyMouseChordBooleans(raw, s.MouseChords);
        foreach (var (k, v) in DefaultMouseChords())
            s.MouseChords.TryAdd(k, v);
        DropDuplicateMouseChords(s.MouseChords);
        DropUninstallableMouseChords(s.MouseChords);
        DropUnknownBindings(s.Hotkeys, HotkeyService.Actions);
        DropUnknownBindings(s.MouseChords, MouseChordService.Actions);
        return s;
    }

    /// <summary>清掉那些**装不上钩子**的手势，让配置跟界面说同一件事。
    ///
    /// <see cref="MouseChordService.Installable"/> 会把"点击类动作绑了裸按钮"挡在门外（那会让全系统
    /// 右键菜单消失）。但挡住的只是**装载**——设置页照着 settings.json 画键帽，于是那一格显示着
    /// 「Right」、看上去绑得好好的，按下去却什么都不发生，而鼠标这侧没有冲突态可标，用户没有任何
    /// 途径知道为什么。
    ///
    /// 与其让界面替一个永远不会响应的绑定站台，不如在读取时就清掉：那一格显示成未绑定——它本来
    /// 就是未绑定。跟 <see cref="DropUnknownBindings"/> 同一个道理，配置文件不该声称一件不成立的事。</summary>
    private static void DropUninstallableMouseChords(Dictionary<string, string> chords)
    {
        foreach (var name in MouseChordService.Actions)
        {
            var raw = chords.GetValueOrDefault(name, "");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (MouseChordGesture.TryParse(raw) is { } g && !MouseChordService.Installable(name, g))
                chords[name] = "";
        }
    }

    /// <summary>删掉配置里那些**已经不存在的动作**留下的绑定。
    ///
    /// 动作表是会缩的（这一版把 `Identify`、`FixStraddling` 从可绑列表里拿掉了，前者改由托盘菜单
    /// 提供）。TryAdd 只补缺失的键、从不删多余的，于是老配置里那几条会一直留着——**绑定还写在文件里，
    /// 却永远不会被注册**（Apply 只遍历 Actions），设置页上也没有对应的那一格可以让用户看见或改掉它。
    /// 用户按下 Ctrl+Alt+3 什么都不发生，翻遍界面找不到它录在哪儿。
    ///
    /// 删掉不能让那个快捷键复活——动作真的没了。但它让配置文件不再**声称**一件不成立的事：
    /// 那一格从此干干净净，用户再录别的组合也不会撞上一个看不见的旧主人。
    /// 顺带也清掉手改配置时写错的动作名。</summary>
    private static void DropUnknownBindings(Dictionary<string, string> table, string[] known)
    {
        foreach (var orphan in table.Keys.Where(k => !known.Contains(k)).ToList())
            table.Remove(orphan);
    }

    /// <summary>同一个组合只能归一个动作，重复的一律清空——只留 <see cref="MouseChordService.Actions"/>
    /// 顺序里最靠前的那一个，因为 Apply 正是按那个顺序灌字典、而 Resolve 取第一个命中的。
    ///
    /// 这不是防御性编程，是修一条真实的升级路径。上一版的默认表把 `Ctrl+右键` 给了「送去主屏」，
    /// 这一版把同一个组合给了「按方向送」。老用户配置里 ToPrimary=Ctrl+Right 原样留着（TryAdd 只补
    /// 缺失的键），上面那句又把 ToDirection=Ctrl+Right 加了进去；ToPrimary 在 Actions 里排在前面，
    /// 于是永远赢——**这一版的主打手势对每一个升级上来的用户都是死的**，而设置页那一格照样画着
    /// Ctrl+右键，鼠标这侧又没有冲突态可标（钩子没有 RegisterHotKey 那种注册期冲突信号）。
    /// <see cref="MigrateLegacyMouseChordBooleans"/>（老版那两个 bool）会造出一模一样的重复。
    ///
    /// 清后面那个而不是前面那个：用户自己录过的绑定，优先于我们替他补进去的默认值。代价是老用户
    /// 升级后「按方向送」是空的、得自己录一次——但那比"绑着却永远不响应"诚实，也比擅自改掉一个
    /// 他已经在用的手势礼貌。按解析后的规范形比对，不是比字符串：手工写的 "right+ctrl"、"CTRL+Right"
    /// 跟 "Ctrl+Right" 是同一个组合，逐字比会把它们当成两个而漏掉。</summary>
    private static void DropDuplicateMouseChords(Dictionary<string, string> chords)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in MouseChordService.Actions)
        {
            var raw = chords.GetValueOrDefault(name, "");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // 解析不了的原样留着：这里不是校验入口，Apply 自己会忽略它（也才不会把用户写错的
            // 一行悄悄抹成空、让他以为自己从没写过）。
            if (MouseChordGesture.TryParse(raw) is not { } g) continue;
            if (!seen.Add(g.ToString())) chords[name] = "";
        }
    }

    /// <summary>老配置里 MouseChordToPrimary/MouseChordSwapTop 那两个 bool 已经从 Settings 类型上
    /// 除名——JsonSerializer 反序列化时会直接无视 JSON 里认不出的字段，不补这一步，已经打开过这两个
    /// 开关的用户配置文件会原样留着这两个键、却悄悄丢了绑定这件事本身。只能顶着原始 JSON 文本另外
    /// 找一遍：某个键是 true，就把它翻译成 MouseChords 里对应动作的一条默认手势（照抄老版硬编码的
    /// Ctrl+右键/Alt+右键），TryAdd——用户若已经在新表里自己录过同一个动作，新绑定优先，不覆盖。</summary>
    private static void MigrateLegacyMouseChordBooleans(string? raw, Dictionary<string, string> chords)
    {
        if (raw is null) return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("MouseChordToPrimary", out var ctrl)
                && ctrl.ValueKind == JsonValueKind.True)
                chords.TryAdd("ToPrimary", "Ctrl+Right");
            if (doc.RootElement.TryGetProperty("MouseChordSwapTop", out var alt)
                && alt.ValueKind == JsonValueKind.True)
                chords.TryAdd("SwapTop", "Alt+Right");
        }
        catch (JsonException) { /* 坏 JSON 已经在上面的 Load 里回落到默认，这里静默跳过迁移 */ }
    }

    public static void Save(string path, Settings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";                       // 同目录 = 同卷，Move 是原子的
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>开机自启的两种落地方式，二选一：
///   · 普通——一条 HKCU\Run 注册表值，随当前用户权限启动；
///   · 提权——一个 Run with highest privileges 的**计划任务**。建任务弹一次 UAC，之后每次开机
///     都以管理员身份静默启动，不再有任何提示。
///
/// 提权那档解决的是这个程序在这类桌面上的两堵墙（都实测过）：管理员程序的窗口占前台时，我们的
/// 低级鼠标钩子一个事件都收不到（所有手势整体哑掉）；不占前台时，SetWindowPos 也会被 UIPI 拒绝。
/// 曾经试过绕（把左右手势交给系统的 Win+Shift 键），两头都撞死——**提权是唯一的完整解**，所以
/// 值得把它的摩擦降到零。计划任务是这件事的标准做法（AutoHotkey、Everything 都这么干）：
/// HKCU\Run 起不了提权进程，而任务的 UAC 只在勾选那一刻付一次。
///
/// 两种方式互斥（同时存在会开机起两个实例）：切到提权就删 Run 值，切回来就删任务。
/// 状态不进 settings.json——注册表和任务计划器本身就是唯一事实来源，镜像一份只会有一天对不上。</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WindowShuttle";
    private const string TaskName = "WindowShuttle";

    /// <summary>自启项该写的那一行。带 --tray：开机自启的那一次不弹主窗口，手动双击 exe 没有这个
    /// 参数、会正常显示窗口（见 App.OnStartup 里 startHidden 的注释）。</summary>
    private static string RunCommand => $"\"{Environment.ProcessPath}\" --tray";

    /// <summary>写/清开机自启那一行。
    ///
    /// <c>CreateSubKey</c> 而不是 <c>OpenSubKey(..., writable: true)</c>：全新的用户配置里
    /// <c>HKCU\...\CurrentVersion\Run</c> 可能**根本不存在**（GitHub 的 windows runner 就是这样，
    /// 这条是在那儿炸出来的）。OpenSubKey 那时返回 null，而原来那个 <c>!</c> 只压得住编译器警告、
    /// 压不住 NullReferenceException——用户勾一下「开机自启」就是一个未处理异常。
    /// CreateSubKey 在键已存在时等价于以可写方式打开，不存在时建出来，正是这里要的语义。
    ///
    /// 同一个文件里 <see cref="Get"/> 用 <c>key?.</c>、<see cref="RepairRunValue"/> 显式判了 null，
    /// 只有这一处漏了。</summary>
    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValueName, RunCommand);
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    public static bool Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValueName) != null;
    }

    /// <summary>自启项已经存在、但内容跟这一版该写的不一样时，就地改写。驻留启动时调一次。
    ///
    /// 修的是一条真实的升级路径：`--tray` 是这一版才加的，上一版写进注册表的是**不带参数**的裸路径。
    /// 升级之后 <see cref="Get"/> 照样返回 true、复选框照样勾着，于是谁也不会重写它——而 OnStartup
    /// 现在按"没有 --tray 就是手动启动"来判断，于是每次开机都把主窗口糊到用户脸上。这正是加这个参数
    /// 要避免的事，而且是相对上一版的倒退（上一版驻留模式从不开窗）。
    ///
    /// 顺带也修好"exe 挪过位置"：值里存的是绝对路径，把程序移到别的目录后那一行就指向一个不存在的
    /// 文件，自启静默失效。两种情况同一个动作——内容对不上就照现在该有的样子重写。
    ///
    /// 只在**值已经存在**时改写：不存在就是用户没开自启，这里绝不能替他开。</summary>
    public static void RepairRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            // **不只看我们自己那个值名**。同一个 exe 可能被人用别的名字登记过（换过版本、从别的机器
            // 同步过注册表、或者干脆手工加的）。那种项一样没有 --tray，一样会在每次开机把主窗口糊到
            // 脸上，而只认 RunValueName 的话永远够不着它。按"这一行指向的是不是我们这个 exe"来认，
            // 比按名字认可靠。
            //
            // 够不着的仍然有：shell:startup 里的快捷方式、组策略登录脚本、第三方启动器——那些不在
            // 注册表里，也没有可靠的"这次是不是开机自启"信号可查（父进程跟手动双击一样都是 explorer）。
            // 那条留给文档，不拿启发式去猜：猜错的代价是"我双击了它却什么都没出现"。
            string exe = Environment.ProcessPath ?? "";
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string current) continue;
                bool mine = name == RunValueName
                    || (exe.Length > 0 && current.Contains(exe, StringComparison.OrdinalIgnoreCase));
                if (mine && current != RunCommand) key.SetValue(name, RunCommand);
            }
        }
        catch (UnauthorizedAccessException) { /* 注册表被策略锁了：自启不可用，但不该拦住启动 */ }
    }

    /// <summary>提权自启任务在不在。<see cref="Get"/> 是一次注册表读取，这个却要**开一个进程**
    /// （schtasks.exe，实测约 45ms，最坏等到 5s 超时），所以答案缓存在进程内。
    ///
    /// 缓存不是性能洁癖，是修一个卡顿：调用点在 MainWindow 的构造函数（LoadToggles）和
    /// ApplyStartupMode 里，而后者每次勾任何一个复选框都要问两遍——包括「跳过全屏」这种跟自启毫无
    /// 关系的开关。于是"点开窗口"和"勾一下设置"都会在 UI 线程上同步等一个外部进程，界面当场卡住；
    /// 机器负载高、或者 Defender 正好要扫这次新起的 schtasks.exe 时尤其明显。测试也一样受益：
    /// 每个建 MainWindow 的 WPF 测试原来都会真的去起一个 schtasks 进程。
    ///
    /// 缓存能成立是因为这个状态**只有我们自己会改**（<see cref="SetElevated"/> 成功时同步更新它）。
    /// 用户绕过应用、自己去任务计划程序里删掉那条任务，界面会显示到下次启动为止的旧状态——代价可以
    /// 接受，换来的是每次开窗口和每次勾选都不再卡。</summary>
    private static bool? _elevatedCache;

    public static bool GetElevated()
    {
        if (_elevatedCache is bool cached) return cached;
        // 只缓存**确定的**答案。查不出来（超时/异常）时按"没有"用，但不写进缓存——下次再问就
        // 重新查一次。原来连这种失败也一起缓存，后果不是"显示不准"这么轻：一次偶发超时会把
        // "没有提权任务"钉死一整个进程，用户勾「开机自启」时 elevatedNow 算出 false，于是在**任务
        // 还在**的情况下又写了一个 HKCU\Run 值——下次登录起两个实例，正是互斥代码声称绝不允许的。
        var (ok, definite) = QueryElevated();
        if (definite) _elevatedCache = ok;
        return ok;
    }

    /// <summary>问一次系统。返回 (答案, 这答案算不算数)。</summary>
    private static (bool Ok, bool Definite) QueryElevated()
    {
        System.Diagnostics.Process? p = null;
        try
        {
            p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return (false, false);
            // **先把两个流读干净再等退出**。两个流都重定向了却不读，输出一旦填满管道缓冲区，
            // schtasks 就会阻塞在写上、永远等不到退出——经典的 Process 管道死锁。中文/详细模式下
            // 的 /Query 输出本来就比英文长，这不是理论风险。异步读避免了"先读哪一个"的次级死锁。
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                // 超时：这个进程还挂在那儿，得亲手收掉，否则每查一次就漏一个 schtasks。
                try { p.Kill(entireProcessTree: true); } catch { /* 已经自己退了 */ }
                return (false, Definite: false);
            }
            Task.WaitAll([stdout, stderr], 1000);
            return (p.ExitCode == 0, Definite: true);
        }
        catch { return (false, Definite: false); }
        finally { p?.Dispose(); }
    }

    /// <summary>建/删提权自启任务。两个方向都要过一次 UAC（建带 /RL HIGHEST 的任务、删它都要管理员），
    /// 用户在 UAC 上点了「否」就返回 false——调用方必须把复选框拨回真实状态，不能假装成功。
    ///
    /// 走 schtasks.exe 而不是 Task Scheduler 的 COM 接口：COM 那条路要么整个进程先提权、要么再起一个
    /// 提权的自己并加一套"我是来建任务的"命令行协议；ShellExecute runas 一个系统自带的 exe，UAC 对话框
    /// 上显示的也是 schtasks 这个可信名字。</summary>
    public static bool SetElevated(bool on)
    {
        string args = on
            ? $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{Environment.ProcessPath}\\\" --tray\""
            : $"/Delete /F /TN \"{TaskName}\"";
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe", Arguments = args,
                UseShellExecute = true, Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            p!.WaitForExit(30000);      // UAC 给用户留足时间
            bool ok = p.HasExited && p.ExitCode == 0;
            // 缓存必须跟着真实结果走：成功就是新状态，失败（UAC 被拒）时**作废**而不是写回旧值——
            // schtasks 可能已经改了一半，下次问就老老实实再查一次。
            _elevatedCache = ok ? on : null;
            // 互斥：提权任务建成了就收掉普通 Run 值，否则开机会起两个实例。
            if (ok && on) Set(false);
            return ok;
        }
        // UAC 上点了「否」（1223）：任务肯定没建成，但缓存一律作废——这两条路都不该让一个可能过期的
        // 答案留在进程里，下次问就重新查。
        catch (System.ComponentModel.Win32Exception) { _elevatedCache = null; return false; }
        catch { _elevatedCache = null; return false; }
    }
}
