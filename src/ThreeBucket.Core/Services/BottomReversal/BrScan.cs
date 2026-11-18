using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>每日全市场扫描：股票池过滤 → 东财日K → 100 分评分 → 前20/单股详情/历史评分/日报（data/bottom_reversal/）。</summary>
public static class BrScan
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed class ScanOutput
    {
        public string ScanDay = "";
        public int Scored;
        public bool DroppedIncompleteToday, Intraday;
        public Dictionary<string, int> RatingCounts = new();
        public List<BrResult> Results = [];
        public string ReportPath = "", OutDir = "";
        public JsonObject Meta = new();
    }

    /// <summary>盘中运行丢弃今日未完成K线（data.use_incomplete_today_bar=false）。返回 (扫描日, 是否盘中, 是否丢弃)。</summary>
    public static (string Day, bool Intraday, bool Dropped) ResolveScanDay(BrConfig cfg, IReadOnlyList<string> cal, Action<string> log)
    {
        var now = BrMarketData.NowCn();
        var today = now.ToString("yyyy-MM-dd");
        var hm = cfg.S("data.market_close_time", "15:05").Split(':');
        var close = new TimeSpan(int.Parse(hm[0], Inv), int.Parse(hm[1], Inv), 0);
        var nowHm = new TimeSpan(now.Hour, now.Minute, 0);
        var intraday = cal.Count > 0 && cal[^1] == today && nowHm < close;
        if (intraday && !cfg.B("data.use_incomplete_today_bar"))
        {
            log($"[S27] 当前 {now:HH:mm} 盘中，今日K线未完成 → 丢弃今日K线，按上一交易日 {cal[^2]} 收盘数据评分");
            return (cal[^2], true, true);
        }
        return (cal[^1], intraday, false);
    }

    /// <summary>与 ResolveScanDay 一致地截掉今日未完成K线（复盘/回测用）。</summary>
    public static BrSeries DropIncomplete(BrSeries s, BrConfig cfg)
    {
        var now = BrMarketData.NowCn();
        var hm = cfg.S("data.market_close_time", "15:05").Split(':');
        var close = new TimeSpan(int.Parse(hm[0], Inv), int.Parse(hm[1], Inv), 0);
        if (!cfg.B("data.use_incomplete_today_bar") && s.Count > 0 && s.Date[^1] == now.ToString("yyyy-MM-dd") && new TimeSpan(now.Hour, now.Minute, 0) < close)
            return s.Take(s.Count - 1);
        return s;
    }

    public static async Task<BrMarketEnv> MarketEnvAsync(BrConfig cfg, string scanDay, CancellationToken ct)
    {
        var o = new BrMarketEnv();
        if (!cfg.B("market.enabled", true)) return o;
        var flat = cfg.D("market.ma20_flat", 0.003);
        int downs = 0; var shrink = false;
        var parts = new List<string>();
        foreach (var (name, sid) in cfg.Indexes())
        {
            var k = await BrMarketData.FetchKlineAsync(sid, 80, 0, 3, ct).ConfigureAwait(false);
            if (k is null || k.S.Count == 0) { o.Indexes[name] = new JsonObject { ["error"] = "获取失败" }; parts.Add($"{name}MA20?"); continue; }
            var n = k.S.Date.Count(x => string.CompareOrdinal(x, scanDay) <= 0);
            var c = k.S.Close[..n]; var a = k.S.Amount[..n];
            var ma20 = BrIndicators.Sma(c, 20);
            var slope = ma20[^1] / ma20[^6] - 1;
            var trend = slope < -flat ? "向下" : (slope <= flat ? "走平" : "向上");
            var a5 = BrMath.NpMean(a.AsSpan(a.Length - 5)); var a20 = BrMath.NpMean(a.AsSpan(a.Length - 20));
            o.Indexes[name] = new JsonObject
            {
                ["secid"] = sid, ["date"] = k.S.Date[n - 1], ["close"] = BrMath.PyRound(c[^1], 2), ["ma20"] = BrMath.PyRound(ma20[^1], 2),
                ["ma20_slope5"] = BrMath.PyRound(slope, 4), ["trend"] = trend, ["amount5_vs_20"] = BrMath.PyRound(a5 / a20, 3),
                ["source"] = k.Source, ["fetched_at"] = k.FetchedAt,
            };
            parts.Add($"{name}MA20{trend}");
            if (trend == "向下" && c[^1] < ma20[^1]) downs++;
            if (name == "中证全指" && a5 / a20 < cfg.D("market.amount_shrink_ratio", 0.8)) shrink = true;
        }
        o.Weak = downs >= 1 && (shrink || downs >= 2);
        o.Label = (o.Weak ? "偏弱" : "中性/偏强") + "（" + string.Join("，", parts) + (shrink ? "，成交额萎缩" : "") + "）";
        return o;
    }

    public static async Task<ScanOutput> RunAsync(BrConfig cfg, string dataDir, Action<string> log, int limit = 0,
        IReadOnlyCollection<string>? codes = null, string? outDir = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var cacheDir = Path.Combine(dataDir, "cache");
        outDir ??= Path.Combine(dataDir, "bottom_reversal");
        Directory.CreateDirectory(outDir);

        var calK = await BrMarketData.FetchKlineAsync(cfg.S("data.calendar_index_secid", "1.000985"), 400, 0, 3, ct).ConfigureAwait(false);
        if (calK is null || calK.S.Count < 130) throw new InvalidOperationException("交易日历（中证全指日K）获取失败");
        var (scanDay, intraday, dropped) = ResolveScanDay(cfg, calK.S.Date, log);
        var calDates = calK.S.Date.Where(x => string.CompareOrdinal(x, scanDay) <= 0).ToList();
        var liqWin = cfg.I("universe.liquidity_window", 20);
        var last20 = calDates.Skip(Math.Max(0, calDates.Count - liqWin)).ToHashSet();

        var (items, uFetched, uSource) = await BrMarketData.GetUniverseAsync(cfg, cacheDir, log, ct).ConfigureAwait(false);
        var excl = new Dictionary<string, int>();
        var exLock = new object();
        void Ex(string reason) { lock (exLock) excl[reason] = excl.GetValueOrDefault(reason) + 1; }
        var minListed = cfg.I("universe.min_listed_trading_days", 120);
        var pool = new List<BrMarketData.UniverseItem>();
        foreach (var it in items)
        {
            var b = it.Board;
            if ((b == "sh" && !cfg.B("universe.include_sh", true)) || (b == "sz" && !cfg.B("universe.include_sz", true)) || (b == "bj" && !cfg.B("universe.include_bj", true)))
            { Ex("板块开关排除"); continue; }
            if (cfg.B("universe.exclude_st", true) && it.Name.ToUpperInvariant().Contains("ST")) { Ex("ST/*ST"); continue; }
            if (cfg.B("universe.exclude_delisting", true) && it.Name.Contains('退')) { Ex("退市/退市整理"); continue; }
            var ld = it.ListDate ?? "";
            if (ld.Length == 8)
            {
                var ldf = $"{ld[..4]}-{ld[4..6]}-{ld[6..]}";
                if (string.CompareOrdinal(ldf, scanDay) > 0) { Ex("未上市"); continue; }
                var ndays = calDates.Count(x => string.CompareOrdinal(x, ldf) >= 0);
                if (ndays < minListed && ndays < calDates.Count) { Ex($"上市不足{minListed}交易日"); continue; }
            }
            pool.Add(it);
        }
        if (codes is { Count: > 0 })
        {
            var sel = pool.Where(x => codes.Contains(x.Code)).ToList();
            pool = sel.Count > 0 ? sel : codes.Select(c => new BrMarketData.UniverseItem(c, "", BrMarketData.Board(c), "", "", null, null)).ToList();
        }
        if (limit > 0) pool = pool.Take(limit).ToList();
        log($"[S27] 扫描日 {scanDay}；股票列表 {items.Count} 只，名称/上市日过滤后 {pool.Count} 只");

        var market = await MarketEnvAsync(cfg, scanDay, ct).ConfigureAwait(false);
        log($"[S27] 市场环境：{market.Label}");

        var results = new ConcurrentBag<BrResult>();
        int up = 0, down = 0, flatN = 0, lu = 0, ld2 = 0, done = 0;
        string src = "", fetchedAt = "";
        var bars = cfg.I("data.kline_bars", 260); var retries = cfg.I("data.retries", 4);
        double minAmt = cfg.D("universe.min_avg_amount", 5e7); var minValid = cfg.I("universe.min_valid_days", 15);
        using var sem = new SemaphoreSlim(Math.Max(1, cfg.I("data.concurrency", 8)));
        var tasks = pool.Select(async it =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var k = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(it.Code), bars, 1, retries, ct).ConfigureAwait(false);
                if (k is null || k.S.Count == 0) { Ex("日K获取失败"); return; }
                lock (exLock) { src = k.Source; fetchedAt = k.FetchedAt; }
                var n = k.S.Date.Count(x => string.CompareOrdinal(x, scanDay) <= 0);
                if (n == 0 || k.S.Date[n - 1] != scanDay) { Ex("停牌/无扫描日K线"); return; }
                var s = k.S.Take(n);
                if (n >= 2)
                {
                    var ch = s.Close[^1] / s.Close[^2] - 1; var lp = BrMarketData.LimitPct(it.Code);
                    if (ch > 0) Interlocked.Increment(ref up); else if (ch < 0) Interlocked.Increment(ref down); else Interlocked.Increment(ref flatN);
                    if (ch >= lp - 0.005) Interlocked.Increment(ref lu);
                    if (ch <= -lp + 0.005) Interlocked.Increment(ref ld2);
                }
                if (n < minListed) { Ex($"上市不足{minListed}交易日"); return; }
                var valid = Enumerable.Range(0, n).Where(j => last20.Contains(s.Date[j]) && s.Volume[j] > 0).ToList();
                if (valid.Count < minValid) { Ex("近20日有效交易天数不足(长期停牌)"); return; }
                double sum = 0; foreach (var j in valid) sum += s.Amount[j];
                var avgAmt = sum / valid.Count;
                if (avgAmt < minAmt) { Ex("20日日均成交额不足"); return; }
                s.Prepare(cfg);
                BrResult r;
                try { r = BrModel.ScoreAt(s, n - 1, cfg, it.Code, market); }
                catch (Exception e) { Ex("评分异常"); log($"[S27] {it.Code} 评分异常: {e.Message}"); return; }
                if (r.Total is null) { Ex("数据不足(K线根数)"); return; }
                r.Name = it.Name.Length > 0 ? it.Name : k.Name; r.Industry = it.Industry ?? "";
                r.AvgAmount20 = BrMath.PyRound(avgAmt, 0); r.Mv = it.Mv; r.FloatMv = it.FloatMv;
                try { r.Trade = BrTradeEngine.Evaluate(s, n - 1, cfg, it.Code); r.Trade.Group = BrTradeEngine.AssignGroup(r.Trade, r.Total.Value, cfg); }
                catch (Exception e) { log($"[S27] {it.Code} 交易阶段计算异常: {e.Message}"); }
                results.Add(r);
            }
            finally
            {
                sem.Release();
                var dn = Interlocked.Increment(ref done);
                if (dn % 500 == 0) log($"[S27] 进度 {dn}/{pool.Count}，入选评分 {results.Count}");
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var list = results.OrderBy(r => -r.Total!.Value).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
        var bands = new Dictionary<string, int>(); var stages = new Dictionary<string, int>();
        foreach (var r in list) { bands[r.Rating] = bands.GetValueOrDefault(r.Rating) + 1; stages[r.Stage] = stages.GetValueOrDefault(r.Stage) + 1; }

        var meta = new JsonObject
        {
            ["strategy_id"] = cfg.S("strategy_id", "S27"), ["strategy_name"] = cfg.S("strategy_name", "增强版底部反转V1"), ["model_version"] = cfg.Version, ["scan_day"] = scanDay,
            ["run_at"] = BrMarketData.NowCn().ToString("yyyy-MM-dd HH:mm:ss"), ["intraday_run"] = intraday, ["dropped_incomplete_today_bar"] = dropped,
            ["kline_source"] = src, ["fetched_at"] = fetchedAt, ["universe_source"] = uSource, ["universe_fetched_at"] = uFetched,
            ["config"] = cfg.Path, ["engine"] = "C# ThreeBucket.Core BottomReversal", ["pool"] = pool.Count, ["scored"] = list.Count,
            ["excluded"] = ToObj(excl), ["rating_counts"] = ToObj(bands), ["stage_counts"] = ToObj(stages),
            ["market"] = new JsonObject { ["indexes"] = market.Indexes.DeepClone(), ["weak"] = market.Weak, ["label"] = market.Label },
            ["market_label"] = market.Label,
            ["breadth"] = new JsonObject { ["up"] = up, ["down"] = down, ["flat"] = flatN, ["limit_up"] = lu, ["limit_down"] = ld2 },
            ["elapsed_sec"] = BrMath.PyRound(sw.Elapsed.TotalSeconds, 1),
        };

        var topN = cfg.I("output.top_n", 20);
        var top = list.Take(topN).Select((r, k) => BrReport.TopRow(k + 1, r)).ToList();
        BrReport.WriteCsv(Path.Combine(outDir, $"br_top{topN}_{scanDay}.csv"), top, BrReport.TopCols);
        BrReport.WriteCsv(Path.Combine(outDir, $"br_top{topN}_latest.csv"), top, BrReport.TopCols);
        var histDir = Path.Combine(outDir, "history"); Directory.CreateDirectory(histDir);
        var allRows = list.Select((r, k) => BrReport.TopRow(k + 1, r)).ToList();
        BrReport.WriteCsv(Path.Combine(histDir, $"scores_{scanDay}.csv"), allRows, BrReport.AllCols);
        var hpath = Path.Combine(outDir, "score_history.csv");
        var keep = BrReport.ReadCsv(hpath).Where(x => x.GetValueOrDefault("bar_date") != scanDay).Cast<IReadOnlyDictionary<string, string>>().ToList();
        var hmin = cfg.I("output.history_min_score", 50);
        keep.AddRange(allRows.Where(x => int.Parse(x["total"], Inv) >= hmin));
        BrReport.WriteCsv(hpath, keep, BrReport.AllCols);
        BrTradeReport.WriteOutputs(outDir, scanDay, list, cfg, meta);
        var detDir = Path.Combine(outDir, "details", scanDay);
        var grp = BrTradeReport.Groups(list); var gTop = cfg.I("trade.groups.report_top_n", 20);
        var detail = list.Take(cfg.I("output.detail_top_n", 20)).Concat(grp["A"].Take(gTop)).Concat(grp["B"].Take(gTop)).Concat(grp["C"].Take(gTop)).DistinctBy(r => r.Code).ToList();
        foreach (var r in detail) BrReport.WriteDetail(detDir, r, meta);
        File.WriteAllText(Path.Combine(outDir, $"scan_meta_{scanDay}.json"), meta.ToJsonString(BrReport.JsonOpts), new UTF8Encoding(false));
        var md = DailyMd(meta, top) + "\n\n" + BrTradeReport.GroupsMd(list, cfg) + "\n";
        var rp = Path.Combine(outDir, $"report_{scanDay}.md");
        File.WriteAllText(rp, md, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "report_latest.md"), md, new UTF8Encoding(false));
        ConfigGovernance.CommitSuccessfulRun(Path.Combine(outDir,"config_run.json"),cfg.Root,new{ScanDay=scanDay,SourceMetadata=meta},"S27");
        log($"[S27] 完成：评分 {list.Count} 只；评级分布 {string.Join("，", bands.Select(kv => $"{kv.Key} {kv.Value}"))}；V1.2 分组 {meta["group_counts"]?.ToJsonString()}；用时 {sw.Elapsed.TotalSeconds:0.0}s；输出目录 {outDir}");
        return new ScanOutput
        {
            ScanDay = scanDay, Scored = list.Count, DroppedIncompleteToday = dropped, Intraday = intraday, RatingCounts = bands,
            Results = list, ReportPath = Path.Combine(outDir, "report_latest.md"), OutDir = outDir, Meta = meta,
        };
    }

    private static JsonObject ToObj(Dictionary<string, int> d) => new(d.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

    public static string DailyMd(JsonObject meta, List<Dictionary<string, string>> top)
    {
        var L = new List<string> { $"### {meta["strategy_id"]} {meta["strategy_name"]} · 扫描日 {meta["scan_day"]}", "" };
        var dropped = meta["dropped_incomplete_today_bar"]!.GetValue<bool>(); var intra = meta["intraday_run"]!.GetValue<bool>();
        L.Add($"- 运行时间 {meta["run_at"]}（北京时间）" + (dropped ? "；盘中运行，已丢弃今日未完成K线" : intra ? "；盘中运行，使用今日未完成K线" : ""));
        L.Add($"- 股票池 {meta["pool"]} 只 → 通过流动性/停牌/上市天数过滤并评分 {meta["scored"]} 只");
        L.Add("- 排除：" + string.Join("，", ((JsonObject)meta["excluded"]!).OrderByDescending(kv => kv.Value!.GetValue<int>()).Select(kv => $"{kv.Key} {kv.Value}")));
        var rc = (JsonObject)meta["rating_counts"]!;
        L.Add("- 评级分布：" + string.Join("，", new[] { "强反转", "反转初期", "反弹观察", "暂不关注" }.Select(k => $"{k} {rc[k]?.GetValue<int>() ?? 0}")));
        L.Add("- 阶段分布：" + string.Join("，", ((JsonObject)meta["stage_counts"]!).OrderByDescending(kv => kv.Value!.GetValue<int>()).Select(kv => $"{kv.Key} {kv.Value}")));
        var b = meta["breadth"]!;
        L.Add($"- 市场环境（独立标签，不计入个股分）：{meta["market_label"]}；上涨 {b["up"]} / 下跌 {b["down"]} / 涨停 {b["limit_up"]} / 跌停 {b["limit_down"]}");
        L.Add($"- 数据来源：{meta["kline_source"]}（抓取 {meta["fetched_at"]}）；参数文件 `{meta["config"]}`；引擎 C#");
        L.Add("- 未接入：财务风险、公告风险过滤（V1 未核实，见个股报告第4节）");
        L.Add("");
        L.Add("| # | 代码 | 名称 | 总分 | A底部 | B突破 | C承接 | D均线 | E动能 | 阶段 | 评级 | 标记 |");
        L.Add("|---:|---|---|---:|---:|---:|---:|---:|---:|---|---|---|");
        foreach (var x in top)
            L.Add($"| {x["rank"]} | {x["code"]} | {x["name"]} | {x["total"]} | {x["A"]} | {x["B"]} | {x["C"]} | {x["D"]} | {x["E"]} | {x["stage"]} | {x["rating"]} | {x["flags"]} |");
        L.Add("");
        L.Add("> 评分高 ≠ 买入时机好。辅助研究工具，不自动下单。");
        return string.Join("\n", L);
    }
}
