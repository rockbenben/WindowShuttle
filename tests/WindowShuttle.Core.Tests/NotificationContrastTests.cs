using System.Windows.Media;
using WindowShuttle.Core;
using AppTheme = WindowShuttle.App.AppTheme;
using NotificationOverlay = WindowShuttle.App.NotificationOverlay;

namespace WindowShuttle.Core.Tests;

/// <summary>通知卡是这个应用里唯一一处半透明的表面：它浮在别人的窗口上，底下是什么颜色不由我们决定。
/// 其余每一处的对比度都可以拿两个已知色去算（见 <see cref="AppThemeContrastTests"/>），这里不行——
/// 必须先按卡片的不透明度把它和背后的颜色合成，再验合成结果上的文字。
///
/// 取纯白和纯黑两个极端就够：任何真实背景的亮度都落在这两者之间，而合成结果的亮度对背景亮度是单调的，
/// 所以两端都过，中间就都过。
///
/// 不透明度是这条测试真正锁住的东西。全不透明的卡片浮在别人的内容上像一块补丁，太透又读不清字——
/// 中间这档只能靠算。写死一个数而不验，等于把"还读得清吗"这个问题留给用户的桌面背景去回答。</summary>
[Collection(MainWindowWpfCollection.Name)]
public class NotificationContrastTests(WpfTestHost host)
{
    private static readonly (byte R, byte G, byte B) White = (255, 255, 255), Black = (0, 0, 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Card_text_stays_readable_over_any_backdrop(bool dark) => host.Invoke(
        () => ThemeProbe.Under(dark, () =>
        {
            var panel = AppTheme.Panel.Color;
            foreach (var (backdrop, bn) in new[] { (White, "纯白背景"), (Black, "纯黑背景") })
            {
                var card = ColorContrast.Composite(
                    NotificationOverlay.CardAlpha, panel.R, panel.G, panel.B, backdrop);

                double body = ColorContrast.Ratio(ThemeProbe.Rgb(AppTheme.Ink), card);
                Assert.True(body >= 4.5,
                    $"{ThemeProbe.Name(dark)}/{bn}：正文 {body:0.00}:1，低于 4.5:1（卡片合成后 {card}）");

                // 三档严重程度的描边是"这条是什么性质"的唯一颜色信号，欠 3:1 的非文本底线。
                foreach (var (accent, an) in new[]
                         {
                             (AppTheme.NotifyError, "错误"), (AppTheme.NotifyWarn, "可操作"),
                             (AppTheme.NotifyNeutral, "提示"),
                         })
                {
                    double edge = ColorContrast.Ratio(ThemeProbe.Rgb(accent), card);
                    Assert.True(edge >= 3.0,
                        $"{ThemeProbe.Name(dark)}/{bn}：{an}描边 {edge:0.00}:1，低于 3:1");
                }
            }
        }));
}
