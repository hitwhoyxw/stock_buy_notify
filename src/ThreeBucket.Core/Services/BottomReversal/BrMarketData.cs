using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>
/// 东方财富行情：push2his 日K（含成交额 f57、换手率 f61；项目原 KlineService 的腾讯/新浪源没有这两个字段，
/// 且腾讯 fqkline 目前 501，故 S27 单独走东财）与 clist 股票列表（沪/深/北）。结果带来源与抓取时间。
/// </summary>
public static class BrMarketData
{
    public const string KlineUrl = "https://push2his.eastmoney.com/api/qt/stock/kline/get";
    public const string ClistUrl = "https://82.push2.eastmoney.com/api/qt/clist/get";
    public const string SrcKline = "东方财富 push2his.eastmoney.com/api/qt/stock/kline/get（klt=101 日K）";
    public const string SrcClist = "东方财富 push2.eastmoney.com/api/qt/clist/get";

    private static readonly HttpClient Http = CreateClient();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = 16 })
        { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        h.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        return h;
    }

    public static DateTime NowCn() => DateTime.UtcNow.AddHours(8);

    public static string Board(string code)
        => code.StartsWith("92") || code.StartsWith('8') || code.StartsWith('4') ? "bj"
         : code.StartsWith('6') || code.StartsWith('9') ? "sh" : "sz";

    public static string Secid(string code) => (Board(code) == "sh" ? "1." : "0.") + code;

    public static double LimitPct(string code)
    {
        if (Board(code) == "bj") return 0.30;
        if (code.StartsWith("300") || code.StartsWith("301") || code.StartsWith("688") || code.StartsWith("689")) return 0.20;
        return 0.10;
    }

    public sealed class Kline
    {
        public BrSeries S = new();
        public string Name = "";
        public string Source = "";
        public string FetchedAt = "";
    }

    /// <summary>fqt=1 前复权（指标），fqt=0 不复权（实际成交价）。volume 手、amount 元、turnover %。失败返回 null。</summary>
    public static async Task<Kline?> FetchKlineAsync(string secid, int lmt = 260, int fqt = 1, int retries = 3, CancellationToken ct = default)
    {
        var url = $"{KlineUrl}?secid={secid}&fields1=f1,f2,f3&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61&klt=101&fqt={fqt}&end=20500101&lmt={lmt}";
        for (var k = 0; k < retries; k++)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("no data");
                if (!data.TryGetProperty("klines", out var kl) || kl.ValueKind != JsonValueKind.Array || kl.GetArrayLength() == 0)
                    throw new InvalidDataException("empty klines");
                var d = new List<string>(); var o = new List<double>(); var c = new List<double>(); var h = new List<double>(); var l = new List<double>();
                var v = new List<double>(); var a = new List<double>(); var t = new List<double>();
                foreach (var row in kl.EnumerateArray())
                {
                    var p = (row.GetString() ?? "").Split(',');
                    if (p.Length < 11) continue;
                    d.Add(p[0]); o.Add(Num(p[1])); c.Add(Num(p[2])); h.Add(Num(p[3])); l.Add(Num(p[4]));
                    v.Add(Num(p[5])); a.Add(Num(p[6])); t.Add(Num(p[10]));
                }
                return new Kline
                {
                    S = new BrSeries { Date = d.ToArray(), Open = o.ToArray(), Close = c.ToArray(), High = h.ToArray(), Low = l.ToArray(), Volume = v.ToArray(), Amount = a.ToArray(), Turnover = t.ToArray() },
                    Name = data.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                    Source = SrcKline + $" fqt={fqt}",
                    FetchedAt = NowCn().ToString("yyyy-MM-dd HH:mm:ss"),
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { await Task.Delay(800 * (k + 1), ct).ConfigureAwait(false); }
        }
        return null;
    }

    private static double Num(string s) => double.TryParse(s, NumberStyles.Float, Inv, out var x) ? x : double.NaN;

    public sealed record UniverseItem(string Code, string Name, string Board, string ListDate, string Industry, double? Mv, double? FloatMv);

    private static readonly Dictionary<string, string> Fs = new()
    {
        ["sh"] = "m:1+t:2,m:1+t:23",
        ["sz"] = "m:0+t:6,m:0+t:80",
        ["bj"] = "m:0+t:81+s:2048",
    };

    /// <summary>全市场股票列表，缓存 data/cache/br_universe.json（与 Python 版同格式）universe_cache_days 天。</summary>
    public static async Task<(List<UniverseItem> Items, string FetchedAt, string Source)> GetUniverseAsync(BrConfig cfg, string cacheDir, Action<string> log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, "br_universe.json");
        if (File.Exists(path))
        {
            try
            {
                var c = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))!;
                var fetched = c["fetched_at"]!.GetValue<string>();
                var age = NowCn() - DateTime.ParseExact(fetched, "yyyy-MM-dd HH:mm:ss", Inv);
                var arr = c["items"] as JsonArray;
                if (age.TotalDays < cfg.I("universe.universe_cache_days", 7) && arr is { Count: > 0 })
                {
                    var items = arr.Select(ParseItem).ToList();
                    log($"[S27] 股票列表用缓存 {path}（{fetched} 抓取，{items.Count} 只）");
                    return (items, fetched, c["source"]?.GetValue<string>() ?? SrcClist);
                }
            }
            catch { }
        }
        var list = new List<UniverseItem>();
        foreach (var b in new[] { "sh", "sz", "bj" })
        {
            List<JsonElement> raw;
            try { raw = await ClistAsync(Fs[b], ct).ConfigureAwait(false); }
            catch (Exception e) { log($"[S27] {b} 列表拉取失败: {e.Message}"); raw = []; }
            foreach (var d in raw)
            {
                var code = Str(d, "f12").Trim();
                if (code.Length != 6) continue;
                var ld = d.TryGetProperty("f26", out var f26) && f26.ValueKind == JsonValueKind.Number ? f26.GetRawText() : "";
                if (ld == "0") ld = "";
                var ind = Str(d, "f100"); if (ind == "-") ind = "";
                list.Add(new UniverseItem(code, Str(d, "f14"), b, ld, ind, NumOrNull(d, "f20"), NumOrNull(d, "f21")));
            }
            log($"[S27] {b} 列表 {raw.Count} 只");
        }
        if (list.Count == 0) throw new InvalidOperationException("股票列表为空");
        var now = NowCn().ToString("yyyy-MM-dd HH:mm:ss");
        var jarr = new JsonArray();
        foreach (var x in list)
            jarr.Add(new JsonObject
            {
                ["code"] = x.Code, ["name"] = x.Name, ["board"] = x.Board, ["list_date"] = x.ListDate, ["industry"] = x.Industry,
                ["mv"] = x.Mv, ["float_mv"] = x.FloatMv,
            });
        var root = new JsonObject { ["fetched_at"] = now, ["source"] = SrcClist, ["items"] = jarr };
        await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), ct).ConfigureAwait(false);
        return (list, now, SrcClist);
    }

    private static UniverseItem ParseItem(JsonNode? n)
    {
        string S(string k) => n?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        double? D(string k) => n?[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        return new UniverseItem(S("code"), S("name"), S("board"), S("list_date"), S("industry"), D("mv"), D("float_mv"));
    }

    private static string Str(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "") : "";

    private static double? NumOrNull(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static async Task<List<JsonElement>> ClistAsync(string fs, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        for (var pn = 1; ; pn++)
        {
            var url = $"{ClistUrl}?pn={pn}&pz=100&po=1&np=1&fltt=2&invt=2&fid=f12&fs={fs}&fields=f2,f6,f12,f14,f20,f21,f26,f100";
            JsonDocument? doc = null;
            for (var k = 0; k < 3 && doc is null; k++)
            {
                try
                {
                    using var r = await Http.GetAsync(url, ct).ConfigureAwait(false);
                    r.EnsureSuccessStatusCode();
                    doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                }
                catch { await Task.Delay(1000 * (k + 1), ct).ConfigureAwait(false); }
            }
            if (doc is null) throw new InvalidOperationException($"clist 拉取失败 fs={fs} pn={pn}");
            var data = doc.RootElement.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.Object ? dd : default;
            if (data.ValueKind != JsonValueKind.Object) break;
            var total = data.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
            var got = 0;
            if (data.TryGetProperty("diff", out var diff))
            {
                if (diff.ValueKind == JsonValueKind.Array) foreach (var x in diff.EnumerateArray()) { items.Add(x.Clone()); got++; }
                else if (diff.ValueKind == JsonValueKind.Object) foreach (var x in diff.EnumerateObject()) { items.Add(x.Value.Clone()); got++; }
            }
            if (got == 0 || items.Count >= total) break;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return items;
    }
}
