using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThreeBucket.Core.Services.BottomReversal;

namespace ThreeBucket.Core.Services.SlowRise;

/// <summary>
/// 每日扫描（T11）：全市场 → L0~L4 → 观察池（持久化）→ BUY → 模拟持仓（持久化，同回测规则）→ §12 信号表。
/// 状态：data/bottom_slowrise/state/state_&lt;日期&gt;.json（每个交易日一份），state.json = 最新；重跑同一天时从“早于扫描日的最近一份”状态重放，结果不会重复累加。
/// 第一次运行（无状态）从扫描日往前 bootstrap_days 个交易日起重放，建立观察池与模拟持仓。与现有 S26/S27 观察池（data/observation_pool.*）完全无关。
/// </summary>
public static class SrScan
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions Jo = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed class State
    {
        public string Strategy { get; set; } = "S28 底部反转缓升";
        public string Version { get; set; } = "";
        public string LastDate { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string Note { get; set; } = "";
        public Dictionary<string, SrPoolEntry> Pool { get; set; } = new();
        public SrBook Book { get; set; } = new();
    }

    public sealed class Output
    {
        public string Day = "", SignalsPath = "", ReportPath = "", OutDir = ""; public bool Intraday;
        public Dictionary<string, int> Counts = new(); public int Scanned, L0Pass;
    }

    public static readonly string[] Cols = ["date", "code", "name", "signal", "score", "dd", "rebound", "d0_date", "d0_vol_ratio", "ma_cross_date", "macd_cross_date", "er20", "r2_20", "sigma20",
        "entry_price", "stop_price", "reason", "fail_reason", "industry", "close", "pct", "l3_count", "pool_enter_date", "position_note", "key_watch"];

    public static async Task<Output> RunAsync(string dataDir, Action<string> log, IReadOnlyList<string>? codesArg = null, int limit = 0, string? outArg = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = SrParams.Load(dataDir);
        log($"[S28] 参数 {p.Cfg.Path}（version {p.Version}）");
        var outDir = outArg ?? Path.Combine(dataDir, "bottom_slowrise");
        var stDir = Path.Combine(outDir, "state"); Directory.CreateDirectory(stDir);
        var ctx = await SrLoad.CalendarAsync(p, 800, null, log, ct).ConfigureAwait(false);
        var day = ctx.Day;
        var uni = await SrLoad.UniverseAsync(p, dataDir, log, ct).ConfigureAwait(false);

        // 预过滤（L0 中只依赖列表的部分：板块、ST/退市名称、上市日期）
        List<string> codes;
        if (codesArg is { Count: > 0 }) codes = codesArg.ToList();
        else
        {
            var minList = DateTime.ParseExact(day, "yyyy-MM-dd", Inv).AddDays(-p.MinListDays * 1.4).ToString("yyyyMMdd");
            codes = uni.Values.Where(x => !p.ExcludeBoards.Contains(x.Board) && !(p.L0On[0] && (x.Name.Contains("ST", StringComparison.OrdinalIgnoreCase) || x.Name.Contains('退')))
                                       && (!p.L0On[1] || x.ListDate.Length != 8 || string.CompareOrdinal(x.ListDate, minList) <= 0))
                .Select(x => x.Code).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (limit > 0) codes = codes.Take(limit).ToList();
        }
        // 状态：取早于扫描日的最近一份
        var prev = Directory.GetFiles(stDir, "state_*.json").Select(f => Path.GetFileNameWithoutExtension(f)[6..]).Where(d => string.CompareOrdinal(d, day) < 0).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault();
        State st; int fromIx;
        var boot = p.Cfg.I("scan.bootstrap_days", 60);
        if (prev is not null && codesArg is not { Count: > 0 })
        {
            st = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(Path.Combine(stDir, $"state_{prev}.json"), ct).ConfigureAwait(false), Jo) ?? new State();
            fromIx = ctx.CalIx.TryGetValue(prev, out var pi) ? pi + 1 : ctx.Cal.Count - 1;
            if (ctx.Cal.Count - fromIx > boot) { log($"[S28] 上次状态 {prev} 距今超过 {boot} 个交易日，重新 bootstrap"); st = NewState(p, boot); fromIx = ctx.Cal.Count - boot; }
            else log($"[S28] 读取状态 {prev}：观察池 {st.Pool.Count} 只，模拟持仓 {st.Book.Positions.Count} 只，待执行 {st.Book.Pending.Count} 笔；重放 {ctx.Cal[fromIx]}~{day}");
        }
        else { st = NewState(p, boot); fromIx = Math.Max(0, ctx.Cal.Count - boot); log($"[S28] 无历史状态（或指定了 --codes），从 {ctx.Cal[fromIx]} 起重放 {ctx.Cal.Count - fromIx} 个交易日建立观察池与模拟持仓"); }
        // 持仓/待执行的股票也必须拉数据
        var need = codes.Concat(st.Pool.Keys).Concat(st.Book.Positions.Select(x => x.Code)).Concat(st.Book.Pending.Select(x => x.Code)).Distinct().ToList();
        var bars = Math.Max(p.Cfg.I("data.bars_scan", 420), p.HighWin + boot + 80);
        log($"[S28] 扫描日 {day}；股票 {need.Count} 只（预过滤后），每只 {bars} 根日K（不复权+前复权）");
        var (stocks, failed) = await SrLoad.StocksAsync(p, need, uni, ctx, bars, null, log, ct).ConfigureAwait(false);
        var map = stocks.ToDictionary(x => x.Code);
        log($"[S28] 日K完成 {stocks.Count} 只（失败 {failed.Count}）；{sw.Elapsed.TotalSeconds:F0}s");

        // 逐只重放观察池（并行），收集每日 BUY 与扫描日的最终事件
        var buys = new Dictionary<string, List<SrBuyCand>>(); var lastEv = new Dictionary<string, (SrEval Ev, List<SrPoolEvent> Events, SrPoolEntry? Entry, SrPoolEntry? Before)>();
        var lk = new object(); var l0 = 0;
        Parallel.ForEach(stocks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) }, s =>
        {
            SrPoolEntry? entry; lock (lk) entry = st.Pool.GetValueOrDefault(s.Code);
            var evs = new List<SrPoolEvent>(); SrEval? ev = null; SrPoolEntry? before = null; var localBuys = new List<(string, SrBuyCand)>();
            for (var j = fromIx; j < ctx.Cal.Count; j++)
            {
                if (!s.Ix.TryGetValue(ctx.Cal[j], out var i) || i < 1) continue;
                evs.Clear(); var last = j == ctx.Cal.Count - 1; if (last) before = entry;
                entry = SrPool.Step(s, i, entry, p, ctx.Hs300, evs, out ev, last, true);
                foreach (var x in evs.Where(x => x.Kind == "BUY")) localBuys.Add((ctx.Cal[j], new SrBuyCand { Code = s.Code, Ev = x.Ev!, S = s }));
            }
            lock (lk)
            {
                if (entry is null) st.Pool.Remove(s.Code); else st.Pool[s.Code] = entry;
                foreach (var (d, c) in localBuys) { if (!buys.TryGetValue(d, out var l)) buys[d] = l = []; l.Add(c); }
                if (ev is not null && s.Date[^1] == day) { lastEv[s.Code] = (ev, evs.ToList(), entry, before); if (ev.L0) l0++; }
            }
        });
        // 组合重放
        var logs = new List<string>();
        for (var j = fromIx; j < ctx.Cal.Count; j++)
        {
            var d = ctx.Cal[j]; var c = buys.GetValueOrDefault(d) ?? [];
            st.Book.Day(d, ctx.CalIx, x => map.GetValueOrDefault(x), c, ctx.Hs300, p, logs);
        }
        var todays = buys.GetValueOrDefault(day) ?? [];

        // §12 信号表
        var rows = new List<Dictionary<string, string>>();
        string Dt(SrStock s, int i) => i >= 0 && i < s.Count ? s.Date[i] : "";
        Dictionary<string, string> Row(SrStock s, SrEval e, string signal)
        {
            var stop = Stop(s, e, p);
            return new Dictionary<string, string>
            {
                ["date"] = day, ["code"] = s.Code, ["name"] = s.Name, ["signal"] = signal, ["score"] = SrParams.F(e.Score, 1), ["dd"] = SrParams.F(e.Dd, 4), ["rebound"] = SrParams.F(e.Rebound, 4),
                ["d0_date"] = Dt(s, e.D0), ["d0_vol_ratio"] = SrParams.F(e.D0VolRatio, 2), ["ma_cross_date"] = Dt(s, e.MaCross), ["macd_cross_date"] = Dt(s, e.MacdCross),
                ["er20"] = SrParams.F(e.Er, 3), ["r2_20"] = SrParams.F(e.R2, 3), ["sigma20"] = SrParams.F(e.Sigma, 4), ["entry_price"] = SrParams.F(s.RC[e.I], 2), ["stop_price"] = SrParams.F(stop, 2),
                ["industry"] = s.Industry, ["close"] = SrParams.F(s.RC[e.I], 2), ["pct"] = SrParams.F(s.RPct[e.I], 4), ["l3_count"] = e.L3Count.ToString(Inv),
                ["reason"] = "", ["fail_reason"] = "", ["pool_enter_date"] = "", ["position_note"] = "", ["key_watch"] = "",
            };
        }
        foreach (var (code, (ev, events, entry, before)) in lastEv)
        {
            var s = map[code];
            var buy = events.FirstOrDefault(x => x.Kind is "BUY" or "BUY_BLOCKED");
            if (buy is not null)
            {
                var r = Row(s, ev, "BUY");
                var cand = todays.FirstOrDefault(x => x.Code == code);
                r["reason"] = SrEngine.ReasonText(s, ev, p);
                r["position_note"] = buy.Kind == "BUY_BLOCKED" ? "大盘过滤未通过：不开新仓，信号只记录" : cand is null ? "" : "模拟组合：" + cand.Outcome;
                r["pool_enter_date"] = buy.Kind == "BUY" ? buy.Note : entry?.EnterDate ?? "";
                r["entry_price"] = SrParams.F(s.RC[ev.I], 2) + "(参考收盘，实际T+1开盘)";
                rows.Add(r);
            }
            else if (entry is not null && !entry.Bought)
            {
                var r = Row(s, ev, "WATCH");
                r["pool_enter_date"] = entry.EnterDate;
                var validTxt = p.ValidFromD0 > 0 ? $"有效期自 D0（{entry.D0Date}）起 {p.ValidFromD0} 个交易日" : $"有效期 {p.WatchValid} 日";
                r["key_watch"] = ev.KeyWatch ? "重点观察" : "";
                r["reason"] = (ev.KeyWatch ? $"【重点观察】MACD 底部金叉 {Dt(s, ev.MacdCross)} 已出现，等 MA{p.MaS}/{p.MaL} 金叉；" : "") +
                    (events.Any(x => x.Kind == "ENTER") ? $"今日进入观察池：①②③ 通过（③ {ev.L3Count}/9），{validTxt}" : $"观察中（进入 {entry.EnterDate}，{validTxt}）");
                // ② 的 D0 在进池时已固定（跌破 D0 最低价即移出），观察期间不再重复检查 ②；显示池内 D0
                r["fail_reason"] = string.Join("；", SrEngine.FailText(s, ev, p, true).Split('；').Where(x => !x.StartsWith("2.")));
                r["d0_date"] = entry.D0Date;
                rows.Add(r);
            }
        }
        foreach (var pos in st.Book.Positions)
        {
            if (!map.TryGetValue(pos.Code, out var s) || !s.Ix.TryGetValue(day, out var i)) continue;
            var e = s.Ix[pos.EntryDate];
            var stopPx = pos.StopRatio * pos.EntryRawOpen;
            rows.Add(new Dictionary<string, string>
            {
                ["date"] = day, ["code"] = s.Code, ["name"] = s.Name, ["signal"] = pos.LastSignal, ["score"] = SrParams.F(pos.Score, 1), ["dd"] = SrParams.F(pos.Dd, 4), ["rebound"] = SrParams.F(pos.Rebound, 4),
                ["d0_date"] = pos.D0Date, ["d0_vol_ratio"] = SrParams.F(pos.D0VolRatio, 2), ["ma_cross_date"] = pos.MaCrossDate, ["macd_cross_date"] = pos.MacdCrossDate,
                ["er20"] = SrParams.F(pos.Er, 3), ["r2_20"] = SrParams.F(pos.R2, 3), ["sigma20"] = SrParams.F(pos.Sigma, 4), ["entry_price"] = SrParams.F(pos.EntryFill, 2), ["stop_price"] = SrParams.F(stopPx, 2),
                ["industry"] = s.Industry, ["close"] = SrParams.F(s.RC[i], 2), ["pct"] = SrParams.F(s.RPct[i], 4), ["l3_count"] = pos.L3Count.ToString(Inv),
                ["reason"] = pos.Reason, ["fail_reason"] = "", ["pool_enter_date"] = "",
                ["position_note"] = $"模拟持仓 {pos.Shares:F0} 股，{pos.EntryDate} 买入；" + pos.LastNote + (pos.LastSignal == "SELL" ? "；次日开盘卖出" : ""),
            });
        }
        string[] order = ["BUY", "SELL", "REDUCE", "HOLD", "WATCH"];
        var rankKey = p.RankBy == "score" ? "score" : "er20";
        rows = rows.OrderBy(r => Array.IndexOf(order, r["signal"])).ThenByDescending(r => r["key_watch"] == "" ? 0 : 1).ThenByDescending(r => double.TryParse(r[rankKey], NumberStyles.Float, Inv, out var v) ? v : 0).ThenBy(r => r["code"], StringComparer.Ordinal).ToList();

        // 写文件
        var sb = new StringBuilder("\uFEFF" + string.Join(",", Cols) + "\n");
        foreach (var r in rows) sb.Append(string.Join(",", Cols.Select(c => Q(r.GetValueOrDefault(c, ""))))).Append('\n');
        var sigPath = Path.Combine(outDir, $"signals_{day}.csv");
        await File.WriteAllTextAsync(sigPath, sb.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outDir, "signals_latest.csv"), sb.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        var counts = order.ToDictionary(k => k, k => rows.Count(r => r["signal"] == k));
        var keyN = rows.Count(r => r["signal"] == "WATCH" && r["key_watch"] != "");
        st.Version = p.Version; st.LastDate = day; st.UpdatedAt = BrMarketData.NowCn().ToString("yyyy-MM-dd HH:mm:ss");
        st.Note = "模拟组合（同回测规则：风险预算 0.8%/笔、单票≤10%、最多10只、每日新开≤3），不是真实持仓";
        if (codesArg is not { Count: > 0 })
        {
            var js = JsonSerializer.Serialize(st, Jo);
            await File.WriteAllTextAsync(Path.Combine(stDir, $"state_{day}.json"), js, new UTF8Encoding(false), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(outDir, "state.json"), js, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var tc = new StringBuilder("\uFEFFcode,name,sig_date,entry_date,exit_date,entry_fill,shares,pnl,ret,hold_days,primary,reasons,partials\n");
            foreach (var t in st.Book.Closed) tc.Append(string.Join(",", t.Code, Q(t.Name), t.SigDate, t.EntryDate, t.ExitDate, SrParams.F(t.EntryFill), t.Shares0, SrParams.F(t.Pnl), SrParams.F(t.Ret, 4), t.HoldDays, t.Primary, Q(t.Reasons), Q(t.Partials))).Append('\n');
            await File.WriteAllTextAsync(Path.Combine(outDir, "paper_trades.csv"), tc.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        // 日报
        var md = new StringBuilder();
        md.AppendLine($"# S28 底部反转缓升 · 每日信号（{day}）").AppendLine();
        md.AppendLine($"> 参数 {p.Cfg.Path}（v{p.Version}）；运行于 {BrMarketData.NowCn():yyyy-MM-dd HH:mm}{(ctx.Intraday ? "（15:05 前运行，用上一交易日收盘）" : "")}。股票 {need.Count} 只（预过滤后），完成 {stocks.Count}，失败 {failed.Count}；L0 通过 {l0} 只。");
        md.AppendLine($"> 大盘：沪深300 {(ctx.Hs300.Allow(day, p) ? "允许开新仓" : "**大盘过滤未通过，BUY 只记录**")}；{(ctx.Hs300.Weak(day, p) ? "弱势（<MA60 且 MA60 下行）" + (p.WeakMode == "half" ? "，新仓减半" : p.WeakMode == "pause" ? "，暂停新仓" : "（v2.0 不减半）") : "非弱势")}；环境 {ctx.Hs300.Regime(day)}。");
        md.AppendLine($"> 信号：BUY {counts["BUY"]} / SELL {counts["SELL"]} / REDUCE {counts["REDUCE"]} / HOLD {counts["HOLD"]} / WATCH（观察池）{counts["WATCH"]}，其中重点观察（a 已完成、b 未完成）{keyN}。BUY 同日按 {(p.RankBy == "score" ? "评分" : "ER")} 降序。模拟组合：现金 {st.Book.Cash:N0}，权益 {st.Book.LastEquity:N0}，持仓 {st.Book.Positions.Count} 只，待执行 {st.Book.Pending.Count} 笔。").AppendLine();
        foreach (var sig in order)
        {
            var l = rows.Where(r => r["signal"] == sig).ToList(); if (l.Count == 0) continue;
            md.AppendLine($"## {sig}（{l.Count}）").AppendLine();
            md.AppendLine("| 代码 | 名称 | 评分 | DD | 反弹 | D0 / 放量 | 金叉(MA/MACD) | ER / R² / σ | 参考价 | 止损 | 说明 |"); md.AppendLine("|---|---|---:|---:|---:|---|---|---|---:|---:|---|");
            foreach (var r in l.Take(sig == "WATCH" ? 60 : 100))
                md.AppendLine($"| {r["code"]}{(r["key_watch"] != "" ? " ★" : "")} | {r["name"]} | {r["score"]} | {Pct(r["dd"])} | {Pct(r["rebound"])} | {r["d0_date"]} / {r["d0_vol_ratio"]} | {r["ma_cross_date"]} / {r["macd_cross_date"]} | {r["er20"]} / {r["r2_20"]} / {Pct(r["sigma20"])} | {r["entry_price"].Split('(')[0]} | {r["stop_price"]} | {(sig == "WATCH" ? "缺：" + r["fail_reason"] : (r["reason"] + " " + r["position_note"]).Trim())} |");
            if (sig == "WATCH" && l.Count > 60) md.AppendLine($"| … | 另 {l.Count - 60} 只见 signals_{day}.csv | | | | | | | | | |");
            md.AppendLine();
        }
        if (logs.Count > 0) md.AppendLine("模拟组合执行记录：" + string.Join("；", logs.TakeLast(20))).AppendLine();
        md.AppendLine($"> ★ = 重点观察（MACD 底部金叉已出现、均线金叉未出现）。止损价 = min(D0 最低价, 参考价×(1−{p.HardStop:P0}))，最宽 −{p.MaxStop:P0}。BUY 次日开盘价成交；开盘涨停放弃。数据仅供研究，不构成投资建议。");
        var rp = Path.Combine(outDir, $"report_{day}.md");
        await File.WriteAllTextAsync(rp, md.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report_latest.md"), md.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        var meta = new JsonObject
        {
            ["strategy"] = "S28 底部反转缓升", ["version"] = p.Version, ["scan_day"] = day, ["run_at"] = BrMarketData.NowCn().ToString("yyyy-MM-dd HH:mm:ss"), ["intraday_run"] = ctx.Intraday,
            ["stocks"] = need.Count, ["done"] = stocks.Count, ["failed"] = failed.Count, ["l0_pass"] = l0, ["counts"] = new JsonObject(counts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["pool"] = st.Pool.Values.Count(x => !x.Bought), ["positions"] = st.Book.Positions.Count, ["equity"] = Math.Round(st.Book.LastEquity, 2), ["market_allow"] = ctx.Hs300.Allow(day, p), ["market_weak"] = ctx.Hs300.Weak(day, p), ["key_watch"] = keyN, ["config"] = Path.GetFileName(p.Cfg.Path),
            ["elapsed_sec"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, $"scan_meta_{day}.json"), meta.ToJsonString(Jo), new UTF8Encoding(false), ct).ConfigureAwait(false);
        ConfigGovernance.CommitSuccessfulRun(Path.Combine(outDir,"config_run.json"),p.Cfg.Root,new{ScanDay=day,SourceMetadata=meta},"S28");
        log($"[S28] 完成：扫描日 {day}；BUY {counts["BUY"]} / SELL {counts["SELL"]} / REDUCE {counts["REDUCE"]} / HOLD {counts["HOLD"]} / WATCH {counts["WATCH"]}（重点观察 {keyN}）；用时 {sw.Elapsed.TotalSeconds:F0}s；输出 {outDir}");
        return new Output { Day = day, SignalsPath = sigPath, ReportPath = rp, OutDir = outDir, Intraday = ctx.Intraday, Counts = counts, Scanned = stocks.Count, L0Pass = l0 };
    }

    private static State NewState(SrParams p, int boot) => new() { Book = SrBook.New(p), Note = $"bootstrap {boot} 日" };

    /// <summary>建议止损价（以收盘价为参考买入价，实际价格口径）。</summary>
    public static double Stop(SrStock s, SrEval e, SrParams p)
    {
        var refp = s.C[e.I]; var hard = refp * (1 - p.HardStop); var d0 = e.D0 >= 0 ? s.L[e.D0] : double.NaN;
        double st = p.StopMode switch
        {
            "higher" => double.IsNaN(d0) ? hard : Math.Max(d0, hard),
            "atr" => Math.Max(refp - p.AtrMult * s.Atr[e.I], p.AtrCap ? refp * (1 - p.MaxStop) : 0),
            _ => Math.Max(double.IsNaN(d0) ? hard : Math.Min(d0, hard), refp * (1 - p.MaxStop)),
        };
        return st / s.C[e.I] * s.RC[e.I];
    }

    private static string Pct(string s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? SrParams.Pu(v) : "";
    private static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
