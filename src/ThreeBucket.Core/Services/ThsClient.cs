using System.Text.Json;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>同花顺（扶摇）行情快照行（/api/a-share/prices/snapshot）。</summary>
public sealed record ThsQuoteItem(
    string Thscode, string Ticker, string? Name,
    double? Last, double? Prev, double? ChangePct,
    double? Open, double? High, double? Low,
    double? Volume, double? Turnover, long? TimestampMs);

/// <summary>
/// 同花顺（扶摇）金融数据 API 客户端：https://fuyao.aicubes.cn
/// 作为行情快照 / 历史日K / 指数成分股 / 个股分红的主数据源（需 API Key），
/// 失败由调用方降级到腾讯/新浪/中证官网/东财。覆盖范围说明：
/// - 中证红利股息率（官方口径）：扶摇无对应接口，仍走 CsIndexClient 官网 indicator.xls；
/// - 业绩报表/预告、股东、国债收益率：扶摇接口形态不匹配（单股单期、无营收净利绝对值），仍走东财。
/// API Key 解析顺序：环境变量 THS_API_KEY（CI secrets）→ app_config.json 的 ThsApiKey（客户端本地）。
/// 未配置时 IsConfigured=false，各调用方直接走原有免费数据源，行为与接入前完全一致。
/// </summary>
public class ThsClient
{
    public const string DefaultBaseUrl = "https://fuyao.aicubes.cn";

    private static readonly HttpClient Client = new();

    private readonly string _apiKey;

    static ThsClient()
    {
        Client.DefaultRequestHeaders.UserAgent.TryParseAdd("ThreeBucket/1.0");
    }

    /// <summary>是否已配置 API Key（未配置时所有请求都会抛异常，调用方应先判断）。</summary>
    public bool IsConfigured { get; }

    /// <param name="apiKey">显式 Key；null/空白时按 环境变量 → app_config.json 顺序解析。</param>
    /// <param name="cacheDir">data/cache 目录（用于向上定位项目根的 app_config.json）。</param>
    public ThsClient(string? apiKey = null, string? cacheDir = null)
    {
        _apiKey = (string.IsNullOrWhiteSpace(apiKey) ? ResolveApiKey(cacheDir) : apiKey).Trim();
        IsConfigured = _apiKey.Length > 0;
    }

    // ── 公开接口 ───────────────────────────────────────────────────

    /// <summary>
    /// 批量实时行情快照（/api/a-share/prices/snapshot）。
    /// 单次最多 100 个 thscode，超出自动分批（批间 200ms 防限频）。
    /// 注意：本接口不返回股票名称（Name 恒为 null），名称由 UI 本地数据兜底。
    /// </summary>
    public async Task<List<ThsQuoteItem>> GetQuotesSnapshotAsync(
        IEnumerable<string> codes, CancellationToken ct = default)
    {
        var list = codes.Select(NormalizeCode).Where(c => c.Length == 6).Distinct().ToList();
        var result = new List<ThsQuoteItem>();
        for (var i = 0; i < list.Count; i += 100)
        {
            var chunk = list.Skip(i).Take(100).Select(c => ToThsCode(c)).ToList();
            var url = $"{DefaultBaseUrl}/api/a-share/prices/snapshot?thscodes={string.Join(",", chunk)}";
            var data = await GetDataAsync(url, ct);
            result.AddRange(ParseSnapshotItems(data));
            if (i + 100 < list.Count) await Task.Delay(200, ct);
        }
        return result;
    }

    /// <summary>
    /// 个股历史日K（/api/a-share/prices/historical，adjust=forward|none）。
    /// 官方单窗口上限 10 年，超出按 3600 天/段自动分段拼接去重；
    /// count 只用于换算时间窗口，实际返回根数可能略少（调用方自行裁剪）。
    /// </summary>
    public Task<List<DailyBar>> GetStockDailyAsync(string code, int count,
        bool forwardAdjust = true, CancellationToken ct = default)
        => GetHistoricalAsync(ToThsCode(code), count, forwardAdjust ? "forward" : "none", index: false, ct);

    /// <summary>指数历史日K（/api/a-share-index/prices/historical，指数无复权语义）。</summary>
    public Task<List<DailyBar>> GetIndexDailyAsync(string code, int count,
        CancellationToken ct = default)
        => GetHistoricalAsync(IndexThsCode(code), count, adjust: null, index: true, ct);

    /// <summary>指数成分股（/api/a-share-index/constituents/ths-stock-list）。与官网 cons.xls 同口径。</summary>
    public async Task<List<ConsRow>> GetConstituentsAsync(string indexCode,
        CancellationToken ct = default)
    {
        var url = $"{DefaultBaseUrl}/api/a-share-index/constituents/ths-stock-list?thscode={IndexThsCode(indexCode)}";
        var data = await GetDataAsync(url, ct);
        if (!data.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Array)
            return new List<ConsRow>();
        var rows = new List<ConsRow>();
        foreach (var e in item.EnumerateArray())
        {
            var ticker = Str(e, "ticker");
            var name = Str(e, "name");
            if (ticker.Length == 6 && name.Length > 0)
                rows.Add(new ConsRow(ticker, name));
        }
        return rows;
    }

    /// <summary>
    /// 个股历史现金分红（/api/a-share/corporate-actions/adjustment-factors）。
    /// dividend_per_share 为每股税前股利（元），与东财 RPT_SHAREBONUS_DET /10 后同口径；
    /// 纯送股/配股事件（现金=0）已过滤，按除权除息日升序返回。
    /// </summary>
    public async Task<List<DividendRow>> GetDividendsAsync(string code,
        CancellationToken ct = default)
    {
        var url = $"{DefaultBaseUrl}/api/a-share/corporate-actions/adjustment-factors?thscode={ToThsCode(code)}";
        var data = await GetDataAsync(url, ct);
        if (!data.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Array)
            return new List<DividendRow>();

        var rows = new List<DividendRow>();
        foreach (var e in item.EnumerateArray())
        {
            if (e.TryGetProperty("dividend_per_share", out var dps) && dps.ValueKind == JsonValueKind.Number
                && dps.GetDouble() > 0
                && e.TryGetProperty("ex_date_ms", out var ex) && ex.ValueKind == JsonValueKind.Number)
                rows.Add(new DividendRow(CnDate(ex.GetInt64()), dps.GetDouble()));
        }
        rows.Sort((a, b) => a.ExDate.CompareTo(b.ExDate));
        return rows;
    }

    // ── 代码格式转换（供 ThsMarketDataSource 等复用）────────────────

    /// <summary>任意输入（sh600519 / 600519.SH / 600519）→ 标准化 6 位纯代码。</summary>
    public static string NormalizeCode(string code)
    {
        var s = code.Trim();
        if (s.Length > 3 && s.Contains('.'))
            s = s.Split('.')[0];
        if (s.Length > 2 && (s.StartsWith("sh", StringComparison.OrdinalIgnoreCase)
                             || s.StartsWith("sz", StringComparison.OrdinalIgnoreCase)
                             || s.StartsWith("bj", StringComparison.OrdinalIgnoreCase)))
            s = s[2..];
        return s.PadLeft(6, '0');
    }

    /// <summary>个股 thscode：920→BJ（北交所新段），6/5/9→SH，4/8→BJ，其余→SZ（与 EastMoneyClient 同规则）。</summary>
    public static string ToThsCode(string code)
    {
        var pure = NormalizeCode(code);
        var suffix = pure.StartsWith("920") ? "BJ"
            : pure[0] is '6' or '5' or '9' ? "SH"
            : pure[0] is '4' or '8' ? "BJ"
            : "SZ";
        return $"{pure}.{suffix}";
    }

    /// <summary>指数 thscode：399xxx（深证系列）→SZ，其余（000xxx 中证/上证系列）→SH。</summary>
    public static string IndexThsCode(string indexCode)
    {
        var pure = NormalizeCode(indexCode);
        return pure.StartsWith("39", StringComparison.Ordinal) ? $"{pure}.SZ" : $"{pure}.SH";
    }

    // ── 历史K线（分段拼接）─────────────────────────────────────────

    private async Task<List<DailyBar>> GetHistoricalAsync(string thscode, int count,
        string? adjust, bool index, CancellationToken ct)
    {
        // 1 交易日 ≈ 1.55 自然日（A股年均约 243 个交易日）+ 30 天缓冲
        var totalDays = (int)Math.Ceiling(count * 1.55) + 30;
        var segments = Math.Max(1, (int)Math.Ceiling(totalDays / 3600.0)); // 单窗口 ≤3600 天（官方上限 10 年）

        var end = TradingCalendar.NowCn().Date;
        var bars = new List<DailyBar>();
        for (var i = 0; i < segments; i++)
        {
            // 从当前日往前切段：[..., segEnd] 每段 3600 天，段间留 1 天重叠由日期去重兜住
            var segStart = end.AddDays(-(i + 1) * 3600L);
            var segEnd = end.AddDays(-i * 3600L);
            var path = index ? "/api/a-share-index/prices/historical" : "/api/a-share/prices/historical";
            var url = $"{DefaultBaseUrl}{path}?thscode={thscode}&interval=1d" +
                      $"&start={ToUnixMs(segStart)}&end={ToUnixMs(segEnd)}";
            if (adjust is not null) url += $"&adjust={adjust}";

            var data = await GetDataAsync(url, ct);
            if (data.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Array)
                foreach (var e in item.EnumerateArray())
                {
                    if (!e.TryGetProperty("date_ms", out var d) || d.ValueKind != JsonValueKind.Number) continue;
                    bars.Add(new DailyBar(CnDate(d.GetInt64()),
                        Num(e, "open_price"), Num(e, "close_price"),
                        Num(e, "high_price"), Num(e, "low_price"), Num(e, "volume")));
                }
        }

        // 升序 + 段间重叠去重
        var merged = bars.GroupBy(b => b.Date).Select(g => g.First())
            .OrderBy(b => b.Date).ToList();
        return merged;
    }

    // ── 快照解析（公开静态：供离线样本回归测试）─────────────────────

    /// <summary>解析快照响应的 data 节点（含 timestamp 与 item 数组）。</summary>
    public static List<ThsQuoteItem> ParseSnapshotItems(JsonElement data)
    {
        var ts = data.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number
            ? (long?)t.GetInt64() : null;
        if (!data.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Array)
            return new List<ThsQuoteItem>();

        var rows = new List<ThsQuoteItem>();
        foreach (var e in item.EnumerateArray())
        {
            var thscode = Str(e, "thscode");
            if (thscode.Length == 0) continue;
            rows.Add(new ThsQuoteItem(thscode, Str(e, "ticker"),
                e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                Num(e, "last_price"), Num(e, "prev_price"), Num(e, "price_change_ratio_pct"),
                Num(e, "open_price"), Num(e, "high_price"), Num(e, "low_price"),
                Num(e, "volume"), Num(e, "turnover"), ts));
        }
        return rows;
    }

    // ── HTTP 与信封解析 ────────────────────────────────────────────

    /// <summary>GET 请求并校验统一信封（HTTP 恒 200，业务错误经 code 表达：0=成功）。</summary>
    private async Task<JsonElement> GetDataAsync(string url, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("同花顺数据源未配置（THS_API_KEY 环境变量或 app_config.json 的 ThsApiKey）");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-api-key", _apiKey);
        using var resp = await Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
        if (code != 0)
        {
            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : "未知错误";
            throw new InvalidOperationException($"THS API code={code}: {msg}");
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("THS API 响应缺少 data 节点");
        return data.Clone();
    }

    // ── API Key 解析：环境变量（CI secrets）→ app_config.json（客户端本地）──

    private static string ResolveApiKey(string? cacheDir)
    {
        var env = Environment.GetEnvironmentVariable("THS_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        // app_config.json 候选：cacheDir 上两级（data/cache → 项目根，与 DataStore.LoadConfig 同布局）、当前目录
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(cacheDir))
        {
            var root = Path.GetFullPath(Path.Combine(cacheDir, "..", ".."));
            candidates.Add(Path.Combine(root, "app_config.json"));
        }
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "app_config.json"));

        foreach (var p in candidates)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(p));
                if (!string.IsNullOrWhiteSpace(cfg?.ThsApiKey)) return cfg.ThsApiKey;
            }
            catch { /* 配置损坏时静默跳过，视为未配置 */ }
        }
        return "";
    }

    // ── 解析辅助 ───────────────────────────────────────────────────

    private static string Str(JsonElement o, string prop)
        => o.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    private static double Num(JsonElement o, string prop)
        => o.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

    /// <summary>毫秒 Unix 时间戳 → 北京时间自然日（Asia/Shanghai 零点口径）。</summary>
    private static DateTime CnDate(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(TimeSpan.FromHours(8)).Date;

    private static long ToUnixMs(DateTime cnDate)
        => new DateTimeOffset(DateTime.SpecifyKind(cnDate, DateTimeKind.Unspecified), TimeSpan.FromHours(8))
            .ToUnixTimeMilliseconds();
}
