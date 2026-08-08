using System.IO;
using WindowShuttle.App;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>settings.json 是绕开界面的那条入口，这里守着它。
///
/// 两条都是审查抓出来的真缺陷，共同点是：界面上做不出来，配置文件里做得出来，而后果都是全系统级的。</summary>
public class ChordPolicyTests
{
    private static MouseChordGesture Chord(string s) => MouseChordGesture.TryParse(s)!;

    /// <summary>裸按钮只有「按方向送」能装。点击类动作绑了裸右键，全系统的右键菜单就此消失——
    /// 而点击类动作没有补发路径（补发只在划动那一支），用户在任何程序里都拿不回右键菜单。</summary>
    [Fact]
    public void A_bare_button_is_only_installable_for_the_directional_gesture()
    {
        Assert.True(MouseChordService.Installable("ToDirection", Chord("Right")));
        Assert.False(MouseChordService.Installable("ToPrimary", Chord("Right")));
        Assert.False(MouseChordService.Installable("Swap", Chord("Middle")));
        Assert.False(MouseChordService.Installable("Undo", Chord("X1")));
    }

    /// <summary>带修饰键的组合谁都能装——上一条不能顺手把正常绑定也挡掉。</summary>
    [Fact]
    public void A_modified_chord_is_installable_for_every_action()
    {
        foreach (var name in MouseChordService.Actions)
            Assert.True(MouseChordService.Installable(name, Chord("Ctrl+Right")), name);
    }

    /// <summary>装到钩子上的那张表必须跟上面这道闸一致：这是缺陷原本的位置——闸只设在录制那一侧，
    /// Apply 照单全收。这里直接断言 Apply 的产物，而不是再断言一次 Installable。</summary>
    [Fact]
    public void Apply_drops_a_bare_button_bound_to_a_click_action()
    {
        var s = new Settings
        {
            MouseChords = new Dictionary<string, string>
            {
                ["ToPrimary"] = "Right",        // 手工改出来的：界面上录不出这一条
                ["ToDirection"] = "Middle",     // 划动类：裸按钮合法
                ["Swap"] = "Alt+Middle",        // 正常绑定
            },
        };
        var installed = MouseChordService.Installed(s);
        Assert.DoesNotContain(WindowShuttleAction.ToPrimary, installed.Keys);
        Assert.Equal(MouseChordButton.Middle, installed[WindowShuttleAction.ToDirection].Button);
        Assert.Equal(MouseChordButton.Middle, installed[WindowShuttleAction.Swap].Button);
    }

    /// <summary>升级路径：上一版把 `Ctrl+右键` 给了「送去主屏」，这一版给了「按方向送」。
    /// Load 的 TryAdd 补默认值会把两者同时绑在同一个组合上，而 Resolve 取 Actions 顺序里第一个命中的
    /// ——ToPrimary 排在前面，于是这一版的主打手势对每个老用户都是死的。</summary>
    [Fact]
    public void Loading_a_previous_version_config_does_not_double_bind_the_same_chord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ws-dup-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "MouseChords": { "Swap": "Alt+Middle", "SwapTop": "Alt+Right", "ToPrimary": "Ctrl+Right" } }
            """);
        try
        {
            var s = SettingsStore.Load(path);
            var bound = s.MouseChords.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();
            Assert.Equal(bound.Count, bound.Select(kv => kv.Value).Distinct().Count());
            // 用户自己那条留着，补进来的默认值让位。
            Assert.Equal("Ctrl+Right", s.MouseChords["ToPrimary"]);
            Assert.Equal("", s.MouseChords["ToDirection"]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>大小写和 token 顺序不同的写法是同一个组合，逐字比会把它们当成两个而漏掉。</summary>
    [Fact]
    public void Duplicate_detection_normalises_before_comparing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ws-dup2-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "MouseChords": { "Swap": "Alt+Middle", "SwapTop": "right+CTRL", "ToPrimary": "" } }
            """);
        try
        {
            var s = SettingsStore.Load(path);
            Assert.Equal("right+CTRL", s.MouseChords["SwapTop"]);   // 排在前面的原样留着
            Assert.Equal("", s.MouseChords["ToDirection"]);         // 默认的 Ctrl+Right 是同一个组合，让位
        }
        finally { File.Delete(path); }
    }

    /// <summary>长按交还之后，抬起那一帧仍然必须进"抬起处理块"——账本已经销了（按钮归应用），
    /// 但划动状态还挂着，只有那一帧能清掉它。
    ///
    /// 这道闸被改窄回 <c>mask != 0</c> 的后果不是"少清一次状态"这么轻：_strokeButton 停在旧值上，
    /// 而左键取消分支的前提正是"_strokeButton 不为 null"，于是用户**下一次左键点击**会被当成取消
    /// 吞掉。真机上要先让长按超时一次才看得见，正因如此才把它拆成纯函数钉在这里。</summary>
    [Fact]
    public void The_up_frame_still_runs_after_the_button_has_been_handed_over()
    {
        // 交还之后：欠条销了（0），但划动还在进行 —— 必须进
        Assert.True(MouseChordService.NeedsUpPass(0, strokeActive: true));
        // 普通点击类 chord：欠着一张条，没有划动 —— 必须进
        Assert.True(MouseChordService.NeedsUpPass(0b10, strokeActive: false));
        // 两者都没有：这是绝大多数事件，放它走
        Assert.False(MouseChordService.NeedsUpPass(0, strokeActive: false));
    }

    /// <summary>动作表缩过一次（Identify 改由托盘菜单提供，FixStraddling 整个删掉）。老配置里那几条
    /// 绑定永远不会被注册，设置页上也没有对应的格子能看见或改掉它——配置文件不该继续声称一件
    /// 不成立的事。手改配置时写错的动作名走同一条路。</summary>
    [Fact]
    public void Bindings_for_actions_that_no_longer_exist_are_dropped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ws-orphan-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "Hotkeys":     { "Identify": "Ctrl+Alt+3", "FixStraddling": "Ctrl+Alt+9", "Swap": "Ctrl+Alt+1" },
              "MouseChords": { "Identify": "Shift+Right", "Typo": "Alt+X1" }
            }
            """);
        try
        {
            var s = SettingsStore.Load(path);
            Assert.DoesNotContain("Identify", s.Hotkeys.Keys);
            Assert.DoesNotContain("FixStraddling", s.Hotkeys.Keys);
            Assert.DoesNotContain("Identify", s.MouseChords.Keys);
            Assert.DoesNotContain("Typo", s.MouseChords.Keys);
            // 还在的动作一个都不能被误伤
            Assert.Equal("Ctrl+Alt+1", s.Hotkeys["Swap"]);
            foreach (var k in MouseChordService.Actions) Assert.Contains(k, s.MouseChords.Keys);
            foreach (var k in HotkeyService.Actions) Assert.Contains(k, s.Hotkeys.Keys);
        }
        finally { File.Delete(path); }
    }

    /// <summary>装不上钩子的绑定在读取时就该清空，否则设置页会照着 settings.json 画出一个
    /// 「已绑定」的键帽，而它按下去永远不会响应——鼠标这侧没有冲突态可标，用户查不出为什么。</summary>
    [Fact]
    public void A_chord_that_cannot_be_installed_is_cleared_on_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ws-uninst-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "MouseChords": { "ToPrimary": "Right", "ToDirection": "Middle", "Swap": "Alt+Middle" } }
            """);
        try
        {
            var s = SettingsStore.Load(path);
            Assert.Equal("", s.MouseChords["ToPrimary"]);        // 点击类 + 裸按钮 = 装不上
            Assert.Equal("Middle", s.MouseChords["ToDirection"]); // 划动类允许裸按钮，留着
            Assert.Equal("Alt+Middle", s.MouseChords["Swap"]);    // 带修饰键的正常绑定不受影响
        }
        finally { File.Delete(path); }
    }

    /// <summary>没有冲突时默认值照常补进来——上面两条不能把正常的首次启动也清空。</summary>
    [Fact]
    public void A_fresh_config_still_gets_every_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ws-fresh-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        try
        {
            var s = SettingsStore.Load(path);
            foreach (var (k, v) in SettingsStore.DefaultMouseChords())
                Assert.Equal(v, s.MouseChords[k]);
        }
        finally { File.Delete(path); }
    }
}
