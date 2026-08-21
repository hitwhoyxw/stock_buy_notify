using System.Text;
using Avalonia;
using Avalonia.Controls;

namespace ThreeBucket.UI;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 注册 GBK 提供程序（新浪/腾讯行情为 GBK 编码）。直接引用类型注册——
        // 反射短程序集名解析在 .NET 10 返回 null 且静默跳过，GBK 不会生效（Core 数据源基类同样兜底注册）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 全局异常兜底：WinExe 无控制台，未捕获异常会静默退出进程（"跑一会自己挂掉无弹窗"的元凶）。
        // 统一落盘到 data/logs/crash-*.log 便于事后定位。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog("UnhandledException", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved(); // 标记已观察，阻止进程被终结
        };

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>崩溃日志落盘：与 AppState.DetectProjectRoot 同口径向上找项目根，写 data/logs/。</summary>
    private static void WriteCrashLog(string kind, object? exception)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 10; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))) break;
                var parent = Path.GetDirectoryName(dir);
                if (parent is null || parent == dir) break;
                dir = parent;
            }
            var logDir = Path.Combine(dir, "data", "logs");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var text = $"""
                kind: {kind}
                time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                runtime: {Environment.Version} / {Environment.OSVersion}
                {exception}
                """;
            File.WriteAllText(path, text, new UTF8Encoding(false));
            System.Diagnostics.Trace.WriteLine($"[crash] 已写入 {path}");
        }
        catch
        {
            // 崩溃日志自身失败时不能再抛，否则钩子里无限递归
        }
    }
}
