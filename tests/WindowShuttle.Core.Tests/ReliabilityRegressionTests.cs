using System.IO;
using System.Net;
using System.Net.Http;
using WindowShuttle.App;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.Core.Tests;

[Collection(MainWindowWpfCollection.Name)]
public class ReliabilityRegressionTests(WpfTestHost host)
{
    [Fact]
    public void Instance_startup_waits_for_readiness_or_actual_mutex_ownership()
    {
        using var mutex = new Mutex(false, "Local\\WindowShuttle.Tests." + Guid.NewGuid().ToString("N"));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        owner.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var timeout = App.App.AwaitInstance(mutex, () => 0, TimeSpan.FromMilliseconds(60));
            Assert.False(timeout.Acquired);
            Assert.Equal(0, timeout.Sink);
            int polls = 0;
            var ready = App.App.AwaitInstance(mutex, () => ++polls == 3 ? 42 : 0, TimeSpan.FromSeconds(1));
            Assert.False(ready.Acquired);
            Assert.Equal(42, ready.Sink);
        }
        finally { release.Set(); owner.Join(); }
        var acquired = App.App.AwaitInstance(mutex, () => 0, TimeSpan.FromSeconds(1));
        Assert.True(acquired.Acquired);
        mutex.ReleaseMutex();
    }

    [Fact]
    public void Press_target_must_still_belong_to_its_captured_process()
        => host.Invoke(() =>
        {
            var window = new System.Windows.Window();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            Assert.Equal(hwnd, MouseChordService.ValidateTarget(hwnd, (uint)Environment.ProcessId));
            Assert.Equal(0, MouseChordService.ValidateTarget(hwnd, 0));
            Assert.Equal(0, MouseChordService.ValidateTarget(hwnd, uint.MaxValue));
            window.Close();
        });

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public void Non_object_settings_recover_without_changing_the_source(string json)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            Assert.Equal("Ctrl+Alt+1", SettingsStore.Load(path).Hotkeys["Swap"]);
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1366, 696, 1.5)]
    [InlineData(759, 479, 1.0)]
    public void Startup_bounds_fit_work_areas_smaller_than_nominal_minimum(int width, int height, double dpi)
    {
        var monitor = TestData.Mon(1, -200, -100, width, height, primary: true, taskbar: 0, dpi: dpi);
        var (x, y, w, h) = MainWindow.ComputeStartupBounds([monitor], new Settings(), 1120, 900, 760, 480);
        Assert.InRange(w, 1, width);
        Assert.InRange(h, 1, height);
        Assert.True(x >= -200 && y >= -100 && x + w <= -200 + width && y + h <= -100 + height);
    }

    [Theory]
    [InlineData(WindowShuttleAction.Swap)]
    [InlineData(WindowShuttleAction.SwapTop)]
    public void New_primary_cannot_be_its_own_swap_partner(WindowShuttleAction action)
    {
        var a = TestData.Mon(1, 0, 0, 1000, 800, primary: true);
        var b = TestData.Mon(2, 1000, 0, 1000, 800);
        var win = TestData.Win(11, RectPx.FromLTWH(10, 10, 300, 300));
        var result = SwapPlanner.Plan(new([a, b], [win], action, new(20, 20), null, 1, true));
        Assert.Single(result.Moves);
        Assert.True(result.Moves[0].Target.Left >= b.WorkArea.Left);
    }

    [Fact]
    public void Gather_rescues_a_fully_offscreen_window_with_only_one_monitor()
    {
        var monitor = TestData.Mon(1, 0, 0, 1000, 800, primary: true);
        var win = TestData.Win(11, RectPx.FromLTWH(1800, 10, 300, 300));
        var plan = SwapPlanner.Plan(new([monitor], [win], WindowShuttleAction.Gather, default, null, null, true));
        Assert.Single(plan.Moves);
        Assert.Equal(plan.Moves[0].Target.Area, RectPx.OverlapArea(plan.Moves[0].Target, monitor.WorkArea));
    }

    [Fact]
    public void Vertical_map_preserves_alignment_and_order()
    {
        var layout = MainWindow.EffectiveLayout([
            TestData.Mon(2, 0, 800, 1000, 800),
            TestData.Mon(1, 0, 0, 1000, 800, primary: true)]);
        Assert.Equal(layout[0].X, layout[1].X);
        Assert.Equal(layout[0].Y + layout[0].H, layout[1].Y);
    }

    [Theory]
    [InlineData(1, 10, true)]
    [InlineData(0, 10, false)]
    [InlineData(1, -1, false)]
    [InlineData(1, 513, false)]
    [InlineData(1, 9, false)]
    public void Ipc_rejects_unknown_protocol_and_invalid_lengths(int protocol, int length, bool valid)
        => Assert.Equal(valid, App.App.ValidCommandData(protocol, length));

    [Fact]
    public void Failed_move_preserves_previous_undo_bytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "ws-undo-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "undo.json");
        try
        {
            UndoStore.Save(path, new([new(99, ShowState.Normal, new(1, 2, 30, 40))]));
            string old = File.ReadAllText(path);
            var facts = TestData.Win(11, new(10, 10, 100, 100));
            var plan = new MovePlan([new(11, ShowState.Normal, new(200, 10, 290, 100), new(0, 0, 1000, 800))], 0, 0, null, null);
            var result = CommandRouter.CommitWithUndo(plan, [facts], _ => new(0, 0, 1, 0), path: path);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal(old, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".pending"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Final_save_failure_retains_changed_result_and_recoverable_snapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ws-recovery-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "undo.json");
        try
        {
            UndoStore.Save(path, new([new(99, ShowState.Normal, new(1, 2, 30, 40))]));
            var facts = TestData.Win(11, new(10, 10, 100, 100));
            var plan = new MovePlan([new(11, ShowState.Normal, new(200, 10, 290, 100), new(0, 0, 1000, 800))], 0, 0, null, null);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = CommandRouter.CommitWithUndo(plan, [facts],
                    _ => new CommitResult(1, 0, 0, 0) { ChangedWindows = new HashSet<nint> { 11 } }, path: path);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal(1, result.Commit!.Moved);
                Assert.Equal(facts.WindowRect, Assert.Single(UndoStore.Load(path)!.Entries).Target);
            }
            CommandRouter.CommitWithUndo(plan, [facts], _ => new(0, 0, 1, 0), path: path);
            Assert.False(File.Exists(path + ".pending"));
            Assert.Equal(11, Assert.Single(UndoStore.Load(path)!.Entries).Hwnd);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_throwing_result_subscriber_does_not_skip_later_subscribers()
    {
        var router = new CommandRouter();
        bool notified = false;
        router.Executed += (_, _) => throw new InvalidOperationException("subscriber");
        router.Executed += (_, _) => notified = true;
        router.Execute((WindowShuttleAction)999, null);
        Assert.True(notified);
    }

    [Fact]
    public void Explicit_press_target_disappearing_never_selects_the_window_now_under_the_pointer()
    {
        var a = TestData.Mon(1, 0, 0, 1000, 800, primary: true);
        var b = TestData.Mon(2, 1000, 0, 1000, 800);
        var replacement = TestData.Win(22, new(1010, 10, 1200, 200));
        var plan = SwapPlanner.Plan(new([a, b], [replacement], WindowShuttleAction.ToPrimary,
            new(1050, 50), null, null, true, (nint)11));
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Partial_undo_keeps_failed_and_deferred_targets()
    {
        var original = new UndoSnapshot([new(1, ShowState.Normal, new(10, 10, 100, 100)),
            new(2, ShowState.Normal, new(20, 20, 110, 110))]);
        var before = new UndoSnapshot([new(1, ShowState.Normal, new(300, 10, 390, 100)),
            new(2, ShowState.Normal, new(400, 10, 490, 100))]);
        var merged = CommandRouter.MergeUndo(before, original, new HashSet<nint> { 1 });
        Assert.Equal(before.Entries[0], merged.Entries.Single(e => e.Hwnd == 1));
        Assert.Equal(original.Entries[1], merged.Entries.Single(e => e.Hwnd == 2));
    }

    [Fact]
    public void A_launcher_argument_does_not_make_its_command_ours()
    {
        string exe = @"C:\Apps\WindowShuttle.exe";
        Assert.False(StartupRegistration.IsDirectCommand("\"C:\\launcher.exe\" \"" + exe + "\"", exe));
        Assert.True(StartupRegistration.IsDirectCommand("\"" + exe + "\" --tray", exe));
    }

    [Fact]
    public async Task Release_policy_rejects_executable_tags_existing_tags_and_version_regression()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "WindowShuttle.sln")))
            root = Directory.GetParent(root)!.FullName;
        string policy = Path.Combine(root, "tools", "release-policy.ps1").Replace("'", "''");
        string script = """
            $ErrorActionPreference = 'Stop'
            $published = @([pscustomobject]@{tag_name='v2.0.0'; draft=$false; prerelease=$false})
            foreach ($tag in @('v1.2.3$(throw 1)', 'v2.0.0', 'v1.0.0')) {
                $rejected = $false
                try { & '__POLICY__' -Tag $tag -Releases $published }
                catch { $rejected = $true }
                if (-not $rejected) { throw "Accepted $tag" }
            }
            & '__POLICY__' -Tag v2.0.1 -Releases $published
            & '__POLICY__' -Tag v1.0.0 -Releases @()
            # Execute the workflow's real preflight script with an offline, paginated GitHub response.
            function gh {
                if ($args -notcontains '--paginate' -or $args -notcontains '--slurp') { throw 'Missing pagination' }
                $global:LASTEXITCODE = 0
                '[[{"tag_name":"v1.0.0","draft":false,"prerelease":false}],[{"tag_name":"v3.0.0","draft":false,"prerelease":false}]]'
            }
            $env:RELEASE_TAG = 'v2.0.0'
            $env:GITHUB_REPOSITORY = 'test/test'
            $workflow = Get-Content '.github/workflows/ci.yml' -Raw
            $match = [regex]::Match($workflow, '(?s)- name: Validate release intent.*?run: \|\r?\n(.*?)(?=      - name:)')
            if (-not $match.Success) { throw 'Missing release preflight' }
            $preflight = [regex]::Replace($match.Groups[1].Value, '(?m)^          ', '')
            $rejected = $false
            try { & ([scriptblock]::Create($preflight)) }
            catch {
                if ($_.Exception.Message -notlike '*same or newer*') { throw }
                $rejected = $true
            }
            if (-not $rejected) { throw 'Ignored the release on page two' }
            """.Replace("__POLICY__", policy);
        var info = new System.Diagnostics.ProcessStartInfo("pwsh") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(15000)) { process.Kill(true); Assert.Fail("Release policy timed out"); }
        Assert.True(process.ExitCode == 0, (await error) + (await output));
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HeadersOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
    }

    [Fact]
    public async Task Download_body_obeys_cancellation_after_headers_arrive()
    {
        using var client = new HttpClient(new HeadersOnlyHandler());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        string path = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UpdateService.DownloadFileAsync("https://example.invalid/package", path, timeout.Token, client));
        }
        finally { File.Delete(path); }
    }
}
