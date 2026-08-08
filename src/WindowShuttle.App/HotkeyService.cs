using System.Runtime.InteropServices;
using System.Windows.Interop;
using WindowShuttle.Core;

namespace WindowShuttle.App;

/// <summary>一个动作的热键状态。Unbound 与 Conflict 必须分开——UI 对两者的呈现完全不同。</summary>
public enum HotkeyState { Registered, Unbound, Conflict }

public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    internal static readonly string[] Actions =
        ["Swap", "SwapTop", "ToPrimary", "ToNext", "ToDirection",
         "Gather", "Undo"];

    private readonly nint _hwnd;
    private readonly List<int> _registered = [];
    public event Action<WindowShuttleAction>? Pressed;

    public HotkeyService(nint sinkHwnd, HwndSource sink)
    {
        _hwnd = sinkHwnd;
        sink.AddHook(Hook);
    }

    /// <summary>
    /// 重注册全部热键。返回 动作名→三态，每个动作都必有一项（UI 靠它逐行渲染，缺项会让某行悄悄漏掉）。
    /// 三态而非 bool：默认只绑两个动作（见 SettingsStore.DefaultHotkeys），其余留空。
    /// 若把「用户故意没绑」和「被别的程序占了」都表示成 false，设置页会把一整排未绑定标成红色冲突。
    /// </summary>
    public Dictionary<string, HotkeyState> Apply(Settings s)
    {
        foreach (var id in _registered) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        var states = new Dictionary<string, HotkeyState>();
        for (int i = 0; i < Actions.Length; i++)
        {
            // 绑不了快捷键的动作（只有「按方向送屏」，理由见 SettingsStore.AllowsHotkey）：一律当未绑定，
            // **绝不注册**。界面上那一格已经画成不可录，但手改 settings.json 那条路只有这里挡得住——
            // 真放它注册成功，按下去 SwapPlanner.Plan 就抛，用户得到一张红色错误卡。
            if (!SettingsStore.AllowsHotkey(Actions[i])) { states[Actions[i]] = HotkeyState.Unbound; continue; }
            var raw = s.Hotkeys.GetValueOrDefault(Actions[i], "");
            if (string.IsNullOrWhiteSpace(raw)) { states[Actions[i]] = HotkeyState.Unbound; continue; }
            var g = HotkeyGesture.TryParse(raw);
            // 非空但解析不了 = 配置文件被手改坏了，按冲突处理让它在 UI 上显形，而不是静默当没绑
            if (g is null) { states[Actions[i]] = HotkeyState.Conflict; continue; }
            bool r = RegisterHotKey(_hwnd, i + 1, g.Modifiers, g.VirtualKey);
            if (r) _registered.Add(i + 1);
            states[Actions[i]] = r ? HotkeyState.Registered : HotkeyState.Conflict;
        }
        return states;
    }

    private nint Hook(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == WM_HOTKEY && wp >= 1 && wp <= Actions.Length)
        {
            handled = true;
            Pressed?.Invoke(Enum.Parse<WindowShuttleAction>(Actions[(int)wp - 1]));
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _registered) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }
}
