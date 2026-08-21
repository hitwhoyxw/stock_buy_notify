using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.DataSources;

/// <summary>
/// 基于 HTTP 的数据源基类：统一处理 HttpClient、请求头、编码解码，
/// 子类只需提供 ① 接口地址 ② 响应编码 ③ 解析逻辑。这样“多数据源”的接入成本极低。
/// </summary>
public abstract class HttpMarketDataSourceBase : IMarketDataSource
{
    // 注册 GBK 等中文编码提供程序（包 System.Text.Encoding.CodePages；腾讯/新浪接口均为 GBK）。
    // 直接引用类型而非反射 —— 短程序集名的 Type.GetType 解析不到时返回 null，会导致 GBK 未注册、
    // GetEncoding("GBK") 抛 ArgumentException，行情拉取全部失败（静默回退为空结果）。
    static HttpMarketDataSourceBase()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public abstract MarketDataSourceId Id { get; }
    public abstract string DisplayName { get; }

    /// <summary>接口地址，通常已包含 "=..." 前缀，例如 https://hq.sinajs.cn/list= </summary>
    protected abstract string Endpoint { get; }

    /// <summary>响应编码。新浪/腾讯均为 GBK。</summary>
    protected virtual Encoding ResponseEncoding => Encoding.GetEncoding("GBK");

    /// <summary>额外的请求头（如新浪需 Referer，否则 403）。</summary>
    protected virtual IEnumerable<KeyValuePair<string, string>>? ExtraHeaders => null;

    // 复用单个 HttpClient，避免套接字耗尽
    private static readonly HttpClient SharedClient = new();

    public abstract IReadOnlyList<RealTimeQuote> ParseRealTimeResponse(string rawResponse);

    public async Task<IReadOnlyList<RealTimeQuote>> GetRealTimeQuotesAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var list = new List<string>(symbols);
        if (list.Count == 0)
            return Array.Empty<RealTimeQuote>();

        var url = $"{Endpoint}{string.Join(",", list)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (ExtraHeaders is not null)
            foreach (var h in ExtraHeaders)
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        req.Headers.UserAgent.TryParseAdd("ThreeBucket/0.1");

        using var resp = await SharedClient.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        var text = ResponseEncoding.GetString(bytes);
        return ParseRealTimeResponse(text);
    }

    // ---- 解析辅助（供子类复用）----

    protected static bool TryDecimal(string[] p, int i, Action<decimal> set)
    {
        if (i < p.Length && decimal.TryParse(p[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
        {
            set(v);
            return true;
        }
        return false;
    }

    protected static bool TryLong(string[] p, int i, Action<long> set)
    {
        if (i < p.Length && long.TryParse(p[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
        {
            set(v);
            return true;
        }
        return false;
    }

    protected static bool TryTimestamp(string date, string time, Action<DateTime> set)
    {
        if (DateTime.TryParse($"{date} {time}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            set(dt);
            return true;
        }
        return false;
    }
}
