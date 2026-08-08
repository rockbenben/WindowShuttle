using System.Windows.Threading;
using WindowShuttle.App;

namespace WindowShuttle.Core.Tests;

public class MouseChordServiceTests
{
    // 只验证"一个都没绑就不装钩子"这一半——这半用真对象、真 Apply() 就能测，SetWindowsHookExW
    // 压根不会被调到。反过来"绑了就装"那一半经代码检视确认（Apply 里 needed && _hook==0 分支），
    // 不在自动化测试里对着跑测试的这台机器挂一次真的全局鼠标钩子（哪怕转瞬即逝也不做，见
    // ui-round2-report.md 里"逐条验证"那一节）。
    [Fact] public void No_hook_installed_when_no_chord_is_bound()
    {
        var svc = new MouseChordService(Dispatcher.CurrentDispatcher);
        try
        {
            // 显式造一份全空的表，不要用 DefaultMouseChords()——出厂现在默认绑三个手势了，
            // 拿默认值当"全空"的替身，这条测试会从"验证不装钩子"变成"验证会装钩子"，而断言还写着
            // False，等于把它翻成一条永远红的（或者更糟：改了断言之后永远绿而什么也没验）。
            var empty = SettingsStore.DefaultMouseChords().ToDictionary(kv => kv.Key, _ => "");
            svc.Apply(new Settings { MouseChords = empty });
            Assert.False(svc.HookInstalled);
        }
        finally { svc.Dispose(); }
    }

    [Fact] public void No_hook_installed_when_every_bound_chord_string_is_unparseable()
    {
        var svc = new MouseChordService(Dispatcher.CurrentDispatcher);
        try
        {
            // 同上：先清空再只放一条坏的，否则出厂那三个合法手势会让钩子照常装上。
            var chords = SettingsStore.DefaultMouseChords().ToDictionary(kv => kv.Key, _ => "");
            chords["ToPrimary"] = "garbage";           // 手改坏了的配置——解析不出手势，等同未绑定
            svc.Apply(new Settings { MouseChords = chords });
            Assert.False(svc.HookInstalled);
        }
        finally { svc.Dispose(); }
    }

    /// <summary>钩子必须跑在自己的线程上，不能跟 UI 线程共用。
    ///
    /// 这条看着像实现细节，其实是用户能直接感觉到的东西：低级钩子的回调由**装它的那条线程**在取
    /// 消息时同步跑，而系统只等 300ms（LowLevelHooksTimeout）。钩子一旦装在 UI 线程上，任何一次
    /// 稍慢的动作——枚举窗口在这台机器上要 10~30ms、跨进程 SetWindowPos 要同步等目标进程回应——
    /// 都会让全系统的鼠标输入卡在那儿等，超时后钩子还会被静默跳过。
    ///
    /// 把动作 BeginInvoke 给 UI 线程**并不能**解决它：那正是必须来服务这个回调的同一条线程。
    /// 谁要是哪天把 HookThread 化简回 _dispatcher，这条会红。</summary>
    [Fact] public void Hook_runs_on_its_own_thread_not_the_ui_thread()
    {
        var svc = new MouseChordService(Dispatcher.CurrentDispatcher);
        try
        {
            var hookThread = svc.HookThread().Thread;
            Assert.NotEqual(Environment.CurrentManagedThreadId, hookThread.ManagedThreadId);
            Assert.True(hookThread.IsBackground, "钩子线程不能拦着进程退出");
        }
        finally { svc.Dispose(); }
    }
}
