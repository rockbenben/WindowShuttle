using System.Text.RegularExpressions;
using WindowShuttle.Core;

namespace WindowShuttle.Core.Tests;

public class CliParserTests
{
    [Theory]
    [InlineData("swap", WindowShuttleAction.Swap)]
    [InlineData("swap-top", WindowShuttleAction.SwapTop)]
    [InlineData("to-primary", WindowShuttleAction.ToPrimary)]
    [InlineData("to-next", WindowShuttleAction.ToNext)]
    [InlineData("gather", WindowShuttleAction.Gather)]
    [InlineData("undo", WindowShuttleAction.Undo)]
    [InlineData("identify", WindowShuttleAction.Identify)]
    [InlineData("list", WindowShuttleAction.List)]
    public void Verbs_parse(string verb, WindowShuttleAction expected)
    {
        var c = CliParser.Parse([verb]);
        Assert.NotNull(c);
        Assert.Equal(expected, c!.Action);
        Assert.Null(c.ToMonitor);
    }

    [Fact] public void Swap_accepts_to_option()
    {
        var c = CliParser.Parse(["swap", "--to", "2"]);
        Assert.Equal(WindowShuttleAction.Swap, c!.Action);
        Assert.Equal(2, c.ToMonitor);
    }

    // "移动到指定屏幕"的 CLI 形态：to-next --to <n> 直送 n 号屏，与 swap --to 同一套语法。
    [Fact] public void ToNext_accepts_to_option()
    {
        var c = CliParser.Parse(["to-next", "--to", "3"]);
        Assert.Equal(WindowShuttleAction.ToNext, c!.Action);
        Assert.Equal(3, c.ToMonitor);
    }

    [Fact] public void Verbs_are_case_insensitive()
        => Assert.Equal(WindowShuttleAction.Gather, CliParser.Parse(["GATHER"])!.Action);

    [Theory]
    [InlineData(new object[] { new[] { "nope" } })]
    [InlineData(new object[] { new[] { "swap", "--to" } })]          // 缺值
    [InlineData(new object[] { new[] { "swap", "--to", "x" } })]     // 非数字
    [InlineData(new object[] { new[] { "gather", "--to", "2" } })]   // --to 只属于 swap/to-next
    [InlineData(new object[] { new[] { "to-primary", "--to", "2" } })]
    [InlineData(new object[] { new[] { "swap", "extra" } })]
    public void Garbage_returns_null(string[] args)
        => Assert.Null(CliParser.Parse(args));

    /// <summary>帮助里列出的每一条命令都必须真的能解析。
    ///
    /// 这条也是补票来的：砍掉 fix-straddling / save-layout / restore-layout 时，Parse 里的映射删了，
    /// Usage 那段字符串忘了删——`--help` 照旧把三条已经不存在的命令推荐给用户，敲进去只会得到
    /// "unknown command"。帮助文本和解析器是同一件事的两种说法，必须对得上。</summary>
    [Fact]
    public void Every_command_the_help_advertises_actually_parses()
    {
        // 帮助正文里每行开头那个命令词：两个空格缩进 + 命令名，后面跟空格或 [
        var advertised = Regex.Matches(CliParser.Usage, @"^ {2}(?<cmd>[a-z][a-z-]*)(?= |\[|$)", RegexOptions.Multiline)
            .Select(m => m.Groups["cmd"].Value)
            .Where(c => c != "usage")
            .ToList();
        Assert.NotEmpty(advertised);                       // 正则失配就该红，而不是"零条命令全部通过"
        Assert.All(advertised, c => Assert.True(CliParser.Parse([c]) is not null,
            $"帮助里写着 `{c}`，但解析器不认识它"));
    }

    /// <summary>反过来：解析器认的每个动作也都要在帮助里露面，否则那条命令只有读源码才发现得了。</summary>
    [Fact]
    public void Every_parsable_action_is_advertised_in_the_help()
    {
        foreach (var a in Enum.GetValues<WindowShuttleAction>())
        {
            // 命令名 = 动作名转 kebab-case（swap-top、to-primary…）
            var cmd = Regex.Replace(a.ToString(), "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
            if (CliParser.Parse([cmd]) is null) continue;   // 这个动作没有命令行入口，跳过
            Assert.Contains("  " + cmd, CliParser.Usage);
        }
    }
}
