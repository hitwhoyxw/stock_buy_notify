using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.Demo;

/// <summary>
/// StrategyEngine / IndicatorContext 离线回归（对齐 Python test_strategy_engine.py 用例）。
/// 全部基于合成 K 线，不依赖网络；MACD 用独立参考实现对照，防止引擎 EMA 写错。
/// 条件树用 C# 对象构造再序列化（避免手写 JSON 转义出错）。Run() 返回是否全部通过。
/// </summary>
public static class StrategyEngineRegression
{
    public static bool Run()
    {
        Console.WriteLine("=== 策略引擎离线回归（条件树 / 金叉 / 三值逻辑 / 旧格式兼容） ===\n");
        var ok = true;
        ok &= Case1MaAlignmentWithVolume();
        ok &= Case2MacdCrossUp();
        ok &= Case3DoubleMaGoldenCross();
        ok &= Case4CsvStrategies();
        ok &= Case5LegacyS1();
        ok &= Case6DescribeAndValidate();
        Console.WriteLine(ok ? "\n策略引擎回归：全部通过 ✅" : "\n策略引擎回归：存在失败 ❌");
        return ok;
    }

    // ── 条件树对象构造（SerializeToElement，无手写 JSON）────────

    private static System.Text.Json.JsonElement Json(object obj)
        => System.Text.Json.JsonSerializer.SerializeToElement(obj);

    private static Dictionary<string, object> And(params Dictionary<string, object>[] children)
        => new() { ["logic"] = "and", ["children"] = children };

    private static Dictionary<string, object> Or(params Dictionary<string, object>[] children)
        => new() { ["logic"] = "or", ["children"] = children };

    private static Dictionary<string, object> Not(Dictionary<string, object> child)
        => new() { ["logic"] = "not", ["children"] = new[] { child } };

    private static Dictionary<string, object> Ref(string indicator,
        params (string Key, object Val)[] pars)
    {
        var node = new Dictionary<string, object> { ["indicator"] = indicator };
        if (pars.Length > 0)
            node["params"] = pars.ToDictionary(p => p.Key, p => p.Val);
        return node;
    }

    private static Dictionary<string, object> Leaf(string indicator, string op, object value,
        params (string Key, object Val)[] pars)
    {
        var node = Ref(indicator, pars);
        node["operator"] = op;
        node["value"] = value;
        return node;
    }

    // ── 合成数据工具 ────────────────────────────────────────────

    private static List<DailyBar> MakeKline(IReadOnlyList<double> closes,
        IReadOnlyList<double>? volumes = null)
    {
        var n = closes.Count;
        var vols = volumes ?? Enumerable.Repeat(10000.0, n).ToArray();
        var date = new DateTime(2026, 1, 1);
        var bars = new List<DailyBar>(n);
        for (var i = 0; i < n; i++)
        {
            var c = closes[i];
            bars.Add(new DailyBar(date.AddDays(i), c * 0.995, c, c * 1.01, c * 0.99, vols[i]));
        }
        return bars;
    }

    /// <summary>独立参考 MACD（与 pandas ewm(span, adjust=False) 同口径）。</summary>
    private static (double[] Dif, double[] Dea) RefMacd(double[] c)
    {
        double[] Ema(double[] x, int span)
        {
            var a = 2.0 / (span + 1);
            var e = new double[x.Length];
            e[0] = x[0];
            for (var i = 1; i < x.Length; i++) e[i] = e[i - 1] + a * (x[i] - e[i - 1]);
            return e;
        }
        var dif = new double[c.Length];
        var ema12 = Ema(c, 12);
        var ema26 = Ema(c, 26);
        for (var i = 0; i < c.Length; i++) dif[i] = ema12[i] - ema26[i];
        return (dif, Ema(dif, 9));
    }

    private static string P(bool? v) => v switch { null => "None", true => "True", false => "False" };

    // ── 用例1：均线多头排列 AND 量比（对齐 Python 用例1）─────────

    private static bool Case1MaAlignmentWithVolume()
    {
        // 80 天匀速上升 → MA5>MA10>MA20>MA60 且现价站上 MA5；末日 3 倍量
        var closes = Enumerable.Range(0, 80).Select(i => 10.0 * Math.Pow(1.003, i)).ToList();
        var vols = Enumerable.Repeat(10000.0, 79).Append(30000.0).ToList();
        var ctx = new IndicatorContext(closes[^1], 3.0, MakeKline(closes, vols));

        System.Text.Json.JsonElement Tree(double minVolumeRatio) => Json(And(
            Leaf("ma", ">", Ref("ma", ("period", 10)), ("period", 5)),
            Leaf("ma", ">", Ref("ma", ("period", 20)), ("period", 10)),
            Leaf("ma", ">", Ref("ma", ("period", 60)), ("period", 20)),
            Leaf("price", ">", Ref("ma", ("period", 5))),
            Leaf("volume_ratio", ">=", minVolumeRatio, ("window", 20))));

        var tree = Tree(2);
        if (StrategyEngine.EvaluateCondition(tree, ctx) is not true)
        { Console.WriteLine("FAIL 用例1: 多头排列+放量应触发"); return false; }

        // 量比阈值提到 5 → and 短路为 False
        if (StrategyEngine.EvaluateCondition(Tree(5), ctx) is not false)
        { Console.WriteLine("FAIL 用例1: 量比不足应不触发"); return false; }

        // 下跌序列 → 多头排列不成立
        var down = Enumerable.Range(0, 80).Select(i => 10.0 * Math.Pow(0.997, i)).ToList();
        var ctx2 = new IndicatorContext(down[^1], -0.3, MakeKline(down));
        if (StrategyEngine.EvaluateCondition(tree, ctx2) is not false)
        { Console.WriteLine("FAIL 用例1: 空头走势不应触发多头排列"); return false; }

        Console.WriteLine("PASS 用例1: 均线多头排列 AND 量比>=2");
        return true;
    }

    // ── 用例2：MACD 金叉 cross_up（V 型 + 参考实现理论对照）────

    private static bool Case2MacdCrossUp()
    {
        // V 型：60 天阴跌 + 30 天强反弹
        var closes = Enumerable.Range(0, 60).Select(i => 20.0 * Math.Pow(0.99, i))
            .Concat(Enumerable.Range(1, 30).Select(i => 20.0 * Math.Pow(0.99, 59) * Math.Pow(1.012, i)))
            .ToList();
        var (dif, dea) = RefMacd(closes.ToArray());
        var crosses = new List<int>();
        for (var j = 1; j < closes.Count; j++)
            if (dif[j - 1] < dea[j - 1] && dif[j] >= dea[j]) crosses.Add(j);
        if (crosses.Count == 0)
        { Console.WriteLine("FAIL 用例2: V型数据应存在金叉点（数据构造不合理）"); return false; }

        var j0 = crosses[0];
        var tree = Json(Leaf("macd", "cross_up", Ref("macd", ("field", "dea")), ("field", "dif")));

        // 截断到金叉当日（最后一天 = j0）→ True
        var ctxHit = new IndicatorContext(closes[j0], 1.2, MakeKline(closes.Take(j0 + 1).ToList()));
        if (StrategyEngine.EvaluateCondition(tree, ctxHit) is not true)
        { Console.WriteLine($"FAIL 用例2: 金叉当日应触发（index={j0}）"); return false; }

        // 截断到前一天 → False（金叉未发生）
        var ctxPre = new IndicatorContext(closes[j0 - 1], -0.8, MakeKline(closes.Take(j0).ToList()));
        if (StrategyEngine.EvaluateCondition(tree, ctxPre) is not false)
        { Console.WriteLine("FAIL 用例2: 金叉前一日不应触发"); return false; }

        // 死叉反向：金叉当日 cross_down 应为 False
        var treeDn = Json(Leaf("macd", "cross_down", Ref("macd", ("field", "dea")), ("field", "dif")));
        if (StrategyEngine.EvaluateCondition(treeDn, ctxHit) is not false)
        { Console.WriteLine("FAIL 用例2: 金叉当日死叉应为 False"); return false; }

        Console.WriteLine($"PASS 用例2: MACD金叉 cross_up（金叉点 index={j0}，参考实现对照一致）");
        return true;
    }

    // ── 用例3：双均线金叉（S6 条件：MA5 上穿 MA10 且现价站上 MA10）──

    private static bool Case3DoubleMaGoldenCross()
    {
        var tree = Json(And(
            Leaf("ma", "cross_up", Ref("ma", ("period", 10)), ("period", 5)),
            Leaf("price", ">", Ref("ma", ("period", 10)))));

        // 30 天阴跌 + n 天反弹，扫描 n 找到「昨天 MA5<MA10 且今天 MA5>=MA10」的构造
        var baseDown = Enumerable.Range(0, 30).Select(i => 10.0 * Math.Pow(0.99, i)).ToList();
        for (var n = 2; n <= 15; n++)
        {
            var closes = baseDown
                .Concat(Enumerable.Range(1, n).Select(k => baseDown[^1] * Math.Pow(1.05, k)))
                .ToList();
            var ctx = new IndicatorContext(closes[^1], 5.0, MakeKline(closes));
            if (ctx.GetMa(5, 1) >= ctx.GetMa(10, 1) || ctx.GetMa(5, 0) < ctx.GetMa(10, 0))
                continue; // 不是「恰好当日金叉」

            if (StrategyEngine.EvaluateCondition(tree, ctx) is not true)
            { Console.WriteLine($"FAIL 用例3: 双均线金叉当日应触发（n={n}）"); return false; }

            // 截掉最后一天（回到金叉前）→ False
            var ctxPre = new IndicatorContext(closes[^2], 0, MakeKline(closes.Take(closes.Count - 1).ToList()));
            if (StrategyEngine.EvaluateCondition(tree, ctxPre) is not false)
            { Console.WriteLine("FAIL 用例3: 金叉前一日不应触发"); return false; }

            Console.WriteLine($"PASS 用例3: 双均线金叉（MA5 上穿 MA10，反弹天数 n={n}）");
            return true;
        }
        Console.WriteLine("FAIL 用例3: 扫描 2..15 天均未构造出当日金叉（数据设计有误）");
        return false;
    }

    // ── 用例4：strategies.csv 的 S6–S8 端到端（Validate + 构造数据触发）──

    private static bool Case4CsvStrategies()
    {
        // 定位 data/strategies.csv（与 Demo 主程序同规则：向上找含 scripts/ 的目录）
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "scripts"))) break;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        var store = new Core.Data.DataStore(Path.Combine(dir, "data"));
        var strategies = store.ListStrategies();
        var s6 = strategies.FirstOrDefault(s => s.Id == "S6");
        var s7 = strategies.FirstOrDefault(s => s.Id == "S7");
        var s8 = strategies.FirstOrDefault(s => s.Id == "S8");
        if (s6 is null || s7 is null || s8 is null)
        { Console.WriteLine("FAIL 用例4: strategies.csv 缺少 S6/S7/S8"); return false; }

        // CSV 三条策略的 condition JSON 必须全部通过结构校验
        foreach (var s in new[] { s6, s7, s8 })
        {
            var err = StrategyEngine.ValidateStrategy(s);
            if (err != null)
            { Console.WriteLine($"FAIL 用例4: {s.Id} 校验失败：{err}"); return false; }
        }

        // S8 均线多头排列：80 天匀速上涨 + 末日 2 倍量 → 触发
        var closes = Enumerable.Range(0, 80).Select(i => 10.0 * Math.Pow(1.003, i)).ToList();
        var vols = Enumerable.Repeat(10000.0, 79).Append(20000.0).ToList();
        var ctx = new IndicatorContext(closes[^1], 3.0, MakeKline(closes, vols));
        if (StrategyEngine.EvaluateStrategy(s8, ctx) is not true)
        { Console.WriteLine("FAIL 用例4: S8 多头排列+量比1.5 应触发"); return false; }

        // S8 横盘量平 → 不触发（量比 1.0）
        var flat = Enumerable.Repeat(10.0, 80).ToList();
        var ctxFlat = new IndicatorContext(10.0, 0, MakeKline(flat));
        if (StrategyEngine.EvaluateStrategy(s8, ctxFlat) is not false)
        { Console.WriteLine("FAIL 用例4: S8 横盘无量不应触发"); return false; }

        // S7 MACD金叉放量：V 型 + 末日 2 倍量，理论对照（金叉未必落在最后一日）
        var vcloses = Enumerable.Range(0, 60).Select(i => 20.0 * Math.Pow(0.99, i))
            .Concat(Enumerable.Range(1, 30).Select(i => 20.0 * Math.Pow(0.99, 59) * Math.Pow(1.012, i)))
            .ToList();
        var vvols = Enumerable.Repeat(10000.0, vcloses.Count - 1).Append(20000.0).ToList();
        var vctx = new IndicatorContext(vcloses[^1], 1.2, MakeKline(vcloses, vvols));
        var (dif, dea) = RefMacd(vcloses.ToArray());
        var last = vcloses.Count - 1;
        var expectCross = dif[last - 1] < dea[last - 1] && dif[last] >= dea[last];
        var expectVol = vctx.GetVolumeRatio(20, 0) ?? 0;
        var expect = expectCross && expectVol >= 1.5;
        var actual = StrategyEngine.EvaluateStrategy(s7, vctx) == true;
        if (actual != expect)
        { Console.WriteLine($"FAIL 用例4: S7 应与理论值一致（理论={P(expect)}，实际={P(actual)}）"); return false; }

        Console.WriteLine("PASS 用例4: CSV S6–S8 校验通过，S7/S8 触发与理论一致");
        return true;
    }

    // ── 用例5：旧扁平三列兼容（S1 跌破MA60）────────────────────

    private static bool Case5LegacyS1()
    {
        // 79 天横盘 10 元 + 末日跌到 9 元 → bias(60) = -10% < 0 触发
        var closes = Enumerable.Repeat(10.0, 79).Append(9.0).ToList();
        var ctx = new IndicatorContext(9.0, -10.0, MakeKline(closes));
        var s1 = new Strategy
        {
            Id = "S1", Name = "跌破MA60减仓", Type = "sell",
            Indicator = "price_vs_ma60", Operator = "<", Threshold = "0",
        };
        if (StrategyEngine.EvaluateStrategy(s1, ctx) is not true)
        { Console.WriteLine("FAIL 用例5: 跌破MA60应触发"); return false; }

        // 阈值非法 → null（数据不足语义，跳过不报错）
        s1.Threshold = "abc";
        if (StrategyEngine.EvaluateStrategy(s1, ctx) is not null)
        { Console.WriteLine("FAIL 用例5: 阈值非法应为 None"); return false; }

        // 未知 legacy 指标 → StrategyConfigError
        s1.Threshold = "0"; s1.Indicator = "rsi_14";
        try
        {
            StrategyEngine.EvaluateStrategy(s1, ctx);
            Console.WriteLine("FAIL 用例5: 未知指标应抛 StrategyConfigError");
            return false;
        }
        catch (StrategyConfigError) { /* 预期 */ }

        Console.WriteLine("PASS 用例5: 旧三列兼容 + 阈值非法 None + 未知指标报错");
        return true;
    }

    // ── 用例6：describe 人类可读文本 + 校验错误信息 ─────────────

    private static bool Case6DescribeAndValidate()
    {
        var tree = Json(And(
            Leaf("ma", "cross_up", Ref("ma", ("period", 10)), ("period", 5)),
            Or(Leaf("volume_ratio", ">=", 2, ("window", 20)),
               Leaf("day_change_pct", ">", 5)),
            Not(Leaf("bias", "<", -5, ("period", 20)))));
        var text = StrategyEngine.DescribeCondition(tree);
        if (!text.Contains("MA5 上穿 MA10") || !text.Contains("且") || !text.Contains("或")
            || !text.Contains("(") || !text.Contains("非(乖离率MA20(%) < -5)"))
        { Console.WriteLine($"FAIL 用例6: describe 文本异常: {text}"); return false; }

        // 校验：未知指标 / 非法参数 / 缺 value / 非法 field
        var bad1 = Json(Leaf("rsi_14", ">", 30));
        var bad2 = Json(Leaf("ma", ">", 10, ("period", 5), ("step", 2)));
        var bad3Node = Ref("ma", ("period", 5)); bad3Node["operator"] = ">"; // 缺 value
        var bad3 = Json(bad3Node);
        var bad4 = Json(Leaf("macd", ">", 0, ("field", "xxx")));
        foreach (var (tag, bad) in new[] { ("未知指标", bad1), ("非法参数", bad2), ("缺value", bad3), ("非法field", bad4) })
        {
            try
            {
                StrategyEngine.ValidateCondition(bad);
                Console.WriteLine($"FAIL 用例6: {tag} 应校验失败");
                return false;
            }
            catch (StrategyConfigError) { /* 预期 */ }
        }
        Console.WriteLine($"PASS 用例6: describe = {text}");
        return true;
    }
}
