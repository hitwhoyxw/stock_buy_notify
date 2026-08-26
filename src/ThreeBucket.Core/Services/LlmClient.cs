using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ThreeBucket.Core.Services;

/// <summary>
/// OpenAI 兼容 chat completions 客户端：POST 到用户填的完整 endpoint（含 /chat/completions）。
/// 兼容 OpenAI / DeepSeek / Qoder / 扶摇 / Ollama 等所有 OpenAI 兼容端点。
/// 仅做非流式补全，一次性拿回完整文本。失败返回 (false, 错误说明)，不抛异常。
/// 配置来源：AppConfig.LlmApiUrl（完整 endpoint）/ LlmApiKey（Bearer）/ LlmModel（模型名）。
/// </summary>
public class LlmClient
{
    // 候选池分析可能较长（每桶≤100只、三档全量），给足超时
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static LlmClient() => Http.DefaultRequestHeaders.UserAgent.TryParseAdd("ThreeBucket/1.0");

    /// <summary>完整 endpoint，如 https://api.deepseek.com/chat/completions（由调用方填全，本类直用不拼）。</summary>
    public string ApiUrl { get; }
    public string ApiKey { get; }
    public string Model { get; }

    /// <summary>URL 与 Key 均非空才算已配置（缺一不发请求）。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(ApiKey);

    public LlmClient(string apiUrl, string apiKey, string model)
    {
        ApiUrl = (apiUrl ?? "").Trim();
        ApiKey = (apiKey ?? "").Trim();
        Model = (model ?? "").Trim();
    }

    /// <summary>
    /// 非流式补全：单条 user message → 返回 (是否成功, 回复文本或错误说明)。
    /// temperature=0.3 偏确定性（候选排序需稳定，避免每次结果漂移）。
    /// cancellationToken 用于「调用 LLM」/「全桶调用」的取消按钮：取消时 SendAsync 抛 OperationCanceledException，
    /// 走 catch 统一回 "已取消"，不发空请求、不残留半截响应。
    /// </summary>
    public async Task<(bool Ok, string Text)> ChatAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return (false, "LLM API 未配置（URL/Key 为空）");
        if (string.IsNullOrWhiteSpace(Model)) return (false, "未配置模型名（设置页 LLM API · 模型）");

        var body = JsonSerializer.Serialize(new
        {
            model = Model,
            messages = new[] { new { role = "user", content = userMessage } },
            temperature = 0.3,
        });
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Authorization", "Bearer " + ApiKey);
            // HttpCompletionOption.ResponseHeadersRead：拿到响应头即视为请求成功，
            // 随后流式读 body 时仍受 token 控制——取消能在长响应中途及时中断，而非干等 5min 超时。
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var respBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {Truncate(respBody)}");
            using var doc = JsonDocument.Parse(respBody);
            var content = doc.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString();
            return content is null ? (false, "响应无 content 字段") : (true, content);
        }
        catch (OperationCanceledException) { return (false, "已取消"); }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Truncate(string s, int n = 300)
        => s.Length <= n ? s : s[..n] + "…";
}
