using System.Globalization;
using System.Text;
using ThreeBucket.Core.Data;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T2 · 每周红利状态判定（C# 版，移植自 scripts/t2_weekly_dividend.py，桌面/移动端通用）。
///
/// 指标采集（公开 HTTP 源替代 akshare）：
/// - 中证红利股息率 5 年分位：中证官网 indicator.xls（仅保留约 20 个交易日样本，分位按可得样本算）
/// - 10Y 国债：东财 RPTA_WEB_TREASURYYIELD；ERP = 股息率 - 10Y
/// - 相对超额 60d：000922 vs 000985（中证全指 = Wind 全 A 近似）
/// - 全 A 20 日均量分位：000001+399001 成交量之和（Python 版允许 volume 回退，不影响分位）
/// - 红利板块相对成交度分位 / 前3行业成交额占比：无公开数据源 → 缺失（Python 受限环境同样缺失）
/// - 沪深300 单日最大跌幅 / 20 日最大回撤：指数日K
///
/// 档位判定阈值硬编码自 yaml v1.0（StrategyConfig 先例）。分类规则保持"防单类别共振"意图：
/// 估值类 ≥1 且（宏观流动性+情绪）类 ≥1。注：Python 版原文要求"估值≥1 且 宏观流动性≥1"，
/// 但宏观流动性类指标在 yaml conditions 中未定义阈值 → 实际运行永远判 S0（死锁）；
/// C# 版修正为估值 + 其他类别跨类别双确认，宏观数据缺失时退化为估值+情绪确认。
/// </summary>
public class WeeklyDividendTask : IBuiltinTask
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Key => "T2";
    public string Name => "每周红利判定";

    private readonly string _dataDir;
    private readonly DataStore _store;
    private readonly KlineService _klines;
    private readonly CsIndexClient _csi;
    private readonly EastMoneyClient _em;
    private readonly SignalLogStore _signals;

    public WeeklyDividendTask(string dataDir, DataStore store, KlineService klines,
        CsIndexClient csi, EastMoneyClient em, SignalLogStore signals)
    {
        _dataDir = dataDir;
        _store = store;
        _klines = klines;
        _csi = csi;
        _em = em;
        _signals = signals;
    }

    /// <summary>判定条件（yaml bucket_A.market_signals.conditions）。Dir: ge=值≥阈值达标，le=值≤阈值达标。</summary>
    private sealed record Cond(string Key, string Dir, double S1, double S2, double S3)
    {
        public double Thr(string tier) => tier == "S1" ? S1 : tier == "S2" ? S2 : S3;

        // yaml 中 ERP 阈值为浮点字面量（3.0/4.0/5.0），其余为整数——显示与 Python 报告一致
        public string Disp(string tier) => Disp(Thr(tier));

        private static string Disp(double v) => v is 3.0 or 4.0 or 5.0
            ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("0", CultureInfo.InvariantCulture);
    }

    private static readonly Cond[] Conditions =
    {
        new("dividend_yield_percentile_5y", "ge", 70, 80, 90),
        new("equity_risk_premium_pct", "ge", 3.0, 4.0, 5.0),
        new("relative_excess_60d_pct", "le", -5, -8, -12),
        new("top3_industry_turnover_share_pct", "ge", 35, 42, 48),
        new("hs300_single_day_pct", "le", -2, -3, -5),
        new("hs300_drawdown_20d_pct", "le", -8, -12, -18),
    };

    // 指标定义（报告展示顺序）：key / 标签 / 类别（用于"防单类别共振"分类规则）
    private static readonly (string Key, string Label, string Cat)[] Indicators =
    {
        ("dividend_yield_percentile_5y", "中证红利股息率5年分位%", "估值"),
        ("equity_risk_premium_pct", "ERP=股息-10Y国债", "估值"),
        ("relative_excess_60d_pct", "红利vs全指60日相对超额%", "估值"),
        ("market_turnover_20d_percentile", "全A 20日均量分位%", "宏观流动性"),
        ("dividend_sector_relative_turnover_percentile", "红利板块相对成交度分位%", "宏观流动性"),
        ("top3_industry_turnover_share_pct", "前3行业成交额占比%", "情绪"),
        ("hs300_single_day_pct", "沪深300单日最大跌幅%", "情绪"),
        ("hs300_drawdown_20d_pct", "沪深300 20日最大回撤%", "情绪"),
    };

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string msg) => log?.Invoke($"[T2] {msg}");
        try
        {
            var today = TradingCalendar.NowCn();
            L("采集市场指标…");
            var ind = await CollectIndicatorsAsync(L, ct);
            foreach (var (key, label, _) in Indicators)
                L($"{label}: {Fmt(ind.GetValueOrDefault(key))}");

            var (tier, met) = EvaluateTier(ind);
            var lastTier = ReadLastTier();
            L($"档位判定：{tier}（上次 {lastTier ?? "无记录"}）");

            // 跃迁告警
            var alerts = new List<RiskAlert>();
            if (lastTier is not null && lastTier != tier)
                alerts.Add(new RiskAlert("P1", $"A-STATE-{lastTier}->{tier}", "A", "组合状态跃迁",
                    tier, $"上次 {lastTier}",
                    $"按 allocation.states.{tier} 调仓；A 桶按 ladder 分档投入", "T2 指标扫描"));
            else if (lastTier is null && tier != "S0")
                alerts.Add(new RiskAlert("P1", $"A-STATE-INIT-{tier}", "A", "首次判定",
                    tier, "-", $"按 allocation.states.{tier} 建立初始仓位", "T2 指标扫描"));

            // 四桶权重对照
            var weights = _store.BucketWeights();
            var target = StrategyConfig.AllocationStates.GetValueOrDefault(tier)
                ?? StrategyConfig.AllocationStates["S0"];
            var deltaRows = new List<Dictionary<string, string>>();
            foreach (var k in new[] { "A", "B", "C", "D" })
            {
                var cur = weights.GetValueOrDefault(k, 0.0) * 100;
                var tgt = target.GetValueOrDefault(k, 0.0) * 100;
                deltaRows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["桶"] = k,
                    ["当前%"] = cur.ToString("0.0", Inv),
                    ["目标%"] = tgt.ToString("0.0", Inv),
                    ["偏离%"] = (tgt - cur).ToString("+0.0;-0.0", Inv),
                });
            }

            // 跃迁写台账
            foreach (var a in alerts)
                _signals.AppendSignal(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["触发日期"] = today.ToString("yyyy-MM-dd"),
                    ["yaml_version_at_trigger"] = StrategyConfig.YamlTag,
                    ["触发任务"] = "T2",
                    ["桶"] = "A",
                    ["规则ID"] = a.RuleId,
                    ["标的代码"] = "-",
                    ["标的名称"] = "组合",
                    ["分桶基准代码"] = StrategyConfig.BucketABenchmark,
                    ["触发时指标值"] = $"S={tier}",
                    ["阈值"] = "分类规则通过",
                    ["当时组合状态"] = $"{lastTier ?? "INIT"}->{tier}",
                    ["信号方向"] = tier != "S0" ? "买入" : "观察",
                    ["建议动作"] = a.Action,
                    ["是否实际执行"] = "否",
                });

            // 报告
            string Met(string t, string cat) => met[t][cat].Count.ToString(Inv);
            var sections = new List<(string, string)>
            {
                ("状态判定",
                    $"当前档位 **{tier}**（上周 **{lastTier ?? "无记录"}**）\n\n"
                    + $"S3 类别命中：估值 {Met("S3", "估值")}、流动性 {Met("S3", "宏观流动性")}、情绪 {Met("S3", "情绪")}；"
                    + $"S2 命中：估值 {Met("S2", "估值")}、流动性 {Met("S2", "宏观流动性")}、情绪 {Met("S2", "情绪")}；"
                    + $"S1 命中：估值 {Met("S1", "估值")}、流动性 {Met("S1", "宏观流动性")}、情绪 {Met("S1", "情绪")}\n"),
                ("指标明细表", RenderIndicatorTable(ind)),
                ("四桶权重对照", ReportBuilder.RenderKvTable(deltaRows, new[] { "桶", "当前%", "目标%", "偏离%" })),
                ("yaml 版本", $"`{StrategyConfig.YamlTag}`（C# 内置常量，对应 02_strategy_config.yaml v1.0）\n"),
            };
            var path = ReportBuilder.WriteReport(_dataDir, "T2",
                $"T2 周度红利判定 · {today:yyyy-MM-dd}", sections, alerts);
            var transition = lastTier is not null && lastTier != tier;
            L($"报告已写入 {path}，档位 {tier}，跃迁={transition}");
            return new TaskRunResult(true, path, alerts.Count,
                $"档位 {tier}（上次 {lastTier ?? "无记录"}），跃迁={transition}");
        }
        catch (Exception ex)
        {
            return new TaskRunResult(false, "", 0, $"T2 失败: {ex.Message}");
        }
    }

    // ── 指标采集 ────────────────────────────────────────────────

    private async Task<Dictionary<string, double?>> CollectIndicatorsAsync(Action<string> L, CancellationToken ct)
    {
        var ind = new Dictionary<string, double?>();

        try
        {
            var dy = await _csi.GetDividendYieldPercentileAsync("000922", ct: ct);
            ind["dividend_yield_current"] = dy?.Current;
            ind["dividend_yield_percentile_5y"] = dy?.Percentile;
        }
        catch (Exception e) { L($"股息率分位拉取失败: {e.Message}"); }

        double? y10 = null;
        try { y10 = await _em.GetCn10yYieldAsync(ct); }
        catch (Exception e) { L($"10Y 国债拉取失败: {e.Message}"); }
        ind["cn10y_yield_pct"] = y10;
        ind["equity_risk_premium_pct"] =
            ind.GetValueOrDefault("dividend_yield_current") is { } cur && y10 is { } y ? cur - y : null;

        ind["relative_excess_60d_pct"] =
            await TryAsync(() => GetRelativeExcessAsync("000922", "000985", 60), L, "相对超额");
        ind["market_turnover_20d_percentile"] =
            await TryAsync(() => GetMarketTurnoverPercentileAsync(20, 250), L, "市场成交分位");
        ind["dividend_sector_relative_turnover_percentile"] = null; // 无公开数据源（同 Python 受限环境）
        ind["top3_industry_turnover_share_pct"] = null;             // 申万行业成交额无公开源

        (double SingleDayPct, double DrawdownPct)? hs = null;
        try { hs = await GetHs300DrawdownAsync(20); }
        catch (Exception e) { L($"沪深300回撤计算失败: {e.Message}"); }
        ind["hs300_single_day_pct"] = hs?.SingleDayPct;
        ind["hs300_drawdown_20d_pct"] = hs?.DrawdownPct;

        return ind;
    }

    private static async Task<double?> TryAsync(Func<Task<double?>> f, Action<string> L, string what)
    {
        try { return await f(); }
        catch (Exception e) { L($"{what}拉取失败: {e.Message}"); return null; }
    }

    /// <summary>红利指数相对基准（000985 中证全指）近 days 交易日的区间超额（%）。</summary>
    private async Task<double?> GetRelativeExcessAsync(string bucketCode, string benchmarkCode, int days)
    {
        var a = await _klines.GetIndexDailyAsync(bucketCode, days);
        var b = await _klines.GetIndexDailyAsync(benchmarkCode, days);
        if (a is not { Count: >= 5 } || b is not { Count: >= 5 }) return null;
        var ar = a[^1].Close / a[0].Close - 1;
        var br = b[^1].Close / b[0].Close - 1;
        return (ar - br) * 100;
    }

    /// <summary>全 A 成交量分位：000001+399001 日成交量之和的 window 日均量在 lookback 窗口分位（%）。</summary>
    private async Task<double?> GetMarketTurnoverPercentileAsync(int windowDays, int lookbackDays)
    {
        var sh = await _klines.GetIndexDailyAsync("000001", lookbackDays + windowDays);
        var sz = await _klines.GetIndexDailyAsync("399001", lookbackDays + windowDays);
        if (sh is null || sz is null) return null;

        var szByDate = sz.GroupBy(b => b.Date).ToDictionary(g => g.Key, g => g.Sum(b => b.Volume));
        var daily = sh.Where(b => szByDate.ContainsKey(b.Date))
            .Select(b => (b.Date, Vol: b.Volume + szByDate[b.Date]))
            .OrderBy(x => x.Date).ToList();
        if (daily.Count < windowDays + 30) return null;

        var rolls = new List<double>();
        for (var i = windowDays - 1; i < daily.Count; i++)
        {
            double s = 0;
            for (var j = i - windowDays + 1; j <= i; j++) s += daily[j].Vol;
            rolls.Add(s / windowDays);
        }
        var current = rolls[^1];
        var hist = rolls.Skip(Math.Max(0, rolls.Count - lookbackDays)).ToList();
        return 100.0 * hist.Count(h => h <= current) / hist.Count;
    }

    /// <summary>沪深300 近 days 日单日最大跌幅（%）与期间最大回撤（%）。</summary>
    private async Task<(double SingleDayPct, double DrawdownPct)?> GetHs300DrawdownAsync(int days)
    {
        var bars = await _klines.GetIndexDailyAsync("000300", days);
        if (bars is not { Count: >= 5 }) return null;

        var closes = bars.Select(b => b.Close).ToList();
        var minRet = double.MaxValue;
        for (var i = 1; i < closes.Count; i++)
            minRet = Math.Min(minRet, (closes[i] / closes[i - 1] - 1) * 100);
        var peak = double.MinValue;
        var minDd = 0.0;
        foreach (var c in closes)
        {
            peak = Math.Max(peak, c);
            minDd = Math.Min(minDd, (c - peak) / peak * 100);
        }
        return (minRet, minDd);
    }

    // ── 档位判定 ────────────────────────────────────────────────

    private static (string Tier, Dictionary<string, Dictionary<string, List<string>>> Met) EvaluateTier(
        Dictionary<string, double?> ind)
    {
        var met = new Dictionary<string, Dictionary<string, List<string>>>();
        var reached = "S0";
        foreach (var tier in new[] { "S1", "S2", "S3" })
        {
            var byCat = new Dictionary<string, List<string>>
            {
                ["估值"] = new(),
                ["宏观流动性"] = new(),
                ["情绪"] = new(),
            };
            foreach (var c in Conditions)
            {
                if (!ind.TryGetValue(c.Key, out var v) || v is null) continue;
                var thr = c.Thr(tier);
                var ok = c.Dir == "ge" ? v >= thr : v <= thr;
                if (ok)
                    byCat[Indicators.First(i => i.Key == c.Key).Cat].Add(c.Key);
            }
            met[tier] = byCat;
            // 跨类别双确认（防单类别共振）：估值 ≥1 且（宏观流动性+情绪）≥1
            if (byCat["估值"].Count >= 1 && byCat["宏观流动性"].Count + byCat["情绪"].Count >= 1)
                reached = tier;
        }
        return (reached, met);
    }

    // ── 台账与渲染 ──────────────────────────────────────────────

    /// <summary>从台账读上次 T2 判定档位（"当时组合状态"形如 S1-&gt;S2，取末段）。</summary>
    private string? ReadLastTier()
    {
        var t2 = _signals.ReadAll()
            .Where(r => r.GetValueOrDefault("触发任务") == "T2")
            .OrderBy(r => r.GetValueOrDefault("触发日期")).ToList();
        if (t2.Count == 0) return null;
        var state = t2[^1].GetValueOrDefault("当时组合状态", "");
        if (state.Length == 0) return null;
        var last = state.Split("->")[^1].Trim();
        return last.Length > 0 ? last : null;
    }

    private static string RenderIndicatorTable(Dictionary<string, double?> ind)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| 指标 | 当前值 | S1阈值 | S2阈值 | S3阈值 | 分类 |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var (key, label, cat) in Indicators)
        {
            var c = Array.Find(Conditions, x => x.Key == key);
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {Fmt(ind.GetValueOrDefault(key))} | " +
                $"{c?.Disp("S1") ?? "-"} | {c?.Disp("S2") ?? "-"} | {c?.Disp("S3") ?? "-"} | {cat} |");
        }
        return sb.ToString();
    }

    private static string Fmt(double? v) => v is null ? "缺失" : v.Value.ToString("0.00", Inv);
}
