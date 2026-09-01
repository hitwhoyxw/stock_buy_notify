using System.Globalization;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 候选池阈值配置：从 trading-system/02_strategy_config.yaml 加载（全系统唯一阈值来源，
/// Python 端 scripts/lib/config.py 同读此文件，两端口径强制一致）。
///
/// 解析器为手写缩进解析（该 yaml 结构固定、无块标量/锚点/引号嵌套，仅需嵌套 mapping + 标量），
/// 避免引入 YamlDotNet 依赖。阈值缺失/解析失败时回退到 Defaults（与 yaml 现值一致），
/// 保证移动端（无项目结构）与配置文件损坏时任务仍可运行。
///
/// 人工编辑友好：直接改 02_strategy_config.yaml 对应键即可，无需重新编译；每项带行内注释。
/// </summary>
public sealed class PoolThresholds
{
    // ── A 桶 · 红利逆向（bucket_A.stock_filters） ──
    public double MinDy;      // dividend_yield_ttm_min_pct 股息率TTM下限 %
    public double MaxPb;      // pb_max PB 上限
    public double MinRoeA;    // roe_5y_avg_min_pct ROE 年化下限 %

    // ── B 桶 · 成长（bucket_B.batch_screen） ──
    public double MinMv;      // total_mv_min_yi 总市值下限（亿）
    public double MinNpCagr;  // np_cagr_3y_min_pct 净利3年CAGR下限 %
    public double MinRevCagr; // rev_cagr_3y_min_pct 营收3年CAGR下限 %
    public double MinNpYoy;   // latest_np_yoy_min_pct 最新期净利同比下限 %
    public double MinRoeB;    // roe_annualized_min_pct ROE 年化下限 %
    public double MinOcfRatio;// ocf_to_np_annual_min 年报 OCF/NP 下限
    public double MaxPe;      // pe_ttm_max PE(TTM) 上限
    public double MaxPeg;     // peg_max PEG 上限

    /// <summary>每桶 LLM 分析上限（Top N 截断；当前 yaml 未收录，代码常量兜底）。</summary>
    public int TopN = 100;

    /// <summary>兜底默认值：与 02_strategy_config.yaml 当前值一致。yaml 缺失/缺项时用。</summary>
    public static PoolThresholds Defaults() => new()
    {
        MinDy = 3.0,
        MaxPb = 4.0,      // 2026-08-31 放宽 2→3→4
        MinRoeA = 10.0,
        MinMv = 30.0,
        MinNpCagr = 15.0,
        MinRevCagr = 10.0,
        MinNpYoy = 10.0,
        MinRoeB = 6.0,
        MinOcfRatio = 0.4,
        MaxPe = 80.0,
        MaxPeg = 1.5,
        TopN = 100,
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
            var root = MiniYaml.Parse(File.ReadAllText(yamlPath));
            // A 桶：bucket_A.stock_filters.*
            if (root.TryGet("bucket_A", out var a) && a.TryGet("stock_filters", out var af))
            {
                d.MinDy = af.Num("dividend_yield_ttm_min_pct", d.MinDy);
                d.MaxPb = af.Num("pb_max", d.MaxPb);
                d.MinRoeA = af.Num("roe_5y_avg_min_pct", d.MinRoeA);
            }
            // B 桶：bucket_B.batch_screen
            if (root.TryGet("bucket_B", out var b) && b.TryGet("batch_screen", out var bs))
            {
                d.MinMv = bs.Num("total_mv_min_yi", d.MinMv);
                d.MinNpCagr = bs.Num("np_cagr_3y_min_pct", d.MinNpCagr);
                d.MinRevCagr = bs.Num("rev_cagr_3y_min_pct", d.MinRevCagr);
                d.MinNpYoy = bs.Num("latest_np_yoy_min_pct", d.MinNpYoy);
                d.MinRoeB = bs.Num("roe_annualized_min_pct", d.MinRoeB);
                d.MinOcfRatio = bs.Num("ocf_to_np_annual_min", d.MinOcfRatio);
                d.MaxPe = bs.Num("pe_ttm_max", d.MaxPe);
                d.MaxPeg = bs.Num("peg_max", d.MaxPeg);
            }
        }
        catch
        {
            return Defaults(); // 解析失败整体回退，不带着半套脏值跑
        }
        return d;
    }
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

        public bool TryGet(string key, out Node node) => Map.TryGetValue(key, out node!);

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

    /// <summary>解析 yaml 文本为嵌套 Node 树。列表行（- xxx）忽略——阈值读取只关心 mapping 与标量。</summary>
    public static Node Parse(string text)
    {
        var root = new Node();
        var stack = new List<(int Indent, Node Node)> { (0, root) };
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith("#")) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;                       // 无冒号（列表项/续行）跳过
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
                // 中间节点（子 mapping）
                parent.Map[key] = node;
                stack.Add((indent, node));
            }
            else
            {
                // 叶子标量（列表值如 [20, 80] 存原串，Num 解析不了会回退默认，无碍）
                node.Scalar = val;
                parent.Map[key] = node;
            }
        }
        return root;
    }
}
