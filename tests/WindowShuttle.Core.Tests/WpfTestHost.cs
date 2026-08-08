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
