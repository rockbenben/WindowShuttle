using System.IO;
using WindowShuttle.App;

namespace WindowShuttle.Core.Tests;

public class SettingsStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"windowshuttle-settings-{Guid.NewGuid():N}.json");

    [Fact] public void Missing_file_yields_defaults()
    {
        var s = SettingsStore.Load(TempPath());
        Assert.Null(s.Language);
        Assert.True(s.SkipFullscreen);
        Assert.True(s.CloseToTray);
        // 出厂只绑 {Ctrl, Alt} × {右键, 中键} 四格，其余空着（见 SettingsStore.DefaultMouseChords 的注释）。
        Assert.Equal(SettingsStore.DefaultMouseChords(), s.MouseChords);
        Assert.Equal("Ctrl+Alt+1", s.Hotkeys["Swap"]);
    }

    /// <summary>唯一一个**会在用户没按任何键的情况下移动他窗口**的开关，出厂必须是关的。
    ///
    /// Windows 自己在拔屏时就会把那块屏上的窗口挪走，主场景它接住了；这条通路只覆盖残余
    /// （最小化窗口还原到屏外、应用按存档坐标启动、分辨率变小后留在原坐标）。用"无人触发的
    /// 窗口移动 + 一张卡"去换这点残余，不该是所有人的默认。撞上的人自己打开即可。
    ///
    /// 单独钉一条而不是塞进上面那堆断言：它是个产品决定，改它要连 README 和手工清单一起改，
    /// 值得在测试名字里写清楚为什么。</summary>
    [Fact] public void The_only_setting_that_moves_windows_unprompted_is_off_by_default()
        => Assert.False(SettingsStore.Load(TempPath()).RescueOnDisplayChange);

    [Fact] public void Corrupted_file_yields_defaults()
    {
        var p = TempPath();
        try
        {
            File.WriteAllText(p, "{broken");
            Assert.True(SettingsStore.Load(p).SkipFullscreen);
        }
        finally { File.Delete(p); }
    }

    [Fact] public void Roundtrips()
    {
        var p = TempPath();
        try
        {
            var s = SettingsStore.Load(p);
            s.Language = "en";
            s.SkipFullscreen = false;                  // 布尔字段都设为与初始值相反
            s.CloseToTray = false;
            s.MouseChords["ToPrimary"] = "Ctrl+Right";
            s.MouseChords["SwapTop"] = "Alt+Right";
            s.Hotkeys["Swap"] = "Ctrl+Shift+S";
            s.WindowLeft = 120; s.WindowTop = 80; s.WindowWidth = 1000; s.WindowHeight = 720;
            SettingsStore.Save(p, s);
            var back = SettingsStore.Load(p);
            Assert.Equal("en", back.Language);
            Assert.False(back.SkipFullscreen);
            Assert.False(back.CloseToTray);
            Assert.Equal("Ctrl+Right", back.MouseChords["ToPrimary"]);
            Assert.Equal("Alt+Right", back.MouseChords["SwapTop"]);
            Assert.Equal("Ctrl+Shift+S", back.Hotkeys["Swap"]);
            Assert.Equal(120, back.WindowLeft);
            Assert.Equal(80, back.WindowTop);
            Assert.Equal(1000, back.WindowWidth);
            Assert.Equal(720, back.WindowHeight);
        }
        finally { File.Delete(p); }
    }

    // 从没存过位置（全新装机/首次启动）：四个字段都必须是 null，不能悄悄回落到 0——
    // MainWindow.ComputeStartupBounds 靠"是不是 null"分辨"有没有存档"，不是靠"是不是 0"。
    [Fact] public void Missing_file_yields_null_window_bounds()
    {
        var s = SettingsStore.Load(TempPath());
        Assert.Null(s.WindowLeft);
        Assert.Null(s.WindowTop);
        Assert.Null(s.WindowWidth);
        Assert.Null(s.WindowHeight);
    }

    // 键集跟可绑定动作对齐（List 是 CLI 专用、Rescue 是自动触发，都不可绑定）——加动作忘了补默认表，
    // SettingsStore.Load 的 TryAdd 补默认那步就会漏掉它，老配置升级后新动作在设置页永远"未绑定"还录不上。
    [Fact] public void Default_mouse_chords_cover_all_bindable_actions()
    {
        var d = SettingsStore.DefaultMouseChords();
        // 跟 ActionTablesAgreeTests.Bindable 同一口径：List 是 CLI 专用，Rescue 由拔屏自动触发，
        // Identify 不是搬窗动作（入口在地图栏的按钮和托盘里，不占键位）。
        var actions = Enum.GetValues<WindowShuttleAction>()
            .Except([WindowShuttleAction.List, WindowShuttleAction.Rescue, WindowShuttleAction.Identify])
            .Select(a => a.ToString());
        Assert.Equal(actions.ToHashSet(), d.Keys.ToHashSet());
    }

    /// <summary>出厂只绑四格：{Ctrl, Alt} × {右键, 中键}，Shift 整行留白。</summary>
    [Fact] public void Default_mouse_chords_fill_the_grid()
    {
        var d = SettingsStore.DefaultMouseChords();
        Assert.Equal("Ctrl+Right", d["ToDirection"]);    // 主力：一扇窗 → 你划向的那块屏
        Assert.Equal("Ctrl+Middle", d["Undo"]);          // 划错了就地收回来
        Assert.Equal("Alt+Right", d["SwapTop"]);         // 一扇窗 ↔ 对调
        Assert.Equal("Alt+Middle", d["Swap"]);           // 整屏  ↔ 对调
        // 这三个都已经有别的路：往主屏方向划 / 拔屏自动救援 / 往右划。理由见 DefaultMouseChords 注释。
        Assert.Equal("", d["ToPrimary"]);
        Assert.Equal("", d["Gather"]);
        Assert.Equal("", d["ToNext"]);
    }

    /// <summary>Shift 整行必须留白——Shift+右键（资源管理器"复制为路径"那套扩展菜单）和 Shift+中键
    /// （浏览器前台开标签页）都是有人天天在用的，出厂不去动；同时它也是留给用户加自己手势的干净格子。
    /// 哪天有人图省事把某个动作塞进这一行，这条会红。</summary>
    [Fact] public void Default_mouse_chords_leave_the_whole_shift_row_alone()
    {
        var taken = SettingsStore.DefaultMouseChords().Values.Where(v => v.StartsWith("Shift+")).ToList();
        Assert.True(taken.Count == 0, "Shift 行被占了: " + string.Join(", ", taken));
    }

    /// <summary>出厂一个裸按键都不用。划动类**允许**绑裸按钮（AllowsBareButton），但那是留给用户
    /// 自己选的能力——出厂替他把右键吞掉，赌的是全系统的右键菜单，跟省下的一个修饰键不对等。
    /// 下面那条 …never_acceptable 只挡住"点击类不许裸按键"，挡不住有人顺手把划动类的默认也改成裸键，
    /// 所以这条单独钉一下。</summary>
    [Fact] public void No_default_binding_uses_a_bare_button()
    {
        foreach (var (action, raw) in SettingsStore.DefaultMouseChords().Where(kv => kv.Value != ""))
            Assert.Contains('+', raw);
    }

    /// <summary>还剩三条真正的禁区。别的"冲突"都不是禁区——我们是全局钩子，命中即拦截，必然抢赢，
    /// 占不占只是取舍（理由写在 DefaultMouseChords 的注释里）。</summary>
    [Fact] public void Default_mouse_chords_stay_off_the_three_things_that_are_never_acceptable()
    {
        var bound = SettingsStore.DefaultMouseChords().Where(kv => kv.Value != "").ToList();
        // 裸按键绝不能出厂绑给**点击类**动作——那会直接吃掉右键菜单和中键。
        //
        // 划动类是唯一的例外，而且它并不构成同一个危险：裸按钮按下先被扣住，松手时若位移不到
        // MinStroke 就把这次点击原样补发回去（MouseChordService.Replay），右键菜单照常弹，只晚一瞬。
        // 也就是说它吃掉的只有"划"，从不吃"点"。判据挂在 AllowsBareButton 上，加新动作时想绕开它
        // 就得先去改那个方法——不是随手在这里加个名字就能放行。
        Assert.All(bound, kv =>
        {
            if (SettingsStore.AllowsBareButton(kv.Key)) return;
            Assert.Contains('+', kv.Value);
        });
        var values = bound.Select(kv => kv.Value).ToList();
        // 侧键很多鼠标没有，绑了等于那台机器上没有这个动作。
        Assert.All(values, v => Assert.False(v.Contains("X1") || v.Contains("X2"), $"{v} 用到了侧键"));
        // Win 键在点击落地前就被系统收走，录不进也永远不会触发——技术上不成立，不是取舍。
        Assert.All(values, v => Assert.DoesNotContain("Win", v));
    }

    /// <summary>这套映射之所以能被推理而不是死记，全靠两条结构性质。它们比任何一格的具体取值都重要——
    /// 哪天有人按"手感"挪动其中一格，这条会红。</summary>
    [Fact] public void Default_mouse_chords_keep_both_axes_meaningful()
    {
        var d = SettingsStore.DefaultMouseChords();

        // 按钮轴（最硬的一条）：右键那一列**全部**是"对光标下那一扇窗做事"，一个例外都没有。
        foreach (var oneWindow in new[] { d["ToDirection"], d["SwapTop"] })
            Assert.EndsWith("+Right", oneWindow);
        // 中键那一列**全部**是"一次动一堆窗"——撤销退的往往正是整屏互换那种大动作，所以它也在这一列。
        // 这一条现在没有例外了，不再需要"撤销不属于任何一个轴"那段解释。
        foreach (var many in new[] { d["Undo"], d["Swap"] })
            Assert.EndsWith("+Middle", many);

        // 修饰键轴：Ctrl 行是"我正在搬窗户"（右键搬出去、中键收回来），Alt 行是"两边对调"
        // （一扇 ↔ 整屏两个版本）。每一行的两格只差按钮。
        Assert.Equal(d["ToDirection"].Replace("+Right", "+Middle"), d["Undo"]);
        Assert.Equal(d["SwapTop"].Replace("+Right", "+Middle"), d["Swap"]);
        // 两行之间只差修饰键。
        Assert.Equal(d["ToDirection"].Replace("Ctrl+", "Alt+"), d["SwapTop"]);

        // 主力动作必须落在抢得最少的那一格上：Shift+右键 是资源管理器的扩展菜单，Alt+右键 是 AltSnap
        // 的默认缩放手势，只有 Ctrl+右键 没有对得上号的通用约定。
        Assert.Equal("Ctrl+Right", d["ToDirection"]);
    }

    /// <summary>两个动作绑同一个手势，其中一个就永远触发不到，而界面上看不出任何异常——鼠标手势
    /// 没有快捷键那种注册期冲突信号。键盘那边同理。</summary>
    [Fact] public void No_two_default_bindings_share_a_gesture()
    {
        foreach (var (name, table) in new (string, Dictionary<string, string>)[]
                 { ("鼠标手势", SettingsStore.DefaultMouseChords()), ("快捷键", SettingsStore.DefaultHotkeys()) })
        {
            var bound = table.Values.Where(v => v != "").ToList();
            Assert.True(bound.Count == bound.Distinct().Count(),
                $"{name}出厂绑定里有重复: " + string.Join(", ", bound.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key)));
        }
    }

    // 上面那条只守出厂表。录制器写进去的值走的是 AssignMouseChord，同一条不变量必须在那里也成立——
    // 否则用户录一个已经被占用的手势，被占的那个动作会彻底不响应，而它的键位区照样显示已绑定
    // （鼠标手势没有冲突态可标，钩子拿不到"已被占用"的信号）。
    [Fact] public void Recording_a_gesture_takes_it_away_from_whoever_had_it()
    {
        var chords = SettingsStore.DefaultMouseChords();       // SwapTop = Alt+Right
        SettingsStore.AssignMouseChord(chords, "Swap", "Alt+Right");

        Assert.Equal("Alt+Right", chords["Swap"]);
        Assert.Equal("", chords["SwapTop"]);                   // 原主人当场变回未绑定，不是静默失效
        var bound = chords.Values.Where(v => v != "").ToList();
        Assert.Equal(bound.Count, bound.Distinct().Count());
    }

    // 清空（Delete 键）传的是空串，它不该把其余所有空位当成"重复"一起收走。
    [Fact] public void Clearing_a_binding_leaves_the_other_empty_slots_alone()
    {
        var chords = SettingsStore.DefaultMouseChords();
        SettingsStore.AssignMouseChord(chords, "Swap", "");
        Assert.Equal("", chords["Swap"]);
        Assert.Equal("Alt+Right", chords["SwapTop"]);
        Assert.Equal("Ctrl+Right", chords["ToDirection"]);
        Assert.Equal("", chords["ToPrimary"]);           // 本来就是空的，不该被这次清空牵连
    }

    [Fact] public void Every_default_mouse_chord_actually_parses()
    {
        foreach (var (action, raw) in SettingsStore.DefaultMouseChords().Where(kv => kv.Value != ""))
            Assert.True(MouseChordGesture.TryParse(raw) is not null, $"{action} 的出厂手势 {raw} 解析不出来");
    }

    [Fact] public void Loaded_mouse_chords_are_backfilled_with_missing_defaults()
    {
        var p = TempPath();
        try
        {
            File.WriteAllText(p, """{"MouseChords":{"ToPrimary":"Ctrl+Middle"}}""");
            var s = SettingsStore.Load(p);
            Assert.Equal("Ctrl+Middle", s.MouseChords["ToPrimary"]);   // 用户改的保留，不被出厂默认覆盖
            Assert.Equal("Alt+Right", s.MouseChords["SwapTop"]);       // 文件里没有的那些补出厂默认
            Assert.Equal("", s.MouseChords["ToNext"]);                 // 出厂就空的那些仍然是空
        }
        finally { File.Delete(p); }
    }

    // 老配置里那两个写死的 bool 已经从 Settings 上除名；把它们打开过的用户不该悄悄丢绑定——
    // Load 得从原始 JSON 文本里把这两个键翻译成 MouseChords 里的默认手势。
    [Fact] public void Legacy_mouse_chord_booleans_migrate_into_the_chord_table()
    {
        var p = TempPath();
        try
        {
            File.WriteAllText(p, """{"MouseChordToPrimary":true,"MouseChordSwapTop":true}""");
            var s = SettingsStore.Load(p);
            Assert.Equal("Ctrl+Right", s.MouseChords["ToPrimary"]);
            Assert.Equal("Alt+Right", s.MouseChords["SwapTop"]);
        }
        finally { File.Delete(p); }
    }

    [Fact] public void Legacy_mouse_chord_boolean_false_does_not_bind_anything()
    {
        var p = TempPath();
        try
        {
            File.WriteAllText(p, """{"MouseChordToPrimary":false}""");
            var s = SettingsStore.Load(p);
            // 老开关是 false：迁移不写任何东西，这一项照常落到出厂默认（不是空）。
            Assert.Equal(SettingsStore.DefaultMouseChords()["ToPrimary"], s.MouseChords["ToPrimary"]);
        }
        finally { File.Delete(p); }
    }

    // 用户在新表里已经自己录过一次，迁移不能覆盖——新绑定优先于老 bool 翻译出来的默认值。
    [Fact] public void Existing_new_style_binding_is_not_overwritten_by_legacy_migration()
    {
        var p = TempPath();
        try
        {
            File.WriteAllText(p,
                """{"MouseChordToPrimary":true,"MouseChords":{"ToPrimary":"Ctrl+Shift+Middle"}}""");
            var s = SettingsStore.Load(p);
            Assert.Equal("Ctrl+Shift+Middle", s.MouseChords["ToPrimary"]);
        }
        finally { File.Delete(p); }
    }

    [Fact] public void Default_hotkeys_cover_all_bindable_actions()
    {
        var d = SettingsStore.DefaultHotkeys();
        // 跟 ActionTablesAgreeTests.Bindable 同一口径：List 是 CLI 专用，Rescue 由拔屏自动触发，
        // Identify 不是搬窗动作（入口在地图栏的按钮和托盘里，不占键位）。
        var actions = Enum.GetValues<WindowShuttleAction>()
            .Except([WindowShuttleAction.List, WindowShuttleAction.Rescue, WindowShuttleAction.Identify])
            .Select(a => a.ToString());
        Assert.Equal(actions.ToHashSet(), d.Keys.ToHashSet());
    }

    // 默认圈地越少越好（见 DefaultHotkeys 的注释）：只有 Swap/Undo 出厂绑定，其余五个留空等用户自己录。
    [Fact] public void Default_hotkeys_bind_only_Swap_and_Undo()
    {
        var d = SettingsStore.DefaultHotkeys();
        var bound = d.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).ToHashSet();
        Assert.Equal(new HashSet<string> { "Swap", "Undo" }, bound);
    }

    [Fact] public void Non_empty_default_hotkeys_parse()
    {
        var d = SettingsStore.DefaultHotkeys();
        Assert.All(d.Values.Where(v => v.Length > 0),
            v => Assert.NotNull(WindowShuttle.Core.HotkeyGesture.TryParse(v)));
    }

    [Fact] public void Loaded_hotkeys_are_backfilled_with_missing_defaults()
    {
        var p = TempPath();
        try
        {
            File.WriteAllText(p, """{"Hotkeys":{"Swap":"Ctrl+Shift+S"}}""");
            var s = SettingsStore.Load(p);
            Assert.Equal("Ctrl+Shift+S", s.Hotkeys["Swap"]);      // 用户改的保留
            Assert.Equal("Ctrl+Alt+2", s.Hotkeys["Undo"]);        // 缺的补默认（非空）
            Assert.Equal("", s.Hotkeys["SwapTop"]);               // 缺的补默认——即便默认是空字符串也要补上这一项
        }
        finally { File.Delete(p); }
    }

    [Fact] public void Null_hotkeys_section_yields_full_default_table()
    {
        var p = TempPath();
        try
        {
            File.WriteAllText(p, """{"Hotkeys":null}""");
            var s = SettingsStore.Load(p);
            Assert.Equal(SettingsStore.DefaultHotkeys(), s.Hotkeys);
        }
        finally { File.Delete(p); }
    }
}
