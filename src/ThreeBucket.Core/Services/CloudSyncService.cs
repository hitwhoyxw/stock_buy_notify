using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ThreeBucket.Core.Services;

/// <summary>云端一行同步数据（对应 three_bucket_sync 表）。</summary>
public class CloudSyncRow
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("payload")] public JsonElement Payload { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("device")] public string Device { get; set; } = "";
}

/// <summary>
/// Supabase 云同步：把策略 / 交易流水 / 监控自选 / 提醒历史推送到免费的 Supabase 项目，
/// 在不同平台（Windows / macOS / Linux）间同步，免去每台机器重新录入。
/// <para>直接调用 PostgREST HTTP 接口，无需 SDK —— 只需配置项目 URL 与 API Key。</para>
/// <para>表结构见 <see cref="CreateTableSql"/>，在 Supabase 控制台 SQL Editor 执行一次即可。</para>
/// </summary>
public class CloudSyncService
{
    public const string TableName = "three_bucket_sync";

    /// <summary>建表 SQL：在 Supabase 控制台（SQL Editor）执行一次即可。</summary>
    public static readonly string CreateTableSql = """
        create table if not exists three_bucket_sync (
          kind       text primary key,      -- strategies / trades / watchlist / alerts
          payload    jsonb not null,        -- 完整行数据
          updated_at timestamptz not null default now(),
          device     text                   -- 上传设备（排查用）
        );
        alter table three_bucket_sync enable row level security;
        drop policy if exists three_bucket_sync_all on three_bucket_sync;
        create policy three_bucket_sync_all on three_bucket_sync
          for all to anon, authenticated using (true) with check (true);
        """;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _url;   // https://xxxx.supabase.co
    private readonly string _key;   // anon public key（个人使用也可用 service_role key）

    public CloudSyncService(string url, string key)
    {
        _url = (url ?? "").Trim().TrimEnd('/');
        _key = (key ?? "").Trim();
    }

    public bool IsConfigured =>
        _url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && _key.Length > 20;

    private HttpRequestMessage Req(HttpMethod method, string path)
    {
        var m = new HttpRequestMessage(method, $"{_url}/rest/v1/{path}");
        m.Headers.Add("apikey", _key);
        m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return m;
    }

    /// <summary>测试连接与表就绪状态，返回 (是否可用, 给用户看的消息)。</summary>
    public async Task<(bool ok, string msg)> TestAsync()
    {
        if (!IsConfigured) return (false, "请先填写 Supabase URL 和 API Key");
        try
        {
            using var req = Req(HttpMethod.Get, $"{TableName}?select=kind&limit=1");
            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return (true, "连接成功，同步表就绪 ✓");
            var body = await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                return (false, "API Key 无效或权限不足（401/403）");
            if ((int)resp.StatusCode == 404 || body.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return (false, "连接成功，但同步表不存在 —— 请先点「复制建表 SQL」到 Supabase SQL Editor 执行");
            return (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body)}");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败: {ex.Message}");
        }
    }

    /// <summary>上传：kind -> payload（jsonb），按 kind upsert 覆盖云端同 kind 行。</summary>
    public async Task<(bool ok, string msg)> PushAsync(
        Dictionary<string, object> payloads, string device)
    {
        if (!IsConfigured) return (false, "请先在设置中配置 Supabase URL 和 API Key");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var body = payloads.Select(kv => (object)new Dictionary<string, object>
        {
            ["kind"] = kv.Key,
            ["payload"] = kv.Value,
            ["updated_at"] = now,
            ["device"] = device,
        }).ToList();
        var json = JsonSerializer.Serialize(body, JsonOpts);
        try
        {
            using var req = Req(HttpMethod.Post, TableName);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.Add("Prefer", "resolution=merge-duplicates"); // 按 kind 主键 upsert
            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                return (true, $"已上传 {payloads.Count} 类数据到云端（{string.Join("/", payloads.Keys)}）");
            var err = await resp.Content.ReadAsStringAsync();
            if (err.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return (false, "云端还没有同步表 —— 请先点「复制建表 SQL」到 Supabase SQL Editor 执行");
            return (false, $"上传失败 HTTP {(int)resp.StatusCode}: {Truncate(err)}");
        }
        catch (Exception ex)
        {
            return (false, $"上传失败: {ex.Message}");
        }
    }

    /// <summary>下载云端全部同步行。</summary>
    public async Task<(List<CloudSyncRow> rows, string error)> PullAsync()
    {
        if (!IsConfigured) return (new(), "请先在设置中配置 Supabase URL 和 API Key");
        try
        {
            using var req = Req(HttpMethod.Get, $"{TableName}?select=kind,payload,updated_at,device");
            using var resp = await Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                if (body.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                    return (new(), "云端还没有同步表 —— 先执行建表 SQL，并从任意一端上传一次");
                return (new(), $"下载失败 HTTP {(int)resp.StatusCode}: {Truncate(body)}");
            }
            var rows = JsonSerializer.Deserialize<List<CloudSyncRow>>(body, JsonOpts) ?? new();
            return (rows, "");
        }
        catch (Exception ex)
        {
            return (new(), $"下载失败: {ex.Message}");
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
