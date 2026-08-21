using System.Globalization;
using System.Text;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T8 · 信号台账维护（C# 版，移植自 scripts/t8_signal_log.py，桌面/移动端通用）。
///
/// 1. 回补历史信号收益：对每条"是否实际执行=是"的记录，已过 60/120/250 交易日 →
///    拉执行价 & 当期价 → 算绝对收益、超沪深300、超分桶基准；每 horizon 回补一次即写死。
/// 2. 实盘 vs 回测胜率：按桶×规则ID 汇总最近 90 天记录，样本 ≥5 且 delta < -10pct → 失效预警 P0。
/// </summary>
public class SignalLogTask : IBuiltinTask
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Key => "T8";
    public string Name => "信号台账";

    private readonly SignalLogStore _signals;
    private readonly KlineService _klines;
    private readonly TradingCalendar _calendar;
    private readonly string _dataDir;

    public SignalLogTask(string dataDir, SignalLogStore signals, KlineService klines, TradingCalendar calendar)
    {
        _dataDir = dataDir;
        _signals = signals;
        _klines = klines;
        _calendar = calendar;
    }

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string msg) => log?.Invoke($"[T8] {msg}");
        try
        {
            var today = TradingCalendar.NowCn();
            var rows = _signals.ReadAll();
            var updatedIds = new List<string>();

            if (rows.Count > 0)
            {
                L($"台账 {rows.Count} 条，开始收益回补…");
                foreach (var row in rows)
                {
                    if (row.GetValueOrDefault("是否实际执行", "") != "是") continue;
                    string signalId;
                    List<(string Col, string Val)> patch;
                    try
                    {
                        signalId = row.GetValueOrDefault("signal_id", "");
                        patch = await ComputeReturnsAsync(row, today, L);
                    }
                    catch (Exception ex)
                    {
                        L($"{row.GetValueOrDefault("signal_id", "")} 回补失败({ex.Message})，跳过");
                        continue;
                    }
                    if (patch.Count == 0) continue;
                    _signals.UpdateSignal(signalId, patch.ToDictionary(p => p.Col, p => p.Val));
                    updatedIds.Add(signalId);
                }
            }
            L($"回补完成，本次更新 {updatedIds.Count} 条");

            // 重新读一遍拿最新收益，做失效预警
            rows = _signals.ReadAll();
            var decayAlerts = CheckAlphaDecay(rows, today);
            foreach (var a in decayAlerts)
                _signals.AppendSignal(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["触发日期"] = today.ToString("yyyy-MM-dd"),
                    ["yaml_version_at_trigger"] = "c#-builtin",
                    ["触发任务"] = "T8",
                    ["桶"] = a.Bucket,
                    ["规则ID"] = a.RuleId,
                    ["标的代码"] = "-",
                    ["标的名称"] = "失效预警",
                    ["触发时指标值"] = a.Current,
                    ["阈值"] = a.Threshold,
                    ["当时组合状态"] = "-",
                    ["信号方向"] = "暂停",
                    ["建议动作"] = a.Action,
                    ["是否实际执行"] = "否",
                    ["信号最终评价"] = "alpha decay",
                });
            if (decayAlerts.Count > 0)
                L($"失效预警 {decayAlerts.Count} 条（已写入台账）");

            var path = BuildReport(rows, updatedIds, decayAlerts, today);
            L($"报告已写入 {path}");

            var summary = $"台账 {rows.Count} 条，本次回补 {updatedIds.Count} 行，失效预警 {decayAlerts.Count} 条";
            return new TaskRunResult(true, path, decayAlerts.Count, summary);
        }
        catch (Exception ex)
        {
            L($"运行失败: {ex}");
            return new TaskRunResult(false, "", 0, $"T8 异常: {ex.Message}");
        }
    }

    // ── 收益回补 ──────────────────────────────────────────────────

    /// <summary>取标的在 target 当天（非交易日则最近下一交易日）的收盘价。</summary>
    private async Task<double?> GetCloseOnAsync(string code, DateTime target)
    {
        if (string.IsNullOrWhiteSpace(code) || code == "-") return null;
        var bars = await _klines.GetStockDailyAsync(code, 320);
        if (bars is not { Count: > 0 }) return null;
        var candidates = bars.Where(b => b.Date >= target).OrderBy(b => b.Date).ToList();
        return candidates.Count > 0 && candidates[0].Close > 0 ? candidates[0].Close : null;
    }

    /// <summary>取指数在 target 当天（非交易日则最近下一交易日）的收盘价。</summary>
    private async Task<double?> GetIndexCloseOnAsync(string code, DateTime target)
    {
        var bars = await _klines.GetIndexDailyAsync(code, 320);
        if (bars is not { Count: > 0 }) return null;
        var candidates = bars.Where(b => b.Date >= target).OrderBy(b => b.Date).ToList();
        return candidates.Count > 0 && candidates[0].Close > 0 ? candidates[0].Close : null;
    }

    /// <summary>分桶基准指数（A:中证红利 B:创业板指 C:中证500 其余:沪深300）。</summary>
    private static string BucketBenchmarkCode(string bucket) => bucket switch
    {
        "A" => "000922",
        "B" => "399006",
        "C" => "000905",
        _ => "000300",
    };

    /// <summary>对单行信号回补 60/120/250 收益（已回补过的列跳过）。返回 (列名, 值) 补丁。</summary>
    private async Task<List<(string Col, string Val)>> ComputeReturnsAsync(
        Dictionary<string, string> row, DateTime today, Action<string> log)
    {
        var patch = new List<(string, string)>();

        var execDateStr = row.GetValueOrDefault("执行日期", "");
        if (string.IsNullOrWhiteSpace(execDateStr)) execDateStr = row.GetValueOrDefault("触发日期", "");
        if (!DateTime.TryParseExact(execDateStr, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var execDate))
            return patch;

        // T+1 开盘起算口径：简化为 exec_date 后第 1 个交易日的收盘价
        var baseDate = await _calendar.DaysOffsetToDateAsync(execDate, 1);
        if (baseDate is not { } bd) return patch;

        var code = row.GetValueOrDefault("标的代码", "").Trim();
        var execPriceStr = row.GetValueOrDefault("执行价格", "").Trim();
        double? basePrice = double.TryParse(execPriceStr, NumberStyles.Any, Inv, out var ep) && ep > 0 ? ep : null;
        basePrice ??= await GetCloseOnAsync(code, bd);
        if (basePrice is not { } bp || bp <= 0) return patch;

        var hs300Base = await GetIndexCloseOnAsync("000300", bd);
        var benchCode = row.GetValueOrDefault("分桶基准代码", "").Trim();
        if (benchCode.Length == 0) benchCode = BucketBenchmarkCode(row.GetValueOrDefault("桶", "").Trim());
        var benchBase = await GetIndexCloseOnAsync(benchCode, bd);

        foreach (var (days, retCol, exHsCol, exBucketCol) in SignalLogStore.ReturnHorizons)
        {
            var target = await _calendar.DaysOffsetToDateAsync(bd, days);
            if (target is not { } t || t > today) continue;      // 未到期
            if (!string.IsNullOrWhiteSpace(row.GetValueOrDefault(retCol, ""))) continue; // 已回补

            var stockEnd = await GetCloseOnAsync(code, t);
            if (stockEnd is not { } se) continue;
            var ret = (se / bp - 1) * 100;
            patch.Add((retCol, ret.ToString("F2", Inv)));

            if (hs300Base is { } hb)
            {
                var hsEnd = await GetIndexCloseOnAsync("000300", t);
                if (hsEnd is { } he)
                {
                    var hsRet = (he / hb - 1) * 100;
                    patch.Add((exHsCol, (ret - hsRet).ToString("F2", Inv)));
                }
            }
            if (benchBase is { } bb)
            {
                var benchEnd = await GetIndexCloseOnAsync(benchCode, t);
                if (benchEnd is { } be)
                {
                    var bRet = (be / bb - 1) * 100;
                    patch.Add((exBucketCol, (ret - bRet).ToString("F2", Inv)));
                }
            }
        }

        if (patch.Count > 0 && string.IsNullOrWhiteSpace(row.GetValueOrDefault("分桶基准代码", "")))
            patch.Add(("分桶基准代码", benchCode));
        return patch;
    }

    // ── 失效预警 ──────────────────────────────────────────────────

    /// <summary>按桶+规则ID 汇总最近 90 天已执行记录，实盘胜率 &lt; 回测预期 - 10pct 且 n≥5 → P0。</summary>
    private static List<RiskAlert> CheckAlphaDecay(List<Dictionary<string, string>> rows, DateTime today)
    {
        var alerts = new List<RiskAlert>();
        if (rows.Count == 0) return alerts;
        var cutoff = today.AddDays(-90);

        var recent = rows
            .Where(r => r.GetValueOrDefault("是否实际执行", "") == "是"
                        && DateTime.TryParseExact(r.GetValueOrDefault("触发日期", ""), "yyyy-MM-dd",
                            Inv, DateTimeStyles.None, out var d) && d >= cutoff)
            .ToList();
        if (recent.Count == 0) return alerts;

        static double? Pct(string? v)
        {
            var s = (v ?? "").Replace("%", "").Trim();
            return double.TryParse(s, NumberStyles.Any, Inv, out var x) && s.Length > 0 ? x : null;
        }

        foreach (var g in recent.GroupBy(r => (r.GetValueOrDefault("桶", ""), r.GetValueOrDefault("规则ID", ""))))
        {
            var list = g.ToList();
            var rets = list.Select(r => Pct(r.GetValueOrDefault("事后60日收益%"))).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (rets.Count < 5) continue;

            var liveWinrate = rets.Count(r => r > 0) * 100.0 / rets.Count;
            var expected = list.Select(r => Pct(r.GetValueOrDefault("回测预期胜率"))).FirstOrDefault(v => v.HasValue);
            if (expected is not { } exp) continue;

            var delta = liveWinrate - exp;
            if (delta >= -10) continue;

            var (bucket, rule) = g.Key;
            alerts.Add(new RiskAlert("P0", $"DECAY-{bucket}-{rule}", bucket, $"{bucket} 桶规则 {rule}",
                $"实盘胜率 {liveWinrate:F1}% (n={rets.Count})",
                $"回测预期 {exp:F1}%（差 {delta:+0.0;-0.0}pct）",
                "暂停使用该规则，触发 T7 重新回测；参数变更前不得再触发新信号",
                "T8 实盘vs回测"));
        }
        return alerts;
    }

    // ── 报告 ──────────────────────────────────────────────────────

    private string BuildReport(List<Dictionary<string, string>> rows, List<string> updatedIds,
        List<RiskAlert> decayAlerts, DateTime today)
    {
        var executed = rows.Count(r => r.GetValueOrDefault("是否实际执行", "") == "是");
        var pendingBackfill = rows.Count(r => r.GetValueOrDefault("是否实际执行", "") == "是"
            && (string.IsNullOrWhiteSpace(r.GetValueOrDefault("事后60日收益%", ""))
                || string.IsNullOrWhiteSpace(r.GetValueOrDefault("事后120日收益%", ""))));

        var overview =
            $"| 维度 | 数值 |\n|------|------|\n"
            + $"| 台账总信号数 | {rows.Count} |\n"
            + $"| 已实际执行 | {executed} |\n"
            + $"| 待回补收益 | {pendingBackfill} |\n"
            + $"| 本次回补 | {updatedIds.Count} |\n"
            + $"| 失效预警 | {decayAlerts.Count} 条 |\n";

        var recentRows = rows.TakeLast(10).Reverse().Select(r => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["signal_id"] = r.GetValueOrDefault("signal_id", ""),
            ["日期"] = r.GetValueOrDefault("触发日期", ""),
            ["桶"] = r.GetValueOrDefault("桶", ""),
            ["规则"] = r.GetValueOrDefault("规则ID", ""),
            ["标的"] = $"{r.GetValueOrDefault("标的代码", "")} {r.GetValueOrDefault("标的名称", "")}".Trim(),
            ["方向"] = r.GetValueOrDefault("信号方向", ""),
            ["已执行"] = r.GetValueOrDefault("是否实际执行", ""),
            ["60d收益"] = string.IsNullOrWhiteSpace(r.GetValueOrDefault("事后60日收益%", "")) ? "-" : r.GetValueOrDefault("事后60日收益%", ""),
        }).ToList();
        var recentMd = recentRows.Count > 0
            ? ReportBuilder.RenderKvTable(recentRows, ["signal_id", "日期", "桶", "规则", "标的", "方向", "已执行", "60d收益"])
            : "_台账为空_\n";

        var backfillMd = updatedIds.Count > 0
            ? $"本次回补 signal_id：`{string.Join(", ", updatedIds.Take(20))}{(updatedIds.Count > 20 ? "…" : "")}`\n"
            : "本次无需回补。\n";

        var sections = new List<(string, string)>
        {
            ("台账总览", overview),
            ("最近信号（倒序前 10）", recentMd),
            ("回补明细", backfillMd),
            ("失效预警", decayAlerts.Count > 0 ? ReportBuilder.RenderAlertList(decayAlerts) : "本次无失效预警。\n"),
        };
        return ReportBuilder.WriteReport(_dataDir, "T8", $"T8 台账维护 · {today:yyyy-MM-dd}", sections, decayAlerts);
    }
}
