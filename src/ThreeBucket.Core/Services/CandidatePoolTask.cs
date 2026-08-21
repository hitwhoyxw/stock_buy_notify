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
/// - C 桶（热点周期）：T4 文本判定 PASS → yjbb 补充增速/毛利率 → 动态PE + MA60 → 增速排序
///
/// 产出：data/skill_input_T6_{A,B,C}.md（LLM 消费，格式对齐 skills/t6_semantic_ranking.md）
/// + data/candidates_{A,B,C}.csv（直接查看）。每桶按排序值截断 Top 100（LLM 分析上限）。
///
/// 阈值硬编码自 yaml v1.0（bucket_A.stock_filters / bucket_B.batch_screen）。
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
    private readonly KlineService _klines;

    public CandidatePoolTask(string dataDir, CsIndexClient csi, EastMoneyClient em,
        TencentSnapshot tencent, KlineService klines)
    {
        _dataDir = dataDir;
        _csi = csi;
        _em = em;
        _tencent = tencent;
        _klines = klines;
    }

    // ── 阈值（yaml v1.0 硬编码） ───────────────────────────────────

    private const double MinDy = 3.0;      // A桶 股息率TTM下限 %
    private const double MaxPb = 2.0;      // A桶 PB 上限
    private const double MinRoeA = 10.0;   // A桶 ROE 年化下限 %
    private const double MinMv = 50.0;     // B桶 总市值下限（亿）
    private const double MinNpCagr = 20.0; // B桶 净利3年CAGR下限 %
    private const double MinRevCagr = 15.0;// B桶 营收3年CAGR下限 %
    private const double MinNpYoy = 15.0;  // B桶 最新期净利同比下限 %
    private const double MinRoeB = 8.0;    // B桶 ROE 年化下限 %
    private const double MinOcfRatio = 0.5;// B桶 年报 OCF/NP 下限
    private const double MaxPe = 60.0;     // B桶 PE(TTM) 上限
    private const double MaxPeg = 1.2;     // B桶 PEG 上限
    private const int TopN = 100;          // 每桶 LLM 分析上限

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
        "profit_cagr_3y", "revenue_cagr_3y", "np_yoy_latest",
        "roe_ann", "ocf_to_np", "loss_q_3y", "pe_ttm", "peg",
        "has_insurance", "has_social_security", "has_pension", "has_qfii",
        "inst_detail", "sort_value", "pick_reason",
    };
    private static readonly string[] BMdCols = BCols.Where(c => c != "inst_detail").ToArray();

    private static readonly string[] CCols =
    {
        "code", "name", "industry", "text_score", "categories_hit_count",
        "np_yoy", "revenue_yoy", "gross_margin",
        "pe_ttm", "pe_dynamic", "pe_method", "peg",
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
            aRows = aRows.Take(TopN).ToList();
            bRows = bRows.Take(TopN).ToList();
            cRows = cRows.Take(TopN).ToList();
            L($"三桶候选：A {aRows.Count}/{aScanned}、B {bRows.Count}/{bScanned}、"
              + $"C {cRows.Count}/{cScanned}（C 截前 {cBefore}，上限 Top{TopN}）");

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

    private sealed record GrowthRow(double? NpCagr, double? RevCagr, double? OcfNpRatio, double? OcfPsAnnual);
    private sealed record QualityRow(int LossQ, double? OcfPsAnnual);
    private sealed record Snapshots(
        Dictionary<string, GrowthRow> Growth,
        Dictionary<string, QualityRow> Quality,
        Dictionary<string, YjbbRow> Yjbb,
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
                growth[code] = new GrowthRow(npCagr, revCagr, ocfRatio, l.OcfPs);
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

        // — 最新报告期 yjbb —
        var yjbbRows = await _em.GetYjbbAsync(period, L, ct);
        var yjbb = yjbbRows.GroupBy(r => r.Code)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return new Snapshots(growth, quality, yjbb, period);
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
            if (dy is { } d && d != 0 && d < MinDy) continue;
            if (pb is { } p && p != 0 && p > MaxPb) continue;
            if (roe is { } ro && ro < MinRoeA) continue;

            // 盈利质量：近3年单季亏损 → 剔除；年报经营现金流为负（借钱分红）→ 剔除
            snap.Quality.TryGetValue(c.Code, out var q);
            if (q is { LossQ: > 0 }) continue;
            if (q?.OcfPsAnnual is < 0) continue;

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
                dy is { } d2 && d2 != 0 ? $"股息率{d2:0.00}%≥{MinDy:0.0}%" : "股息率缺失放行",
                pb is { } p2 && p2 != 0 ? $"PB {p2:0.00}≤{MaxPb:0.0}" : "PB缺失放行",
                roe is { } r2 ? $"ROE年化{r2:0.0}%≥{MinRoeA:0.0}%" : "ROE缺失放行",
                q is not null ? $"近3年亏损季度{q.LossQ}" : "亏损数据缺失放行",
                q?.OcfPsAnnual is { } oq ? $"年报经营现金流/股{oq:0.00}≥0" : "现金流数据缺失放行",
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

        var dropped = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["基本面缺失"] = 0, ["市值不足"] = 0, ["净利CAGR"] = 0, ["营收CAGR"] = 0,
            ["最新期增速"] = 0, ["ROE"] = 0, ["亏损季度"] = 0, ["现金流"] = 0,
            ["PE"] = 0, ["PEG"] = 0,
        };
        var results = new List<Row>();

        foreach (var (code, name) in consMap)
        {
            if (!fund.TryGetValue(code, out var f))
            {
                dropped["基本面缺失"]++;
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
            if (totalMv is null || totalMv < MinMv)
            {
                dropped["市值不足"]++;
                continue;
            }

            // 成长门槛：CAGR 缺失（基期为负/缺披露）直接剔除，不放行
            snap.Growth.TryGetValue(code, out var g);
            var npCagr = g?.NpCagr;
            var revCagr = g?.RevCagr;
            var ocfRatio = g?.OcfNpRatio;
            var ocfPsA = g?.OcfPsAnnual;
            if (npCagr is null || npCagr < MinNpCagr)
            {
                dropped["净利CAGR"]++;
                continue;
            }
            if (revCagr is null || revCagr < MinRevCagr)
            {
                dropped["营收CAGR"]++;
                continue;
            }

            // 最新报告期净利同比（成长未熄火）；快照缺该票则剔除
            var npYoy = y?.NpYoy;
            var industry = y?.Industry ?? "";
            if (npYoy is null || npYoy < MinNpYoy)
            {
                dropped["最新期增速"]++;
                continue;
            }

            // ROE 年化门槛
            if (roe is null || roe < MinRoeB)
            {
                dropped["ROE"]++;
                continue;
            }

            // 盈利质量：近 3 年单季亏损（缺失同样剔除——核心数据缺失即剔除）
            snap.Quality.TryGetValue(code, out var q);
            var lossQ = (int?)q?.LossQ;
            if (lossQ is null || lossQ > 0)
            {
                dropped["亏损季度"]++;
                continue;
            }

            // 现金流：年报 OCF/NP 与每股 OCF
            if (ocfRatio is null || ocfRatio < MinOcfRatio || ocfPsA is null || ocfPsA <= 0)
            {
                dropped["现金流"]++;
                continue;
            }

            // 估值：PE 区间 + PEG
            if (pe is null || pe <= 0 || pe > MaxPe)
            {
                dropped["PE"]++;
                continue;
            }
            var cagrCapped = Math.Min(npCagr.Value, 100.0);
            var peg = pe.Value / cagrCapped;
            if (peg > MaxPeg)
            {
                dropped["PEG"]++;
                continue;
            }

            var sortVal = cagrCapped / pe.Value; // = 1/PEG
            var reason =
                $"总市值{totalMv:0}亿≥{MinMv:0} | " +
                $"净利CAGR3年+{npCagr:0}%≥{MinNpCagr:0}% | " +
                $"营收CAGR3年+{revCagr:0}%≥{MinRevCagr:0}% | " +
                $"最新期净利同比+{npYoy:0}%≥{MinNpYoy:0}% | " +
                $"ROE年化{roe:0.0}%≥{MinRoeB:0}% | " +
                "近3年亏损季度0 | " +
                $"年报OCF/NP {ocfRatio:0.00}≥{MinOcfRatio} | " +
                $"PE(TTM){pe:0.0}≤{MaxPe:0} | PEG {peg:0.00}≤{MaxPeg}";

            var row = new Row { Sort = sortVal };
            row.F["code"] = code;
            row.F["name"] = name;
            row.F["industry"] = industry;
            row.F["price"] = price is { } pr ? pr.ToString("0.00", Inv) : "";
            row.F["total_mv_yi"] = totalMv.Value.ToString("0", Inv);
            row.F["profit_cagr_3y"] = npCagr.Value.ToString("0.0", Inv);
            row.F["revenue_cagr_3y"] = revCagr.Value.ToString("0.0", Inv);
            row.F["np_yoy_latest"] = npYoy.Value.ToString("0.0", Inv);
            row.F["roe_ann"] = roe.Value.ToString("0.0", Inv);
            row.F["ocf_to_np"] = ocfRatio.Value.ToString("0.00", Inv);
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

        var dropNote = string.Join("、", dropped.Where(kv => kv.Value > 0)
            .Select(kv => $"{kv.Key}{kv.Value}"));
        if (dropNote.Length > 0) L($"[B] 剔除分布: {dropNote}");

        return (results.OrderByDescending(r => r.Sort).ToList(), consMap.Count, dropNote);
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

            var aboveMa60 = await CheckPriceAboveMa60Async(code, ct);

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
            row.F["contract_liability_yoy"] = "";       // 需要财报，LLM 层验证
            row.F["price_above_ma60"] = aboveMa60 ? "是" : "否";
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

    /// <summary>个股当前价格是否在 MA60 上方（不足 60 根K线视为否）。</summary>
    private async Task<bool> CheckPriceAboveMa60Async(string code, CancellationToken ct)
    {
        try
        {
            var bars = await _klines.GetStockDailyAsync(code, 60);
            if (bars is not { Count: >= 60 }) return false;
            var ma = bars.TakeLast(60).Average(b => b.Close);
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

    private static string AssembleBucket(string letter, List<Row> rows, string[] mdCols)
    {
        var parts = new List<string> { $"=== BUCKET: {letter} ===" };
        if (letter == "A") parts.Add(RulesNoteA());
        else if (letter == "B") parts.Add(RulesNoteB());
        parts.Add(rows.Count == 0 ? $"（{letter} 桶候选为空）" : ToCsv(mdCols, rows));
        parts.Add("");
        parts.Add($"=== YAML_TAG: {StrategyConfig.YamlTag} ===");
        return string.Join("\n", parts);
    }

    private static string RulesNoteA() => string.Join("\n", new[]
    {
        $"筛选规则: 中证红利成分 + 股息率TTM≥{MinDy:0.0}% + PB≤{MaxPb:0.0} + ROE≥{MinRoeA:0.0}%"
        + "（ROE 为最新报告期年化近似，非5年均值）"
        + " + 近3年无单季亏损 + 最新年报每股经营现金流≥0（自由现金流近似，剔除借钱分红）"
        + "；数据缺失时放行，见 pick_reason",
        "排序公式: sort_value = 股息率TTM × quality_score（quality_score 含 ROE 权重）",
    });

    private static string RulesNoteB() => string.Join("\n", new[]
    {
        $"筛选规则: 中证1000+500+A500+800成分(剔ST/退) + 总市值≥{MinMv:0}亿"
        + $" + 净利3年CAGR≥{MinNpCagr:0}%(年报首末期,基期须为正)"
        + $" + 营收3年CAGR≥{MinRevCagr:0}%"
        + $" + 最新报告期净利同比≥{MinNpYoy:0}%"
        + $" + ROE年化≥{MinRoeB:0}%"
        + " + 近3年无单季亏损"
        + $" + 最新年报OCF/NP≥{MinOcfRatio}且每股OCF>0"
        + $" + 0<PE(TTM)≤{MaxPe:0} + PEG≤{MaxPeg}"
        + "（PEG=PE÷min(净利CAGR,100%)）；核心数据缺失即剔除，每只票见 pick_reason",
        "排序公式: sort_value = min(净利CAGR,100)/PE（即 1/PEG）降序",
        "批量层未覆盖、需 LLM/人工复核: 商誉/净资产、应收vs营收增速、研发占比、"
        + "行业渗透率、PE 上市以来分位",
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
                new[] { "代码", "名称", "总市值(亿)", "净利CAGR%", "PE", "PEG", "排序值" },
                r => new[] { r.F["code"], r.F["name"], r.F["total_mv_yi"], r.F["profit_cagr_3y"],
                    r.F["pe_ttm"], r.F["peg"], r.F["sort_value"] }),
            _ => TopTable(rows,
                new[] { "代码", "名称", "净利同比%", "动态PE", "MA60上", "排序值" },
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
