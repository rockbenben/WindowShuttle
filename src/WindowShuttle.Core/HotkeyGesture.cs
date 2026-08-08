namespace WindowShuttle.Core;

/// <summary>热键组合。Modifiers/VirtualKey 直接对应 RegisterHotKey 的 fsModifiers/vk。</summary>
public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    public static HotkeyGesture? TryParse(string s)
    {
        uint mods = 0, vk = 0;
        foreach (var raw in s.Split('+'))
        {
            var tok = raw.Trim();
            if (tok.Length == 0) return null;
            switch (tok.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModControl; continue;
                case "alt": mods |= ModAlt; continue;
                case "shift": mods |= ModShift; continue;
                case "win": mods |= ModWin; continue;
            }
            if (vk != 0) return null;                       // 两个主键
            vk = KeyToVk(tok);
            if (vk == 0) return null;
        }
        return mods == 0 || vk == 0 ? null : new HotkeyGesture(mods, vk);
    }

    private static uint KeyToVk(string k)
    {
        k = k.ToUpperInvariant();
        if (k.Length == 1 && (char.IsAsciiLetter(k[0]) || char.IsAsciiDigit(k[0]))) return k[0];
        if (k.Length is 2 or 3 && k[0] == 'F'
            && int.TryParse(k[1..], out int f) && f is >= 1 and <= 24)
            return (uint)(0x6F + f);                        // VK_F1 = 0x70
        return 0;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(VirtualKey is >= 0x70 and <= 0x87
            ? $"F{VirtualKey - 0x6F}" : ((char)VirtualKey).ToString());
        return string.Join("+", parts);
    }
}
