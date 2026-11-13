using Microsoft.Win32;
using WindowShuttle.App;

namespace WindowShuttle.Core.Tests;

/// <summary>开机自启那条注册表值的内容体检。
///
/// 真跑注册表而不是抽象一层出来：这条值的**格式**就是被测对象（少一个 `--tray` 就是每次开机
/// 糊用户一脸窗口），把它藏到接口后面，测的就只剩"我调了我自己"。在独立的测试子键下验证真实注册表行为，用完清理。</summary>
[Collection(MainWindowWpfCollection.Name)]   // 跟别的测试串行：同一个 HKCU 值，不能并发改
public class StartupRegistrationTests(WpfTestHost host) : IDisposable
{
    private readonly string _previous = StartupRegistration.RunKey;
    private readonly string RunKey = NewRunKey();
    private static string NewRunKey()
    {
        string key = @"Software\WindowShuttle.Tests\" + Guid.NewGuid().ToString("N");
        StartupRegistration.RunKey = key;
        return key;
    }
    public void Dispose()
    {
        StartupRegistration.QueryOverride = null;
        StartupRegistration.RunKey = _previous;
        Registry.CurrentUser.DeleteSubKeyTree(RunKey, false);
    }
    [Fact]
    public void Repair_migrates_direct_alias_and_preserves_launcher()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue("old-name", $"\"{Environment.ProcessPath}\"");
        string launcher = $"\"C:\\launcher.exe\" \"{Environment.ProcessPath}\"";
        key.SetValue("launcher", launcher);
        StartupRegistration.RepairRunValue();
        Assert.Null(key.GetValue("old-name"));
        Assert.Equal(launcher, key.GetValue("launcher"));
        Assert.True(StartupRegistration.Get());
        StartupRegistration.Set(false);
        Assert.False(StartupRegistration.Get());
        Assert.Equal(launcher, key.GetValue("launcher"));
    }

    [Fact]
    public async Task Unknown_task_state_cannot_create_a_Run_value()
    {
        StartupRegistration.QueryOverride = () => (false, false);
        try
        {
            await Assert.ThrowsAsync<System.IO.IOException>(() => host.Invoke(() =>
            {
                App.App.Cfg = new Settings();
                var window = new MainWindow();
                window.StartupBox.IsChecked = true;
                return window.ApplyStartupMode();
            }));
            Assert.Null(Read());
        }
        finally { StartupRegistration.QueryOverride = null; }
    }

    [Theory]
    [InlineData(false, true, true, true, false, true)]
    [InlineData(false, true, true, false, true, false)]
    [InlineData(true, true, false, true, true, false)]
    [InlineData(true, true, false, false, false, true)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(true, false, false, false, false, true)]
    public async Task Startup_switches_reflect_acceptance_or_refusal_without_touching_real_tasks(
        bool initiallyElevated, bool wantStartup, bool wantElevated, bool accepted,
        bool expectedRun, bool expectedElevated)
    {
        bool taskExists = initiallyElevated;
        StartupRegistration.QueryOverride = () => (taskExists, true);
        StartupRegistration.Set(!initiallyElevated);
        int changes = 0;
        var window = host.Invoke(() =>
        {
            App.App.Cfg = new Settings();
            var w = new MainWindow();
            w.StartupBox.IsChecked = wantStartup;
            w.ElevatedStartupBox.IsChecked = wantElevated;
            w.ChangeElevatedStartup = on =>
            {
                changes++;
                Assert.Equal(wantStartup && wantElevated, on);
                if (accepted) taskExists = on;
                return Task.FromResult(accepted);
            };
            return w;
        });
        await host.Invoke(window.ApplyStartupMode);
        Assert.Equal(1, changes);
        Assert.Equal(expectedRun, StartupRegistration.Get());
        Assert.Equal(expectedElevated, taskExists);
        host.Invoke(() =>
        {
            Assert.Equal(expectedRun || expectedElevated, window.StartupBox.IsChecked);
            Assert.Equal(expectedElevated, window.ElevatedStartupBox.IsChecked);
            Assert.True(window.StartupBox.IsEnabled);
        });
    }

    [Fact]
    public async Task A_task_operation_failure_restores_the_observed_state_and_propagates_the_error()
    {
        StartupRegistration.QueryOverride = () => (true, true);
        var window = host.Invoke(() =>
        {
            App.App.Cfg = new Settings();
            var w = new MainWindow();
            w.StartupBox.IsChecked = false;
            w.ElevatedStartupBox.IsChecked = false;
            w.ChangeElevatedStartup = _ => Task.FromException<bool>(new System.IO.IOException("task unavailable"));
            return w;
        });
        await Assert.ThrowsAsync<System.IO.IOException>(() => host.Invoke(window.ApplyStartupMode));
        Assert.False(StartupRegistration.Get());
        host.Invoke(() =>
        {
            Assert.True(window.StartupBox.IsChecked);
            Assert.True(window.ElevatedStartupBox.IsChecked);
            Assert.True(window.StartupBox.IsEnabled);
        });
    }

    private const string ValueName = "WindowShuttle";

    private string? Read()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) as string;
    }

    private void Write(string? v)
    {
        // CreateSubKey：全新的用户配置里这个键可能不存在（CI 的 runner 就是），OpenSubKey 会返回
        // null，而 ! 压不住 NullReferenceException。产品侧同一个洞见 SettingsStore.Set。
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (v is null) k.DeleteValue(ValueName, throwOnMissingValue: false);
        else k.SetValue(ValueName, v);
    }

    /// <summary>上一版写进去的是不带参数的裸路径。升级之后 Get() 照样返回 true、复选框照样勾着，
    /// 于是没有任何东西会重写它——而 OnStartup 按"没有 --tray 就是手动启动"判断，每次开机都把主窗口
    /// 弹出来。这是相对上一版的**倒退**（上一版驻留模式从不开窗）。</summary>
    [Fact]
    public void A_legacy_run_value_without_the_tray_flag_is_repaired()
    {
        var saved = Read();
        try
        {
            Write(@"""C:\Somewhere\WindowShuttle.exe""");     // 老格式：没有 --tray
            StartupRegistration.RepairRunValue();
            var now = Read();
            Assert.NotNull(now);
            Assert.Contains("--tray", now);
            Assert.Contains(Environment.ProcessPath!, now);   // 顺带把挪过位置的 exe 路径也纠回来
        }
        finally { Write(saved); }
    }

    /// <summary>没开自启的人，体检不能替他开。这条是这个方法唯一的危险面：写反了就等于
    /// **未经允许把自己设成开机启动**。</summary>
    [Fact]
    public void Repair_never_creates_the_value_when_autostart_is_off()
    {
        var saved = Read();
        try
        {
            Write(null);
            StartupRegistration.RepairRunValue();
            Assert.Null(Read());
        }
        finally { Write(saved); }
    }

    /// <summary>已经是对的就别动它——反复重写注册表没有意义，也会让"这一行是什么时候变的"变得难查。</summary>
    [Fact]
    public void Repair_leaves_a_correct_value_untouched()
    {
        var saved = Read();
        try
        {
            StartupRegistration.Set(true);
            var before = Read();
            StartupRegistration.RepairRunValue();
            Assert.Equal(before, Read());
            Assert.Contains("--tray", Read()!);
        }
        finally { Write(saved); }
    }
}
