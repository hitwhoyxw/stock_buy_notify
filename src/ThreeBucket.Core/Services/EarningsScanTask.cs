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
    private readonly PoolThresholds _th;

    public EarningsScanTask(string dataDir, EastMoneyClient em, SignalLogStore signals)
    {
        _dataDir = dataDir;
        _em = em;
        _signals = signals;
        // 时效分档从 yaml 读（每次运行重载，改 yaml 即生效）；找不到项目根时用 Defaults
        var root = dataDir;
        for (var i = 0; i < 6 && root is not null; i++)
        {
            if (Directory.Exists(Path.Combine(root, "trading-system"))) break;
            root = Path.GetDirectoryName(root);
        }
        _th = PoolThresholds.Load(root is null
            ? null : Path.Combine(root, "trading-system", "02_strategy_config.yaml"));
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
            "产销量增长","产销量提升","产销量大幅提升","产销量同比增长",
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
    /// <summary>板块热度榜门槛（%）。**仅用于渲染头部热度榜**，不再作为入池条件。</summary>
    private const double MinGain = 50.0;

    // 投资者关系活动记录写入 skill_input 的体积闸门：每票最多 3 场、每场正文最多 600 字。
    // 实测单场正文 ~1.4k 字，300 只池若不设限会把 200KB 的输入文件推到 MB 级，白烧 token。
    private const int MaxSurveyPerStock = 3;
    private const int SurveyTextLimit = 600;

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
                ["备注"] = Clip(Str(item, "reason")
                    + (Str(item, "freshness").Length > 0
                        ? $"｜时效: {Str(item, "freshness")}"
                          + (Str(item, "signal_days").Length > 0 ? $"（距今 {Str(item, "signal_days")} 天）" : "")
                        : "")
                    + (Str(item, "fresh_score").Length > 0
                        ? $"｜时效分 {Str(item, "fresh_score")}（仅排序用）" : ""), 200),
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

        // 板块热度（仅作 LLM 背景参考，不参与池发现）
        var heat = ComputeSectorHeat(yjbb, MinGain);

        // ── 扫描池发现（2026-09-20 重设计）──
        // 原则（用户明确要求）：**业绩预告 / 投资者关系活动记录表（电话会议·调研·说明会）
        // 是同等作用的关键词来源**；业绩报表/增速**不再作为入池来源**——
        // 「高增长的相关信息披露自然会包含静态关键词」，靠增速入池是本末倒置。
        //
        // 入池 = **任一来源关键词命中**。赛道龙头也不再强制入池（用户要求）：
        // 它们是固定的 CPO/PCB 名单，不构成本轮文本证据，占位会挤掉真正命中的票。
        // 「只在名单里、文本却无命中」的票由 LLM 复核层按行业热度榜自行关注。
        //
        // 鸡生蛋问题的解法：活动记录必须逐票拉（分块会因单票数百页爆掉，见 9701 那个坑），
        // 所以先对「有业绩预告但预告文本未命中关键词」的票去查活动记录——
        // 这批正是广汇能源那类「预告用词不标准、调研里说得很清楚」的票。
        var pool = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string code)
        {
            if (seen.Add(code)) pool.Add(code);
        }

        // ① 预告文本关键词命中（全量入池，不受上限约束——文本命中即证据）
        var kwMapYjyg = ScanKeywordHits(yjyg, new Dictionary<string, List<OrgSurveyRow>>(), today,
            _th.Freshness);
        double GainOf(string code) => yjygByCode.GetValueOrDefault(code)?.GainPct ?? 0;
        var kwSorted = kwMapYjyg.OrderByDescending(kv => kv.Value.FreshScore)
            .ThenByDescending(kv => GainOf(kv.Key)).Select(kv => kv.Key).ToList();
        foreach (var code in kwSorted) Add(code);
        L($"预告关键词源：命中 {kwMapYjyg.Count} 只，全部入池（池={pool.Count}）");

        // ② 活动记录关键词命中：对「有预告但预告未命中」的票补查活动记录
        var orgByCode = new Dictionary<string, List<OrgSurveyRow>>(StringComparer.Ordinal);
        if (_th.Freshness.Enabled)
        {
            var toProbe = yjyg.Where(r => !kwMapYjyg.ContainsKey(r.Code))
                .Select(r => r.Code).Distinct(StringComparer.Ordinal).ToList();
            L($"活动记录补查：{toProbe.Count} 只（有预告但预告文本无关键词命中）");

            var surveys = await _em.GetOrgSurveysAsync(toProbe, _th.Freshness.LookbackDays,
                log: L, ct: ct,
                budget: TimeSpan.FromMinutes(_th.Freshness.ActivityBudgetMinutes));
            orgByCode = surveys.GroupBy(s => s.Code, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            // 活动记录命中的票 → 入池（与预告源完全同等，不再只是"池内补充"）
            var kwMapOrgOnly = ScanKeywordHits(
                new List<YjygRow>(), orgByCode, today, _th.Freshness);
            var orgSorted = kwMapOrgOnly.OrderByDescending(kv => kv.Value.FreshScore)
                .Select(kv => kv.Key).ToList();
            foreach (var code in orgSorted) Add(code);
            L($"活动记录关键词源：覆盖 {orgByCode.Count}/{toProbe.Count} 只，"
                + $"命中 {kwMapOrgOnly.Count} 只入池（池={pool.Count}）");
        }

        if (pool.Count == 0)
        {
            L("[WARN] 所有数据源均为空，无法自动发现");
            return (0, 0);
        }

        // 合并三源关键词（预告 + 活动记录），供排序、渲染与截断清单
        var kwMap = ScanKeywordHits(yjyg, orgByCode, today, _th.Freshness);
        var nYjygOnly = kwMap.Keys.Count(c => kwMapYjyg.ContainsKey(c));
        var nOrgOnly = kwMap.Keys.Count - nYjygOnly;
        L($"关键词命中合计 {kwMap.Count} 只（预告 {nYjygOnly} + 仅活动记录 {nOrgOnly}）");

        // 安全阀：超上限时按时效分降序截断；被截断的票写入清单文件而非无声消失
        // （它们仍是文本命中的有效候选，只是本轮 LLM 复核成本所限未纳入，下轮可回补）。
        if (pool.Count > _th.ScanPoolTopN)
        {
            var ordered = pool
                .OrderByDescending(c => kwMap.GetValueOrDefault(c)?.FreshScore ?? 0)
                .ToList();
            var truncated = ordered.Skip(_th.ScanPoolTopN).ToList();
            pool = ordered.Take(_th.ScanPoolTopN).ToList();

            try
            {
                var lns = new List<string>
                {
                    $"# T4 扫描池截断清单 · {today:yyyy-MM-dd}",
                    $"池 {seen.Count} 只 > 上限 {_th.ScanPoolTopN}，截断 {truncated.Count} 只",
                    "说明: 以下票**文本关键词已命中**（有效候选），仅因 LLM 复核成本上限未纳入本轮输入。",
                    "      池子收缩或有预算时可在下轮回补；截断按时效分升序（先砍最低分）。",
                    "",
                    "| 代码 | 名称 | 行业 | 加权分 | 时效分 | 距今天数 | 时效 | 命中来源 |",
                    "|------|------|------|--------|--------|----------|------|----------|",
                };
                foreach (var c in truncated)
                {
                    var k = kwMap.GetValueOrDefault(c);
                    var m2 = BuildPoolMeta(c, yjygByCode, yjbbByCode, kwMap);
                    var srcKinds = string.Join("+", (k?.Sources ?? new List<SourceHit>())
                        .Select(s => s.Kind).Distinct(StringComparer.Ordinal));
                    lns.Add($"| {c} | {m2.Name} | {m2.Industry} "
                        + $"| {k?.Score.ToString("0.0", Inv) ?? ""} "
                        + $"| {k?.FreshScore.ToString("0.0", Inv) ?? ""} "
                        + $"| {k?.AgeDays?.ToString(Inv) ?? ""} "
                        + $"| {FreshnessResult.Evaluate(k?.AgeDays, _th.Freshness.Tiers).Label} "
                        + $"| {srcKinds} |");
                }
                File.WriteAllText(Path.Combine(_dataDir, "t4_pool_truncated.md"),
                    string.Join("\n", lns), new UTF8Encoding(false));
                L($"[WARN] 池 {seen.Count} 只超上限 {_th.ScanPoolTopN}，按时效分截断至 {pool.Count} 只"
                    + $"（被截断 {truncated.Count} 只见 t4_pool_truncated.md）");
            }
            catch (Exception e)
            {
                L($"[WARN] 池超上限截断至 {pool.Count} 只，但清单写入失败: {e.Message}");
            }
        }
        else
        {
            try { File.Delete(Path.Combine(_dataDir, "t4_pool_truncated.md")); } catch { }
        }

        // 组装文件
        var sb = new StringBuilder();
        sb.Append(BuildHeader(pool, kwMap, yjbb, heat, period, today, orgByCode.Count, _th.Freshness));
        foreach (var code in pool)
        {
            var m = BuildPoolMeta(code, yjygByCode, yjbbByCode, kwMap);
            var tags = m.Sources.Count > 0 ? " | 来源: " + string.Join("+", m.Sources) : "";
            var kwLine = FormatKwLine(m.Kw);
            var freshLine = FormatFreshnessLine(m.Kw, _th.Freshness);
            var textBlock = BuildReportText(code, yjygByCode, yjbbByCode, orgByCode);
            if (textBlock.Length == 0)
                textBlock = $"（{(m.Name.Length > 0 ? m.Name : code)} 暂无可用财报数据，请手动补充）";

            // 数据来源行必须与实际取到的材料一致：活动记录被限流/失败时不得声称用了它
            // （原「+ 巨潮互动易」就是一句从未成立的假话，这里不再重犯）
            var hasSurvey = orgByCode.ContainsKey(code);
            var srcLine = "数据来源: 东财业绩报表/业绩预告"
                + (hasSurvey ? " + 东财投资者关系活动记录表（电话会议/业绩说明会/特定对象调研）" : "")
                + (orgByCode.Count == 0 ? "（本次活动记录接口不可用，仅用预告/报表文本）" : "");

            sb.Append("\n\n");
            sb.Append($"=== {code} · {(m.Name.Length > 0 ? m.Name : "?")} · {(m.Industry.Length > 0 ? m.Industry : "未知")}{tags} ===\n");
            if (kwLine.Length > 0) sb.Append(kwLine + "\n");
            if (freshLine.Length > 0) sb.Append(freshLine + "\n");
            sb.Append(srcLine + "\n");
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

    /// <summary>
    /// 单个文本来源的命中记录。Timestamp = 该来源的公告/活动日（时效判定用；可能为 null）。
    /// 时效口径：**每个来源各自保留时间戳**，最终取全部来源中最新的一个作为该票的信号时间。
    /// </summary>
    public sealed record SourceHit(string Kind, DateTime? Timestamp, string Text);

    /// <summary>一票的关键词命中结果。
    /// Score 为**全部来源合并后**的原始加权分（口径与改动前完全一致，不因时效而变）；
    /// AgeDays / FreshnessPenalty / FreshScore 为新增的时效派生值，**仅用于排序与展示**。
    /// public：供 ThreeBucket.Demo 离线回归断言"时效不改变 weighted_score"这一核心不变式。</summary>
    public sealed record KwHit(Dictionary<string, List<string>> Hits, List<string> Neg, double Score,
        List<SourceHit> Sources, DateTime? LatestHitDate, string LatestHitKind,
        int? AgeDays, double FreshnessPenalty)
    {
        public double FreshScore => Math.Max(0.0, Math.Round(Score + FreshnessPenalty, 1));
    }

    /// <summary>
    /// 对多个文本来源做 bucket_C 关键词匹配。仅返回至少命中一个正向词的票。
    ///
    /// 合并口径与改动前的单来源版**逐字一致**：把各来源正文拼在一起扫一遍，
    /// 命中数与反向词数跨来源累加 → Score 不变 → weighted_score 与 PASS/REJECT 不受影响。
    /// 时效另算：每个来源独立记时间戳，取最新者。
    /// </summary>
    private static Dictionary<string, KwHit> ScanKeywordHits(
        List<YjygRow> yjyg, Dictionary<string, List<OrgSurveyRow>> orgByCode, DateTime today,
        FreshnessConfig freshCfg)
        => ScanKeywordHitsCore(yjyg, orgByCode, today, freshCfg);

    /// <summary>供离线回归断言"时效不改变加权分"（Demo 程序集可见）。</summary>
    public static Dictionary<string, KwHit> ScanKeywordHitsForTest(
        List<YjygRow> yjyg, Dictionary<string, List<OrgSurveyRow>> orgByCode, DateTime today,
        FreshnessConfig freshCfg)
        => ScanKeywordHitsCore(yjyg, orgByCode, today, freshCfg);

    private static Dictionary<string, KwHit> ScanKeywordHitsCore(
        List<YjygRow> yjyg, Dictionary<string, List<OrgSurveyRow>> orgByCode, DateTime today,
        FreshnessConfig freshCfg)
    {
        // 1) 汇总每个代码的全部文本来源
        var byCode = new Dictionary<string, List<SourceHit>>(StringComparer.Ordinal);
        void AddSource(string code, SourceHit src)
        {
            if (src.Text.Trim().Length <= 1) return;
            if (!byCode.TryGetValue(code, out var list)) byCode[code] = list = new();
            list.Add(src);
        }

        foreach (var r in yjyg)
            AddSource(r.Code, new SourceHit("业绩预告", r.NoticeDate, $"{r.Reason}\n{r.Excerpt}"));

        foreach (var (code, surveys) in orgByCode)
            foreach (var s in surveys)
                AddSource(code, new SourceHit(
                    s.Way.Length > 0 ? s.Way : "投资者关系活动", s.ReceiveDate ?? s.NoticeDate, s.Content));

        // 取数窗口（2026-09-20 新增）：公告/活动日超过 N 天的来源**直接不参与扫描**，
        // 从源头切断陈旧公告（如三季度仍在匹配一季报预告）。无日期的来源保留——
        // 宁可放行也不误杀（旧缓存/接口缺日期时不该整票消失）。
        // 窗口取两位配置的较大值，两个来源各自已按自己的窗口取数。
        var maxAge = Math.Max(freshCfg.NoticeWindowDays, freshCfg.LookbackDays);
        if (maxAge > 0)
            byCode = byCode.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Where(s => s.Timestamp is null
                    || (today.Date - s.Timestamp.Value.Date).TotalDays <= maxAge).ToList(),
                StringComparer.Ordinal);
        byCode = byCode.Where(kv => kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // 2) 逐票：合并打分（原口径）+ 逐来源归属 + 时效评定
        var outMap = new Dictionary<string, KwHit>(StringComparer.Ordinal);
        foreach (var (code, sources) in byCode)
        {
            var merged = ScanText(string.Join("\n", sources.Select(s => s.Text)));
            if (merged is null) continue;

            // 逐来源单独扫一遍，把每个命中词**归属到真正出现它的来源**。
            // 注意 Score 仍用合并文本算出的 merged.Score（与改动前的单来源口径逐字一致），
            // 逐来源结果只用于展示归属，不参与计分——否则同一词在多来源重复出现会被重复计分。
            var mergedHits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var src in sources)
            {
                var own = ScanText(src.Text);
                if (own is null) continue;
                foreach (var (cat, words) in own.Hits)
                {
                    if (!mergedHits.TryGetValue(cat, out var list))
                        mergedHits[cat] = list = new List<string>();
                    foreach (var w in words)
                        if (!list.Contains(w)) list.Add(w);
                }
            }
            // 兜底：理论上不会差，但若某词只在合并文本里匹配到（分隔符造成的边界效应），仍保留
            foreach (var (cat, words) in merged.Hits)
            {
                if (!mergedHits.TryGetValue(cat, out var list))
                    mergedHits[cat] = list = new List<string>();
                foreach (var w in words)
                    if (!list.Contains(w)) list.Add(w);
            }

            var latest = sources.Where(s => s.Timestamp is not null)
                .OrderByDescending(s => s.Timestamp).FirstOrDefault();
            var days = latest?.Timestamp is { } ts ? (int)(today.Date - ts.Date).TotalDays : (int?)null;
            var fresh = FreshnessResult.Evaluate(days, freshCfg.Tiers);

            outMap[code] = merged with
            {
                Hits = mergedHits,
                Sources = sources,
                LatestHitDate = latest?.Timestamp,
                LatestHitKind = latest?.Kind ?? "",
                AgeDays = fresh.Days,
                FreshnessPenalty = fresh.Penalty,
            };
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
        return hits.Count > 0
            ? new KwHit(hits, neg, Math.Round(score, 1), new List<SourceHit>(),
                null, "", null, 0)
            : null;
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
        List<YjbbRow> yjbb, List<SectorHeat> heat, string period, DateTime today,
        int nSurveyCodes, FreshnessConfig freshCfg)
    {
        var nKw = pool.Count(c => kwMap.ContainsKey(c));
        var sb = new StringBuilder();
        sb.AppendLine("# T4 财报季扫描输入（自动发现）");
        sb.AppendLine($"生成日期: {today:yyyy-MM-dd}；报告期: {period}；扫描池 {pool.Count} 只"
            + $"（其中关键词命中 {nKw} 只；投资者关系活动记录覆盖 {nSurveyCodes} 只）");
        if (nSurveyCodes == 0)
            sb.AppendLine("⚠️ 本次未取到投资者关系活动记录（接口限流或失败），文本信号仅来自业绩预告/报表，"
                + "可能漏掉只在电话会议/调研中披露的景气表述。");
        sb.AppendLine();
        sb.AppendLine("关键词口径: 业绩预告『变动原因/变动』+ 投资者关系活动记录表（电话会议/业绩说明会/"
            + "特定对象调研）正文，**两者同等作为信号来源**，匹配 bucket_C.text_signal 需求/价格/供给关键词；"
            + "加权分=Σ(命中数×权重)−3×反向词数，最终判定以 LLM 复核为准。");
        sb.AppendLine("入池规则: 任一来源关键词命中即入池；业绩报表与净利增速**不作为入池条件**"
            + "（仅用于下方数据验证与排序参考）——高增长公司的信息披露自然会包含上述关键词。");
        sb.AppendLine();
        sb.AppendLine("信号时效口径（⚠️ 仅影响 T6 排序，不改判定）: 按**各来源最新公告/活动日**据今天数"
            + "（日历天）分档扣分，无日期则不扣分。");
        sb.AppendLine("  分档: " + string.Join(" / ", freshCfg.Tiers.Select(t =>
            $"≤{t.MaxDays}天 {t.Penalty.ToString("0.0", Inv)}")));
        sb.AppendLine("  时效分 = 原始加权分 + 时效扣分（下限 0）。时效陈旧但景气证据明确的票**仍可 PASS**，"
            + "请在 reason 中注明证据所属月份，供人工复核景气窗口是否仍开。");

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

    /// <summary>单票关键词命中渲染一行：命中类别[词…]、加权分、反向词；时效降权有则附在末尾。</summary>
    private static string FormatKwLine(KwHit? kw)
    {
        if (kw is null) return "";
        var parts = kw.Hits.Select(kv => $"{kv.Key}[{string.Join("、", kv.Value)}]").ToList();
        var neg = kw.Neg.Count > 0 ? string.Join("、", kw.Neg) : "无";
        var line = $"关键词命中: {string.Join(" ", parts)}；加权分 {kw.Score.ToString("0.0", Inv)}"
            + $"（LLM 复核口径 ≥{LlmReviewScore:0.0} 且三类齐全）；反向词: {neg}";
        if (kw.FreshnessPenalty < 0)
            line += $"；⚠️ 时效 {kw.FreshnessPenalty.ToString("0.0", Inv)}"
                + $"（距今 {kw.AgeDays} 天，最新来源 {kw.LatestHitKind} {kw.LatestHitDate:yyyy-MM-dd}）"
                + $"；时效分 {kw.FreshScore.ToString("0.0", Inv)}";
        return line;
    }

    /// <summary>
    /// 时效单独一行（高亮）：最新命中来源、距今天数、档位、扣分，以及各来源日期明细。
    /// 口径为**日历天数**；无日期时渲染为「未知」并明确不做扣分。
    /// </summary>
    private static string FormatFreshnessLine(KwHit? kw, FreshnessConfig cfg)
    {
        if (kw is null) return "";
        var srcs = "要点来源明细: " + string.Join(" · ", kw.Sources
            .GroupBy(s => s.Kind, StringComparer.Ordinal)
            .Select(g =>
            {
                var d = g.Where(x => x.Timestamp is not null)
                    .OrderByDescending(x => x.Timestamp).FirstOrDefault();
                return d is null ? $"{g.Key} 日期未知" : $"{g.Key} {d.Timestamp:yyyy-MM-dd}";
            }));
        if (kw.AgeDays is not { } days)
            return $"❔ 信号时效: 无可用日期（无法判定新旧，扣分 0）\n{srcs}";

        var label = FreshnessResult.Evaluate(days, cfg.Tiers).Label;
        var icon = kw.FreshnessPenalty <= -1.5 ? "🔴" : kw.FreshnessPenalty < 0 ? "⚠️" : "✅";
        return $"{icon} 信号时效: 最新来源 {kw.LatestHitKind} {kw.LatestHitDate:yyyy-MM-dd}，"
            + $"距今 {days} 天 → {label}（扣 {kw.FreshnessPenalty.ToString("0.0", Inv)}）"
            + $"；时效分 {kw.FreshScore.ToString("0.0", Inv)}"
            + $"（原始加权分 {kw.Score.ToString("0.0", Inv)}，时效分仅用于 T6 排序，不改判定）\n{srcs}";
    }

    /// <summary>从批量快照拼出单只票的财报要点文本（业绩报表数字 + 预告原因 + 投资者关系活动记录）。</summary>
    private static string BuildReportText(string code,
        Dictionary<string, YjygRow> yjygByCode, Dictionary<string, YjbbRow> yjbbByCode,
        Dictionary<string, List<OrgSurveyRow>> orgByCode)
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

        // 投资者关系活动记录表（电话会议/业绩说明会/特定对象调研）——与业绩预告**同等**的
        // 关键词来源（2026-09-20 起参与池发现，不再只是池内补充材料）。
        // 正文很长（~1.4k-5k 字/场），且实测 **72% 的场次前 600 字不含任何关键词**
        // （分红/股东户数/产品闲聊等）。若按位置截断，无效文本会淹没有效证据并撑爆 LLM 输入。
        // 故按**关键词窗口**提取：只保留命中句 ± 相邻句（保留语境），每场再设总量上限。
        if (orgByCode.TryGetValue(code, out var surveys) && surveys.Count > 0)
            foreach (var s in surveys.Take(MaxSurveyPerStock))
                lines.Add($"投资者关系活动记录（{s.ReceiveDate?.ToString("yyyy-MM-dd") ?? "日期未知"} · "
                    + $"{s.Way}{(s.Object.Length > 0 ? " · " + s.Object : "")}）: "
                    + ExtractKeywordContext(s.Content, SurveyTextLimit));

        return string.Join("\n", lines);
    }

    /// <summary>截断长文本并标注截断（活动记录正文可能上千字）。</summary>
    private static string ClipText(string s, int limit)
        => s.Length <= limit ? s : s[..limit] + $" ...（截断，共 {s.Length} 字）";

    /// <summary>全部关键词（正向含 negative），供 ExtractKeywordContext 与离线回归用。</summary>
    public static string[] AllKeywords()
        => Categories.SelectMany(c => c.Keywords).Concat(NegKeywords).Distinct().ToArray();

    /// <summary>句切分（中英文句末标点 + 换行）。</summary>
    private static readonly Regex SentenceSplit = new(@"(?<=[。！？；!?;\n])", RegexOptions.Compiled);

    /// <summary>问答对切分：Q/A 前缀行（问题1:/问题2:/回复:/答复:/答:/Q:/A:/问: 等）。</summary>
    private static readonly Regex QaBoundary =
        new(@"(?=^(?:问题\d+[:：.]|回复[:：]|答复[:：]|答[:：]|问[:：]|Q[:：.]|A[:：.])\s*)",
            RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// 按语义粒度提取正文，只保留含关键词的部分，供 LLM 复核（2026-09-29 重写）。
    ///
    /// 动机（用户指出）：投资者互动平台的问答里大量与景气无关的对子
    /// （分红/股东户数/产品闲聊/致谢）全被整段保留，输入文件里 ~60% 是这类噪音。
    ///
    /// 策略——两级切分，取更粗、语义更完整的粒度：
    /// 1. 先按**问答对**切（问题1:/回复:/答复:/答:/Q:/A: 行边界）：
    ///    问或答**本身含关键词**的问答对才保留（无关对整对丢弃，不保留语境——
    ///    问答自成一体的语境，邻对的语境没有信息量）。
    /// 2. 无问答边界（如业绩说明会的整段陈述）→ 退回**句窗口**切：命中句 ± 1 句。
    ///
    /// 细节：
    /// - 短句（≤6 字，如"谢谢""您好"）并入相邻句，避免碎片化
    /// - 无任何命中 → 返回空串（该场记录对景气判定无信息量，整场丢弃）
    /// - 超出 limit 先到先得截断并标注
    /// - 词表含 negative（反向词语境同样要给 LLM 看）
    /// </summary>
    public static string ExtractKeywordContext(string content, int limit)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";

        var allKws = AllKeywords();
        bool HasKw(string s) => allKws.Any(k => s.Contains(k));

        // 全文无任何命中 → 直接丢弃（不用再按粒度切）
        if (!HasKw(content)) return "";
        // 短文本（≤limit）：无需拼接优化，但**仍按问答粒度过滤**（无关问答对照样要滤，
        // 不能因为全文有命中就整段放行——短文本走同一套问答/句窗口逻辑，见下）。

        // ── 1. 问答对粒度 ──
        var qaParts = QaBoundary.Split(content).Select(s => s.Trim())
            .Where(s => s.Length > 0).ToList();
        if (qaParts.Count >= 2)
        {
            // 问答对可能出现"问题N"与"回复"分离成两段的情况——把"回复/答复/答/A"段
            // 并回它前面的"问题"段，构成完整一问一答
            var pairs = new List<string>();
            foreach (var p in qaParts)
            {
                var isReply = p.StartsWith("回复") || p.StartsWith("答复")
                    || p.StartsWith("答") && !p.StartsWith("答复符") || p.StartsWith("A");
                if (isReply && pairs.Count > 0) pairs[^1] += "\n" + p;
                else pairs.Add(p);
            }

            var kept = pairs.Where(HasKw).ToList();
            if (kept.Count == 0) return "";   // 全部问答无关 → 整场丢弃

            var sb2 = new StringBuilder();
            var n2 = 0;
            var cut2 = false;
            foreach (var p in kept)
            {
                // pair 可能异常大（回复正文与后段无前缀文本粘连，如业绩说明会的整段陈述
                // 接在"回复:"后）——超限时 pair 内部降级为句窗口，保住命中句而非整对丢弃
                var piece = p;
                if (piece.Length > limit)
                    piece = ExtractBySentenceWindow(piece, limit, HasKw);
                if (piece.Length == 0) continue;   // pair 内无命中句（理论上 HasKw(p) 已保证，防御）
                if (n2 + piece.Length > limit) { cut2 = true; break; }
                sb2.Append(piece).Append('\n');
                n2 += piece.Length + 1;
            }
            var r2 = sb2.ToString().TrimEnd();
            if (cut2) r2 += $" ...（仅保留含关键词的问答，全文 {content.Length} 字）";
            return r2;
        }

        // ── 无问答边界 → 句窗口 ──
        return ExtractBySentenceWindow(content, limit, HasKw);
    }

    /// <summary>
    /// 句窗口提取：命中句完整保留，语境句（命中句±1）按命中句长度比例截短。
    /// 供问答 pair 超限降级与整段陈述式文本两条路径复用。
    /// </summary>
    private static string ExtractBySentenceWindow(string content, int limit, Func<string, bool> HasKw)
    {
        var sentences = SentenceSplit.Split(content)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var merged = new List<string>();
        foreach (var s in sentences)
        {
            if (merged.Count > 0 && (s.Length <= 6 || merged[^1].Length < 10))
                merged[^1] += s;
            else
                merged.Add(s);
        }
        if (merged.Count == 0) return "";

        // 语境句上限：命中句 ±1 的"语境句"最长不超过**命中句自身长度**（且 ≤80 字）。
        // 语境是辅助，不该反客为主——尤其对无标点粘连的病态长句（表格粘贴/异常文本），
        // 固定 80 字仍会拖进大量无关内容；以命中句为锚按比例收紧。
        const int CtxCapAbs = 80;

        var keep = new bool[merged.Count];
        var anyHit = false;
        for (var i = 0; i < merged.Count; i++)
        {
            if (HasKw(merged[i]))
            {
                anyHit = true;
                for (var j = Math.Max(0, i - 1); j <= Math.Min(merged.Count - 1, i + 1); j++)
                    keep[j] = true;
            }
        }
        if (!anyHit) return "";

        var sb = new StringBuilder();
        var total = 0;
        var truncated = false;
        for (var i = 0; i < merged.Count; i++)
        {
            if (!keep[i]) continue;
            if (HasKw(merged[i]))
            {
                // 命中句完整保留
                if (total + merged[i].Length > limit) { truncated = true; break; }
                sb.Append(merged[i]);
                total += merged[i].Length;
            }
            else
            {
                // 纯语境句：≤ min(80, 该句相邻命中句的长度)（无相邻命中句信息时用 80）
                var cap = Math.Min(CtxCapAbs,
                    new[] { i > 0 && HasKw(merged[i - 1]) ? merged[i - 1].Length : CtxCapAbs,
                            i < merged.Count - 1 && HasKw(merged[i + 1]) ? merged[i + 1].Length : CtxCapAbs }
                        .Min());
                var piece = merged[i].Length > cap ? merged[i][..cap] : merged[i];
                if (total + piece.Length > limit) { truncated = true; break; }
                sb.Append(piece);
                total += piece.Length;
            }
        }
        var result = sb.ToString();
        if (truncated) result += $" ...（按关键词窗口截取，全文 {content.Length} 字）";
        return result;
    }

    // ── 报告表格 ────────────────────────────────────────────────────

    private static string PassTable(List<JsonElement> items)
    {
        var lines = new List<string> { "| 代码 | 名称 | 行业 | 加权分 | 时效 | 关键理由 |", "|------|------|------|--------|------|----------|" };
        lines.AddRange(items.Select(i =>
        {
            var days = Str(i, "signal_days");
            var fresh = Str(i, "freshness");
            var cell = days.Length > 0 ? $"{days}天 {fresh}" : (fresh.Length > 0 ? fresh : "—");
            return $"| {Str(i, "stock_code")} | {Str(i, "stock_name")} | {Str(i, "industry")} "
                + $"| {Str(i, "weighted_score")} | {cell} | {Clip(Str(i, "reason"), 50)} |";
        }));
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
