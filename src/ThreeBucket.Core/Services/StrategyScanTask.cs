using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T9 · 策略全市场扫描（尾盘选股）。
/// 股票池：东财 clist 分页拉沪深 A 股全市场（<see cref="EastMoneySnapshot"/>，可缓存数天/一周），
/// 剔 ST / 北交所后并发拉日K，用 StrategyEngine 评估「策略扫描页维护的扫描名单」（data/strategy_scan_ids.txt）
/// （默认含 S26 深调底部反转等），按策略分组输出命中列表（不含业绩同比环比）。
///
/// 产出：
/// - data/strategy_scan.csv（全量命中，含 strategy_id）
/// - data/strategy_scan_{策略ID}.csv（分策略子表）
/// - data/report_yyyy-MM-dd_T9.md
///
/// 限流：MaxScan=0 表示不截断全市场（可改属性），Semaphore 控制并发；
/// 日K走 KlineService 缓存链（同花顺→腾讯→新浪），不新造券商接口。
/// </summary>
public sealed class StrategyScanTask : IBuiltinTask
{
    public string Key => "T9";
    public string Name => "策略全市场扫描";

    /// <summary>单次最多评估的股票数（0=不截断全市场；默认 0）。</summary>
    public int MaxScan { get; set; } = 0;

    /// <summary>日K并发度。</summary>
    public int Concurrency { get; set; } = 8;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _dataDir;
    private readonly DataStore _store;
    private readonly KlineService _klines;
    private readonly EastMoneySnapshot _emSnap;

    public StrategyScanTask(string dataDir, DataStore store, KlineService klines,
        EastMoneySnapshot? emSnap = null)
    {
        _dataDir = dataDir;
        _store = store;
        _klines = klines;
        _emSnap = emSnap ?? new EastMoneySnapshot(Path.Combine(dataDir, "cache"));
    }

    /// <summary>兼容旧签名（忽略 EastMoneyClient；股票池不再依赖 yjbb）。</summary>
    public StrategyScanTask(string dataDir, DataStore store, EastMoneyClient em, KlineService klines,
        EastMoneySnapshot? emSnap = null)
        : this(dataDir, store, klines, emSnap ?? new EastMoneySnapshot(Path.Combine(dataDir, "cache")))
    {
        _ = em;
    }

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) { log?.Invoke(m); Console.WriteLine(m); }
        try
        {
            var today = TradingCalendar.NowCn();
            L($"[T9] {today:yyyy-MM-dd} · 策略全市场扫描开始…");

            // 扫描名单来自 data/strategy_scan_ids.txt（策略扫描页维护）；不在此自动塞全部策略
            var listed = _store.ListStrategyScanStrategies();
            var missing = _store.ListStrategyScanIds()
                .Where(id => listed.All(s => !string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count > 0)
                L($"[T9] 名单中有 {missing.Count} 个 ID 在策略库已不存在，已跳过：" + string.Join(", ", missing));

            var strategies = listed
                .Where(s => !string.IsNullOrWhiteSpace(s.Condition) || !string.IsNullOrWhiteSpace(s.Indicator))
                .ToList();
            var skipped = listed.Where(s => string.IsNullOrWhiteSpace(s.Condition) && string.IsNullOrWhiteSpace(s.Indicator))
                .Select(s => s.Id).ToList();
            if (skipped.Count > 0)
                L($"[T9] 跳过不可求值策略（无 condition/indicator）：" + string.Join(", ", skipped));

            if (strategies.Count == 0)
                return new TaskRunResult(false, "", 0, "扫描名单为空或无可求值策略（请在「策略扫描」页从策略库加入）");
            L($"[T9] 策略 {strategies.Count} 条：" + string.Join(", ", strategies.Select(s => $"{s.Id}:{s.Name}")));

            // ── 股票池：东财 clist 分批全市场（可走磁盘缓存），剔 ST / 北交所 ──
            var spot = await _emSnap.GetMarketSnapshotAsync(L, ct).ConfigureAwait(false);
            if (spot.Count == 0)
                return new TaskRunResult(false, "", 0, "东财全市场快照为空，无法建股票池");
            var nameByCode = spot.ToDictionary(kv => kv.Key, kv => kv.Value.Name ?? "", StringComparer.Ordinal);
            var universe = spot.Values
                .Where(r => IsMainBoard(r.Code) && !IsSt(r.Name))
                .Select(r => r.Code)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
            var totalPool = universe.Count;
            if (MaxScan > 0 && universe.Count > MaxScan)
            {
                universe = universe.Take(MaxScan).ToList();
                L($"[T9] 股票池 {totalPool} → 截断 MaxScan={MaxScan}");
            }
            else
                L($"[T9] 股票池 {universe.Count} 只（clist 全市场已剔 ST/北交所）");

            var barDateCount = new Dictionary<DateTime, int>(); // 各股最新K线日期分布：扫描交易日取众数（max 易被个别源的盘中K线带偏）
            var maxLock = new object();
            var hits = new ConcurrentBag<Hit>();
            var done = 0;
            var failK = 0;
            using var sem = new SemaphoreSlim(Math.Max(1, Concurrency));
            var tasks = universe.Select(async code =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    IReadOnlyList<DailyBar>? bars = null;
                    try { bars = await _klines.GetStockDailyFreshAsync(code).ConfigureAwait(false); }
                    catch { Interlocked.Increment(ref failK); return; }
                    if (bars is null || bars.Count < 60) { Interlocked.Increment(ref failK); return; }

                    var last = bars[^1];
                    lock (maxLock) { barDateCount[last.Date.Date] = barDateCount.GetValueOrDefault(last.Date.Date) + 1; }
                    var prevClose = bars.Count >= 2 ? bars[^2].Close : last.Close;
                    var changePct = prevClose > 0 ? (last.Close / prevClose - 1) * 100 : 0;
                    var ctx = new IndicatorContext(last.Close, changePct, bars, cost: null, quotePeTtm: null);
                    nameByCode.TryGetValue(code, out var spotName);

                    foreach (var s in strategies)
                    {
                        bool? hit;
                        try { hit = StrategyEngine.EvaluateStrategy(s, ctx); }
                        catch (StrategyConfigError) { continue; }
                        if (hit is not true) continue;

                        hits.Add(new Hit(
                            s.Id, s.Name, s.Priority, s.Action,
                            code, spotName ?? "",
                            last.Close, changePct, last.Date));
                    }
                }
                finally
                {
                    sem.Release();
                    var n = Interlocked.Increment(ref done);
                    if (n % 200 == 0 || n == universe.Count)
                        L($"[T9] 进度 {n}/{universe.Count} · 命中 {hits.Count} · K线失败 {failK}");
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);

            var all = hits.OrderBy(h => h.StrategyId, StringComparer.Ordinal)
                .ThenBy(h => h.Code, StringComparer.Ordinal)
                .ToList();
            L($"[T9] 完成：扫描 {done} · 命中 {all.Count} 条（策略×股票）· K线不足/失败 {failK}");

            Directory.CreateDirectory(_dataDir);
            WriteCsv(Path.Combine(_dataDir, "strategy_scan.csv"), all);
            foreach (var g in all.GroupBy(h => h.StrategyId))
                WriteCsv(Path.Combine(_dataDir, $"strategy_scan_{g.Key}.csv"), g.ToList());

            // ── 观察池：命中自动入池（最新K线须=扫描交易日），并刷新在池涨跌、到期归档 ──
            var poolMsg = "";
            string? poolMd = null;
            try
            {
                var pool = new ObservationPool(_dataDir, _klines);
                DateTime? scanDay = barDateCount.Count == 0 ? null : barDateCount.OrderByDescending(kv => kv.Value).First().Key;
                L("[T9] 最新K线日期分布：" + string.Join("，", barDateCount.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key:yyyy-MM-dd}×{kv.Value}")) + $" → 扫描交易日 {scanDay:yyyy-MM-dd}");
                var ing = await pool.IngestAsync(all.Select(h => new ObservationHit(h.Code, h.Name, h.StrategyId, h.StrategyName, h.Price, h.BarDate)).ToList(), scanDay, L, ct, strategies.Select(s => s.Id).ToList()).ConfigureAwait(false);
                var rf = await pool.RefreshAsync(L, ct).ConfigureAwait(false);
                poolMsg = $" / 观察池新增 {ing.Added}、在池 {rf.Active}";
                poolMd = pool.BuildMarkdown(ing, rf);
            }
            catch (Exception pex) { L($"[T9] 观察池更新失败（不影响扫描结果）: {pex.Message}"); }
            var sections = BuildSections(strategies, universe.Count, totalPool, all, failK);
            if (poolMd is not null) sections.Add(("观察池（30天）", poolMd));
            var report = ReportBuilder.WriteReport(_dataDir, "T9",
                $"T9 策略全市场扫描（{today:yyyy-MM-dd}）", sections);
            ConfigGovernance.CommitSuccessfulRun(report+".config.json",System.Text.Json.JsonSerializer.SerializeToNode(strategies)!,new{KlineDates=barDateCount,QuoteAsOf="not retained by this consumer; no invented timestamp"},"T9/S26");
            return new TaskRunResult(true, report, all.Count,
                $"命中 {all.Count} 条 / 扫描 {universe.Count} 只 / 策略 {strategies.Count} 条" + poolMsg);
        }
        catch (Exception ex)
        {
            L($"[T9] 失败: {ex.Message}");
            return new TaskRunResult(false, "", 0, ex.Message);
        }
    }

    private static bool IsSt(string name)
        => name.Contains("ST", StringComparison.OrdinalIgnoreCase);

    private static bool IsMainBoard(string code)
    {
        if (code.Length != 6 || !code.All(char.IsDigit)) return false;
        return code[0] is '0' or '3' or '6';
    }

    private sealed record Hit(
        string StrategyId, string StrategyName, string Priority, string Action,
        string Code, string Name,
        double Price, double ChangePct, DateTime BarDate);

    private static readonly string[] CsvCols =
    [
        "strategy_id", "strategy_name", "priority", "code", "name",
        "price", "change_pct", "action",
    ];

    private static void WriteCsv(string path, List<Hit> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvCols));
        foreach (var h in rows)
        {
            string Esc(string s) => s.Contains(',') || s.Contains('"')
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            sb.Append(Esc(h.StrategyId)).Append(',')
              .Append(Esc(h.StrategyName)).Append(',')
              .Append(Esc(h.Priority)).Append(',')
              .Append(Esc(h.Code)).Append(',')
              .Append(Esc(h.Name)).Append(',')
              .Append(h.Price.ToString("0.####", Inv)).Append(',')
              .Append(h.ChangePct.ToString("0.##", Inv)).Append(',')
              .Append(Esc(h.Action))
              .AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static List<(string Title, string Body)> BuildSections(
        List<Strategy> strategies, int scanned, int pool,
        List<Hit> all, int failK)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"- 股票池：东财 clist 全市场剔 ST/北交所后 {pool}，本轮扫描 {scanned}，K线失败/不足 {failK}");
        summary.AppendLine($"- 策略数：{strategies.Count}（来自 strategy_scan_ids 扫描名单）");
        summary.AppendLine($"- 命中：{all.Count} 条（同一股票可命中多策略，分列在各子表）");
        summary.AppendLine("- 产出：`strategy_scan.csv` + `strategy_scan_{ID}.csv`（仅策略命中，不含业绩同比环比）");
        summary.AppendLine("- 列表缓存：`data/cache/em_clist_a_share.json`（默认 7 天）");

        var sections = new List<(string, string)> { ("概览", summary.ToString()) };
        foreach (var s in strategies)
        {
            var rows = all.Where(h => h.StrategyId == s.Id).ToList();
            var body = new StringBuilder();
            body.AppendLine($"策略 **{s.Id} {s.Name}** · 优先级 {s.Priority} · 命中 **{rows.Count}** 只");
            body.AppendLine();
            body.AppendLine(s.Action);
            body.AppendLine();
            if (rows.Count == 0)
            {
                body.AppendLine("_本轮无命中_");
            }
            else
            {
                body.AppendLine("| 代码 | 名称 | 现价 | 涨跌% |");
                body.AppendLine("|---|---|---:|---:|");
                foreach (var h in rows.Take(80))
                {
                    body.Append("| ").Append(h.Code)
                        .Append(" | ").Append(h.Name)
                        .Append(" | ").Append(h.Price.ToString("0.##", Inv))
                        .Append(" | ").Append(h.ChangePct.ToString("0.#", Inv))
                        .AppendLine(" |");
                }
                if (rows.Count > 80)
                    body.AppendLine($"_… 另有 {rows.Count - 80} 只见 `strategy_scan_{s.Id}.csv`_");
            }
            sections.Add(($"策略 {s.Id} · {s.Name}", body.ToString()));
        }
        return sections;
    }
}