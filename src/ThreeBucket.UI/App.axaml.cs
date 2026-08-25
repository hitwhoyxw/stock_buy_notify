using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ThreeBucket.UI.Services;
using ThreeBucket.UI.Views;

namespace ThreeBucket.UI;

public class App : Application
{
    /// <summary>可写的日志目录：桌面为 exe 旁；移动端 BaseDirectory 是只读 apk 路径，改用应用沙盒。</summary>
    private static string LogDir =>
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : AppContext.BaseDirectory;

    private static void LogError(string source, Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(LogDir, "ui_error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n");
        }
        catch { /* 日志写失败不再抛 */ }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // UI 线程未处理异常兜底：控件内部异常（如 SelectionModel 越界）无法逐处 try-catch，
        // 默认行为是直接终止进程（切 tab 崩溃的来源）。这里拦下并记日志，保证界面不闪退、异常可追溯。
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogError("UIThread", e.Exception);
            e.Handled = true;
        };
        // 后台线程兜底：Task 内未观察异常默认进程终止，同样落盘便于移动端（adb pull）排查
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError("Task", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogError("AppDomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 主窗口构造链（AppState/各 View 初始化）若在移动端抛异常，
            // 显式落日志后再吞掉——否则 Avalonia 直接白屏且无任何痕迹可查
            try
            {
                desktop.MainWindow = new MainWindow();
            }
            catch (Exception ex)
            {
                LogError("MainWindow", ex);
            }
        }
        // 移动端（Android/iOS）是 SingleViewLifetime：必须设置 MainView，
        // AvaloniaActivity 才会触发 SetContentView——漏掉此分支表现为纯白屏、
        // 无 crash、无异常日志（Android 模拟器实测复现）
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            try
            {
                single.MainView = new MainView(new AppState());
            }
            catch (Exception ex)
            {
                LogError("MainView", ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
