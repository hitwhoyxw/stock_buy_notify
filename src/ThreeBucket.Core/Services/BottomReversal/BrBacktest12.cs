using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static ThreeBucket.Core.Services.BottomReversal.BrMath;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>
/// V1.2 实验回测（不覆盖 V1.1；输出 data/bottom_reversal/backtest_v12/）：
/// 一、B3 止损实验组 S1 结构 / S2 固定8% / S3 固定10% / S4 结构与8%取更低 / S5 ATR14×1.5/2.0/2.5，统一按单笔账户风险预算定仓位；
/// 二、B1 全部信号的 T+1 数据与取消原因（首次 + 全部触发，7 类）；三、B1 分级 A/B/C；四、被取消 B1 的后续跟踪；
/// 五、B1 / B2 / B3 / B1-B 等待 B3 四组对照（相同初始资金、成本、风险预算），全样本 + 样本内 / 样本外。
/// 无未来函数：信号在 T 日收盘用 ≤T 数据确认；T+1 开盘成交，仓位用 T 日收盘权益；同日先开仓（开盘）后平仓（盘中/收盘）。
/// </summary>
public static class BrBacktest12
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(double? v, int nd = 4) => v is double x && !double.IsNaN(x) && !double.IsInfinity(x) ? PyFmt(x, nd) : "";
    private static string Pc(double? v, int nd = 1) => v is double x && !double.IsNaN(x) && !double.IsInfinity(x) ? PyFmtSigned(x * 100, nd) + "%" : "-";

    public static readonly string[] Schemes = ["S1", "S2", "S3", "S4", "S5a", "S5b", "S5c"];
    public static string SchemeName(string s) => s switch
    {
        "S1" => "S1 原始结构止损", "S2" => "S2 固定8%", "S3" => "S3 固定10%", "S4" => "S4 结构与8%取更低",
        "S5a" => "S5 ATR14×1.5", "S5b" => "S5 ATR14×2.0", "S5c" => "S5 ATR14×2.5", _ => s,
    };
    public static readonly string[] Groups = ["B1", "B2", "B3", "B1W"];
    public static string GroupName(string g) => g switch { "B1" => "B1 突破", "B2" => "B2 确认", "B3" => "B3 缩量回踩", "B1W" => "B1 过滤后等待B3", _ => g };

    private sealed class Sig
    {
        public string Code = "", Name = "", Type = "", Date = "";
        public int I; public BrTradeSignal S = null!;
        public BrTradeEngine.B1Eval? B1; public string ExecStatus = "", ExecNote = ""; public double? Entry; public BrLevels? Lv;
        public bool Suspended, LimitUp;
        public int FirstCat; public string FirstReason = "";
        public List<(int Cat, string Detail)> All = [];
        public string Grade = "";
    }

    private sealed class Cand
    {
        public string Code = "", Group = "", Scheme = "", SigDate = "", EntryDate = "", ExitDate = "", Reason = "", SrcB1Date = "";
        public int E, X; public double EntryQ, EntryRaw, StopQ, RiskPct, Rr, ExitQ, Mae, Mfe, Resist;
        public List<(string Date, double CloseRatio)> Path = [];   // 每日收盘 / 买入价（前复权口径）
    }

    private sealed class Stock
    {
        public string Code = "", Name = ""; public BrSeries Q = null!, Raw = null!; public int Start; public double Lp;
        public List<Sig> Sigs = [];
    }

    // ── 数据：东财日K + 本地缓存（当日有效）──
    private static async Task<BrSeries?> Kline(string cacheDir, string code, int lmt, int fqt, CancellationToken ct)
    {
        var f = Path.Combine(cacheDir, $"{code}_{fqt}.csv");
        var today = BrMarketData.NowCn().ToString("yyyy-MM-dd");
        if (File.Exists(f))
        {
            var lines = await File.ReadAllLinesAsync(f, ct).ConfigureAwait(false);
            if (lines.Length > 1 && lines[0] == $"#{today}|{lmt}")
            {
                var rows = lines.Skip(1).Select(l => l.Split(',')).ToList();
                double[] C(int k) => rows.Select(r => double.Parse(r[k], Inv)).ToArray();
                return new BrSeries { Date = rows.Select(r => r[0]).ToArray(), Open = C(1), Close = C(2), High = C(3), Low = C(4), Volume = C(5), Amount = C(6), Turnover = C(7) };
            }
        }
        var k = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), lmt, fqt, 4, ct).ConfigureAwait(false);
        if (k is null) return null;
        var s = k.S; var sb = new StringBuilder(); sb.Append($"#{today}|{lmt}\n");
        for (var j = 0; j < s.Count; j++)
            sb.Append(string.Join(",", s.Date[j], R(s.Open[j]), R(s.Close[j]), R(s.High[j]), R(s.Low[j]), R(s.Volume[j]), R(s.Amount[j]), R(s.Turnover[j]))).Append('\n');
        await File.WriteAllTextAsync(f, sb.ToString(), ct).ConfigureAwait(false);
        return s;
        static string R(double v) => double.IsNaN(v) ? "NaN" : v.ToString("R", Inv);
    }

    private static BrSeries Align(BrSeries k, List<string> common)
    {
        var ix = new Dictionary<string, int>(); for (var j = 0; j < k.Count; j++) ix[k.Date[j]] = j;
        double[] P(double[] a) => common.Select(x => a[ix[x]]).ToArray();
        return new BrSeries { Date = common.ToArray(), Open = P(k.Open), Close = P(k.Close), High = P(k.High), Low = P(k.Low), Volume = P(k.Volume), Amount = P(k.Amount), Turnover = P(k.Turnover) };
    }

    private static bool LimitUpOpen(BrSeries raw, int j, double lp) => raw.Open[j] >= raw.Close[j - 1] * (1 + lp) - 0.011;
    private static bool Locked(BrSeries raw, int j, double lp) => raw.High[j] - raw.Low[j] < 1e-6 && raw.Close[j] <= raw.Close[j - 1] * (1 - lp) + 0.011;

    /// <summary>卖出模拟：A股 T+1（买入当日不卖）；开盘已低于止损按开盘价；盘中触及按止损价；跌停封死顺延到下一个可成交日开盘；到期按收盘。</summary>
    private static (int X, double Px, string Why)? Simulate(BrSeries q, BrSeries raw, int e, int hold, double stop, double lp)
    {
        var target = e + hold - 1; var pending = false;
        for (var j = e + 1; j < q.Count; j++)
        {
            var lk = Locked(raw, j, lp);
            if (pending) { if (!lk) return (j, q.Open[j], "跌停顺延卖出"); continue; }
            if (q.Open[j] <= stop) { if (lk) { pending = true; continue; } return (j, q.Open[j], "跳空低开止损(开盘价)"); }
            if (q.Low[j] <= stop) { if (lk) { pending = true; continue; } return (j, stop, "止损"); }
            if (j >= target) { if (lk) { pending = true; continue; } return (j, q.Close[j], "持有到期"); }
        }
        return null;
    }

    private static double? SchemeStop(string scheme, double entry, BrLevels lv)
    {
        var st = lv.StructStop is double ss && ss > 0 && ss < entry ? ss : (double?)null;
        double? v = scheme switch
        {
            "S1" => st, "S2" => entry * 0.92, "S3" => entry * 0.90, "S4" => st is double s4 ? Math.Min(s4, entry * 0.92) : null,
            "S5a" => entry - 1.5 * lv.Atr, "S5b" => entry - 2.0 * lv.Atr, "S5c" => entry - 2.5 * lv.Atr, _ => null,
        };
        return v is double x && x > 0 && x < entry && !double.IsNaN(x) ? x : null;
    }

    public static async Task RunAsync(BrConfig cfg0, string dataDir, IReadOnlyList<string> codesArg, int days, int limit, string? outArg, Action<string> log, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outDir = outArg ?? Path.Combine(dataDir, "bottom_reversal", "backtest_v12" + (codesArg.Count > 0 ? $"_codes{codesArg.Count}" : ""));
        Directory.CreateDirectory(outDir);
        // 信号识别用原始 V1.1 止损口径（与对照组同一信号集合）；止损实验只作用于成交后的风险管理
        var cfg = cfg0.Node("backtest_v12.signal_stop") is JsonNode ss ? cfg0.With("trade.stop", ss) : cfg0;
        if (days <= 0) days = cfg.I("backtest_v12.days", 1500);
        var hold = cfg.I("backtest_v12.hold_days", 20);
        var split = cfg.S("backtest_v12.split_date", "2024-01-01");
        var cacheDir = Path.Combine(dataDir, "cache", "br_bt12"); Directory.CreateDirectory(cacheDir);
        var warm = 260;

        // 样本
        var names = new Dictionary<string, string>();
        List<string> codes;
        string sampleNote;
        if (codesArg.Count > 0) { codes = codesArg.ToList(); sampleNote = $"指定 {codes.Count} 只"; }
        else
        {
            var (items, fetched, _) = await BrMarketData.GetUniverseAsync(cfg, Path.Combine(dataDir, "cache"), log, ct).ConfigureAwait(false);
            var minList = BrMarketData.NowCn().AddDays(-(days * 1.45 + warm * 1.45)).ToString("yyyyMMdd");
            var pool = items.Where(x => x.Board != "bj" && !x.Name.Contains("ST") && !x.Name.Contains("退") && x.ListDate.Length == 8 && string.CompareOrdinal(x.ListDate, minList) <= 0)
                            .OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
            foreach (var x in items) names[x.Code] = x.Name;
            var n = limit > 0 ? limit : cfg.I("backtest_v12.sample_size", 400);
            var step = Math.Max(1.0, (double)pool.Count / n);
            codes = Enumerable.Range(0, Math.Min(n, pool.Count)).Select(k => pool[(int)Math.Floor(k * step)].Code).ToList();
            if (cfg.Node("backtest_v12.include_codes") is JsonArray inc) foreach (var c in inc) { var s = c!.GetValue<string>(); if (!codes.Contains(s)) codes.Add(s); }
            sampleNote = $"全市场列表（{fetched} 抓取，{items.Count} 只）剔除北交所/ST/退市/上市日晚于 {minList} 后 {pool.Count} 只，按代码等距抽样 {n} 只 + 指定 3 只 = {codes.Count} 只（仅当前在市股票，存在幸存者偏差）";
        }
        log($"[S27 V1.2] 样本：{sampleNote}；回测 {days} 个交易日，持有 {hold} 日，样本内/外分界 {split}");

        var idx = await BrMarketData.FetchKlineAsync(cfg.S("backtest_v11.regime_index_secid", "1.000985"), days + warm + 50, 0, 4, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("指数日K获取失败（交易日历）");
        var cal = BrScan.DropIncomplete(idx.S, cfg).Date.ToList();
        var calNext = new Dictionary<string, string>(); for (var j = 0; j + 1 < cal.Count; j++) calNext[cal[j]] = cal[j + 1];
        var periodStart = cal[Math.Max(0, cal.Count - days)];   // 统一回测区间：指数日历最近 days 个交易日（个股不足的从其可用数据开始）

        // ── 逐只：拉数据 + 逐日信号 + 执行检查（并行）──
        var stocks = new ConcurrentBag<Stock>(); var failed = new ConcurrentBag<string>(); var done = 0;
        var gate = new SemaphoreSlim(cfg.I("backtest_v12.fetch_concurrency", 6));
        await Parallel.ForEachAsync(codes, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2), CancellationToken = ct }, async (code, tk) =>
        {
            BrSeries? qk, rk;
            await gate.WaitAsync(tk).ConfigureAwait(false);
            try { qk = await Kline(cacheDir, code, days + warm, 1, tk).ConfigureAwait(false); rk = await Kline(cacheDir, code, days + warm, 0, tk).ConfigureAwait(false); }
            finally { gate.Release(); }
            if (qk is null || rk is null || qk.Count < 300) { failed.Add(code); return; }
            var q0 = BrScan.DropIncomplete(qk, cfg); var r0 = BrScan.DropIncomplete(rk, cfg);
            var common = q0.Date.Intersect(r0.Date).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var q = Align(q0, common).Prepare(cfg); var raw = Align(r0, common);
            var st = new Stock { Code = code, Name = names.GetValueOrDefault(code, ""), Q = q, Raw = raw, Lp = BrMarketData.LimitPct(code), Start = Math.Max(Math.Max(cfg.I("model.min_bars", 80) - 1, 120), q.Count - days) };
            while (st.Start < q.Count && string.CompareOrdinal(q.Date[st.Start], periodStart) < 0) st.Start++;
            for (var i = st.Start; i < q.Count - 1; i++)
            {
                var tr = BrTradeEngine.Evaluate(q, i, cfg, code);
                if (tr.Signal is null) continue;
                var sg = new Sig { Code = code, Name = st.Name, Type = tr.Signal.Type, Date = q.Date[i], I = i, S = tr.Signal };
                var e = i + 1;
                sg.Suspended = calNext.TryGetValue(q.Date[i], out var nx) && nx != q.Date[e];
                sg.LimitUp = !sg.Suspended && LimitUpOpen(raw, e, st.Lp);
                if (sg.Type == "B1")
                {
                    var b = BrTradeEngine.B1Final(q, tr.Signal, cfg, sg.Suspended, sg.LimitUp);
                    sg.B1 = b; sg.ExecStatus = b.Status; sg.ExecNote = b.Note; sg.Entry = b.Entry; sg.Lv = b.Lv; sg.Grade = b.Grade;
                    sg.FirstCat = b.FirstCat; sg.FirstReason = b.FirstReason; sg.All = b.All;
                }
                else
                {
                    if (sg.Suspended) sg.ExecStatus = "取消：停牌";
                    else if (sg.LimitUp) sg.ExecStatus = "取消：涨停开盘无法成交";
                    else { var (s2, en, note, lv) = BrTradeEngine.ExecCheck(q, tr.Signal, cfg); sg.ExecStatus = s2; sg.ExecNote = note; sg.Entry = en; sg.Lv = lv; }
                }
                st.Sigs.Add(sg);
            }
            stocks.Add(st);
            var d = Interlocked.Increment(ref done);
            if (d % 50 == 0) log($"[S27 V1.2] 进度 {d}/{codes.Count}（{sw.Elapsed.TotalSeconds:F0}s）");
        }).ConfigureAwait(false);
        var list = stocks.OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
        var allSigs = list.SelectMany(x => x.Sigs).ToList();
        var firstDate = list.Where(x => x.Start < x.Q.Count).Select(x => x.Q.Date[x.Start]).DefaultIfEmpty("").Min() ?? "";
        var lastDate = list.Select(x => x.Q.Date[^1]).DefaultIfEmpty("").Max() ?? "";
        log($"[S27 V1.2] 数据完成 {list.Count} 只（失败 {failed.Count}）；区间 {firstDate}~{lastDate}；信号 B1 {allSigs.Count(x => x.Type == "B1")} / B2 {allSigs.Count(x => x.Type == "B2")} / B3 {allSigs.Count(x => x.Type == "B3")}");

        // ── 候选交易（每组 × 每种止损；与资金无关）──
        var cands = new List<Cand>(); var watchMin = cfg.I("trade.b1_grade.watch_min_days", 3); var watchMax = cfg.I("trade.b1_grade.watch_max_days", 10);
        var b1Rows = new List<Dictionary<string, string>>();
        foreach (var st in list)
        {
            var q = st.Q;
            Cand? Make(Sig sg, string group, string scheme, string src = "")
            {
                if (sg.Entry is not double en || sg.Lv is null) return null;
                var stop = SchemeStop(scheme, en, sg.Lv); if (stop is null) return null;
                var e = sg.I + 1; var res = Simulate(q, st.Raw, e, hold, stop.Value, st.Lp); if (res is null) return null;
                var (x, px, why) = res.Value;
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                for (var j = e; j <= x; j++) { var se = j == x && why.Contains("止损"); lo = Math.Min(lo, se ? px : q.Low[j]); if (!se) hi = Math.Max(hi, q.High[j]); }
                if (double.IsNegativeInfinity(hi)) hi = en;
                var c = new Cand
                {
                    Code = st.Code, Group = group, Scheme = scheme, SigDate = sg.Date, SrcB1Date = src, E = e, X = x, EntryDate = q.Date[e], ExitDate = q.Date[x],
                    EntryQ = en, EntryRaw = st.Raw.Open[e], StopQ = stop.Value, RiskPct = (en - stop.Value) / en, ExitQ = px, Reason = why,
                    Mae = lo / en - 1, Mfe = hi / en - 1, Resist = sg.Lv.Resistance ?? double.NaN,
                };
                c.Rr = sg.Lv.Resistance is double rs ? (rs / en - 1) / c.RiskPct : double.NaN;
                for (var j = e; j <= x; j++) c.Path.Add((q.Date[j], (j == x ? px : q.Close[j]) / en));
                return c;
            }
            foreach (var sg in st.Sigs)
            {
                if (sg.Entry is not null)
                    foreach (var sc in Schemes) { var c = Make(sg, sg.Type, sc); if (c is not null) cands.Add(c); }
                if (sg.Type != "B1") continue;
                // B1-B：3–10 日内第一个可介入的 B3
                Sig? wb3 = null; Sig? anyB3 = null;
                foreach (var b3 in st.Sigs.Where(x => x.Type == "B3" && x.I > sg.I && x.I - sg.I <= 20).OrderBy(x => x.I))
                {
                    anyB3 ??= b3;
                    if (sg.Grade == "B" && b3.I - sg.I >= watchMin && b3.I - sg.I <= watchMax && b3.Entry is not null) { wb3 ??= b3; }
                }
                if (wb3 is not null) foreach (var sc in Schemes) { var c = Make(wb3, "B1W", sc, sg.Date); if (c is not null) cands.Add(c); }
                // 二/四：B1 明细 + 后续跟踪（以信号日收盘为基准）
                var i0 = sg.I; var e1 = i0 + 1; var c0 = q.Close[i0];
                double? Ret(int k) => i0 + k < q.Count ? q.Close[i0 + k] / c0 - 1 : null;
                double mfe = double.NaN, mae = double.NaN; var endF = Math.Min(q.Count - 1, i0 + 20);
                if (endF > i0) { mfe = Enumerable.Range(i0 + 1, endF - i0).Max(j => q.High[j]) / c0 - 1; mae = Enumerable.Range(i0 + 1, endF - i0).Min(j => q.Low[j]) / c0 - 1; }
                var firstB3ok = st.Sigs.Where(x => x.Type == "B3" && x.I > sg.I && x.I - sg.I <= 20 && x.Entry is not null).OrderBy(x => x.I).FirstOrDefault();
                // 若照常 T+1 开盘买入（可成交时），结构止损 S1 持有 hold 日的净收益（仅作对照，不是策略交易）
                string hypo = "";
                if (!sg.Suspended && !sg.LimitUp && e1 < q.Count)
                {
                    var lvO = sg.S.Levels.WithEntry(q.Open[e1], "", false, cfg); var stp = SchemeStop("S1", q.Open[e1], lvO);
                    if (stp is double sp) { var rr = Simulate(q, st.Raw, e1, hold, sp, st.Lp); if (rr is not null) hypo = N(NetRet(cfg, q.Open[e1], rr.Value.Px)); }
                }
                var lvR = sg.Lv;
                b1Rows.Add(new Dictionary<string, string>
                {
                    ["code"] = st.Code, ["name"] = st.Name, ["signal_date"] = sg.Date, ["period"] = string.CompareOrdinal(sg.Date, split) < 0 ? "样本内" : "样本外",
                    ["platform"] = N(sg.S.Platform, 3), ["signal_close"] = N(c0, 3),
                    ["t1_date"] = e1 < q.Count ? q.Date[e1] : "", ["t1_open"] = e1 < q.Count ? N(q.Open[e1], 3) : "", ["t1_high"] = e1 < q.Count ? N(q.High[e1], 3) : "",
                    ["t1_low"] = e1 < q.Count ? N(q.Low[e1], 3) : "", ["t1_close"] = e1 < q.Count ? N(q.Close[e1], 3) : "",
                    ["t1_open_vs_platform"] = e1 < q.Count ? N(q.Open[e1] / sg.S.Platform - 1) : "", ["t1_close_vs_platform"] = e1 < q.Count ? N(q.Close[e1] / sg.S.Platform - 1) : "",
                    ["gap_open"] = e1 < q.Count ? N(q.Open[e1] / c0 - 1) : "", ["ma20_dev"] = N(c0 / q.Ma20[i0] - 1), ["rsi14"] = N(q.Rsi[i0], 2),
                    ["rr_signal_day"] = N(sg.S.Levels.Rr, 2), ["rr_t1_open"] = N(lvR?.Rr, 2), ["risk_tags"] = string.Join("；", sg.S.Tags),
                    ["grade_pre"] = BrTradeEngine.B1PreGrade(sg.S, cfg).Grade, ["grade_final"] = "B1-" + sg.Grade, ["exec_status"] = sg.ExecStatus,
                    ["first_cancel_cat"] = sg.FirstCat > 0 ? $"{sg.FirstCat} {BrTradeEngine.B1CatNames[sg.FirstCat]}" : "",
                    ["first_cancel_reason"] = sg.FirstReason,
                    ["all_cancel_cats"] = string.Join("+", sg.All.Select(x => x.Cat).Distinct().OrderBy(x => x)),
                    ["all_cancel_reasons"] = string.Join("；", sg.All.Select(x => $"{x.Cat}{BrTradeEngine.B1CatNames[x.Cat]}:{x.Detail}")),
                    ["ret_5d"] = N(Ret(5)), ["ret_10d"] = N(Ret(10)), ["ret_20d"] = N(Ret(20)), ["mfe_20d"] = N(mfe), ["mae_20d"] = N(mae),
                    ["b3_within_20d"] = anyB3 is null ? "否" : "是", ["b3_date"] = anyB3?.Date ?? "", ["b3_days_after"] = anyB3 is null ? "" : (anyB3.I - sg.I).ToString(Inv),
                    ["b3_exec_ok"] = firstB3ok is null ? "否" : "是", ["b3_exec_status"] = anyB3?.ExecStatus ?? "",
                    ["b3_entry_px"] = firstB3ok is null ? "" : N(firstB3ok.Entry, 3), ["b3_entry_vs_signal_close"] = firstB3ok is null ? "" : N(firstB3ok.Entry!.Value / c0 - 1),
                    ["b3_entry_vs_t1_open"] = firstB3ok is null || e1 >= q.Count ? "" : N(firstB3ok.Entry!.Value / q.Open[e1] - 1),
                    ["b3_in_watch_window"] = wb3 is null ? "否" : "是",
                    ["hypo_t1_buy_S1_net"] = hypo,
                });
            }
        }

        // ── 组合模拟 ──
        var sim = new Sim(cfg, list.ToDictionary(x => x.Code), cal);
        var periods = new[] { ("全样本", "", "9999"), ("样本内", "", split), ("样本外", split, "9999") };
        var rows = new List<Dictionary<string, string>>();
        var tradeRows = new List<Dictionary<string, string>>();
        foreach (var g in Groups)
            foreach (var sc in Schemes)
                foreach (var variant in new[] { "RS", "RF" })
                    foreach (var (pn, p0, p1) in periods)
                    {
                        bool InP(string d) => string.CompareOrdinal(d, p0) >= 0 && string.CompareOrdinal(d, p1) < 0;
                        var cs = cands.Where(c => c.Group == g && c.Scheme == sc && InP(g == "B1W" ? c.SrcB1Date : c.SigDate) && (variant == "RS" || (!double.IsNaN(c.Rr) && c.Rr >= cfg.D("trade.rr.watch", 1.5)))).ToList();
                        var nSig = g == "B1W" ? allSigs.Count(x => x.Type == "B1" && x.Grade == "B" && InP(x.Date)) : allSigs.Count(x => x.Type == g && InP(x.Date));
                        var r = sim.Run(cs, out var fills);
                        r["group"] = GroupName(g); r["scheme"] = SchemeName(sc); r["variant"] = variant == "RS" ? "RR按结构止损" : "RR按本方案止损"; r["period"] = pn;
                        r["signals"] = nSig.ToString(Inv); r["candidates"] = cs.Count.ToString(Inv);
                        r["conversion"] = nSig > 0 ? N((double)fills.Count / nSig, 3) : "";
                        rows.Add(r);
                        if (pn == "全样本")
                            foreach (var f in fills)
                                tradeRows.Add(new Dictionary<string, string>
                                {
                                    ["group"] = GroupName(g), ["scheme"] = SchemeName(sc), ["variant"] = r["variant"], ["code"] = f.C.Code, ["b1_date"] = f.C.SrcB1Date, ["signal_date"] = f.C.SigDate,
                                    ["entry_date"] = f.C.EntryDate, ["entry_px_qfq"] = N(f.C.EntryQ, 3), ["entry_px_raw"] = N(f.C.EntryRaw, 3), ["stop_px_qfq"] = N(f.C.StopQ, 3), ["risk_pct"] = N(f.C.RiskPct),
                                    ["rr"] = N(f.C.Rr, 2), ["shares"] = f.Shares.ToString(Inv), ["position_value"] = N(f.Value, 0), ["position_pct"] = N(f.PosPct), ["risk_amount"] = N(f.RiskAmt, 0),
                                    ["exit_date"] = f.C.ExitDate, ["exit_px_qfq"] = N(f.C.ExitQ, 3), ["exit_reason"] = f.C.Reason, ["net_ret"] = N(f.Net), ["pnl"] = N(f.Pnl, 0), ["r_multiple"] = N(f.RMult, 2),
                                    ["cost"] = N(f.Cost, 2), ["mae"] = N(f.C.Mae), ["mfe"] = N(f.C.Mfe), ["hold_days"] = (f.C.X - f.C.E + 1).ToString(Inv),
                                });
                    }

        // ── 输出 ──
        string[] cols = ["group", "scheme", "variant", "period", "signals", "candidates", "trades", "conversion", "win_rate", "avg_ret", "median_ret", "payoff_ratio", "avg_r", "avg_mae", "avg_mfe",
            "max_dd", "max_dd_peak", "max_dd_trough", "annual_ret", "total_ret", "total_cost", "cost_pct_capital", "avg_hold_days", "avg_position_pct", "skipped_cash", "skipped_holding", "sim_start", "sim_end"];
        BrReport.WriteCsv(Path.Combine(outDir, "bt12_summary.csv"), rows, cols);
        BrReport.WriteCsv(Path.Combine(outDir, "bt12_trades.csv"), tradeRows, ["group", "scheme", "variant", "code", "b1_date", "signal_date", "entry_date", "entry_px_qfq", "entry_px_raw", "stop_px_qfq", "risk_pct", "rr",
            "shares", "position_value", "position_pct", "risk_amount", "exit_date", "exit_px_qfq", "exit_reason", "net_ret", "pnl", "r_multiple", "cost", "mae", "mfe", "hold_days"]);
        string[] bcols = ["code", "name", "signal_date", "period", "platform", "signal_close", "t1_date", "t1_open", "t1_high", "t1_low", "t1_close", "t1_open_vs_platform", "t1_close_vs_platform", "gap_open",
            "ma20_dev", "rsi14", "rr_signal_day", "rr_t1_open", "risk_tags", "grade_pre", "grade_final", "exec_status", "first_cancel_cat", "first_cancel_reason", "all_cancel_cats", "all_cancel_reasons",
            "ret_5d", "ret_10d", "ret_20d", "mfe_20d", "mae_20d", "b3_within_20d", "b3_date", "b3_days_after", "b3_exec_ok", "b3_exec_status", "b3_entry_px", "b3_entry_vs_signal_close", "b3_entry_vs_t1_open",
            "b3_in_watch_window", "hypo_t1_buy_S1_net"];
        BrReport.WriteCsv(Path.Combine(outDir, "bt12_b1_signals.csv"), b1Rows, bcols);
        BrReport.WriteCsv(Path.Combine(outDir, "bt12_signals.csv"), allSigs.OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.I).Select(x => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["code"] = x.Code, ["type"] = x.Type, ["signal_date"] = x.Date, ["grade"] = x.Grade, ["exec_status"] = x.ExecStatus, ["exec_note"] = x.ExecNote, ["entry"] = N(x.Entry, 3),
            ["struct_stop"] = N(x.Lv?.StructStop, 3), ["atr14"] = N(x.Lv?.Atr, 3), ["resistance"] = N(x.Lv?.Resistance, 3), ["rr"] = N(x.Lv?.Rr, 2), ["risk_tags"] = string.Join("；", x.S.Tags),
        }), ["code", "type", "signal_date", "grade", "exec_status", "exec_note", "entry", "struct_stop", "atr14", "resistance", "rr", "risk_tags"]);

        var md = Report(cfg, rows, b1Rows, allSigs, sampleNote, list.Count, failed.ToList(), firstDate, lastDate, split, hold, days, out var chosen);
        File.WriteAllText(Path.Combine(outDir, "bt12_report.md"), md, new UTF8Encoding(false));
        var meta = new JsonObject
        {
            ["model_version"] = "V" + cfg0.Version, ["config"] = cfg0.Path, ["run_at"] = BrMarketData.NowCn().ToString("yyyy-MM-dd HH:mm:ss"), ["sample"] = sampleNote,
            ["stocks"] = list.Count, ["failed"] = new JsonArray(failed.Select(x => (JsonNode?)x).ToArray()), ["period"] = $"{firstDate}~{lastDate}", ["split_date"] = split,
            ["hold_days"] = hold, ["chosen_scheme_in_sample"] = chosen, ["elapsed_sec"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["codes"] = new JsonArray(list.Select(x => (JsonNode?)x.Code).ToArray()),
        };
        File.WriteAllText(Path.Combine(outDir, "bt12_meta.json"), meta.ToJsonString(BrReport.JsonOpts), new UTF8Encoding(false));
        log($"[S27 V1.2] 完成：{list.Count} 只，候选交易 {cands.Count}，B1 明细 {b1Rows.Count} 行；样本内选出的 B3 止损方案 {chosen}；用时 {sw.Elapsed.TotalSeconds:F0}s；输出 {outDir}");
    }

    private static double NetRet(BrConfig cfg, double buy, double sell)
    {
        double slip = cfg.D("backtest.slippage", 0.001), com = cfg.D("backtest.commission", 0.00025), tf = cfg.D("backtest.transfer_fee", 0.00001), tax = cfg.D("backtest.stamp_tax_sell", 0.0005);
        var b = buy * (1 + slip) * (1 + com + tf); var s = sell * (1 - slip) * (1 - com - tf - tax);
        return s / b - 1;
    }

    // ── 组合：统一初始资金 / 成本 / 单笔风险预算 ──
    private sealed class Fill { public Cand C = null!; public int Shares; public double Value, PosPct, RiskAmt, Net, Pnl, RMult, Cost; }

    private sealed class Sim(BrConfig cfg, Dictionary<string, Stock> stocks, List<string> cal)
    {
        readonly double cap0 = cfg.D("backtest_v12.initial_capital", 1_000_000), risk = cfg.D("backtest_v12.risk_per_trade", 0.0075), maxPos = cfg.D("backtest_v12.max_position_pct", 0.20);
        readonly int lot = cfg.I("backtest_v12.lot", 100);
        readonly double slip = cfg.D("backtest.slippage", 0.001), com = cfg.D("backtest.commission", 0.00025), comMin = cfg.D("backtest.commission_min", 5), tf = cfg.D("backtest.transfer_fee", 0.00001), tax = cfg.D("backtest.stamp_tax_sell", 0.0005);

        public Dictionary<string, string> Run(List<Cand> cs, out List<Fill> fills)
        {
            fills = [];
            var o = new Dictionary<string, string> { ["trades"] = "0" };
            if (cs.Count == 0) return o;
            var byEntry = cs.GroupBy(c => c.EntryDate).ToDictionary(g => g.Key, g => g.OrderByDescending(c => double.IsNaN(c.Rr) ? -1 : c.Rr).ThenBy(c => c.Code, StringComparer.Ordinal).ToList());
            var first = cs.Min(c => c.EntryDate)!; var lastExit = cs.Max(c => c.ExitDate)!;
            var days = cal.Where(d => string.CompareOrdinal(d, first) >= 0 && string.CompareOrdinal(d, lastExit) <= 0).ToList();
            double cash = cap0, eqPrev = cap0, totalCost = 0; int skipCash = 0, skipHold = 0;
            var open = new List<(Fill F, Dictionary<string, double> Px, double Buy)>();
            var curve = new List<double>();
            foreach (var d in days)
            {
                // 开盘：按前一日收盘权益定仓位（不使用当日任何价格以外的信息）
                if (byEntry.TryGetValue(d, out var todays))
                    foreach (var c in todays)
                    {
                        if (open.Any(p => p.F.C.Code == c.Code)) { skipHold++; continue; }
                        var px = c.EntryRaw * (1 + slip);
                        var perShareRisk = c.EntryRaw * c.RiskPct;
                        var sh = Math.Floor(eqPrev * risk / perShareRisk / lot) * lot;
                        sh = Math.Min(sh, Math.Floor(eqPrev * maxPos / px / lot) * lot);
                        while (sh >= lot && px * sh + Math.Max(comMin, px * sh * com) + px * sh * tf > cash) sh -= lot;
                        if (sh < lot) { skipCash++; continue; }
                        var notional = px * sh; var fee = Math.Max(comMin, notional * com) + notional * tf;
                        cash -= notional + fee;
                        var f = new Fill { C = c, Shares = (int)sh, Value = notional, PosPct = notional / eqPrev, RiskAmt = sh * perShareRisk, Cost = fee + c.EntryRaw * slip * sh };
                        open.Add((f, c.Path.ToDictionary(x => x.Date, x => x.CloseRatio), notional + fee));
                    }
                // 盘中/收盘：平仓
                for (var k = open.Count - 1; k >= 0; k--)
                {
                    var (f, pxs, buy) = open[k];
                    if (f.C.ExitDate != d) continue;
                    var sellPx = f.C.EntryRaw * (f.C.ExitQ / f.C.EntryQ) * (1 - slip);
                    var gross = sellPx * f.Shares; var fee = Math.Max(comMin, gross * com) + gross * (tf + tax);
                    cash += gross - fee;
                    f.Cost += fee + f.C.EntryRaw * (f.C.ExitQ / f.C.EntryQ) * slip * f.Shares;
                    f.Pnl = gross - fee - buy; f.Net = (gross - fee) / buy - 1; f.RMult = f.Pnl / f.RiskAmt;
                    totalCost += f.Cost; fills.Add(f); open.RemoveAt(k);
                }
                // 收盘盯市
                double mv = 0;
                foreach (var (f, pxs, _) in open)
                {
                    var ratio = pxs.TryGetValue(d, out var rr) ? rr : LastBefore(pxs, d);
                    mv += f.Shares * f.C.EntryRaw * ratio;
                }
                eqPrev = cash + mv; curve.Add(eqPrev);
            }
            if (fills.Count == 0) return o;
            var r = fills.Select(f => f.Net).ToList(); var w = r.Where(x => x > 0).ToList(); var l = r.Where(x => x <= 0).ToList();
            var so = r.OrderBy(x => x).ToList(); var med = so.Count % 2 == 1 ? so[so.Count / 2] : (so[so.Count / 2 - 1] + so[so.Count / 2]) / 2;
            double peak = cap0, mdd = 0; int pk = 0, pkAt = 0, tr = 0;
            for (var j = 0; j < curve.Count; j++) { if (curve[j] > peak) { peak = curve[j]; pk = j; } var dd = curve[j] / peak - 1; if (dd < mdd) { mdd = dd; tr = j; pkAt = pk; } }
            var fin = curve[^1]; var yrs = Math.Max(curve.Count, 1) / 252.0;
            o["trades"] = fills.Count.ToString(Inv);
            o["win_rate"] = N((double)w.Count / r.Count, 3); o["avg_ret"] = N(r.Average()); o["median_ret"] = N(med);
            o["payoff_ratio"] = w.Count > 0 && l.Count > 0 && l.Average() < 0 ? N(w.Average() / Math.Abs(l.Average()), 2) : "";
            o["avg_r"] = N(fills.Average(f => f.RMult), 3);
            o["avg_mae"] = N(fills.Average(f => f.C.Mae)); o["avg_mfe"] = N(fills.Average(f => f.C.Mfe));
            o["max_dd"] = N(mdd); o["max_dd_peak"] = mdd < 0 ? days[pkAt] : ""; o["max_dd_trough"] = mdd < 0 ? days[tr] : "";
            o["total_ret"] = N(fin / cap0 - 1); o["annual_ret"] = N(Math.Pow(fin / cap0, 1 / yrs) - 1);
            o["total_cost"] = N(totalCost, 0); o["cost_pct_capital"] = N(totalCost / cap0);
            o["avg_hold_days"] = N(fills.Average(f => f.C.X - f.C.E + 1), 1); o["avg_position_pct"] = N(fills.Average(f => f.PosPct), 3);
            o["skipped_cash"] = skipCash.ToString(Inv); o["skipped_holding"] = skipHold.ToString(Inv);
            o["sim_start"] = days[0]; o["sim_end"] = days[^1];
            return o;
        }

        private static double LastBefore(Dictionary<string, double> pxs, string d)
        {
            var best = ""; var v = 1.0;
            foreach (var kv in pxs) if (string.CompareOrdinal(kv.Key, d) <= 0 && string.CompareOrdinal(kv.Key, best) > 0) { best = kv.Key; v = kv.Value; }
            return v;
        }
    }

    // ── 报告 ──
    private static string Report(BrConfig cfg, List<Dictionary<string, string>> rows, List<Dictionary<string, string>> b1, List<Sig> sigs, string sample, int nStocks, List<string> failed,
        string d0, string d1, string split, int hold, int days, out string chosen)
    {
        string G(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) && v.Length > 0 ? v : "";
        string P(Dictionary<string, string> r, string k) { var v = G(r, k); return v.Length == 0 ? "-" : Pc(double.Parse(v, Inv)); }
        string W(Dictionary<string, string> r) { var v = G(r, "win_rate"); return v.Length == 0 ? "-" : PyFmt(double.Parse(v, Inv) * 100, 0) + "%"; }
        Dictionary<string, string>? Row(string g, string sc, string v, string p) => rows.FirstOrDefault(r => r["group"] == GroupName(g) && r["scheme"] == SchemeName(sc) && r["variant"] == v && r["period"] == p);
        const string RS = "RR按结构止损", RF = "RR按本方案止损";
        var L = new List<string>
        {
            "# S27 V1.2 实验回测（B3 止损实验 / B1 过滤分析 / 四组策略对照）", "",
            $"> 样本：{sample}；实际完成 {nStocks} 只" + (failed.Count > 0 ? $"（数据失败 {failed.Count} 只：{string.Join(",", failed.Take(10))}）" : "") + $"。区间 {d0}~{d1}（每只最多 {days} 个交易日）。**样本内**：信号日 < {split}；**样本外**：≥ {split}。",
            $"> 资金：初始 {N(cfg.D("backtest_v12.initial_capital", 1e6), 0)} 元，单笔风险预算 {PyFmt(cfg.D("backtest_v12.risk_per_trade", 0.0075) * 100, 2)}% × 前一日收盘权益，股数 = 风险预算 ÷ (买入价 − 止损价)，按 {cfg.I("backtest_v12.lot", 100)} 股取整，单票 ≤ {PyFmt(cfg.D("backtest_v12.max_position_pct", 0.2) * 100, 0)}% 权益且不超过可用现金；同一股票不重复持仓。所有组、所有止损方案用同一套资金/成本/风险预算。",
            $"> 成本：佣金 {PyFmt(cfg.D("backtest.commission") * 1e4, 1)}‱（最低 {N(cfg.D("backtest.commission_min"), 0)} 元）、过户费、卖出印花税 {PyFmt(cfg.D("backtest.stamp_tax_sell") * 1e4, 0)}‱、单边滑点 {PyFmt(cfg.D("backtest.slippage") * 100, 2)}%。成交：T 日收盘确认 → T+1 开盘买入（停牌/涨停开盘不成交）；A股 T+1 买入当日不卖；开盘已低于止损按开盘价、盘中触及按止损价、跌停封死顺延；持有 {hold} 个交易日到期按收盘价。",
            "> 信号识别统一用 V1.1 原始口径（结构止损缓冲 max(1%,0.25×ATR)，B3 需 R/R≥1.5），所以各止损方案面对同一批信号；止损方案只改变成交后的止损价和仓位。",
            "> 两种变体：**RR按结构止损**＝成交与否按 V1.1 规则（所有方案同一批成交候选）；**RR按本方案止损**＝在此基础上再要求用本方案止损重算的 R/R≥1.5（宽止损会少成交）。",
            "> 年化收益按模拟区间交易日数 /252 折算；最大回撤为组合权益（收盘盯市）最大回撤。", "",
        };

        // 一、B3 止损实验
        foreach (var v in new[] { RS, RF })
        {
            L.Add($"## 一、B3 缩量回踩 · 止损实验组（{v}）"); L.Add("");
            L.Add("| 区间 | 方案 | 交易数 | 胜率 | 平均收益 | 中位数 | 盈亏比 | 平均R | MAE | MFE | 最大回撤 | 年化 | 平均持仓(日) | 平均仓位 | 交易成本(占本金) |");
            L.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var p in new[] { "全样本", "样本内", "样本外" })
                foreach (var sc in Schemes)
                {
                    var r = Row("B3", sc, v, p); if (r is null) continue;
                    L.Add($"| {p} | {SchemeName(sc)} | {G(r, "trades")} | {W(r)} | {P(r, "avg_ret")} | {P(r, "median_ret")} | {G(r, "payoff_ratio")} | {G(r, "avg_r")} | {P(r, "avg_mae")} | {P(r, "avg_mfe")} | {P(r, "max_dd")} | {P(r, "annual_ret")} | {G(r, "avg_hold_days")} | {P(r, "avg_position_pct")} | {P(r, "cost_pct_capital")} |");
                }
            L.Add("");
        }
        // 样本内选择（不按胜率）：年化/|最大回撤|，交易数门槛
        var minT = cfg.I("backtest_v12.selection_min_trades", 30);
        double Score(Dictionary<string, string>? r)
        {
            if (r is null || !int.TryParse(G(r, "trades"), out var t) || t < minT) return double.NegativeInfinity;
            var a = double.Parse(G(r, "annual_ret"), Inv); var dd = Math.Abs(double.Parse(G(r, "max_dd").Length > 0 ? G(r, "max_dd") : "0", Inv));
            return a / Math.Max(dd, 0.01);
        }
        var ranked = Schemes.Select(sc => (sc, s: Score(Row("B3", sc, RS, "样本内")))).OrderByDescending(x => x.s).ToList();
        chosen = double.IsNegativeInfinity(ranked[0].s) ? "S1" : ranked[0].sc;
        L.Add("## 参数选择（只用样本内，指标 = 年化收益 ÷ |最大回撤|，交易数 ≥ " + minT + "；不按胜率）"); L.Add("");
        L.Add("| 方案 | 样本内得分 | 样本内年化 | 样本内最大回撤 | 样本外年化 | 样本外最大回撤 | 样本外平均R | 样本外交易数 |"); L.Add("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (sc, s) in ranked)
        {
            var i = Row("B3", sc, RS, "样本内"); var oo = Row("B3", sc, RS, "样本外");
            L.Add($"| {SchemeName(sc)}{(sc == chosen ? " ✅" : "")} | {(double.IsNegativeInfinity(s) ? "样本不足" : PyFmt(s, 2))} | {(i is null ? "-" : P(i, "annual_ret"))} | {(i is null ? "-" : P(i, "max_dd"))} | {(oo is null ? "-" : P(oo, "annual_ret"))} | {(oo is null ? "-" : P(oo, "max_dd"))} | {(oo is null ? "-" : G(oo, "avg_r"))} | {(oo is null ? "-" : G(oo, "trades"))} |");
        }
        L.Add($"\n样本内选出：**{SchemeName(chosen)}**。是否采用以样本外表现为准（见上表样本外列）。\n");

        // 五、四组对照
        foreach (var sc in new[] { "S1", chosen }.Distinct())
        {
            L.Add($"## 五、四组策略对照（{SchemeName(sc)}，{RS}）"); L.Add("");
            L.Add("| 区间 | 策略 | 信号数 | 成交数 | 转化率 | 胜率 | 平均收益 | 中位数 | 盈亏比 | 平均R | MAE | MFE | 最大回撤 | 年化 | 交易成本(元) | 成本占本金 |");
            L.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var p in new[] { "全样本", "样本内", "样本外" })
                foreach (var g in Groups)
                {
                    var r = Row(g, sc, RS, p); if (r is null) continue;
                    L.Add($"| {p} | {GroupName(g)} | {G(r, "signals")} | {G(r, "trades")} | {P(r, "conversion")} | {W(r)} | {P(r, "avg_ret")} | {P(r, "median_ret")} | {G(r, "payoff_ratio")} | {G(r, "avg_r")} | {P(r, "avg_mae")} | {P(r, "avg_mfe")} | {P(r, "max_dd")} | {P(r, "annual_ret")} | {G(r, "total_cost")} | {P(r, "cost_pct_capital")} |");
                }
            L.Add("");
        }
        L.Add("> B1 过滤后等待B3：信号数 = 被分为 B1-B（进入回踩观察池）的 B1 个数；成交 = 其后第 3–10 个交易日内第一个通过 T+1 检查的 B3。转化率 = 成交数 ÷ 信号数。\n");

        // 二、B1 取消原因
        var b1s = sigs.Where(x => x.Type == "B1").ToList();
        L.Add("## 二、B1 信号与取消原因"); L.Add("");
        L.Add($"B1 信号 {b1s.Count} 个：T+1 可介入 {b1s.Count(x => x.Entry is not null)} 个，取消 {b1s.Count(x => x.Entry is null)} 个。分级：B1-A {b1s.Count(x => x.Grade == "A")} / B1-B {b1s.Count(x => x.Grade == "B")} / B1-C {b1s.Count(x => x.Grade == "C")}。\n");
        L.Add("| 类别 | 首次取消原因（个） | 全部触发原因（个，可重复计） |"); L.Add("|---|---:|---:|");
        for (var c = 1; c <= 7; c++)
            L.Add($"| {c} {BrTradeEngine.B1CatNames[c]} | {b1s.Count(x => x.Entry is null && x.FirstCat == c)} | {b1s.Count(x => x.Entry is null && x.All.Any(a => a.Cat == c))} |");
        L.Add("\n> 1 高开过热 = T+1 高开>3% 或信号日 5日涨幅>15% / 连续放量大阳；2 = MA20 乖离>10%；3 = RSI14>75；4 = T+1 开盘重算 R/R<1.5；5 = 低开跌回平台或开盘已低于结构止损；6 = 停牌/涨停开盘；7 = 其他（止损距离>10% 等）。明细见 bt12_b1_signals.csv。\n");

        // 四、被取消 B1 的后续
        L.Add("## 四、被取消 B1 的后续表现（以信号日收盘为基准）"); L.Add("");
        L.Add("| 分组 | 个数 | 5日收益(均/中) | 10日收益(均/中) | 20日收益(均/中) | 20日MFE均值 | 20日MAE均值 | 20日内出现B3 | 其中B3可介入 | 3–10日窗口内可介入B3 | 可介入B3 买入价相对信号日收盘(均) | 若照常T+1买入·S1·持有" + hold + "日 净收益(均/胜率) |");
        L.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        double? D(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) && v.Length > 0 && double.TryParse(v, NumberStyles.Float, Inv, out var x) ? x : null;
        string MM(List<Dictionary<string, string>> rs, string k)
        {
            var v = rs.Select(r => D(r, k)).Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList();
            if (v.Count == 0) return "-";
            var med = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
            return $"{Pc(v.Average())} / {Pc(med)}";
        }
        string Avg(List<Dictionary<string, string>> rs, string k) { var v = rs.Select(r => D(r, k)).Where(x => x is not null).Select(x => x!.Value).ToList(); return v.Count == 0 ? "-" : Pc(v.Average()); }
        string Hy(List<Dictionary<string, string>> rs) { var v = rs.Select(r => D(r, "hypo_t1_buy_S1_net")).Where(x => x is not null).Select(x => x!.Value).ToList(); return v.Count == 0 ? "-" : $"{Pc(v.Average())} / {PyFmt(100.0 * v.Count(x => x > 0) / v.Count, 0)}%（{v.Count}笔）"; }
        string Cnt(List<Dictionary<string, string>> rs, string k) { var n = rs.Count(r => r[k] == "是"); return rs.Count == 0 ? "-" : $"{n}（{PyFmt(100.0 * n / rs.Count, 0)}%）"; }
        var groupsB1 = new List<(string, List<Dictionary<string, string>>)>
        {
            ("全部被取消 B1", b1.Where(r => r["grade_final"] != "B1-A").ToList()),
            ("B1-B（转回踩观察）", b1.Where(r => r["grade_final"] == "B1-B").ToList()),
            ("B1-C（取消资格）", b1.Where(r => r["grade_final"] == "B1-C").ToList()),
            ("对照：B1-A（已成交）", b1.Where(r => r["grade_final"] == "B1-A").ToList()),
        };
        for (var c = 1; c <= 7; c++) { var cc = c; var rs = b1.Where(r => r["grade_final"] != "B1-A" && r["first_cancel_cat"].StartsWith(cc + " ")).ToList(); if (rs.Count > 0) groupsB1.Add(($"首次原因 {c} {BrTradeEngine.B1CatNames[c]}", rs)); }
        foreach (var (name, rs) in groupsB1)
            L.Add($"| {name} | {rs.Count} | {MM(rs, "ret_5d")} | {MM(rs, "ret_10d")} | {MM(rs, "ret_20d")} | {Avg(rs, "mfe_20d")} | {Avg(rs, "mae_20d")} | {Cnt(rs, "b3_within_20d")} | {Cnt(rs, "b3_exec_ok")} | {Cnt(rs, "b3_in_watch_window")} | {Avg(rs, "b3_entry_vs_signal_close")} | {Hy(rs)} |");
        L.Add("\n> “若照常 T+1 买入”只用于判断过滤器是否过严：按 T+1 开盘价、S1 结构止损、持有到期、含成本的单笔净收益，不进入任何策略组合。\n");
        L.Add("_文件：bt12_summary.csv（全部 组×止损×变体×区间）、bt12_trades.csv（全样本成交明细，含股数/仓位/风险金额/R 倍数）、bt12_b1_signals.csv（每个 B1 的 T+1 数据、取消原因、后续跟踪）、bt12_signals.csv（全部信号及执行检查）、bt12_meta.json（样本代码与区间）。_");
        return string.Join("\n", L);
    }
}
