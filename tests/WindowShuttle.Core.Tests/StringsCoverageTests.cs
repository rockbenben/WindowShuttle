using System.Collections;
using System.IO;
using System.Globalization;
using System.Resources;
using WindowShuttle.App.I18n;

namespace WindowShuttle.Core.Tests;

/// <summary>凡是碰 UI 语言的测试都归这一个 collection，且不与别的 collection 并行。
///
/// <see cref="Strings.ApplyCulture"/> 写的是 <see cref="CultureInfo.DefaultThreadCurrentUICulture"/>
/// ——进程级、跨线程。xunit 默认让不同 collection 并行跑，于是本文件把语言切成 zh-CN 的那一刻，
/// 正在别的线程上断言英文文案的 TrayNotifyRulesTests 就当场读到中文。实测约三次一红，而且红的是
/// 一条跟语言毫无关系的测试（"AccessDenied 优先于 OtherFailed"），现场极具误导性——它断言的两边
/// 都走 Strings，本该恒等，只是中间被人换了语言。
///
/// 只让改语言的一方还原是不够的：还原发生在测试之后，而竞态的窗口就在测试之内。必须串行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class UiCultureCollection
{
    public const string Name = "UI culture";
}

/// <summary>切完语言必须还原：这些测试之后还有别的 collection 要跑，把进程留在 en 或 zh-CN 上，
/// 下一个读文案的测试就得看运气。</summary>
[Collection(UiCultureCollection.Name)]
public class StringsCoverageTests : IDisposable
{
    private readonly CultureInfo _entry = CultureInfo.CurrentUICulture;

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _entry;
        CultureInfo.DefaultThreadCurrentUICulture = _entry;
    }

    private static HashSet<string> Keys(CultureInfo? ci)
    {
        var rm = new ResourceManager("WindowShuttle.App.Resources.Strings", typeof(Strings).Assembly);
        var set = rm.GetResourceSet(ci ?? CultureInfo.InvariantCulture, true, true)!;
        return set.Cast<DictionaryEntry>().Select(e => (string)e.Key!).ToHashSet();
    }

    /// <summary>每门语言都要有自己的 resx 且键集与中性（中文）完全一致。tryParents 关掉：开着的话
    /// 缺失的 resx 会静默回退到中性资源，键集恒等，整条测试空转。</summary>
    public static TheoryData<string> NonNeutralLanguages()
    {
        var d = new TheoryData<string>();
        foreach (var (_, code) in Languages.All) if (code != "zh-CN") d.Add(code);
        return d;
    }

    [Theory]
    [MemberData(nameof(NonNeutralLanguages))]
    public void Every_language_covers_every_neutral_key(string code)
    {
        var rm = new ResourceManager("WindowShuttle.App.Resources.Strings", typeof(Strings).Assembly);
        var set = rm.GetResourceSet(CultureInfo.GetCultureInfo(code), true, false);
        Assert.True(set != null, $"no satellite resources for {code} — missing Strings.{code}.resx?");
        var keys = set!.Cast<DictionaryEntry>().Select(e => (string)e.Key!).ToHashSet();
        var neutral = Keys(null);
        Assert.True(neutral.SetEquals(keys),
            $"missing in {code}: " + string.Join(", ", neutral.Except(keys))
            + $" | extra in {code}: " + string.Join(", ", keys.Except(neutral)));
    }

    [Fact] public void Neutral_lookup_returns_chinese()
    {
        Strings.ApplyCulture("zh-CN");
        Assert.Equal("撤销", Strings.Get("Action_Undo"));
    }

    [Fact] public void English_lookup_returns_english()
    {
        Strings.ApplyCulture("en");
        Assert.Equal("Undo", Strings.Get("Action_Undo"));
    }

    // A missing key must surface the key itself (visible typo), not go silently blank.
    [Fact] public void Get_missing_key_returns_key_itself()
        => Assert.Equal("No_Such_Key_Xyz", Strings.Get("No_Such_Key_Xyz"));

    /// <summary>每一条 *_One 单数键都必须有对应的复数键，反之亦然——<see cref="Strings.Plural"/> 靠
    /// 拼接键名找单数形式，缺哪一半都不会编译报错，只会在运行时悄悄退化成显示键名本身。</summary>
    [Fact]
    public void Every_singular_key_has_its_plural_partner()
    {
        var keys = Keys(null);
        var singulars = keys.Where(k => k.EndsWith("_One")).ToList();
        Assert.NotEmpty(singulars);                       // 一条都没有就说明这条测试在空转
        var orphans = singulars.Where(k => !keys.Contains(k[..^4])).ToList();
        Assert.True(orphans.Count == 0, "有 _One 却没有对应复数键: " + string.Join(", ", orphans));
    }

    /// <summary>resx 里不该留没人引用的键。这不是洁癖：死键会被翻译、被 review、被当成还在用的文案
    /// 维护，而它对应的界面早就没了——这一版删掉的 Settings_Title / Main_Hotkey_Col / Main_Mouse_Col
    /// 就是这么来的（设置页不存在了，两列表头被拿掉了），没有任何东西提醒过它们已经死了。</summary>
    [Fact]
    public void No_resource_key_is_unreferenced_by_the_app()
    {
        // 只扫源码目录，不从仓库根往下漫游：根下面有 .git、.superpowers、tools/*/bin 这些跟本测试
        // 无关的树，走进去既慢又多一堆可能读不了的东西。
        var src = new[] { "src", "tools" }
            .Select(d => Path.Combine(RepoRoot(), d))
            .Where(Directory.Exists)
            .SelectMany(d => SourceFiles(d))
            .Select(File.ReadAllText)
            .ToList();
        Assert.NotEmpty(src);                          // 路径找错了就该红，而不是"零个文件里没找到引用"
        // 有三族键是在运行时拼出来的，源码里永远搜不到字面量。它们不是被豁免，而是拿应用自己的
        // 那份清单去核对——直接豁免 "Action_" 前缀的话，一条对不上任何动作的 Action_Foo_Desc 就再也
        // 抓不到了，而那正是这条测试要防的东西。
        var composed = new HashSet<string>(StringComparer.Ordinal);
        // 名字：托盘菜单里出现的每一个动作都要有。
        foreach (var (resxKey, _) in WindowShuttle.App.TrayService.ActionMenuSpec)
            composed.Add(resxKey);                                   // $"Action_{ShortKey(key)}"
        // 描述：只有**动作列表里有一行**的动作才有描述。这两族不再重合——「屏幕序号」进得了托盘，
        // 却没有动作行（它的入口是地图栏那个按钮），所以它有名字、没有描述。
        foreach (var key in WindowShuttle.App.MainWindow.ActionKeys)
        {
            var stem = $"Action_{(key == "FixStraddling" ? "Fix" : key)}";
            composed.Add(stem);
            composed.Add(stem + "_Desc");
        }
        foreach (var reason in Enum.GetNames<NoOpReason>()) composed.Add("NoOp_" + reason);
        foreach (var k in Keys(null).Where(k => k.EndsWith("_One"))) composed.Add(k);   // Strings.Plural

        var dead = Keys(null)
            .Where(k => !composed.Contains(k))
            .Where(k => !src.Any(t => t.Contains(k, StringComparison.Ordinal)))
            .OrderBy(k => k)
            .ToList();
        Assert.True(dead.Count == 0, "resx 里有没人引用的键: " + string.Join(", ", dead));

        // 反向：拼出来的键必须真的存在于 resx，否则界面上会显示键名本身。
        var missing = composed.Where(k => !Keys(null).Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "代码会拼出但 resx 里没有的键: " + string.Join(", ", missing));
    }

    /// <summary>递归列出一棵源码树里的 .cs / .xaml，跳过 bin/obj。
    ///
    /// 用 <see cref="EnumerationOptions"/> 重载，不用 <c>SearchOption.AllDirectories</c>：后者走的是
    /// 兼容语义（<c>IgnoreInaccessible = false</c>），任意一个子目录读不了就整棵树抛异常——一个叶子
    /// 的失败被升级成"这台机器没有源码"。<c>ReparsePoint</c> 一并跳掉，避免符号链接把同一棵树数两遍。
    ///
    /// 注意 <c>AttributesToSkip</c> 一旦显式赋值，默认的 Hidden|System 就不再自动生效，要写全。</summary>
    private static IEnumerable<string> SourceFiles(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var sep = Path.DirectorySeparatorChar;
        return new[] { "*.cs", "*.xaml" }
            .SelectMany(pat => Directory.EnumerateFiles(root, pat, opts))
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    /// <summary>从测试程序集所在目录往上找到仓库根（含 WindowShuttle.sln 的那一层）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WindowShuttle.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根（WindowShuttle.sln）");
    }
}
