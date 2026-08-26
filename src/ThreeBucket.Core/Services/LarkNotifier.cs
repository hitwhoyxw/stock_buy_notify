using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 飞书自定义机器人 webhook 推送（策略监控提醒外发通道）。
///
/// 支持三种安全设置：
/// - 自定义关键词：消息标题自带「三桶监控」前缀，关键词填"三桶"即可；
/// - 签名校验：配置 LarkSecret（timestamp+sign 附加到请求体）；
/// - IP 白名单：无需客户端配合。
/// 成功响应：{"code":0}（旧版）或 {"StatusCode":0}（新版），其余视为失败。
/// </summary>
public static class LarkNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>webhook URL 形态校验（配置页/发送前快速判断，不发网络请求）。</summary>
    public static bool IsValidWebhook(string? url)
        => Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
           && u.Host.Length > 0;

    /// <summary>推送 text 消息；返回 (是否成功, 结果说明)。secret 非空时启用签名校验。</summary>
    public static async Task<(bool Ok, string Message)> SendAsync(
        string webhook, string text, string secret = "")
    {
        if (!IsValidWebhook(webhook)) return (false, "飞书 Webhook URL 无效（应为 http(s):// 开头）");
        try
        {
            object body = new { msg_type = "text", content = new { text } };
            if (!string.IsNullOrWhiteSpace(secret))
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                body = new
                {
                    msg_type = "text",
                    content = new { text },
                    timestamp = ts.ToString(),
                    sign = BuildSign(secret.Trim(), ts),
                };
            }
            using var resp = await Http.PostAsync(webhook.Trim(),
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {Truncate(respBody)}");
            return respBody.Contains("\"code\":0") || respBody.Contains("\"StatusCode\":0")
                ? (true, "发送成功")
                : (false, Truncate(respBody)); // 例 {"code":19021,"msg":"sign match fail or timestamp error"}
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 飞书签名（安全设置选"签名校验"时）：
    /// key = "{timestamp}\n{secret}" 的 UTF-8 字节，对空串做 HMAC-SHA256 后 Base64。
    /// </summary>
    private static string BuildSign(string secret, long timestamp)
    {
        var stringToSign = $"{timestamp}\n{secret}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(hmac.ComputeHash(Array.Empty<byte>()));
    }

    /// <summary>
    /// 策略提醒列表 → 飞书消息文本（多条合并为一条，P0 排最前）。
    /// 同一批最多展开 20 条（去重机制保证单批通常远小于此）。
    /// </summary>
    public static string BuildAlertMessage(IReadOnlyList<AlertEntry> alerts)
    {
        var ordered = alerts
            .OrderBy(a => a.Priority switch { "P0" => 0, "P1" => 1, "P2" => 2, _ => 3 })
            .ThenBy(a => a.Code)
            .Take(20)
            .ToList();
        var sb = new StringBuilder();
        sb.Append($"【三桶监控】{DateTime.Now:MM-dd HH:mm} 触发 {alerts.Count} 条策略提醒\n");
        foreach (var a in ordered)
        {
            var pri = a.Priority.Length > 0 ? $"[{a.Priority}] " : "";
            var name = a.Name.Length > 0 ? $" {a.Name}" : "";
            sb.Append($"\n{pri}{a.Code}{name} · {a.StrategyName}");
            if (a.Action.Length > 0) sb.Append($"\n{a.Action}");
        }
        if (alerts.Count > ordered.Count) sb.Append($"\n… 另有 {alerts.Count - ordered.Count} 条略");
        return sb.ToString();
    }

    private static string Truncate(string s, int n = 200)
        => s.Length <= n ? s : s[..n] + "…";
}
