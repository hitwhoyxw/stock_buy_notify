using System.Globalization;
using System.Text;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T5 · 季度归因数据准备（C# 版，移植自 scripts/t5_prepare.py，桌面/移动端通用）。
///
/// 组装 LLM 归因输入 → data/skill_input_T5.md：
/// 1. 交易日志（区间过滤）CSV
/// 2. 信号台账（区间过滤）CSV
/// 3. 市场与各桶基准指数区间收益（沪深300 / 桶A 000922 / 桶B 000852 / 桶C 000905）
///
/// LLM 产出由用户/CI 写回 data/skill_output_T5.md（文件交接，任务内不调用 LLM）。
/// 默认统计上一个自然季（如 8 月运行 → 2026Q2）。
/// </summary>
public class AttributionPrepTask : IBuiltinTask
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Key => "T5";
    public string Name => "季度归因准备";

    private readonly string _dataDir;
    private readonly DataStore _store;
    private readonly SignalLogStore _signals;
    private readonly KlineService _klines;

    public AttributionPrepTask(string dataDir, DataStore store, SignalLogStore signals, KlineService klines)
    {
        _dataDir = dataDir;
        _store = store;
        _signals = signals;
        _klines = klines;
    }

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string msg) => log?.Invoke($"[T5] {msg}");
        try
        {
            var today = TradingCalendar.NowCn();
            var q = (today.Month - 1) / 3; // 上一自然季
            var season = q == 0 ? $"{today.Year - 1}Q4" : $"{today.Year}Q{q}";
            var (start, end) = SeasonDateRange(season);
            L($"季度={season}  日期范围={start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}");

            // 1. 交易日志（区间过滤）
            var tradeRows = _store.ReadCsv("live_trade_log.csv").Rows
                .Where(r => DateInRange(r.GetValueOrDefault("日期", ""), start, end)).ToList();
            L($"交易日志：{tradeRows.Count} 条");

            // 2. 信号台账（区间过滤）
            var signalRows = _signals.ReadAll()
                .Where(r => DateInRange(r.GetValueOrDefault("触发日期", ""), start, end)).ToList();
            L($"信号台账：{signalRows.Count} 条");

            // 3. 基准指数区间收益
            var benchLines = new List<string>
            {
                await FetchBenchmarkAsync("000300", "沪深300", start, end, ct),
                "[桶A基准] " + await FetchBenchmarkAsync("000922", "中证红利", start, end, ct),
                "[桶B基准] " + await FetchBenchmarkAsync("000852", "中证1000", start, end, ct),
                "[桶C基准] " + await FetchBenchmarkAsync("000905", "中证500", start, end, ct),
            };
            foreach (var b in benchLines)
                L($"基准：{b}");

            // 4. 组装输出（=== SECTION === 格式对齐 Python 版）
            var sb = new StringBuilder();
            sb.AppendLine($"=== SEASON: {season} ===");
            sb.AppendLine();
            sb.AppendLine("=== TRADE_LOG ===");
            sb.AppendLine(tradeRows.Count > 0 ? ToCsv(TradeCols, tradeRows) : "（无交易记录）");
            sb.AppendLine();
            sb.AppendLine("=== SIGNAL_LOG ===");
            sb.AppendLine(signalRows.Count > 0 ? ToCsv(SignalLogStore.Columns, signalRows) : "（无信号记录）");
            sb.AppendLine();
            sb.AppendLine("=== BENCHMARKS ===");
            sb.AppendLine(string.Join("\n", benchLines));
            sb.AppendLine();
            sb.AppendLine("=== CURRENT_YAML_HASH ===");
            sb.AppendLine(StrategyConfig.YamlTag);

            var path = Path.Combine(_dataDir, "skill_input_T5.md");
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            L($"输入文件已生成：{path}（{new FileInfo(path).Length:N0} bytes）");
            return new TaskRunResult(true, path, 0,
                $"{season} 归因输入已生成：交易 {tradeRows.Count} 条 / 信号 {signalRows.Count} 条；LLM 产出写回 skill_output_T5.md");
        }
        catch (Exception ex)
        {
            return new TaskRunResult(false, "", 0, $"T5 失败: {ex.Message}");
        }
    }

    // 交易日志列（与 DataStore.TradeColumns 同顺序；该数组为私有，此处按模板声明）
    private static readonly string[] TradeCols =
    {
        "日期","方向","桶","代码","名称","申万一级行业",
        "价格","股数","金额","占总资产%","触发规则ID",
        "触发时指标值","阈值","决策理由(一句话)","当时组合状态",
        "当时四桶权重ABCD","情绪自评(1-5)","是否违反纪律",
        "事后30日涨跌%","事后90日涨跌%","复盘结论",
    };

    private static bool DateInRange(string raw, DateTime start, DateTime end)
        => DateTime.TryParse(raw, Inv, DateTimeStyles.None, out var d) && d >= start && d <= end;

    private static (DateTime Start, DateTime End) SeasonDateRange(string season)
    {
        var year = int.Parse(season[..4], Inv);
        var q = int.Parse(season[^1..], Inv);
        var startMonth = (q - 1) * 3 + 1;
        var start = new DateTime(year, startMonth, 1);
        var end = q == 4 ? new DateTime(year, 12, 31) : new DateTime(year, startMonth + 3, 1).AddDays(-1);
        return (start, end);
    }

    /// <summary>基准指数区间收益行（格式对齐 Python：起止日期+价格+收益率；数据不足给 N/A）。</summary>
    private async Task<string> FetchBenchmarkAsync(
        string code, string name, DateTime start, DateTime end, CancellationToken ct)
    {
        IReadOnlyList<DailyBar>? bars = null;
        try { bars = await _klines.GetIndexDailyAsync(code, 320); }
        catch { /* 单个基准失败不阻断整个任务 */ }
        var inRange = bars?.Where(b => b.Date >= start && b.Date <= end).ToList();
        if (inRange is not { Count: >= 2 })
            return $"{code}  {name}  start={start:yyyy-MM-dd} end={end:yyyy-MM-dd} return=N/A（数据不足）";

        var sp = inRange[0].Close;
        var ep = inRange[^1].Close;
        var ret = (ep / sp - 1) * 100;
        return $"{code}  {name}  {inRange[0].Date:yyyy-MM-dd} start={sp:0.00}  "
            + $"{inRange[^1].Date:yyyy-MM-dd} end={ep:0.00}  return={ret:+0.00;-0.00}%";
    }

    /// <summary>rows → CSV 文本（与 pandas to_csv 等价：表头 + 数据行，含逗号/引号转义）。</summary>
    private static string ToCsv(IReadOnlyList<string> cols, List<Dictionary<string, string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(Csv)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", cols.Select(c => Csv(r.GetValueOrDefault(c, "")))));
        return sb.ToString();
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
