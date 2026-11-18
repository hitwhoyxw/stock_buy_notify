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
    double? GrossMargin, // 销售毛利率 %
    DateTime? NoticeDate); // NOTICE_DATE 公告日（T4 时效判定用；旧缓存无此字段时为 null）

/// <summary>东财业绩预告行（RPT_PUBLIC_OP_NEWPREDICT，替代 akshare stock_yjyg_em；每股一行）。</summary>
public sealed record YjygRow(
    string Code, string Name, string PreviewType,
    double? GainPct,   // 预告变动幅度（INCREASE_JZ，利润类指标取最大）
    string Excerpt,    // 业绩变动（PREDICT_CONTENT）
    string Reason,     // 变动原因（CHANGE_REASON_EXPLAIN）
    string Period,
    DateTime? NoticeDate); // NOTICE_DATE 公告日（T4 时效判定用；旧缓存无此字段时为 null）

/// <summary>
/// 投资者关系活动记录表/机构调研行（RPT_ORG_SURVEY）——覆盖电话会议、业绩说明会、特定对象调研。
/// 注意：**一次活动会拆成多行**（每个接待对象一行，同一 URL 共享同一份 CONTENT），
/// 消费前必须按 (Code, Url) 分组去重，否则同一份正文会被重复计入关键词命中。
/// </summary>
public sealed record OrgSurveyRow(
    string Code, string Name,
    DateTime? NoticeDate,   // NOTICE_DATE 公告日
    DateTime? ReceiveDate,  // RECEIVE_START_DATE 活动发生日（时效判定优先用它）
    string Way,             // RECEIVE_WAY_EXPLAIN：业绩说明会/特定对象调研/网络互动/网络文字互动/电话会议
    string Object,          // RECEIVE_OBJECT 接待对象
    string Content,         // CONTENT 正文（~1.4k 字，非空）
    string Url);            // URL 同一次活动多行共享，用于分组去重

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
public sealed record DividendRow(DateTime ExDate, double Dps, double Transfer = 0);

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
        using var ownFinancialScope = FinancialAccess.Current is null ? new FinancialAccess{AllowFinancialNetwork=true,CycleMonths=FinancialAccess.ConfiguredCycle(_cacheDir)} : null;
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
                Num(item, "XSMLL"),
                ParseDate(Str(item, "NOTICE_DATE"))),
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
        // 注意：此接口 filter 前必须保留一个空格（东财网关约定），域名与 yjbb 不同。
        // columns 只列实际读取的字段（原为 ALL）：ALL 会带回 30+ 字段 × 23 页，
        // 既拖慢解析也更快撞上东财的「服务器繁忙」(code 9701) 限流。
        var filter = Uri.EscapeDataString($" (REPORT_DATE='{date}')");
        var raw = await FetchPagedRawAsync(
            $"https://datacenter.eastmoney.com/securities/api/data/v1/get?sortColumns=NOTICE_DATE,SECURITY_CODE&sortTypes=-1,-1&pageSize=500&pageNumber={{0}}&reportName=RPT_PUBLIC_OP_NEWPREDICT&columns=SECURITY_CODE,SECURITY_NAME_ABBR,NOTICE_DATE,PREDICT_TYPE,PREDICT_FINANCE,INCREASE_JZ,PREDICT_CONTENT,CHANGE_REASON_EXPLAIN&filter={filter}",
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
                Str(best, "PREDICT_CONTENT"), reason, period,
                // 公告日取该股所有指标行的最新 NOTICE_DATE（同一次预告多行指标，日期相同）
                items.Select(i => ParseDate(Str(i, "NOTICE_DATE")))
                    .Where(d => d is not null).Max()));
        }

        TryWriteCache(cacheFile, rows, IsHistoricalPeriod(period) ? TimeSpan.MaxValue : TimeSpan.FromHours(24));
        return rows;
    }

    /// <summary>
    /// 投资者关系活动记录表/机构调研（RPT_ORG_SURVEY）——C 桶文本扫描的补充材料源，
    /// 覆盖**电话会议 / 业绩说明会 / 特定对象调研 / 网络互动**（见 RECEIVE_WAY_EXPLAIN）。
    ///
    /// 设计要点：
    /// - 全市场单月即数万行（一次活动拆多行），故**必须**用 `SECURITY_CODE in (...)` 收窄到扫描池；
    ///   按 50 只一批分块请求。若 in-filter 整体失效（接口变更），退化为逐票单请求且本次上限
    ///   <paramref name="maxCodesOnFallback"/> 只，保证不会卡死。
    /// - 同一次活动多行共享同一 URL → 按 (Code, Url) 分组去重，Way 合并、日期取最新、Content 只留一份。
    /// - 该接口在高频调用下会返回「服务器繁忙」→ **任何异常都吞掉并返回空**，活动记录是增强源，
    ///   绝不能拖垮 T4 主流程（失败时降级为仅用业绩预告/报表文本）。
    /// </summary>
    /// <param name="codes">扫描池股票代码（6 位）。</param>
    /// <param name="lookbackDays">取数窗口（天），只拉 RECEIVE_START_DATE 在窗口内的记录。</param>
    /// <param name="maxCodesOnFallback">in-filter 失效时的逐票降级上限。</param>
    public async Task<List<OrgSurveyRow>> GetOrgSurveysAsync(
        IReadOnlyList<string> codes, int lookbackDays = 180, int maxCodesOnFallback = 30,
        Action<string>? log = null, CancellationToken ct = default,
        TimeSpan? budget = null)
    {
        var pool = codes.Where(c => c.Length == 6).Distinct(StringComparer.Ordinal).ToList();
        if (pool.Count == 0) return new List<OrgSurveyRow>();
        var budgetEff = budget ?? OrgSurveyDefaultBudget;

        var startDate = TradingCalendar.NowCn().AddDays(-Math.Max(1, lookbackDays));
        // 注意：这里**不再**用「按日期+池指纹」的聚合缓存。
        // 那个 design 有两个坑：(1) 日期每天平移 → 缓存永不命中；(2) 文件名里的 8 位日期会被
        // PeriodFromCacheName 当成报告期，往年日期 → TTL=MaxValue → **永久缓存**，于是过期的
        // 空结果会一直返回，把增量累积彻底堵死。改为只用逐票缓存（见下），覆盖度自然跨轮累积。

        var rows = new List<OrgSurveyRow>();
        var blocked = false;    // 连续失败过多，判定接口不可用
        var noRecord = 0;       // 「返回数据为空」的票数——正常情况，不是故障
        var served = 0;         // 命中持久库的票数
        var fetched = 0;        // 实拉到新数据的票数
        var failed = 0;         // 尝试后仍失败的票数（含重试）
        var consecutiveFail = 0;// 连续失败计数（成功后清零）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var store = LoadOrgSurveyStore();
        var storeDirty = false;
        try
        {
            // ── 实测根因（2026-09-20）：本接口在 yjyg 全市场快照之后调用会持续 9701「服务器繁忙」──
            //  · 单次独立调用：270~610ms，稳定成功（含共享 HttpClient + Referer，与 PowerShell 结果一致）；
            //  · 紧跟 GetYjygSnapshotAsync（10 页突发）之后：每个请求耗时 ~28s 后返回 9701，
            //    002462(14s)→300308(42s)→000807(59s) 逐个失败，约 1 分钟后自行恢复。
            //  即东财服务端在快照高负载后短期内无法响应本接口，并非参数/UA/IP 问题
            //  （同 IP 同参数在冷却后立即成功）。
            // 对策：静默期 + 低速节流 + 失败长歇自愈；并用可提交的持久库让覆盖度跨运行累积。
            log?.Invoke($"[EM] 活动记录：静默 {OrgSurveySettleDelay.TotalSeconds:0}s 后开始"
                + $"（持久库已有 {store.Entries.Count} 只；待查 {pool.Count} 只；"
                + $"预算 {budgetEff.TotalMinutes:0} 分钟）");
            await Task.Delay(OrgSurveySettleDelay, ct);

            foreach (var code in pool)
            {
                ct.ThrowIfCancellationRequested();

                // 墙钟预算：到期停手，已取到的照常用（持久库保证下轮从断点续跑）
                if (sw.Elapsed >= budgetEff)
                {
                    log?.Invoke($"[EM] 活动记录已达时间预算 {budgetEff.TotalMinutes:0} 分钟，"
                        + $"提前结束（本轮覆盖 {served + fetched + noRecord + failed}/{pool.Count} 只，"
                        + $"持久库累计 {store.Entries.Count} 只；未覆盖的下轮续跑）");
                    break;
                }

                // 持久库命中（TTL 内）→ 直接用，零请求
                if (store.Entries.TryGetValue(code, out var hit)
                    && DateTime.Now - hit.FetchedAt < OrgSurveyCacheTtl)
                {
                    var fresh = hit.Rows.Where(r =>
                        (r.ReceiveDate ?? r.NoticeDate) is not { } d || d.Date >= startDate.Date).ToList();
                    served++;
                    if (fresh.Count > 0) rows.AddRange(fresh);
                    consecutiveFail = 0;
                    continue;
                }

                // 单票最多试 3 次。低速后台跑：退避 5s→15s，用时间换成功率，
                // 避免越失败越急着重试反而加深限流。
                var (got, wasBlocked, wasEmpty) = await FetchOrgSurveyAsync(code, startDate, log, ct);
                for (var attempt = 1; wasBlocked && attempt <= 2 && !ct.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(attempt == 1 ? OrgSurveyRetryDelay1 : OrgSurveyRetryDelay2, ct);
                    (got, wasBlocked, wasEmpty) = await FetchOrgSurveyAsync(code, startDate, null, ct);
                }
                if (wasBlocked)
                {
                    // 仍失败：记一笔继续下一只。连续 3 票都失败说明进入限流期，
                    // 长歇等待自愈后**继续**，而不是放弃整轮。
                    failed++;
                    if (++consecutiveFail >= OrgSurveyAbortAfterConsecutiveFailures)
                    {
                        log?.Invoke($"[EM] 活动记录连续 {consecutiveFail} 票失败，长歇 "
                            + $"{OrgSurveyCooldown.TotalMinutes:0} 分钟等待自愈后继续…");
                        await Task.Delay(OrgSurveyCooldown, ct);
                        consecutiveFail = 0;
                    }
                    continue;
                }
                consecutiveFail = 0;
                if (wasEmpty) noRecord++;
                else rows.AddRange(got);
                // 落持久库（含空结果——空也是有效结论，可避免下轮重试同一只）
                store.Entries[code] = new OrgSurveyEntry(got, DateTime.Now);
                storeDirty = true;
                fetched++;
                await Task.Delay(OrgSurveyPerStockDelay, ct);   // 低速节流
            }
        }
        catch (OperationCanceledException) { /* 取消也要落库，保住已取到的部分 */ }
        catch (Exception e)
        {
            log?.Invoke($"[EM] 活动记录拉取失败: {e.Message}，本次仅用业绩预告/报表文本（不影响主流程）");
        }

        // 落库：无论正常结束/预算到期/异常/取消，已取到的都要保住（这是跨运行累积的关键）
        if (storeDirty)
        {
            SaveOrgSurveyStore(store);
            log?.Invoke($"[EM] 活动记录已写入持久库 {OrgSurveyStorePath}"
                + $"（本次新增/更新 {fetched} 只，库内共 {store.Entries.Count} 只）");
        }

        var grouped = GroupOrgSurveys(rows);
        log?.Invoke($"[EM] 活动记录 {grouped.Count} 场（原始 {rows.Count} 行，已按 URL 去重；"
            + $"单票缓存命中 {served}/{pool.Count}，无记录 {noRecord} 只，失败 {failed} 只"
            + $"，用时 {sw.Elapsed.TotalSeconds:0}s" + "）");
        return grouped;
    }

    /// <summary>
    /// 单票活动记录请求。返回 (行, 接口异常, 该票无记录)。
    /// 「返回数据为空」是**正常结果**（公司当期没开调研/说明会），必须与接口故障区分开，
    /// 否则会把正常空结果当成限流而中断整轮。
    /// </summary>
    /// <remarks>
    /// 实测性能记录（2026-09-20，用于说明为何是"每票最近 1 场 + 正文内联截断"）：
    ///   · 单次请求延迟仅 270~610ms，慢的不是网络；
    ///   · 但个别票（如 000301）返回 **238KB**（窗口内几十场活动 × 每场 ~5KB 正文），
    ///     这类大响应会把接口推回「服务器繁忙」，之后每票都要退避+重试 → 整体从 ~1只/分钟
    ///     塌到 1 只/数分钟，300 只池子跑不完。
    ///   · 故 pageSize 降到 1（只要最近一场）、pageNum=1，把单票 payload 压到最小。
    /// </remarks>
    // ── 活动记录抓取节奏参数（2026-09-20 定档：低速后台跑，宁可慢也不触发限流）──

    /// <summary>每票只取最近 1 场（越小越稳；最近一场的信息量也最高）。</summary>
    private const int OrgSurveyPerStock = 1;

    /// <summary>连续多少票失败即判定进入限流期，转入长歇等待自愈。</summary>
    private const int OrgSurveyAbortAfterConsecutiveFailures = 3;

    /// <summary>
    /// 快照后静默期：紧跟 yjyg 全市场快照之后调用本接口易遭 9701，先等一会儿再开始。
    /// </summary>
    private static readonly TimeSpan OrgSurveySettleDelay = TimeSpan.FromSeconds(45);

    /// <summary>连续失败后的长歇（等东财自愈），随后继续而非放弃。低速后台跑，歇得久一点。</summary>
    private static readonly TimeSpan OrgSurveyCooldown = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 活动记录抓取的默认墙钟预算（分钟）。实际值由调用方从 yaml
    /// `freshness.activity_budget_minutes` 传入；全量探测约 950 只 × 3.4s ≈ 55 分钟。
    /// 到期即停手，已取到的写入持久库（下轮从断点续跑）。
    /// </summary>
    private static readonly TimeSpan OrgSurveyDefaultBudget = TimeSpan.FromMinutes(60);

    /// <summary>每票之间的节流间隔：低速后台跑，放长到 3s（300 只约 15 分钟纯等待）。</summary>
    private static readonly TimeSpan OrgSurveyPerStockDelay = TimeSpan.FromSeconds(3);

    /// <summary>单票失败重试的两级退避（再慢一点，用时间换成功率）。</summary>
    private static readonly TimeSpan OrgSurveyRetryDelay1 = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OrgSurveyRetryDelay2 = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 单票活动记录缓存 TTL（7 天）。T4 每周跑一次，故每周至多重拉一遍；
    /// 配合「每轮只补未覆盖票」使 300 只多在首轮后即全量命中缓存（几乎零请求）。
    /// </summary>
    private static readonly TimeSpan OrgSurveyCacheTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// 活动记录的**跨运行持久库**（data/org_survey_cache.json，**可提交**）。
    ///
    /// 为什么不用 data/cache/：该目录被 .gitignore 忽略，而 CI（GitHub Actions）每次
    /// 都是全新 runner——缓存带不过去，导致每轮都冷启动重拉 300 只（实测覆盖停在 2/300）。
    /// 放在 data/ 下并纳入工作流 commit，覆盖度才能跨运行累积：首轮慢，之后每周只需补新增。
    /// </summary>
    private sealed record OrgSurveyStore(Dictionary<string, OrgSurveyEntry> Entries);

    private sealed record OrgSurveyEntry(List<OrgSurveyRow> Rows, DateTime FetchedAt);

    private string OrgSurveyStorePath => Path.Combine(
        Path.GetDirectoryName(_cacheDir) ?? _cacheDir, "org_survey_cache.json");

    /// <summary>读持久库；文件不存在/损坏返回空库（不影响主流程）。</summary>
    private OrgSurveyStore LoadOrgSurveyStore()
    {
        try
        {
            if (!File.Exists(OrgSurveyStorePath)) return new OrgSurveyStore(new());
            var s = JsonSerializer.Deserialize<OrgSurveyStore>(File.ReadAllText(OrgSurveyStorePath));
            return s ?? new OrgSurveyStore(new());
        }
        catch { return new OrgSurveyStore(new()); }
    }

    /// <summary>写持久库；失败不抛（缓存失败不该拖垮 T4）。</summary>
    private void SaveOrgSurveyStore(OrgSurveyStore store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OrgSurveyStorePath)!);
            File.WriteAllText(OrgSurveyStorePath,
                JsonSerializer.Serialize(store, _jsonOpts));
        }
        catch { /* 落库失败不影响本次结果 */ }
    }

    /// <summary>
    /// 单票活动记录请求。返回 (行, 接口异常, 该票无记录)。
    /// 「返回数据为空」是**正常结果**（公司当期没开调研/说明会），必须与接口故障区分开，
    /// 否则会把正常空结果当成限流而中断整轮。
    /// </summary>
    private async Task<(List<OrgSurveyRow> Rows, bool Blocked, bool Empty)> FetchOrgSurveyAsync(
        string code, DateTime startDate, Action<string>? log, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString(
            $"(SECURITY_CODE in (\"{code}\"))(RECEIVE_START_DATE>='{startDate:yyyy-MM-dd}')");
        // ⚠️ pageSize 必须 ≤50：实测本接口 pageSize=100/200/500 一律返回「服务器繁忙」
        // （同域名的 yjbb/yjyg 接口 pageSize=500 正常）——不是封 IP，是本接口的页大小上限。
        // 症状有迷惑性：报错文案像限流，实际是参数过大。这里只取最近几场，进一步降低 payload。
        var url = "https://datacenter-web.eastmoney.com/api/data/v1/get"
            + "?reportName=RPT_ORG_SURVEY"
            + "&columns=SECURITY_CODE,SECURITY_NAME_ABBR,NOTICE_DATE,RECEIVE_START_DATE,"
            + "RECEIVE_WAY_EXPLAIN,RECEIVE_OBJECT,CONTENT,URL"
            + $"&filter={filter}&pageSize={OrgSurveyPerStock}&pageNumber=1"
            + "&sortColumns=RECEIVE_START_DATE,SECURITY_CODE&sortTypes=-1,-1&source=WEB&client=WEB";
        try
        {
            // ⚠️ 必须用 FetchOnePageAsync（只取第 1 页），**不能**用 FetchPagedRawAsync：
            // 后者会 while 循环翻完 result.pages 全部页。本接口的 pages 是按全表算的
            // （002463 报 294 页、300308 报 838 页），于是"只想取最近 1 条"变成了
            // 294~838 次请求——单票就足以触发 9701，且失败表现为"该票服务器繁忙"，
            // 极易误判成限流/IP 问题（实测排查了很久）。按 RECEIVE_START_DATE 降序后
            // 第 1 页就是最近一场，取 1 页足够。
            var raw = await FetchOnePageAsync(url, ct);
            return (raw.Select(ParseOrgSurvey).Where(r => r is not null).Select(r => r!).ToList(), false, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (OrgSurveyEmptyException) { return (new List<OrgSurveyRow>(), false, true); }
        catch (Exception e)
        {
            // 东财错误表现：HTTP 200 + success:false + message「服务器繁忙」/「返回数据为空」。
            // 只有前者是故障；后者已在 FetchPagedRawAsync 转成 OrgSurveyEmptyException。
            log?.Invoke($"[EM] 活动记录({code}) 接口异常: {e.Message}");
            return (new List<OrgSurveyRow>(), true, false);
        }
    }

    /// <summary>「返回数据为空」——正常结果（该票无活动记录），非故障。</summary>
    private sealed class OrgSurveyEmptyException : Exception
    {
        public OrgSurveyEmptyException(string msg) : base(msg) { }
    }

    /// <summary>单块（≤50 只）活动记录请求。返回 (行, 是否限流/接口错误)。</summary>
    private async Task<(List<OrgSurveyRow> Rows, bool Blocked)> FetchOrgSurveyChunkAsync(
        List<string> codes, DateTime startDate, Action<string>? log, CancellationToken ct)
    {
        var inList = string.Join(",", codes.Select(c => $"\"{c}\""));
        var filter = Uri.EscapeDataString(
            $"(SECURITY_CODE in ({inList}))(RECEIVE_START_DATE>='{startDate:yyyy-MM-dd}')");
        // ⚠️ pageSize 必须 ≤50：实测本接口 pageSize=100/200/500 一律返回「服务器繁忙」
        // （而同域名的 yjbb/yjyg 接口 pageSize=500 正常）——不是封 IP，是本接口的页大小上限。
        // 症状具有迷惑性：报错文案像限流，实际是参数过大；换小页即可。
        // columns 只列需要的 8 个字段：ALL 会带回 30+ 字段（含长文本副本），纯属 payload 膨胀。
        var url = "https://datacenter-web.eastmoney.com/api/data/v1/get"
            + "?reportName=RPT_ORG_SURVEY"
            + "&columns=SECURITY_CODE,SECURITY_NAME_ABBR,NOTICE_DATE,RECEIVE_START_DATE,"
            + "RECEIVE_WAY_EXPLAIN,RECEIVE_OBJECT,CONTENT,URL"
            + $"&filter={filter}&pageSize=50&pageNumber={{0}}"
            + "&sortColumns=RECEIVE_START_DATE,SECURITY_CODE&sortTypes=-1,-1&source=WEB&client=WEB";
        try
        {
            var raw = await FetchPagedRawAsync(url, log, ct, throwOnApiError: true);
            return (raw.Select(ParseOrgSurvey).Where(r => r is not null).Select(r => r!).ToList(), false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // 东财限流表现为 success:false + message「服务器繁忙」(code 9701)，HTTP 状态仍是 200。
            // 这里返回 Blocked=true 让调用方**区别于「真的没数据」**：限流时不可再逐票加压。
            log?.Invoke($"[EM] 活动记录分块失败: {e.Message}");
            return (new List<OrgSurveyRow>(), true);
        }
    }

    /// <summary>解析单行活动记录；无代码、且无正文无日期的行丢弃。</summary>
    private static OrgSurveyRow? ParseOrgSurvey(JsonElement item)
    {
        var code = Str(item, "SECURITY_CODE").PadLeft(6, '0');
        if (code.Trim().Length == 0) return null;
        var content = Str(item, "CONTENT");
        var receive = ParseDate(Str(item, "RECEIVE_START_DATE"));
        var notice = ParseDate(Str(item, "NOTICE_DATE"));
        if (content.Length == 0 && receive is null && notice is null) return null;
        return new OrgSurveyRow(code, Str(item, "SECURITY_NAME_ABBR"),
            notice, receive,
            Str(item, "RECEIVE_WAY_EXPLAIN"), Str(item, "RECEIVE_OBJECT"),
            content, Str(item, "URL"));
    }

    /// <summary>
    /// 按 (Code, Url) 分组：一次活动多行 → 一场。
    /// Way 合并去重（"网络互动,业绩说明会" → "业绩说明会、网络互动"），
    /// Content 只留一份（同一 URL 正文相同），日期取该场最新。
    /// public：供 ThreeBucket.Demo 离线回归验证"同一场活动拆多行"这一真实接口特性。
    /// </summary>
    public static List<OrgSurveyRow> GroupOrgSurveys(List<OrgSurveyRow> rows)
    {
        return rows
            .GroupBy(r => (r.Code, Key: r.Url.Length > 0 ? r.Url : $"{r.ReceiveDate:yyyyMMdd}|{r.Way}"),
                StringTupleComparer.Instance)
            .Select(g =>
            {
                var f = g.First();
                var ways = g.SelectMany(r => r.Way.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    .Select(w => w.Trim()).Where(w => w.Length > 0)
                    .Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal);
                var objects = g.Select(r => r.Object).Where(o => o.Length > 0)
                    .Distinct(StringComparer.Ordinal);
                // 正文取最长的一行（同一 URL 本应相同，容错取最完整的一份）
                var content = g.Select(r => r.Content).OrderByDescending(c => c.Length).First();
                return f with
                {
                    Way = string.Join("、", ways),
                    Object = string.Join("、", objects.Take(3)),
                    Content = content,
                    ReceiveDate = g.Select(r => r.ReceiveDate).Where(d => d is not null).Max(),
                    NoticeDate = g.Select(r => r.NoticeDate).Where(d => d is not null).Max(),
                };
            })
            .OrderByDescending(r => r.ReceiveDate ?? r.NoticeDate ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>(string, string) 元组的 Ordinal 比较器（GroupBy 需显式传入才大小写敏感）。</summary>
    private sealed class StringTupleComparer : IEqualityComparer<(string Code, string Key)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string Code, string Key) x, (string Code, string Key) y)
            => string.Equals(x.Code, y.Code, StringComparison.Ordinal)
               && string.Equals(x.Key, y.Key, StringComparison.Ordinal);
        public int GetHashCode((string Code, string Key) o)
            => HashCode.Combine(o.Code.GetHashCode(StringComparison.Ordinal),
                o.Key.GetHashCode(StringComparison.Ordinal));
    }

    /// <summary>池指纹：用于缓存文件名，池变化即换缓存（避免串味）。</summary>
    private static string PoolHash(IReadOnlyList<string> pool)
    {
        var joined = string.Join(",", pool.OrderBy(c => c, StringComparer.Ordinal));
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
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
        using var ownFinancialScope = FinancialAccess.Current is null ? new FinancialAccess{AllowFinancialNetwork=true,CycleMonths=FinancialAccess.ConfiguredCycle(_cacheDir)} : null;
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
        using var ownFinancialScope = FinancialAccess.Current is null ? new FinancialAccess{AllowFinancialNetwork=true,CycleMonths=FinancialAccess.ConfiguredCycle(_cacheDir)} : null;
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
        using var ownFinancialScope = FinancialAccess.Current is null ? new FinancialAccess{AllowFinancialNetwork=true,CycleMonths=FinancialAccess.ConfiguredCycle(_cacheDir)} : null;
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
        using var ownFinancialScope = FinancialAccess.Current is null ? new FinancialAccess{AllowFinancialNetwork=true,CycleMonths=FinancialAccess.ConfiguredCycle(_cacheDir)} : null;
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
                    if (ex.Length < 10) ex = Str(i, "EQUITY_RECORD_DATE");
                    if (ex.Length < 10) ex = Str(i, "IMPLEMENT_DATE");
                    if (ex.Length < 10) continue;
                    if (!DateTime.TryParseExact(ex[..10], "yyyy-MM-dd", Inv,
                            DateTimeStyles.None, out var d)) continue;
                    var transfer10 = Num(i, "TRANSFER_RATIO") ?? Num(i, "IT_RATIO") ?? Num(i, "BONUS_IT_RATIO") ?? 0;
                    var bonusShare10 = Num(i, "BONUS_RATIO") ?? 0;
                    var transfer = (transfer10 + bonusShare10) / 10.0;
                    if ((bonus is null || bonus <= 0) && transfer <= 0) continue;
                    rows.Add(new DividendRow(d, (bonus ?? 0) / 10.0, transfer));
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

    private Task<List<JsonElement>> FetchPagedRawAsync(string urlTemplate,
        Action<string>? log, CancellationToken ct)
        => FetchPagedRawAsync(urlTemplate, log, ct, throwOnApiError: false);

    /// <summary>
    /// 分页抓取。throwOnApiError=false（默认，历史行为）：接口返回 success:false（如限流
    /// 「服务器繁忙」）时按空结果处理，不中断调用方。true：抛出，便于调用方区分
    /// 「真的没数据」与「被限流/接口错误」并据此决定是否降级重试。
    /// </summary>
    private async Task<List<JsonElement>> FetchPagedRawAsync(string urlTemplate,
        Action<string>? log, CancellationToken ct, bool throwOnApiError)
    {
        var all = new List<JsonElement>();
        var page = 1;
        while (true)
        {
            using var doc = await GetJsonAsync(string.Format(Inv, urlTemplate, page), ct);
            // 东财错误表现：HTTP 200 + success:false，两种含义完全不同：
            //   · 「服务器繁忙」= 真故障（参数过大或限流）→ 抛出
            //   · 「返回数据为空」= 正常业务结果（该查询没有记录）→ 按空结果返回
            if (doc.RootElement.TryGetProperty("success", out var okEl)
                && okEl.ValueKind == JsonValueKind.False)
            {
                var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                if (msg.Contains("为空", StringComparison.Ordinal)) break;   // 正常空结果
                if (throwOnApiError)
                {
                    // 带上 URL 形状（长度/段数）与原始响应片段，便于定位是哪个参数触发的
                    var u = string.Format(Inv, urlTemplate, page);
                    var q = u.Length > 120 ? u[..60] + "…" + u[^50..] : u;
                    throw new InvalidOperationException(
                        $"东财接口返回失败: {msg}｜URL({u.Length}字符)={q}"
                        + $"｜响应={doc.RootElement.GetRawText()[..Math.Min(200, doc.RootElement.GetRawText().Length)]}");
                }
                break;
            }
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

    /// <summary>
    /// 只取第 1 页（不翻页）。用于"只要最新 N 条"的场景——配合按时间降序排序，
    /// 第 1 页即最新记录，避免 FetchPagedRawAsync 按 result.pages 翻完全表。
    /// 与 FetchPagedRawAsync 同样区分「服务器繁忙」(抛出手) 与「返回数据为空」(空结果)。
    /// </summary>
    private async Task<List<JsonElement>> FetchOnePageAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);
        if (doc.RootElement.TryGetProperty("success", out var okEl)
            && okEl.ValueKind == JsonValueKind.False)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (!msg.Contains("为空", StringComparison.Ordinal))
                throw new InvalidOperationException($"东财接口返回失败: {msg}");
            return new();
        }
        if (!doc.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object) return new();
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new();
        return data.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        if (FinancialRawStore.IsFinancialUrl(url))
        {
            var canonical=FinancialRawStore.CanonicalUrl(url);
            return await new FinancialRawStore(_cacheDir).GetAsync("eastmoney",FinancialRawStore.Resource(canonical),FinancialRawStore.Security(canonical),canonical,async()=>
            {
                FinancialAccess.Current?.BeforeNetwork("em-resource:"+FinancialRawStore.Resource(canonical),!canonical.Contains("RPT_SHAREBONUS_DET"));
                using var response=await Client.GetAsync(canonical,ct);response.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            },ct);
        }
        FinancialAccess.Current?.BeforeNetwork("em-other");
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
        return period.Length >= 4 && int.TryParse(period.AsSpan(0, 4), out var y) && y < today.Year;
    }

    /// <summary>
    /// 从缓存文件名推断 TTL 基准：取第一个 4 位纯数字段（报告期，如 yjbb_20260630.json → 20260630）。
    /// 不能用「最后一段」——像 mainfin_600519.json / orgsurvey_20260918_ab12.json 那样末段不是
    /// 报告期的文件会误判；取不到数字段时按「当前期」处理（24h TTL）。
    /// </summary>
    private static string PeriodFromCacheName(string path)
    {
        foreach (var seg in Path.GetFileNameWithoutExtension(path).Split('_'))
            if (seg.Length >= 4 && char.IsDigit(seg[0])) return seg;
        return "";
    }

    private bool TryReadCache<T>(string path, out T? value)
    {
        var name=Path.GetFileName(path);
        if(new[]{"yjbb_","mainfin_","cashflow_","zcfz_","div_"}.Any(name.StartsWith))
        {
            try
            {
                var access=FinancialAccess.Current;
                var corporate=name.StartsWith("div_");
                var allow=(access==null||access.AllowFinancialNetwork||(corporate&&access.AllowCorporateActionsNetwork))&&access?.MissingOnly!=true;
                var native=FinancialRawStore.NativeAsOf(_cacheDir,path);
                if(native>DateTime.MinValue && (!File.Exists(path)||native>File.GetLastWriteTime(path))) {value=default;return false;}
                if(File.Exists(path) && (!allow || !(corporate?DateTime.Now>=File.GetLastWriteTime(path).AddDays(1):FinancialAccess.IsDue(File.GetLastWriteTime(path),DateTime.Now,access?.CycleMonths??FinancialAccess.ConfiguredCycle(_cacheDir)))))
                {value=JsonSerializer.Deserialize<T>(File.ReadAllText(path),_jsonOpts);if(value is not null){access?.Hit(path);return true;}}
                if(!allow){access?.Missing(path);value=Activator.CreateInstance<T>();return true;}
            } catch {if(FinancialAccess.Current is {AllowFinancialNetwork:false}){value=Activator.CreateInstance<T>();return true;}}
            value=default;return false;
        }

        value = default;
        try
        {
            if (!File.Exists(path)) return false;
            var ttl = IsHistoricalPeriod(PeriodFromCacheName(path))
                ? TimeSpan.MaxValue : TimeSpan.FromHours(24);
            if (DateTime.Now - File.GetLastWriteTime(path) > ttl) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 显式 TTL 的缓存读取。用于文件名不含报告期的缓存（如 orgsurvey_{code}.json）——
    /// 那类名字会骗过 PeriodFromCacheName 的启发式（"300308" 被当成 3003 年 → 永久缓存），
    /// 导致新数据永不刷新。
    /// </summary>
    private static bool TryReadCacheWithTtl<T>(string path, TimeSpan ttl, out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(path)) return false;
            if (DateTime.Now - File.GetLastWriteTime(path) > ttl) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value is not null;
        }
        catch { return false; }
    }

    private void TryWriteCache<T>(string path, T value, TimeSpan ttl)
    {
        if (ttl == TimeSpan.Zero) return;
        if(FinancialRawStore.LegacyResource(path).Length>0 && FinancialAccess.Current?.RefreshFailures.Count>0)return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(value, _jsonOpts));
        }
        catch { /* 缓存失败不影响主流程 */ }
    }
}
