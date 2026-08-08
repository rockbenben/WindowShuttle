using System.Runtime.CompilerServices;

// 测试项目要在 STA 线程上真的建一扇 MainWindow 去量布局（§window-bounds-fit 的六档分辨率验证），
// 建窗口前必须先有一份非 null 的 App.Cfg——生产路径里这份数据来自 App.OnStartup，但那个方法
// 还会开互斥体、注册全局热键、建托盘图标，不是测试想背的重量。开一条内部窗口让测试直接赋值。
[assembly: InternalsVisibleTo("WindowShuttle.Core.Tests")]
// round4 的 UIA 测量harness（tools/UiaHarness）同样要在建窗口前摆一份 App.Cfg，且要落到
// NotificationOverlay 一个只给测试/harness用的、按指定屏（不是"鼠标现在在哪"）落位的入口——
// 同一个理由，同一个口子。
[assembly: InternalsVisibleTo("WindowShuttle.UiaHarness")]
