using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 一笔待审核交易的客观事实快照（纪律审核的「系统自动」数据来源）。
/// 所有字段都可能缺失：取数失败时保持 null，由审核层标为「数据缺失」而不是当作 0 通过。
/// </summary>
public sealed class TradeReviewFacts
{
    /// <summary>用户输入的原始内容（6 位代码，可能带 sh/sz 前缀）。</summary>
    public string InputCode { get; init; } = "";
    /// <summary>标准化后的 6 位代码。</summary>
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";

    public decimal Price { get; init; }
    public decimal PrevClose { get; init; }
    /// <summary>当日涨跌幅 %（负数为下跌）。</summary>
    public double? ChangePct { get; init; }

    /// <summary>审核时刻（北京时间）。</summary>
    public DateTime NowCn { get; init; }
    /// <summary>是否为 A 股交易日。</summary>
    public bool? IsTradingDay { get; init; }
    /// <summary>是否处于连续竞价交易时段。</summary>
    public bool IsMarketTime { get; init; }

    /// <summary>基准指数名称（默认沪深300）。</summary>
    public string IndexName { get; init; } = "沪深300";
    public string IndexCode { get; init; } = "000300";
    /// <summary>指数最新涨跌幅 %。</summary>
    public double? IndexChangePct { get; init; }
    /// <summary>指数近 20 日最大回撤 %（正数表示回撤幅度）。</summary>
    public double? IndexDrawdown20dPct { get; init; }
    /// <summary>指数取数说明（数据缺失时给原因）。</summary>
    public string IndexNote { get; init; } = "";

    /// <summary>个股近 5 日涨跌幅 %（辅助判断是「单日大跌」还是「趋势下跌」）。</summary>
    public double? StockChange5dPct { get; init; }

    public bool HasQuote => ChangePct is not null;
    public bool HasIndex => IndexChangePct is not null;

    /// <summary>渲染成给 LLM / 用户看的客观事实表（markdown）。缺失项显式标「数据缺失」。</summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"- 股票：{(Name.Length > 0 ? Name : "（名称未知）")}（{Code}）");
        sb.AppendLine($"- 审核时间：{NowCn:yyyy-MM-dd HH:mm}（北京时间）"
                      + $"{(IsMarketTime ? "，连续竞价时段内" : "，非连续竞价时段")}");
        if (IsTradingDay is { } td)
            sb.AppendLine($"- 交易日：{(td ? "是" : "否（非交易日，行情为最近一根）")}");

        if (HasQuote)
            sb.AppendLine($"- 当日价格：现价 {Price:F2}，昨收 {PrevClose:F2}，涨跌幅 {ChangePct:+0.00;-0.00;0.00}%");
        else
            sb.AppendLine("- 当日价格：数据缺失（行情取数失败）");

        if (StockChange5dPct is { } c5)
            sb.AppendLine($"- 个股近 5 日涨跌幅：{c5:+0.00;-0.00;0.00}%");

        if (HasIndex)
        {
            sb.AppendLine($"- {IndexName}（{IndexCode}）涨跌幅：{IndexChangePct:+0.00;-0.00;0.00}%");
            if (IndexDrawdown20dPct is { } dd)
                sb.AppendLine($"- {IndexName} 近 20 日最大回撤：{dd:F2}%");
        }
        else
        {
            sb.AppendLine($"- {IndexName} 涨跌幅：数据缺失"
                          + (IndexNote.Length > 0 ? $"（{IndexNote}）" : ""));
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 纪律审核事实采集：把「个股跌幅 / 大盘涨跌 / 当前时间 / 是否交易日」等客观数据取齐。
/// 纪律规则里的「系统自动」项都由这里提供；取不到的保持 null，绝不猜。
/// </summary>
public sealed class DisciplineFactsCollector
{
    private readonly QuoteService _quotes;
    private readonly KlineService _klines;
    private readonly TradingCalendar? _calendar;

    public DisciplineFactsCollector(QuoteService quotes, KlineService klines, TradingCalendar? calendar = null)
    {
        _quotes = quotes;
        _klines = klines;
        _calendar = calendar;
    }

    /// <summary>基准指数（纪律里用沪深300 代表「大环境」）。</summary>
    public const string BenchmarkIndexCode = "000300";
    public const string BenchmarkIndexName = "沪深300";

    /// <summary>
    /// 采集一笔交易的事实。now 便于离线回归注入固定时间；取数失败降级为 null 字段，不抛异常。
    /// </summary>
    public async Task<TradeReviewFacts> CollectAsync(string code, DateTime? now = null,
        CancellationToken ct = default)
    {
        var pure = NormalizeCode(code);
        var nowCn = now ?? TradingCalendar.NowCn();

        // ── 个股实时行情 ──
        decimal price = 0, prevClose = 0;
        double? changePct = null;
        string name = "";
        try
        {
            var map = await _quotes.FetchAsync(new[] { pure }, ct);
            if (map.TryGetValue(pure, out var q))
            {
                price = q.Price;
                prevClose = q.PrevClose;
                name = q.Name ?? "";
                if (q.PrevClose > 0 && q.Price > 0) changePct = q.ChangePct;
            }
        }
        catch { /* 行情失败：changePct 保持 null，审核层标数据缺失 */ }

        // 名称兜底：同花顺主源无名称字段（QuoteService 注释已说明），走腾讯快照补全，
        // 否则审核结论里只有一串代码，用户核对时容易看错票。
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var snap = await new TencentSnapshot().GetBatchAsync(new[] { pure }, ct: ct);
                if (snap.TryGetValue(pure, out var f) && !string.IsNullOrWhiteSpace(f.Name))
                    name = f.Name;
            }
            catch { /* 名称拿不到不影响判定，显示代码即可 */ }
        }

        // ── 个股近 5 日涨跌（区分「单日大跌」与「趋势下跌」）──
        double? change5d = null;
        try
        {
            var bars = await _klines.GetStockDailyAsync(pure, 8);
            change5d = PctOverLastN(bars, 5);
        }
        catch { }

        // ── 基准指数：最新涨跌 + 近 20 日最大回撤 ──
        double? indexPct = null, drawdown = null;
        var indexNote = "";
        try
        {
            var idx = await _klines.GetIndexDailyAsync(BenchmarkIndexCode, 25);
            if (idx is { Count: >= 2 })
            {
                var last = idx[^1];
                var prev = idx[^2];
                if (prev.Close > 0)
                    indexPct = (last.Close - prev.Close) / prev.Close * 100.0;

                // 近 20 日最大回撤：从区间内滚动高点的最大跌幅
                var window = idx.Skip(Math.Max(0, idx.Count - 21)).ToList();
                double peak = 0, worst = 0;
                foreach (var b in window)
                {
                    if (b.Close > peak) peak = b.Close;
                    if (peak > 0)
                    {
                        var dd = (peak - b.Close) / peak * 100.0;
                        if (dd > worst) worst = dd;
                    }
                }
                drawdown = worst;
            }
            else indexNote = "指数日K不足 2 根";
        }
        catch (Exception ex) { indexNote = "指数日K取数失败: " + ex.Message; }

        // ── 是否交易日（日历不可用时留 null，不猜测）──
        bool? isTradingDay = null;
        if (_calendar is not null)
        {
            try { isTradingDay = await _calendar.IsTradingDayAsync(nowCn.Date); }
            catch { }
        }

        return new TradeReviewFacts
        {
            InputCode = code?.Trim() ?? "",
            Code = pure,
            Name = name,
            Price = price,
            PrevClose = prevClose,
            ChangePct = changePct,
            NowCn = nowCn,
            IsTradingDay = isTradingDay,
            IsMarketTime = MarketTimes.IsMarketTime(nowCn),
            IndexName = BenchmarkIndexName,
            IndexCode = BenchmarkIndexCode,
            IndexChangePct = indexPct,
            IndexDrawdown20dPct = drawdown,
            IndexNote = indexNote,
            StockChange5dPct = change5d,
        };
    }

    /// <summary>近 n 根 K 线的涨跌幅 %（不足 n+1 根返回 null）。</summary>
    private static double? PctOverLastN(IReadOnlyList<DailyBar>? bars, int n)
    {
        if (bars is null || bars.Count < n + 1) return null;
        var last = bars[^1];
        var baseBar = bars[^(n + 1)];
        return baseBar.Close > 0 ? (last.Close - baseBar.Close) / baseBar.Close * 100.0 : null;
    }

    /// <summary>把用户输入（可含 sh/sz 前缀、.SH 后缀）标准化成 6 位纯代码。</summary>
    public static string NormalizeCode(string code)
    {
        var s = (code ?? "").Trim().ToUpperInvariant();
        if (s.Contains('.')) s = s.Split('.')[0];       // 600519.SH → 600519
        s = s.TrimStart('S', 'Z', 'H', 'B', 'J');       // sh600519 / sz000001 / bj830799
        if (s.Length > 6) s = s[^6..];
        return s.PadLeft(6, '0');
    }
}
