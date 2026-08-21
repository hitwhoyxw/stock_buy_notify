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

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
