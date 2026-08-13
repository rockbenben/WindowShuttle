using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WindowShuttle.Core.Tests;

/// <summary>Everything in this assembly that touches real WPF-UI controls (<c>ui:TitleBar</c>,
/// <c>ui:Button</c>, <c>ui:Card</c>, the Fluent-restyled <c>CheckBox</c>/<c>ComboBox</c>) needs an
/// <see cref="Application"/> carrying WPF-UI's <c>ThemesDictionary</c>/<c>ControlsDictionary</c> —
/// production gets that from <c>App.xaml</c>, but the test project never creates an Application, so
/// those controls have no template, measure to (0,0), and the whole visual tree they sit in collapses
/// (not just those controls — the ancestor Grid too).
///
/// <see cref="Application"/> is one-per-AppDomain and thread-affine, so this fixture owns exactly one
/// background STA thread for the life of the collection, creates the one Application on it before
/// anything else runs, and pumps its Dispatcher. Every test marshals its WPF work onto that thread via
/// <see cref="Invoke(Action)"/>/<see cref="Invoke{T}(Func{T})"/> instead of spinning its own thread.
/// One shared dispatcher also means the app's static SolidColorBrush fields (see AppTheme.Frozen /
/// NotificationOverlay.Frozen) only ever get first touched by one thread for the whole run — the
/// separate-thread-per-test scheme this replaces was the actual reason those Freeze() calls were load-
/// bearing (a non-frozen Freezable binds permanently to whichever thread first touches it).</summary>
/// <summary>把一扇窗按**指定尺寸**显示出来，不受跑测试这台机器的屏幕大小影响。
///
/// 为什么需要它：MainWindow 在 <c>OnSourceInitialized</c> 里调 <c>ApplyStartupBounds</c>，直接用
/// <c>SetWindowPos</c> 按**真实**屏幕的工作区把物理矩形钉死（首次启动的落位逻辑，见
/// ComputeStartupBounds 的说明）。测试在构造时设的 Width/Height 因此只是"想要"——真实屏幕比它小时，
/// Show() 期间就被那次 SetWindowPos 覆盖掉了。
///
/// 本机三块大屏上从来看不出来，换到 GitHub 的 windows runner（虚拟显示器约 1024×768）就当场垮掉：
/// 第一次推上去红了 6 条，全是同一个根因——"窗口只有 976×672，要 1120×680"，以及被夹窄之后文案
/// 多折一行、动作区放不下两张卡。这套测试此前从没在别的机器上跑过（仓库一直没有 remote），
/// 所以这个对显示器的隐性依赖藏了很久。
///
/// 解法：Show() 之后再定尺寸——那时 ApplyStartupBounds 已经跑完，后设的值说了算。跟 --shots 里
/// 「必须 Show 之后设，否则被真实显示器的值覆盖」是同一招（见 App.SelfCheck.cs）。顺序不能换。
///
/// 顺带把 MaxWidth/MaxHeight 解开：WPF 的默认值本来就是无穷，这两行是防御性的——哪天窗口自己开始
/// 按工作区设上限（--shots 就是这么模拟矮屏的），这里不至于又被无声地夹回去。</summary>
public sealed class WpfTestHost : IDisposable
{
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(20);
    private readonly Dispatcher _dispatcher;

    private readonly string _home;

    public WpfTestHost()
    {
        // 先把落地目录挪到临时目录再做任何别的事。这个 fixture 底下的测试都会真的建 MainWindow，
        // 而关窗会经 OnClosing → SaveWindowBounds 写 SettingsStore.DefaultPath——不改写的话，
        // 开发机上每跑一次 dotnet test 就把本机真实的 %APPDATA%\WindowShuttle\settings.json 覆盖成
        // 测试里那份 new Settings()，用户自己录的快捷键和鼠标手势当场全没（实测踩到过）。
        _home = Path.Combine(Path.GetTempPath(), "windowshuttle-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
        WindowShuttle.App.SettingsStore.HomeOverride = _home;

        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            // Application's ctor sets Application.Current and must run before any control that
            // resolves a DynamicResource against it — that is the WPF-UI chrome this host exists to
            // provide. Mirrors App.xaml's own two merged dictionaries exactly (see that file).
            //
            // ShutdownMode must match App.xaml's OnExplicitShutdown too: the default,
            // OnLastWindowClose, tears the Application down the moment the first test's Window.Close()
            // drops the last open window — every later test's InitializeComponent() then throws "The
            // Application object is being shut down" trying to load its XAML off a dead Application.
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
                Resources = new ResourceDictionary
                {
                    MergedDictionaries =
                    {
                        new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light },
                        new Wpf.Ui.Markup.ControlsDictionary(),
                    },
                },
            };
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    /// <summary>Runs <paramref name="action"/> on the shared WPF thread and waits for it, bounded so a
    /// test that hangs the pump (e.g. a modal dialog that never sets DialogResult) fails just that one
    /// test instead of wedging every other test sharing this host.</summary>
    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    public T Invoke<T>(Func<T> func)
    {
        var operation = _dispatcher.InvokeAsync(func, DispatcherPriority.Normal);
        if (operation.Task.Wait(InvokeTimeout) == false)
            throw new TimeoutException(
                "WpfTestHost.Invoke timed out — a test hung the shared WPF dispatcher.");
        return operation.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dispatcher.InvokeShutdown();
        WindowShuttle.App.SettingsStore.HomeOverride = null;
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { /* 临时目录，删不掉就算了 */ }
    }
}

/// <summary>xunit runs different test collections in parallel by default; every test class here that
/// touches real WPF (MainWindow, ConfirmDialog, NotificationOverlay) shares this one collection so they
/// all share the single <see cref="WpfTestHost"/> instance xunit constructs once and disposes once the
/// collection is done.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class MainWindowWpfCollection : ICollectionFixture<WpfTestHost>
{
    public const string Name = "MainWindow WPF";
}
