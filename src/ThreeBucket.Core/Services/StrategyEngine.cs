using System.Text.Json;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 条件树策略引擎（Python desktop/legacy-pyqt/strategy_engine.py 的 C# 移植版）。
///
/// 一、条件树 Schema
///   逻辑节点：{"logic":"and|or|not","children":[子节点...]}
///   叶子节点：{"indicator":"ma","params":{"period":5},"operator":">",
///              "value":10.5 或 {"indicator":"ma","params":{"period":10}}}
///   交叉事件：operator 为 cross_up/cross_down 时比较「今天 vs 昨天」的翻转：
///     cross_up   = 昨天 A &lt; B  且 今天 A &gt;= B（金叉）
///     cross_down = 昨天 A &gt; B  且 今天 A &lt;= B（死叉）
///
/// 二、三值逻辑
///   指标数据不足（K 线不够长、无持仓成本等）叶子返回 null：
///     and：任一 false → false；全 true → true；否则 null（数据不足，策略跳过）
///     or ：任一 true → true；全 false → false；否则 null
///     not：null → null
///   配置错误（指标名不存在 / 参数不合法 / JSON 解析失败 / 结构错误）抛
///   StrategyConfigError —— 由调用方捕获并上报，绝不静默当作 false。
///
/// 三、offset 语义
///   所有指标计算都支持 offset：0 = 最新一根 K 线（今天），1 = 昨天……
///   strategies.csv 的 condition 列存条件树 JSON；旧扁平三列
///   (indicator/operator/threshold) 自动经 LEGACY_MAP 映射成等价叶子。
/// </summary>
public static class StrategyEngine
{
    public const int MaxDepth = 12; // 条件树最大嵌套深度（防手写 JSON 深层套娃）

    private static readonly HashSet<string> LogicOps = new(StringComparer.Ordinal) { "and", "or", "not" };
    private static readonly HashSet<string> CompareOps = new(StringComparer.Ordinal) { "<", "<=", ">", ">=", "==" };
    private static readonly HashSet<string> CrossOps = new(StringComparer.Ordinal) { "cross_up", "cross_down" };

    public static readonly string[] MacdFields = ["dif", "dea", "hist"];

    // ── 指标注册表 ───────────────────────────────────────────────

    private sealed record Def(
        string Label,
        string[] IntParams,
        string[] StrParams,
        Func<IndicatorContext, CondParams, int, double?> Calc);

    private static readonly Dictionary<string, Def> Defs = new(StringComparer.Ordinal)
    {
        ["price"] = new("现价(元)", [], [],
            (c, p, o) => c.GetPrice(o)),
        ["day_change_pct"] = new("当日涨跌幅(%)", [], [],
            (c, p, o) => c.GetDayChange(o)),
        ["pct_change"] = new("日涨跌幅(%)·K线", [], [],
            (c, p, o) => c.GetPctChange(o)),
        ["pe_ttm"] = new("市盈率TTM", [], [],
            (c, p, o) => o == 0 ? c.QuotePeTtm : null),
        ["cost_basis_gain"] = new("持仓浮盈(%)", [], [],
            (c, p, o) => o == 0 ? c.GetCostBasisGain() : null),
        ["ma"] = new("均线MA", ["period"], [],
            (c, p, o) => c.GetMa(p.Int("period", 5), o)),
        ["bias"] = new("乖离率(现价vs MA,%)", ["period"], [],
            (c, p, o) => c.GetBias(p.Int("period", 20), o)),
        ["ma_spread"] = new("均线发散(MA快/MA慢,%)", ["fast", "slow"], [],
            (c, p, o) => c.GetMaSpread(p.Int("fast", 5), p.Int("slow", 60), o)),
        ["macd"] = new("MACD", [], ["field"],
            (c, p, o) => c.GetMacd(p.Str("field", "dif"), o)),
        ["volume_ratio"] = new("量比(vs N日均量)", ["window"], [],
            (c, p, o) => c.GetVolumeRatio(p.Int("window", 20), o)),
        ["volume"] = new("成交量", [], [],
            (c, p, o) => c.GetVolume(o)),
        ["drawdown_from_high"] = new("距N日高点回撤(%)", ["window"], [],
            (c, p, o) => c.GetDrawdownFromHigh(p.Int("window", 180), o)),
        ["gain_from_low"] = new("距N日低点涨幅(%)", ["window"], [],
            (c, p, o) => c.GetGainFromLow(p.Int("window", 180), o)),
    };

    // 旧 strategies.csv 扁平三列 indicator key → 新指标 key + 参数
    private static readonly Dictionary<string, (string Key, IReadOnlyDictionary<string, int> Params)> LegacyMap = new(StringComparer.Ordinal)
    {
        ["price"] = ("price", new Dictionary<string, int>()),
        ["day_change_pct"] = ("day_change_pct", new Dictionary<string, int>()),
        ["pe_ttm"] = ("pe_ttm", new Dictionary<string, int>()),
        ["cost_basis_gain"] = ("cost_basis_gain", new Dictionary<string, int>()),
        ["price_vs_ma20"] = ("bias", new Dictionary<string, int> { ["period"] = 20 }),
        ["price_vs_ma60"] = ("bias", new Dictionary<string, int> { ["period"] = 60 }),
        ["drawdown_from_high_180d"] = ("drawdown_from_high", new Dictionary<string, int> { ["window"] = 180 }),
        ["gain_from_low_180d"] = ("gain_from_low", new Dictionary<string, int> { ["window"] = 180 }),
        ["volume_ratio_20d"] = ("volume_ratio", new Dictionary<string, int> { ["window"] = 20 }),
    };

    // ============================================================
    // 求值
    // ============================================================

    /// <summary>递归求值条件树：true/false/null（null=数据不足，策略跳过）。</summary>
    public static bool? EvaluateCondition(JsonElement node, IndicatorContext ctx, int depth = 0)
    {
        if (depth > MaxDepth)
            throw new StrategyConfigError($"条件树嵌套超过 {MaxDepth} 层");
        if (node.ValueKind != JsonValueKind.Object)
            throw new StrategyConfigError($"条件节点必须是对象: {node}");

        if (node.TryGetProperty("logic", out var logicEl))
        {
            var logic = (logicEl.GetString() ?? "").ToLowerInvariant();
            if (!LogicOps.Contains(logic))
                throw new StrategyConfigError($"未知逻辑运算 {logicEl.GetString()}");
            if (!node.TryGetProperty("children", out var childrenEl)
                || childrenEl.ValueKind != JsonValueKind.Array || childrenEl.GetArrayLength() == 0)
                throw new StrategyConfigError($"{logic} 节点缺少 children");
            var children = childrenEl.EnumerateArray().ToList();
            if (logic == "not" && children.Count != 1)
                throw new StrategyConfigError("not 节点必须恰好 1 个子条件");

            var vals = children.Select(c => EvaluateCondition(c, ctx, depth + 1)).ToList();
            if (logic == "and")
            {
                if (vals.Any(v => v == false)) return false;
                return vals.All(v => v == true) ? true : null;
            }
            if (logic == "or")
            {
                if (vals.Any(v => v == true)) return true;
                return vals.All(v => v == false) ? false : null;
            }
            var v = vals[0]; // not
            return v is null ? null : !v;
        }

        // 叶子节点：单一比较
        var key = node.TryGetProperty("indicator", out var indEl) ? indEl.GetString() : null;
        var where = $"叶子[{key}]";
        var op = node.TryGetProperty("operator", out var opEl) ? opEl.GetString() : null;
        if (op is null || !(CompareOps.Contains(op) || CrossOps.Contains(op)))
            throw new StrategyConfigError($"{where}: 非法操作符 {op}");
        if (!node.TryGetProperty("value", out var valueEl))
            throw new StrategyConfigError($"{where}: 缺少 value");

        var left = EvalIndicator(key, ParamsOf(node), ctx, 0, where);
        if (CrossOps.Contains(op!))
        {
            var leftPrev = EvalIndicator(key, ParamsOf(node), ctx, 1, where);
            var right = ResolveValue(valueEl, ctx, 0, where);
            var rightPrev = ResolveValue(valueEl, ctx, 1, where);
            if (left is null || leftPrev is null || right is null || rightPrev is null)
                return null; // 今天/昨天任一值缺失 → 无法判定
            if (op == "cross_up")
                return leftPrev < rightPrev && left >= right;
            return leftPrev > rightPrev && left <= right; // cross_down
        }

        var rhs = ResolveValue(valueEl, ctx, 0, where);
        if (left is null || rhs is null) return null;
        var l = left.Value;
        var r = rhs.Value;
        return op switch
        {
            "<" => l < r,
            "<=" => l <= r,
            ">" => l > r,
            ">=" => l >= r,
            _ => Math.Abs(l - r) < 1e-9, // "==" 浮点近似相等
        };
    }

    private static double? EvalIndicator(string? key, CondParams p, IndicatorContext ctx,
        int offset, string where)
    {
        if (string.IsNullOrEmpty(key) || !Defs.TryGetValue(key, out var def))
            throw new StrategyConfigError($"{where}: 未知指标 {key}");
        return def.Calc(ctx, p, offset);
    }

    /// <summary>叶子右值解析：数字 / 数字串 → 常量；对象 → 指标引用（同 offset 取值）。</summary>
    private static double? ResolveValue(JsonElement v, IndicatorContext ctx, int offset, string where)
    {
        if (v.ValueKind == JsonValueKind.Object)
        {
            var key = v.TryGetProperty("indicator", out var indEl) ? indEl.GetString() : null;
            return EvalIndicator(key, ParamsOf(v), ctx, offset, $"{where}.value");
        }
        if (v.ValueKind is JsonValueKind.Number)
            return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
            throw new StrategyConfigError($"{where}.value 不是数字 {s}");
        }
        throw new StrategyConfigError($"{where}.value 类型非法 {v}");
    }

    /// <summary>策略行求值入口：优先 condition 列(JSON 条件树)，否则回退旧三列。
    /// 旧三列会先映射成一棵等价叶子再求值，保证存量策略零迁移可用。</summary>
    public static bool? EvaluateStrategy(Strategy s, IndicatorContext ctx)
    {
        var raw = (s.Condition ?? "").Trim();
        if (raw.Length > 0)
        {
            JsonElement node;
            try
            {
                node = JsonDocument.Parse(raw).RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new StrategyConfigError($"condition JSON 解析失败: {e.Message}");
            }
            return EvaluateCondition(node, ctx);
        }

        var legacyKey = (s.Indicator ?? "").Trim();
        if (legacyKey.Length == 0)
            throw new StrategyConfigError("策略既无 condition 也无 indicator");
        if (!LegacyMap.TryGetValue(legacyKey, out var map))
            throw new StrategyConfigError($"未知指标 {legacyKey}");
        if (!double.TryParse((s.Threshold ?? "").Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var threshold))
            return null; // 阈值缺失/非法：数据不足语义，跳过（与旧行为一致）
        var op = (s.Operator ?? "").Trim();
        if (!(CompareOps.Contains(op) || CrossOps.Contains(op)))
            throw new StrategyConfigError($"叶子[{map.Key}]: 非法操作符 {op}");

        var p = CondParams.FromInt(map.Params);
        var left = EvalIndicator(map.Key, p, ctx, 0, "legacy");
        if (op is "cross_up" or "cross_down")
        {
            var leftPrev = EvalIndicator(map.Key, p, ctx, 1, "legacy");
            if (left is null || leftPrev is null) return null;
            return op == "cross_up"
                ? leftPrev < threshold && left >= threshold
                : leftPrev > threshold && left <= threshold;
        }
        if (left is null) return null;
        var lv = left.Value;
        return op switch
        {
            "<" => lv < threshold,
            "<=" => lv <= threshold,
            ">" => lv > threshold,
            ">=" => lv >= threshold,
            _ => Math.Abs(lv - threshold) < 1e-9,
        };
    }

    // ============================================================
    // 校验（不取行情数据）：非法抛 StrategyConfigError。
    // 供 UI 保存策略前自检，把配置错误拦在写入 CSV 之前。
    // ============================================================

    public static void ValidateCondition(JsonElement node, int depth = 0)
    {
        if (depth > MaxDepth)
            throw new StrategyConfigError($"条件树嵌套超过 {MaxDepth} 层");
        if (node.ValueKind != JsonValueKind.Object)
            throw new StrategyConfigError($"条件节点必须是对象: {node}");

        if (node.TryGetProperty("logic", out var logicEl))
        {
            var logic = (logicEl.GetString() ?? "").ToLowerInvariant();
            if (!LogicOps.Contains(logic))
                throw new StrategyConfigError($"未知逻辑运算 {logicEl.GetString()}");
            if (!node.TryGetProperty("children", out var childrenEl)
                || childrenEl.ValueKind != JsonValueKind.Array || childrenEl.GetArrayLength() == 0)
                throw new StrategyConfigError($"{logic} 节点缺少 children");
            var count = childrenEl.GetArrayLength();
            if (logic == "not" && count != 1)
                throw new StrategyConfigError("not 节点必须恰好 1 个子条件");
            foreach (var c in childrenEl.EnumerateArray())
                ValidateCondition(c, depth + 1);
            return;
        }

        var key = node.TryGetProperty("indicator", out var indEl) ? indEl.GetString() : null;
        ValidateLeafIndicator(key, ParamsOf(node), $"叶子[{key}]");
        var op = node.TryGetProperty("operator", out var opEl) ? opEl.GetString() : null;
        if (op is null || !(CompareOps.Contains(op) || CrossOps.Contains(op)))
            throw new StrategyConfigError($"叶子[{key}]: 非法操作符 {op}");
        if (!node.TryGetProperty("value", out var v))
            throw new StrategyConfigError($"叶子[{key}]: 缺少 value");
        if (v.ValueKind == JsonValueKind.Object)
        {
            var vkey = v.TryGetProperty("indicator", out var vEl) ? vEl.GetString() : null;
            ValidateLeafIndicator(vkey, ParamsOf(v), $"叶子[{key}].value");
        }
        else if (v.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            if (v.ValueKind == JsonValueKind.String
                && !double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new StrategyConfigError($"叶子[{key}].value 不是数字 {v.GetString()}");
        }
        else
            throw new StrategyConfigError($"叶子[{key}].value 类型非法 {v}");
    }

    private static void ValidateLeafIndicator(string? key, CondParams p, string where)
    {
        if (string.IsNullOrEmpty(key) || !Defs.TryGetValue(key, out var def))
            throw new StrategyConfigError($"{where}: 未知指标 {key}");
        foreach (var name in p.Keys)
            if (!def.IntParams.Contains(name) && !def.StrParams.Contains(name))
                throw new StrategyConfigError($"{where}: 指标 {key} 不支持参数 {name}");
        if (def.StrParams.Contains("field"))
        {
            var field = p.Str("field", "dif").ToLowerInvariant();
            if (!MacdFields.Contains(field))
                throw new StrategyConfigError($"{where}: macd.field 必须是 dif/dea/hist");
        }
    }

    /// <summary>校验策略定义（condition 树或旧三列），返回错误消息；合法返回 null。</summary>
    public static string? ValidateStrategy(Strategy s)
    {
        var raw = (s.Condition ?? "").Trim();
        if (raw.Length > 0)
        {
            try
            {
                ValidateCondition(JsonDocument.Parse(raw).RootElement.Clone());
                return null;
            }
            catch (Exception e) // JsonException + StrategyConfigError
            {
                return e.Message;
            }
        }
        var legacyKey = (s.Indicator ?? "").Trim();
        if (legacyKey.Length == 0) return "策略既无 condition 也无 indicator";
        if (!LegacyMap.ContainsKey(legacyKey)) return $"未知指标 {legacyKey}";
        return null;
    }

    // ============================================================
    // 降级展示：条件树 → 人类可读文本
    // ============================================================

    private static readonly Dictionary<string, string> OpCn = new(StringComparer.Ordinal)
    {
        ["cross_up"] = "上穿", ["cross_down"] = "下穿",
    };

    private static string IndDisplay(string key, CondParams p)
    {
        switch (key)
        {
            case "ma": return $"MA{p.Int("period", 5)}";
            case "bias": return $"乖离率MA{p.Int("period", 20)}(%)";
            case "ma_spread": return $"均线发散MA{p.Int("fast", 5)}/MA{p.Int("slow", 60)}(%)";
            case "macd": return $"MACD.{p.Str("field", "dif").ToUpperInvariant()}";
            case "volume_ratio": return $"量比({p.Int("window", 20)}日均量)";
            case "drawdown_from_high": return $"{p.Int("window", 180)}日高点回撤(%)";
            case "gain_from_low": return $"{p.Int("window", 180)}日低点涨幅(%)";
            default: return Defs.TryGetValue(key, out var def) ? def.Label : key;
        }
    }

    private static string ValueDisplay(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Object)
        {
            var key = v.TryGetProperty("indicator", out var indEl) ? indEl.GetString() ?? "" : "";
            return IndDisplay(key, ParamsOf(v));
        }
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble().ToString("G7");
        return v.ToString();
    }

    /// <summary>条件树 → 中文可读字符串（策略表格"条件"列 / 提醒展示用）。
    /// 嵌套时按需加括号：and 里的 or 子树、or 里的 and 子树会带括号。</summary>
    public static string DescribeCondition(JsonElement node, string? parent = null)
    {
        if (node.ValueKind != JsonValueKind.Object) return node.ToString();
        if (node.TryGetProperty("logic", out var logicEl))
        {
            var logic = (logicEl.GetString() ?? "").ToLowerInvariant();
            var subs = (node.TryGetProperty("children", out var ch)
                        && ch.ValueKind == JsonValueKind.Array
                        ? ch.EnumerateArray().Select(c => DescribeCondition(c, logic)).ToList()
                        : []);
            if (subs.Count == 0) return "(空)";
            if (logic == "not") return $"非({subs[0]})";
            var joiner = logic == "and" ? " 且 " : " 或 ";
            var text = string.Join(joiner, subs);
            var needParen = (parent == "and" && logic == "or") || (parent == "or" && logic == "and");
            return needParen ? $"({text})" : text;
        }
        var op = node.TryGetProperty("operator", out var opEl) ? opEl.GetString() ?? "" : "";
        var opTxt = OpCn.GetValueOrDefault(op, op);
        var key = node.TryGetProperty("indicator", out var indEl) ? indEl.GetString() ?? "" : "";
        var left = IndDisplay(key, ParamsOf(node));
        var val = node.TryGetProperty("value", out var vEl) ? ValueDisplay(vEl) : "";
        return $"{left} {opTxt} {val}";
    }

    /// <summary>策略行 → 中文条件文本（兼容 condition 树与旧三列）。</summary>
    public static string DescribeStrategy(Strategy s)
    {
        var raw = (s.Condition ?? "").Trim();
        if (raw.Length > 0)
        {
            try { return DescribeCondition(JsonDocument.Parse(raw).RootElement.Clone()); }
            catch { return raw; }
        }
        var key = (s.Indicator ?? "").Trim();
        var label = LegacyMap.TryGetValue(key, out var map)
            ? IndDisplay(map.Key, CondParams.FromInt(map.Params))
            : key;
        return $"{label} {s.Operator} {s.Threshold}";
    }

    // ── params 读取工具 ──────────────────────────────────────────

    private static CondParams ParamsOf(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("params", out var p)
            && p.ValueKind == JsonValueKind.Object)
            return new CondParams(p);
        return CondParams.Empty;
    }
}

/// <summary>叶子指标参数包装：数值参数按 int、macd.field 按字符串读取，均带默认值。</summary>
public sealed class CondParams
{
    public static readonly CondParams Empty = new(null);

    private readonly JsonElement? _obj;

    public CondParams(JsonElement? obj) => _obj = obj;

    public static CondParams FromInt(IReadOnlyDictionary<string, int>? p)
        => p is null || p.Count == 0
            ? Empty
            : new CondParams(JsonSerializer.SerializeToElement(p));

    public IEnumerable<string> Keys
    {
        get
        {
            if (_obj is { } o && o.ValueKind == JsonValueKind.Object)
                foreach (var kv in o.EnumerateObject())
                    yield return kv.Name;
        }
    }

    public int Int(string name, int def)
    {
        if (_obj is not { } o || o.ValueKind != JsonValueKind.Object) return def;
        if (!o.TryGetProperty(name, out var el)) return def;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)el.GetDouble(),
            JsonValueKind.String when int.TryParse(el.GetString(), out var i) => i,
            _ => def,
        };
    }

    public string Str(string name, string def)
    {
        if (_obj is not { } o || o.ValueKind != JsonValueKind.Object) return def;
        return o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? def
            : def;
    }
}

/// <summary>策略条件树配置错误（指标名/参数/结构/JSON 非法）。调用方须捕获上报，不得静默当 false。</summary>
public class StrategyConfigError : Exception
{
    public StrategyConfigError(string message) : base(message) { }
}
