using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 历史日K数据服务：腾讯主源 + 新浪回退。
/// akshare 依赖 Python 运行时，桌面 Avalonia 版与移动端均不可用——
/// 这里用两家行情商的网页日K接口替代，供 T1 风控（MA60/高点回撤）与 T8 收益回补使用。
/// 结果按 symbol 进程内缓存：一次任务内多 horizon 复用同一份K线，避免重复请求。
/// </summary>
public class KlineService
{
    private static readonly HttpClient Client = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<DailyBar>> _cache = new();

    static KlineService()
    {
        Client.DefaultRequestHeaders.UserAgent.TryParseAdd("ThreeBucket/1.0");
    }

    /// <summary>个股日K（前复权）。code 为 6 位纯代码；网络失败返回 null。</summary>
    public Task<IReadOnlyList<DailyBar>?> GetStockDailyAsync(string code, int count = 320)
        => GetAsync(StockSymbol(code), count, qfq: true);

    /// <summary>指数日K（不复权）。code 为 6 位纯代码，如 000300 沪深300、000922 中证红利。</summary>
    public Task<IReadOnlyList<DailyBar>?> GetIndexDailyAsync(string code, int count = 320)
        => GetAsync(IndexSymbol(code), count, qfq: false);

    /// <summary>个股转接口符号：6/5/9 开头 sh，4/8 开头 bj，其余 sz（与 QuoteService.ToSymbol 同规则）。</summary>
    public static string StockSymbol(string code)
    {
        var pure = code.Split('.')[0].Trim().PadLeft(6, '0');
        return pure[0] is '6' or '5' or '9' ? "sh" + pure
            : pure[0] is '4' or '8' ? "bj" + pure
            : "sz" + pure;
    }

    /// <summary>指数转接口符号：399xxx 走深交所，其余（000xxx 中证/上证系列）走上交所。</summary>
    public static string IndexSymbol(string code)
    {
        var pure = code.Split('.')[0].Trim().PadLeft(6, '0');
        return pure.StartsWith("39", StringComparison.Ordinal) ? "sz" + pure : "sh" + pure;
    }

    private async Task<IReadOnlyList<DailyBar>?> GetAsync(string symbol, int count, bool qfq)
    {
        var key = (qfq ? "qfq:" : "raw:") + symbol;
        if (_cache.TryGetValue(key, out var cached))
            return Clip(cached, count);

        IReadOnlyList<DailyBar>? bars = null;
        try { bars = await FetchTencentAsync(symbol, count, qfq); }
        catch { /* 主源失败静默回退新浪 */ }
        if (bars is null || bars.Count == 0)
        {
            try { bars = await FetchSinaAsync(symbol, count); }
            catch { }
        }
        if (bars is not { Count: > 0 }) return null;
        _cache[key] = bars;
        return Clip(bars, count);
    }

    private static IReadOnlyList<DailyBar> Clip(IReadOnlyList<DailyBar> bars, int count)
        => bars.Count <= count ? bars : bars.Skip(bars.Count - count).ToList();

    // 腾讯日K：个股前复权返回 data.{symbol}.qfqday，指数返回 data.{symbol}.day（同一接口）
    private static async Task<IReadOnlyList<DailyBar>?> FetchTencentAsync(string symbol, int count, bool qfq)
    {
        var url = $"https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={symbol},day,,,{count}{(qfq ? ",qfq" : "")}";
        using var resp = await Client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty(symbol, out var node) || node.ValueKind != JsonValueKind.Object) return null;

        JsonElement arr;
        if (node.TryGetProperty(qfq ? "qfqday" : "day", out var a) && a.ValueKind == JsonValueKind.Array) arr = a;
        else if (node.TryGetProperty("day", out var b) && b.ValueKind == JsonValueKind.Array) arr = b; // 请求 qfq 但仅返回 day
        else if (node.TryGetProperty("qfqday", out var c) && c.ValueKind == JsonValueKind.Array) arr = c;
        else return null;

        // 腾讯行格式：[日期, 开盘, 收盘, 最高, 最低, 成交量, ...]
        var list = new List<DailyBar>();
        foreach (var row in arr.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var p = row.EnumerateArray().ToArray();
            if (p.Length < 6) continue;
            if (!DateTime.TryParseExact(p[0].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                continue;
            list.Add(new DailyBar(d, Num(p[1]), Num(p[2]), Num(p[3]), Num(p[4]), Num(p[5])));
        }
        return list;
    }

    // 新浪日K（回退源，不复权）：[{day,open,high,low,close,volume},...]
    private static async Task<IReadOnlyList<DailyBar>?> FetchSinaAsync(string symbol, int count)
    {
        var url = $"https://quotes.sina.cn/cn/api/json_v2.php/CN_MarketDataService.getKLineData?symbol={symbol}&scale=240&ma=no&datalen={count}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://finance.sina.com.cn");
        using var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var list = new List<DailyBar>();
        foreach (var o in doc.RootElement.EnumerateArray())
        {
            if (!DateTime.TryParseExact(o.TryGetProperty("day", out var day) ? day.GetString() : null,
                    "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            list.Add(new DailyBar(d, Num(o, "open"), Num(o, "close"), Num(o, "high"), Num(o, "low"), Num(o, "volume")));
        }
        return list;
    }

    private static double Num(JsonElement e)
        => e.ValueKind == JsonValueKind.String
           && double.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double Num(JsonElement o, string prop)
        => o.TryGetProperty(prop, out var e) ? Num(e) : 0;
}
