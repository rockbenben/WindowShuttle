using WindowShuttle.App;

namespace WindowShuttle.Core.Tests;

/// <summary>「管理员程序占前台 = 手势全哑」那条提示的判据。
///
/// 真正想测的那件事（前台真是个提权程序时会不会报）在单测里造不出来——起一个提权进程要过 UAC。
/// 所以这里钉住的是**反方向**：不该报的时候绝不报。这正是这个功能最贵的失败模式：报错了就是给
/// 用户一张"点我以管理员身份重启"的假邀约，而且是在他什么问题都没有的时候。</summary>
public class ForegroundWatchTests
{
    /// <summary>自己不会挡自己。测试进程跟被测代码同完整性，判据必须是"严格高于我"而不是"是不是高
    /// 完整性"——写成后者的话，用户一旦点过「以管理员身份重启」，此后每切一次前台都会弹一张卡，
    /// 而那时手势明明是好的。</summary>
    [Fact] public void Our_own_process_never_counts_as_blocking()
        => Assert.False(ForegroundWatch.BlocksUs((uint)Environment.ProcessId));

    /// <summary>读不到就当作"不比我高"。System 进程（pid 4）在普通权限下连句柄都开不出来——它的
    /// 完整性其实比我们高，但我们无从得知，而这种时候必须闭嘴：宁可漏报也不能凭"打不开"就推断
    /// 提权（本机实测微信也拒绝令牌访问，它根本没提权，手势在它前台时工作正常）。</summary>
    [Fact] public void An_unreadable_process_is_treated_as_not_blocking()
        => Assert.False(ForegroundWatch.BlocksUs(4));

    /// <summary>不存在的 pid 不能把它弄崩——前台窗口的进程可能在我们读它的那一瞬退出了。</summary>
    [Fact] public void A_dead_pid_is_not_blocking_and_does_not_throw()
        => Assert.False(ForegroundWatch.BlocksUs(0xFFFFFFF0));

    /// <summary>普通权限下必须真的把钩子挂上——这条守的是构造函数里那道"自己已提权就不挂"的判断
    /// 别被写反。测试进程是普通权限，所以这里必须是 true。</summary>
    [Fact] public void Watch_is_installed_when_we_are_not_elevated()
    {
        using var w = new ForegroundWatch();
        bool elevated = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Assert.Equal(!elevated, w.Watching);
    }

    [Fact] public void Dispose_unhooks()
    {
        var w = new ForegroundWatch();
        w.Dispose();
        Assert.False(w.Watching);
    }
}
