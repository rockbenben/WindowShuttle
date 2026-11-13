using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using WindowShuttle.App;
using WindowShuttle.Core;
using WindowShuttle.Core.Native;

namespace WindowShuttle.Core.Tests;

[Collection(MainWindowWpfCollection.Name)]
public class UndoIdentityTests(WpfTestHost host)
{
    private static readonly WindowIdentity Identity = new(123, 456, 789);
    private static readonly MonitorInfo Monitor = TestData.Mon(1, 0, 0, 1920, 1080, primary: true);
    private static readonly RectPx Target = new(100, 100, 400, 400);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetPropW(nint hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint RemovePropW(nint hwnd, string name);

    private static HwndSource MakeWindow()
        => new(new HwndSourceParameters("WindowShuttle undo identity regression") {
            Width = 200, Height = 120, PositionX = -30000, PositionY = -30000, WindowStyle = 0 });

    [Fact]
    public void Identity_survives_serialization_and_repeated_capture_of_the_same_window()
        => host.Invoke(() =>
        {
            using var window = MakeWindow();
            var identity = WindowIdentityProbe.Capture(window.Handle);
            Assert.NotNull(identity);
            var snapshot = new UndoSnapshot([new((long)window.Handle, ShowState.Normal, Target, identity)]);
            var restored = UndoStore.Deserialize(UndoStore.Serialize(snapshot))!;
            Assert.Equal(identity, Assert.Single(restored.Entries).Identity);
            Assert.Equal(identity, WindowIdentityProbe.Capture(window.Handle));
            Assert.Equal(WindowIdentityStatus.Match, WindowIdentityProbe.Inspect(window.Handle, restored.Entries[0].Identity));
        });

    [Fact]
    public async Task A_separate_process_can_verify_the_persisted_identity()
    {
        var window = host.Invoke(MakeWindow);
        try
        {
            var identity = host.Invoke(() => WindowIdentityProbe.Capture(window.Handle))!;
            Assert.NotNull(identity);
            string assembly = typeof(WindowIdentityProbe).Assembly.Location.Replace("'", "''");
            string script = $$"""
                $ErrorActionPreference = 'Stop'
                [void][System.Reflection.Assembly]::LoadFrom('{{assembly}}')
                $identity = [WindowShuttle.Core.WindowIdentity]::new([uint32]{{identity.ProcessId}}, [long]{{identity.ProcessCreated}}, [long]{{identity.WindowToken}})
                $result = [WindowShuttle.Core.Native.WindowIdentityProbe]::Inspect([intptr]{{window.Handle}}, $identity)
                if ($result.ToString() -ne 'Match') { throw "Identity mismatch: $result" }
                """;
            var info = new ProcessStartInfo("pwsh") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            Assert.True(process.ExitCode == 0, (await errors) + (await output));
        }
        finally { host.Invoke(window.Dispose); }
    }

    [Fact]
    public void Same_process_window_replacement_cannot_inherit_the_old_token()
        => host.Invoke(() =>
        {
            using var first = MakeWindow();
            using var replacement = MakeWindow();
            var identity = WindowIdentityProbe.Capture(first.Handle)!;
            var other = WindowIdentityProbe.Capture(replacement.Handle)!;
            Assert.Equal(identity.ProcessId, other.ProcessId);
            Assert.Equal(identity.ProcessCreated, other.ProcessCreated);
            Assert.NotEqual(identity.WindowToken, other.WindowToken);
            Assert.Equal(WindowIdentityStatus.Different, WindowIdentityProbe.Inspect(replacement.Handle, identity));
            // Reuse of the numeric HWND has the same observable state: its old property no longer exists.
            RemovePropW(first.Handle, WindowIdentityProbe.PropertyName);
            Assert.Equal(WindowIdentityStatus.Unavailable, WindowIdentityProbe.Inspect(first.Handle, identity));
            Assert.Equal(0, GetPropW(first.Handle, WindowIdentityProbe.PropertyName));
        });

    [Fact]
    public void Closed_windows_and_reused_process_ids_are_rejected()
        => host.Invoke(() =>
        {
            var window = MakeWindow();
            nint hwnd = window.Handle;
            var identity = WindowIdentityProbe.Capture(hwnd)!;
            Assert.Equal(WindowIdentityStatus.Different,
                WindowIdentityProbe.Inspect(hwnd, identity with { ProcessCreated = identity.ProcessCreated + 1 }));
            Assert.Equal(WindowIdentityStatus.Different,
                WindowIdentityProbe.Inspect(hwnd, identity with { ProcessId = uint.MaxValue }));
            window.Dispose();
            Assert.Equal(WindowIdentityStatus.Different, WindowIdentityProbe.Inspect(hwnd, identity));
        });

    [Fact]
    public void Unknown_or_incomplete_identity_does_not_stamp_the_current_window()
        => host.Invoke(() =>
        {
            using var window = MakeWindow();
            Assert.Equal(WindowIdentityStatus.Unavailable, WindowIdentityProbe.Inspect(window.Handle, null));
            Assert.Equal(WindowIdentityStatus.Unavailable, WindowIdentityProbe.Inspect(window.Handle, new(0, 0, 0)));
            Assert.Equal(0, GetPropW(window.Handle, WindowIdentityProbe.PropertyName));
            Assert.Null(WindowIdentityProbe.Capture(0));
        });

    [Fact]
    public void A_valid_persisted_snapshot_supports_undo_and_redo_of_a_hidden_test_window()
        => host.Invoke(() =>
        {
            using var window = MakeWindow();
            string root = Path.Combine(Path.GetTempPath(), "ws-identity-roundtrip-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "undo.json");
            try
            {
                Win32.GetWindowRect(window.Handle, out var initial);
                var before = Win32.ToRect(initial);
                var target = RectPx.FromLTWH(before.Left + 30, before.Top + 20, before.Width, before.Height);
                var monitor = TestData.Mon(1, -32000, -32000, 4000, 4000, primary: true, taskbar: 0);
                var facts = TestData.Win(window.Handle, before);
                var plan = new MovePlan([new(window.Handle, ShowState.Normal, target, monitor.WorkArea)], 0, 0, null, null);
                Assert.Equal(0, CommandRouter.CommitWithUndo(plan, [facts], path: path).ExitCode);
                var identity = Assert.Single(UndoStore.Load(path)!.Entries).Identity;
                Assert.NotNull(identity);
                Win32.GetWindowRect(window.Handle, out var moved);
                Assert.Equal(target, Win32.ToRect(moved));
                var result = CommandRouter.RestoreUndo(UndoStore.Load(path)!, [facts with { WindowRect = target }],
                    [monitor], true, path: path);
                Assert.Equal(0, result.ExitCode);
                Win32.GetWindowRect(window.Handle, out var undone);
                Assert.Equal(before, Win32.ToRect(undone));
                Assert.Equal(identity, Assert.Single(UndoStore.Load(path)!.Entries).Identity);
                Assert.Equal(0, CommandRouter.RestoreUndo(UndoStore.Load(path)!, [facts], [monitor], true, path: path).ExitCode);
                Win32.GetWindowRect(window.Handle, out var redone);
                Assert.Equal(target, Win32.ToRect(redone));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });

    [Fact]
    public void Even_a_directly_constructed_legacy_plan_cannot_move_a_native_window()
        => host.Invoke(() =>
        {
            using var window = MakeWindow();
            var legacy = new UndoSnapshot([new((long)window.Handle, ShowState.Normal, Target)]);
            Win32.GetWindowRect(window.Handle, out var before);
            var result = WindowCommitter.Commit(UndoStore.ToPlan(legacy, [Monitor]));
            Assert.Equal(0, result.Moved);
            Assert.Equal(1, result.OtherFailed);
            Win32.GetWindowRect(window.Handle, out var after);
            Assert.Equal(Win32.ToRect(before), Win32.ToRect(after));
            Assert.Equal(0, GetPropW(window.Handle, WindowIdentityProbe.PropertyName));
        });

    [Theory]
    [InlineData(ShowState.Normal)]
    [InlineData(ShowState.Maximized)]
    [InlineData(ShowState.Minimized)]
    public void Commit_rechecks_identity_after_planning_and_cannot_change_a_replacement(ShowState state)
        => host.Invoke(() =>
        {
            using var window = MakeWindow();
            var identity = WindowIdentityProbe.Capture(window.Handle)!;
            Win32.GetWindowRect(window.Handle, out var before);
            var snapshot = new UndoSnapshot([new((long)window.Handle, state, Target, identity)]);
            var plan = UndoStore.ToPlan(snapshot, [Monitor]);
            RemovePropW(window.Handle, WindowIdentityProbe.PropertyName);
            var result = WindowCommitter.Commit(plan);
            Assert.Equal(0, result.Moved);
            Assert.Equal(1, result.OtherFailed);
            Assert.Empty(result.ChangedWindows);
            Win32.GetWindowRect(window.Handle, out var after);
            Assert.Equal(Win32.ToRect(before), Win32.ToRect(after));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_json_is_readable_and_retained_but_cannot_move_the_current_occupant(bool pending)
    {
        const string json = """{"Entries":[{"Hwnd":11,"ShowState":0,"Target":{"Left":100,"Top":100,"Right":400,"Bottom":400}}]}""";
        var snapshot = UndoStore.Deserialize(json)!;
        Assert.Null(Assert.Single(snapshot.Entries).Identity);
        string root = Path.Combine(Path.GetTempPath(), "ws-legacy-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "undo.json");
        Directory.CreateDirectory(root);
        try
        {
            string originalPath = pending ? path + ".pending" : path;
            File.WriteAllText(originalPath, json);
            var facts = TestData.Win(11, new(500, 100, 800, 400));
            var result = CommandRouter.RestoreUndo(UndoStore.Load(path)!, [facts], [Monitor], true,
                _ => throw new InvalidOperationException("Legacy identity must not be inferred"),
                _ => throw new InvalidOperationException("No window may move"), path);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(json, File.ReadAllText(originalPath));
            Assert.Equal(pending, File.Exists(path + ".pending"));
            Assert.Equal(!pending, File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identity_is_written_to_normal_and_recovery_snapshots(bool failFinalSave)
    {
        string root = Path.Combine(Path.GetTempPath(), "ws-identity-save-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "undo.json");
        Directory.CreateDirectory(root);
        try
        {
            UndoStore.Save(path, new([]));
            using var locked = failFinalSave ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            var facts = TestData.Win(11, new(500, 100, 800, 400));
            var plan = new MovePlan([new(11, ShowState.Normal, Target, Monitor.WorkArea)], 0, 0, null, null);
            var result = CommandRouter.CommitWithUndo(plan, [facts],
                _ => new(1, 0, 0, 0) { ChangedWindows = new HashSet<nint> { 11 } },
                path: path, captureIdentity: _ => Identity);
            Assert.Equal(failFinalSave ? 3 : 0, result.ExitCode);
            Assert.Equal(Identity, Assert.Single(UndoStore.Load(path)!.Entries).Identity);
            Assert.Equal(failFinalSave, File.Exists(path + ".pending"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Capture_failure_keeps_the_move_result_but_never_authorizes_later_undo()
    {
        string root = Path.Combine(Path.GetTempPath(), "ws-identity-missing-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "undo.json");
        try
        {
            var facts = TestData.Win(11, new(500, 100, 800, 400));
            var plan = new MovePlan([new(11, ShowState.Normal, Target, Monitor.WorkArea)], 0, 0, null, null);
            var result = CommandRouter.CommitWithUndo(plan, [facts],
                _ => new(1, 0, 0, 0) { ChangedWindows = new HashSet<nint> { 11 } },
                path: path, captureIdentity: _ => null);
            Assert.Equal(0, result.ExitCode);
            var undo = CommandRouter.RestoreUndo(UndoStore.Load(path)!, [facts], [Monitor], true,
                commit: _ => throw new InvalidOperationException("No unverifiable undo"), path: path);
            Assert.Equal(1, undo.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Mixed_undo_keeps_unknown_legacy_and_hung_entries_and_drops_confirmed_replacements()
    {
        string root = Path.Combine(Path.GetTempPath(), "ws-identity-mixed-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "undo.json");
        var snapshot = new UndoSnapshot([
            new(1, ShowState.Normal, Target, Identity),
            new(2, ShowState.Normal, Target, Identity),
            new(3, ShowState.Normal, Target, Identity),
            new(4, ShowState.Normal, Target),
            new(5, ShowState.Normal, Target, Identity)]);
        try
        {
            var windows = Enumerable.Range(1, 5).Select(i => TestData.Win(i, new(500, 100, 800, 400), hung: i == 5)).ToList();
            var result = CommandRouter.RestoreUndo(snapshot, windows, [Monitor], true,
                entry => entry.Hwnd switch { 2 => WindowIdentityStatus.Different, 3 => WindowIdentityStatus.Unavailable, _ => WindowIdentityStatus.Match },
                plan =>
                {
                    Assert.Equal((nint)1, Assert.Single(plan.Moves).Hwnd);
                    Assert.Equal(Identity, plan.Moves[0].ExpectedIdentity);
                    Assert.True(plan.Moves[0].RequireIdentity);
                    return new(1, 0, 0, 0) { ChangedWindows = new HashSet<nint> { 1 } };
                }, path);
            Assert.Equal(0, result.ExitCode);
            var entries = UndoStore.Load(path)!.Entries;
            Assert.Equal(new long[] { 1, 3, 4, 5 }, entries.Select(e => e.Hwnd));
            Assert.Equal(Identity, entries[0].Identity);
            Assert.Equal(windows[0].WindowRect, entries[0].Target);
            Assert.Equal(Target, entries.Single(e => e.Hwnd == 3).Target);
            Assert.Null(entries.Single(e => e.Hwnd == 4).Identity);
        }
        finally { Directory.Delete(root, true); }
    }
}
