using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ThreeBucket.Core.Data;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T4 · 财报季文本扫描（C# 版，移植自 scripts/t4_ingest.py，桌面/移动端通用）。
///
/// RunAsync 串联两个阶段（Python 版拆成 --prepare / 默认 ingest 两种 CLI 模式）：
/// 1. ingest：data/skill_output_T4C.md 存在时解析 LLM 判定 JSON（三级降级：直接解析 →
///    ```json 围栏 → [...] 最大区间），PASS 条目写信号台账（桶C / C-TEXT-SCAN / 基准 000905）
/// 2. prepare：自动发现扫描池（赛道龙头 + 关键词命中 + 预告增速 + 报表增速补足，上限 300）
///    → data/skill_input_T4C.md（头部附板块热度榜：全市场净利同比≥50% 按行业聚合），交 LLM 复核
///
/// 关键词与阈值硬编码自 yaml v1.0（bucket_C.text_signal）；互动易文本无公开稳定接口，
/// 跳过（Python 受限环境同样缺失，扫描只用业绩预告文本）。
/// </summary>
public class EarningsScanTask : IBuiltinTask
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Key => "T4";
    public string Name => "财报季文本扫描";

    private readonly string _dataDir;
    private readonly EastMoneyClient _em;
    private readonly SignalLogStore _signals;

    public EarningsScanTask(string dataDir, EastMoneyClient em, SignalLogStore signals)
    {
        _dataDir = dataDir;
        _em = em;
        _signals = signals;
    }

    // ── bucket_C.text_signal 关键词（yaml v1.0 硬编码） ─────────────

    /// <summary>类别（名称, 权重, 关键词表）。加权分 = Σ(命中数×权重) − 3×反向词数。</summary>
    private static readonly (string Name, double Weight, string[] Keywords)[] Categories =
    {
        ("demand", 1.2, new[]
        {
            // — 订单景气 —
            "供不应求","订单饱满","在手订单充足","订单量增加","订单稳步增长","订单充足","订单大幅增长",
            "订单同比增长","订单同比大增","新签订单","新增订单","追加订单","订单能见度","订单可见度",
            // — 需求景气 —
            "需求旺盛","需求强劲","需求大增","需求高景气","需求持续增长","需求增长","需求回暖","需求复苏",
            "需求改善","需求超预期","需求扩张","市场需求增长","供需缺口","供需错配","供小于求",
            // — 排产/交付 —
            "下游排产满负荷","产能已排至","排产紧张","排产饱满","交付周期延长","交付周期拉长","交付压力大",
            // — 客户拓展 —
            "客户提前锁量","客户加单","客户追加订单","新增客户","新客户导入",
            // — 市场/份额 —
            "市场超预期拓展","新品上市持续超预期","市场占有率提升","市占率提升","市场份额提升",
            // — 量能 —
            "出货量增长","销量增长","销量大增","产销两旺",
            // — 海外 —
            "出口增长","海外需求增长",
        }),
        ("price", 1.5, new[]
        {
            // — 涨价/提价 —
            "涨价","涨价函","提价","提价函","调价函","调价通知","价格上涨","价格持续上涨","价格稳步回升",
            "价格回升","价格上行","价格走强","价格上调","上调价格","价格调整","调价","价格中枢上移",
            "产品价格中枢持续上涨",
            // — 量价 —
            "量价齐升","量增价涨",
            // — 均价/加工费 —
            "销售均价","均价提升","均价上涨","均价上调","加工费上涨","加工费上调","加工费提升","出货均价",
            // — 议价/定价权 —
            "议价能力提升","议价权","定价权","定价能力强",
            // — 价差/合同 —
            "价差扩大","长协价上调","合同价高于现货价","合同价上涨","签约价上涨",
            // — 成本传导 —
            "成本传导","成本转嫁",
            // — 海外价格 —
            "出口价格上涨","海外定价上调",
            // — 稀缺溢价 —
            "稀缺性溢价",
        }),
        ("supply", 1.3, new[]
        {
            // — 供给偏紧 —
            "供给偏紧","供应偏紧","产能紧张","产能不足","产能饱和","产能受限","产能瓶颈","产能缺口",
            // — 出清 —
            "行业出清","老旧产能退出","落后产能退出","产能退出",
            // — 扩产受限 —
            "新增产能有限","扩产周期长","产能投放放缓","产能扩张放缓","产能爬坡",
            // — 限制/配额 —
            "限产","牌照受限","配额制","配额管理","环保限产","能耗双控","双高限制",
            // — 运力 —
            "有效运力下降","运力紧张","运力不足",
            // — 利用率/满负荷 —
            "产能利用率","满产满销","满负荷运转","高负荷运转","满产","产能满载","满载运行",
            // — 库存低位 —
            "库存低位","库存去化","低库存","库存去化明显",
        }),
    };

    private static readonly string[] NegKeywords =
    {
        // — 扩产/产能释放 —
        "积极扩产","新增产能投放","产能大幅扩张","产能释放","产能投放加速","产能集中投放",
        // — 竞争恶化 —
        "行业竞争加剧","竞争恶化","价格战","内卷","恶性竞争",
        // — 降价 —
        "以价换量","价格承压","价格下行","价格下跌","降价","降价促销","价格松动","价格回落",
        // — 需求弱 —
        "需求偏弱","需求疲软","需求下滑","需求不及预期","需求放缓","需求萎缩",
        // — 库存压力 —
        "控制库存","库存积压","库存高企","去库存","库存压力",
        // — 观望 —
        "下游观望情绪","下游观望","采购谨慎",
        // — 开工率低 —
        "产能利用率下降","产能闲置","开工率不足","开工率下降",
    };

    private const double NegPenalty = -3.0;
    private const double LlmReviewScore = 6.0;  // min_weighted_score（LLM 复核口径）
    private const double MinGain = 50.0;        // 预告/报表净利同比门槛（%）
    private const int TopN = 300;               // 扫描池上限（安全阀）

    // 赛道龙头（yaml bucket_C.text_signal.sector_leaders；CPO/光模块/PCB/AI 链）
    private static readonly string[] SectorLeaders =
        { "300308", "300502", "300394", "002281", "000988", "002463", "002916", "600183", "300476" };

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string msg) => log?.Invoke($"[T4] {msg}");
        try
        {
            var today = TradingCalendar.NowCn();
            var sections = new List<(string, string)>();
            var alerts = new List<RiskAlert>();
            var passedCount = 0;

            // ── Phase 2 先行：ingest（LLM 产出存在时消费 → PASS 写台账） ──
            var outputPath = Path.Combine(_dataDir, "skill_output_T4C.md");
            if (File.Exists(outputPath))
            {
                var (passed, rejected) = Ingest(outputPath, today, L);
                passedCount = passed.Count;
                if (passed.Count > 0)
                {
                    sections.Add(("通过文本判定（纳入候选池）", PassTable(passed)));
                    alerts.Add(new RiskAlert("P1", "C-TEXT-SCAN", "C", "财报季文本扫描",
                        $"{passed.Count} 只通过景气判定", $"加权分 ≥{LlmReviewScore:0.0}",
                        "纳入 C 桶候选池观察", "T4 LLM 判定（skill_output_T4C.md）"));
                }
                if (rejected.Count > 0)
                    sections.Add(("未通过文本判定", RejectTable(rejected)));
                L($"ingest：PASS={passed.Count} REJECT={rejected.Count}");
            }
            else
            {
                L("skill_output_T4C.md 不存在，跳过 ingest（首次运行或 LLM 尚未产出）");
            }

            // ── Phase 1：prepare（自动发现扫描池 → skill_input_T4C.md） ──
            var inputPath = Path.Combine(_dataDir, "skill_input_T4C.md");
            var (poolCount, kwCount) = await PrepareAsync(inputPath, today, L, ct);

            sections.Add(("下一步",
                $"扫描池 {poolCount} 只（其中关键词命中 {kwCount} 只）已写入 `skill_input_T4C.md`。\n\n"
                + "请将文件内容喂给 LLM（参考 skills/t4_c_text_scan.md），"
                + "产出 JSON 数组写回 data/skill_output_T4C.md 后重跑本任务入账。\n"));

            var path = ReportBuilder.WriteReport(_dataDir, "T4",
                $"T4 财报季文本扫描 · {today:yyyy-MM-dd}", sections, alerts);
            L($"报告已写入 {path}");
            return new TaskRunResult(true, path, alerts.Count,
                $"ingest PASS {passedCount} 条；扫描池 {poolCount} 只已生成 skill_input_T4C.md");
        }
        catch (Exception ex)
        {
            return new TaskRunResult(false, "", 0, $"T4 失败: {ex.Message}");
        }
    }

    // ── Phase 2：ingest ────────────────────────────────────────────

    /// <summary>读 LLM 产出 → PASS 条目写台账。返回（通过, 淘汰）。</summary>
    private (List<JsonElement> Passed, List<JsonElement> Rejected) Ingest(string path, DateTime today, Action<string> L)
    {
        var results = ParseLlmOutput(File.ReadAllText(path));
        if (results.Count == 0)
        {
            L("无法从 skill_output_T4C.md 解析 JSON 数组，跳过 ingest");
            return (new(), new());
        }

        var passed = new List<JsonElement>();
        var rejected = new List<JsonElement>();
        foreach (var item in results)
            (Str(item, "verdict").ToUpperInvariant() == "PASS" ? passed : rejected).Add(item);

        foreach (var item in passed)
        {
            var sid = _signals.AppendSignal(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["触发日期"] = today.ToString("yyyy-MM-dd"),
                ["yaml_version_at_trigger"] = StrategyConfig.YamlTag,
                ["触发任务"] = "T4",
                ["桶"] = "C",
                ["规则ID"] = "C-TEXT-SCAN",
                ["标的代码"] = Str(item, "stock_code"),
                ["标的名称"] = Str(item, "stock_name"),
                ["申万一级行业"] = Str(item, "industry"),
                ["分桶基准代码"] = "000905", // 中证 500 作为 C 桶通用基准
                ["触发时指标值"] = Str(item, "weighted_score"),
                ["阈值"] = "6.0",
                ["当时组合状态"] = "",
                ["信号方向"] = "买入候选",
                ["建议动作"] = "纳入 C 桶候选池观察",
                ["是否实际执行"] = "",
                ["备注"] = Clip(Str(item, "reason"), 200),
            });
            L($"[OK] {sid} | {Str(item, "stock_code")} {Str(item, "stock_name")} score={Str(item, "weighted_score")}");
        }
        return (passed, rejected);
    }

    /// <summary>三级降级解析 JSON 数组：直接解析 → ```json 围栏 → [..] 最大区间（T6 C 桶复用）。</summary>
    internal static List<JsonElement> ParseLlmOutput(string text)
    {
        text = text.Trim();

        // 1. 整体直接是 JSON 数组
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (JsonException) { /* 进入下一级 */ }

        // 2. markdown 代码围栏
        foreach (Match m in Regex.Matches(text, @"```(?:json)?\s*\n(.*?)\n```", RegexOptions.Singleline))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            catch (JsonException) { continue; }
        }

        // 3. 首个 [ 到最后一个 ] 的最大区间
        var s = text.IndexOf('[');
        var e = text.LastIndexOf(']');
        if (s >= 0 && e > s)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[s..(e + 1)]);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
            }
            catch (JsonException) { }
        }
        return new List<JsonElement>();
    }

    // ── Phase 1：prepare（自动发现 → skill_input_T4C.md） ──────────

    /// <summary>自动发现扫描池并组装 LLM 输入。返回（池内数量, 关键词命中数）。</summary>
    private async Task<(int Pool, int KwHit)> PrepareAsync(
        string inputPath, DateTime today, Action<string> L, CancellationToken ct)
    {
        L("拉取全市场业绩快照（yjyg + yjbb）…");
        var yjyg = await _em.GetYjygSnapshotAsync(L, ct);
        var yjbb = await _em.GetYjbbAsync(EastMoneyClient.LatestReportPeriod(today), L, ct);
        var period = EastMoneyClient.LatestReportPeriod(today);
        L($"快照：预告 {yjyg.Count} 条 / 报表 {yjbb.Count} 条（报告期 {period}）");

        var yjygByCode = yjyg.GroupBy(r => r.Code).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var yjbbByCode = yjbb.GroupBy(r => r.Code).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 板块热度 + 关键词命中
        var heat = ComputeSectorHeat(yjbb, MinGain);
        var kwMap = ScanKeywordHits(yjyg);

        // 发现池：0 赛道龙头 → 1 关键词 → 2 预告增速 → 3 报表增速补足
        var pool = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string code)
        {
            if (seen.Add(code)) pool.Add(code);
        }

        foreach (var c in SectorLeaders) Add(c);
        L($"赛道龙头源：{SectorLeaders.Length} 只，入池 {pool.Count} 只");

        double GainOf(string code) => yjygByCode.GetValueOrDefault(code)?.GainPct ?? 0;
        var kwSorted = kwMap.OrderByDescending(kv => kv.Value.Score)
            .ThenByDescending(kv => GainOf(kv.Key)).Select(kv => kv.Key).ToList();
        foreach (var code in kwSorted) Add(code);
        L($"关键词源：预告文本命中 {kwMap.Count} 只，全部入池");

        var hit2 = yjyg.Where(r => r.GainPct is null || r.GainPct >= MinGain)
            .OrderByDescending(r => Math.Min(r.GainPct ?? double.MinValue, 500.0)).ToList();
        var before = pool.Count;
        foreach (var r in hit2)
        {
            if (pool.Count >= TopN) break;
            Add(r.Code);
        }
        L($"预告增速源：幅度≥{MinGain:0}% 共 {hit2.Count} 只，入池 {pool.Count - before} 只（增速 clip 500%）");

        var hit3 = yjbb.Where(r => r.NpYoy is not null && r.NpYoy >= MinGain && (r.RevYoy ?? 0) > 0)
            .OrderByDescending(r => Math.Min(r.NpYoy ?? 0, 500.0)).ToList();
        before = pool.Count;
        foreach (var r in hit3)
        {
            if (pool.Count >= TopN) break;
            Add(r.Code);
        }
        if (pool.Count > before)
            L($"报表增速源：补充 {pool.Count - before} 只（np_yoy≥{MinGain:0}% 且营收正增长，clip 500%）");

        if (pool.Count == 0)
        {
            L("[WARN] 所有数据源均为空，无法自动发现");
            return (0, 0);
        }

        // 组装文件
        var sb = new StringBuilder();
        sb.Append(BuildHeader(pool, kwMap, yjbb, heat, period, today));
        foreach (var code in pool)
        {
            var m = BuildPoolMeta(code, yjygByCode, yjbbByCode, kwMap);
            var tags = m.Sources.Count > 0 ? " | 来源: " + string.Join("+", m.Sources) : "";
            var kwLine = FormatKwLine(m.Kw);
            var textBlock = BuildReportText(code, yjygByCode, yjbbByCode);
            if (textBlock.Length == 0)
                textBlock = $"（{(m.Name.Length > 0 ? m.Name : code)} 暂无可用财报数据，请手动补充）";

            sb.Append("\n\n");
            sb.Append($"=== {code} · {(m.Name.Length > 0 ? m.Name : "?")} · {(m.Industry.Length > 0 ? m.Industry : "未知")}{tags} ===\n");
            if (kwLine.Length > 0) sb.Append(kwLine + "\n");
            sb.Append("数据来源: 东财业绩报表/业绩预告 + 巨潮互动易（批量抓取）\n");
            sb.Append($"报告期: {period}；生成日期: {today:yyyy-MM-dd}\n");
            sb.Append("------\n");
            sb.Append(textBlock + "\n");
            sb.Append("------\n");
        }

        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(inputPath, sb.ToString(), new UTF8Encoding(false));
        L($"输入文件已生成：{inputPath}（共 {pool.Count} 只）");
        return (pool.Count, pool.Count(c => kwMap.ContainsKey(c)));
    }

    /// <summary>板块热度：全市场净利同比 ≥ minGain 的票按行业聚合（家数/中位增速/增速前3）。</summary>
    private sealed record SectorHeat(string Industry, int Count, double MedianGain, string[] TopCodes);

    private static List<SectorHeat> ComputeSectorHeat(List<YjbbRow> yjbb, double minGain, int top = 10)
    {
        var hb = yjbb.Where(r => r.NpYoy is not null && r.NpYoy >= minGain)
            .Select(r => (r.Code, r.Industry, NpYoy: r.NpYoy!.Value))
            .Where(x => CleanIndustry(x.Industry) != "未分类")
            .ToList();
        return hb.GroupBy(x => CleanIndustry(x.Industry))
            .Select(g => new SectorHeat(g.Key, g.Count(),
                Median(g.Select(x => x.NpYoy).ToList()),
                g.OrderByDescending(x => x.NpYoy).Take(3).Select(x => x.Code).ToArray()))
            .OrderByDescending(h => h.Count).ThenByDescending(h => h.MedianGain)
            .Take(top).ToList();
    }

    /// <summary>东财行业带 Ⅱ/Ⅲ 后缀（申万二级/三级），聚合热度时去掉便于归并。</summary>
    private static string CleanIndustry(string v)
    {
        var s = (v ?? "").Trim().TrimEnd('Ⅱ', 'Ⅲ').Trim();
        return s.Length > 0 ? s : "未分类";
    }

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    // ── 关键词静态匹配（打分口径与 t4_c_text_scan skill 一致） ──────

    private sealed record KwHit(Dictionary<string, List<string>> Hits, List<string> Neg, double Score);

    /// <summary>对业绩预告文本（变动原因+变动描述）做 bucket_C 关键词匹配。仅返回至少命中一个正向词的票。</summary>
    private static Dictionary<string, KwHit> ScanKeywordHits(List<YjygRow> yjyg)
    {
        var outMap = new Dictionary<string, KwHit>(StringComparer.Ordinal);
        foreach (var r in yjyg)
        {
            var text = $"{r.Reason}\n{r.Excerpt}";
            if (text.Trim().Length <= 1) continue;
            var hit = ScanText(text);
            if (hit is not null) outMap[r.Code] = hit;
        }
        return outMap;
    }

    private static KwHit? ScanText(string text)
    {
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        double score = 0;
        foreach (var (name, w, kws) in Categories)
        {
            var matched = kws.Where(k => text.Contains(k)).ToList();
            if (matched.Count > 0)
            {
                hits[name] = matched;
                score += matched.Count * w;
            }
        }
        var neg = NegKeywords.Where(k => text.Contains(k)).ToList();
        score += NegPenalty * neg.Count;
        return hits.Count > 0 ? new KwHit(hits, neg, Math.Round(score, 1)) : null;
    }

    // ── skill_input_T4C.md 组装辅助 ────────────────────────────────

    private sealed record PoolMeta(List<string> Sources, KwHit? Kw, string Industry, string Name);

    private static PoolMeta BuildPoolMeta(string code,
        Dictionary<string, YjygRow> yjygByCode, Dictionary<string, YjbbRow> yjbbByCode,
        Dictionary<string, KwHit> kwMap)
    {
        var sources = new List<string>();
        var name = "";
        var industry = "";

        if (yjygByCode.TryGetValue(code, out var g))
        {
            name = g.Name;
            sources.Add(g.GainPct is { } gp ? $"预告{gp:+0;-0}%" : $"预告({g.PreviewType})");
        }
        if (yjbbByCode.TryGetValue(code, out var b))
        {
            if (b.NpYoy is { } ny) sources.Add($"报表净利{ny:+0;-0}%");
            industry = b.Industry;
        }
        var kw = kwMap.GetValueOrDefault(code);
        if (kw is not null) sources.Insert(0, "关键词");
        return new PoolMeta(sources, kw, industry, name);
    }

    private static string BuildHeader(List<string> pool, Dictionary<string, KwHit> kwMap,
        List<YjbbRow> yjbb, List<SectorHeat> heat, string period, DateTime today)
    {
        var nKw = pool.Count(c => kwMap.ContainsKey(c));
        var sb = new StringBuilder();
        sb.AppendLine("# T4 财报季扫描输入（自动发现）");
        sb.AppendLine($"生成日期: {today:yyyy-MM-dd}；报告期: {period}；扫描池 {pool.Count} 只（其中关键词命中 {nKw} 只）");
        sb.AppendLine();
        sb.AppendLine("关键词口径: 业绩预告『变动原因/变动』文本匹配 bucket_C.text_signal 需求/价格/供给关键词；"
            + "加权分=Σ(命中数×权重)−3×反向词数，最终判定以 LLM 复核为准。");

        if (heat.Count > 0)
        {
            var hot = yjbb.Where(r => r.NpYoy is not null && r.NpYoy >= 50.0).ToList();
            var total = hot.Count;
            var nInd = hot.Select(r => CleanIndustry(r.Industry))
                .Where(i => i != "未分类").Distinct(StringComparer.Ordinal).Count();
            sb.AppendLine();
            sb.AppendLine($"## 板块热度榜（全市场净利同比≥50% 共 {total} 只、分布在 {nInd} 个行业；"
                + $"按高增长家数排序，前 {heat.Count} 名）");
            sb.AppendLine("同一板块多家公司业绩集体爆发 = 板块级景气，是 C 桶热点判定的核心证据；"
                + "单票不可买时可沿同板块寻找替代标的。");
            sb.AppendLine();
            for (var i = 0; i < heat.Count; i++)
            {
                var h = heat[i];
                sb.AppendLine($"{i + 1}. {h.Industry} — 高增长 {h.Count} 家 | "
                    + $"中位增速 {h.MedianGain:+0;-0}% | 代表: {string.Join("/", h.TopCodes)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>单票关键词命中渲染一行：命中类别[词…]、加权分、反向词。</summary>
    private static string FormatKwLine(KwHit? kw)
    {
        if (kw is null) return "";
        var parts = kw.Hits.Select(kv => $"{kv.Key}[{string.Join("、", kv.Value)}]").ToList();
        var neg = kw.Neg.Count > 0 ? string.Join("、", kw.Neg) : "无";
        return $"关键词命中: {string.Join(" ", parts)}；加权分 {kw.Score.ToString("0.0", Inv)}"
            + $"（LLM 复核口径 ≥{LlmReviewScore:0.0} 且三类齐全）；反向词: {neg}";
    }

    /// <summary>从批量快照拼出单只票的财报要点文本（业绩报表结构化数字 + 预告变动原因/描述）。</summary>
    private static string BuildReportText(string code,
        Dictionary<string, YjygRow> yjygByCode, Dictionary<string, YjbbRow> yjbbByCode)
    {
        var lines = new List<string>();

        if (yjbbByCode.TryGetValue(code, out var b))
        {
            var parts = new List<string>();
            if (b.Revenue is { } rev)
            {
                var s = $"营业收入 {rev / 1e8:0.00}亿";
                if (b.RevYoy is { } ry) s += $"（同比 {ry:+0.0;-0.0}%）";
                parts.Add(s);
            }
            if (b.Np is { } np)
            {
                var s = $"净利润 {np / 1e8:0.00}亿";
                if (b.NpYoy is { } ny) s += $"（同比 {ny:+0.0;-0.0}%）";
                parts.Add(s);
            }
            if (b.Roe is { } roe) parts.Add($"ROE {roe:0.00}%");
            if (b.GrossMargin is { } gm) parts.Add($"毛利率 {gm:0.0}%");
            if (b.OcfPs is { } ocf) parts.Add($"每股经营现金流 {ocf:0.00}");
            if (parts.Count > 0) lines.Add("业绩报表: " + string.Join("；", parts));
        }

        if (yjygByCode.TryGetValue(code, out var g))
        {
            if (g.Excerpt.Length > 0) lines.Add("业绩预告: " + g.Excerpt);
            if (g.Reason.Length > 0) lines.Add("变动原因: " + g.Reason);
        }
        // 互动易问答文本：无公开稳定接口，跳过（Python 受限环境同样经常失败）
        return string.Join("\n", lines);
    }

    // ── 报告表格 ────────────────────────────────────────────────────

    private static string PassTable(List<JsonElement> items)
    {
        var lines = new List<string> { "| 代码 | 名称 | 行业 | 加权分 | 关键理由 |", "|------|------|------|--------|----------|" };
        lines.AddRange(items.Select(i =>
            $"| {Str(i, "stock_code")} | {Str(i, "stock_name")} | {Str(i, "industry")} "
            + $"| {Str(i, "weighted_score")} | {Clip(Str(i, "reason"), 50)} |"));
        return string.Join("\n", lines) + "\n";
    }

    private static string RejectTable(List<JsonElement> items)
    {
        var lines = new List<string> { "| 代码 | 名称 | 加权分 | 淘汰理由 |", "|------|------|--------|----------|" };
        lines.AddRange(items.Select(i =>
            $"| {Str(i, "stock_code")} | {Str(i, "stock_name")} "
            + $"| {Str(i, "weighted_score")} | {Clip(Str(i, "reason"), 60)} |"));
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>LLM 判定条目字段（stock_code 等可能为字符串或数字）。</summary>
    private static string Str(JsonElement o, string prop)
    {
        if (!o.TryGetProperty(prop, out var e)) return "";
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Number => e.GetRawText(),
            _ => "",
        };
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n];
}
