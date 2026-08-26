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
        Console.WriteLine("=== 策略引擎离线回归（条件树 / 金叉死叉 / 背离 / 超买超卖 / 旧格式兼容） ===\n");
        var ok = true;
        ok &= Case1MaAlignmentWithVolume();
        ok &= Case2MacdCrossUp();
        ok &= Case3DoubleMaGoldenCross();
        ok &= Case4CsvStrategies();
        ok &= Case5LegacyS1();
        ok &= Case6DescribeAndValidate();
        ok &= Case7Divergence();
        ok &= Case8KdjRsiLegacyTrend();
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

    // ── 用例7：MACD 顶背离 / 底背离 / 零上死叉（S10–S12）──────

    /// <summary>从序列末尾向前生成 days 天每日 factor 复利的价格段。</summary>
    private static List<double> Grow(IReadOnlyList<double> seed, double factor, int days)
    {
        var list = new List<double>(days);
        var px = seed[^1];
        for (var i = 0; i < days; i++) { px *= factor; list.Add(px); }
        return list;
    }

    private static List<double> ConcatAll(params IReadOnlyList<double>[] parts)
    {
        var all = new List<double>();
        foreach (var p in parts) all.AddRange(p);
        return all;
    }

    private static Core.Data.DataStore RepoStore()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "scripts"))) break;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return new Core.Data.DataStore(Path.Combine(dir, "data"));
    }

    private static bool Case7Divergence()
    {
        var store = RepoStore();
        var strategies = store.ListStrategies();
        var s10 = strategies.FirstOrDefault(s => s.Id == "S10");
        var s11 = strategies.FirstOrDefault(s => s.Id == "S11");
        var s12 = strategies.FirstOrDefault(s => s.Id == "S12");
        if (s10 is null || s11 is null || s12 is null)
        { Console.WriteLine("FAIL 用例7: strategies.csv 缺少 S10/S11/S12"); return false; }
        foreach (var s in new[] { s10, s11, s12 })
        {
            var err = StrategyEngine.ValidateStrategy(s);
            if (err != null)
            { Console.WriteLine($"FAIL 用例7: {s.Id} 校验失败：{err}"); return false; }
        }

        // 顶背离构造：陡涨(24d +2.6%) → 回调(18d -1.3%) → 缓涨创新高(40d +1.0%)
        var top = ConcatAll(
            new List<double> { 10.0 },
            Grow(new List<double> { 10.0 }, 1.026, 24),
            Grow(new List<double> { 10.0 * Math.Pow(1.026, 24) }, 0.987, 18),
            Grow(new List<double> { 10.0 * Math.Pow(1.026, 24) * Math.Pow(0.987, 18) }, 1.010, 40));
        var (tDif, _) = RefMacd(top.ToArray());
        var tn = top.Count;
        var hhv = tDif.Skip(tn - 60).Max();
        var topGap = (tDif[^1] - hhv) / Math.Abs(hhv) * 100;
        var tPeak = top.Skip(tn - 60).Max() * 1.01; // MakeKline: high = close*1.01
        var tDd = (top[^1] / tPeak - 1) * 100;
        var expectTop = tDd >= -3 && topGap <= -25 && tDif[^1] > 0;
        var tctx = new IndicatorContext(top[^1], 1.0, MakeKline(top));
        var actualTop = StrategyEngine.EvaluateStrategy(s11, tctx) == true;
        if (actualTop != expectTop)
        { Console.WriteLine($"FAIL 用例7: S11 与理论不一致（理论={P(expectTop)} 实际={P(actualTop)} gap={topGap:F1}% dd={tDd:F1}%）"); return false; }
        if (!expectTop)
        { Console.WriteLine($"FAIL 用例7: 顶背离数据未构造成功（gap={topGap:F1}% dd={tDd:F1}%）"); return false; }

        // 底背离构造：陡跌(24d -2.6%) → 反弹(18d +1.3%) → 缓跌创新低(40d -1.0%)
        var bot = ConcatAll(
            new List<double> { 10.0 },
            Grow(new List<double> { 10.0 }, 0.974, 24),
            Grow(new List<double> { 10.0 * Math.Pow(0.974, 24) }, 1.013, 18),
            Grow(new List<double> { 10.0 * Math.Pow(0.974, 24) * Math.Pow(1.013, 18) }, 0.990, 40));
        var (bDif, _) = RefMacd(bot.ToArray());
        var bn = bot.Count;
        var llv = bDif.Skip(bn - 60).Min();
        var botGap = (bDif[^1] - llv) / Math.Abs(llv) * 100;
        var bTrough = bot.Skip(bn - 60).Min() * 0.99;
        var bGain = (bot[^1] / bTrough - 1) * 100;
        var expectBot = bGain <= 3 && botGap >= 25 && bDif[^1] < 0;
        var bctx = new IndicatorContext(bot[^1], -1.0, MakeKline(bot));
        var actualBot = StrategyEngine.EvaluateStrategy(s12, bctx) == true;
        if (actualBot != expectBot)
        { Console.WriteLine($"FAIL 用例7: S12 与理论不一致（理论={P(expectBot)} 实际={P(actualBot)} gap={botGap:F1}% gain={bGain:F1}%）"); return false; }
        if (!expectBot)
        { Console.WriteLine($"FAIL 用例7: 底背离数据未构造成功（gap={botGap:F1}% gain={bGain:F1}%）"); return false; }

        // 反例：匀速上涨价格创新高但 DIF 同步创新高 → 无背离，不触发
        var steady = Enumerable.Range(0, 80).Select(i => 10.0 * Math.Pow(1.01, i)).ToList();
        var ectx = new IndicatorContext(steady[^1], 1.0, MakeKline(steady));
        if (StrategyEngine.EvaluateStrategy(s11, ectx) is not false)
        { Console.WriteLine("FAIL 用例7: 匀速上涨（无背离）不应触发 S11"); return false; }

        // S10 零上死叉：涨40天(+1.0%)后回调 n 天(-1.5%)，扫描「恰好当日死叉且 DIF>0」
        var up = new List<double> { 10.0 };
        up.AddRange(Grow(up, 1.010, 40));
        for (var dn = 2; dn <= 15; dn++)
        {
            var seq = new List<double>(up);
            seq.AddRange(Grow(seq, 0.985, dn));
            var (sDif, sDea) = RefMacd(seq.ToArray());
            var last = seq.Count - 1;
            if (!(sDif[last - 1] > sDea[last - 1] && sDif[last] <= sDea[last] && sDif[last] > 0))
                continue; // 不是「恰好当日零上死叉」
            var sctx = new IndicatorContext(seq[^1], -1.5, MakeKline(seq));
            if (StrategyEngine.EvaluateStrategy(s10, sctx) is not true)
            { Console.WriteLine($"FAIL 用例7: S10 零上死叉当日应触发（回调天数={dn}）"); return false; }
            var pre = seq.Take(seq.Count - 1).ToList();
            var pctx = new IndicatorContext(pre[^1], -1.5, MakeKline(pre));
            if (StrategyEngine.EvaluateStrategy(s10, pctx) is not false)
            { Console.WriteLine("FAIL 用例7: S10 死叉前一日不应触发"); return false; }
            Console.WriteLine($"PASS 用例7: 顶背离(gap={topGap:F0}%) 底背离(gap={botGap:F0}%) 零上死叉(回调{dn}日) 均触发且与理论一致");
            return true;
        }
        Console.WriteLine("FAIL 用例7: 扫描 2..15 天均未构造出零上死叉（数据设计有误）");
        return false;
    }

    // ── 用例8：KDJ 超买超卖 / RSI / 双均线死叉 / 空头排列 / 乖离（S9,S13–S16）──

    /// <summary>独立参考 KDJ.J（国内标准 9,3,3），与引擎实现互为对照。</summary>
    private static double[] RefKdjJ(double[] c, double[] hi, double[] lo, int n = 9)
    {
        var j = new double[c.Length];
        double k = 50, d = 50;
        for (var i = 0; i < c.Length; i++)
        {
            if (i < n - 1) { j[i] = double.NaN; continue; }
            double hh = double.MinValue, ll = double.MaxValue;
            for (var t = i - n + 1; t <= i; t++)
            {
                if (hi[t] > hh) hh = hi[t];
                if (lo[t] < ll) ll = lo[t];
            }
            var rsv = hh > ll ? (c[i] - ll) / (hh - ll) * 100 : 50;
            k = k * 2.0 / 3 + rsv / 3;
            d = d * 2.0 / 3 + k / 3;
            j[i] = 3 * k - 2 * d;
        }
        return j;
    }

    /// <summary>独立参考 RSI（Wilder 递推），与引擎实现互为对照。</summary>
    private static double[] RefRsi(double[] c, int period = 14)
    {
        var rsi = new double[c.Length];
        if (c.Length < period + 1) return rsi;
        for (var i = 0; i < period; i++) rsi[i] = double.NaN;
        double ag = 0, al = 0;
        for (var i = 1; i <= period; i++)
        {
            ag += Math.Max(c[i] - c[i - 1], 0);
            al += Math.Max(c[i - 1] - c[i], 0);
        }
        ag /= period;
        al /= period;
        rsi[period] = al <= 1e-12 ? 100 : 100 - 100 / (1 + ag / al);
        for (var i = period + 1; i < c.Length; i++)
        {
            ag = (ag * (period - 1) + Math.Max(c[i] - c[i - 1], 0)) / period;
            al = (al * (period - 1) + Math.Max(c[i - 1] - c[i], 0)) / period;
            rsi[i] = al <= 1e-12 ? 100 : 100 - 100 / (1 + ag / al);
        }
        return rsi;
    }

    private static bool Same(double? a, double? b)
        => a is null && b is null
           || a is not null && b is not null && Math.Abs(a.Value - b.Value) < 1e-9;

    private static bool Case8KdjRsiLegacyTrend()
    {
        var store = RepoStore();
        var strategies = store.ListStrategies();
        var s9 = strategies.FirstOrDefault(s => s.Id == "S9");
        var s13 = strategies.FirstOrDefault(s => s.Id == "S13");
        var s14 = strategies.FirstOrDefault(s => s.Id == "S14");
        var s15 = strategies.FirstOrDefault(s => s.Id == "S15");
        var s16 = strategies.FirstOrDefault(s => s.Id == "S16");
        if (s9 is null || s13 is null || s14 is null || s15 is null || s16 is null)
        { Console.WriteLine("FAIL 用例8: strategies.csv 缺少 S9/S13/S14/S15/S16"); return false; }
        foreach (var s in new[] { s9, s13, s14, s15, s16 })
        {
            var err = StrategyEngine.ValidateStrategy(s);
            if (err != null)
            { Console.WriteLine($"FAIL 用例8: {s.Id} 校验失败：{err}"); return false; }
        }

        // 1) KDJ/RSI 引擎 vs 独立参考实现：横盘→连涨→连跌→交替混合序列逐点对照
        var mix = new List<double> { 10.0 };
        mix.AddRange(Enumerable.Repeat(10.0, 20));
        mix.AddRange(Grow(mix, 1.02, 8));
        mix.AddRange(Grow(mix, 0.985, 8));
        for (var i = 0; i < 24; i++)
            mix.Add(mix[^1] * (i % 2 == 0 ? 1.025 : 0.988));
        var bars = MakeKline(mix);
        var cArr = mix.ToArray();
        var refJ = RefKdjJ(cArr, bars.Select(b => b.High).ToArray(), bars.Select(b => b.Low).ToArray());
        var refR = RefRsi(cArr);
        var mctx = new IndicatorContext(cArr[^1], 0, bars);
        for (var off = 0; off < cArr.Length; off++)
        {
            var i = cArr.Length - 1 - off;
            if (!Same(double.IsNaN(refJ[i]) ? null : refJ[i], mctx.GetKdjJ(9, off)))
            { Console.WriteLine($"FAIL 用例8: KDJ 与参考实现不一致（i={i} ref={refJ[i]} engine={mctx.GetKdjJ(9, off)}）"); return false; }
            if (!Same(double.IsNaN(refR[i]) ? null : refR[i], mctx.GetRsi(14, off)))
            { Console.WriteLine($"FAIL 用例8: RSI 与参考实现不一致（i={i} ref={refR[i]} engine={mctx.GetRsi(14, off)}）"); return false; }
        }

        // 2) S15 KDJ超买：横盘 20 天 + 连涨 8 天(+4%) → J>=100 触发；纯横盘 → 不触发
        var surge = new List<double> { 10.0 };
        surge.AddRange(Enumerable.Repeat(10.0, 20));
        surge.AddRange(Grow(surge, 1.04, 8));
        var gctx = new IndicatorContext(surge[^1], 4.0, MakeKline(surge));
        if (StrategyEngine.EvaluateStrategy(s15, gctx) is not true)
        { Console.WriteLine($"FAIL 用例8: S15 连涨超买应触发（J={gctx.GetKdjJ(9)}）"); return false; }
        var flat = Enumerable.Repeat(10.0, 30).ToList();
        var fctx = new IndicatorContext(10.0, 0, MakeKline(flat));
        if (StrategyEngine.EvaluateStrategy(s15, fctx) is not false)
        { Console.WriteLine("FAIL 用例8: S15 横盘不应触发"); return false; }

        // 3) S16 KDJ超卖：横盘 20 天 + 连跌 8 天(-4%) → J<=0 触发
        var slump = new List<double> { 10.0 };
        slump.AddRange(Enumerable.Repeat(10.0, 20));
        slump.AddRange(Grow(slump, 0.96, 8));
        var dctx = new IndicatorContext(slump[^1], -4.0, MakeKline(slump));
        if (StrategyEngine.EvaluateStrategy(s16, dctx) is not true)
        { Console.WriteLine($"FAIL 用例8: S16 连跌超卖应触发（J={dctx.GetKdjJ(9)}）"); return false; }

        // 4) S9 双均线死叉：涨25天(+1.8%)后跌 n 天(-3.5%)，扫描「恰好当日死叉」
        var up = new List<double> { 10.0 };
        up.AddRange(Grow(up, 1.018, 25));
        for (var dn = 2; dn <= 15; dn++)
        {
            var seq = new List<double>(up);
            seq.AddRange(Grow(seq, 0.965, dn));
            var ctx = new IndicatorContext(seq[^1], -3.5, MakeKline(seq));
            if (ctx.GetMa(5, 1) <= ctx.GetMa(10, 1) || ctx.GetMa(5, 0) >= ctx.GetMa(10, 0))
                continue; // 不是「恰好当日死叉」
            if (StrategyEngine.EvaluateStrategy(s9, ctx) is not true)
            { Console.WriteLine($"FAIL 用例8: S9 死叉当日应触发（n={dn}）"); return false; }
            var pre = seq.Take(seq.Count - 1).ToList();
            var pctx = new IndicatorContext(pre[^1], -3.5, MakeKline(pre));
            if (StrategyEngine.EvaluateStrategy(s9, pctx) is not false)
            { Console.WriteLine("FAIL 用例8: S9 死叉前一日不应触发"); return false; }
            break; // 找到即完成本段验证
        }

        // 5) S13 均线空头排列：80 天匀速下跌触发；上涨序列不触发
        var down = Enumerable.Range(0, 80).Select(i => 10.0 * Math.Pow(0.997, i)).ToList();
        var dwctx = new IndicatorContext(down[^1], -0.3, MakeKline(down));
        if (StrategyEngine.EvaluateStrategy(s13, dwctx) is not true)
        { Console.WriteLine("FAIL 用例8: S13 空头排列应触发"); return false; }
        if (StrategyEngine.EvaluateStrategy(s13, gctx) is not false)
        { Console.WriteLine("FAIL 用例8: S13 上涨序列不应触发"); return false; }

        // 6) S14 五日乖离：横盘 30 天 + 末日+10% → bias≈7.8% 触发；末日+2% → 不触发
        var spike = Enumerable.Repeat(10.0, 30).Append(11.0).ToList();
        var spctx = new IndicatorContext(11.0, 10.0, MakeKline(spike));
        if (StrategyEngine.EvaluateStrategy(s14, spctx) is not true)
        { Console.WriteLine($"FAIL 用例8: S14 乖离5%应触发（bias={spctx.GetBias(5)}）"); return false; }
        var mild = Enumerable.Repeat(10.0, 30).Append(10.2).ToList();
        var mdctx = new IndicatorContext(10.2, 2.0, MakeKline(mild));
        if (StrategyEngine.EvaluateStrategy(s14, mdctx) is not false)
        { Console.WriteLine("FAIL 用例8: S14 温和上涨不应触发"); return false; }

        Console.WriteLine("PASS 用例8: KDJ/RSI 对照一致；S9/S13/S14/S15/S16 触发与理论一致");
        return true;
    }
}
