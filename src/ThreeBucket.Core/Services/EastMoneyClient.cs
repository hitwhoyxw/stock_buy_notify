using System.Globalization;
using System.Text.Json;

namespace ThreeBucket.Core.Services;

/// <summary>东财业绩报表行（RPT_LICO_FN_CPD，替代 akshare stock_yjbb_em）。</summary>
public sealed record YjbbRow(
    string Code, string Name, string Industry,
    double? Revenue,   // 营业总收入（元）
    double? RevYoy,    // 营收同比 %
    double? Np,        // 归母净利润（元）
    double? NpYoy,     // 净利同比 %
    double? Qoq,       // 净利润季度环比增长 %（SJLHZ，T7 B/C 桶业绩动量信号用）
    double? Roe,       // 加权平均 ROE（报告期口径，未年化）
    double? OcfPs,     // 每股经营现金流
    double? Eps,       // 每股收益
    double? GrossMargin); // 销售毛利率 %

/// <summary>东财业绩预告行（RPT_PUBLIC_OP_NEWPREDICT，替代 akshare stock_yjyg_em；每股一行）。</summary>
public sealed record YjygRow(
    string Code, string Name, string PreviewType,
    double? GainPct,   // 预告变动幅度（INCREASE_JZ，利润类指标取最大）
    string Excerpt,    // 业绩变动（PREDICT_CONTENT）
    string Reason,     // 变动原因（CHANGE_REASON_EXPLAIN）
    string Period);

/// <summary>十大流通股东行（RPT_F10_EH_FREEHOLDERS）。</summary>
public sealed record HolderRow(string Name, double? Ratio);

/// <summary>主要财务指标行（RPT_F10_FINANCE_MAINFINADATA，逐票拉取，按报告期降序返回多期）。
/// 供 B 桶填充巴菲特式"缺少"指标：ROIC/资产负债率/利息保障/BPS/净现比等。</summary>
public sealed record MainFinRow(
    string Code,
    DateTime? ReportDate,
    double? RoeWeighted,          // ROEJQ 加权ROE %
    double? Roic,                 // ROIC %
    double? DebtRatio,            // ZCFZL 资产负债率 %
    double? InterestCoverage,     // INTEREST_COVERAGE_RATIO 利息保障倍数
    double? Bps,                  // BPS 每股净资产
    double? OcfPs,                // MGJYXJJE 每股经营现金流
    double? OcfToNp,              // NCO_NETPROFIT 净现比（经营现金净流量/净利润）
    double? GrossMargin);         // XSMLL 销售毛利率 %

/// <summary>个股现金流量表行（RPT_F10_FINANCE_GCASHFLOW，逐票，按报告期降序）。
/// 供 B 桶计算 FCF 三项：FCF利润率/Capex强度/所有者收益（净值口径用年报期累计值）。</summary>
public sealed record CashFlowRow(
    string Code,
    DateTime? ReportDate,
    double? Ocf,              // NETCASH_OPERATE 经营活动现金流净额（元）
    double? Capex,            // CONSTRUCT_LONG_ASSET 购建长期资产支出（元）
    double? Depreciation,     // FA_IR_DEPR 固定资产折旧（元）
    double? IntangibleAmort,  // IA_AMORTIZE 无形资产摊销（元）
    double? DeferredAmort);   // DEFER_INCOME_AMORTIZE 长期待摊费用摊销（元）

/// <summary>现金分红行（RPT_SHAREBONUS_DET，替代 akshare stock_dividend_cninfo）。</summary>
public sealed record DividendRow(DateTime ExDate, double Dps);

/// <summary>资产负债表行（RPT_F10_FINANCE_GBALANCE，逐票拉取，按报告期返回多期）。
/// 只取订单积压指标需要的科目：合同负债/存货/应收账款 期末值 + 各自同比增速。</summary>
public sealed record BalanceSheetRow(
    string Code,
    DateTime? ReportDate,
    double? ContractLiab,     // 合同负债（元）
    double? ContractLiabYoy,   // 合同负债同比 %
    double? Inventory,         // 存货（元）
    double? InventoryYoy,      // 存货同比 %
    double? AccountsRece,      // 应收账款（元）
    double? AccountsReceYoy);  // 应收账款同比 %

/// <summary>
/// 东方财富 datacenter HTTP 客户端（桌面/移动端通用，替代 akshare 的 5 个数据接口）。
/// 全部接口已实测：yjbb / yjyg / 机构持仓 / 分红送配 / 中国国债收益率。
/// 历史报告期数据不变 → 磁盘 JSON 缓存永久有效；最新期快照 24h 过期。
/// 个股分红支持同花顺（扶摇）主源：配置了 API Key 时优先走 API，失败降级本源。
/// </summary>
public class EastMoneyClient
{
    private static readonly HttpClient Client = new();

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _cacheDir;
    private readonly ThsClient? _ths;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static EastMoneyClient()
    {
        Client.DefaultRequestHeaders.UserAgent.TryParseAdd("Mozilla/5.0 (ThreeBucket/1.0)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://data.eastmoney.com/");
    }

    /// <param name="cacheDir">磁盘缓存目录（data/cache）。</param>
    /// <param name="ths">同花顺（扶摇）客户端；配置了 API Key 时个股分红优先走同花顺。</param>
    public EastMoneyClient(string? cacheDir = null, ThsClient? ths = null)
    {
        _cacheDir = cacheDir ?? Path.Combine("data", "cache");
        _ths = ths;
        try { Directory.CreateDirectory(_cacheDir); } catch { /* 移动端沙盒外路径由调用方保证 */ }
    }

    // ── 公开接口 ───────────────────────────────────────────────────

    /// <summary>全市场业绩报表（分页拉取；period 形如 20260630）。失败返回空表。</summary>
    public async Task<List<YjbbRow>> GetYjbbAsync(string period,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(_cacheDir, $"yjbb_{period}.json");
        if (TryReadCache(cacheFile, out List<YjbbRow>? cached))
        {
            log?.Invoke($"[EM] yjbb {period} 命中缓存 {cached!.Count} 行");
            return cached;
        }

        var date = $"{period[..4]}-{period[4..6]}-{period[6..]}";
        var filter = Uri.EscapeDataString($"(REPORTDATE='{date}')");
        var rows = await FetchPagedAsync(
            $"https://datacenter-web.eastmoney.com/api/data/v1/get?sortColumns=UPDATE_DATE,SECURITY_CODE&sortTypes=-1,-1&pageSize=500&pageNumber={{0}}&reportName=RPT_LICO_FN_CPD&columns=ALL&filter={filter}",
            item => new YjbbRow(
                Str(item, "SECURITY_CODE").PadLeft(6, '0'),
                Str(item, "SECURITY_NAME_ABBR"),
                Str(item, "PUBLISHNAME"),
                Num(item, "TOTAL_OPERATE_INCOME"),
                Num(item, "YSTZ"),
                Num(item, "PARENT_NETPROFIT"),
                Num(item, "SJLTZ"),
                Num(item, "SJLHZ"),
                Num(item, "WEIGHTAVG_ROE"),
                Num(item, "MGJYXJJE"),
                Num(item, "BASIC_EPS"),
                Num(item, "XSMLL")),
            log, ct);

        // 历史期数据不会变 → 永久缓存；最新期（当年）靠 24h TTL 失效
        TryWriteCache(cacheFile, rows, IsHistoricalPeriod(period) ? TimeSpan.MaxValue : TimeSpan.FromHours(24));
        return rows;
    }

    /// <summary>
    /// 最新业绩预告快照（正面类型：预增/略增/扭亏/续盈/减亏；每股一行，利润类指标优先）。
    /// 报告期候选与 Python lib/data_fetch 一致，第一个非空期胜出。
    /// </summary>
    public async Task<List<YjygRow>> GetYjygSnapshotAsync(
        Action<string>? log = null, CancellationToken ct = default)
    {
        var today = TradingCalendar.NowCn();
        var (y, m) = (today.Year, today.Month);
        var candidates = m >= 11 ? new[] { $"{y}0930", $"{y}0630" }
            : m >= 9 ? new[] { $"{y}0630", $"{y}0331" }
            : m >= 5 ? new[] { $"{y}0331", $"{y - 1}1231" }
            : new[] { $"{y - 1}1231", $"{y - 1}0930" };

        foreach (var period in candidates)
        {
            var rows = await GetYjygAsync(period, log, ct);
            if (rows.Count > 0) return rows;
        }
        return new List<YjygRow>();
    }

    /// <summary>全市场业绩预告（分页 + 每股聚合；period 形如 20260630）。</summary>
    public async Task<List<YjygRow>> GetYjygAsync(string period,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(_cacheDir, $"yjyg_{period}.json");
        if (TryReadCache(cacheFile, out List<YjygRow>? cached))
        {
            log?.Invoke($"[EM] yjyg {period} 命中缓存 {cached!.Count} 行");
            return cached;
        }

        var date = $"{period[..4]}-{period[4..6]}-{period[6..]}";
        // 注意：此接口 filter 前必须保留一个空格（东财网关约定），域名与 yjbb 不同
        var filter = Uri.EscapeDataString($" (REPORT_DATE='{date}')");
        var raw = await FetchPagedRawAsync(
            $"https://datacenter.eastmoney.com/securities/api/data/v1/get?sortColumns=NOTICE_DATE,SECURITY_CODE&sortTypes=-1,-1&pageSize=500&pageNumber={{0}}&reportName=RPT_PUBLIC_OP_NEWPREDICT&columns=ALL&filter={filter}",
            log, ct);

        var positive = new HashSet<string>(StringComparer.Ordinal)
            { "预增", "略增", "扭亏", "续盈", "减亏" };
        var byCode = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            if (!positive.Contains(Str(item, "PREDICT_TYPE"))) continue;
            var code = Str(item, "SECURITY_CODE").PadLeft(6, '0');
            if (!byCode.TryGetValue(code, out var list)) byCode[code] = list = new();
            list.Add(item);
        }

        var rows = new List<YjygRow>();
        foreach (var (code, items) in byCode)
        {
            // 利润类指标行优先（口径与 Python _fetch_yjyg_snapshot 一致）
            var profit = items.Where(i => Str(i, "PREDICT_FINANCE").Contains("净利润")).ToList();
            var pool = profit.Count > 0 ? profit : items;
            // 预告变动幅度取利润行中 INCREASE_JZ 最大者（akshare"业绩变动幅度"列的来源字段）
            var best = pool.OrderByDescending(i => Num(i, "INCREASE_JZ") ?? double.MinValue).First();
            var reason = Str(best, "CHANGE_REASON_EXPLAIN");
            if (reason.Length == 0)
                reason = items.Select(i => Str(i, "CHANGE_REASON_EXPLAIN"))
                    .FirstOrDefault(s => s.Length > 0) ?? "";
            rows.Add(new YjygRow(code, Str(best, "SECURITY_NAME_ABBR"),
                Str(best, "PREDICT_TYPE"), Num(best, "INCREASE_JZ"),
                Str(best, "PREDICT_CONTENT"), reason, period));
        }

        TryWriteCache(cacheFile, rows, IsHistoricalPeriod(period) ? TimeSpan.MaxValue : TimeSpan.FromHours(24));
        return rows;
    }

    /// <summary>十大流通股东（按持有人分类用）。失败返回空表。</summary>
    public async Task<List<HolderRow>> GetHoldersAsync(string code, CancellationToken ct = default)
    {
        var prefix = code.StartsWith("920") ? "BJ"
            : code.Length == 6 && code[0] is '0' or '2' or '3' ? "SZ"
            : "SH";
        var filter = Uri.EscapeDataString($"(SECUCODE=\"{code}.{prefix}\")");
        var url = $"https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPT_F10_EH_FREEHOLDERS&columns=HOLDER_NAME,FREE_HOLDNUM_RATIO&filter={filter}&pageNumber=1&pageSize=10&sortColumns=UPDATE_DATE,HOLDER_RANK&sortTypes=-1,1&source=WEB&client=WEB";
        try
        {
            var items = await FetchOnePageAsync(url, ct);
            return items.Select(i => new HolderRow(Str(i, "HOLDER_NAME"), Num(i, "FREE_HOLDNUM_RATIO"))).ToList();
        }
        catch { return new List<HolderRow>(); }
    }

    /// <summary>个股资产负债表（RPT_F10_FINANCE_GBALANCE，逐票，按报告期降序返回多期）。
    /// 仅取订单积压指标所需科目：合同负债/存货/应收账款 期末值 + 同比。
    /// 失败或无数据返回空表。历史报告期永久缓存、最新期 24h（同 yjbb 缓存策略）。</summary>
    public async Task<List<BalanceSheetRow>> GetBalanceSheetAsync(string code,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(_cacheDir, $"zcfz_{code}.json");
        if (TryReadCache(cacheFile, out List<BalanceSheetRow>? cached))
        {
            log?.Invoke($"[EM] zcfz {code} 命中缓存 {cached!.Count} 期");
            return cached;
        }

        var prefix = code.StartsWith("920") ? "BJ"
            : code.Length == 6 && code[0] is '0' or '2' or '3' ? "SZ"
            : "SH";
        var filter = Uri.EscapeDataString($"(SECUCODE=\"{code}.{prefix}\")");
        // 注意：HSF10 接口域名是 datacenter.eastmoney.com（非 datacenter-web），
        // source=HSF10 client=PC，与 GetHoldersAsync 的 WEB 源不同——实测此接口只在 HSF10 源可用。
        var url = $"https://datacenter.eastmoney.com/securities/api/data/v1/get"
            + $"?reportName=RPT_F10_FINANCE_GBALANCE&columns=REPORT_DATE,CONTRACT_LIAB,CONTRACT_LIAB_YOY,INVENTORY,INVENTORY_YOY,ACCOUNTS_RECE,ACCOUNTS_RECE_YOY"
            + $"&filter={filter}&pageNumber=1&pageSize=100&sortColumns=REPORT_DATE&sortTypes=-1&source=HSF10&client=PC";
        try
        {
            var items = await FetchOnePageAsync(url, ct);
            var rows = items.Select(i => new BalanceSheetRow(
                code,
                ParseDate(Str(i, "REPORT_DATE")),
                Num(i, "CONTRACT_LIAB"), Num(i, "CONTRACT_LIAB_YOY"),
                Num(i, "INVENTORY"), Num(i, "INVENTORY_YOY"),
                Num(i, "ACCOUNTS_RECE"), Num(i, "ACCOUNTS_RECE_YOY"))).ToList();
            // 历史期数据不会变 → 永久缓存；最新期（当年）靠 24h TTL 失效。
            // 缓存 TTL 按 code 无 reportDate 维度，取保守 24h（含最新期）。
            TryWriteCache(cacheFile, rows, TimeSpan.FromHours(24));
            return rows;
        }
        catch (Exception e)
        {
            log?.Invoke($"[EM] 资产负债表({code}) 拉取失败: {e.Message}");
            return new List<BalanceSheetRow>();
        }
    }

    /// <summary>个股主要财务指标（RPT_F10_FINANCE_MAINFINADATA，逐票，按报告期降序返回多期）。
    /// 供 B 桶填充巴菲特式"缺少"指标：ROIC/资产负债率/利息保障/BPS/净现比等。
    /// 失败或无数据返回空表。最新期 24h 缓存（同 zcfz 策略）。</summary>
    public async Task<List<MainFinRow>> GetMainFinAsync(string code,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(_cacheDir, $"mainfin_{code}.json");
        if (TryReadCache(cacheFile, out List<MainFinRow>? cached))
        {
            log?.Invoke($"[EM] mainfin {code} 命中缓存 {cached!.Count} 期");
            return cached;
        }

        var prefix = code.StartsWith("920") ? "BJ"
            : code.Length == 6 && code[0] is '0' or '2' or '3' ? "SZ"
            : "SH";
        var filter = Uri.EscapeDataString($"(SECUCODE=\"{code}.{prefix}\")");
        var url = $"https://datacenter.eastmoney.com/securities/api/data/v1/get"
            + $"?reportName=RPT_F10_FINANCE_MAINFINADATA&columns=REPORT_DATE,ROEJQ,ROIC,ZCFZL,INTEREST_COVERAGE_RATIO,BPS,MGJYXJJE,NCO_NETPROFIT,XSMLL"
            + $"&filter={filter}&pageNumber=1&pageSize=50&sortColumns=REPORT_DATE&sortTypes=-1&source=HSF10&client=PC";
        try
        {
            var items = await FetchOnePageAsync(url, ct);
            var rows = items.Select(i => new MainFinRow(
                code,
                ParseDate(Str(i, "REPORT_DATE")),
                Num(i, "ROEJQ"),
                Num(i, "ROIC"),
                Num(i, "ZCFZL"),
                Num(i, "INTEREST_COVERAGE_RATIO"),
                Num(i, "BPS"),
                Num(i, "MGJYXJJE"),
                Num(i, "NCO_NETPROFIT"),
                Num(i, "XSMLL"))).ToList();
            TryWriteCache(cacheFile, rows, TimeSpan.FromHours(24));
            return rows;
        }
        catch (Exception e)
        {
            log?.Invoke($"[EM] 主要财务指标({code}) 拉取失败: {e.Message}");
            return new List<MainFinRow>();
        }
    }

    /// <summary>个股现金流量表（RPT_F10_FINANCE_GCASHFLOW，逐票，按报告期降序返回多期）。
    /// 供 B 桶计算 FCF 三项：FCF利润率/Capex强度/所有者收益。
    /// 失败或无数据返回空表。最新期 24h 缓存（同 zcfz 策略）。</summary>
    public async Task<List<CashFlowRow>> GetCashFlowAsync(string code,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(_cacheDir, $"cashflow_{code}.json");
        if (TryReadCache(cacheFile, out List<CashFlowRow>? cached))
        {
            log?.Invoke($"[EM] cashflow {code} 命中缓存 {cached!.Count} 期");
            return cached;
        }

        var prefix = code.StartsWith("920") ? "BJ"
            : code.Length == 6 && code[0] is '0' or '2' or '3' ? "SZ"
            : "SH";
        var filter = Uri.EscapeDataString($"(SECUCODE=\"{code}.{prefix}\")");
        var url = $"https://datacenter.eastmoney.com/securities/api/data/v1/get"
            + $"?reportName=RPT_F10_FINANCE_GCASHFLOW&columns=REPORT_DATE,NETCASH_OPERATE,CONSTRUCT_LONG_ASSET,FA_IR_DEPR,IA_AMORTIZE,DEFER_INCOME_AMORTIZE"
            + $"&filter={filter}&pageNumber=1&pageSize=50&sortColumns=REPORT_DATE&sortTypes=-1&source=HSF10&client=PC";
        try
        {
            var items = await FetchOnePageAsync(url, ct);
            var rows = items.Select(i => new CashFlowRow(
                code,
                ParseDate(Str(i, "REPORT_DATE")),
                Num(i, "NETCASH_OPERATE"),
                Num(i, "CONSTRUCT_LONG_ASSET"),
                Num(i, "FA_IR_DEPR"),
                Num(i, "IA_AMORTIZE"),
                Num(i, "DEFER_INCOME_AMORTIZE"))).ToList();
            TryWriteCache(cacheFile, rows, TimeSpan.FromHours(24));
            return rows;
        }
        catch (Exception e)
        {
            log?.Invoke($"[EM] 现金流量表({code}) 拉取失败: {e.Message}");
            return new List<CashFlowRow>();
        }
    }

    private static DateTime? ParseDate(string s)
    {
        if (s.Length >= 10 && DateTime.TryParseExact(s[..10], "yyyy-MM-dd", Inv,
                DateTimeStyles.None, out var d)) return d;
        return null;
    }

    /// <summary>个股历史现金分红（每10股税前股利 → 每股 DPS；按除权日升序）。
    /// 配置了同花顺 API Key 时优先走扶摇除复权事件流（dividend_per_share 每股税前，同口径）。</summary>
    public async Task<List<DividendRow>> GetDividendsAsync(string code,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(_cacheDir, $"div_{code}.json");
        if (TryReadCache(cacheFile, out List<DividendRow>? cached)) return cached!;

        var rows = new List<DividendRow>();
        // 主源：同花顺（扶摇）除复权事件流；未配置/失败降级东财 RPT_SHAREBONUS_DET
        if (_ths is { IsConfigured: true })
        {
            try { rows = await _ths.GetDividendsAsync(code, ct); }
            catch (Exception e) { log?.Invoke($"[THS] 分红({code}) 拉取失败: {e.Message}，降级东财"); }
        }
        if (rows.Count == 0)
        {
            var filter = Uri.EscapeDataString($"(SECURITY_CODE=\"{code}\")");
            var url = $"https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPT_SHAREBONUS_DET&columns=ALL&filter={filter}&pageNumber=1&pageSize=200&sortColumns=PLAN_NOTICE_DATE,EX_DIVIDEND_DATE&sortTypes=-1,-1&source=WEB&client=WEB";
            try
            {
                var items = await FetchOnePageAsync(url, ct);
                foreach (var i in items)
                {
                    var bonus = Num(i, "PRETAX_BONUS_RMB"); // 每 10 股税前股利（元）
                    var ex = Str(i, "EX_DIVIDEND_DATE");
                    if (bonus is null || bonus <= 0 || ex.Length < 10) continue;
                    if (!DateTime.TryParseExact(ex[..10], "yyyy-MM-dd", Inv,
                            DateTimeStyles.None, out var d)) continue;
                    rows.Add(new DividendRow(d, bonus.Value / 10.0));
                }
            }
            catch (Exception e) { log?.Invoke($"[EM] 分红({code}) 拉取失败: {e.Message}"); }
        }

        rows.Sort((a, b) => a.ExDate.CompareTo(b.ExDate));
        TryWriteCache(cacheFile, rows, TimeSpan.FromHours(24)); // 分红记录基本不变
        return rows;
    }

    /// <summary>最新中国 10 年期国债到期收益率（%，RPTA_WEB_TREASURYYIELD）。</summary>
    public async Task<double?> GetCn10yYieldAsync(CancellationToken ct = default)
    {
        try
        {
            const string url = "https://datacenter.eastmoney.com/api/data/get?type=RPTA_WEB_TREASURYYIELD&sty=ALL&st=SOLAR_DATE&sr=-1&token=894050c76af8597a853f5b408b759f5d&p=1&ps=10&pageNo=1&pageNum=1";
            using var doc = await GetJsonAsync(url, ct);
            var data = doc.RootElement.GetProperty("result").GetProperty("data");
            foreach (var item in data.EnumerateArray())
                if (Num(item, "EMM00166466") is { } v) // 中国国债收益率10年
                    return v;
        }
        catch { }
        return null;
    }

    /// <summary>报告期 → ROE 年化系数：Q1×4 / 中报×2 / 三季报×4/3 / 年报×1。</summary>
    public static double RoeAnnualizeFactor(string period) => period[^4..] switch
    {
        "0331" => 4.0,
        "0630" => 2.0,
        "0930" => 4.0 / 3.0,
        _ => 1.0,
    };

    /// <summary>按披露节奏推断最新完整披露报告期（与 Python _report_period_candidates 首选一致）。</summary>
    public static string LatestReportPeriod(DateTime today)
    {
        var (y, m) = (today.Year, today.Month);
        return m >= 11 ? $"{y}0930"
            : m >= 9 ? $"{y}0630"
            : m >= 5 ? $"{y}0331"
            : $"{y - 1}1231";
    }

    /// <summary>最近 n 个已完整披露年报期（旧→新，年报 4-30 前披露完）。
    /// 例：今天 2026-08，last=2025，n=4 → [20221231, 20231231, 20241231, 20251231]。</summary>
    public static List<string> LatestAnnualPeriods(int nYears)
    {
        var today = TradingCalendar.NowCn();
        var last = today.Month >= 5 ? today.Year - 1 : today.Year - 2;
        // i 从 nYears-1 递减到 0：last-(n-1) … last，得到旧→新序列
        return Enumerable.Range(0, nYears).Select(i => $"{last - (nYears - 1) + i}1231").ToList();
    }

    /// <summary>最新已完整披露报告期起往前 numQuarters 个季度（旧→新，每季一期的报告期）。</summary>
    public static List<string> RecentQuarters(int numQuarters)
    {
        var today = TradingCalendar.NowCn();
        var (y, m) = (today.Year, today.Month);
        var q = m >= 11 ? 3 : m >= 9 ? 2 : m >= 5 ? 1 : 4;
        if (m < 5) y -= 1;
        var suffix = new[] { "0331", "0630", "0930", "1231" };
        var list = new List<string> { $"{y}{suffix[q - 1]}" };
        for (var i = 0; i < numQuarters; i++)
        {
            q--;
            if (q == 0) { q = 4; y--; }
            list.Add($"{y}{suffix[q - 1]}");
        }
        return list; // [最新期, 前1期, ...]
    }

    // ── 分页抓取与解析辅助 ─────────────────────────────────────────

    private async Task<List<T>> FetchPagedAsync<T>(string urlTemplate, Func<JsonElement, T> map,
        Action<string>? log, CancellationToken ct)
    {
        var raw = await FetchPagedRawAsync(urlTemplate, log, ct);
        return raw.Select(map).ToList();
    }

    private async Task<List<JsonElement>> FetchPagedRawAsync(string urlTemplate,
        Action<string>? log, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var page = 1;
        while (true)
        {
            using var doc = await GetJsonAsync(string.Format(Inv, urlTemplate, page), ct);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object) break;

            var pages = result.TryGetProperty("pages", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : 1;
            if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray()) all.Add(item.Clone());

            if (page >= pages) break;
            page++;
            await Task.Delay(200, ct); // 分页间轻休眠防限频
        }
        log?.Invoke($"[EM] 分页抓取完成 {all.Count} 行（{page} 页）");
        return all;
    }

    private async Task<List<JsonElement>> FetchOnePageAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);
        if (!doc.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object) return new();
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new();
        return data.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await Client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private static string Str(JsonElement o, string prop)
        => o.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? "" : "";

    /// <summary>数值字段解析：0 视为缺失（与 Python 端 _num 口径一致）。</summary>
    private static double? Num(JsonElement o, string prop)
    {
        if (!o.TryGetProperty(prop, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble() is var d && d != 0 ? d : null,
            JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Any, Inv, out var v)
                => v != 0 ? v : null,
            _ => null,
        };
    }

    // ── 磁盘缓存（JSON 文件；data/cache/ 下与 Python parquet 缓存同目录） ──

    private static bool IsHistoricalPeriod(string period)
    {
        // 当年报告期视为"进行中"，往年数据已冻结
        var today = TradingCalendar.NowCn();
        return int.TryParse(period.AsSpan(0, 4), out var y) && y < today.Year;
    }

    private bool TryReadCache<T>(string path, out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(path)) return false;
            var ttl = IsHistoricalPeriod(Path.GetFileName(path).Split('_')[^1].Split('.')[0])
                ? TimeSpan.MaxValue : TimeSpan.FromHours(24);
            if (DateTime.Now - File.GetLastWriteTime(path) > ttl) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value is not null;
        }
        catch { return false; }
    }

    private void TryWriteCache<T>(string path, T value, TimeSpan ttl)
    {
        if (ttl == TimeSpan.Zero) return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(value, _jsonOpts));
        }
        catch { /* 缓存失败不影响主流程 */ }
    }
}
