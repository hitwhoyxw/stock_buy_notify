using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace ThreeBucket.UI;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // UI 线程未处理异常兜底：控件内部异常（如 SelectionModel 越界）无法逐处 try-catch，
        // 默认行为是直接终止进程（切 tab 崩溃的来源）。这里拦下并记日志到 exe 旁 ui_error.log，
        // 保证界面不闪退、异常可追溯。
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "ui_error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
            }
            catch { /* 日志写失败不再抛 */ }
            e.Handled = true;
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
