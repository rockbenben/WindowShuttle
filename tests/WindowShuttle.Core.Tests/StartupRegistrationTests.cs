using Microsoft.Win32;
using WindowShuttle.App;

namespace WindowShuttle.Core.Tests;

/// <summary>开机自启那条注册表值的内容体检。
///
/// 真跑注册表而不是抽象一层出来：这条值的**格式**就是被测对象（少一个 `--tray` 就是每次开机
/// 糊用户一脸窗口），把它藏到接口后面，测的就只剩"我调了我自己"。用完原样还原。</summary>
[Collection(MainWindowWpfCollection.Name)]   // 跟别的测试串行：同一个 HKCU 值，不能并发改
public class StartupRegistrationTests
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowShuttle";

    private static string? Read()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) as string;
    }

    private static void Write(string? v)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)!;
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
