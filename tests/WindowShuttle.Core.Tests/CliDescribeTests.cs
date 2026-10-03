using WindowShuttle.Core;
using WindowShuttle.Core.Native;
using AppType = WindowShuttle.App.App;   // 别名不能叫 App：会被外层命名空间 WindowShuttle.App 抢先解析
using ExecResult = WindowShuttle.App.ExecResult;

namespace WindowShuttle.Core.Tests;

/// <summary>CLI 结果行的单复数（batch4 R2）。UI 侧的 «(s)» 被 Strings.Plural 的注释明令禁过——
/// 「真实产品里读起来就是没做完」；CLI 是同一张脸，同一个标准。钉的是用户/脚本真会看到的整行：
/// 数词、名词、动词（need/needs）三处都跟着数走。枚举名那一支（nothing to do: X）是脚本 grep
/// 的稳定契约，同样钉住——它被"成人化"了就该红。</summary>
public class CliDescribeTests
{
    private static ExecResult Done(int moved = 0, int denied = 0, int failed = 0, int corrected = 0,
        int skipFs = 0, int skipHung = 0)
        => new(0, new MovePlan([], skipFs, skipHung, null, null),
               new CommitResult(moved, denied, failed, corrected));

    [Fact]
    public void Singular_and_plural_read_like_sentences_not_placeholders()
    {
        Assert.Equal("moved 1 window", AppType.Describe(Done(moved: 1)));
        Assert.Equal("moved 3 windows", AppType.Describe(Done(moved: 3)));
        Assert.DoesNotContain("(s)", AppType.Describe(Done(moved: 2)));
    }

    [Fact]
    public void Verb_agrees_with_the_count_on_admin_rights()
    {
        Assert.Equal("moved 2 windows, 1 needs administrator rights",
            AppType.Describe(Done(moved: 2, denied: 1)));
        Assert.Equal("moved 2 windows, 2 need administrator rights",
            AppType.Describe(Done(moved: 2, denied: 2)));
    }

    [Fact]
    public void Skips_name_their_noun()
    {
        Assert.Equal("moved 1 window, skipped 2 fullscreen windows, skipped 1 not-responding window",
            AppType.Describe(Done(moved: 1, skipFs: 2, skipHung: 1)));
    }

    [Fact]
    public void NoOp_line_keeps_the_enum_token_scripts_grep_for()
    {
        var r = new ExecResult(1, MovePlan.NoOpPlan(NoOpReason.AlreadyOnTarget), null);
        Assert.Equal("nothing to do: AlreadyOnTarget", AppType.Describe(r));
    }
}
