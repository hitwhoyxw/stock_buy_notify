using System.Globalization;
using System.Text;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T1 · 每日盘后风控扫描（C# 版，移植自 scripts/t1_daily_risk.py，桌面/移动端通用）。
///
/// 检查项（阈值取自 02_strategy_config.yaml 默认值；C# 端不解析 yaml，调整需改常量）：
/// 1. C 桶持仓 MA60 位置 / 180 日高点回撤（C-E1 / C-E2，P0）
/// 2. 止损：B ≤ -25% / C ≤ -15%（P0）；C 桶浮盈 ≥ 40%（C-E4，P1）/ ≥ 80%（C-E5，P0）
/// 3. 集中度：单票 A>8% / B>6% / C>4%；单申万一级行业 >20%（P2）
/// 4. 组合级回撤熔断：-15% L1（P1）/ -20% L2（P0）
///
/// 输出：data/report_YYYY-MM-DD_T1.md；P0/P1 追加信号台账；更新 portfolio_nav.csv。
/// </summary>
public class DailyRiskTask : IBuiltinTask
{
    private const double StopLossB = -25.0;
    private const double StopLossC = -15.0;
    private const double CDdrawdownThr = -15.0;
    private const double CGainHalf = 40.0;
    private const double CGainHalfAgain = 80.0;
    private const double CbL1 = -15.0;
    private const double CbL2 = -20.0;
    private const double IndustryMax = 20.0;
    private static readonly IReadOnlyDictionary<string, double> SingleStockCap =
        new Dictionary<string, double> { ["A"] = 8.0, ["B"] = 6.0, ["C"] = 4.0, ["D"] = 100.0 };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] NavColumns =
        { "date", "A_mv", "B_mv", "C_mv", "D_mv", "total_mv", "nav", "peak_nav", "drawdown_pct" };

    public string Key => "T1";
    public string Name => "每日风控";

    private readonly DataStore _store;
    private readonly QuoteService _quotes;
    private readonly KlineService _klines;
    private readonly TradingCalendar _calendar;
    private readonly SignalLogStore _signals;
    private readonly CsIndexClient _csi;
    private readonly EastMoneyClient _em;

    public DailyRiskTask(DataStore store, QuoteService quotes, KlineService klines,
        TradingCalendar calendar, SignalLogStore signals, CsIndexClient csi, EastMoneyClient em)
    {
        _store = store;
        _quotes = quotes;
        _klines = klines;
        _calendar = calendar;
        _signals = signals;
        _csi = csi;
        _em = em;
    }

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string msg) => log?.Invoke($"[T1] {msg}");
        try
        {
            var today = TradingCalendar.NowCn();
            if (!await _calendar.IsTradingDayAsync(today))
            {
                L($"{today:yyyy-MM-dd} 非交易日，跳过");
                return new TaskRunResult(true, "", 0, "非交易日，跳过");
            }

            var positions = _store.LoadPositions();
            var alerts = new List<RiskAlert>();

            L($"持仓 {positions.Count} 只，开始净值更新…");
            NavInfo? nav = null;
            try { nav = await UpdateNavAsync(positions, today, L); }
            catch (Exception ex) { L($"净值更新失败: {ex.Message}"); }

            L("C 桶回撤 & MA60 检查…");
            alerts.AddRange(await CheckCBucketAsync(positions, today));
            L("止损 & 浮盈检查…");
            alerts.AddRange(await CheckStopLossAsync(positions, today, nav?.Prices));
            L("集中度检查…");
            alerts.AddRange(CheckConcentration(positions));
            alerts.AddRange(CheckCircuitBreaker(nav, today));

            foreach (var a in alerts.Where(a => a.Level is "P0" or "P1"))
                AppendAlertToSignalLog(a, today);
            if (alerts.Count > 0)
                L($"触发 {alerts.Count} 项（P0/P1 已写入信号台账）");

            L("拉取市场概况…");
            var marketOverview = await MarketOverviewAsync();

            var path = BuildReport(positions, nav, alerts, marketOverview, today);
            L($"报告已写入 {path}");

            var summary = ReportBuilder.SummaryLine(alerts);
            return new TaskRunResult(true, path, alerts.Count, summary);
        }
        catch (Exception ex)
        {
            L($"运行失败: {ex}");
            return new TaskRunResult(false, "", 0, $"T1 异常: {ex.Message}");
        }
    }

    // ── 净值序列（portfolio_nav.csv） ─────────────────────────────

    private sealed record NavInfo(
        DateTime Date, double Nav, double PeakNav, double DrawdownPct, double TotalMv,
        Dictionary<string, double> BucketMv, Dictionary<string, double> Prices);

    /// <summary>计算当日净值并写 portfolio_nav.csv（当天已有记录则替换）。现价缺失回退平均成本。</summary>
    private async Task<NavInfo> UpdateNavAsync(List<Position> positions, DateTime today, Action<string> log)
    {
        var bucketMv = new Dictionary<string, double> { ["A"] = 0, ["B"] = 0, ["C"] = 0, ["D"] = 0 };
        var prices = new Dictionary<string, double>(StringComparer.Ordinal);

        var codes = positions.Select(p => p.Code).Where(c => c.Trim().Length > 0).Distinct().ToList();
        var quotes = codes.Count > 0 ? await _quotes.FetchAsync(codes) : new Dictionary<string, QuoteInfo>();

        foreach (var p in positions)
        {
            var bucket = p.Bucket.Trim().ToUpper();
            if (!bucketMv.ContainsKey(bucket) || p.Shares <= 0) continue;
            var price = quotes.TryGetValue(DataStore.NormalizeCode(p.Code), out var q) ? (double)q.Price : 0;
            if (price <= 0) price = p.AvgCost; // 回退平均成本
            if (price <= 0) continue;
            prices[p.Code] = price;
            bucketMv[bucket] += p.Shares * price;
        }
        var totalMv = bucketMv.Values.Sum();

        var hist = _store.LoadNav();
        var baseMv = hist.Count > 0 && double.TryParse(hist[0].GetValueOrDefault("total_mv"), NumberStyles.Any, Inv, out var b) && b > 0 ? b : totalMv;
        var nav = baseMv > 0 ? totalMv / baseMv : 1.0;
        var peak = nav;
        foreach (var r in hist)
            if (double.TryParse(r.GetValueOrDefault("peak_nav"), NumberStyles.Any, Inv, out var pv) && pv > peak) peak = pv;
        var dd = peak > 0 ? (nav - peak) / peak * 100 : 0;

        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["date"] = today.ToString("yyyy-MM-dd"),
            ["A_mv"] = bucketMv["A"].ToString("F2", Inv),
            ["B_mv"] = bucketMv["B"].ToString("F2", Inv),
            ["C_mv"] = bucketMv["C"].ToString("F2", Inv),
            ["D_mv"] = bucketMv["D"].ToString("F2", Inv),
            ["total_mv"] = totalMv.ToString("F2", Inv),
            ["nav"] = nav.ToString("F4", Inv),
            ["peak_nav"] = peak.ToString("F4", Inv),
            ["drawdown_pct"] = dd.ToString("F2", Inv),
        };
        // 当天已有记录则替换最后一条同日期行，否则追加
        var idx = hist.FindLastIndex(r => r.GetValueOrDefault("date") == row["date"]);
        if (idx >= 0) hist[idx] = row;
        else hist.Add(row);
        _store.WriteCsv("portfolio_nav.csv", NavColumns.ToList(), hist);

        log($"净值更新: nav={nav:F4} 回撤={dd:F1}% 总市值={totalMv:F0}");
        return new NavInfo(today, nav, peak, dd, totalMv, bucketMv, prices);
    }

    // ── 检查项 ────────────────────────────────────────────────────

    /// <summary>C 桶：现价 vs MA60（C-E1）与距 180 日高点回撤（C-E2）。</summary>
    private async Task<List<RiskAlert>> CheckCBucketAsync(List<Position> positions, DateTime today)
    {
        var alerts = new List<RiskAlert>();
        foreach (var p in positions.Where(x => x.Bucket.Trim().ToUpper() == "C"))
        {
            var bars = await _klines.GetStockDailyAsync(p.Code, 180);
            if (bars is not { Count: >= 30 }) continue;
            var closes = bars.Select(b => b.Close).Where(c => c > 0).ToList();
            if (closes.Count < 30) continue;

            var latest = closes[^1];
            var ma60 = closes.TakeLast(60).Average();
            var peak = closes.Max();
            var drawdown = peak > 0 ? (latest - peak) / peak * 100 : 0;
            var src = $"腾讯/新浪日K · {today:yyyy-MM-dd}";
            var target = $"{p.Code} {p.Name}".Trim();

            if (latest < ma60)
                alerts.Add(new RiskAlert("P0", "C-E1", "C", target,
                    $"现价 {latest:F2} < MA60 {ma60:F2}", "price_index_cross_below_ma60", "同日减仓 50%", src));
            if (drawdown < CDdrawdownThr)
                alerts.Add(new RiskAlert("P0", "C-E2", "C", target,
                    $"距高点 {drawdown:F1}%", "drawdown_from_high_pct > 15", "清仓", src));
        }
        return alerts;
    }

    /// <summary>止损（B≤-25%/C≤-15%）与 C 桶浮盈（≥40%/≥80%）。现价优先实时行情，缺失回退日K收盘。</summary>
    private async Task<List<RiskAlert>> CheckStopLossAsync(
        List<Position> positions, DateTime today, Dictionary<string, double>? livePrices)
    {
        var alerts = new List<RiskAlert>();
        var thr = new Dictionary<string, double> { ["B"] = StopLossB, ["C"] = StopLossC };
        foreach (var p in positions)
        {
            var bucket = p.Bucket.Trim().ToUpper();
            if (!thr.TryGetValue(bucket, out var limit) || p.AvgCost <= 0) continue;

            var price = livePrices?.GetValueOrDefault(p.Code, 0) ?? 0;
            if (price <= 0)
            {
                var bars = await _klines.GetStockDailyAsync(p.Code, 30);
                if (bars is { Count: > 0 }) price = bars[^1].Close;
            }
            if (price <= 0) continue;

            var ret = (price - p.AvgCost) / p.AvgCost * 100;
            var src = $"腾讯/新浪行情 · {today:yyyy-MM-dd}";
            var target = $"{p.Code} {p.Name}".Trim();

            if (ret <= limit)
                alerts.Add(new RiskAlert("P0", $"{bucket}-STOP", bucket, target,
                    $"浮亏 {ret:F1}%", $"< {limit}%", "立即止损清仓", src));
            else if (bucket == "C" && ret >= CGainHalf)
            {
                var big = ret >= CGainHalfAgain;
                alerts.Add(new RiskAlert(big ? "P0" : "P1", big ? "C-E5" : "C-E4", "C", target,
                    $"浮盈 {ret:F1}%", $">= {(big ? "80" : "40")}%",
                    big ? "再减半仓" : "减半仓并挂 10% 尾随止盈", src));
            }
        }
        return alerts;
    }

    /// <summary>集中度：单票成本占比超桶上限 / 单申万一级行业 >20%。</summary>
    private static List<RiskAlert> CheckConcentration(List<Position> positions)
    {
        var alerts = new List<RiskAlert>();
        var total = positions.Sum(p => p.CostPool);
        if (total <= 0) return alerts;

        foreach (var p in positions)
        {
            var share = p.CostPool / total * 100;
            var bucket = p.Bucket.Trim().ToUpper();
            var cap = SingleStockCap.GetValueOrDefault(bucket, 8.0);
            if (share > cap)
                alerts.Add(new RiskAlert("P2", "CONC-STOCK", bucket, $"{p.Code} {p.Name}".Trim(),
                    $"单票占比 {share:F1}%", $"<= {cap}%", $"减仓至 {cap}% 以下", "04_交易日志"));
        }

        foreach (var g in positions.Where(p => !string.IsNullOrWhiteSpace(p.Industry)).GroupBy(p => p.Industry))
        {
            var share = g.Sum(p => p.CostPool) / total * 100;
            if (share <= IndustryMax) continue;
            alerts.Add(new RiskAlert("P2", "CONC-INDUSTRY", "*", g.Key,
                $"行业占比 {share:F1}%", $"<= {IndustryMax}%", $"减配至 {IndustryMax}% 以下", "04_交易日志 + 申万一级"));
        }
        return alerts;
    }

    /// <summary>组合级回撤熔断：-15% L1（B/C 各减半）/ -20% L2（清仓 B/C）。</summary>
    private static List<RiskAlert> CheckCircuitBreaker(NavInfo? nav, DateTime today)
    {
        var alerts = new List<RiskAlert>();
        if (nav is null) return alerts;
        var src = $"portfolio_nav · {nav.Date:yyyy-MM-dd}";
        if (nav.DrawdownPct <= CbL2)
            alerts.Add(new RiskAlert("P0", "PORTFOLIO-CB-L2", "*", "全组合",
                $"净值回撤 {nav.DrawdownPct:F1}%", $"<= {CbL2}%",
                "组合级二级熔断：清仓 B/C 桶，A 桶减至 10%，D 桶提升至 70%", src));
        else if (nav.DrawdownPct <= CbL1)
            alerts.Add(new RiskAlert("P1", "PORTFOLIO-CB-L1", "*", "全组合",
                $"净值回撤 {nav.DrawdownPct:F1}%", $"<= {CbL1}%",
                "组合级一级熔断：B/C 桶各减仓 50%，暂停新建仓", src));
        return alerts;
    }

    // ── 市场概况（公开 HTTP 源：指数日K + 中证 indicator + 东财国债收益率，同 T2 口径） ──

    private async Task<string> MarketOverviewAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| 指标 | 当前值 | 备注 |");
        sb.AppendLine("|------|--------|------|");

        var hs300 = await _klines.GetIndexDailyAsync("000300", 20);
        if (hs300 is { Count: >= 2 })
        {
            var closes = hs300.Select(b => b.Close).ToList();
            var peak = closes.Max();
            var last = closes[^1];
            var worstDay = 0.0;
            for (var i = 1; i < closes.Count; i++)
                if (closes[i - 1] > 0)
                    worstDay = Math.Min(worstDay, (closes[i] - closes[i - 1]) / closes[i - 1] * 100);
            var dd = peak > 0 ? (last - peak) / peak * 100 : 0;
            sb.AppendLine($"| 沪深300 20日最大回撤 | {dd:F2}% | 单日最大跌幅 {worstDay:F2}% |");
        }
        else
            sb.AppendLine("| 沪深300 20日最大回撤 | N/A | 数据获取失败 |");

        var div = await _klines.GetIndexDailyAsync("000922", 60);
        var allA = await _klines.GetIndexDailyAsync("000985", 60);
        if (div is { Count: >= 2 } && allA is { Count: >= 2 })
        {
            static double Ret(List<double> c) => c[0] > 0 ? (c[^1] - c[0]) / c[0] * 100 : 0;
            var excess = Ret(div.Select(b => b.Close).ToList()) - Ret(allA.Select(b => b.Close).ToList());
            sb.AppendLine($"| 红利60日相对超额(vs全A) | {excess:+0.00;-0.00}% | {(excess > 0 ? "红利占优" : "红利落后")} |");
        }
        else
            sb.AppendLine("| 红利60日相对超额 | N/A | 数据获取失败 |");

        // 中证红利股息率 + 5 年分位（中证官网 indicator，同 T2 数据源）
        double? divYield = null;
        try
        {
            var dy = await _csi.GetDividendYieldPercentileAsync("000922");
            if (dy is { } d)
            {
                divYield = d.Current;
                sb.AppendLine($"| 中证红利股息率 | {d.Current:F2}% | 5年分位 {d.Percentile:F0}%（中证官网样本） |");
            }
        }
        catch { /* 拉取失败走下方兜底行 */ }
        if (divYield is null)
            sb.AppendLine("| 中证红利股息率 / 5年分位 | N/A | 中证官网拉取失败 |");

        // 10Y 国债（东财 RPTA_WEB_TREASURYYIELD）与 ERP（同 T2 口径）
        double? y10 = null;
        try { y10 = await _em.GetCn10yYieldAsync(); } catch { }
        if (y10 is { } y)
        {
            var erp = divYield is { } dcur ? dcur - y : (double?)null;
            sb.AppendLine(erp is { } e
                ? $"| 10Y国债 / ERP(股息-10Y) | {y:F2}% / {e:+0.00;-0.00}% | {(e >= 3 ? "ERP≥3 红利性价比区间" : "ERP<3")} |"
                : $"| 10Y国债 | {y:F2}% | 东财国债收益率（股息率缺失，ERP 未算） |");
        }
        else
            sb.AppendLine("| 10Y国债 / ERP | N/A | 东财拉取失败 |");

        // 全 A 20 日均量分位（000001+399001 成交量之和，同 T2 口径）
        double? turnoverPct = null;
        try { turnoverPct = await GetMarketTurnoverPercentileAsync(20, 250); } catch { }
        sb.AppendLine(turnoverPct is { } tp
            ? $"| 全A 20日均量分位 | {tp:F0}% | 沪深成交量之和口径（缩量<30 / 放量>70） |"
            : "| 全A 20日均量分位 | N/A | 指数日K获取失败 |");

        return sb.ToString();
    }

    /// <summary>全 A 成交量分位：000001+399001 日成交量之和的 window 日均量在 lookback 窗口分位（%）。与 T2 同口径。</summary>
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

    // ── 台账 & 报告 ────────────────────────────────────────────────

    private void AppendAlertToSignalLog(RiskAlert a, DateTime today)
    {
        var parts = a.Target.Split(' ', 2);
        var direction = a.Action.Contains("止损") || a.Action.Contains("清仓") || a.Action.Contains("减仓") ? "卖出" : "观察";
        _signals.AppendSignal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["触发日期"] = today.ToString("yyyy-MM-dd"),
            ["yaml_version_at_trigger"] = "c#-builtin",
            ["触发任务"] = "T1",
            ["桶"] = a.Bucket,
            ["规则ID"] = a.RuleId,
            ["标的代码"] = parts.Length > 0 ? parts[0] : "",
            ["标的名称"] = parts.Length > 1 ? parts[1] : "",
            ["触发时指标值"] = a.Current,
            ["阈值"] = a.Threshold,
            ["信号方向"] = direction,
            ["建议动作"] = a.Action,
            ["是否实际执行"] = "否",
        });
    }

    private string BuildReport(List<Position> positions, NavInfo? nav,
        List<RiskAlert> alerts, string marketOverview, DateTime today)
    {
        var weights = _store.BucketWeights();
        var weightsMd = string.Join(" · ", weights.Where(w => w.Value > 0.0001).Select(w => $"{w.Key}={w.Value * 100:F1}%"));

        var snapshot = positions.Select(p => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["代码"] = p.Code,
            ["名称"] = p.Name,
            ["桶"] = p.Bucket,
            ["净股数"] = p.Shares.ToString("F0", Inv),
            ["平均成本"] = p.AvgCost.ToString("F2", Inv),
        }).ToList();

        var navMd = nav is null
            ? "_净值数据获取失败_\n"
            : $"| 净值 | {nav.Nav:F4} |\n| 峰值净值 | {nav.PeakNav:F4} |\n| 当前回撤 | {nav.DrawdownPct:F1}% |\n"
              + $"| 总市值 | {nav.TotalMv:F0} 元 |\n| A/B/C/D 市值 | {nav.BucketMv["A"]:F0} / {nav.BucketMv["B"]:F0} / {nav.BucketMv["C"]:F0} / {nav.BucketMv["D"]:F0} 元 |\n";

        var cbStatus = nav is null ? "净值获取失败" : $"已检查（回撤 {nav.DrawdownPct:F1}%）";
        var empty = positions.Count == 0;
        var sections = new List<(string, string)>
        {
            ("市场概况", marketOverview),
            ("四桶权重", $"`{weightsMd}`\n\n持仓标的数：**{positions.Count}**\n"),
            ("组合净值", navMd),
            ("持仓明细", empty ? "_当前空仓_\n"
                : ReportBuilder.RenderKvTable(snapshot, ["代码", "名称", "桶", "净股数", "平均成本"])),
            ("风控检查结论",
                $"- C 桶回撤/MA60：{(empty || positions.All(p => p.Bucket.Trim().ToUpper() != "C") ? "无持仓" : "已检查")}\n"
                + $"- 止损线（B<-25%, C<-15%）：{(empty ? "无持仓" : "已检查")}\n"
                + $"- 集中度（单票/行业）：{(empty ? "无持仓" : "已检查")}\n"
                + $"- 组合级回撤熔断：{cbStatus}\n"),
        };
        return ReportBuilder.WriteReport(_store.DataDir, "T1",
            $"T1 每日风控扫描 · {today:yyyy-MM-dd}", sections, alerts);
    }
}
