using System.IO;
using MainWindow = WindowShuttle.App.MainWindow;
using Settings = WindowShuttle.App.Settings;

namespace WindowShuttle.Core.Tests;

/// <summary>语言下拉的「先问，确认了才落盘」（batch1 B3）。被钉的是旧实现说谎的那两面：
/// 旧顺序是先写 settings.json 再弹确认框，点「取消」之后配置已改（下次开机真的换语言），
/// 而下拉框停在新语言上、界面仍是旧语言（看着像没生效）——两面各错各的。
///
/// 断言全部落在用户可感的结果上，且都走**真实事件路径**（设 SelectedIndex 抬 SelectionChanged），
/// 不是直调内部方法：处理器的守卫顺序本身就是行为的一部分。确认框与重启走注入缝
/// （ConfirmBox / RestartApp），测试里绝不允许真弹窗、真重启。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class MainWindowLangChoiceTests(WpfTestHost host)
{
    /// <summary>「跟随系统」之后的第一个真实语言项对应下拉 index 1 → Languages.All[0]。
    /// 取表不取数：改语言清单时这条测试跟着指到新的第一项，而不是悄悄钉死在某门语言上。</summary>
    private static int FirstRealChoice => 1;
    private static string FirstRealCode => WindowShuttle.App.I18n.Languages.All[0].Code;

    private void WithWindow(Action<MainWindow> body, string? cfgLanguage = null) => host.Invoke(() =>
    {
        global::WindowShuttle.App.App.Cfg = new Settings { Language = cfgLanguage };
        var w = new MainWindow { Width = 960, Height = 760, Opacity = 0, ShowInTaskbar = false };
        w.Show();
        w.UpdateLayout();
        // Loaded 的派发优先级高于这里的 body（Normal），Show 之后它已排在前面落地，_loaded 为真。
        try { body(w); }
        finally { w.Close(); }
    });

    [Fact]
    public void Cancelling_the_restart_prompt_leaves_config_untouched_and_reverts_the_dropdown()
    {
        WithWindow(w =>
        {
            Assert.Equal(19, w.LangBox.Items.Count);       // 跟随系统 + 18 门语言：确认这一格真能选
            bool restarted = false;
            w.RestartApp = () => restarted = true;
            w.ConfirmBox = (_, _) => false;                // 用户点「取消」

            w.LangBox.SelectedIndex = FirstRealChoice;

            Assert.Null(global::WindowShuttle.App.App.Cfg.Language);
            Assert.Equal(0, w.LangBox.SelectedIndex);       // 回退到「跟随系统」
            Assert.False(restarted);
        });
    }

    [Fact]
    public void Cancelling_and_re_choosing_reverts_to_the_last_committed_choice()
    {
        // 「上一项」不是常数：反悔要回的是**打开时那一项**（_lastLangIndex），不是硬编码的 0。
        WithWindow(w =>
        {
            w.ConfirmBox = (_, _) => false;
            w.LangBox.SelectedIndex = 2;                    // 反悔一次：应回到 1（当前生效的 zh-CN）
            Assert.Equal(1, w.LangBox.SelectedIndex);
            Assert.Equal(FirstRealCode, global::WindowShuttle.App.App.Cfg.Language);
        }, cfgLanguage: FirstRealCode);
    }

    [Fact]
    public void Confirming_the_restart_prompt_saves_the_choice_and_restarts_once()
    {
        WithWindow(w =>
        {
            int restarts = 0;
            w.RestartApp = () => restarts++;
            w.ConfirmBox = (_, _) => true;

            w.LangBox.SelectedIndex = FirstRealChoice;

            Assert.Equal(FirstRealCode, global::WindowShuttle.App.App.Cfg.Language);
            Assert.Equal(FirstRealChoice, w.LangBox.SelectedIndex);   // 确认后不回退
            Assert.Equal(1, restarts);
            // 真的落盘了——旧实现也落盘，但落在「问之前」；这里钉的是「确认之后盘里有」。
            Assert.Contains(FirstRealCode, File.ReadAllText(WindowShuttle.App.SettingsStore.DefaultPath));
        });
    }

    [Fact]
    public void Re_choosing_the_already_active_language_asks_nothing()
    {
        WithWindow(w =>
        {
            int asked = 0;
            w.ConfirmBox = (_, _) => { asked++; return false; };

            w.LangBox.SelectedIndex = FirstRealChoice;      // 选的正是当前生效的那门（cfg 预置为它）

            Assert.Equal(0, asked);
            Assert.Equal(FirstRealCode, global::WindowShuttle.App.App.Cfg.Language);
            Assert.Equal(FirstRealChoice, w.LangBox.SelectedIndex);   // 不弹不问，也不回退
        }, cfgLanguage: FirstRealCode);
    }
}
