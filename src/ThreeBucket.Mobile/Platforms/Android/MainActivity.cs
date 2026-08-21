using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace ThreeBucket.UI;

/// <summary>Android 入口：复用桌面 App/MainWindow（Avalonia 移动端单窗口同样走 desktop lifetime）。</summary>
[Activity(
    Label = "三桶策略系统",
    Theme = "@style/MyTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
}
