namespace WindowShuttle.Core;

/// <summary>鼠标 chord 能挂的按钮——从不含左键（全局吞掉左键无法辩护，见调用点的注释）。</summary>
public enum MouseChordButton { Right, Middle, X1, X2 }

/// <summary>
/// 鼠标 chord：修饰键 + 一个按钮。HotkeyGesture 的鼠标版孪生——同一套修饰键位掩码
/// （直接复用 HotkeyGesture.Mod* 常量，不重开一份魔数），同一种"恰好等于"语义（不是子集/超集），
/// 同一种字符串往返方式，好落进 settings.json 里跟 Hotkeys 并排存。
///
/// 裸按钮（没有修饰键）**只对划动类动作合法**，由调用方把关（见 SettingsStore.AllowsBareButton）。
/// 点击类动作绝不能绑裸右键——那是上下文菜单的地盘，一吞整个系统的右键菜单就没了。
/// 划动不一样：按下先扣住，松手时若没划出方向，就把这一次点击原样补发回去，菜单照常弹（见
/// MouseChordService 的 Replay）。所以同一个裸右键，点是点、划是划，两不相扰。
/// </summary>
public sealed record MouseChordGesture(uint Modifiers, MouseChordButton Button)
{
    public static MouseChordGesture? TryParse(string s)
    {
        uint mods = 0;
        MouseChordButton? btn = null;
        foreach (var raw in s.Split('+'))
        {
            var tok = raw.Trim();
            if (tok.Length == 0) return null;
            switch (tok.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyGesture.ModControl; continue;
                case "alt": mods |= HotkeyGesture.ModAlt; continue;
                case "shift": mods |= HotkeyGesture.ModShift; continue;
                case "win": mods |= HotkeyGesture.ModWin; continue;
            }
            if (btn is not null) return null;               // 两个按钮
            btn = tok.ToLowerInvariant() switch
            {
                "right" => MouseChordButton.Right,
                "middle" => MouseChordButton.Middle,
                "x1" => MouseChordButton.X1,
                "x2" => MouseChordButton.X2,
                _ => (MouseChordButton?)null,                // 含"left"——左键永不合法，落这一分支被拒
            };
            if (btn is null) return null;
        }
        return btn is null ? null : new MouseChordGesture(mods, btn.Value);
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyGesture.ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyGesture.ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & HotkeyGesture.ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyGesture.ModWin) != 0) parts.Add("Win");
        parts.Add(Button.ToString());
        return string.Join("+", parts);
    }

    /// <summary>纯判断：这次按下的按钮 + 当时按住的修饰键位掩码，在整张已绑定的 chord 表里找精确
    /// 匹配的动作。"精确"是关键——跟 RegisterHotKey 对热键的语义一致，held 必须逐位等于某个 chord
    /// 的 Modifiers，不是超集也不是子集。这天然重现了旧版"两个修饰键都按住算歧义"的行为，不需要
    /// 额外特判：Ctrl+Alt 都按住时，held=Ctrl|Alt 跟任何单修饰键的 chord 都对不上，两个都不触发，
    /// 不猜一个赢家。
    ///
    /// 参数类型是具体的 Dictionary，不是 IReadOnlyDictionary——这个判断从 MouseChordService 的
    /// HookProc 热路径调用，接口类型的 foreach 会把结构体枚举器装箱成堆分配；具体类型让编译器
    /// 挑到不装箱的那个重载。表本身在 Apply(Settings) 时就建好，HookProc 里只读不写。
    /// </summary>
    public static WindowShuttleAction? Resolve(
        MouseChordButton button, uint heldModifiers, Dictionary<WindowShuttleAction, MouseChordGesture> chords)
    {
        foreach (var (action, g) in chords)
            if (g.Button == button && g.Modifiers == heldModifiers) return action;
        return null;
    }
}
