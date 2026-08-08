using WindowShuttle.App;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

/// <summary>TrayService.ActionMenuSpec 是托盘菜单的顺序 + 内容本身，纯数据，不用真的拉一个
/// TaskbarIcon（那东西连着 pack://，脱离一个跑起来的 Application 不一定立得住）就能测。
///
/// 归 UiCultureCollection：菜单项文案取自 Strings，跟并行切语言的测试撞上会读到另一种语言。</summary>
[Collection(UiCultureCollection.Name)]
public class TrayMenuTests
{
    // §tray-order：出厂**有绑定**的动作排在未绑定的前面，不能被"点了才知道得先去绑"的那几个
    // 挤没存在感。原来这条写死成"Swap 第一、Undo 第二"，那是当时那份出厂表的样子，不是规矩本身；
    // 按常用程度重排一次它就红了，而顺序其实更符合它的本意。这里改钉本意。
    [Fact] public void Bound_actions_lead_the_menu()
    {
        var hotkeys = SettingsStore.DefaultHotkeys();
        var chords = SettingsStore.DefaultMouseChords();
        bool Bound(WindowShuttleAction a)
        {
            string k = a.ToString();
            return (hotkeys.TryGetValue(k, out var h) && h.Length > 0)
                || (chords.TryGetValue(k, out var c) && c.Length > 0);
        }
        // 屏幕序号不可绑定，固定排最后，不参与这条。
        Assert.Equal(WindowShuttleAction.Identify, TrayService.ActionMenuSpec[^1].Action);
        var actions = TrayService.ActionMenuSpec.Select(e => e.Action)
            .Where(a => a != WindowShuttleAction.Identify).ToList();
        int firstUnbound = actions.FindIndex(a => !Bound(a));
        int lastBound = actions.FindLastIndex(Bound);
        Assert.True(firstUnbound < 0 || lastBound < firstUnbound,
            "未绑定的动作排到了已绑定的前面: " + string.Join(", ", actions));
    }

    /// <summary>托盘菜单跟主窗口的动作卡必须是同一个顺序——同一份清单在两个界面上排得不一样，
    /// 用户会以为是两套功能。两处的差别只允许是那两个有明确理由的出入：按方向送屏进不了菜单
    /// （点一下带不出方向），屏幕序号只在菜单和地图栏里有。</summary>
    [Fact] public void Tray_order_matches_the_main_window()
    {
        var main = MainWindow.ActionKeys.Where(k => k != nameof(WindowShuttleAction.ToDirection));
        var tray = TrayService.ActionMenuSpec.Select(e => e.Action.ToString())
            .Where(a => a != nameof(WindowShuttleAction.Identify));
        Assert.Equal(main, tray);
    }

    /// <summary>菜单要收全"点一下就能干"的动作，四个例外各有各的道理：
    /// List 是 CLI 专用；Rescue 由拔屏自动触发；Identify 不是搬窗动作，入口在地图栏那个按钮；
    /// **ToDirection 压根没有"点一下"的形态**——它的输入是划出来的方向，菜单项里点它无从表达方向，
    /// 收进来只会是一条点了没反应的死项。</summary>
    [Fact] public void All_user_actions_are_covered_exactly_once()
    {
        var expected = Enum.GetValues<WindowShuttleAction>()
            .Except([WindowShuttleAction.List, WindowShuttleAction.Rescue,
                     WindowShuttleAction.ToDirection]).ToHashSet();
        Assert.Equal(expected, TrayService.ActionMenuSpec.Select(e => e.Action).ToHashSet());
        Assert.Equal(expected.Count, TrayService.ActionMenuSpec.Length);
    }

    // 每一项的 resx key 都要真能查到字符串——菜单项文案不能悄悄变成键名本身。
    [Fact] public void Every_menu_entry_resolves_to_a_real_string()
    {
        foreach (var (key, _) in TrayService.ActionMenuSpec)
            Assert.NotEqual(key, WindowShuttle.App.I18n.Strings.Get(key));
    }
}
