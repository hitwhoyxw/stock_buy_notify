using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>入池候选：来自 T9 命中（BarDate 已知）或历史 strategy_scan.csv 回灌（BarDate 未知，入池时现拉日K校验）。</summary>
public sealed record ObservationHit(
    string Code, string Name, string StrategyId, string StrategyName,
    double Price, DateTime? BarDate);

/// <summary>观察池一条记录（data/observation_pool.json；归档后写 observation_pool_archive.csv）。</summary>
public sealed class ObservationEntry
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>命中过的策略 ID（去重合并）。</summary>
    public List<string> StrategyIds { get; set; } = new();
    public List<string> StrategyNames { get; set; } = new();
    /// <summary>首次入选交易日 yyyy-MM-dd。</summary>
    public string EntryDate { get; set; } = "";
    /// <summary>入选价：入选当天扫描价（T9 14:30 扫描即盘中价；CSV 回灌取当日收盘）。再次命中不重置。</summary>
    public double EntryPrice { get; set; }
    /// <summary>计算涨跌的基准价：当前前复权序列中入选日收盘（除权后自动随复权调整，避免分红误算成下跌）。</summary>
    public double BasePrice { get; set; }
    /// <summary>到期日 = 入选日 + 30 个自然日；过了到期日自动归档。</summary>
    public string ExpireDate { get; set; } = "";
    public string LastHitDate { get; set; } = "";
    public int HitCount { get; set; } = 1;

    public double LastPrice { get; set; }
    /// <summary>最新K线日期（≠市场最新交易日即视为停牌/行情未更新）。</summary>
    public string LastBarDate { get; set; } = "";
    /// <summary>入选以来涨跌幅%（最新收盘 / 基准价 - 1）。</summary>
    public double ReturnPct { get; set; }
    /// <summary>期间最高涨幅%（入选后各日最高价相对基准价的最大值，≥0）。</summary>
    public double MaxGainPct { get; set; }
    /// <summary>期间最大浮亏%（入选后各日最低价相对基准价的最小值，≤0）。</summary>
    public double MinReturnPct { get; set; }
    /// <summary>最大回撤%（入选后收盘价相对此前峰值的最大跌幅，峰值起点=基准价，≤0）。</summary>
    public double MaxDrawdownPct { get; set; }
    /// <summary>持有天数：入选日之后的交易日根数。</summary>
    public int HoldDays { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Note { get; set; } = "";

    // 归档字段（仅归档记录有值）
    public string ExitDate { get; set; } = "";
    public string ExitReason { get; set; } = "";

    public int RemainingDays(DateTime today)
        => DateTime.TryParseExact(ExpireDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? (d.Date - today.Date).Days : 0;
}

public sealed record PoolIngestResult(int Added, int Updated, int Skipped, List<string> SkipReasons, DateTime ScanDate);
public sealed record PoolRefreshResult(int Active, int Refreshed, int Stale, int Failed, int Archived, DateTime ReferenceDate);

/// <summary>
/// 有时效的观察池：T9 每次扫描命中自动入池，观察 30 个自然日，跟踪入选后涨跌幅 / 最高涨幅 / 最大回撤，
/// 到期自动归档（observation_pool_archive.csv，保留历史，便于统计策略命中后 30 天表现）。
/// 防脏数据：剔 ST / *ST / 退市整理（名称含「退」）/ 北交所 / 非沪深主板代码；
/// 入池要求该股最新K线日期 = 扫描交易日（旧行情 / 已退市 / 停牌股不入池）。
/// </summary>
public sealed class ObservationPool
{
    public const int ObserveDays = 30;
    public const string PoolFile = "observation_pool.json";
    public const string PoolCsvFile = "observation_pool.csv";
    public const string ArchiveFile = "observation_pool_archive.csv";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly SemaphoreSlim Gate = new(1, 1); // 进程内串行化（T9 定时 / UI 手动刷新 / 移除）
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dataDir;
    private readonly KlineService _klines;
    public int Concurrency { get; set; } = 8;

    public ObservationPool(string dataDir, KlineService klines)
    {
        _dataDir = dataDir;
        _klines = klines;
    }

    public string PoolPath => Path.Combine(_dataDir, PoolFile);
    public string ArchivePath => Path.Combine(_dataDir, ArchiveFile);

    private static string D(DateTime d) => d.ToString("yyyy-MM-dd", Inv);
    private static DateTime ParseD(string s)
        => DateTime.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;

    // ───────────────────────── 读写 ─────────────────────────

    public List<ObservationEntry> Load()
    {
        try
        {
            if (!File.Exists(PoolPath)) return new();
            return JsonSerializer.Deserialize<List<ObservationEntry>>(File.ReadAllText(PoolPath, Encoding.UTF8), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    private void Save(List<ObservationEntry> list)
    {
        Directory.CreateDirectory(_dataDir);
        var ordered = list.OrderBy(e => e.EntryDate, StringComparer.Ordinal).ThenBy(e => e.Code, StringComparer.Ordinal).ToList();
        AtomicWrite(PoolPath, JsonSerializer.Serialize(ordered, JsonOpts));
        AtomicWrite(Path.Combine(_dataDir, PoolCsvFile), ToCsv(ordered, archive: false));
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(true)); // 带 BOM：Excel 直接打开不乱码
        File.Move(tmp, path, overwrite: true);
    }

    private static readonly string[] Cols =
    [
        "code", "name", "strategy_ids", "strategy_names", "entry_date", "entry_price", "base_price",
        "expire_date", "last_hit_date", "hit_count", "last_price", "last_bar_date", "return_pct",
        "max_gain_pct", "min_return_pct", "max_drawdown_pct", "hold_days", "updated_at", "note",
        "exit_date", "exit_reason",
    ];

    private static string Esc(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n')
        ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string Row(ObservationEntry e) => string.Join(",", new[]
    {
        e.Code, Esc(e.Name), Esc(string.Join("|", e.StrategyIds)), Esc(string.Join("|", e.StrategyNames)),
        e.EntryDate, e.EntryPrice.ToString("0.####", Inv), e.BasePrice.ToString("0.####", Inv),
        e.ExpireDate, e.LastHitDate, e.HitCount.ToString(Inv), e.LastPrice.ToString("0.####", Inv), e.LastBarDate,
        e.ReturnPct.ToString("0.##", Inv), e.MaxGainPct.ToString("0.##", Inv), e.MinReturnPct.ToString("0.##", Inv),
        e.MaxDrawdownPct.ToString("0.##", Inv), e.HoldDays.ToString(Inv), Esc(e.UpdatedAt), Esc(e.Note),
        e.ExitDate, Esc(e.ExitReason),
    });

    private static string ToCsv(IEnumerable<ObservationEntry> rows, bool archive)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Cols));
        foreach (var e in rows) sb.AppendLine(Row(e));
        return sb.ToString();
    }

    private void AppendArchive(IEnumerable<ObservationEntry> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var exists = File.Exists(ArchivePath);
        var sb = new StringBuilder();
        if (!exists) sb.AppendLine(string.Join(",", Cols));
        foreach (var e in list) sb.AppendLine(Row(e));
        File.AppendAllText(ArchivePath, sb.ToString(), exists ? new UTF8Encoding(false) : new UTF8Encoding(true));
    }

    public List<ObservationEntry> LoadArchive()
    {
        var res = new List<ObservationEntry>();
        if (!File.Exists(ArchivePath)) return res;
        var lines = File.ReadAllLines(ArchivePath, Encoding.UTF8);
        if (lines.Length < 2) return res;
        var header = ParseCsvLine(lines[0]).Select(h => h.Trim().TrimStart('\uFEFF')).ToList();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = ParseCsvLine(line);
            string G(string k) { var i = header.IndexOf(k); return i >= 0 && i < f.Count ? f[i] : ""; }
            double N(string k) => double.TryParse(G(k), NumberStyles.Any, Inv, out var v) ? v : 0;
            res.Add(new ObservationEntry
            {
                Code = G("code"), Name = G("name"),
                StrategyIds = G("strategy_ids").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList(),
                StrategyNames = G("strategy_names").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList(),
                EntryDate = G("entry_date"), EntryPrice = N("entry_price"), BasePrice = N("base_price"),
                ExpireDate = G("expire_date"), LastHitDate = G("last_hit_date"), HitCount = (int)N("hit_count"),
                LastPrice = N("last_price"), LastBarDate = G("last_bar_date"), ReturnPct = N("return_pct"),
                MaxGainPct = N("max_gain_pct"), MinReturnPct = N("min_return_pct"), MaxDrawdownPct = N("max_drawdown_pct"),
                HoldDays = (int)N("hold_days"), UpdatedAt = G("updated_at"), Note = G("note"),
                ExitDate = G("exit_date"), ExitReason = G("exit_reason"),
            });
        }
        return res;
    }

    public static List<string> ParseCsvLine(string line)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res;
    }

    /// <summary>读 strategy_scan.csv（T9 产出格式）为入池候选（BarDate 未知，入池时现拉日K校验）。</summary>
    public static List<ObservationHit> ReadScanCsv(string path)
    {
        var res = new List<ObservationHit>();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length < 2) return res;
        var header = ParseCsvLine(lines[0]).Select(h => h.Trim().TrimStart('\uFEFF')).ToList();
        int Ix(string k) => header.IndexOf(k);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = ParseCsvLine(line);
            string G(string k) { var i = Ix(k); return i >= 0 && i < f.Count ? f[i].Trim() : ""; }
            var code = G("code");
            if (code.Length == 0) continue;
            res.Add(new ObservationHit(code.PadLeft(6, '0'), G("name"), G("strategy_id"), G("strategy_name"),
                double.TryParse(G("price"), NumberStyles.Any, Inv, out var p) ? p : 0, null));
        }
        return res;
    }

    // ───────────────────────── 过滤 ─────────────────────────

    /// <summary>不可入池原因（null=可入池）：非沪深主板/创业/科创、北交所、ST、退市整理。</summary>
    public static string? ExcludeReason(string code, string name)
    {
        if (code.Length != 6 || !code.All(char.IsDigit)) return "代码非法";
        if (code[0] is not ('0' or '3' or '6')) return "北交所/非A股";
        if (name.Contains("ST", StringComparison.OrdinalIgnoreCase)) return "ST";
        if (name.Contains('退')) return "退市整理";
        return null;
    }

    /// <summary>市场最新交易日：取几只权重股的最新日K日期最大值（强制刷新，不吃进程内旧缓存）。</summary>
    public async Task<DateTime?> GetReferenceDateAsync()
    {
        DateTime? best = null;
        foreach (var c in new[] { "600519", "000001", "300750", "601318" })
        {
            try
            {
                var bars = await _klines.GetStockDailyFreshAsync(c, 5).ConfigureAwait(false);
                if (bars is { Count: > 0 } && (best is null || bars[^1].Date.Date > best)) best = bars[^1].Date.Date;
            }
            catch { }
        }
        return best;
    }

    // ───────────────────────── 入池 ─────────────────────────

    /// <param name="scanDate">扫描交易日；null=市场最新交易日（GetReferenceDateAsync）。</param>
    public async Task<PoolIngestResult> IngestAsync(IReadOnlyList<ObservationHit> hits, DateTime? scanDate,
        Action<string>? log = null, CancellationToken ct = default, IReadOnlyCollection<string>? allowedStrategyIds = null)
    {
        void L(string m) => log?.Invoke(m);
        // 只收全市场扫描名单内的策略命中（null=调用方已保证；空名单=全部丢弃）
        if (allowedStrategyIds is not null)
        {
            var allow = new HashSet<string>(allowedStrategyIds, StringComparer.OrdinalIgnoreCase);
            var before = hits.Count;
            hits = hits.Where(h => allow.Contains(h.StrategyId)).ToList();
            if (before != hits.Count)
                L($"[观察池] 丢弃名单外策略命中 {before - hits.Count} 条（扫描名单：{(allow.Count == 0 ? "空" : string.Join(",", allow))}）");
        }
        var refDate = await GetReferenceDateAsync().ConfigureAwait(false);
        var sd = (scanDate ?? refDate ?? hits.Where(h => h.BarDate.HasValue).Select(h => h.BarDate!.Value.Date)
            .DefaultIfEmpty(TradingCalendar.NowCn().Date).Max()).Date;

        var skips = new ConcurrentBag<string>();
        // 同一股票多策略命中合并为一条候选
        var groups = hits.GroupBy(h => h.Code.Trim().PadLeft(6, '0'), StringComparer.Ordinal).ToList();
        var accepted = new ConcurrentBag<(string Code, string Name, List<(string Id, string Name)> Strats, double Price)>();

        using var sem = new SemaphoreSlim(Math.Max(1, Concurrency));
        await Task.WhenAll(groups.Select(async g =>
        {
            var code = g.Key;
            var name = g.Select(h => h.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
            var why = ExcludeReason(code, name);
            if (why is not null) { skips.Add($"{code} {name}: {why}"); return; }
            var strats = g.Select(h => (h.StrategyId, h.StrategyName)).Distinct().ToList();

            var known = g.FirstOrDefault(h => h.BarDate.HasValue);
            if (known is not null)
            {
                // T9 在线入池：命中时的最新K线必须就是扫描交易日
                if (known.BarDate!.Value.Date != sd)
                { skips.Add($"{code} {name}: 最新K线 {D(known.BarDate.Value)} ≠ 扫描日 {D(sd)}（旧行情/停牌/退市）"); return; }
                accepted.Add((code, name, strats, known.Price));
                return;
            }

            // 回灌：现拉日K，要求扫描日当天有K线，且该股仍在交易（最新K线 = 市场最新交易日）
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyList<DailyBar>? bars = null;
                try { bars = await _klines.GetStockDailyFreshAsync(code, 120).ConfigureAwait(false); } catch { }
                if (bars is not { Count: > 0 }) { skips.Add($"{code} {name}: 日K拉取失败"); return; }
                var bar = bars.FirstOrDefault(b => b.Date.Date == sd);
                if (bar is null) { skips.Add($"{code} {name}: 扫描日 {D(sd)} 无K线（最新 {D(bars[^1].Date)}，疑似停牌/退市）"); return; }
                if (refDate is not null && bars[^1].Date.Date < refDate.Value)
                { skips.Add($"{code} {name}: 最新K线 {D(bars[^1].Date)} < 市场最新 {D(refDate.Value)}（停牌/退市）"); return; }
                accepted.Add((code, name, strats, bar.Close));
            }
            finally { sem.Release(); }
        })).ConfigureAwait(false);

        int added = 0, updated = 0;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pool = Load();
            var byCode = pool.ToDictionary(e => e.Code, StringComparer.Ordinal);
            var now = TradingCalendar.NowCn();
            foreach (var a in accepted.OrderBy(x => x.Code, StringComparer.Ordinal))
            {
                if (byCode.TryGetValue(a.Code, out var e) && ParseD(e.ExpireDate) >= sd)
                {
                    // 有效期内再次命中：不重置入选价/入选日/到期日，只记命中
                    if (string.CompareOrdinal(D(sd), e.LastHitDate) > 0) { e.HitCount++; e.LastHitDate = D(sd); }
                    foreach (var (id, nm) in a.Strats)
                        if (!e.StrategyIds.Contains(id, StringComparer.OrdinalIgnoreCase)) { e.StrategyIds.Add(id); e.StrategyNames.Add(nm); }
                    if (!string.IsNullOrWhiteSpace(a.Name)) e.Name = a.Name;
                    updated++;
                }
                else
                {
                    if (e is not null) continue; // 已过期但尚未归档：由 Refresh 归档后下次命中再新入
                    var ne = new ObservationEntry
                    {
                        Code = a.Code, Name = a.Name,
                        StrategyIds = a.Strats.Select(s => s.Id).ToList(),
                        StrategyNames = a.Strats.Select(s => s.Name).ToList(),
                        EntryDate = D(sd), EntryPrice = Math.Round(a.Price, 4), BasePrice = Math.Round(a.Price, 4),
                        ExpireDate = D(sd.AddDays(ObserveDays)), LastHitDate = D(sd), HitCount = 1,
                        LastPrice = a.Price, LastBarDate = D(sd), UpdatedAt = now.ToString("yyyy-MM-dd HH:mm", Inv),
                    };
                    pool.Add(ne);
                    byCode[a.Code] = ne;
                    added++;
                }
            }
            Save(pool);
        }
        finally { Gate.Release(); }

        var skipList = skips.OrderBy(s => s, StringComparer.Ordinal).ToList();
        L($"[观察池] 入池 {D(sd)}：新增 {added} · 再次命中 {updated} · 跳过 {skipList.Count}");
        foreach (var s in skipList.Where(s => !s.Contains(": ST") && !s.Contains("北交所")).Take(30)) L($"[观察池]   跳过 {s}");
        return new PoolIngestResult(added, updated, skipList.Count, skipList, sd);
    }

    // ───────────────────────── 刷新 / 到期归档 ─────────────────────────

    public async Task<PoolRefreshResult> RefreshAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Invoke(m);
        var refDate = await GetReferenceDateAsync().ConfigureAwait(false);
        var snapshot = Load();
        var barsByCode = new ConcurrentDictionary<string, IReadOnlyList<DailyBar>>(StringComparer.Ordinal);
        int failed = 0;
        using (var sem = new SemaphoreSlim(Math.Max(1, Concurrency)))
        {
            await Task.WhenAll(snapshot.Select(e => e.Code).Distinct().Select(async code =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var bars = await _klines.GetStockDailyFreshAsync(code, 120).ConfigureAwait(false);
                    if (bars is { Count: > 0 }) barsByCode[code] = bars; else Interlocked.Increment(ref failed);
                }
                catch { Interlocked.Increment(ref failed); }
                finally { sem.Release(); }
            })).ConfigureAwait(false);
        }

        int refreshed = 0, stale = 0;
        var archived = new List<ObservationEntry>();
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pool = Load(); // 重新读：拉K线期间可能有入池/移除
            var today = TradingCalendar.NowCn();
            foreach (var e in pool)
            {
                if (barsByCode.TryGetValue(e.Code, out var bars))
                {
                    Compute(e, bars, refDate);
                    refreshed++;
                    if (e.Note.StartsWith("停牌", StringComparison.Ordinal)) stale++;
                }
                else e.Note = "行情拉取失败（沿用上次数据）";
                e.UpdatedAt = today.ToString("yyyy-MM-dd HH:mm", Inv);
                if (today.Date > ParseD(e.ExpireDate))
                {
                    e.ExitDate = D(today);
                    e.ExitReason = "到期";
                    archived.Add(e);
                }
            }
            if (archived.Count > 0)
            {
                AppendArchive(archived);
                pool.RemoveAll(archived.Contains);
            }
            Save(pool);
            L($"[观察池] 刷新：在池 {pool.Count} · 更新 {refreshed} · 停牌/未更新 {stale} · 失败 {failed} · 到期归档 {archived.Count}"
              + (refDate is null ? "" : $" · 行情日 {D(refDate.Value)}"));
            return new PoolRefreshResult(pool.Count, refreshed, stale, failed, archived.Count, refDate ?? today.Date);
        }
        finally { Gate.Release(); }
    }

    /// <summary>按入选日后的日K计算涨跌指标（纯函数，便于回归）。</summary>
    public static void Compute(ObservationEntry e, IReadOnlyList<DailyBar> bars, DateTime? refDate)
    {
        var entry = ParseD(e.EntryDate);
        var entryBar = bars.FirstOrDefault(b => b.Date.Date == entry);
        // 基准：当前前复权序列中入选日收盘（盘中扫描当天=实时价，收盘后即定盘）；缺失时用入选价
        var basePrice = entryBar is { Close: > 0 } ? entryBar.Close : (e.BasePrice > 0 ? e.BasePrice : e.EntryPrice);
        if (basePrice <= 0) return;
        e.BasePrice = Math.Round(basePrice, 4);

        var after = bars.Where(b => b.Date.Date > entry).OrderBy(b => b.Date).ToList();
        var last = bars[^1];
        e.LastPrice = last.Close;
        e.LastBarDate = D(last.Date);
        e.ReturnPct = Math.Round((last.Close / basePrice - 1) * 100, 2);
        e.HoldDays = after.Count;

        double maxGain = 0, minRet = 0, mdd = 0, peak = basePrice;
        foreach (var b in after)
        {
            if (b.High > 0) maxGain = Math.Max(maxGain, b.High / basePrice - 1);
            if (b.Low > 0) minRet = Math.Min(minRet, b.Low / basePrice - 1);
            peak = Math.Max(peak, b.Close);
            mdd = Math.Min(mdd, b.Close / peak - 1);
        }
        e.MaxGainPct = Math.Round(maxGain * 100, 2);
        e.MinReturnPct = Math.Round(minRet * 100, 2);
        e.MaxDrawdownPct = Math.Round(mdd * 100, 2);
        e.Note = refDate is not null && last.Date.Date < refDate.Value.Date
            ? $"停牌/行情未更新（最新K线 {D(last.Date)}）" : "";
    }

    /// <summary>手动移除：移出观察池并写入归档（exit_reason=手动移除）。</summary>
    public async Task<bool> RemoveAsync(string code, string reason = "手动移除")
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var pool = Load();
            var hit = pool.Where(e => e.Code == code).ToList();
            if (hit.Count == 0) return false;
            var today = TradingCalendar.NowCn();
            foreach (var e in hit) { e.ExitDate = D(today); e.ExitReason = reason; }
            AppendArchive(hit);
            pool.RemoveAll(hit.Contains);
            Save(pool);
            return true;
        }
        finally { Gate.Release(); }
    }

    /// <summary>T9 报告用的观察池小结（Markdown）。</summary>
    public string BuildMarkdown(PoolIngestResult? ing, PoolRefreshResult? rf)
    {
        var sb = new StringBuilder();
        if (ing is not null)
            sb.AppendLine($"- 本轮入池（扫描日 {D(ing.ScanDate)}）：新增 **{ing.Added}** · 再次命中 {ing.Updated} · 跳过 {ing.Skipped}（ST/北交所/退市/旧行情）");
        if (rf is not null)
            sb.AppendLine($"- 在池 **{rf.Active}** 只 · 停牌/未更新 {rf.Stale} · 到期归档 {rf.Archived}");
        sb.AppendLine($"- 文件：`{PoolFile}` / `{PoolCsvFile}` / 归档 `{ArchiveFile}`（观察期 {ObserveDays} 个自然日）");
        var pool = Load().OrderByDescending(e => e.ReturnPct).ToList();
        if (pool.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| 代码 | 名称 | 策略 | 入选日 | 入选价 | 现价 | 涨跌% | 最高% | 回撤% | 到期 |");
            sb.AppendLine("|---|---|---|---|---:|---:|---:|---:|---:|---|");
            foreach (var e in pool.Take(60))
                sb.AppendLine($"| {e.Code} | {e.Name} | {string.Join("/", e.StrategyIds)} | {e.EntryDate} | {e.EntryPrice:0.##} | {e.LastPrice:0.##} | {e.ReturnPct:+0.0;-0.0;0} | {e.MaxGainPct:0.0} | {e.MaxDrawdownPct:0.0} | {e.ExpireDate} |");
            if (pool.Count > 60) sb.AppendLine($"_… 另有 {pool.Count - 60} 只见 `{PoolCsvFile}`_");
        }
        return sb.ToString();
    }
}
