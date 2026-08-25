using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 历史日K数据服务：同花顺（配置了 API Key 时）→ 腾讯 → 新浪 三级降级。
/// akshare 依赖 Python 运行时，桌面 Avalonia 版与移动端均不可用——
/// 这里用行情商的网页日K接口替代，供 T1 风控（MA60/高点回撤）与 T8 收益回补使用。
/// 结果按 symbol 进程内缓存：一次任务内多 horizon 复用同一份K线，避免重复请求。
/// </summary>
public class KlineService
{
    private static readonly HttpClient Client = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<DailyBar>> _cache = new();
    private readonly ThsClient? _ths;

    static KlineService()
    {
        Client.DefaultRequestHeaders.UserAgent.TryParseAdd("ThreeBucket/1.0");
    }

    /// <param name="ths">同花顺（扶摇）客户端；配置了 API Key 时作为日K主源。</param>
    public KlineService(ThsClient? ths = null) => _ths = ths;

    /// <summary>个股日K（前复权）。code 为 6 位纯代码；网络失败返回 null。</summary>
    public Task<IReadOnlyList<DailyBar>?> GetStockDailyAsync(string code, int count = 320)
        => GetAsync(StockSymbol(code), count, qfq: true, isIndex: false);

    /// <summary>个股日K（前复权，强制刷新缓存）。监控引擎盘中评估用：
    /// 腾讯日K最后一根盘中实时滚动，forceRefresh 才能捕捉当日金叉/放量。</summary>
    public Task<IReadOnlyList<DailyBar>?> GetStockDailyFreshAsync(string code, int count = 320)
    {
        _cache.TryRemove("qfq:" + StockSymbol(code), out _);
        return GetAsync(StockSymbol(code), count, qfq: true, isIndex: false);
    }

    /// <summary>个股日K（不复权，历史真实价；T7 回测 5 年窗口用，口径同 Python 新浪直连）。</summary>
    public Task<IReadOnlyList<DailyBar>?> GetStockDailyRawAsync(string code, int count = 320)
        => GetAsync(StockSymbol(code), count, qfq: false, isIndex: false);

    /// <summary>指数日K（不复权）。code 为 6 位纯代码，如 000300 沪深300、000922 中证红利。</summary>
    public Task<IReadOnlyList<DailyBar>?> GetIndexDailyAsync(string code, int count = 320)
        => GetAsync(IndexSymbol(code), count, qfq: false, isIndex: true);

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

    private async Task<IReadOnlyList<DailyBar>?> GetAsync(string symbol, int count, bool qfq, bool isIndex = false)
    {
        var key = (qfq ? "qfq:" : "raw:") + symbol;
        if (_cache.TryGetValue(key, out var cached))
            return Clip(cached, count);

        IReadOnlyList<DailyBar>? bars = null;
        // 主源：同花顺（扶摇，配置了 API Key 时）；失败静默回退腾讯 → 新浪
        if (_ths is { IsConfigured: true })
        {
            try
            {
                bars = isIndex
                    ? await _ths.GetIndexDailyAsync(symbol, count)
                    : await _ths.GetStockDailyAsync(symbol, count, forwardAdjust: qfq);
            }
            catch { }
        }
        if (bars is null || bars.Count == 0)
        {
            try { bars = await FetchTencentAsync(symbol, count, qfq); }
            catch { }
        }
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

    // 腾讯日K：个股前复权返回 data.{symbol}.qfqday，指数返回 data.{symbol}.day（同一接口）。
    // 接口 version 16 起 param 强制 6 段（symbol,day,start,end,count,fq）：fq 空串=不复权，qfq=前复权；
    // 5 段旧格式（指数/不复权）会被拒（code=1 bad params），第 6 段必须带上（可为空）
    private static async Task<IReadOnlyList<DailyBar>?> FetchTencentAsync(string symbol, int count, bool qfq)
    {
        var url = $"https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={symbol},day,,,{count},{(qfq ? "qfq" : "")}";
        using var resp = await Client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object) return null; // data:[] → 腾讯拒绝该 datalen，回退新浪
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
    // money.finance 站点接口（与 Python 蓝本同源）：quotes.sina.cn 变体不支持 datalen>~1000，
    // 而 T7 回测需要 2500 根（约 10 年）
    private static async Task<IReadOnlyList<DailyBar>?> FetchSinaAsync(string symbol, int count)
    {
        var url = $"https://money.finance.sina.com.cn/quotes_service/api/json_v2.php/CN_MarketData.getKLineData?symbol={symbol}&scale=240&ma=no&datalen={count}";
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
