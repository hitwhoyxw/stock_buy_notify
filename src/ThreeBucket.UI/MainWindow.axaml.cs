using Avalonia.Controls;
using ThreeBucket.UI.Services;
using ThreeBucket.UI.Views;

namespace ThreeBucket.UI;

public partial class MainWindow : Window
{
    public AppState App { get; }

    public MainWindow()
    {
        InitializeComponent();
        App = new AppState();
        Content = new MainView(App);
        // 临时调试入口：--phone 用手机尺寸窗口预览窄屏布局（与 --page=N 配合）
        if (System.Environment.GetCommandLineArgs().Contains("--phone"))
        {
            MinWidth = 0; MinHeight = 0;
            Width = 411; Height = 850;
        }
    }
}
