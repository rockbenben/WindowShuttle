using WindowShuttle.App;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>同一份「可绑定动作」清单在四个地方各写了一遍：热键注册表、鼠标手势表、主窗口的动作卡
/// 列表，以及 SettingsStore 的两张出厂默认表。它们必须逐字一致。
///
/// 这条测试是补票来的：加新动作时四张表更新了三张，漏掉了
/// <see cref="MouseChordService.Actions"/>。后果比"新动作没手势"重得多——给新动作录一个已被占用的
/// 组合，<see cref="SettingsStore.AssignMouseChord"/> 会先把它从原主人那里收走，而 Apply 遍历的是
/// 这张漏掉了新动作的表、于是新动作根本没装上：那个组合从此**谁都不响应**，界面上两格却都显示
/// 已绑定（鼠标侧没有冲突态可标，见 MouseChordService 的类注释）。若它是唯一绑定的手势，
/// _chords 归零还会把整个 WH_MOUSE_LL 钩子卸掉，所有手势一起失效。
///
/// 断言口径是「跟枚举对齐」而不是「四张表互相相等」：后者在四张表一起漏掉同一个新动作时依然全绿。</summary>
public class ActionTablesAgreeTests
{
    /// <summary>可绑定动作 = 枚举减去三个不可绑定的：List 是 CLI 专用，Rescue 由拔屏自动触发
    /// （见 WindowShuttleAction 的注释），Identify 不是搬窗动作——它是地图的校验工具，入口在地图那一栏
    /// 的按钮和托盘菜单里，不占快捷键位和手势位。</summary>
    private static readonly string[] Bindable =
        [.. Enum.GetValues<WindowShuttleAction>()
              .Except([WindowShuttleAction.List, WindowShuttleAction.Rescue, WindowShuttleAction.Identify])
              .Select(a => a.ToString())];

    public static TheoryData<string, string[]> Tables() => new()
    {
        { "HotkeyService.Actions", HotkeyService.Actions },
        { "MouseChordService.Actions", MouseChordService.Actions },
        { "MainWindow.ActionKeys", MainWindow.ActionKeys },
        { "SettingsStore.DefaultHotkeys", [.. SettingsStore.DefaultHotkeys().Keys] },
        { "SettingsStore.DefaultMouseChords", [.. SettingsStore.DefaultMouseChords().Keys] },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_action_table_covers_exactly_the_bindable_actions(string name, string[] table)
    {
        Assert.Equal(Bindable.ToHashSet(), table.ToHashSet());
        Assert.Equal(Bindable.Length, table.Length);       // 没有重复项
    }

    /// <summary>每个名字都要真能解析回枚举——表里写错一个字母，Apply 里的 Enum.Parse 会在
    /// 用户按下手势的那一刻抛，而不是在启动时。</summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_name_parses_back_to_the_enum(string name, string[] table)
        => Assert.All(table, n => Assert.True(Enum.TryParse<WindowShuttleAction>(n, out _), $"{name}: {n}"));
}
