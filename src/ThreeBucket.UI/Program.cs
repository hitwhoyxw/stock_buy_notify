using System.Text;
using Avalonia;
using Avalonia.Controls;

namespace ThreeBucket.UI;

internal sealed class Program
{
    // 注册 GBK 提供程序（新浪/腾讯行情为 GBK 编码）
    static Program()
    {
        var providerType = Type.GetType(
            "System.Text.Encoding.CodePages.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
        if (providerType is not null)
        {
            var instance = providerType.GetProperty("Instance")?.GetValue(null);
            if (instance is EncodingProvider provider)
                Encoding.RegisterProvider(provider);
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
