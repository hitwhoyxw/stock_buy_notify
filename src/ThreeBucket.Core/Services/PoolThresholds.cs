using System.Globalization;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 候选池阈值配置：从 trading-system/02_strategy_config.yaml 加载（全系统唯一阈值来源，
/// Python 端 scripts/lib/config.py 同读此文件，两端口径强制一致）。
///
/// 解析器为手写缩进解析（该 yaml 结构固定、无块标量/锚点/引号嵌套，仅需嵌套 mapping + 标量），
/// 避免引入 YamlDotNet 依赖。整文件缺失可用文档化内置默认；已存在但格式/字段非法会明确报错，
/// 保证移动端（无项目结构）与配置文件损坏时任务仍可运行。
///
/// 人工编辑友好：直接改 02_strategy_config.yaml 对应键即可，无需重新编译；每项带行内注释。
/// </summary>
public sealed class PoolThresholds
{
    public string ConfigHash {get;private set;}="builtin-defaults-v1";
    public System.Text.Json.Nodes.JsonNode? ValidatedConfig {get;private set;}
    // ── A 桶 · 红利逆向（bucket_A.stock_filters） ──
    public double MinDy;      // dividend_yield_ttm_min_pct 股息率TTM下限 %
    public double MaxPb;      // pb_max PB 上限
    public double MinRoeA;    // roe_5y_avg_min_pct ROE 年化下限 %

    // ── B 桶 · 成长（bucket_B.batch_screen，2026-09-02 巴菲特式指标集） ──
    public double MinMv;      // total_mv_min_yi 总市值下限（亿）
    public double MinNpCagr;  // np_cagr_3y_min_pct 净利3年CAGR下限 %
    public double MinRevCagr; // rev_cagr_3y_min_pct 营收3年CAGR下限 %
    public bool GmNoDecline;  // gross_margin_5y_no_decline 毛利率逐年不趋势下滑
    public double MinRoeB;    // roe_annualized_min_pct ROE 年化下限 %
    public double MinOcfRatio;// ocf_to_np_annual_min 最新年报 OCF/净利下限
    public double MaxPe;      // pe_ttm_max PE(TTM) 上限
    // 注意：无周期行业黑名单、无暴增上限（用户明确要求）——纯按巴菲特式财务指标
    // 标准筛选；周期/爆发属性交给 LLM 复核（巴菲特式业务质量维度）个案定性。
    public int TopN = 100;

    // ── C 桶 · 信号时效（bucket_C.text_signal.freshness，2026-09-18 新增） ──
    // 按「关键词命中文本距今的日历天数」分档降权。**仅影响排序**，不改 weighted_score 与 PASS/REJECT。
    public FreshnessConfig Freshness = FreshnessConfig.Defaults();

    /// <summary>
    /// T4 扫描池上限（bucket_C.text_signal.scan_pool_top_n）。
    /// 2026-09-20：300 → 500。三源（预告/活动记录）平等作关键词来源后池子变大，
    /// 原 300 会把「预告用词不标准但调研说得很清楚」的票挤掉（如广汇能源 600256）。
    /// </summary>
    public int ScanPoolTopN = 500;

    /// <summary>兜底默认值：与 02_strategy_config.yaml 当前值一致。yaml 缺失/缺项时用。</summary>
    public static PoolThresholds Defaults() => new()
    {
        MinDy = 3.0,
        MaxPb = 4.0,      // 2026-08-31 放宽 2→3→4
        MinRoeA = 10.0,
        MinMv = 30.0,
        MinNpCagr = 15.0,
        MinRevCagr = 10.0,
        GmNoDecline = true,
        MinRoeB = 12.0,   // 2026-09-02 巴菲特式：6→12（标准线15%的批量近似）
        MinOcfRatio = 0.8,// 2026-09-02 巴菲特式现金含金量（0.4→0.8，标准线1.0单年近似放宽）
        MaxPe = 80.0,
        TopN = 100,
        ScanPoolTopN = 500,
        Freshness = FreshnessConfig.Defaults(),
    };

    /// <summary>
    /// 从 02_strategy_config.yaml 加载。yamlPath 为文件完整路径；不存在或解析异常返回 Defaults。
    /// 修改 yaml 中的阈值立即生效（任务每次运行重新加载），无需重新编译。
    /// </summary>
    public static PoolThresholds Load(string? yamlPath)
    {
        var d = Defaults();
        if (yamlPath is null || !File.Exists(yamlPath)) return d;
        try
        {
            var validated=QualityValue.QvYaml.ParseFile(yamlPath);
            ConfigGovernance.Validate(validated,"02_strategy_config.yaml",yamlPath);
            d.ConfigHash=ConfigGovernance.Hash(validated);d.ValidatedConfig=validated.DeepClone();
            var root = MiniYaml.Parse(File.ReadAllText(yamlPath));
            // A 桶：bucket_A.stock_filters.*
            if (root.TryGet("bucket_A", out var a) && a.TryGet("stock_filters", out var af))
            {
                d.MinDy = af.Num("dividend_yield_ttm_min_pct", d.MinDy);
                d.MaxPb = af.Num("pb_max", d.MaxPb);
                d.MinRoeA = af.Num("roe_5y_avg_min_pct", d.MinRoeA);
            }
            // B 桶：bucket_B.batch_screen（2026-09-02 巴菲特式指标集）
            if (root.TryGet("bucket_B", out var b) && b.TryGet("batch_screen", out var bs))
            {
                d.MinMv = bs.Num("total_mv_min_yi", d.MinMv);
                d.MinNpCagr = bs.Num("np_cagr_3y_min_pct", d.MinNpCagr);
                d.MinRevCagr = bs.Num("rev_cagr_3y_min_pct", d.MinRevCagr);
                if (bs.TryGet("gross_margin_5y_no_decline", out var gmNd))
                    d.GmNoDecline = !gmNd.Scalar.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
                d.MinRoeB = bs.Num("roe_annualized_min_pct", d.MinRoeB);
                d.MinOcfRatio = bs.Num("ocf_to_np_annual_min", d.MinOcfRatio);
                d.MaxPe = bs.Num("pe_ttm_max", d.MaxPe);
            }
            // C 桶：bucket_C.text_signal（扫描池上限 + 时效层）
            if (root.TryGet("bucket_C", out var c) && c.TryGet("text_signal", out var cts))
            {
                d.ScanPoolTopN = (int)cts.Num("scan_pool_top_n", d.ScanPoolTopN);
                if (cts.TryGet("freshness", out var fr))
                {
                    var f = FreshnessConfig.Defaults();
                    if (fr.TryGet("enabled", out var en))
                        f.Enabled = !en.Scalar.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
                    f.LookbackDays = (int)fr.Num("activity_lookback_days", f.LookbackDays);
                    f.NoticeWindowDays = (int)fr.Num("lookback_days", f.NoticeWindowDays);
                    f.ActivityBudgetMinutes = (int)fr.Num("activity_budget_minutes", f.ActivityBudgetMinutes);
                    // 分档为列表形态（MiniYaml.Items），每项含 max_days / penalty；
                    // 缺 max_days 或值为非数字的项丢弃，避免脏档插进判定链
                    var tiers = new List<FreshnessTier>();
                    foreach (var t in fr.Items("tiers"))
                    {
                        if (!t.TryGet("max_days", out var md)) continue;
                        if (!double.TryParse(md.Scalar.Trim().Trim('"', '\''), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var maxDays)) continue;
                        tiers.Add(new FreshnessTier((int)maxDays, t.Num("penalty", 0)));
                    }
                    if (tiers.Count > 0) f.Tiers = tiers.OrderBy(x => x.MaxDays).ToList();
                    d.Freshness = f;
                }
            }
        }
        catch (Exception ex)
        {
            throw new ConfigValidationException(yamlPath,"$",1,"Candidate configuration invalid; no default fallback: "+ex.Message);
        }
        return d;
    }
}

/// <summary>时效分档：days ≤ MaxDays 即命中，Penalty 为加权扣分（≤0）。</summary>
public sealed record FreshnessTier(int MaxDays, double Penalty);

/// <summary>
/// C 桶文本信号时效配置（02_strategy_config.yaml → bucket_C.text_signal.freshness）。
/// 口径：按「各来源最新公告/活动日距今天数」（日历日）分档降权，**仅用于排序**，
/// 绝不改写 weighted_score 与 PASS/REJECT 判定。
/// </summary>
public sealed class FreshnessConfig
{
    public bool Enabled = true;
    /// <summary>投资者关系活动记录表的取数窗口（天）。</summary>
    public int LookbackDays = 180;
    /// <summary>公告文本（业绩预告等）的取数窗口（天）：公告日超过该天数的记录不进扫描。</summary>
    public int NoticeWindowDays = 180;
    /// <summary>
    /// 活动记录抓取的墙钟预算（分钟）。
    /// 本地跑可放宽（全量探测 ~950 只需 ~55 分钟）；CI 受 GitHub 30 分钟上限约束，
    /// 超时会被强杀，但持久库会保住已取部分、下轮续跑。
    /// </summary>
    public int ActivityBudgetMinutes = 90;
    public List<FreshnessTier> Tiers = new();

    public static FreshnessConfig Defaults() => new()
    {
        Enabled = true,
        LookbackDays = 180,
        NoticeWindowDays = 180,
        ActivityBudgetMinutes = 90,
        Tiers = new List<FreshnessTier>
        {
            new(45, 0.0),     // ≤45 天：新鲜
            new(90, -0.5),    // 46-90 天：偏旧
            new(180, -1.0),   // 91-180 天：陈旧
            new(9999, -2.0),  // >180 天：极旧
        },
    };
}

/// <summary>时效评定结果。Days = null 表示无可用日期（不做惩罚，渲染为「未知」）。</summary>
public sealed record FreshnessResult(int? Days, double Penalty, string Label)
{
    public static readonly FreshnessResult Unknown = new(null, 0, "未知");

    /// <summary>
    /// 按 days（日历天数）查档位。tiers 为空时返回不惩罚。
    /// 语义：取第一个满足 days ≤ MaxDays 的档；全部不满足则用最后一档并标「超阈值」。
    /// </summary>
    public static FreshnessResult Evaluate(int? days, IReadOnlyList<FreshnessTier> tiers)
    {
        if (days is not { } d) return Unknown;
        if (tiers.Count == 0) return new FreshnessResult(d, 0, "未启用");
        foreach (var t in tiers.OrderBy(x => x.MaxDays))
            if (d <= t.MaxDays) return new FreshnessResult(d, t.Penalty, LabelOf(t.Penalty));
        var last = tiers.OrderBy(x => x.MaxDays).Last();
        return new FreshnessResult(d, last.Penalty, "超阈值");
    }

    private static string LabelOf(double penalty) => penalty switch
    {
        0 => "新鲜",
        > -1.0 => "偏旧",
        > -1.5 => "陈旧",
        _ => "极旧",
    };
}

/// <summary>
/// 极简嵌套 mapping 解析器：仅支持 02_strategy_config.yaml 用到的形态——
/// 「缩进表示层级 + key: value 标量 + # 行内注释 + [a, b] 列表 + 引号串」。
/// 供 PoolThresholds 等读取阈值用；复杂 yaml（多行文本/锚点）不在支持范围。
/// </summary>
public static class MiniYaml
{
    public sealed class Node
    {
        public Dictionary<string, Node> Map = new(StringComparer.Ordinal);
        public string Scalar = "";

        /// <summary>该 key 下的列表项（仅 `- xxx` / `- {k: v, k2: v2}` 形态，见 Parse）。</summary>
        public List<Node> List = new();

        public bool TryGet(string key, out Node node) => Map.TryGetValue(key, out node!);

        /// <summary>取列表项；缺键/非列表返回空列表。</summary>
        public IReadOnlyList<Node> Items(string key)
            => Map.TryGetValue(key, out var n) ? n.List : (IReadOnlyList<Node>)Array.Empty<Node>();

        /// <summary>取标量并按 InvariantCulture 解析为 double；缺键/非数字返回 def。</summary>
        public double Num(string key, double def)
        {
            if (!Map.TryGetValue(key, out var n)) return def;
            var s = n.Scalar.Trim().Trim('"', '\'');
            // yaml 数字可能带下划线分隔（1_000），先去掉
            s = s.Replace("_", "");
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        }
    }

    /// <summary>
    /// 解析 yaml 文本为嵌套 Node 树。
    /// 支持：嵌套 mapping、标量、`- scalar` 列表、`- {k: v, k2: v2}` 流式映射列表。
    /// 不支持：块标量（| / &gt;）、锚点、多行字符串。
    /// </summary>
    public static Node Parse(string text)
    {
        var root = new Node();
        var stack = new List<(int Indent, Node Node)> { (0, root) };
        // 列表项可能比其父 key 更深缩进（yaml 惯例），也可能是同级；记录当前挂载的父节点
        Node? listOwner = null;
        int listOwnerIndent = -1;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith("#")) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();

            // ── 列表项：`- value` 或 `- {k: v, ...}` ──
            if (line.StartsWith("- ") || line == "-")
            {
                if (listOwner is null) continue;               // 无归属的列表项，忽略
                var body = line.Length > 2 ? line[2..].Trim() : "";
                listOwner.List.Add(ParseListItem(body));
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;                          // 无冒号（续行）跳过
            var key = line[..colon].Trim().Trim('"', '\'');
            var val = line[(colon + 1)..].Trim();
            // 去行内注释（值内含 # 的场景如 url 无 #，本 yaml 阈值列均无 #，稳妥起见按 " #" 切）
            var hash = val.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) val = val[..hash].Trim();

            while (stack.Count > 1 && indent <= stack[^1].Indent) stack.RemoveAt(stack.Count - 1);
            var parent = stack[^1].Node;
            var node = new Node();
            if (val.Length == 0)
            {
                // 中间节点（子 mapping 或列表宿主）
                parent.Map[key] = node;
                stack.Add((indent, node));
                listOwner = node;                              // 其后的 `- ` 行归到它
                listOwnerIndent = indent;
            }
            else
            {
                // 叶子标量（列表值如 [20, 80] 存原串，Num 解析不了会回退默认，无碍）
                node.Scalar = val;
                parent.Map[key] = node;
                listOwner = null;
            }
        }
        return root;
    }

    /// <summary>解析单个列表项：`{k: v, k2: v2}` → 嵌套 Node；纯标量 → Scalar。</summary>
    private static Node ParseListItem(string body)
    {
        var node = new Node();
        if (!body.StartsWith('{'))
        {
            node.Scalar = body.Trim().Trim('"', '\'');
            return node;
        }
        var inner = body.Trim().TrimStart('{').TrimEnd('}');
        foreach (var part in inner.Split(','))
        {
            var c = part.IndexOf(':');
            if (c <= 0) continue;
            var k = part[..c].Trim().Trim('"', '\'');
            var v = part[(c + 1)..].Trim().Trim('"', '\'');
            node.Map[k] = new Node { Scalar = v };
        }
        return node;
    }
}
