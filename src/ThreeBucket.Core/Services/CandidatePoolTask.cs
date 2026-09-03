using System.Globalization;
using System.Text;
using System.Text.Json;
using ThreeBucket.Core.Data;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T6 · 候选池静态筛选（C# 版，移植自 scripts/t6_candidate_pool.py，桌面/移动端通用）。
///
/// 三桶各自筛选：
/// - A 桶（红利逆向）：中证红利成分 → 股息率/PB/ROE 过滤（数据缺失放行）→ quality_score 排序
/// - B 桶（成长）：中证1000+500+A500+800 成分 → 市值/CAGR/增速/ROE/现金流/PEG 过滤
///   （核心数据缺失即剔除）→ 1/PEG 排序
/// - C 桶（热点周期）：T4 文本判定 PASS → yjbb 补充增速/毛利率 → 动态PE + MA20（仅提示） → 增速排序
///
/// 产出：data/skill_input_T6_{A,B,C}.md（LLM 消费，格式对齐 skills/t6_semantic_ranking.md）
/// + data/candidates_{A,B,C}.csv（直接查看）。每桶按排序值截断 Top 100（LLM 分析上限）。
///
/// 阈值从 trading-system/02_strategy_config.yaml 加载（PoolThresholds，改 yaml 即生效无需重编译；
/// yaml 缺失时回退内置默认）。Python 端 scripts/lib/config.py 读同一文件，两端口径强制一致。
/// ROE 口径：yjbb 最新报告期加权 ROE × 年化系数（对齐 Python _attach_roe——
/// 腾讯行情快照无 ROE，Python 版同样从 yjbb 年化补齐）。
/// </summary>
public class CandidatePoolTask : IBuiltinTask
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Key => "T6";
    public string Name => "候选池筛选";

    private readonly string _dataDir;
    private readonly CsIndexClient _csi;
    private readonly EastMoneyClient _em;
    private readonly TencentSnapshot _tencent;
    private readonly EastMoneySnapshot? _emSnap; // 腾讯覆盖率不足时的二级降级源（可空：未注入则不降级）
    private readonly KlineService _klines;

    public CandidatePoolTask(string dataDir, CsIndexClient csi, EastMoneyClient em,
        TencentSnapshot tencent, KlineService klines, EastMoneySnapshot? emSnap = null)
    {
        _dataDir = dataDir;
        _csi = csi;
        _em = em;
        _tencent = tencent;
        _emSnap = emSnap;
        _klines = klines;
        // 阈值源：<项目根>/trading-system/02_strategy_config.yaml（dataDir 的上一级）
        var projectRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)));
        _th = PoolThresholds.Load(Path.Combine(projectRoot ?? "", "trading-system", "02_strategy_config.yaml"));
    }

    // ── 阈值：从 trading-system/02_strategy_config.yaml 加载（人工编辑该文件即生效，无需重编译）──
    // yaml 缺失/解析失败时回退 PoolThresholds.Defaults()。字段含义见 PoolThresholds 注释。
    private readonly PoolThresholds _th;

    // ── 机构持仓识别关键词（险资/社保/养老金/QFII） ─────────────────

    private static readonly string[] PensionKw = { "基本养老", "养老基金" }; // 政府养老金，排除商业养老保险
    private static readonly string[] InsuranceKw =
    {
        "保险", "人寿", "平安", "泰康", "太保", "人保", "新华保险",
        "太平人寿", "友邦", "中再", "大家人寿", "农银人寿",
    };
    private static readonly string[] SocialSecurityKw = { "社保", "全国社保" };
    private static readonly string[] QfiiKw =
    {
        "摩根", "瑞银", "高盛", "富达", "QFII", "渣打", "花旗", "德银",
        "野村", "景顺", "施罗德", "巴克莱", "汇丰", "挪威中央银行",
        "阿布达比", "科威特", "淡马锡", "比尔盖茨", "老虎", "安本",
        "魁尔坎", "法兴", "新加坡政府投资", "澳门金融", "瑞信",
        "伯克希尔", "耶鲁", "斯坦福",
    };

    // ── 输出列（全列进 candidates CSV；skill_input 列对齐 Python assemble_bucket） ──

    private static readonly string[] ACols =
    {
        "code", "name", "industry", "price", "dividend_yield_ttm", "dividend_percentile_5y",
        "roe_5y_avg", "fcf_coverage", "pb", "pb_percentile", "dividend_years",
        "loss_q_3y", "ocf_ps_annual", "quality_score",
        "has_insurance", "has_social_security", "has_pension", "has_qfii",
        "inst_detail", "sort_value", "pick_reason",
    };
    private static readonly string[] AMdCols = ACols.Where(c => c != "inst_detail").ToArray();

    private static readonly string[] BCols =
    {
        "code", "name", "industry", "price", "total_mv_yi",
        // 巴菲特式批量可得指标（硬门槛 + 展示）
        "profit_cagr_3y", "revenue_cagr_3y", "roe_ann",
        "gross_margin_by_year", "gm_trend",
        "ocf_to_np", "ocf_ps_annual", "loss_q_3y", "pe_ttm", "peg",
        "np_yoy_by_year", "rev_yoy_by_year", "np_yoy_latest",
        // 巴菲特式批量不可得指标（固定填"缺少"，LLM 复核确认或解释）
        "roic", "debt_ratio", "interest_coverage", "bvps_cagr",
        "fcf_margin", "capex_intensity", "owner_earnings",
        // 订单积压参考列（LLM 复核用，不进硬门槛/排序）
        "gross_margin", "gross_margin_yoy", "rev_yoy_latest", "ocf_yoy",
        "drr", "drgs", "ibr", "arr", "order_backlog_score", "filter_pass",
        "has_insurance", "has_social_security", "has_pension", "has_qfii",
        "inst_detail", "sort_value", "pick_reason",
    };
    private static readonly string[] BMdCols = BCols.Where(c => c != "inst_detail").ToArray();

    private static readonly string[] CCols =
    {
        "code", "name", "industry", "text_score", "categories_hit_count",
        "np_yoy", "revenue_yoy", "gross_margin",
        "pe_ttm", "pe_dynamic", "pe_method", "peg",
        // 订单积压参考列（LLM 复核用，不进排序）
        "drr", "drgs", "ibr", "arr", "order_backlog_score", "filter_pass",
        "price_index_1y_high", "contract_liability_yoy", "price_above_ma60",
        "has_insurance", "has_social_security", "has_pension", "has_qfii",
        "inst_detail", "sort_value",
    };
    private static readonly string[] CMdCols =
        CCols.Where(c => c != "inst_detail" && c != "sort_value").ToArray();

    // ── 主流程 ─────────────────────────────────────────────────────

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string msg) => log?.Invoke($"[T6] {msg}");
        try
        {
            var today = TradingCalendar.NowCn();

            // 共享快照（三桶复用，一次拉取；历史期 yjbb 有磁盘缓存）
            L("加载全市场快照：3年成长（4 个年报期）+ 盈利质量（13 个季度）+ 最新报告期…");
            var snap = await LoadSnapshotsAsync(L, ct);
            L($"快照就绪：成长 {snap.Growth.Count} 只 / 质量 {snap.Quality.Count} 只 / "
              + $"最新期 yjbb {snap.Yjbb.Count} 只（报告期 {snap.Period}）");

            var (aRows, aScanned, _) = await ScreenBucketAAsync(snap, L, ct);
            var (bRows, bScanned, bDropNote) = await ScreenBucketBAsync(snap, L, ct);
            var (cRows, cScanned, _) = await ScreenBucketCAsync(snap, L, ct);

            // Top N 截断（每桶 LLM 分析上限）
            var cBefore = cRows.Count;
            aRows = aRows.Take(_th.TopN).ToList();
            bRows = bRows.Take(_th.TopN).ToList();
            cRows = cRows.Take(_th.TopN).ToList();
            L($"三桶候选：A {aRows.Count}/{aScanned}、B {bRows.Count}/{bScanned}、"
              + $"C {cRows.Count}/{cScanned}（C 截前 {cBefore}，上限 Top{_th.TopN}）");

            // 输出 skill_input_T6_{A,B,C}.md + candidates_{A,B,C}.csv
            Directory.CreateDirectory(_dataDir);
            var outputs = new List<string>();
            WriteBucket("A", aRows, outputs, log);
            WriteBucket("B", bRows, outputs, log);
            WriteBucket("C", cRows, outputs, log);

            var sections = new List<(string, string)>
            {
                ("A 桶 · 红利逆向", BucketSection("A", aRows, aScanned, "")),
                ("B 桶 · 成长", BucketSection("B", bRows, bScanned, bDropNote)),
                ("C 桶 · 热点周期", BucketSection("C", cRows, cScanned, "")),
                ("下一步",
                    "三桶候选已写入 `skill_input_T6_A/B/C.md`（Top 100 截断）。\n\n"
                    + "请将各桶内容分别喂给 LLM（参考 skills/t6_semantic_ranking.md），"
                    + "产出写回 `data/skill_output_T6_{A,B,C}.md`。\n\n"
                    + "同时已导出 `candidates_{A,B,C}.csv` 供直接查看。\n"),
            };
            var path = ReportBuilder.WriteReport(_dataDir, "T6",
                $"T6 候选池筛选 · {today:yyyy-MM-dd}", sections);
            L($"报告已写入 {path}");
            return new TaskRunResult(true, path, 0,
                $"A {aRows.Count} / B {bRows.Count} / C {cRows.Count} 只候选就绪：{string.Join("、", outputs)}");
        }
        catch (Exception ex)
        {
            return new TaskRunResult(false, "", 0, $"T6 失败: {ex.Message}");
        }
    }

    // ── 共享快照 ───────────────────────────────────────────────────

    /// <summary>4 个年报期逐年同比序列（"2023:12.3|2024:8.5|2025:20.1"，扭亏年份不含）。</summary>
    private sealed record YearlyYoy(string Np, string Rev);

    private sealed record GrowthRow(double? NpCagr, double? RevCagr, double? OcfNpRatio, double? OcfPsAnnual,
        YearlyYoy? Yoy, double? GmTrend, string GmByYear);
    private sealed record QualityRow(int LossQ, double? OcfPsAnnual);
    private sealed record Snapshots(
        Dictionary<string, GrowthRow> Growth,
        Dictionary<string, QualityRow> Quality,
        Dictionary<string, YjbbRow> Yjbb,
        Dictionary<string, double> TtmRevenue,        // TTM 营收 = 最近4单季营收之和（DRR 分母）
        Dictionary<string, double> PrevGrossMargin,  // 上年同期毛利率（过滤1：毛利率同比）
        Dictionary<string, double> PrevOcf,          // 上年同期每股经营现金流（过滤3：现金流改善）
        string Period);

    /// <summary>
    /// 全市场快照（与 Python get_growth_snapshot / get_profit_quality_snapshot / get_yjbb_snapshot 同口径）：
    /// 1. 成长：4 个年报期首末期（基期/末期净利均须为正，剔除低基数与扭亏假成长）
    /// 2. 质量：13 期（最新期 + 前 12 季），单季净利 = 本期累计 − 上期累计，一季报累计即单季
    /// 3. 最新报告期 yjbb（净利同比 / 行业 / ROE 年化基数）
    /// </summary>
    private async Task<Snapshots> LoadSnapshotsAsync(Action<string> L, CancellationToken ct)
    {
        var today = TradingCalendar.NowCn();
        var period = EastMoneyClient.LatestReportPeriod(today);

        // — 成长快照：4 个年报期 —
        var growth = new Dictionary<string, GrowthRow>(StringComparer.Ordinal);
        var annualPeriods = EastMoneyClient.LatestAnnualPeriods(4); // 旧 → 新
        var yearly = new Dictionary<string, Dictionary<string, YjbbRow>>(StringComparer.Ordinal);
        foreach (var p in annualPeriods)
        {
            var rows = await _em.GetYjbbAsync(p, L, ct);
            if (rows.Count > 0)
                yearly[p] = rows.GroupBy(r => r.Code)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        if (yearly.TryGetValue(annualPeriods[0], out var baseYear)
            && yearly.TryGetValue(annualPeriods[^1], out var lastYear))
        {
            const int span = 3; // 4 个年报期 → 3 年
            foreach (var code in baseYear.Keys.Intersect(lastYear.Keys, StringComparer.Ordinal))
            {
                var b = baseYear[code];
                var l = lastYear[code];
                double? npCagr = b.Np is > 0 && l.Np is > 0
                    ? (Math.Pow(l.Np.Value / b.Np.Value, 1.0 / span) - 1) * 100 : null;
                double? revCagr = b.Revenue is > 0 && l.Revenue is > 0
                    ? (Math.Pow(l.Revenue.Value / b.Revenue.Value, 1.0 / span) - 1) * 100 : null;
                double? ocfRatio = l.OcfPs is { } o && l.Eps is > 0 ? o / l.Eps : null;

                // 逐年同比（B 桶增长稳健性检验用）：相邻年报期水平值推导，
                // 基期非正/缺失的年份跳过（扭亏年份同比无意义，不猜）。
                var npYoyParts = new List<string>();
                var revYoyParts = new List<string>();
                for (var i = 1; i < annualPeriods.Count; i++)
                {
                    if (!yearly.TryGetValue(annualPeriods[i - 1], out var py)
                        || !yearly.TryGetValue(annualPeriods[i], out var cy))
                        continue;
                    if (!py.TryGetValue(code, out var pv) || !cy.TryGetValue(code, out var cv))
                        continue;
                    var yr = annualPeriods[i][..4];
                    if (pv.Np is > 0 && cv.Np is { } n2)
                        npYoyParts.Add($"{yr}:{(n2 / pv.Np.Value - 1) * 100:0.0}");
                    if (pv.Revenue is > 0 && cv.Revenue is { } r2)
                        revYoyParts.Add($"{yr}:{(r2 / pv.Revenue.Value - 1) * 100:0.0}");
                }

                growth[code] = new GrowthRow(npCagr, revCagr, ocfRatio, l.OcfPs,
                    new YearlyYoy(string.Join("|", npYoyParts), string.Join("|", revYoyParts)),
                    GmTrendOf(yearly, annualPeriods, code), GmSeriesOf(yearly, annualPeriods, code));
            }
        }
        else
        {
            L("[WARN] 年报期数据不足（缺首/末期），成长快照为空");
        }

        // — 盈利质量快照：13 期（旧 → 新），单季亏损计数 + 最新年报每股经营现金流 —
        var quality = new Dictionary<string, QualityRow>(StringComparer.Ordinal);
        var chrono = EastMoneyClient.RecentQuarters(12);          // [最新, 前1, …, 前12]
        chrono.Reverse();                                          // → 旧 → 新（13 期，最老是推导基期）
        var annualPeriod = today.Month >= 5 ? $"{today.Year - 1}1231" : $"{today.Year - 2}1231";
        var prevOf = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < chrono.Count; i++) prevOf[chrono[i]] = chrono[i - 1];
        var rangePeriods = chrono.Skip(1).ToList();               // 前 12 个季度判亏损

        var cumNp = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var cumRev = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal); // 营收累计（TTM 用）
        var ocfMap = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in chrono)
        {
            var rows = await _em.GetYjbbAsync(p, L, ct);
            foreach (var r in rows)
            {
                if (r.Np is { } np)
                {
                    if (!cumNp.TryGetValue(r.Code, out var m))
                        cumNp[r.Code] = m = new Dictionary<string, double>(StringComparer.Ordinal);
                    m[p] = np;
                }
                if (r.Revenue is { } rev)
                {
                    if (!cumRev.TryGetValue(r.Code, out var mr))
                        cumRev[r.Code] = mr = new Dictionary<string, double>(StringComparer.Ordinal);
                    mr[p] = rev;
                }
                if (p == annualPeriod && r.OcfPs is { } ocf)
                    ocfMap[r.Code] = ocf;
            }
        }
        foreach (var (code, pmap) in cumNp)
        {
            var loss = 0;
            foreach (var p in rangePeriods)
            {
                if (!pmap.TryGetValue(p, out var cur)) continue;
                double single;
                if (p.EndsWith("0331", StringComparison.Ordinal))
                    single = cur; // 一季报累计即单季
                else if (!prevOf.TryGetValue(p, out var prev) || !pmap.TryGetValue(prev, out var prevVal))
                    continue;     // 缺基期无法推导单季，跳过不判
                else
                    single = cur - prevVal;
                if (single < 0) loss++;
            }
            quality[code] = new QualityRow(loss, ocfMap.GetValueOrDefault(code));
        }

        // — TTM 营收 = 最近 4 个单季营收之和（单季推导与净利同口径：Q1累计即单季，其余=本期−上期） —
        var ttmRevenue = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (code, pmap) in cumRev)
        {
            // chrono 旧→新，取最新 4 个能推导出单季的报告期
            var singles = new List<double>();
            for (var i = chrono.Count - 1; i >= 0 && singles.Count < 4; i--)
            {
                var p = chrono[i];
                if (!pmap.TryGetValue(p, out var cur)) continue;
                double single;
                if (p.EndsWith("0331", StringComparison.Ordinal))
                    single = cur;
                else if (!prevOf.TryGetValue(p, out var prev) || !pmap.TryGetValue(prev, out var prevVal))
                    continue;
                else
                    single = cur - prevVal;
                singles.Add(single);
            }
            if (singles.Count == 4)
                ttmRevenue[code] = singles.Sum();
        }

        // — 最新报告期 yjbb —
        var yjbbRows = await _em.GetYjbbAsync(period, L, ct);
        var yjbb = yjbbRows.GroupBy(r => r.Code)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // — 上年同期毛利率 / 每股经营现金流（三道过滤用，取 chrono 倒数第5期 = 同一季上年）—
        // chrono 旧→新 13 期，最新 chrono[^1] 即 period，上年同期 = chrono[^5]（往前4季）
        var prevGrossMargin = new Dictionary<string, double>(StringComparer.Ordinal);
        var prevOcf = new Dictionary<string, double>(StringComparer.Ordinal);
        if (chrono.Count >= 5)
        {
            var prevPeriod = chrono[^5];
            var prevRows = await _em.GetYjbbAsync(prevPeriod, L, ct); // 历史期有磁盘缓存，无新增网络压力
            foreach (var r in prevRows)
            {
                if (r.GrossMargin is { } gm) prevGrossMargin[r.Code] = gm;
                if (r.OcfPs is { } ocf) prevOcf[r.Code] = ocf;
            }
        }

        return new Snapshots(growth, quality, yjbb, ttmRevenue, prevGrossMargin, prevOcf, period);
    }

    /// <summary>毛利率 4 个年报期趋势（巴菲特"5年不下滑"批量近似）：
    /// 末期 − 基期，正=上行/持平；某期缺失跳过该期。数据不足 → null。</summary>
    private static double? GmTrendOf(Dictionary<string, Dictionary<string, YjbbRow>> yearly,
        List<string> annualPeriods, string code)
    {
        var gms = new List<double>();
        foreach (var p in annualPeriods)
        {
            if (yearly.TryGetValue(p, out var yr) && yr.TryGetValue(code, out var row)
                && row.GrossMargin is { } gm)
                gms.Add(gm);
        }
        return gms.Count >= 2 ? gms[^1] - gms[0] : null;
    }

    /// <summary>毛利率逐年序列展示串："2022:31.5|2023:32.0|…"（缺失期不含）。</summary>
    private static string GmSeriesOf(Dictionary<string, Dictionary<string, YjbbRow>> yearly,
        List<string> annualPeriods, string code)
    {
        var parts = new List<string>();
        foreach (var p in annualPeriods)
        {
            if (yearly.TryGetValue(p, out var yr) && yr.TryGetValue(code, out var row)
                && row.GrossMargin is { } gm)
                parts.Add($"{p[..4]}:{gm:0.0}");
        }
        return string.Join("|", parts);
    }

    // ── A 桶 · 红利逆向 ────────────────────────────────────────────

    private async Task<(List<Row> Rows, int Scanned, string DropNote)> ScreenBucketAAsync(
        Snapshots snap, Action<string> L, CancellationToken ct)
    {
        L("[A] 拉取中证红利成分股…");
        var cons = await _csi.GetConstituentsAsync("000922", ct);
        if (cons.Count == 0)
        {
            L("[A] ⚠️ 成分股数据为空，跳过 A 桶");
            return (new List<Row>(), 0, "");
        }

        var codes = cons.Select(c => c.Code).ToList();
        L($"[A] 批量拉取 {codes.Count} 只成分股基本面（腾讯行情）…");
        var fund = await _tencent.GetBatchAsync(codes, L, ct);
        var inst = await FetchInstitutionalHoldersAsync(codes, L, ct);

        var results = new List<Row>();
        foreach (var c in cons)
        {
            if (!fund.ContainsKey(c.Code)) continue; // 基本面缺失直接跳过
            var f = fund[c.Code];
            var dy = f.DvTtm;
            var pb = f.Pb;
            var price = f.Price;

            // ROE：yjbb 最新报告期年化（_attach_roe 口径）
            double? roe = null;
            if (snap.Yjbb.TryGetValue(c.Code, out var y) && y.Roe is { } r)
                roe = r * EastMoneyClient.RoeAnnualizeFactor(snap.Period);
            var industry = snap.Yjbb.GetValueOrDefault(c.Code)?.Industry ?? "";

            // 硬门槛：数据缺失放行（成分股本身已由指数做过股息筛选）
            if (dy is { } d && d != 0 && d < _th.MinDy) continue;
            if (pb is { } p && p != 0 && p > _th.MaxPb) continue;
            if (roe is { } ro && ro < _th.MinRoeA) continue;

            // 盈利质量：近3年单季亏损 → 剔除；年报经营现金流为负（借钱分红嫌疑）→ 不剔除，仅显眼标记
            snap.Quality.TryGetValue(c.Code, out var q);
            if (q is { LossQ: > 0 }) continue;
            var ocfNeg = q?.OcfPsAnnual is < 0;

            // quality_score（Python 近似口径：fcf=1.0、div_years=5、机构每种 +0.05）
            var roeNorm = roe is { } rv && rv != 0 ? rv : 10.0; // Python: roe or 10.0
            var roeScore = Math.Min(roeNorm, 30.0) / 30.0;
            var instInfo = inst.GetValueOrDefault(c.Code) ?? new InstInfo(false, false, false, false, "", 0);
            var qualityScore = roeScore * 0.4 + 1.0 * 0.3 + (5.0 / 10.0) * 0.3 + 0.05 * instInfo.Count;

            // PB 分位（简化：当前 PB 在 [0,2] 区间的逆映射）
            var pbPct = pb is { } pbv && pbv != 0
                ? Math.Min(100.0, Math.Max(0.0, (2.0 - pbv) / 2.0 * 100.0))
                : 50.0;

            // 排序值：有股息率用 股息率×质量分，否则退化为质量分
            var sortValue = dy is { } dv && dv != 0 ? dv * qualityScore : qualityScore;

            var reasons = new List<string>
            {
                dy is { } d2 && d2 != 0 ? $"股息率{d2:0.00}%≥{_th.MinDy:0.0}%" : "股息率缺失放行",
                pb is { } p2 && p2 != 0 ? $"PB {p2:0.00}≤{_th.MaxPb:0.0}" : "PB缺失放行",
                roe is { } r2 ? $"ROE年化{r2:0.0}%≥{_th.MinRoeA:0.0}%" : "ROE缺失放行",
                q is not null ? $"近3年亏损季度{q.LossQ}" : "亏损数据缺失放行",
                q?.OcfPsAnnual is { } oq
                    ? (ocfNeg ? $"⚠️年报经营现金流/股{oq:0.00}<0（借钱分红嫌疑，请人工复核）" : $"年报经营现金流/股{oq:0.00}≥0")
                    : "现金流数据缺失放行",
            };

            var row = new Row { Sort = sortValue };
            row.F["code"] = c.Code;
            row.F["name"] = c.Name;
            row.F["industry"] = industry; // Python 版数据源无行业列（留空），C# 从 yjbb 补充
            row.F["price"] = price is { } pr && pr != 0 ? pr.ToString("0.00", Inv) : "";
            row.F["dividend_yield_ttm"] = dy is { } d3 && d3 != 0 ? d3.ToString("0.00", Inv) : "";
            row.F["dividend_percentile_5y"] = ""; // 需要历史数据，Python 版同样留空
            row.F["roe_5y_avg"] = roe is { } r3 ? r3.ToString("0.0", Inv) : "";
            row.F["fcf_coverage"] = "1.0";         // 简化：无 FCF 数据时默认 1
            row.F["pb"] = pb is { } p3 && p3 != 0 ? p3.ToString("0.00", Inv) : "";
            row.F["pb_percentile"] = pbPct.ToString("0.0", Inv);
            row.F["dividend_years"] = "5";         // 简化：成分股默认至少 3 年
            row.F["loss_q_3y"] = q is not null ? q.LossQ.ToString(Inv) : "";
            row.F["ocf_ps_annual"] = q?.OcfPsAnnual is { } o2 ? o2.ToString("0.00", Inv) : "";
            row.F["quality_score"] = qualityScore.ToString("0.000", Inv);
            row.F["has_insurance"] = instInfo.Insurance ? "是" : "";
            row.F["has_social_security"] = instInfo.SocialSecurity ? "是" : "";
            row.F["has_pension"] = instInfo.Pension ? "是" : "";
            row.F["has_qfii"] = instInfo.Qfii ? "是" : "";
            row.F["inst_detail"] = instInfo.Detail;
            row.F["sort_value"] = sortValue.ToString("0.000", Inv);
            row.F["pick_reason"] = string.Join(" | ", reasons);
            results.Add(row);
        }

        L($"[A] 完成：{results.Count} 只通过硬门槛（共扫 {cons.Count} 只）");
        return (results.OrderByDescending(r => r.Sort).ToList(), cons.Count, "");
    }

    // ── B 桶 · 成长 ────────────────────────────────────────────────

    private async Task<(List<Row> Rows, int Scanned, string DropNote)> ScreenBucketBAsync(
        Snapshots snap, Action<string> L, CancellationToken ct)
    {
        L("[B] 中证1000+500+A500+800 全市场大中盘成长筛选…");
        var pools = new List<List<ConsRow>>();
        var labels = new List<string>();
        foreach (var (idx, label) in new[]
                 {
                     ("000852", "中证1000"), ("000905", "中证500"),
                     ("000510", "中证A500"), ("000906", "中证800(含沪深300)"),
                 })
        {
            var rows = await _csi.GetConstituentsAsync(idx, ct);
            if (rows.Count == 0)
            {
                L($"[B] [WARN] {label} 成分数据为空，跳过该池");
                continue;
            }
            pools.Add(rows);
            labels.Add($"{label}{rows.Count}");
        }
        if (pools.Count == 0)
        {
            L("[B] ⚠️ 四个指数成分数据均为空，跳过 B 桶");
            return (new List<Row>(), 0, "");
        }

        var consMap = new Dictionary<string, string>(StringComparer.Ordinal); // code → name
        foreach (var pool in pools)
            foreach (var c in pool)
                consMap.TryAdd(c.Code, c.Name);
        var before = consMap.Count;
        foreach (var k in consMap.Where(kv => kv.Value.Contains("ST") || kv.Value.Contains("退"))
                     .Select(kv => kv.Key).ToList())
            consMap.Remove(k); // ST/退市风险剔除（指数编制本就排除，双保险）
        L($"[B] 股票池合并 {string.Join(" + ", labels)}，去重后 {before} 只，剔 ST/退 后 {consMap.Count} 只");

        var codes = consMap.Keys.ToList();
        L($"[B] 批量拉取 {codes.Count} 只基本面（PE/市值/现价，腾讯行情）…");
        var fund = await _tencent.GetBatchAsync(codes, L, ct);

        // 腾讯覆盖率过低时降级东财全市场快照补全缺失票（PE/总市值腾讯唯一源，
        // 抽风会让大批票计入"基本面缺失"静默剔除 → B 桶塌成 1 只）。
        // 东财 clist 一次拉全市场（含 PE/总市值/市净率），把腾讯没覆盖的票补进 fund。
        if (_emSnap is not null && fund.Count < codes.Count * 0.6)
        {
            var missing = codes.Count - fund.Count;
            L($"[B] 腾讯覆盖率 {fund.Count}/{codes.Count} 偏低，降级东财全市场快照补全…");
            var em = await _emSnap.GetMarketSnapshotAsync(L, ct);
            var added = 0;
            foreach (var code in codes)
            {
                if (fund.ContainsKey(code)) continue; // 腾讯已有优先
                if (!em.TryGetValue(code, out var e)) continue;
                fund[code] = new TencentFundamental(code, "", e.Price, e.PeTtm, e.Pb, e.TotalMvYi, null);
                added++;
            }
            L($"[B] 东财补全 {added}/{missing} 只，现覆盖率 {fund.Count}/{codes.Count}");
        }

        // 剔除计数拆「阈值不足」与「数据缺失」两本账：null 归缺失、值在但不达标归阈值。
        // 否则数据源抽风时大批 null 被算成"没达门槛"，B 桶塌成 1 只却看不出是数据故障。
        // 2026-09-02 巴菲特式指标集（纯财务指标，无行业黑名单）：CAGR/毛利率趋势/ROE/
        // 亏损季/现金含金量/PE；周期与爆发属性由 LLM 复核（业务质量维度）个案定性。
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["市值不足"] = 0, ["净利CAGR"] = 0, ["营收CAGR"] = 0, ["毛利率下滑"] = 0,
            ["ROE"] = 0, ["亏损季度"] = 0, ["现金含金量"] = 0, ["PE"] = 0,
        };
        var droppedMissing = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["基本面"] = 0, ["市值"] = 0, ["净利CAGR"] = 0, ["营收CAGR"] = 0,
            ["毛利率"] = 0, ["ROE"] = 0, ["亏损季度"] = 0, ["现金含金量"] = 0, ["PE"] = 0,
        };
        var results = new List<Row>();

        foreach (var (code, name) in consMap)
        {
            if (!fund.TryGetValue(code, out var f))
            {
                droppedMissing["基本面"]++;
                continue;
            }
            var pe = f.PeTtm;
            var totalMv = f.TotalMvYi; // 亿元
            var price = f.Price;

            // ROE 年化（yjbb 加权 ROE × 年化系数；缺失剔除）
            double? roe = null;
            if (snap.Yjbb.TryGetValue(code, out var y) && y.Roe is { } r)
                roe = r * EastMoneyClient.RoeAnnualizeFactor(snap.Period);

            // 市值门槛（缺市值视为不满足：中盘定位是硬约束）
            if (totalMv is null) { droppedMissing["市值"]++; continue; }
            if (totalMv < _th.MinMv) { dropped["市值不足"]++; continue; }

            // 成长门槛：CAGR 缺失（基期为负/缺披露）直接剔除，不放行
            snap.Growth.TryGetValue(code, out var g);
            var npCagr = g?.NpCagr;
            var revCagr = g?.RevCagr;
            var ocfRatio = g?.OcfNpRatio;
            var ocfPsA = g?.OcfPsAnnual;
            var npSeriesRaw = g?.Yoy?.Np ?? "";
            var revSeriesRaw = g?.Yoy?.Rev ?? "";
            if (npCagr is null) { droppedMissing["净利CAGR"]++; continue; }
            if (npCagr < _th.MinNpCagr) { dropped["净利CAGR"]++; continue; }
            if (revCagr is null) { droppedMissing["营收CAGR"]++; continue; }
            if (revCagr < _th.MinRevCagr) { dropped["营收CAGR"]++; continue; }

            // 盈利能力：毛利率逐年不趋势下滑（巴菲特定价权代理；缺失剔除）
            var gmTrend = g?.GmTrend;
            if (gmTrend is null) { droppedMissing["毛利率"]++; continue; }
            if (_th.GmNoDecline && gmTrend < 0) { dropped["毛利率下滑"]++; continue; }

            // 最新报告期净利同比（仅展示列，不构成门槛——周期暴增由过滤②拦）
            var npYoy = y?.NpYoy;
            var industry = y?.Industry ?? "";

            // ROE 年化门槛（巴菲特 ≥15% 标准线的批量近似 12%）
            if (roe is null) { droppedMissing["ROE"]++; continue; }
            if (roe < _th.MinRoeB) { dropped["ROE"]++; continue; }

            // 盈利稳定性：近 3 年单季亏损（ROE 稳定性批量代理；缺失同样剔除）
            snap.Quality.TryGetValue(code, out var q);
            var lossQ = (int?)q?.LossQ;
            if (lossQ is null) { droppedMissing["亏损季度"]++; continue; }
            if (lossQ > 0) { dropped["亏损季度"]++; continue; }

            // 财务稳健：最新年报现金含金量 OCF/净利（巴菲特 ≥1.0 的单年近似 0.8；缺失剔除）
            if (ocfRatio is null) { droppedMissing["现金含金量"]++; continue; }
            if (ocfRatio < _th.MinOcfRatio) { dropped["现金含金量"]++; continue; }

            // 估值兜底：PE 区间（防负 PE/极端值；PEG 不再作为门槛，仅展示）
            if (pe is null) { droppedMissing["PE"]++; continue; }
            if (pe <= 0 || pe > _th.MaxPe) { dropped["PE"]++; continue; }

            var sortVal = npCagr.Value / pe.Value; // = 1/PEG 降序
            var cagrCapped = Math.Min(npCagr.Value, 100.0);
            var peg = pe.Value / cagrCapped;
            var reason =
                $"总市值{totalMv:0}亿≥{_th.MinMv:0} | " +
                $"净利CAGR3年+{npCagr:0}%≥{_th.MinNpCagr:0}% | " +
                $"营收CAGR3年+{revCagr:0}%≥{_th.MinRevCagr:0}% | " +
                $"毛利率4年{(gmTrend >= 0 ? "未下滑" : "下滑")}({g?.GmByYear}) | " +
                $"ROE年化{roe:0.0}%≥{_th.MinRoeB:0}% | " +
                "近3年亏损季度0 | " +
                $"OCF/净利{ocfRatio:0.00}≥{_th.MinOcfRatio:0.0} | " +
                $"PE(TTM){pe:0.0}≤{_th.MaxPe:0}";

            var row = new Row { Sort = sortVal };
            row.F["code"] = code;
            row.F["name"] = name;
            row.F["industry"] = industry;
            row.F["price"] = price is { } pr ? pr.ToString("0.00", Inv) : "";
            row.F["total_mv_yi"] = totalMv.Value.ToString("0", Inv);
            row.F["profit_cagr_3y"] = npCagr.Value.ToString("0.0", Inv);
            row.F["revenue_cagr_3y"] = revCagr.Value.ToString("0.0", Inv);
            row.F["np_yoy_latest"] = npYoy is { } nyp ? nyp.ToString("0.0", Inv) : "";
            row.F["roe_ann"] = roe.Value.ToString("0.0", Inv);
            row.F["np_yoy_by_year"] = npSeriesRaw;
            row.F["rev_yoy_by_year"] = revSeriesRaw;
            row.F["gross_margin_by_year"] = g?.GmByYear ?? "";
            row.F["gm_trend"] = gmTrend.Value.ToString("+0.0;-0.0", Inv);
            row.F["ocf_to_np"] = ocfRatio.Value.ToString("0.00", Inv);
            row.F["ocf_ps_annual"] = ocfPsA is { } ops ? ops.ToString("0.00", Inv) : "";
            // 巴菲特式批量不可得指标：标"缺少"，供 LLM 复核确认或解释
            row.F["roic"] = "缺少";
            row.F["debt_ratio"] = "缺少";
            row.F["interest_coverage"] = "缺少";
            row.F["bvps_cagr"] = "缺少";
            row.F["fcf_margin"] = "缺少";
            row.F["capex_intensity"] = "缺少";
            row.F["owner_earnings"] = "缺少";
            row.F["loss_q_3y"] = lossQ.Value.ToString(Inv);
            row.F["pe_ttm"] = pe.Value.ToString("0.0", Inv);
            row.F["peg"] = peg.ToString("0.00", Inv);
            row.F["has_insurance"] = "";
            row.F["has_social_security"] = "";
            row.F["has_pension"] = "";
            row.F["has_qfii"] = "";
            row.F["inst_detail"] = "";
            row.F["sort_value"] = sortVal.ToString("0.000", Inv);
            row.F["pick_reason"] = reason;
            results.Add(row);
        }

        L($"[B] 完成：{results.Count} 只通过硬门槛（共扫 {consMap.Count} 只）");

        // 通过硬门槛后再拉主要财务指标（填充巴菲特式"缺少"指标：ROIC/资产负债率/利息保障/BPS/净现比）
        if (results.Count > 0)
        {
            var mfCodes = results.Select(r => r.F["code"]).ToList();
            L($"[B] 拉取主要财务指标（{mfCodes.Count} 只，逐票 MAINFINADATA）…");
            var mfMap = new Dictionary<string, List<MainFinRow>>(StringComparer.Ordinal);
            foreach (var c in mfCodes)
            {
                try
                {
                    var mfr = await _em.GetMainFinAsync(c, L, ct);
                    if (mfr.Count > 0)
                        mfMap[c] = mfr; // 保留全部期数，供多期计算（BPS-CAGR/净现比均值）
                }
                catch { /* 单票失败不影响整体，保持"缺少" */ }
            }
            L($"[B] 拉取现金流量表（{mfCodes.Count} 只，逐票 GCASHFLOW，FCF 三项用）…");
            var cfMap = new Dictionary<string, List<CashFlowRow>>(StringComparer.Ordinal);
            foreach (var c in mfCodes)
            {
                try
                {
                    var cfr = await _em.GetCashFlowAsync(c, L, ct);
                    if (cfr.Count > 0)
                        cfMap[c] = cfr;
                }
                catch { /* 单票失败不影响整体，保持"缺少" */ }
            }

            // 只看年报期（1231 结尾），排除季报失真；rows 已按报告期降序
            var mfGot = 0;
            foreach (var row in results)
            {
                var c0 = row.F["code"];
                var mfs = mfMap.GetValueOrDefault(c0);
                if (mfs is not null && mfs.Count > 0)
                {
                    mfGot++;
                    var latest = mfs[0];

                    // 单期字段：直接取最新报告期
                    if (latest.Roic is { } roicV) row.F["roic"] = roicV.ToString("0.0", Inv);
                    if (latest.DebtRatio is { } debtV) row.F["debt_ratio"] = debtV.ToString("0.0", Inv);
                    if (latest.InterestCoverage is { } icV) row.F["interest_coverage"] = icV.ToString("0.0", Inv);

                    // 多期字段：BPS-CAGR（最早~最晚年报），净现比取年报期均值
                    var annual = mfs.Where(m => m.ReportDate is { } rd && rd.Month == 12).ToList();
                    if (annual.Count >= 2
                        && annual[^1].Bps is { } b0 && b0 > 0
                        && annual[0].Bps is { } b1 && b1 > 0)
                    {
                        var years = (annual[0].ReportDate!.Value.Year - annual[^1].ReportDate!.Value.Year);
                        if (years > 0)
                        {
                            var bvpsCagr = (Math.Pow(b1 / b0, 1.0 / years) - 1) * 100;
                            row.F["bvps_cagr"] = bvpsCagr.ToString("0.0", Inv);
                        }
                    }
                    var ratios = annual.Where(m => m.OcfToNp is { }).Select(m => m.OcfToNp.Value).ToList();
                    if (ratios.Count > 0)
                        row.F["fcf_margin"] = ratios.Average().ToString("0.00", Inv);
                }

                // FCF 三项：年报期 capex / 折旧摊销 / 营收（营收来自 yjbb TTM 或年报）
                var cfs = cfMap.GetValueOrDefault(c0);
                if (cfs is not null)
                {
                    var ann = cfs.Where(m => m.ReportDate is { } rd && rd.Month == 12).ToList();
                    if (ann.Count > 0)
                    {
                        var a0 = ann[0]; // 最新年报
                        if (a0.Ocf is { } ocf && ocf > 0)
                        {
                            if (a0.Capex is { } capex && capex > 0)
                            {
                                // FCF利润率 近似：年报 OCF − Capex（分母营收用 TTM）
                                if (snap.TtmRevenue.TryGetValue(c0, out var ttmRev) && ttmRev > 0)
                                    row.F["fcf_margin"] = (((ocf - capex) / ttmRev) * 100).ToString("0.0", Inv);
                                // Capex强度 = Capex / OCF
                                row.F["capex_intensity"] = (capex / ocf * 100).ToString("0.0", Inv);
                            }
                            // 所有者收益近似 = 净利 + 折旧摊销 − Capex
                            // （净利取 yjbb 最新期净利润；净利润为负时无意义，留空）
                            if (snap.Yjbb.TryGetValue(c0, out var yb) && yb.Np is { } npv && npv > 0)
                            {
                                var da = (a0.Depreciation ?? 0) + (a0.IntangibleAmort ?? 0)
                                       + (a0.DeferredAmort ?? 0);
                                var oe = npv + da - (a0.Capex ?? 0);
                                row.F["owner_earnings"] = oe.ToString("0.0", Inv);
                            }
                        }
                    }
                }
            }
            L($"[B] 主要财务指标填充 {mfGot}/{results.Count} 只");
        }

        // 通过硬门槛后再拉机构持仓（避免拉取上千只）；每种机构 sort +0.3
        if (results.Count > 0)
        {
            var inst = await FetchInstitutionalHoldersAsync(
                results.Select(r => r.F["code"]).ToList(), L, ct);
            foreach (var row in results)
            {
                var info = inst.GetValueOrDefault(row.F["code"])
                    ?? new InstInfo(false, false, false, false, "", 0);
                row.F["has_insurance"] = info.Insurance ? "是" : "";
                row.F["has_social_security"] = info.SocialSecurity ? "是" : "";
                row.F["has_pension"] = info.Pension ? "是" : "";
                row.F["has_qfii"] = info.Qfii ? "是" : "";
                row.F["inst_detail"] = info.Detail;
                row.Sort += 0.3 * info.Count;
                row.F["sort_value"] = row.Sort.ToString("0.000", Inv);
            }
        }

        // 通过硬门槛后再逐票拉资产负债表，计算订单积压指标（DRR/DRGS/IBR/ARR + 综合得分 + 三道过滤）
        if (results.Count > 0)
        {
            var obCodes = results.Select(r => r.F["code"]).ToList();
            var raw = await FetchOrderBacklogAsync(obCodes, snap, L, ct);
            var scores = NormalizeAndScore(raw);
            var yjyg = await _em.GetYjygSnapshotAsync(L, ct);

            foreach (var row in results)
            {
                var code = row.F["code"];
                // 透传已有 yjbb 字段：最新毛利率 / 营收同比 / 现金流同比（B 桶此前未透传）
                if (snap.Yjbb.TryGetValue(code, out var yb))
                {
                    row.F["gross_margin"] = yb.GrossMargin is { } gm ? gm.ToString("0.0", Inv) : "";
                    row.F["rev_yoy_latest"] = yb.RevYoy is { } ry ? ry.ToString("0.0", Inv) : "";
                    row.F["gross_margin_yoy"] = gm2(yb, snap.PrevGrossMargin, code);
                    row.F["ocf_yoy"] = ocfYoy(yb, snap.PrevOcf, code);
                }
                else
                {
                    row.F["gross_margin"] = ""; row.F["rev_yoy_latest"] = "";
                    row.F["gross_margin_yoy"] = ""; row.F["ocf_yoy"] = "";
                }

                var r = raw.GetValueOrDefault(code);
                row.F["drr"] = r?.Drr is { } drr ? drr.ToString("0.000", Inv) : "";
                row.F["drgs"] = r?.Drgs is { } drgs ? drgs.ToString("0.0", Inv) : "";
                row.F["ibr"] = r?.Ibr is { } ibr ? ibr.ToString("0.0", Inv) : "";
                row.F["arr"] = r?.Arr is { } arr ? arr.ToString("0.0", Inv) : "";

                var (score, hasData) = scores.GetValueOrDefault(code);
                row.F["order_backlog_score"] = hasData ? score.ToString("0.0", Inv) : "";

                var (pass, note) = ComputeFilterPass(code, snap, snap.PrevGrossMargin, snap.PrevOcf, yjyg);
                row.F["filter_pass"] = pass ? "是" : $"否({note})";
            }
        }

        // 剔除分布分两行打印：阈值不足是真实没达门槛，数据缺失是源没覆盖到。
        // 数据缺失这本账是 B 桶塌成 1 只的铁证——一眼可判是数据故障而非策略结果。
        var dropNote = string.Join("、", dropped.Where(kv => kv.Value > 0)
            .Select(kv => $"{kv.Key}{kv.Value}"));
        var missingNote = string.Join("、", droppedMissing.Where(kv => kv.Value > 0)
            .Select(kv => $"{kv.Key}{kv.Value}"));
        if (dropNote.Length > 0) L($"[B] 剔除分布(阈值不足): {dropNote}");
        if (missingNote.Length > 0) L($"[B] 剔除分布(数据缺失): {missingNote} ⚠️ 数据源未覆盖，非策略剔除");

        // 覆盖率兜底告警：补充后仍极低 → 结果不可信，明示而非当成 1 只"通过"
        var coverage = fund.Count == 0 ? 0 : (double)fund.Count / consMap.Count * 100;
        if (coverage < 10)
            L($"[B] ⚠️⚠️ 基本面覆盖率 {coverage:0.0}% 极低，B 桶结果不可信（数据源异常）");
        else if (coverage < 60)
            L($"[B] ⚠️ 基本面覆盖率 {coverage:0.0}% 偏低，部分票可能因数据缺失被误删");

        // 写入文件的剔除分布：两本账都带上，让 skill_input 也能看出数据缺失
        var fullDropNote = dropNote + (dropNote.Length > 0 && missingNote.Length > 0 ? "；" : "")
            + (missingNote.Length > 0 ? $"数据缺失: {missingNote}" : "");
        return (results.OrderByDescending(r => r.Sort).ToList(), consMap.Count, fullDropNote);
    }

    // ── C 桶 · 热点周期 ────────────────────────────────────────────

    private async Task<(List<Row> Rows, int Scanned, string DropNote)> ScreenBucketCAsync(
        Snapshots snap, Action<string> L, CancellationToken ct)
    {
        L("[C] 从 T4 输出读取文本判定 PASS 标的…");
        var t4Path = Path.Combine(_dataDir, "skill_output_T4C.md");
        if (!File.Exists(t4Path))
        {
            L("[C] ⚠️ skill_output_T4C.md 不存在，跳过 C 桶（请先运行 T4 流程）");
            return (new List<Row>(), 0, "");
        }

        var passed = EarningsScanTask.ParseLlmOutput(File.ReadAllText(t4Path))
            .Where(i => JStr(i, "verdict").ToUpperInvariant() == "PASS").ToList();
        if (passed.Count == 0)
        {
            L("[C] T4 输出中无 PASS 条目");
            return (new List<Row>(), 0, "");
        }

        var allCodes = passed.Select(i => JStr(i, "stock_code"))
            .Where(c => c.Length > 0).ToList();
        L($"[C] T4 PASS {passed.Count} 只，补充财务指标（yjbb + 腾讯行情）…");
        var fund = await _tencent.GetBatchAsync(allCodes, L, ct);
        var inst = await FetchInstitutionalHoldersAsync(allCodes, L, ct);

        // 订单积压指标：逐票拉资产负债表 + 归一化评分（allCodes 本就是 T4 PASS 小样本）
        var orderRaw = await FetchOrderBacklogAsync(allCodes, snap, L, ct);
        var orderScores = NormalizeAndScore(orderRaw);
        var yjyg = await _em.GetYjygSnapshotAsync(L, ct);

        // 报告期 → 年化系数（动态 PE 用：一季报×4 / 中报×2 / 三季报×4/3 / 年报×1）
        var (qLabel, annFactor) = snap.Period[^4..] switch
        {
            "0331" => ("一季报×4", 4.0),
            "0630" => ("中报×2", 2.0),
            "0930" => ("三季报×4/3", 4.0 / 3.0),
            _ => ("年报", 1.0),
        };

        var results = new List<Row>();
        foreach (var item in passed)
        {
            var code = JStr(item, "stock_code");
            var name = JStr(item, "stock_name");
            var industry = JStr(item, "industry");
            var textScore = double.TryParse(JStr(item, "weighted_score"),
                NumberStyles.Any, Inv, out var ts) ? ts : 0;
            var catsCount = 0;
            if (item.TryGetProperty("categories_hit", out var cats) && cats.ValueKind == JsonValueKind.Object)
                foreach (var v in cats.EnumerateObject())
                    if (v.Value.ValueKind == JsonValueKind.Array)
                        catsCount += v.Value.GetArrayLength();

            // 业绩报表快照补充：净利同比 / 营收同比 / 毛利率
            snap.Yjbb.TryGetValue(code, out var y);
            var npYoy = y?.NpYoy;
            var revYoy = y?.RevYoy;
            var gm = y?.GrossMargin;
            fund.TryGetValue(code, out var f);
            var peTtm = f?.PeTtm;

            // 动态 PE：已披露最新季报 → 年化推算（EPS×系数 优先，退回 总市值/年化净利润）；
            // 未披露 → 用 PE(TTM)
            string peDynamic = "";
            var peMethod = "PE(TTM)";
            if (y is not null)
            {
                if (y.Eps is > 0 && f?.Price is > 0)
                {
                    peDynamic = (f.Price.Value / (y.Eps.Value * annFactor)).ToString("0.0", Inv);
                    peMethod = $"动态({qLabel})";
                }
                else if (y.Np is > 0 && f?.TotalMvYi is > 0)
                {
                    peDynamic = (f.TotalMvYi.Value / (y.Np.Value * annFactor / 1e8)).ToString("0.0", Inv);
                    peMethod = $"动态({qLabel})";
                }
            }
            if (peDynamic.Length == 0 && peTtm is { } pt)
                peDynamic = pt.ToString("0.0", Inv);

            // PEG = PE_TTM / 净利同比增速（增速用百分比数字，如 50% → 50）
            var pegStr = peTtm is > 0 && npYoy is > 0
                ? (peTtm.Value / npYoy.Value).ToString("0.00", Inv) : "";

            // 价在 MA20 上方：仅提示，不进硬门槛/排序（列名 price_above_ma60 沿用旧契约）
            var aboveMa20 = await CheckPriceAboveMaAsync(code, 20, ct);

            // 主排序值：净利润同比增速封顶 500 防极端值；机构持仓每种 +10
            var sortVal = npYoy is { } ny ? Math.Min(ny, 500.0) : 0.0;
            var info = inst.GetValueOrDefault(code) ?? new InstInfo(false, false, false, false, "", 0);
            sortVal += 10 * info.Count;

            var row = new Row { Sort = sortVal };
            row.F["code"] = code;
            row.F["name"] = name;
            row.F["industry"] = industry;
            row.F["text_score"] = textScore.ToString("0.00", Inv);
            row.F["categories_hit_count"] = catsCount.ToString(Inv);
            row.F["np_yoy"] = npYoy is { } n1 ? n1.ToString("0.0", Inv) : "";
            row.F["revenue_yoy"] = revYoy is { } r1 ? r1.ToString("0.0", Inv) : "";
            row.F["gross_margin"] = gm is { } g1 ? g1.ToString("0.0", Inv) : "";
            row.F["pe_ttm"] = peTtm is { } p1 ? p1.ToString("0.0", Inv) : "";
            row.F["pe_dynamic"] = peDynamic;
            row.F["pe_method"] = peMethod;
            row.F["peg"] = pegStr;
            row.F["price_index_1y_high"] = "";          // 需要行业指数，LLM 层验证
            // 订单积压指标（填实原空占位 + 新增列）
            var or = orderRaw.GetValueOrDefault(code);
            // contract_liability_yoy 用合同负债同比原值填实（保留原列名供 yaml 既有契约）
            row.F["contract_liability_yoy"] = or?.ContractLiabYoy is { } cy ? cy.ToString("0.0", Inv) : "";
            row.F["drr"] = or?.Drr is { } drr ? drr.ToString("0.000", Inv) : "";
            row.F["drgs"] = or?.Drgs is { } drgs ? drgs.ToString("0.0", Inv) : "";
            row.F["ibr"] = or?.Ibr is { } ibr ? ibr.ToString("0.0", Inv) : "";
            row.F["arr"] = or?.Arr is { } arr ? arr.ToString("0.0", Inv) : "";
            var (ocs, ocsHas) = orderScores.GetValueOrDefault(code);
            row.F["order_backlog_score"] = ocsHas ? ocs.ToString("0.0", Inv) : "";
            var (fpass, fnote) = ComputeFilterPass(code, snap, snap.PrevGrossMargin, snap.PrevOcf, yjyg);
            row.F["filter_pass"] = fpass ? "是" : $"否({fnote})";
            row.F["price_above_ma60"] = aboveMa20 ? "是" : "否";
            row.F["has_insurance"] = info.Insurance ? "是" : "";
            row.F["has_social_security"] = info.SocialSecurity ? "是" : "";
            row.F["has_pension"] = info.Pension ? "是" : "";
            row.F["has_qfii"] = info.Qfii ? "是" : "";
            row.F["inst_detail"] = info.Detail;
            row.F["sort_value"] = sortVal.ToString("0.000", Inv);
            results.Add(row);
        }

        L($"[C] 完成：{results.Count} 只来自 T4 PASS");
        return (results.OrderByDescending(r => r.Sort).ToList(), passed.Count, "");
    }

    /// <summary>个股当前价格是否在 N 日均线上方（不足 N 根K线视为否）。C 桶 MA20 仅提示，不构成过滤。</summary>
    private async Task<bool> CheckPriceAboveMaAsync(string code, int window, CancellationToken ct)
    {
        try
        {
            var bars = await _klines.GetStockDailyAsync(code, window);
            if (bars is null || bars.Count < window) return false;
            var ma = bars.TakeLast(window).Average(b => b.Close);
            return bars[^1].Close > ma;
        }
        catch
        {
            return false;
        }
    }

    // ── 机构持仓（险资/社保/养老金/QFII） ──────────────────────────

    private sealed record InstInfo(
        bool Insurance, bool SocialSecurity, bool Pension, bool Qfii, string Detail, int Count);

    /// <summary>识别股东属于哪类机构（与 Python _classify_holder 同口径：养老先判、保险排除政府养老）。</summary>
    private static HashSet<string> ClassifyHolder(string name)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var isPension = PensionKw.Any(k => name.Contains(k));
        if (!isPension && InsuranceKw.Any(k => name.Contains(k))) tags.Add("保险");
        if (isPension) tags.Add("养老");
        if (SocialSecurityKw.Any(k => name.Contains(k))) tags.Add("社保");
        if (QfiiKw.Any(k => name.Contains(k))) tags.Add("QFII");
        return tags;
    }

    /// <summary>批量拉取十大流通股东并分类（逐票 0.15s 防限频，与 Python 一致）。</summary>
    private async Task<Dictionary<string, InstInfo>> FetchInstitutionalHoldersAsync(
        IReadOnlyList<string> codes, Action<string> L, CancellationToken ct)
    {
        L($"拉取机构持仓（{codes.Count} 只）…");
        var result = new Dictionary<string, InstInfo>(StringComparer.Ordinal);
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            var tags = new HashSet<string>(StringComparer.Ordinal);
            var parts = new List<string>();
            try
            {
                foreach (var h in await _em.GetHoldersAsync(code, ct))
                {
                    var t = ClassifyHolder(h.Name);
                    if (t.Count == 0) continue;
                    tags.UnionWith(t);
                    parts.Add($"{h.Name}[{string.Join("/", t)}]");
                }
            }
            catch
            {
                // 单票失败不影响整体
            }
            result[code] = new InstInfo(tags.Contains("保险"), tags.Contains("社保"),
                tags.Contains("养老"), tags.Contains("QFII"),
                parts.Count > 0 ? string.Join("; ", parts) : "", tags.Count);
            if ((i + 1) % 20 == 0) L($"  机构持仓 {i + 1}/{codes.Count}");
            await Task.Delay(150, ct);
        }
        L("机构持仓拉取完成");
        return result;
    }

    // ── 订单积压指标（合同负债/存货/应收账款，逐票拉资产负债表） ───────

    /// <summary>订单积压原始指标（归一化前）。DRR 为比值，DRGS/IBR/ARR 为增速差(pct)。
    /// ContractLiabYoy 为合同负债同比原值（填实 C 桶既有 contract_liability_yoy 占位列）。</summary>
    private sealed record OrderBacklogRaw(
        string Code, double? Drr, double? Drgs, double? Ibr, double? Arr,
        double? ContractLiabYoy);

    /// <summary>逐票拉资产负债表，计算订单积压原始指标（B/C 桶共用）。
    /// DRR=期末合同负债/TTM营收；DRGS=合同负债同比−营收同比；IBR=存货同比−营收同比；
    /// ARR=应收账款同比−营收同比。资产负债表 *_YOY 字段直接给同比，省去自算两年值。
    /// 失败/缺数据 → 对应字段 null（后续归一化跳过、输出留空）。</summary>
    private async Task<Dictionary<string, OrderBacklogRaw>> FetchOrderBacklogAsync(
        IReadOnlyList<string> codes, Snapshots snap, Action<string> L, CancellationToken ct)
    {
        L($"拉取资产负债表（{codes.Count} 只，逐票）…");
        var result = new Dictionary<string, OrderBacklogRaw>(StringComparer.Ordinal);
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            double? contractLiab = null, contractLiabYoy = null, inventoryYoy = null, accountsReceYoy = null;
            try
            {
                // 资产负债表按报告期降序，取最新一期
                var bs = await _em.GetBalanceSheetAsync(code, L, ct);
                if (bs.Count > 0)
                {
                    var latest = bs[0];
                    contractLiab = latest.ContractLiab;
                    contractLiabYoy = latest.ContractLiabYoy;
                    inventoryYoy = latest.InventoryYoy;
                    accountsReceYoy = latest.AccountsReceYoy;
                }
            }
            catch
            {
                // 单票失败不影响整体，字段留 null
            }

            // 营收同比（yjbb rev_yoy，pct）
            double? revYoy = snap.Yjbb.TryGetValue(code, out var y) ? y.RevYoy : null;

            // DRR = 期末合同负债 / TTM营收（TTM 来自快照；合同负债或 TTM 缺失 → null）
            double? drr = null;
            if (contractLiab is { } cl && snap.TtmRevenue.TryGetValue(code, out var ttm) && ttm > 0)
                drr = cl / ttm;

            // DRGS/IBR/ARR = 各科目同比 − 营收同比（同比接口直接给，pct 口径）
            double? drgs = contractLiabYoy is { } cy && revYoy is { } ry ? cy - ry : null;
            double? ibr = inventoryYoy is { } iy && revYoy is { } ry2 ? iy - ry2 : null;
            double? arr = accountsReceYoy is { } ay && revYoy is { } ry3 ? ay - ry3 : null;

            result[code] = new OrderBacklogRaw(code, drr, drgs, ibr, arr, contractLiabYoy);
            if ((i + 1) % 20 == 0) L($"  资产负债表 {i + 1}/{codes.Count}");
            await Task.Delay(150, ct); // 与机构持仓同节奏防限频
        }
        L("资产负债表拉取完成");
        return result;
    }

    /// <summary>批次内 min-max 归一化到 0~100 并加权得 order_backlog_score。
    /// DRR/DRGS/IBR 各自在批次内归一化（缺值跳过，不参与 min/max 计算）。
    /// 权重：DRR 0.4 / DRGS 0.4 / IBR 0.2。ARR 仅展示不进得分。
    /// 返回 code → (score 0~100, 是否有足够数据计算)。</summary>
    private static Dictionary<string, (double Score, bool HasData)> NormalizeAndScore(
        Dictionary<string, OrderBacklogRaw> raw)
    {
        static (double min, double max) Range(IEnumerable<double?> vals)
        {
            var present = vals.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (present.Count == 0) return (0, 0);
            return (present.Min(), present.Max());
        }
        static double Norm(double? v, double min, double max) =>
            v is { } x && max > min ? (x - min) / (max - min) * 100 : 50; // 单值/缺值给中位

        var (drrMin, drrMax) = Range(raw.Values.Select(r => r.Drr));
        var (drgsMin, drgsMax) = Range(raw.Values.Select(r => r.Drgs));
        var (ibrMin, ibrMax) = Range(raw.Values.Select(r => r.Ibr));

        var scores = new Dictionary<string, (double, bool)>(StringComparer.Ordinal);
        foreach (var (code, r) in raw)
        {
            var hasData = r.Drr.HasValue || r.Drgs.HasValue || r.Ibr.HasValue;
            if (!hasData) { scores[code] = (0, false); continue; }
            var score = 0.4 * Norm(r.Drr, drrMin, drrMax)
                      + 0.4 * Norm(r.Drgs, drgsMin, drgsMax)
                      + 0.2 * Norm(r.Ibr, ibrMin, ibrMax);
            scores[code] = (score, true);
        }
        return scores;
    }

    /// <summary>三道过滤（准入参考，不剔除）：毛利率同比未大幅下滑(>-3pct)、
    /// 营收预告正(>0)、经营现金流改善。返回 (是否全过, 未过的说明)。</summary>
    private static (bool Pass, string Note) ComputeFilterPass(
        string code, Snapshots snap, Dictionary<string, double> prevGrossMargin,
        Dictionary<string, double> prevOcf, IReadOnlyList<YjygRow> yjyg)
    {
        var notes = new List<string>();

        // 过滤1：毛利率同比 > -3pct（需上年同期毛利率，缺则放行）
        if (snap.Yjbb.TryGetValue(code, out var y) && y.GrossMargin is { } gm
            && prevGrossMargin.TryGetValue(code, out var prevGm))
        {
            if (gm - prevGm < -3.0) notes.Add("毛利率同比下滑>3pct");
        }

        // 过滤2：营收增速预期 > 0（业绩预告正面 gain_pct > 0；无预告则放行）
        var yg = yjyg.FirstOrDefault(r => r.Code == code);
        if (yg is not null && yg.GainPct is { } g && g <= 0) notes.Add("营收预告非正");

        // 过滤3：经营现金流改善（最新 ocf_ps >= 上年同期；缺则放行）
        if (y?.OcfPs is { } ocf && prevOcf.TryGetValue(code, out var prevO))
        {
            if (ocf < prevO) notes.Add("经营现金流同比恶化");
        }

        return notes.Count == 0 ? (true, "") : (false, string.Join("；", notes));
    }

    /// <summary>毛利率同比变化（pct，最新 − 上年同期；缺则空串）。</summary>
    private static string gm2(YjbbRow y, Dictionary<string, double> prevGm, string code)
    {
        if (y.GrossMargin is { } gm && prevGm.TryGetValue(code, out var p))
            return (gm - p).ToString("0.0", Inv);
        return "";
    }

    /// <summary>每股经营现金流同比变化（最新 − 上年同期；缺则空串）。</summary>
    private static string ocfYoy(YjbbRow y, Dictionary<string, double> prevOcf, string code)
    {
        if (y.OcfPs is { } ocf && prevOcf.TryGetValue(code, out var p))
            return (ocf - p).ToString("0.00", Inv);
        return "";
    }

    // ── 输出组装（skill_input_T6_{X}.md 格式对齐 Python assemble_bucket） ──

    private sealed class Row
    {
        public double Sort;
        public readonly Dictionary<string, string> F = new(StringComparer.Ordinal);
    }

    private void WriteBucket(string letter, List<Row> rows, List<string> outputs, Action<string>? log)
    {
        var (allCols, mdCols) = letter switch
        {
            "A" => (ACols, AMdCols),
            "B" => (BCols, BMdCols),
            _ => (CCols, CMdCols),
        };
        var mdPath = Path.Combine(_dataDir, $"skill_input_T6_{letter}.md");
        File.WriteAllText(mdPath, AssembleBucket(letter, rows, mdCols), new UTF8Encoding(false));
        outputs.Add($"skill_input_T6_{letter}.md");
        log?.Invoke($"[T6-{letter}] 输入文件已生成：{mdPath}");
        if (rows.Count > 0)
        {
            var csvPath = Path.Combine(_dataDir, $"candidates_{letter}.csv");
            File.WriteAllText(csvPath, ToCsv(allCols, rows), new UTF8Encoding(false));
            outputs.Add($"candidates_{letter}.csv");
            log?.Invoke($"[T6-{letter}] → {csvPath}");
        }
    }

    private string AssembleBucket(string letter, List<Row> rows, string[] mdCols)
    {
        // 生成日期显式注入：skill 模板要求输出标题带 YYYY-MM-DD，若不告知 LLM 今天的日期，
        // 它会拿训练数据里的旧日期来填（实测 A/B 桶输出写成 2024-05-20）
        var parts = new List<string> { $"=== BUCKET: {letter} ===", $"生成日期: {DateTime.Today:yyyy-MM-dd}" };
        if (letter == "A") parts.Add(RulesNoteA());
        else if (letter == "B") parts.Add(RulesNoteB());
        else if (letter == "C") parts.Add(RulesNoteC());
        parts.Add(rows.Count == 0 ? $"（{letter} 桶候选为空）" : ToCsv(mdCols, rows));
        parts.Add("");
        parts.Add($"=== YAML_TAG: {StrategyConfig.YamlTag} ===");
        return string.Join("\n", parts);
    }

    private string RulesNoteA() => string.Join("\n", new[]
    {
        $"筛选规则: 中证红利成分 + 股息率TTM≥{_th.MinDy:0.0}% + PB≤{_th.MaxPb:0.0} + ROE≥{_th.MinRoeA:0.0}%"
        + "（ROE 为最新报告期年化近似，非5年均值）"
        + " + 近3年无单季亏损；年报每股经营现金流为负（借钱分红嫌疑）不剔除，仅在 pick_reason 标⚠️显眼提示，请人工/LLM 复核"
        + "；数据缺失时放行，见 pick_reason",
        "排序公式: sort_value = 股息率TTM × quality_score（quality_score 含 ROE 权重）",
    });

    private string RulesNoteB() => string.Join("\n", new[]
    {
        // 2026-09-02 巴菲特式指标集（参考 巴菲特式财务指标筛选标准.md）：
        // 批量可得的进硬门槛，不可得的标"缺少"交给 LLM 复核
        "筛选规则(巴菲特式财务指标·批量可得部分): 中证1000+500+A500+800成分(剔ST/退) + 总市值≥"
        + $"{_th.MinMv:0}亿"
        + $" + 净利3年CAGR≥{_th.MinNpCagr:0}% + 营收3年CAGR≥{_th.MinRevCagr:0}%"
        + " + 毛利率4个年报期不趋势下滑（定价权代理）"
        + $" + ROE年化≥{_th.MinRoeB:0}%（标准线15%的批量近似）"
        + " + 近3年无单季亏损（ROE稳定性代理）"
        + $" + 最新年报OCF/净利≥{_th.MinOcfRatio:0.0}（现金含金量，标准线1.0的单年近似）"
        + $" + 0<PE(TTM)≤{_th.MaxPe:0}（估值兜底）"
        + "；核心数据缺失即剔除，每只票见 pick_reason",
        "排序公式: sort_value = min(净利CAGR,100)/PE（即 1/PEG）降序",
        "巴菲特式扩展列（东财 MAINFINADATA/GCASHFLOW 接口逐票填充，2026-09-02 起）:",
        "  roic = ROIC%（最新年报，标准线≥12）；debt_ratio = 资产负债率%（标准线≤50，非金融）",
        "  interest_coverage = 利息保障倍数（标准线≥8x）；bvps_cagr = 每股净资产多年复合增速%（标准线≥10）",
        "  fcf_margin = FCF利润率%（最新年报(OCF−Capex)/TTM营收，标准线≥10）",
        "  capex_intensity = Capex强度%（Capex/经营现金流，标准线≤30，越低越轻资产）",
        "  owner_earnings = 所有者收益（元，净利+折旧摊销−Capex，巴菲特估值锚点，为正且增长为佳）",
        "  以上任一数据源未覆盖仍填「缺少」——请你在复核时结合文本证据评估该维度，无法判断就照实写证据不足，不要编造数值",
        "复核重点（巴菲特式业务质量）: 护城河（定价权/网络效应/转换成本/成本优势/牌照）、"
        + "业务持久性（10-20年需求仍在）、增长连贯性（逐年序列）、竞争格局（理性竞争 vs 价格战）；"
        + "周期性/爆发性标的（利润靠涨价而非量增）请在复核时识别并降档——财务指标无法完全区分",
        "np_yoy_by_year/rev_yoy_by_year = 4 个年报期逐年同比（'年份:增速' 拼接），扭亏年份不含——"
        + "用于评估增长连贯性（一年爆发撑起 CAGR 的伪成长请你在复核时识别）",
        "gross_margin_by_year = 4 个年报期毛利率逐年值，gm_trend = 末期−基期（正=定价权稳固）",
        "订单积压参考列（LLM 复核用，不进硬门槛/排序，数据缺失留空）:",
        "  drr = 期末合同负债/TTM营收，越大越好（订单积压待交付越充足）",
        "  drgs = 合同负债同比−营收同比，越大越好（合同负债增速快于营收=订单加速积压）",
        "  ibr = 存货同比−营收同比，适度为好（过大=滞销囤货风险，过小=无货可交）",
        "  arr = 应收账款同比−营收同比，越小越好（应收增速超营收=降价赊销/回款恶化）",
        "  order_backlog_score = 0.4×DRR+0.4×DRGS+0.2×IBR（批次内min-max归一化0~100），越大越好",
        "  filter_pass = 三道过滤是否全过（毛利率同比>-3pct / 营收预告正 / 现金流改善），全过为好",
        "  gross_margin/gross_margin_yoy/rev_yoy_latest/ocf_yoy = 最新毛利率/毛利率同比/营收同比/现金流同比",
    });

    private static string RulesNoteC() => string.Join("\n", new[]
    {
        "筛选规则: T4 文本判定 PASS（≥1类命中即可）→ 补 yjbb 增速/毛利率 + 动态PE + MA20（仅提示，不构成过滤）",
        "排序公式: sort_value = min(净利同比,500) + 机构持仓×10 降序（文本得分仅初筛，不进排序）",
        "订单积压参考列（LLM 复核用，不进排序，数据缺失留空）:",
        "  drr = 期末合同负债/TTM营收，越大越好（订单积压待交付越充足）",
        "  drgs = 合同负债同比−营收同比，越大越好（合同负债增速快于营收=订单加速积压）",
        "  ibr = 存货同比−营收同比，适度为好（过大=滞销囤货风险，过小=无货可交）",
        "  arr = 应收账款同比−营收同比，越小越好（应收增速超营收=降价赊销/回款恶化）",
        "  order_backlog_score = 0.4×DRR+0.4×DRGS+0.2×IBR（批次内min-max归一化0~100），越大越好",
        "  filter_pass = 三道过滤是否全过（毛利率同比>-3pct / 营收预告正 / 现金流改善），全过为好",
        "  contract_liability_yoy = 合同负债同比原值（验证景气文本是否被预收款数据印证）",
        "需 LLM/人工复核: price_index_1y_high（行业价格指数是否创1年新高）",
    });

    /// <summary>rows → CSV 文本（与 pandas to_csv 等价：表头 + 数据行，含转义）。</summary>
    private static string ToCsv(IReadOnlyList<string> cols, List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(Csv)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", cols.Select(c => Csv(r.F.GetValueOrDefault(c, "")))));
        return sb.ToString();
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    // ── 报告 section 辅助 ─────────────────────────────────────────

    private static string BucketSection(string letter, List<Row> rows, int scanned, string dropNote)
    {
        var head = rows.Count == 0
            ? $"扫描 {scanned} 只，0 只通过硬门槛。"
            : $"扫描 {scanned} 只，{rows.Count} 只通过硬门槛，按排序值取 Top {rows.Count}。";
        if (dropNote.Length > 0) head += $"\n\n剔除分布：{dropNote}";
        return head + "\n\n" + (letter switch
        {
            "A" => TopTable(rows,
                new[] { "代码", "名称", "股息率%", "PB", "ROE年化%", "质量分", "排序值" },
                r => new[] { r.F["code"], r.F["name"], r.F["dividend_yield_ttm"], r.F["pb"],
                    r.F["roe_5y_avg"], r.F["quality_score"], r.F["sort_value"] }),
            "B" => TopTable(rows,
                new[] { "代码", "名称", "总市值(亿)", "净利CAGR%", "营收CAGR%", "ROE%", "OCF/净利", "毛利率趋势", "PE", "排序值" },
                r => new[] { r.F["code"], r.F["name"], r.F["total_mv_yi"], r.F["profit_cagr_3y"],
                    r.F["revenue_cagr_3y"], r.F["roe_ann"], r.F["ocf_to_np"], r.F["gm_trend"],
                    r.F["pe_ttm"], r.F["sort_value"] }),
            _ => TopTable(rows,
                new[] { "代码", "名称", "净利同比%", "动态PE", "MA20上", "排序值" },
                r => new[] { r.F["code"], r.F["name"], r.F["np_yoy"], r.F["pe_dynamic"],
                    r.F["price_above_ma60"], r.F["sort_value"] }),
        });
    }

    private static string TopTable(List<Row> rows, string[] header, Func<Row, string[]> cells, int n = 10)
    {
        if (rows.Count == 0) return "_（空）_\n";
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", header) + " |");
        sb.AppendLine("| " + string.Join(" | ", header.Select(_ => "---")) + " |");
        foreach (var r in rows.Take(n))
            sb.AppendLine("| " + string.Join(" | ", cells(r)) + " |");
        if (rows.Count > n)
            sb.AppendLine($"_（仅展示前 {n} / {rows.Count} 只，全量见 candidates CSV）_");
        return sb.ToString();
    }

    /// <summary>LLM 判定条目字段（stock_code 等可能为字符串或数字）。</summary>
    private static string JStr(JsonElement o, string prop)
    {
        if (!o.TryGetProperty(prop, out var e)) return "";
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Number => e.GetRawText(),
            _ => "",
        };
    }
}
