using System.Runtime.CompilerServices;

// 测试项目要断言 WindowCommitter.BuildPlacement 这类内部纯函数的输出（不装 Win32 也能测的部分），
// 不为此新开一个公共 API 表面。
[assembly: InternalsVisibleTo("WindowShuttle.Core.Tests")]
// round4 的 UIA 测量 harness（tools/UiaHarness）复用同一个 Win32.GetWindowRect，不重新抄一份 P/Invoke。
[assembly: InternalsVisibleTo("WindowShuttle.UiaHarness")]
