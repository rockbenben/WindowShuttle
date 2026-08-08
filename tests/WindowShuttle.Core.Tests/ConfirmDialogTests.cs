using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using ConfirmDialog = WindowShuttle.App.ConfirmDialog;

namespace WindowShuttle.Core.Tests;

/// <summary>round4 Part 3: MessageBox.Show replaced with an in-app dialog. Real Window, real
/// ShowDialog() modal pump — a queued Background-priority callback fires a button Click while the
/// pump is running (the same trick real WPF test suites use for modal dialogs: BeginInvoke at
/// Background priority still runs during ShowDialog's nested DispatcherFrame). Shares the collection's
/// <see cref="WpfTestHost"/> with MainWindow's tests — one shared Application, one shared dispatcher
/// thread, for the same reason as those tests (see WpfTestHost's doc comment).</summary>
[Collection(MainWindowWpfCollection.Name)]
public class ConfirmDialogTests(WpfTestHost host)
{
    /// <summary>
    /// 程序化"点击"一个 Button，必须走 automation peer，不能用 RaiseEvent(ClickEvent)。
    ///
    /// RaiseEvent 只发路由事件，不会调 <c>Button.OnClick()</c>——而 IsCancel / IsDefault 的语义
    /// 恰恰住在 OnClick 里。Confirm 按钮之所以能被 RaiseEvent 驱动，只是因为它在 XAML 上挂了
    /// Click 处理器；Cancel 按钮没有处理器、全靠 IsCancel，于是 RaiseEvent 什么也没发生，
    /// DialogResult 永不被设置，ShowDialog() 永不返回，Join() 无限挂起，整个测试套件跟着死。
    /// IInvokeProvider.Invoke() 走的是真实的 OnClick 路径，与用户点击、以及 Escape 触发 IsCancel
    /// 是同一条路。
    /// </summary>
    private static void Click(ButtonBase button)
    {
        var peer = new ButtonAutomationPeer((Button)button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    /// <summary>ShowDialog() blocks by pushing its own nested DispatcherFrame — running the whole
    /// thing inside <see cref="WpfTestHost.Invoke{T}"/> keeps ConfirmDialog's ContentRendered callback
    /// (which fires while that nested pump is running, see the comment below) on the same dispatcher
    /// thread that owns the shared Application. WpfTestHost.Invoke's own bounded wait still turns "the
    /// dialog never set DialogResult" into one failing test instead of a wedged suite: the dispatcher
    /// keeps servicing its queue — including other tests' Invoke calls — while parked inside
    /// ShowDialog's nested frame, it just never returns from this particular call.</summary>
    private bool? RunModal(Action<ConfirmDialog> duringPump) => host.Invoke(() =>
    {
        var dlg = new ConfirmDialog("test message") { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        // ContentRendered fires once the dialog has actually painted, inside ShowDialog's own
        // modal pump — reliable in a way a pre-queued Background-priority BeginInvoke was not
        // (that version never got serviced on this machine: the dialog just sat open,
        // Responding=true, until something outside the test killed the process).
        dlg.ContentRendered += (_, _) => duringPump(dlg);
        return dlg.ShowDialog();
    });

    [Fact]
    public void Clicking_confirm_returns_true()
    {
        var result = RunModal(dlg => Click(dlg.ConfirmButton));
        Assert.True(result);
    }

    [Fact]
    public void Cancel_button_click_returns_false()
    {
        // CancelButton has no Click handler of its own — IsCancel="True" is what makes clicking it
        // (or, per WPF's documented Button.IsCancel behavior, pressing Escape anywhere in the window)
        // set DialogResult=false and close. This is the same internal path Escape takes, so exercising
        // it via a Click is a faithful stand-in for "the user pressed Escape".
        var result = RunModal(dlg => Click(dlg.CancelButton));
        Assert.False(result);
    }

    [Fact]
    public void Message_text_is_set_from_the_caller_verbatim_not_reworded()
    {
        const string msg = "以管理员身份重启后才能搬动管理员程序的窗口。现在重启？";
        Assert.Equal(msg, host.Invoke(() => new ConfirmDialog(msg).MessageText.Text));
    }

    [Fact]
    public void Confirm_button_is_default_and_cancel_button_is_cancel()
    {
        var (isDefault, isCancel) = host.Invoke(() =>
        {
            var dlg = new ConfirmDialog("test");
            return (dlg.ConfirmButton.IsDefault, dlg.CancelButton.IsCancel);
        });
        Assert.True(isDefault);
        Assert.True(isCancel);
    }
}
