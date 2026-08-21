using System.Globalization;
using System.Text;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T7 · 参数回测（C# 版，移植自 scripts/t7_backtest.py + trading-system/06_backtest_*.py，
/// 桌面/移动端通用，不依赖 Python 运行时）。
///
/// 两套回测：
/// 1. A 桶（红利）：个股 TTM 股息率处于过去 5 年（滚动窗 1210 交易日）分位的阈值%以上 → 买入，
///    持有 60/120/250 交易日平仓，统计胜率/中位/均值收益。阈值扫描 60/70/80/90 分位。
/// 2. B/C 桶（成长/热门）：业绩动量代理信号——
///    B（成长）：净利同比≥25% 且 季度环比≥0（未减速）且 ROE≥12%；
///    C（热门）：净利同比≥50% 且 季度环比&gt;0（加速）。
///    买入 = 财报披露日近似（报告期末+15天）后首个交易日收盘，持有 60/120 交易日。
///    未含 C 桶关键词/行业景气判定（需另接文本源，与 Python 版口径一致）。
///
/// 结果 CSV：data/backtest_dividend_result.csv、data/backtest_growth_hot_result.csv
/// （Python 版写在 trading-system/ 下，C# 版统一收口 data/）；报告静默存档不推送。
/// 价格口径：新浪日K 2500 根不复权（与 Python 蓝本一致，KlineService 腾讯主源超长窗口时
/// 自动回退新浪 money.finance 接口）。
/// </summary>
public class BacktestTask : IBuiltinTask
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Key => "T7";
    public string Name => "参数回测";

    private readonly string _dataDir;
    private readonly EastMoneyClient _em;
    private readonly KlineService _klines;

    public BacktestTask(string dataDir, EastMoneyClient em, KlineService klines)
    {
        _dataDir = dataDir;
        _em = em;
        _klines = klines;
    }

    // ── A 桶：红利池 21 只（与 Python 06_backtest_dividend.py UNIVERSE 一致） ──

    private static readonly (string Symbol, string Code, string Name)[] DividendUniverse =
    {
        ("sh601398", "601398", "工行"), ("sh601939", "601939", "建行"), ("sh601288", "601288", "农行"),
        ("sh601988", "601988", "中行"), ("sh601328", "601328", "交行"), ("sh600036", "600036", "招行"),
        ("sh601166", "601166", "兴业"), ("sh600016", "600016", "民生"), ("sh601998", "601998", "中信"),
        ("sh600000", "600000", "浦发"), ("sh601088", "601088", "中国神华"), ("sh601225", "601225", "陕西煤业"),
        ("sh600188", "600188", "兖矿"), ("sh600050", "600050", "中国联通"), ("sh600900", "600900", "长电"),
        ("sh600011", "600011", "华能"), ("sh600795", "600795", "国电"), ("sh600377", "600377", "宁沪"),
        ("sh600350", "600350", "山东高速"), ("sh600028", "600028", "中石化"), ("sh601857", "601857", "中石油"),
    };
    private static readonly int[] Thresholds = { 60, 70, 80, 90 };
    private static readonly int[] DivHorizons = { 60, 120, 250 };

    // ── B/C 桶：8 个报告期（2023Q1 ~ 2024Q4） ─────────────────────

    private static readonly string[] GhDates =
    {
        "20230331", "20230630", "20230930", "20231231",
        "20240331", "20240630", "20240930", "20241231",
    };
    private static readonly int[] GhHorizons = { 60, 120 };
    private const int KlineCount = 2500; // 约 10 年日K

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string msg) => log?.Invoke($"[T7] {msg}");
        try
        {
            var today = TradingCalendar.NowCn();

            // ── A 桶（红利）回测 ──
            L($"A 桶（红利）股息率分位信号回测：{DividendUniverse.Length} 只红利池（K线 {KlineCount} 根不复权）…");
            var divAgg = Thresholds.ToDictionary(th => th,
                th => DivHorizons.ToDictionary(h => h, _ => new List<double>()));
            var divOk = 0;
            var divSkip = 0;
            foreach (var (_, code, name) in DividendUniverse)
            {
                try
                {
                    var rec = await BacktestDividendStockAsync(code, ct);
                    if (rec is null)
                    {
                        divSkip++;
                        L($"  skip {name}({code}) 数据不足");
                        continue;
                    }
                    foreach (var th in Thresholds)
                        foreach (var h in DivHorizons)
                            divAgg[th][h].AddRange(rec[th][h]);
                    divOk++;
                    L($"  ok {name}({code})");
                }
                catch (Exception e)
                {
                    divSkip++;
                    L($"  ERR {name}({code}): {e.Message}");
                }
                await Task.Delay(400, ct); // Python 版 0.4s 防限频
            }
            L($"A 桶完成：ok {divOk} / skip+err {divSkip}");

            // ── B/C 桶（成长/热门）回测 ──
            L("B/C 桶（成长/热门）业绩动量信号回测：8 个报告期…");
            var (growth, hot, pulled) = await BacktestGrowthHotAsync(L, ct);

            // ── 结果 CSV ──
            Directory.CreateDirectory(_dataDir);
            var divCsv = Path.Combine(_dataDir, "backtest_dividend_result.csv");
            var ghCsv = Path.Combine(_dataDir, "backtest_growth_hot_result.csv");

            var divRows = new List<string[]>();
            foreach (var th in Thresholds)
                foreach (var h in DivHorizons)
                    if (Stats(divAgg[th][h]) is { } s)
                        divRows.Add(new[] { th.ToString(Inv), h.ToString(Inv), s.N.ToString(Inv),
                            R2(s.Win), R2(s.Med), R2(s.Mean) });

            var ghRows = new List<string[]>();
            foreach (var (bucket, res) in new[] { ("B成长", growth), ("C热门", hot) })
                foreach (var h in GhHorizons)
                    if (Stats(res[h]) is { } s)
                        ghRows.Add(new[] { bucket, h.ToString(Inv), s.N.ToString(Inv),
                            R2(s.Win), R2(s.Med), R2(s.Mean) });

            WriteCsv(divCsv, "threshold,horizon,n,win_rate_pct,median_ret_pct,mean_ret_pct", divRows);
            WriteCsv(ghCsv, "bucket,horizon,n,win_rate_pct,median_ret_pct,mean_ret_pct", ghRows);
            L($"结果已保存 → {divCsv} / {ghCsv}");

            // ── 报告 ──
            var growthN = growth.Values.Sum(v => v.Count) / 2;
            var hotN = hot.Values.Sum(v => v.Count) / 2;
            var sections = new List<(string, string)>
            {
                ("A 桶（红利）回测结果",
                    $"红利池 {DividendUniverse.Length} 只（有效 {divOk} 只），"
                    + $"信号 = TTM 股息率 5 年滚动分位 ≥ 阈值（{KlineCount} 根不复权K线）。\n\n"
                    + Table(divRows, "threshold", "horizon", "n", "win_rate_pct", "median_ret_pct", "mean_ret_pct")),
                ("B/C 桶（成长/热门）回测结果",
                    $"业绩动量代理信号（8 个报告期 2023Q1~2024Q4，共拉取 {pulled} 只个股K线）。\n\n"
                    + Table(ghRows, "bucket", "horizon", "n", "win_rate_pct", "median_ret_pct", "mean_ret_pct")),
                ("使用说明",
                    "回测结果为**待评估值**，是否采纳需按 05 号文档 3.3 决策矩阵评估：\n"
                    + "- 若新阈值优于当前 yaml，标记为 candidate，进入 A/B 两季度 或 C 两月影子并跑。\n"
                    + "- 影子并跑期间**不改 yaml**，仅在 07 号台账用另一 signal_id 前缀（如 `SIG-shadow-`）记录。\n"),
            };
            var path = ReportBuilder.WriteReport(_dataDir, "T7",
                $"T7 参数回测 · {today:yyyy-MM-dd}", sections);
            L($"报告已写入 {path}");
            return new TaskRunResult(true, path, 0,
                $"A 桶 {divOk} 只标的、B 桶 {growthN} 样本、C 桶 {hotN} 样本；结果见 data/backtest_*.csv");
        }
        catch (Exception ex)
        {
            return new TaskRunResult(false, "", 0, $"T7 失败: {ex.Message}");
        }
    }

    // ── A 桶：股息率分位信号 ──────────────────────────────────────

    /// <summary>
    /// 单票回测：TTM 股息率（往前 365 天分红合计 / 收盘价）的 5 年滚动分位信号 → 前瞻收益。
    /// 返回 threshold → horizon → 收益列表；K线不足 600 根返回 null。
    /// </summary>
    private async Task<Dictionary<int, Dictionary<int, List<double>>>?> BacktestDividendStockAsync(
        string code, CancellationToken ct)
    {
        var kline = await _klines.GetStockDailyRawAsync(code, KlineCount);
        if (kline is not { Count: >= 600 }) return null;
        var divs = await _em.GetDividendsAsync(code, null, ct);

        var dates = kline.Select(b => b.Date).ToList();
        var closes = kline.Select(b => b.Close).ToList();

        // TTM 股息率：往前 365 天窗口内的每股分红合计 / 当日收盘
        var ttm = BuildTtmDps(divs, dates);
        var yields = new double[closes.Count];
        for (var i = 0; i < closes.Count; i++)
            yields[i] = closes[i] > 0 && ttm[i] > 0 ? ttm[i] / closes[i] : 0.0;

        var pct = RollingPercentile(yields, 1210); // ~5 年滚动分位

        // 信号：分位 ≥ 阈值 且 非持有 → 买入（记录各持有期前瞻收益）
        var records = Thresholds.ToDictionary(th => th,
            th => DivHorizons.ToDictionary(h => h, _ => new List<double>()));
        var holding = Thresholds.ToDictionary(th => th, _ => false);
        for (var i = 0; i < closes.Count; i++)
        {
            if (pct[i] is null) continue;
            foreach (var th in Thresholds)
            {
                var sig = pct[i]!.Value >= th;
                if (sig && !holding[th])
                {
                    holding[th] = true;
                    foreach (var h in DivHorizons)
                        if (i + h < closes.Count)
                            records[th][h].Add(closes[i + h] / closes[i] - 1);
                }
                if (!sig) holding[th] = false;
            }
        }
        return records;
    }

    /// <summary>每个交易日的 TTM 每股分红（往前 365 天窗口内分红合计）。</summary>
    private static double[] BuildTtmDps(List<DividendRow> divs, List<DateTime> dates)
    {
        var res = new double[dates.Count];
        for (var i = 0; i < dates.Count; i++)
        {
            double s = 0;
            foreach (var d in divs)
            {
                var days = (dates[i] - d.ExDate).Days;
                if (days is >= 0 and <= 365) s += d.Dps;
            }
            res[i] = s;
        }
        return res;
    }

    /// <summary>滚动窗口分位（0-100）；窗口不足 250 个样本（约1年）返回 null 不给信号。</summary>
    private static double?[] RollingPercentile(double[] series, int window)
    {
        var res = new double?[series.Length];
        for (var i = 0; i < series.Length; i++)
        {
            var lo = Math.Max(0, i - window + 1);
            if (i - lo + 1 < 250) continue;
            var cur = series[i];
            var cnt = 0;
            for (var k = lo; k <= i; k++)
                if (series[k] <= cur) cnt++;
            res[i] = 100.0 * cnt / (i - lo + 1);
        }
        return res;
    }

    // ── B/C 桶：业绩动量信号 ──────────────────────────────────────

    /// <summary>
    /// 8 个报告期全市场 yjbb → 信号票 → 披露日（期末+15天）后首个交易日收盘买入 →
    /// 持有 60/120 交易日前瞻收益。返回（成长收益表, 热门收益表, 已拉K线股票数）。
    /// </summary>
    private async Task<(Dictionary<int, List<double>> Growth, Dictionary<int, List<double>> Hot, int Pulled)>
        BacktestGrowthHotAsync(Action<string> L, CancellationToken ct)
    {
        var growth = GhHorizons.ToDictionary(h => h, _ => new List<double>());
        var hot = GhHorizons.ToDictionary(h => h, _ => new List<double>());
        var pulled = new HashSet<string>(StringComparer.Ordinal);   // 已成功拉取K线（含缓存）
        var failed = new HashSet<string>(StringComparer.Ordinal);   // 拉取失败，本轮不再重试

        foreach (var date in GhDates)
        {
            var yjbb = await _em.GetYjbbAsync(date, L, ct);
            var sigG = new List<string>();
            var sigH = new List<string>();
            foreach (var r in yjbb)
            {
                if (r.NpYoy is null || r.Qoq is null) continue;
                // B 成长：同比≥25 且 环比≥0（未减速）且 ROE≥12（缺失按 0，不满足）
                if (r.NpYoy >= 25 && r.Qoq >= 0 && (r.Roe ?? 0) >= 12) sigG.Add(r.Code);
                // C 热门：同比≥50 且 环比>0（加速）
                if (r.NpYoy >= 50 && r.Qoq > 0) sigH.Add(r.Code);
            }

            // 披露日近似 = 报告期末 + 15 天
            var tgt = DateTime.ParseExact(date, "yyyyMMdd", Inv).AddDays(15);

            foreach (var code in sigG)
            {
                if (!pulled.Contains(code) && !failed.Contains(code)) await Task.Delay(80, ct);
                await AddForwardAsync(code, tgt, growth);
            }
            foreach (var code in sigH)
            {
                if (!pulled.Contains(code) && !failed.Contains(code)) await Task.Delay(80, ct);
                await AddForwardAsync(code, tgt, hot);
            }
            L($"  {date}: 成长候选 {sigG.Count} 热门候选 {sigH.Count}（累计K线 {pulled.Count} 只）");
        }
        return (growth, hot, pulled.Count);

        async Task AddForwardAsync(string code, DateTime target, Dictionary<int, List<double>> sink)
        {
            if (failed.Contains(code)) return;
            IReadOnlyList<DailyBar>? kl = null;
            try { kl = await _klines.GetStockDailyRawAsync(code, KlineCount); }
            catch { }
            if (kl is not { Count: > 0 })
            {
                failed.Add(code); // 失败缓存，避免同一票反复重试拖慢整体
                return;
            }
            pulled.Add(code);

            // 披露日后首个交易日（K线升序）
            int? idx = null;
            for (var i = 0; i < kl.Count; i++)
                if (kl[i].Date >= target)
                {
                    idx = i;
                    break;
                }
            if (idx is null) return;
            foreach (var h in GhHorizons)
            {
                var j = idx.Value + h;
                if (j < kl.Count)
                    sink[h].Add(kl[j].Close / kl[idx.Value].Close - 1);
            }
        }
    }

    // ── 统计与输出 ────────────────────────────────────────────────

    private sealed record Stat(int N, double Win, double Med, double Mean);

    /// <summary>胜率/中位/均值（%）。中位取下中位数，与 Python sorted(vals)[n//2] 一致；空列表返回 null。</summary>
    private static Stat? Stats(List<double> vals)
    {
        if (vals.Count == 0) return null;
        var s = vals.OrderBy(v => v).ToList();
        return new Stat(s.Count,
            100.0 * s.Count(v => v > 0) / s.Count,
            s[s.Count / 2] * 100,
            s.Sum() / s.Count * 100);
    }

    /// <summary>round(x, 2) 后的原样数字文本（Python round 为银行家舍入，C# Math.Round 默认一致）。</summary>
    private static string R2(double v) => Math.Round(v, 2).ToString(Inv);

    private static void WriteCsv(string path, string header, List<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", r));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Table(List<string[]> rows, params string[] cols)
    {
        var dictRows = rows.Select(r => cols.Select((c, i) => (c, r[i]))
            .ToDictionary(t => t.Item1, t => t.Item2, StringComparer.Ordinal)).ToList();
        return ReportBuilder.RenderKvTable(dictRows, cols);
    }
}
