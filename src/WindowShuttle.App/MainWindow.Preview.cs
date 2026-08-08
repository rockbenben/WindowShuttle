using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowShuttle.Core;

namespace WindowShuttle.App;

public partial class MainWindow
{
    // 预览态自己的两个字段。跟地图那边分开放：地图的状态是"现在有哪些屏、画在哪"，这里的状态是
    // "鼠标此刻停在哪条动作上"——同一块画布，两件事。
    private UIElement? _previewGlyph;
    private string? _previewAction;

    // ══ 悬停预览：地图不只是图例，它是这个动作的现场示意图 ══════════════════════════════════
    //
    // 鼠标停在某条动作上，地图就只留下这条动作真正会碰到的那几块屏，中间标一个方向记号；其余的
    // 屏调暗。整扇窗里只有这一处会动。
    //
    // 两个信号，不是三个：调暗（哪几块屏参与）＋ 方向记号（往哪边搬）。原本还想给参与的卡片加一圈
    // 高亮描边，删掉了——没被调暗本身就已经是高亮，第三个信号只是把同一句话说三遍。
    //
    // 这跟之前被砍掉的那条示例曲线不是一回事：那条是静态装饰，画的是一对假想的屏，谁也不指；这里
    // 画的是此刻真实的屏号、真实的主屏、真实的光标位置，只在悬停时出现，鼠标一走就没。

    private static readonly Dictionary<string, string> PreviewGlyphs = new()
    {
        ["Swap"] = "⇄",          // ⇄ 两边对调
        ["SwapTop"] = "⇄",
        ["ToPrimary"] = "→",     // → 单向送过去（方向在 ShowActionPreview 里按左右关系翻转）
        ["ToNext"] = "→",        // 同为单向送，终点是循环意义上的下一块屏
        ["Gather"] = "⇲",        // ⇲ 全部收拢到一处
    };

    /// <summary>这条动作会碰到哪几块屏。返回 null＝不涉及特定屏（撤销、修正跨屏），不作预览——
    /// 与其画一个"全都算"的假示意图，不如什么都不画。</summary>
    private (int From, int To)? PreviewPair(string actionKey)
    {
        if (_monitors.Count < 2 || !PreviewGlyphs.ContainsKey(actionKey)) return null;
        var primary = _monitors.FirstOrDefault(m => m.IsPrimary);
        if (primary is null || _cursorMonitorIndex < 0) return null;
        if (actionKey == "Gather") return (-1, primary.Index);           // -1＝除主屏外的所有屏
        if (actionKey == "ToNext")
        {
            // 直接问 planner，不再自己抄一份循环规则——抄的那份按 Index 排，而 planner 按物理位置排，
            // 于是预览箭头指一块屏、按下去去了另一块。同一个问题只能有一个出处。
            // FirstOrDefault：拔屏和悬停之间隔着一次 Refresh，_cursorMonitorIndex 可能还指着已经没了的
            // 那块屏。这里是 hover 处理器，抛出去就是整个应用崩掉——不画预览就行。
            var cur = _monitors.FirstOrDefault(m => m.Index == _cursorMonitorIndex);
            if (cur is null) return null;
            var next = SwapPlanner.NextMonitor(cur, _monitors);
            return next.Index == _cursorMonitorIndex ? null : (_cursorMonitorIndex, next.Index);
        }
        return _cursorMonitorIndex == primary.Index ? null : (_cursorMonitorIndex, primary.Index);
    }

    private void ShowActionPreview(string? actionKey)
    {
        _previewAction = actionKey;
        if (_previewGlyph is not null) { LayoutCanvas.Children.Remove(_previewGlyph); _previewGlyph = null; }
        if (_cards.Count == 0) return;

        var pair = actionKey is null ? null : PreviewPair(actionKey);
        if (pair is null)
        {
            foreach (var card in _cards.Values) card.Opacity = 1;
            return;
        }

        var (from, to) = pair.Value;
        foreach (var (idx, card) in _cards)
            card.Opacity = from == -1 || idx == from || idx == to ? 1 : 0.35;

        // 记号落在这条动作的两个端点之间；Gather 的"起点"是全体，就落在主屏卡片正上方。
        if (!_cards.TryGetValue(to, out var toCard)) return;
        double toX = Canvas.GetLeft(toCard) + toCard.Width / 2, toY = Canvas.GetTop(toCard) + toCard.Height / 2;
        double x, y;
        string glyph = PreviewGlyphs[actionKey!];
        if (from == -1) { x = toX; y = toY; }
        else
        {
            if (!_cards.TryGetValue(from, out var fromCard)) return;   // 同上：这是 hover 处理器，抛出去就是崩
            double fromX = Canvas.GetLeft(fromCard) + fromCard.Width / 2;
            x = (fromX + toX) / 2;
            y = (Canvas.GetTop(fromCard) + fromCard.Height / 2 + toY) / 2;
            if (glyph == "→" && toX < fromX) glyph = "←";      // 主屏在左边就得反过来指
        }

        var text = new TextBlock
        {
            Text = glyph, FontSize = 26, Foreground = AppTheme.Live, FontWeight = FontWeights.Bold,
        };
        var chip = new Border
        {
            Background = AppTheme.Room, BorderBrush = AppTheme.Live, BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(99), Padding = new Thickness(9, 1, 9, 3), Child = text,
            IsHitTestVisible = false,                                    // 别挡住底下卡片的拖放目标
        };
        chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(chip, x - chip.DesiredSize.Width / 2);
        Canvas.SetTop(chip, y - chip.DesiredSize.Height / 2);
        Panel.SetZIndex(chip, 10);
        LayoutCanvas.Children.Add(chip);
        _previewGlyph = chip;
    }
}
