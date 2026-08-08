using System.Globalization;

namespace WindowShuttle.App.I18n;

// 照抄 020-Clockwork 的 Languages（18 门全量清单）。加语言 = 放一个 Strings.<code>.resx + 加一行。
public static class Languages
{
    public static readonly (string Native, string Code)[] All =
    {
        ("中文", "zh-CN"),
        ("繁體中文", "zh-TW"),
        ("English", "en"),
        ("日本語", "ja"),
        ("한국어", "ko"),
        ("Español", "es"),
        ("Français", "fr"),
        ("Deutsch", "de"),
        ("Italiano", "it"),
        ("Português", "pt"),
        ("Русский", "ru"),
        ("العربية", "ar"),
        ("हिन्दी", "hi"),
        ("Bahasa Indonesia", "id"),
        ("Tiếng Việt", "vi"),
        ("ไทย", "th"),
        ("Türkçe", "tr"),
        ("Nederlands", "nl"),
    };

    public static string ResolveForSystem()
    {
        try { return ResolveFor(CultureInfo.InstalledUICulture); }
        catch { return "en"; }
    }

    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return ResolveForSystem();
        var hit = All.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        if (hit.Code != null) return hit.Code;
        try { return ResolveFor(CultureInfo.GetCultureInfo(code)); }
        catch { return ResolveForSystem(); }
    }

    // 把任意文化映射到最接近的受支持语言 code：精确名 → 中文繁简分流 → 两字母 → 回退英文。
    public static string ResolveFor(CultureInfo ci)
    {
        var exact = All.FirstOrDefault(x => string.Equals(x.Code, ci.Name, StringComparison.OrdinalIgnoreCase));
        if (exact.Code != null) return exact.Code;
        if (string.Equals(ci.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
        {
            var n = ci.Name.ToLowerInvariant();
            if (n.Contains("hant") || n.Contains("cht")) return "zh-TW";  // 繁体脚本优先（zh-Hant-* / 旧 zh-CHT）
            if (n.Contains("hans") || n.Contains("chs")) return "zh-CN";  // 简体脚本优先（含 zh-Hans-HK / 旧 zh-CHS）
            return (n.Contains("-tw") || n.Contains("-hk") || n.Contains("-mo")) ? "zh-TW" : "zh-CN";  // 无脚本标记按地区
        }
        var two = All.FirstOrDefault(x => string.Equals(x.Code, ci.TwoLetterISOLanguageName,
            StringComparison.OrdinalIgnoreCase));
        return two.Code ?? "en";
    }
}
