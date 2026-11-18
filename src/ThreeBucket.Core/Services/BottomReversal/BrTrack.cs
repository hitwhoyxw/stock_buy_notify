using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>
/// S27（T10）信号实盘跟踪：仿 T9 观察池的“30 天跟踪”，对 signal_history.csv 里每个 B1/B2/B3 信号（按 代码+信号日+信号类型 去重）
/// 自信号日起跟踪 30 个交易日（交易日历 = 中证全指 1.000985，与 S27 扫描相同）。每次收盘后由 T10 扫描调用，也可单独运行 ThreeBucket.Cli --br track。
/// 价格口径：信号表里的收盘/计划价/止损都是“信号日当时”的价格（S27 用前复权，信号日当天前复权 = 不复权）。跟踪时同时取不复权（fqt=0）与前复权（fqt=1）日K：
/// 显示用不复权收盘；收益/最高/最低/止损判断用“除权调整到信号日口径”的价格 = 前复权价 × (信号日不复权收盘 ÷ 信号日前复权收盘)，
/// 这样信号后发生分红送转时收益不会被除权缺口扭曲；期间没有除权时与不复权价格完全相同。
/// 输出 data/bottom_reversal/tracking/：br_track_latest.csv、br_track_&lt;日期&gt;.csv、br_track_summary_latest.md、br_track_summary_&lt;日期&gt;.md。
/// 只读 signal_history.csv / history/trade_*.csv，不改 S27 其他输出，不碰观察池。
/// </summary>
public static class BrTrack
{
    public const int TrackDays = 30;
    public static readonly int[] Checkpoints = [5, 10, 20];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] Cols =
    [
        "key", "code", "name", "signal_date", "signal", "group", "stage", "total", "op_status", "exec_status", "exec_source",
        "signal_close", "entry", "stop", "platform", "plan_price", "plan_basis",
        "as_of", "days", "cur_close_raw", "cur_close_adj", "ret_signal", "ret_plan", "max_gain", "max_drawdown", "max_gain_date", "max_dd_date",
        "stop_hit", "stop_date", "ret_d5", "ret_d10", "ret_d20", "status", "end_date", "adj_factor", "note",
    ];

    public sealed class Result
    {
        public string AsOf = ""; public int Total, Tracking, Expired, StopHit, Failed, Frozen;
        public string LatestPath = "", SummaryPath = "";
        public string Summary => $"跟踪 {Total} 个信号（截至 {AsOf}）：跟踪中 {Tracking} / 已到期 {Expired} / 触及止损 {StopHit}" + (Failed > 0 ? $" / 数据缺失 {Failed}" : "");
    }

    public static async Task<Result> RunAsync(BrConfig cfg, string dataDir, Action<string> log, CancellationToken ct = default)
    {
        var outDir = Path.Combine(dataDir, "bottom_reversal");
        var trDir = Path.Combine(outDir, "tracking"); Directory.CreateDirectory(trDir);
        var hist = BrReport.ReadCsv(Path.Combine(outDir, "signal_history.csv"));
        // 去重：代码 + 信号日 + 信号类型
        var sigs = new List<Dictionary<string, string>>(); var seen = new HashSet<string>();
        foreach (var h in hist)
        {
            var type = G(h, "signal");
            if (type is not ("B1" or "B2" or "B3")) continue;
            var key = $"{G(h, "code").PadLeft(6, '0')}|{G(h, "date")}|{type}";
            if (seen.Add(key)) sigs.Add(h);
        }
        log($"[S27跟踪] signal_history.csv {hist.Count} 行 → 去重后 B1/B2/B3 信号 {sigs.Count} 个");

        // 交易日历（与 S27 扫描相同的指数；盘中运行丢弃今日未完成K线）
        var calK = await BrMarketData.FetchKlineAsync(cfg.S("data.calendar_index_secid", "1.000985"), 200, 0, 3, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("交易日历（指数日K）获取失败");
        var cal = BrScan.DropIncomplete(calK.S, cfg).Date.ToList();
        var asOf = cal[^1];
        var calIx = cal.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);

        // 已冻结（满 30 个交易日）的行沿用上次结果，不再取数
        var prev = BrReport.ReadCsv(Path.Combine(trDir, "br_track_latest.csv")).Where(r => G(r, "key").Length > 0).ToDictionary(r => G(r, "key"));
        var trades = LoadTradeHistory(outDir);

        var rows = new ConcurrentDictionary<string, Dictionary<string, string>>();
        var res = new Result { AsOf = asOf };
        int failed = 0, frozen = 0;
        using var sem = new SemaphoreSlim(Math.Max(1, cfg.I("data.concurrency", 8)));
        var byCode = sigs.GroupBy(s => G(s, "code").PadLeft(6, '0')).ToList();
        var tasks = byCode.Select(async g =>
        {
            var need = new List<Dictionary<string, string>>();
            foreach (var s in g)
            {
                var key = $"{g.Key}|{G(s, "date")}|{G(s, "signal")}";
                if (prev.TryGetValue(key, out var p) && int.TryParse(G(p, "days"), out var dd) && dd >= TrackDays && G(p, "status") != "数据缺失") { rows[key] = p; Interlocked.Increment(ref frozen); }
                else need.Add(s);
            }
            if (need.Count == 0) return;
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var first = need.Min(s => G(s, "date"))!;
                var lmt = (calIx.TryGetValue(first, out var fi) ? cal.Count - fi : 150) + 15;
                var sid = BrMarketData.Secid(g.Key);
                var raw = await BrMarketData.FetchKlineAsync(sid, lmt, 0, 3, ct).ConfigureAwait(false);
                var qfq = await BrMarketData.FetchKlineAsync(sid, lmt, 1, 3, ct).ConfigureAwait(false);
                foreach (var s in need)
                {
                    var key = $"{g.Key}|{G(s, "date")}|{G(s, "signal")}";
                    var r = Track(s, key, raw is null ? null : BrScan.DropIncomplete(raw.S, cfg), qfq is null ? null : BrScan.DropIncomplete(qfq.S, cfg), cal, calIx, asOf, trades, cfg);
                    if (G(r, "status") == "数据缺失") Interlocked.Increment(ref failed);
                    rows[key] = r;
                }
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var list = rows.Values.OrderByDescending(r => G(r, "signal_date"), StringComparer.Ordinal).ThenBy(r => G(r, "signal"), StringComparer.Ordinal).ThenBy(r => G(r, "code"), StringComparer.Ordinal).ToList();
        res.Total = list.Count; res.Failed = failed; res.Frozen = frozen;
        res.Tracking = list.Count(r => G(r, "status") == "跟踪中"); res.Expired = list.Count(r => G(r, "status") == "已到期"); res.StopHit = list.Count(r => G(r, "status") == "触及止损");
        var ro = list.Cast<IReadOnlyDictionary<string, string>>().ToList();
        res.LatestPath = Path.Combine(trDir, "br_track_latest.csv");
        BrReport.WriteCsv(res.LatestPath, ro, Cols);
        BrReport.WriteCsv(Path.Combine(trDir, $"br_track_{asOf}.csv"), ro, Cols);
        var md = SummaryMd(list, asOf, sigs.Count, hist.Count, frozen);
        res.SummaryPath = Path.Combine(trDir, "br_track_summary_latest.md");
        File.WriteAllText(res.SummaryPath, md, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(trDir, $"br_track_summary_{asOf}.md"), md, new UTF8Encoding(false));
        log($"[S27跟踪] {res.Summary}；沿用已到期 {frozen} 个；输出 {trDir}");
        return res;
    }

    /// <summary>信号日之后各次扫描的 T+1 检查结果（history/trade_*.csv 中 recent_signal = 类型@信号日 的 recent_exec），取最早一次。</summary>
    private static Dictionary<string, (string Exec, string Grade)> LoadTradeHistory(string outDir)
    {
        var d = new Dictionary<string, (string, string)>();
        var dir = Path.Combine(outDir, "history");
        if (!Directory.Exists(dir)) return d;
        foreach (var f in Directory.GetFiles(dir, "trade_*.csv").OrderBy(x => x, StringComparer.Ordinal))
            foreach (var r in BrReport.ReadCsv(f))
            {
                var rs = G(r, "recent_signal"); if (rs.Length == 0 || !rs.Contains('@')) continue;
                var p = rs.Split('@'); var key = $"{G(r, "code").PadLeft(6, '0')}|{p[1]}|{p[0]}";
                if (!d.ContainsKey(key)) d[key] = (G(r, "recent_exec"), G(r, "b1_grade"));
            }
        return d;
    }

    private static Dictionary<string, string> Track(Dictionary<string, string> s, string key, BrSeries? raw, BrSeries? qfq, List<string> cal, Dictionary<string, int> calIx,
        string asOf, Dictionary<string, (string Exec, string Grade)> trades, BrConfig cfg)
    {
        var sd = G(s, "date"); var type = G(s, "signal");
        var r = new Dictionary<string, string>
        {
            ["key"] = key, ["code"] = key[..6], ["name"] = G(s, "name"), ["signal_date"] = sd, ["signal"] = type, ["group"] = G(s, "group"), ["stage"] = G(s, "stage"),
            ["total"] = G(s, "total"), ["op_status"] = G(s, "op_status"), ["signal_close"] = G(s, "close"), ["entry"] = G(s, "entry"), ["stop"] = G(s, "stop"), ["platform"] = G(s, "platform"),
            ["as_of"] = asOf,
        };
        var sigClose = D(G(s, "close")); var stop = D(G(s, "stop")); var entry = D(G(s, "entry")); var platform = D(G(s, "platform"));
        if (raw is null || qfq is null || raw.Count == 0) { r["status"] = "数据缺失"; r["note"] = "日K获取失败"; return r; }
        var rIx = Ix(raw); var qIx = Ix(qfq);
        if (!rIx.TryGetValue(sd, out var k) || !qIx.TryGetValue(sd, out var kq)) { r["status"] = "数据缺失"; r["note"] = "取不到信号日K线"; return r; }
        if (double.IsNaN(sigClose)) sigClose = raw.Close[k];
        var notes = new List<string>();
        if (Math.Abs(raw.Close[k] / sigClose - 1) > 0.002) notes.Add($"信号表收盘 {F(sigClose, 3)} 与当前不复权信号日收盘 {F(raw.Close[k], 3)} 不一致，按信号表计算");
        var f = raw.Close[k] / qfq.Close[kq];   // 前复权 → 信号日口径
        r["adj_factor"] = F(f, 6);

        // 窗口：信号日之后第 1..30 个交易日（按交易日历）
        if (!calIx.TryGetValue(sd, out var ci)) { r["status"] = "数据缺失"; r["note"] = "信号日不在交易日历内"; return r; }
        var endIx = Math.Min(ci + TrackDays, cal.Count - 1);
        var days = endIx - ci;
        r["days"] = days.ToString(Inv);
        r["end_date"] = ci + TrackDays < cal.Count ? cal[ci + TrackDays] : "";
        var endDate = cal[endIx];
        var path = new List<int>();   // 前复权序列中信号日之后、窗口内的K线
        for (var j = kq + 1; j < qfq.Count && string.CompareOrdinal(qfq.Date[j], endDate) <= 0; j++) path.Add(j);
        double Adj(double[] a, int j) => a[j] * f;
        var sus = days - path.Count; if (sus > 0) notes.Add($"窗口内停牌/无K线 {sus} 天");

        // T+1 检查：优先用 S27 扫描自己的结果（history/trade_*.csv 的 recent_exec），否则用简化规则
        string exec, src; double plan = double.NaN; string planBasis;
        if (trades.TryGetValue(key, out var t) && t.Exec.Length > 0 && !t.Exec.StartsWith("T+1未发生"))
        {
            exec = t.Exec.Split('（')[0]; src = "S27扫描T+1检查" + (t.Grade.Length > 0 ? $"（{t.Grade}）" : "");
            if (exec.StartsWith("可介入") && path.Count > 0) plan = Adj(qfq.Open, path[0]);
        }
        else if (path.Count == 0) { exec = "T+1未发生"; src = ""; }
        else
        {
            var e = path[0]; var o = Adj(qfq.Open, e); var gap = o / sigClose - 1;
            var hot = G(s, "risk_tags").Split('；').Any(BrTradeEngine.IsHotTag);
            if (qfq.Date[e] != cal[ci + 1]) exec = "取消：停牌";
            else if (gap > cfg.D("trade.exec.max_gap_up", 0.03)) exec = "取消：T+1高开超阈值不追";
            else if (type == "B1" && o < sigClose && !double.IsNaN(platform) && o < platform) exec = "取消：低开跌回突破平台下方";
            else if (cfg.B("trade.exec.skip_hot", true) && hot) exec = "取消：信号日已过热不追涨";
            else if (!double.IsNaN(stop) && o <= stop) exec = "取消：开盘已低于止损";
            else { exec = "可介入(模拟)"; plan = o; }
            src = "跟踪表简化规则（高开>3%/低开破平台/过热/开盘破止损；未重算R/R）";
        }
        if (!double.IsNaN(plan)) planBasis = "T+1开盘(模拟计划价)";
        else { plan = entry; planBasis = "信号日计划价(未介入，仅参考)"; }
        r["exec_status"] = exec; r["exec_source"] = src; r["plan_price"] = F(plan, 3); r["plan_basis"] = planBasis;

        if (path.Count == 0)
        {
            r["cur_close_raw"] = F(raw.Close[k], 3); r["cur_close_adj"] = F(sigClose, 3);
            r["status"] = "跟踪中"; r["stop_hit"] = "否"; notes.Add("信号日之后尚无K线"); r["note"] = string.Join("；", notes);
            return r;
        }
        var last = path[^1];
        var lastRaw = rIx.TryGetValue(qfq.Date[last], out var lr) ? raw.Close[lr] : double.NaN;
        var cur = Adj(qfq.Close, last);
        r["cur_close_raw"] = F(lastRaw, 3); r["cur_close_adj"] = F(cur, 3);
        r["ret_signal"] = F(cur / sigClose - 1, 4);
        if (!double.IsNaN(plan) && plan > 0) r["ret_plan"] = F(cur / plan - 1, 4);
        double hi = double.MinValue, lo = double.MaxValue; string hiD = "", loD = "", stopD = "";
        foreach (var j in path)
        {
            var h = Adj(qfq.High, j); var l = Adj(qfq.Low, j);
            if (h > hi) { hi = h; hiD = qfq.Date[j]; }
            if (l < lo) { lo = l; loD = qfq.Date[j]; }
            if (stopD.Length == 0 && !double.IsNaN(stop) && l <= stop) stopD = qfq.Date[j];
        }
        r["max_gain"] = F(Math.Max(0, hi / sigClose - 1), 4); r["max_gain_date"] = hi / sigClose - 1 > 0 ? hiD : "";
        r["max_drawdown"] = F(Math.Min(0, lo / sigClose - 1), 4); r["max_dd_date"] = lo / sigClose - 1 < 0 ? loD : "";
        r["stop_hit"] = stopD.Length > 0 ? "是" : "否"; r["stop_date"] = stopD;
        foreach (var n in Checkpoints)
        {
            if (ci + n >= cal.Count) continue;
            var dN = cal[ci + n]; var jj = -1;
            foreach (var j in path) if (string.CompareOrdinal(qfq.Date[j], dN) <= 0) jj = j;
            r[$"ret_d{n}"] = jj >= 0 ? F(Adj(qfq.Close, jj) / sigClose - 1, 4) : F(0, 4);
        }
        r["status"] = stopD.Length > 0 ? "触及止损" : days >= TrackDays ? "已到期" : "跟踪中";
        if (Math.Abs(f - 1) > 1e-6) notes.Add($"信号后有除权，收益按前复权×{F(f, 4)} 调整到信号日口径");
        r["note"] = string.Join("；", notes);
        return r;
    }

    private static string SummaryMd(List<Dictionary<string, string>> list, string asOf, int nSig, int nHist, int frozen)
    {
        var L = new List<string>
        {
            $"# S27 底部反转 · 信号跟踪表现（截至 {asOf}）", "",
            $"> 来源 signal_history.csv {nHist} 行，按 代码+信号日+信号类型 去重后 {nSig} 个 B1/B2/B3 信号；每个信号自信号日起跟踪 {TrackDays} 个交易日（交易日历 = 中证全指）。沿用已到期结果 {frozen} 个。",
            "> 收益 = 当前（或第 N 日）收盘 ÷ 信号日收盘 − 1；“计划价收益”以 T+1 开盘（可介入时）或信号日计划价为基准。价格为不复权、信号后如有除权则按前复权比例调整到信号日口径。不计成本，不是真实成交。",
            "> 胜率 = 当前收益 >0 的比例；最大涨幅/最大回撤 = 窗口内最高价/最低价相对信号日收盘；触及止损 = 窗口内最低价 ≤ 信号日止损价（跟踪不因止损停止，状态记为“触及止损”）。", "",
        };
        void Table(string title, Func<Dictionary<string, string>, string> by, IEnumerable<string>? order = null)
        {
            L.Add($"## {title}"); L.Add("");
            L.Add("| 分组 | 信号数 | 跟踪中 / 到期 / 止损 | 胜率（赚/有收益样本） | 平均收益 | 中位收益 | 平均计划价收益 | 平均最大涨幅 | 平均最大回撤 | 第5日 均值(样本) | 第10日 均值(样本) | 第20日 均值(样本) |");
            L.Add("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            var groups = list.GroupBy(by).ToDictionary(g => g.Key, g => g.ToList());
            var keys = (order ?? groups.Keys.OrderBy(x => x, StringComparer.Ordinal)).Where(groups.ContainsKey).ToList();
            foreach (var kname in keys.Append("合计"))
            {
                var g = kname == "合计" ? list : groups[kname];
                var rs = g.Select(x => D(G(x, "ret_signal"))).Where(v => !double.IsNaN(v)).ToList();
                string Avg(string c) { var v = g.Select(x => D(G(x, c))).Where(x => !double.IsNaN(x)).ToList(); return v.Count == 0 ? "-" : $"{P(v.Average())}" + (c.StartsWith("ret_d") ? $"({v.Count})" : ""); }
                L.Add($"| {kname} | {g.Count} | {g.Count(x => G(x, "status") == "跟踪中")} / {g.Count(x => G(x, "status") == "已到期")} / {g.Count(x => G(x, "status") == "触及止损")} | " +
                      $"{(rs.Count == 0 ? "-" : (rs.Count(v => v > 0) * 100.0 / rs.Count).ToString("0.0", Inv) + $"%（{rs.Count(v => v > 0)}/{rs.Count}）")} | {(rs.Count == 0 ? "-" : P(rs.Average()))} | {(rs.Count == 0 ? "-" : P(Median(rs)))} | {Avg("ret_plan")} | {Avg("max_gain")} | {Avg("max_drawdown")} | {Avg("ret_d5")} | {Avg("ret_d10")} | {Avg("ret_d20")} |");
            }
            L.Add("");
        }
        Table("按信号类型", x => G(x, "signal"), ["B1", "B2", "B3"]);
        Table("按 A/B/C 组（信号日分组；空 = 未分组）", x => G(x, "group") is { Length: > 0 } g ? g + "组" : "未分组", ["A组", "B组", "C组", "未分组"]);
        Table("按信号日", x => G(x, "signal_date"));
        Table("按 T+1 检查（是否可介入）", x => G(x, "exec_status").StartsWith("可介入") ? "可介入(模拟)" : G(x, "exec_status").StartsWith("取消") ? "取消" : G(x, "exec_status"));
        L.Add("_明细：br_track_latest.csv（每次收盘更新）与 br_track_<日期>.csv 快照。数据仅供研究，不构成投资建议。_");
        return string.Join("\n", L) + "\n";
    }

    private static Dictionary<string, int> Ix(BrSeries s) { var d = new Dictionary<string, int>(); for (var i = 0; i < s.Count; i++) d[s.Date[i]] = i; return d; }
    private static string G(IReadOnlyDictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v ?? "" : "";
    private static double D(string s) => double.TryParse(s.Trim().TrimEnd('%'), NumberStyles.Float, Inv, out var v) ? v : double.NaN;
    private static string F(double v, int nd) => double.IsNaN(v) || double.IsInfinity(v) ? "" : Math.Round(v, nd).ToString("0." + new string('0', nd), Inv);
    private static string P(double v) => (v >= 0 ? "+" : "") + (v * 100).ToString("0.00", Inv) + "%";
    private static double Median(List<double> v) { var a = v.OrderBy(x => x).ToList(); return a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2; }
}
