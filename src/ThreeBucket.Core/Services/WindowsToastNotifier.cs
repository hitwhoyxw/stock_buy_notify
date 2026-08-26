using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace ThreeBucket.Core.Services;

/// <summary>
/// Windows 系统通知（toast）：通知中心弹窗 + 右下角横幅。
///
/// 实现路径：HKCU 注册 AUMID（unpackaged 应用归属，否则通知中心显示异常）→
/// PowerShell 5.1（Windows 内置）激活 WinRT ToastNotificationManager 弹 toast，
/// -EncodedCommand 传脚本规避引号转义（BurntToast 同款机制）。
/// 纯 BCL 实现、无平台包依赖（不加 SDK 投影包，保持 net10.0 跨平台发布）；
/// 非 Windows / Win10 以下静默跳过——外发提醒由飞书 webhook 通道兜底。
/// </summary>
public static class WindowsToastNotifier
{
    public const string Aumid = "ThreeBucket.UI.Monitor";
    private static bool _aumidReady;

    /// <summary>弹一条系统通知（fire-and-forget：PowerShell 子进程弹完自退，不阻塞调用方）。</summary>
    public static void Show(string title, string message)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        try
        {
            EnsureAumidRegistered();
            var xml = "<toast scenario=\"default\"><visual><binding template=\"ToastGeneric\">"
                      + $"<text>{Escape(title)}</text>"
                      + $"<text>{Escape(Truncate(message, 180))}</text>"
                      + "</binding></visual></toast>";
            // 单引号 here-string（@'…'@）不展开变量，xml 内单双引号均安全
            var script =
                "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null\n" +
                "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null\n" +
                "$doc = New-Object Windows.Data.Xml.Dom.XmlDocument\n" +
                "$doc.LoadXml(@'\n" + xml + "\n'@)\n" +
                "$toast = New-Object Windows.UI.Notifications.ToastNotification($doc)\n" +
                $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{Aumid}').Show($toast)\n";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -NonInteractive -EncodedCommand " + encoded,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch
        {
            // 通知失败（策略禁用/PowerShell 不可用）不影响监控主流程
        }
    }

    /// <summary>unpackaged 应用须在 HKCU 注册 AUMID，通知中心才能归属应用并显示名称（幂等，仅首次写）。</summary>
    private static void EnsureAumidRegistered()
    {
        if (_aumidReady) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid);
        key.SetValue("DisplayName", "三桶监控", RegistryValueKind.String);
        _aumidReady = true;
    }

    private static string Escape(string s)
        => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n] + "…";
}
