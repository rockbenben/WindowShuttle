using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WindowShuttle.App.I18n;
using WindowShuttle.Core;

namespace WindowShuttle.App;

public partial class MainWindow
{
    private readonly Dictionary<string, Border> _mouseCaps = [];

    // ---------- 鼠标 chord 录制：热键区旁边的第二个键位区，录法/三态视觉语言完全照抄键盘那一套，
    // 只是输入源换成鼠标按钮。
    //
    // 录制**经由全局钩子**，不是这块元素上的一次普通 WPF 鼠标事件。理由是改绑：录制期间钩子先把
    // 按下截走、不再走 Resolve，所以"把一个已经绑给别的动作的组合改绑过来"不会被原主人抢先吞掉并
    // 执行。这里原来还写着"这样就能录 Win 组合（钩子读得到 0x8）"——**那句是错的**，Win 在两侧都
    // 读不到（shell 在点击落地前就把它收走了），能读到 0x8 的只有脚本合成出来的假 Win。
    // 详见 MouseChordService.BeginCapture 里那段"合成输入会说谎"。
    // ----------
    private Border BuildMouseCap(string actionKey)
    {
        var cap = NewCapShell(actionKey, new Thickness(7, 0, 0, 0));
        cap.PreviewMouseDown += OnMouseChordRecord;
        cap.PreviewKeyDown += OnMouseChordKeyDown;
        // 指针一进这块键位区就进入捕获态，离开就退出——**不是**等它被点中才开始。
        //
        // 这一条是被 Win 键逼出来的，也是这个缺陷的最后一块：Win 按住时 Windows 根本不把点击派发给
        // 应用（钩子看得见 mods=0x8，WPF 那侧一行事件都收不到），所以 Win+点击连"把这一格点亮成录制态"
        // 都做不到——用户看到的就是怎么点都没反应。按悬停进入捕获，手势本身全部由钩子接管，
        // 不再依赖那一次点击能不能送到界面。
        cap.MouseEnter += (_, _) =>
        {
            _hoverSlot = actionKey;
            SyncChordCapture();
            if (!cap.IsKeyboardFocusWithin) RenderMouseCap(cap, actionKey);
        };
        cap.MouseLeave += (_, _) =>
        {
            if (_hoverSlot == actionKey) _hoverSlot = null;
            SyncChordCapture();
            if (!cap.IsKeyboardFocusWithin) RenderMouseCap(cap, actionKey);
        };
        cap.GotFocus += (_, _) =>
        {
            _focusSlot = actionKey;
            EnterRecordingChrome(cap, "MouseChord_Recording");
            SyncChordCapture();
        };
        cap.LostFocus += (_, _) =>
        {
            if (_focusSlot == actionKey) _focusSlot = null;
            SyncChordCapture();
            ExitRecordingChrome(cap);
            RenderMouseCap(cap, actionKey);
        };
        _mouseCaps[actionKey] = cap;
        RenderMouseCap(cap, actionKey);
        return cap;
    }

    // 未绑定不是错误状态——全部默认都是空的（§own-decision），跟热键的未绑定用同一个虚线占位，
    // 不单独发明一套视觉；鼠标 chord 没有"冲突"这个状态可言（钩子没有注册期冲突信号），
    // 所以只有绑定/未绑定两态，不是三态。
    //
    // 空位这一侧只画鼠标记号，不重复"未绑定 · 点击录制"那行字：手势默认全空、而且应用自己在设置条
    // 里劝你别用，让一列文字把这句话在七条动作上各说一遍，等于把一个默认关闭、且被劝阻的功能摆成
    // 跟快捷键同等的分量。虚线圈保留——它是这个应用里"这里是空的、点它能录"的固定词汇，换掉就等于
    // 要用户再学一遍。已绑定时记号留在键帽前面：两侧的键帽长得一样，不留个记号就分不出这串组合
    // 是键盘的还是鼠标的。
    private void RenderMouseCap(Border cap, string actionKey)
    {
        string raw = App.Cfg.MouseChords.GetValueOrDefault(actionKey, "");
        // ToolTip 归 Render 管，不是构造时设一次就完事——EnterRecordingChrome 会把它清成 null
        // （录制态下"点击录制"这句提示是错的），而回到常态只有这里能把它装回去。设一次的写法在
        // 键盘那侧不出问题（RenderCap 每次都重设 ToolTip），在这侧就是：用户点过一次键位区、再
        // 退出录制，这一格从此到进程结束都没有任何悬浮提示——偏偏是他已经动过手的那几格。
        cap.ToolTip = Strings.Get("MouseChord_Tip");
        bool hover = cap.IsMouseOver;
        if (string.IsNullOrEmpty(raw))
        {
            var glyph = MakeMouseGlyph(hover ? AppTheme.Dim : AppTheme.Faint);
            glyph.Margin = new Thickness(11, 4, 11, 4);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            cap.Child = MakeUnboundPill(hover, glyph);
            return;
        }
        var bound = MakeMouseGlyph(AppTheme.Live);
        bound.Margin = new Thickness(0, 0, 5, 0);
        bound.VerticalAlignment = VerticalAlignment.Center;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { bound } };
        foreach (var token in raw.Split('+'))
            panel.Children.Add(MakeKeycap(token, conflict: false, hover));
        cap.Child = panel;
    }

    // ══ 录制态：一个派生状态，两个输入 ═════════════════════════════════════════════════════
    //
    // 录制态一旦挂住，代价是全系统的：钩子会吞掉每一次右/中/侧键按下，右键菜单在所有程序里都不再
    // 弹出，而任何一次带修饰键的点击都会被当成录制、静默改掉某个动作的绑定。所以这里的规矩是
    // **宁可少录一次，也绝不能挂住**。
    //
    // 输入只有两个：指针停在哪一格（_hoverSlot）、焦点在哪一格（_focusSlot）。真正录哪一格是**算**
    // 出来的，焦点优先。原来两者各自直接写 _recordingSlot，于是可以出现"A 亮着录制光圈、实际录进
    // B"（悬停 B 覆盖了 _recordingSlot），以及反过来"A 亮着、但离开 B 时把 _recordingSlot 清成 null，
    // 从此什么都录不进去"。一个状态只能有一个出处。
    //
    // 还有一道闸：窗口不在前台或已经藏进托盘时一律不录。原来的解除条件是"鼠标离开且没有焦点"，
    // 而一个聚焦着的键位区恰恰会压住 MouseLeave——点一下手势位再 Alt+Tab 切走，捕获态就永远留着了。
    private string? _hoverSlot, _focusSlot;
    private string? _recordingSlot;      // 当前真正在录的那一格；null = 没在录

    /// <summary>把两个输入折算成"到底录不录、录哪一格"，只在结论变化时才去动钩子。</summary>
    private void SyncChordCapture()
    {
        // IsActive 而不是 IsVisible：藏进托盘固然要停，切到别的程序去也必须停——那时候用户的
        // 右键是给别人用的，不是给我们录的。
        //
        // IsMouseOver 是第三道，它兑现的正是钩子那侧写下的安全前提：BeginCapture 的注释说"捕获是靠
        // 指针悬停才武装起来的，指针既然在我们这一格上，底下就没有别人的窗口可抢"——而那句话只对
        // _hoverSlot 成立。_focusSlot 是点一下键位区就留下的，它会让捕获在**指针早已移到别处**时
        // 继续武装着：窗口还是 IsActive（点了它就是前台），用户到浏览器里右键一下，钩子当场把它吞掉、
        // 顺手把那一格改绑成 Right——菜单没弹出来，绑定被悄悄改了，而那次按下既然没送达浏览器，
        // 浏览器也就没被激活，于是下一次右键继续被吞。中键（粘贴/新标签页/自动滚动）同理。
        // 指针在我们窗口范围内就够了（不必非在那一格上）：那种情况下这次点击本来就是我们的，
        // 跨窗口误吞在类型上不可能发生，而"点一下键位区、指针滑开一点再按组合"这个自然节奏还留着。
        string? want = IsActive && IsVisible && IsMouseOver ? _focusSlot ?? _hoverSlot : null;
        if (want == _recordingSlot) return;
        _recordingSlot = want;
        if (Application.Current is not App app) return;      // 测试里直接 new 出来的窗口没有 App
        if (want is null)
        {
            ChordDebug.Log("disarm");
            app.EndChordCapture();
        }
        else
        {
            var h = new WindowInteropHelper(this).Handle;
            ChordDebug.Log($"arm    slot={want} hwnd={h}");
            app.BeginChordCapture(h);
        }
    }

    /// <summary>录完一次就把录制态整个收掉：把焦点从键位区拿走，两个输入一起清空。
    /// 不收的话，紧接着的下一次右键（可能是在别的程序里点的）还会被吞掉并再录一次。</summary>
    private void EndChordRecording()
    {
        _hoverSlot = null;
        _focusSlot = null;
        if (Keyboard.FocusedElement is DependencyObject d)
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(d), null);
        Keyboard.ClearFocus();
        SyncChordCapture();
    }

    /// <summary>钩子在输入那一瞬捕到的一次手势。这里只负责登记。</summary>
    internal void OnChordCaptured(MouseChordButton button, uint mods)
    {
        if (_recordingSlot is not { } actionKey) return;
        ChordDebug.Log($"captured btn={button} mods=0x{mods:X} slot={actionKey}");
        // 没有修饰键就不收——**除了划动类动作**，那一格裸按钮是合法的（点击会被原样补发，
        // 不吃右键菜单，见 SettingsStore.AllowsBareButton）。不弹提示卡：录制态那行提示已经点名了
        // Ctrl / Alt / Shift，该说的话在用户动手**之前**就说了，事后再弹一张浮层是把同一句话说第二遍。
        // Win 键读不到（系统在点击落地前就把它收走了，见 MouseChordService.BeginCapture），
        // 落到这里的正是那种情况——提示里没有 Win，本身就是答案。
        if (mods == 0 && !SettingsStore.AllowsBareButton(actionKey)) return;

        // 同一个组合只能归一个动作——录进来的这个要从原主人手里收走，规则和理由都在
        // SettingsStore.AssignMouseChord。RefreshAllMouseCaps 紧接着会把被收走的那格重画成未绑定。
        SettingsStore.AssignMouseChord(App.Cfg.MouseChords, actionKey,
            new MouseChordGesture(mods, button).ToString());
        SettingsStore.Save(SettingsStore.DefaultPath, App.Cfg);
        ((App)Application.Current).ReapplyMouseChords();
        RefreshAllMouseCaps();
        EndChordRecording();          // 录到了就收摊，别让下一次右键继续被吞
    }

    // 左键那一下只用来聚焦、进入录制态。右/中/侧键在录制态下压根到不了这里——钩子会先把它们
    // 截走（见 MouseChordService.BeginCapture），这正是 Win 组合能被录进来的原因。
    private void OnMouseChordRecord(object sender, MouseButtonEventArgs e)
    {
        var cap = (Border)sender;
        ChordDebug.Log($"wpf    btn={e.ChangedButton} mods=0x{MouseChordService.HeldModifiers():X} " +
                       $"focused={cap.IsKeyboardFocusWithin} slot={cap.Tag}");
        if (!cap.IsKeyboardFocusWithin) cap.Focus();
        e.Handled = true;                                  // 不透出去弹右键菜单/别的默认行为
    }

    // Esc 取消录制、Delete/Backspace 清空绑定——跟热键那一套的对称版本，键盘输入本身不构成手势，
    // 只用来取消/清除；真正的手势只能来自 OnMouseChordRecord。
    private void OnMouseChordKeyDown(object sender, KeyEventArgs e)
    {
        var cap = (Border)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            e.Handled = true;
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(cap), null);
            Keyboard.ClearFocus();
            return;
        }
        if (key is Key.Delete or Key.Back)
        {
            e.Handled = true;
            var actionKey = (string)cap.Tag!;
            App.Cfg.MouseChords[actionKey] = "";
            SettingsStore.Save(SettingsStore.DefaultPath, App.Cfg);
            ((App)Application.Current).ReapplyMouseChords();
            RefreshAllMouseCaps();
        }
    }

    private void RefreshAllMouseCaps()
    {
        foreach (var (key, cap) in _mouseCaps) RenderMouseCap(cap, key);
    }
}
