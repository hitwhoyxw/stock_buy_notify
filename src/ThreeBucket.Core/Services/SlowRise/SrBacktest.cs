using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThreeBucket.Core.Services.BottomReversal;

namespace ThreeBucket.Core.Services.SlowRise;

/// <summary>
/// 底部反转缓升回测（说明书 §11）：抽样股票 × 2015 起日线；T 日收盘出信号、T+1 开盘成交；组合 = 风险预算 0.8%/笔、单票 ≤10%、最多 10 只、每日新开 ≤3；
/// 训练期（信号/入场日 ≤ train_end）与样本外分开统计；参数扫描只按训练期挑选（样本外只检验），消融逐个关闭子条件。输出 data/bottom_slowrise/&lt;backtest.out_subdir&gt;/（v2 为 backtest_v2）。
/// </summary>
public static class SrBacktest
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, int nd = 2) => SrParams.F(v, nd);
    private static string P(double v, int nd = 1) => SrParams.P(v, nd);
    private static string Pu(double v, int nd = 1) => SrParams.Pu(v, nd);

    public sealed class Sig
    {
        public string Code = "", Date = "", Kind = ""; public int I; public SrEval Ev = null!; public SrStock S = null!;
        public string Outcome = ""; public bool Fillable;
        public double[] Ret = [], Mae = [], Mfe = [];
    }

    public sealed class Funnel
    {
        public Dictionary<int, long[]> Days = new();                  // 年 → [L0,L1,L2,L3,池内天数,ENTER,BUY,BUY_BLOCKED]
        public Dictionary<int, HashSet<string>[]> Stocks = new();     // 年 → 每层出现过的股票
        public long[] F0 = new long[7], F1 = new long[6], F2 = new long[6], F3 = new long[9], F4 = new long[7], FB = new long[5]; public long F3Buy;
        public long[] First1 = new long[6], First2 = new long[6];
        public long PoolDays, PoolNoBuy;
        public long[] Ladder = new long[6]; public List<double> LadderScores = new();   // 池内天：[池内, L3达BUY, +L4全过, +B4~B6(不含评分), +评分, L4全过(不看L3)]
        public Dictionary<string, long> Removes = new();
        public Dictionary<int, long> Key = new();   // 年 → 观察池内“重点观察”（a 已出现、b 未出现）的股票·交易日
        public void Merge(Funnel o)
        {
            foreach (var (y, a) in o.Days) { if (!Days.TryGetValue(y, out var b)) Days[y] = b = new long[8]; for (var k = 0; k < 8; k++) b[k] += a[k]; }
            foreach (var (y, a) in o.Stocks) { if (!Stocks.TryGetValue(y, out var b)) Stocks[y] = b = Enumerable.Range(0, 8).Select(_ => new HashSet<string>()).ToArray(); for (var k = 0; k < 8; k++) b[k].UnionWith(a[k]); }
            void M(long[] x, long[] y) { for (var k = 0; k < x.Length; k++) x[k] += y[k]; }
            M(F0, o.F0); M(F1, o.F1); M(F2, o.F2); M(F3, o.F3); M(F4, o.F4); M(FB, o.FB); M(First1, o.First1); M(First2, o.First2);
            for (var k = 0; k < Ladder.Length; k++) Ladder[k] += o.Ladder[k]; LadderScores.AddRange(o.LadderScores);
            F3Buy += o.F3Buy; PoolDays += o.PoolDays; PoolNoBuy += o.PoolNoBuy;
            foreach (var (k, v) in o.Removes) Removes[k] = Removes.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.Key) Key[k] = Key.GetValueOrDefault(k) + v;
        }
    }

    public sealed class Run
    {
        public string Name = "", Group = "", Change = ""; public SrParams P = null!;
        public SrBook Book = null!; public List<Sig> Sigs = []; public Funnel Fn = new();
        public System.Collections.Concurrent.ConcurrentDictionary<string, Attr> PerStock = new();
    }
    /// <summary>逐只股票的过滤归因：每个 L0 通过的交易日记“第一个没通过的条件”（L1→L2→③→④→买入附加），以及各层通过天数、进池与 BUY 次数。</summary>
    public sealed class Attr
    {
        public string Code = "", Name = "", Industry = ""; public long Days, L0, L1, L2, L3, Enter, Buy, Key;
        public Dictionary<string, long> First = new();
    }

    public sealed class Stat
    {
        public double Ann = double.NaN, Mdd = double.NaN, Sharpe = double.NaN, Calmar = double.NaN, Total = double.NaN, Win = double.NaN, Plr = double.NaN, AvgRet = double.NaN, AvgHold = double.NaN;
        public int Trades, Days;
    }

    // ── 单次运行：逐只股票跑观察池状态机（并行）→ 组合逐日 ──
    public static Run Execute(string name, string group, string change, SrParams p, List<SrStock> stocks, SrLoad.Ctx ctx, string start, bool keepSigs)
    {
        var run = new Run { Name = name, Group = group, Change = change, P = p };
        var sigs = new ConcurrentBag<Sig>(); var fl = new object();
        Parallel.ForEach(stocks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) }, s =>
        {
            s.EnsureMa(p);
            var at = new Attr { Code = s.Code, Name = s.Name, Industry = s.Industry };
            var fn = new Funnel(); SrPoolEntry? entry = null; var evs = new List<SrPoolEvent>();
            var i0 = 1; while (i0 < s.Count && string.CompareOrdinal(s.Date[i0], start) < 0) i0++;
            for (var i = i0; i < s.Count; i++)
            {
                evs.Clear();
                var wasIn = entry is not null && !entry.Bought;
                entry = SrPool.Step(s, i, entry, p, ctx.Hs300, evs, out var ev, false, p.BacktestUseCap);
                var y = int.Parse(s.Date[i][..4], Inv);
                if (!fn.Days.TryGetValue(y, out var a)) { fn.Days[y] = a = new long[8]; fn.Stocks[y] = Enumerable.Range(0, 8).Select(_ => new HashSet<string>()).ToArray(); }
                var st = fn.Stocks[y];
                void Hit(int k) { a[k]++; st[k].Add(s.Code); }
                if (ev.L0) { Hit(0); if (ev.L1) { Hit(1); if (ev.L2) { Hit(2); if (ev.L3Watch) Hit(3); } } }
                if (keepSigs)
                {
                    at.Days++;
                    if (ev.L0)
                    {
                        at.L0++; string why;
                        static int Tz(int m) => System.Numerics.BitOperations.TrailingZeroCount(m);
                        if (!ev.L1) why = SrParams.L1Names[Tz(ev.M1)];
                        else { at.L1++;
                            if (!ev.L2) why = ev.M2 != 0 ? SrParams.L2Names[Tz(ev.M2)] : "② 无满足的 D0";
                            else { at.L2++;
                                if (!ev.L3Watch) why = ev.M3 != 0 ? SrParams.L3Names[Tz(ev.M3)] : "③ 项数不足";
                                else { at.L3++;
                                    if (!ev.L4) why = "④ " + (ev.M4 != 0 ? p.L4N[Math.Min(Tz(ev.M4), p.L4N.Length - 1)] : "未完成");
                                    else if (!ev.BuyCond) why = ev.MB != 0 ? SrParams.BuyNames[Tz(ev.MB)] : "买入附加";
                                    else why = "全部通过";
                                } } }
                        at.First[why] = at.First.GetValueOrDefault(why) + 1;
                    }
                    if (entry is not null && !entry.Bought && ev.KeyWatch) at.Key++;
                    foreach (var x in evs) { if (x.Kind == "ENTER") at.Enter++; else if (x.Kind is "BUY" or "BUY_BLOCKED") at.Buy++; }
                }
                // 被哪个条件过滤：上一层通过、本层未通过时统计（可重复计）与“首个”未通过条件
                for (var k = 0; k < 7; k++) if ((ev.M0 & (1 << k)) != 0) fn.F0[k]++;
                if (ev.L0 && !ev.L1) { for (var k = 0; k < 6; k++) if ((ev.M1 & (1 << k)) != 0) fn.F1[k]++; fn.First1[System.Numerics.BitOperations.TrailingZeroCount(ev.M1)]++; }
                if (ev.L0 && ev.L1 && !ev.L2) { for (var k = 0; k < 6; k++) if ((ev.M2 & (1 << k)) != 0) fn.F2[k]++; if (ev.M2 != 0) fn.First2[System.Numerics.BitOperations.TrailingZeroCount(ev.M2)]++; }
                if (ev.L0 && ev.L1 && ev.L2 && !ev.L3Watch) for (var k = 0; k < 9; k++) if ((ev.M3 & (1 << k)) != 0) fn.F3[k]++;
                if (evs.Any(x => x.Kind is "WATCH" or "BUY" or "BUY_BLOCKED"))
                {
                    fn.Ladder[0]++; if (ev.L4) fn.Ladder[5]++;
                    if (ev.L3Buy) { fn.Ladder[1]++; if (ev.L4) { fn.Ladder[2]++; if ((ev.MB & ~(1 << 4)) == 0) { fn.Ladder[3]++; fn.LadderScores.Add(ev.Score); if (ev.BuyCond) fn.Ladder[4]++; } } }
                }
                foreach (var x in evs)
                {
                    switch (x.Kind)
                    {
                        case "ENTER": Hit(5); break;
                        case "REMOVE": foreach (var r in x.Note.Split('、')) fn.Removes[r] = fn.Removes.GetValueOrDefault(r) + 1; break;
                        case "WATCH":
                            fn.PoolNoBuy++;
                            if (!ev.L3Buy) fn.F3Buy++;
                            for (var k = 0; k < 7; k++) if ((ev.M4 & (1 << k)) != 0) fn.F4[k]++;
                            for (var k = 0; k < 5; k++) if ((ev.MB & (1 << k)) != 0) fn.FB[k]++;
                            break;
                        case "BUY": Hit(6); break;
                        case "BUY_BLOCKED": Hit(7); break;
                    }
                    if (x.Kind is "BUY" or "BUY_BLOCKED")
                        sigs.Add(new Sig { Code = s.Code, Date = s.Date[i], Kind = x.Kind, I = i, Ev = ev, S = s });
                }
                if ((entry is not null && !entry.Bought) || wasIn) { Hit(4); fn.PoolDays++; }
                if (entry is not null && !entry.Bought && ev.KeyWatch) fn.Key[y] = fn.Key.GetValueOrDefault(y) + 1;
            }
            lock (fl) run.Fn.Merge(fn);
            if (keepSigs) run.PerStock[s.Code] = at;
        });
        var byDate = sigs.Where(x => x.Kind == "BUY").GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.ToList());
        var map = stocks.ToDictionary(x => x.Code);
        var book = SrBook.New(p);
        var d0 = ctx.Cal.FindIndex(d => string.CompareOrdinal(d, start) >= 0);
        for (var j = d0; j < ctx.Cal.Count; j++)
        {
            var date = ctx.Cal[j];
            var cands = byDate.TryGetValue(date, out var l) ? l.Select(x => new SrBuyCand { Code = x.Code, Ev = x.Ev, S = x.S }).ToList() : [];
            book.Day(date, ctx.CalIx, c => map.GetValueOrDefault(c), cands, ctx.Hs300, p);
            if (l is not null) foreach (var x in l) x.Outcome = cands.First(c => c.Code == x.Code).Outcome;
        }
        book.CloseAllAtEnd(ctx.Cal[^1], ctx.CalIx, c => map.GetValueOrDefault(c), p);
        run.Book = book;
        if (keepSigs)
        {
            var fd = p.Cfg.Doubles("backtest.forward_days").Select(x => (int)x).DefaultIfEmpty(20).ToArray();
            foreach (var x in sigs)
            {
                var s = x.S; var e = x.I + 1;
                x.Fillable = e < s.Count && s.Date[e] == (ctx.CalIx.TryGetValue(x.Date, out var cp) && cp + 1 < ctx.Cal.Count ? ctx.Cal[cp + 1] : "") && s.RO[e] < Math.Round(s.RefPrev[e] * (1 + s.Lp), 2) - 0.001;
                x.Ret = new double[fd.Length]; x.Mae = new double[fd.Length]; x.Mfe = new double[fd.Length];
                for (var k = 0; k < fd.Length; k++)
                {
                    var end = x.I + fd[k];
                    if (!x.Fillable || end >= s.Count) { x.Ret[k] = x.Mae[k] = x.Mfe[k] = double.NaN; continue; }
                    double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                    for (var t = e; t <= end; t++) { lo = Math.Min(lo, s.L[t]); hi = Math.Max(hi, s.H[t]); }
                    x.Ret[k] = s.C[end] / s.O[e] - 1; x.Mae[k] = lo / s.O[e] - 1; x.Mfe[k] = hi / s.O[e] - 1;
                }
            }
            run.Sigs = sigs.OrderBy(x => x.Date, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToList();
        }
        else run.Sigs = sigs.ToList();
        return run;
    }

    // ── 统计 ──
    public static Stat Stats(SrBook b, string from, string to, double capital)
    {
        var st = new Stat();
        var idx = Enumerable.Range(0, b.EquityDates.Count).Where(j => string.CompareOrdinal(b.EquityDates[j], from) >= 0 && string.CompareOrdinal(b.EquityDates[j], to) <= 0).ToList();
        if (idx.Count > 1)
        {
            var e0 = idx[0] > 0 ? b.Equity[idx[0] - 1] : capital;
            var eq = idx.Select(j => b.Equity[j]).ToList();
            st.Days = eq.Count; st.Total = eq[^1] / e0 - 1; st.Ann = Math.Pow(eq[^1] / e0, 252.0 / eq.Count) - 1;
            double peak = e0, mdd = 0; foreach (var v in eq) { peak = Math.Max(peak, v); mdd = Math.Min(mdd, v / peak - 1); }
            st.Mdd = mdd;
            var rets = new List<double>(); var prev = e0; foreach (var v in eq) { rets.Add(v / prev - 1); prev = v; }
            var m = rets.Average(); var sd = Math.Sqrt(rets.Sum(x => (x - m) * (x - m)) / Math.Max(1, rets.Count - 1));
            st.Sharpe = sd > 0 ? m / sd * Math.Sqrt(252) : double.NaN; st.Calmar = mdd < 0 ? st.Ann / -mdd : double.NaN;
        }
        var tr = b.Closed.Where(t => string.CompareOrdinal(t.EntryDate, from) >= 0 && string.CompareOrdinal(t.EntryDate, to) <= 0).ToList();
        st.Trades = tr.Count;
        if (tr.Count > 0)
        {
            var w = tr.Where(t => t.Ret > 0).ToList(); var l = tr.Where(t => t.Ret <= 0).ToList();
            st.Win = (double)w.Count / tr.Count; st.AvgRet = tr.Average(t => t.Ret); st.AvgHold = tr.Average(t => t.HoldDays);
            st.Plr = w.Count > 0 && l.Count > 0 && l.Average(t => t.Ret) < 0 ? w.Average(t => t.Ret) / -l.Average(t => t.Ret) : double.NaN;
        }
        return st;
    }

    public static Stat IndexStats(SrData.Index? x, string from, string to)
    {
        var st = new Stat(); if (x is null) return st;
        var ix = Enumerable.Range(0, x.D.Length).Where(j => string.CompareOrdinal(x.D[j], from) >= 0 && string.CompareOrdinal(x.D[j], to) <= 0).ToList();
        if (ix.Count < 2) return st;
        var e0 = ix[0] > 0 ? x.C[ix[0] - 1] : x.C[ix[0]];
        var eq = ix.Select(j => x.C[j]).ToList();
        st.Days = eq.Count; st.Total = eq[^1] / e0 - 1; st.Ann = Math.Pow(eq[^1] / e0, 252.0 / eq.Count) - 1;
        double peak = e0, mdd = 0; foreach (var v in eq) { peak = Math.Max(peak, v); mdd = Math.Min(mdd, v / peak - 1); }
        st.Mdd = mdd; st.Calmar = mdd < 0 ? st.Ann / -mdd : double.NaN;
        var rets = new List<double>(); var prev = e0; foreach (var v in eq) { rets.Add(v / prev - 1); prev = v; }
        var m = rets.Average(); var sd = Math.Sqrt(rets.Sum(r => (r - m) * (r - m)) / Math.Max(1, rets.Count - 1)); st.Sharpe = sd > 0 ? m / sd * Math.Sqrt(252) : double.NaN;
        return st;
    }

    private static double Median(IEnumerable<double> xs) { var a = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray(); return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
    private static double Mean(IEnumerable<double> xs) { var a = xs.Where(x => !double.IsNaN(x)).ToArray(); return a.Length == 0 ? double.NaN : a.Average(); }

    /// <summary>
    /// 样本（t36）：当前股票列表剔除北交所 / ST / 退市 / 上市不足 min_list_days，按行业分层随机抽样（固定种子，按各行业股票数比例分配名额，最大余数法），
    /// 不按近期表现挑选、不强制加入任何股票。method=random 时为简单随机。种子与方法写入 sr_meta.json。
    /// </summary>
    public static (List<string> Codes, string Note) Sample(SrParams p, Dictionary<string, BrMarketData.UniverseItem> uni, int n, string day)
    {
        var seed = p.Cfg.I("backtest.seed", 20261110); var method = p.Cfg.S("backtest.sample_method", "industry_stratified_random");
        var minList = DateTime.ParseExact(day, "yyyy-MM-dd", Inv).AddDays(-p.MinListDays * 1.45).ToString("yyyyMMdd");
        var pool = uni.Values.Where(x => x.Board != "bj" && !x.Name.Contains("ST", StringComparison.OrdinalIgnoreCase) && !x.Name.Contains('退') && x.ListDate.Length == 8 && string.CompareOrdinal(x.ListDate, minList) <= 0)
            .OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
        var rnd = new Random(seed);
        List<string> pick;
        if (method == "random") pick = pool.OrderBy(_ => rnd.Next()).Take(n).Select(x => x.Code).ToList();
        else
        {
            var groups = pool.GroupBy(x => x.Industry.Length > 0 ? x.Industry : "未分类").OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            var quota = groups.Select(g => (G: g, Q: (double)g.Count() * n / pool.Count)).ToList();
            var alloc = quota.ToDictionary(x => x.G.Key, x => (int)Math.Floor(x.Q));
            foreach (var x in quota.OrderByDescending(x => x.Q - Math.Floor(x.Q)).ThenBy(x => x.G.Key, StringComparer.Ordinal).Take(n - alloc.Values.Sum())) alloc[x.G.Key]++;
            pick = [];
            foreach (var g in groups) pick.AddRange(g.OrderBy(_ => rnd.Next()).Take(alloc[g.Key]).Select(x => x.Code));
            pick = pick.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        var note = $"当前股票列表 {uni.Count} 只剔除北交所/ST/退市/上市晚于 {minList} 后 {pool.Count} 只，{(method == "random" ? "简单随机" : $"按行业分层随机（{pool.Select(x => x.Industry).Distinct().Count()} 个行业，按股票数比例分配名额）")}抽取 {pick.Count} 只，种子 {seed}；未按近期表现挑选、未强制加入任何股票";
        return (pick, note);
    }

    /// <summary>同池等权买入持有基准：区间首日按等权买入当时已有数据的样本股（复权价），持有到区间末。Trades 字段记参与股票数。</summary>
    public static Stat EqualWeight(List<SrStock> stocks, SrLoad.Ctx ctx, string from, string to)
    {
        var st = new Stat();
        var days = ctx.Cal.Where(d => string.CompareOrdinal(d, from) >= 0 && string.CompareOrdinal(d, to) <= 0).ToList();
        if (days.Count < 2) return st;
        var d0 = days[0];
        var mem = stocks.Where(s => s.Count > 0 && string.CompareOrdinal(s.Date[0], d0) < 0).Select(s => (S: s, B: LastC(s, ctx.Cal[Math.Max(0, ctx.CalIx[d0] - 1)]))).Where(x => x.B > 0).ToList();
        if (mem.Count == 0) return st;
        var eq = days.Select(d => mem.Average(x => LastC(x.S, d) / x.B)).ToList();
        st.Trades = mem.Count; st.Days = eq.Count; st.Total = eq[^1] - 1; st.Ann = Math.Pow(eq[^1], 252.0 / eq.Count) - 1;
        double peak = 1, mdd = 0; foreach (var v in eq) { peak = Math.Max(peak, v); mdd = Math.Min(mdd, v / peak - 1); }
        st.Mdd = mdd; st.Calmar = mdd < 0 ? st.Ann / -mdd : double.NaN;
        var rets = new List<double>(); var prev = 1.0; foreach (var v in eq) { rets.Add(v / prev - 1); prev = v; }
        var m = rets.Average(); var sd = Math.Sqrt(rets.Sum(r => (r - m) * (r - m)) / Math.Max(1, rets.Count - 1)); st.Sharpe = sd > 0 ? m / sd * Math.Sqrt(252) : double.NaN;
        return st;
        static double LastC(SrStock s, string d)
        {
            var lo = 0; var hi = s.Count - 1; var ans = -1;
            while (lo <= hi) { var mid = (lo + hi) / 2; if (string.CompareOrdinal(s.Date[mid], d) <= 0) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
            return ans < 0 ? double.NaN : s.C[ans];
        }
    }

    // ── 入口 ──
    public static async Task RunAsync(string dataDir, IReadOnlyList<string> codesArg, int limit, string? outArg, Action<string> log, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = SrParams.Load(dataDir);
        log($"[S28] 参数 {p.Cfg.Path}（version {p.Version}）");
        var outDir = outArg ?? Path.Combine(dataDir, "bottom_slowrise", p.Cfg.S("backtest.out_subdir", "backtest") + (codesArg.Count > 0 ? $"_codes{codesArg.Count}" : ""));
        Directory.CreateDirectory(outDir);
        var cacheDir = Path.Combine(dataDir, "cache", "sr_kline"); Directory.CreateDirectory(cacheDir);
        var start = p.Cfg.S("backtest.start", "2015-01-01"); var trainEnd = p.Cfg.S("backtest.train_end", "2021-12-31");
        var oosStart = DateTime.ParseExact(trainEnd, "yyyy-MM-dd", Inv).AddDays(1).ToString("yyyy-MM-dd");
        var warm = p.Cfg.I("backtest.warm_bars", 300);

        var ctx = await SrLoad.CalendarAsync(p, 6000, cacheDir, log, ct).ConfigureAwait(false);
        var si = ctx.Cal.FindIndex(d => string.CompareOrdinal(d, start) >= 0);
        var lmt = ctx.Cal.Count - Math.Max(0, si - warm) + 5;
        var uni = await SrLoad.UniverseAsync(p, dataDir, log, ct).ConfigureAwait(false);

        List<string> codes; string sampleNote;
        if (codesArg.Count > 0) { codes = codesArg.ToList(); sampleNote = $"指定 {codes.Count} 只"; }
        else
        {
            (codes, sampleNote) = Sample(p, uni, limit > 0 ? limit : p.Cfg.I("backtest.sample_size", 400), ctx.Day);
            if (p.Cfg.Node("backtest.include_codes") is JsonArray inc) foreach (var c in inc) { var s = c!.GetValue<string>(); if (!codes.Contains(s)) codes.Add(s); }
        }
        log($"[S28] 样本：{sampleNote}；回测 {start}~{ctx.Day}（训练期 ≤{trainEnd}，样本外 ≥{oosStart}）；每只取 {lmt} 根日K（含 {warm} 根预热）");
        var (stocks, failed) = await SrLoad.StocksAsync(p, codes, uni, ctx, lmt, cacheDir, log, ct).ConfigureAwait(false);
        var adjEv = stocks.Sum(x => x.AdjEvents);
        var firstBars = stocks.Select(x => x.Date[0]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        log($"[S28] 数据完成 {stocks.Count} 只（失败 {failed.Count}）；除权调整事件 {adjEv} 个；用时 {sw.Elapsed.TotalSeconds:F0}s");

        // ── 运行：基准 + 变体 + 消融 ──
        var runs = new List<Run>();
        Run Go(string name, string group, string change, SrParams pp, bool keep = false)
        {
            var r = Execute(name, group, change, pp, stocks, ctx, start, keep); runs.Add(r);
            var stt = Stats(r.Book, start, ctx.Day, pp.Capital);
            log($"[S28] {group} {name}：BUY 信号 {r.Sigs.Count(x => x.Kind == "BUY")}，成交 {r.Book.Closed.Count}，年化 {P(stt.Ann)}，最大回撤 {P(stt.Mdd)}（{sw.Elapsed.TotalSeconds:F0}s）");
            return r;
        }
        var bas = Go("基准（t37 默认：L4 三段式 a→b→c，MA10/30，有效期 D0 起 30 日，无评分门槛，按 ER 排序）", "基准", "", p, true);
        var noGate = bas;
        // ── t37 第一轮消融：a / b / c / 整个 ③ 缓慢爬升 ──
        var l3Off = p; foreach (var k in SrParams.L3Keys) l3Off = l3Off.With($"l3.on.{k}", false);
        Go("只关 a（只要均线金叉）", "消融R1", "l4.enable_a=false", p.With("l4.enable_a", false), true);
        Go("只关 b（只要 MACD 底部金叉）", "消融R1", "l4.enable_b=false", p.With("l4.enable_b", false), true);
        Go("只关 c（不要求 DIF>DEA 且 C>MA_L）", "消融R1", "l4.enable_c=false", p.With("l4.enable_c", false), true);
        Go("只关 ③ 缓慢爬升（L3 九项全部视为通过）", "消融R1", "l3.on.*=false", l3Off, true);
        // ── t37 均线组合（回测内按变体重算均线）──
        Go("均线 5/20", "均线", "l4.ma_short=5, ma_long=20", p.With("l4.ma_short", 5.0).With("l4.ma_long", 20.0), true);
        Go("均线 10/60", "均线", "l4.ma_short=10, ma_long=60", p.With("l4.ma_short", 10.0).With("l4.ma_long", 60.0), true);
        Go("均线 10/60 + 有效期 D0 起 40 日", "均线", "+ watch.valid_days_from_d0=40", p.With("l4.ma_short", 10.0).With("l4.ma_long", 60.0).With("watch.valid_days_from_d0", 40.0), true);
        // ── v2.0 优先参数扫描（一次只改一项；只按训练期挑，样本外只做检验）──
        if (p.Cfg.B("backtest.param_scan", true))
        {
            foreach (var v in new[] { 0.30, 0.50 }) Go($"DD_MIN {v:P0}", "参数扫描", "l1.dd_min", p.With("l1.dd_min", v), true);
            foreach (var v in new[] { 1.5, 2.5 }) Go($"放量倍数 {v:0.0}", "参数扫描", "l2.vol_mult", p.With("l2.vol_mult", v), true);
            foreach (var v in new[] { 0.06, 0.10 }) Go($"止损 {v:P0}", "参数扫描", "exit.hard_stop", p.With("exit.hard_stop", v), true);
        }
        var variants = new List<(string Name, string Path, JsonNode? V)>
        {
            ("止损=ATR14×2.5（上限−12%）", "exit.stop_mode", "atr"),
            ("仓位=等权10%", "position.sizing", "equal"),
            ("L3 窗口=固定最近20日", "l3.window_mode", "fixed"),
            ("1.5 低点抬高=近10日内 T_low 之后（t34）", "l1.low_lift_mode", "after_low_last10"),
            ("开启评分≥70 门槛", "buy.on.score_gate", true),
            ("按评分排序（代替 ER）", "buy.rank_by", "score"),
            ("关闭大盘过滤", "buy.market_filter.enabled", false),
            ("大盘过滤 v1 口径（MA60 或 5日跌幅>−5%）", "buy.market_filter.logic", "or"),
            ("④ 用 v2.0 原文（近5日双金叉、不分先后）+ 进池起 20 日", "l4.mode", null),
            ("b 不要求在 T_macd 之后（require_order=false）", "l4.require_order", false),
            ("不允许同日 WATCH→BUY", "watch.allow_same_day_buy", false),
        };
        foreach (var (nm, path, v) in variants)
        {
            SrParams pp;
            if (nm.StartsWith("④ 用 v2.0 原文")) pp = p.With("l4.mode", "legacy").With("watch.valid_days_from_d0", 0.0).With("watch.valid_days", 20.0)
                    .With("l4.cross_lookback", 5.0).With("l4.on.ma_long_slope", false).With("l4.on.macd_hist", false).With("l4.on.resonance", false);
            else pp = p.With(path, v);
            Go(nm, "变体", path, pp);
        }
        // 信号数检查（t36）：年均 BUY < 20 时跑放宽实验（只放宽 L3 满足项数、L1 DD_MIN，不加条件）
        var yrsB = Math.Max(1, (DateTime.ParseExact(ctx.Day, "yyyy-MM-dd", Inv) - DateTime.ParseExact(start, "yyyy-MM-dd", Inv)).TotalDays / 365.25);
        var buyPerYear = bas.Sigs.Count(x => x.Kind == "BUY") / yrsB;
        if (buyPerYear < p.Cfg.D("backtest.min_buy_per_year", 20) || p.Cfg.B("backtest.always_relax", false))
        {
            log($"[S28] 年均 BUY 信号 {buyPerYear:F1} 个 < {p.Cfg.D("backtest.min_buy_per_year", 20)}，运行放宽实验");
            // v2.0：年均 BUY<20 先放宽 ③ 和回撤，不加条件。③ 开启 4 项（其余 5 项关闭视为通过，共 9 项），“≥8/9” = 允许 1 项不满足
            Go("③ 允许 1 项不满足（≥8/9）", "放宽", "l3.watch/buy_min_count=8", p.With("l3.watch_min_count", 8.0).With("l3.buy_min_count", 8.0), true);
            Go("DD_MIN 35%", "放宽", "l1.dd_min=0.35", p.With("l1.dd_min", 0.35), true);
            Go("DD_MIN 30%", "放宽", "l1.dd_min=0.30", p.With("l1.dd_min", 0.30), true);
            Go("③ ≥8/9 + DD_MIN 35%", "放宽", "两项", p.With("l3.watch_min_count", 8.0).With("l3.buy_min_count", 8.0).With("l1.dd_min", 0.35), true);
            Go("③ ≥8/9 + DD_MIN 30%", "放宽", "两项", p.With("l3.watch_min_count", 8.0).With("l3.buy_min_count", 8.0).With("l1.dd_min", 0.30), true);
        }
        if (p.Cfg.B("backtest.ablation", true))
        {
            // 只消融默认开启的子条件（v2.0 已删除的条件默认关闭，不再消融）；④ a/b/c 与整个 ③ 见“消融R1”
            void Abl(string sec, string[] keys, string[] names, bool[] on) { for (var k = 0; k < keys.Length; k++) if (on[k]) Go("关闭 " + names[k], "消融", $"{sec}.on.{keys[k]}", p.With($"{sec}.on.{keys[k]}", false)); }
            Abl("l1", SrParams.L1Keys, SrParams.L1Names, p.L1On); Abl("l2", SrParams.L2Keys, SrParams.L2Names, p.L2On); Abl("l3", SrParams.L3Keys, SrParams.L3Names, p.L3On);
            Abl("buy", ["max_pct", "max_ma20_dev", "vma5", "no_limit_up_close", "score_gate"], SrParams.BuyNames, p.BuyOn);   // ④ 买入附加过滤（大盘过滤见变体“关闭大盘过滤”）
        }

        // ── 输出 ──
        Write(outDir, p, bas, noGate, runs, stocks, failed, ctx, start, trainEnd, oosStart, sampleNote, lmt, adjEv, firstBars, sw.Elapsed.TotalSeconds);
        log($"[S28] 回测完成：{stocks.Count} 只，{runs.Count} 组运行；用时 {sw.Elapsed.TotalSeconds:F0}s；输出 {outDir}");
    }

    private static void Write(string outDir, SrParams p, Run bas, Run? noGate, List<Run> runs, List<SrStock> stocks, List<string> failed, SrLoad.Ctx ctx,
        string start, string trainEnd, string oosStart, string sampleNote, int lmt, int adjEv, List<string> firstBars, double secs)
    {
        var end = ctx.Day; var cap = p.Capital;
        var per = new[] { ("全区间", start, end), ("训练期", start, trainEnd), ("样本外", oosStart, end) };
        var md = new StringBuilder();
        md.AppendLine(p.Version.StartsWith("2") ? $"# S28 底部反转缓升 · 回测报告（精简版 v{p.Version} + t37 修订，配置 {SrParams.FileName}）" : $"# S28 底部反转缓升 · 回测报告（说明书 v{p.Version} + t34/t37 修订）").AppendLine();
        md.AppendLine($"> 样本：{sampleNote}；实际完成 {stocks.Count} 只（失败 {failed.Count}{(failed.Count > 0 ? "：" + string.Join(",", failed.Take(10)) : "")}）。");
        md.AppendLine($"> 区间：{start} ~ {end}（日历 = 沪深300 交易日）；训练期 {start}~{trainEnd}，样本外 {oosStart}~{end}（按入场日划分交易；权益曲线按日期切段）。");
        md.AppendLine($"> 数据：东财 push2his 日K（不复权 fqt=0 + 前复权 fqt=1，等比复权由两者反推，除权事件 {adjEv} 个）；每只最多 {lmt} 根；个股最早数据日 {firstBars.FirstOrDefault()}，有 {firstBars.Count(d => string.CompareOrdinal(d, start) > 0)} 只在 {start} 之后才有数据（上市较晚，从上市满 {p.MinListDays} 日起参与）。");
        md.AppendLine($"> 规则：T 日收盘出信号 → T+1 开盘成交（开盘涨停放弃，停牌放弃）；卖出 T+1 开盘执行，跌停封死顺延；成本 佣金 {p.Comm * 1e4:F1}‱（最低 {p.CommMin} 元）+ 卖出印花税 {p.Stamp:P2} + 单边滑点 {p.Slip:P1}。");
        md.AppendLine($"> 仓位：{(p.Sizing == "equal" ? $"等权 {p.EqualPct:P0}" : $"风险预算 {p.RiskPer:P1}×前一日收盘权益 ÷（买入价×止损幅度），单票 ≤{p.MaxPosPct:P0}")}，最多 {p.MaxPos} 只，每日新开 ≤{p.MaxNew}，{(p.IndCap ? $"同行业 ≤{p.IndMax}（当前行业），" : "")}卖出后 {p.Cooldown} 日冷却；沪深300 弱势（<MA60 且 MA60 下行）新仓{(p.WeakMode == "half" ? "减半" : p.WeakMode == "pause" ? "暂停" : "不变")}。初始资金 {cap:N0}。");
        var anyR = p.ROn.Any(x => x);
        md.AppendLine($"> 止损：止损价 = min(D0 最低价, 买入价×(1−{p.HardStop:P0}))，最宽不超过 −{p.MaxStop:P0}" + (anyR ? $"；R4（首次收盘跌破 MA10）上移止损；REDUCE R1~R3 首次触发次日开盘卖 {p.SellFrac:P0}。" : "；无减仓（REDUCE/R1~R4 关闭）。") +
            $" 开启的卖出规则：{string.Join("、", Enumerable.Range(0, p.SOn.Length).Where(k => p.SOn[k]).Select(k => SrParams.ExitNames[k]))}；收盘触发、次日开盘清仓。");
        md.AppendLine($"> ④（t37）：{(p.L4Mode == "legacy" ? "旧 4.1~4.7" : $"有序三段 a MACD 底部金叉（D0{p.MacdFromD0Off:+0;-0}日起，金叉日 DIF/C≤{p.DifRatioMax:P0}）→ b MA{p.MaS}/{p.MaL} 金叉（≤{p.MaCrossMaxAge} 日内，{(p.RequireOrder ? "不早于 a" : "不要求先后")}）→ c DIF>DEA 且 C>MA{p.MaL}")}；观察池有效期 {(p.ValidFromD0 > 0 ? $"自 D0 起 {p.ValidFromD0} 个交易日" : $"进池起 {p.WatchValid} 日")}。");
        md.AppendLine(p.BuyOn[4] ? $"> 评分：≥{p.ScoreMin} 才允许 BUY，同日按{(p.RankBy == "score" ? "分数" : "ER")}取前 {p.MaxNew}。" : $"> 评分：不参与选股（只在 §7 做单调性诊断）；同日按{(p.RankBy == "score" ? "分数" : "ER 降序")}取前 {p.MaxNew}。参数扫描只按训练期挑选（见 1b2、§15）。").AppendLine();

        // §1 总体
        md.AppendLine("## 1. 总体（基准配置）").AppendLine();
        md.AppendLine("| 区间 | 年化 | 累计 | 最大回撤 | 夏普 | 卡玛 | 交易数 | 胜率 | 盈亏比 | 平均收益/笔 | 平均持仓(日) | 同池等权持有 年化/回撤 | 沪深300 年化/回撤 | 中证1000 年化/回撤 |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var csv = new StringBuilder("run,group,change,period,ann,total,mdd,sharpe,calmar,trades,win,plr,avg_ret,avg_hold,buy_signals,buy_blocked,watch_entries\n");
        foreach (var (nm, a, b) in per)
        {
            var s = Stats(bas.Book, a, b, cap); var h = IndexStats(ctx.Hs300, a, b); var z = IndexStats(ctx.Csi1000, a, b); var ew = EqualWeight(stocks, ctx, a, b);
            md.AppendLine($"| {nm} | {P(s.Ann)} | {P(s.Total)} | {P(s.Mdd)} | {F(s.Sharpe)} | {F(s.Calmar)} | {s.Trades} | {Pu(s.Win)} | {F(s.Plr)} | {P(s.AvgRet)} | {F(s.AvgHold, 1)} | {P(ew.Ann)} / {P(ew.Mdd)}（{ew.Trades}只） | {P(h.Ann)} / {P(h.Mdd)} | {P(z.Ann)} / {P(z.Mdd)} |");
        }
        md.AppendLine().AppendLine("> 同池等权持有：每个区间第一天按等权买入当时已有数据的样本股（复权价，含分红再投资，不计成本），持有到区间末；停牌按最后收盘计。样本只含当前在市股票，策略与该基准都偏乐观（幸存者偏差），应视为上限。");
        var open = bas.Book.Closed.Count(t => t.OpenAtEnd);
        md.AppendLine().AppendLine($"> 期末未平仓 {open} 笔按 {end} 收盘价计入。BUY 信号 {bas.Sigs.Count(x => x.Kind == "BUY")} 个（另有大盘过滤只记录 {bas.Sigs.Count(x => x.Kind == "BUY_BLOCKED")} 个）；成交 {bas.Book.Closed.Count} 笔。");
        var yrs = Math.Max(1, (DateTime.ParseExact(end, "yyyy-MM-dd", Inv) - DateTime.ParseExact(start, "yyyy-MM-dd", Inv)).TotalDays / 365.25);
        var perYear = bas.Sigs.Count(x => x.Kind == "BUY") / yrs;
        md.AppendLine($"> 年均 BUY 信号 {perYear:F1} 个（{stocks.Count} 只样本）{(perYear < 30 ? "——**少于说明书 §14 建议的“年均几十个”，统计意义不足**" : "")}。").AppendLine();

        // 变体 & 消融汇总
        foreach (var r in runs)
            foreach (var (nm, a, b) in per)
            {
                var s = Stats(r.Book, a, b, cap);
                int bs = r.Sigs.Count(x => x.Kind == "BUY" && string.CompareOrdinal(x.Date, a) >= 0 && string.CompareOrdinal(x.Date, b) <= 0), bb = r.Sigs.Count(x => x.Kind == "BUY_BLOCKED" && string.CompareOrdinal(x.Date, a) >= 0 && string.CompareOrdinal(x.Date, b) <= 0);
                var we = r.Fn.Days.Where(kv => kv.Key >= int.Parse(a[..4], Inv) && kv.Key <= int.Parse(b[..4], Inv)).Sum(kv => kv.Value[5]);
                csv.Append(string.Join(",", Q(r.Name), r.Group, Q(r.Change), nm, N(s.Ann), N(s.Total), N(s.Mdd), N(s.Sharpe), N(s.Calmar), s.Trades, N(s.Win), N(s.Plr), N(s.AvgRet), N(s.AvgHold), bs, bb, we)).Append('\n');
            }
        File.WriteAllText(Path.Combine(outDir, "sr_summary.csv"), "\uFEFF" + csv, new UTF8Encoding(false));
        void RunTable(string title, string group)
        {
            md.AppendLine(title).AppendLine();
            md.AppendLine("| 运行 | BUY信号 | 年均BUY | 训练期 年化 / 回撤 / 夏普 | 训练期 交易 / 胜率 / 盈亏比 | 样本外 年化 / 回撤 / 夏普 | 样本外 交易 / 胜率 / 盈亏比 | 全区间 年化 / 回撤 |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var r in runs.Where(x => x.Group == group || x.Group == "基准"))
            {
                var f = Stats(r.Book, start, end, cap); var t = Stats(r.Book, start, trainEnd, cap); var o = Stats(r.Book, oosStart, end, cap);
                md.AppendLine($"| {r.Name} | {r.Sigs.Count(x => x.Kind == "BUY")} | {r.Sigs.Count(x => x.Kind == "BUY") / yrs:F1} | {P(t.Ann)} / {P(t.Mdd)} / {F(t.Sharpe)} | {t.Trades} / {Pu(t.Win)} / {F(t.Plr)} | {P(o.Ann)} / {P(o.Mdd)} / {F(o.Sharpe)} | {o.Trades} / {Pu(o.Win)} / {F(o.Plr)} | {P(f.Ann)} / {P(f.Mdd)} |");
            }
            md.AppendLine();
        }
        RunTable("## 1a. t37 第一轮消融（a / b / c / 整个 ③）", "消融R1");
        RunTable("## 1b. 均线组合（回测内按变体重算 MA_S/MA_L）", "均线");
        {
            md.AppendLine("同期基准（不计成本；同池等权 = 区间首日等权买入当时已有数据的样本股并持有）：").AppendLine();
            md.AppendLine("| 基准 | 训练期 年化 / 回撤 / 夏普 | 样本外 年化 / 回撤 / 夏普 | 全区间 年化 / 回撤 |"); md.AppendLine("|---|---:|---:|---:|");
            Stat[] B(Func<string, string, Stat> f) => [f(start, trainEnd), f(oosStart, end), f(start, end)];
            foreach (var (nm, st) in new[] { ("同池等权买入持有", B((a, b) => EqualWeight(stocks, ctx, a, b))), ("沪深300", B((a, b) => IndexStats(ctx.Hs300, a, b))), ("中证1000", B((a, b) => IndexStats(ctx.Csi1000, a, b))) })
                md.AppendLine($"| {nm} | {P(st[0].Ann)} / {P(st[0].Mdd)} / {F(st[0].Sharpe)} | {P(st[1].Ann)} / {P(st[1].Mdd)} / {F(st[1].Sharpe)} | {P(st[2].Ann)} / {P(st[2].Mdd)} |");
            md.AppendLine();
            var sel = runs.Where(x => x.Group is "基准" or "均线" or "消融R1").ToList();
            md.AppendLine("每年 进入WATCH次数 / BUY信号数（基准列另附观察池内“重点观察”股票·交易日数）：").AppendLine();
            md.AppendLine("| 年 | " + string.Join(" | ", sel.Select((r, k) => k == 0 ? "基准（10/30）" : r.Name)) + " |");
            md.AppendLine("|---|" + string.Concat(sel.Select(_ => "---:|")));
            foreach (var y in bas.Fn.Days.Keys.OrderBy(x => x))
                md.AppendLine($"| {y} | " + string.Join(" | ", sel.Select((r, k) => { var a = r.Fn.Days.GetValueOrDefault(y) ?? new long[8]; return $"{a[5]} / {a[6]}" + (k == 0 ? $"（重点 {bas.Fn.Key.GetValueOrDefault(y)}）" : ""); })) + " |");
            md.AppendLine($"| 年均 | " + string.Join(" | ", sel.Select(r => $"{r.Fn.Days.Values.Sum(a => a[5]) / yrs:F1} / {r.Fn.Days.Values.Sum(a => a[6]) / yrs:F1}")) + " |");
            md.AppendLine();
        }
        if (runs.Any(r => r.Group == "参数扫描"))
        {
            RunTable("## 1b2. v2.0 优先参数扫描（一次只改一项，其余同基准；均线组合见 1b）", "参数扫描");
            // 只按训练期挑：每个维度在“基准值 + 扫描值”里取训练期夏普最高者（训练期交易 < 10 笔的不参与）；样本外只做检验
            var dims = new (string Dim, Func<Run, bool> In)[]
            {
                ("回撤 DD_MIN", r => r.Group == "基准" || r.Name.StartsWith("DD_MIN")),
                ("放量倍数", r => r.Group == "基准" || r.Name.StartsWith("放量倍数")),
                ("止损", r => r.Group == "基准" || r.Name.StartsWith("止损 ")),
                ("均线组合", r => r.Group == "基准" || (r.Group == "均线" && !r.Name.Contains("40"))),
            };
            md.AppendLine("**按训练期（≤" + trainEnd + "）挑选**（每个维度取训练期夏普最高、且训练期交易 ≥10 笔者；没有样本外参与）：").AppendLine();
            foreach (var (dim, f) in dims)
            {
                var cand = runs.Where(f).Select(r => (R: r, S: Stats(r.Book, start, trainEnd, cap), O: Stats(r.Book, oosStart, end, cap))).ToList();
                var ok = cand.Where(x => x.S.Trades >= 10 && !double.IsNaN(x.S.Sharpe)).OrderByDescending(x => x.S.Sharpe).ToList();
                md.AppendLine(ok.Count == 0 ? $"- {dim}：训练期交易都不足 10 笔，不挑" :
                    $"- {dim}：训练期最优 = {(ok[0].R.Group == "基准" ? "基准值" : ok[0].R.Name)}（训练期夏普 {F(ok[0].S.Sharpe)}，年化 {P(ok[0].S.Ann)}，{ok[0].S.Trades} 笔）；样本外检验：夏普 {F(ok[0].O.Sharpe)}，年化 {P(ok[0].O.Ann)}，{ok[0].O.Trades} 笔");
            }
            md.AppendLine();
        }
        RunTable("## 1c. 其他变体（每行只改一项，其余同基准）", "变体");
        if (runs.Any(r => r.Group == "放宽"))
        {
            RunTable($"## 1d. 放宽实验（基准年均 BUY 信号 {perYear:F1} 个 < {p.Cfg.D("backtest.min_buy_per_year", 20)}，按 v2.0/t36 只放宽 ③ 与回撤 DD_MIN，不加条件）", "放宽");
            if (runs.Any(r => r.Group == "L4诊断")) RunTable("## 1d. L4 诊断（放宽实验无效时定位瓶颈用；不是 t36 规定的放宽项，不改默认）", "L4诊断");
            md.AppendLine("| 运行 | 年均 WATCH 进入 | 年均 BUY | 年均成交 | BUY 后20日 平均 / 中位 / 胜率 |"); md.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var r in runs.Where(x => x.Group is "基准" or "放宽" or "L4诊断"))
            {
                var k20 = Math.Max(0, Array.IndexOf(p.Cfg.Doubles("backtest.forward_days").Select(x => (int)x).ToArray(), 20));
                var v = r.Sigs.Where(x => x.Kind == "BUY" && x.Fillable && x.Ret.Length > k20 && !double.IsNaN(x.Ret[k20])).Select(x => x.Ret[k20]).ToList();
                md.AppendLine($"| {r.Name} | {r.Fn.Days.Sum(kv => kv.Value[5]) / yrs:F1} | {r.Sigs.Count(x => x.Kind == "BUY") / yrs:F1} | {r.Book.Closed.Count / yrs:F1} | {P(Mean(v))} / {P(Median(v))} / {Pu(v.Count == 0 ? double.NaN : (double)v.Count(x => x > 0) / v.Count)}（{v.Count}） |");
            }
            md.AppendLine();
        }

        // §2 漏斗
        md.AppendLine("## 2. 信号漏斗（每年：出现过该层的股票数 / 股票·交易日数）").AppendLine();
        md.AppendLine($"| 年 | L0 | L1 | L2 | L3(≥{p.WatchMin}) | 观察池(股票/天) | 进入WATCH(次) | BUY | 大盘过滤只记录 | 成交 |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var fcsv = new StringBuilder("year,l0_stocks,l0_days,l1_stocks,l1_days,l2_stocks,l2_days,l3_stocks,l3_days,pool_stocks,pool_days,watch_enter,buy,buy_blocked,filled\n");
        foreach (var (y, a) in bas.Fn.Days.OrderBy(x => x.Key))
        {
            var st = bas.Fn.Stocks[y]; var filled = bas.Book.Closed.Count(t => t.EntryDate.StartsWith(y.ToString(Inv)));
            md.AppendLine($"| {y} | {st[0].Count} / {a[0]} | {st[1].Count} / {a[1]} | {st[2].Count} / {a[2]} | {st[3].Count} / {a[3]} | {st[4].Count} / {a[4]} | {a[5]} | {a[6]} | {a[7]} | {filled} |");
            fcsv.Append($"{y},{st[0].Count},{a[0]},{st[1].Count},{a[1]},{st[2].Count},{a[2]},{st[3].Count},{a[3]},{st[4].Count},{a[4]},{a[5]},{a[6]},{a[7]},{filled}\n");
        }
        File.WriteAllText(Path.Combine(outDir, "sr_funnel.csv"), "\uFEFF" + fcsv, new UTF8Encoding(false));
        var fn = bas.Fn;
        md.AppendLine().AppendLine("**被哪个条件过滤**（股票·交易日数；上一层通过、本层未通过时计数，一天可同时计多个条件；括号内为“首个未通过条件”计数）：").AppendLine();
        md.AppendLine("- L0：" + string.Join("，", SrParams.L0Names.Select((n, k) => $"{n} {fn.F0[k]}")));
        md.AppendLine("- L1：" + string.Join("，", SrParams.L1Names.Select((n, k) => $"{n} {fn.F1[k]}（{fn.First1[k]}）")));
        md.AppendLine("- L2：" + string.Join("，", SrParams.L2Names.Select((n, k) => $"{n} {fn.F2[k]}（{fn.First2[k]}）")));
        md.AppendLine("- L3（满足 <6 项的天）：" + string.Join("，", SrParams.L3Names.Select((n, k) => $"{n} {fn.F3[k]}")));
        var ls = fn.LadderScores;
        md.AppendLine($"- **BUY 阶梯**（观察池内的股票·交易日，逐级累加条件）：池内 {fn.Ladder[0]} → L3 达 BUY 要求 {fn.Ladder[1]} → 再加 ④ 全过 {fn.Ladder[2]} → 再加买入附加过滤（不含评分）{fn.Ladder[3]} → 再加评分门槛{(p.BuyOn[4] ? $" ≥{p.ScoreMin}" : "（关闭）")} {fn.Ladder[4]}；只看 ④ 全过（不看 L3）{fn.Ladder[5]}。" +
            (ls.Count > 0 ? $"评分前一级 {ls.Count} 天的分数：平均 {ls.Average():F1}，最高 {ls.Max():F1}，≥60 {ls.Count(x => x >= 60)}，≥50 {ls.Count(x => x >= 50)}" : ""));
        md.AppendLine($"- 观察池内未升级 BUY 的 {fn.PoolNoBuy} 个股票·交易日：L3 未达 BUY 要求 {fn.F3Buy}；" + string.Join("，", p.L4N.Select((n, k) => $"{n} {fn.F4[k]}")) + "；" + string.Join("，", SrParams.BuyNames.Select((n, k) => $"{n} {fn.FB[k]}")));
        md.AppendLine("- 移出观察池原因：" + string.Join("，", fn.Removes.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))).AppendLine();

        // §3 分层收益
        var fd = p.Cfg.Doubles("backtest.forward_days").Select(x => (int)x).DefaultIfEmpty(20).ToArray();
        void Fwd(string title, IEnumerable<Sig> src)
        {
            var l = src.Where(x => x.Fillable).ToList();
            md.AppendLine(title).AppendLine();
            md.AppendLine("| 持有 | 样本 | 平均收益 | 中位数 | 胜率 | 平均MAE | 平均MFE |"); md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            for (var k = 0; k < fd.Length; k++)
            {
                var v = l.Where(x => !double.IsNaN(x.Ret[k])).ToList();
                md.AppendLine($"| {fd[k]} 日 | {v.Count} | {P(Mean(v.Select(x => x.Ret[k])))} | {P(Median(v.Select(x => x.Ret[k])))} | {Pu(v.Count == 0 ? double.NaN : (double)v.Count(x => x.Ret[k] > 0) / v.Count)} | {P(Mean(v.Select(x => x.Mae[k])))} | {P(Mean(v.Select(x => x.Mfe[k])))} |");
            }
            md.AppendLine();
        }
        // 逐只过滤归因（v2.0：报告每只被哪个条件过滤）
        {
            var at = bas.PerStock.Values.OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
            var attrCsv = new StringBuilder("code,name,industry,eval_days,l0_days,l1_days,l2_days,l3_days,enter,buy,key_watch_days,top1,top1_days,top2,top2_days,top3,top3_days\n");
            foreach (var x in at)
            {
                var tops = x.First.Where(kv => kv.Key != "全部通过").OrderByDescending(kv => kv.Value).Take(3).ToList();
                string T(int j) => j < tops.Count ? $"{tops[j].Key},{tops[j].Value}" : ",";
                attrCsv.Append($"{x.Code},{x.Name.Replace(",", " ")},{x.Industry.Replace(",", " ")},{x.Days},{x.L0},{x.L1},{x.L2},{x.L3},{x.Enter},{x.Buy},{x.Key},{T(0)},{T(1)},{T(2)}\n");
            }
            File.WriteAllText(Path.Combine(outDir, "sr_stock_filter.csv"), attrCsv.ToString(), new UTF8Encoding(true));
            md.AppendLine("## 2b. 逐只过滤归因（基准；每只股票每个 L0 通过日记“第一个没通过的条件”，按天数最多的条件归类）").AppendLine();
            var grp = at.Select(x => x.First.Where(kv => kv.Key != "全部通过").OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "（无 L0 通过日）")
                .GroupBy(x => x).OrderByDescending(g => g.Count()).ToList();
            md.AppendLine("| 主要卡点 | 股票数 |"); md.AppendLine("|---|---:|");
            foreach (var g in grp) md.AppendLine($"| {g.Key} | {g.Count()} |");
            var sumF = new Dictionary<string, long>(); foreach (var x in at) foreach (var (kk, v) in x.First) sumF[kk] = sumF.GetValueOrDefault(kk) + v;
            md.AppendLine().AppendLine("全部股票·交易日合计（L0 通过日，第一个没通过的条件）：" + string.Join("，", sumF.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
            md.AppendLine().AppendLine($"进过观察池的股票 {at.Count(x => x.Enter > 0)} 只，出过 BUY 的 {at.Count(x => x.Buy > 0)} 只（共 {at.Count} 只）。逐只明细：sr_stock_filter.csv。").AppendLine();
        }
        md.AppendLine("## 3. BUY 后 N 日表现（全部 BUY 信号，T+1 开盘买入价为基准，不含成本；开盘涨停/停牌不可成交的剔除）").AppendLine();
        Fwd("**全区间**", bas.Sigs.Where(x => x.Kind == "BUY"));
        Fwd("**训练期**", bas.Sigs.Where(x => x.Kind == "BUY" && string.CompareOrdinal(x.Date, trainEnd) <= 0));
        Fwd("**样本外**", bas.Sigs.Where(x => x.Kind == "BUY" && string.CompareOrdinal(x.Date, oosStart) >= 0));
        Fwd("**大盘过滤拦下的信号（只记录）**", bas.Sigs.Where(x => x.Kind == "BUY_BLOCKED"));

        // §4 卖出原因
        md.AppendLine($"## 4. 卖出原因（基准；主因顺序 S1>S2>S6>S4>S3>S5>S7>S8{(p.SOn[1] ? "" : "，v2.0 即 1止损→4放量大阴→3趋势破坏→2移动止盈→5时间止损；D0 低点约束的止损也记为 S1")}；收益为整笔交易、含成本）").AppendLine();
        md.AppendLine("| 主因 | 笔数 | 占比 | 平均收益 | 胜率 | 平均持仓(日) | 全部触发次数 |"); md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        var tr = bas.Book.Closed;
        foreach (var k in SrParams.ExitKeys.Concat(["期末持仓"]))
        {
            var l = tr.Where(t => t.Primary == k).ToList();
            var name = k == "期末持仓" ? "期末未平仓" : SrParams.ExitNames[Array.IndexOf(SrParams.ExitKeys, k)];
            var all = k == "期末持仓" ? l.Count : tr.Count(t => t.Reasons.Contains(name));
            md.AppendLine($"| {name} | {l.Count} | {Pu(tr.Count == 0 ? double.NaN : (double)l.Count / tr.Count)} | {P(Mean(l.Select(t => t.Ret)))} | {Pu(l.Count == 0 ? double.NaN : (double)l.Count(t => t.Ret > 0) / l.Count)} | {F(Mean(l.Select(t => (double)t.HoldDays)), 1)} | {all} |");
        }
        var red = tr.Where(t => t.Partials.Length > 0).ToList();
        md.AppendLine().AppendLine($"> 触发过减仓（R1~R3 卖 {p.SellFrac:P0}）的交易 {red.Count} 笔，整笔平均收益 {P(Mean(red.Select(t => t.Ret)))}。初始止损：D0 低点约束 {tr.Count(t => t.StopKind == "S2")} 笔 / 固定比例约束 {tr.Count(t => t.StopKind == "S1")} 笔（含 R4 上移后改记 S1），平均初始止损幅度 {Pu(Mean(tr.Select(t => t.InitStopPct)))}。").AppendLine();

        // §5 分年度 / 市场环境
        md.AppendLine("## 5. 分年度 / 分市场环境（基准）").AppendLine();
        md.AppendLine("| 年 | 策略收益 | 年内最大回撤 | WATCH 进入 | BUY 信号 | 成交 | 胜率 | 平均收益/笔 | 同池等权 | 沪深300 | 中证1000 |"); md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var y0 = int.Parse(start[..4], Inv); var y1 = int.Parse(end[..4], Inv);
        for (var y = y0; y <= y1; y++)
        {
            var a = $"{y}-01-01"; var b = $"{y}-12-31"; var s = Stats(bas.Book, a, b, cap);
            var fy = bas.Fn.Days.GetValueOrDefault(y) ?? new long[8];
            md.AppendLine($"| {y}{(y == y1 ? "（至 " + end[5..] + "）" : "")} | {P(s.Total)} | {P(s.Mdd)} | {fy[5]} | {fy[6]} | {s.Trades} | {Pu(s.Win)} | {P(s.AvgRet)} | {P(EqualWeight(stocks, ctx, a, b).Total)} | {P(IndexStats(ctx.Hs300, a, b).Total)} | {P(IndexStats(ctx.Csi1000, a, b).Total)} |");
        }
        md.AppendLine().AppendLine("市场环境（沪深300：牛 = 收盘>MA60 且 MA60 较 20 日前上升；熊 = 收盘<MA60 且 MA60 下降；其余震荡）：").AppendLine();
        md.AppendLine("| 环境 | 交易日 | 策略日均收益(年化) | 沪深300 同期(年化) | 入场交易数 | 胜率 | 平均收益/笔 |"); md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var rg in new[] { "牛", "熊", "震荡" })
        {
            var days = new List<double>(); var hs = new List<double>();
            for (var j = 1; j < bas.Book.EquityDates.Count; j++)
            {
                var d = bas.Book.EquityDates[j]; if (ctx.Hs300.Regime(d) != rg) continue;
                days.Add(bas.Book.Equity[j] / bas.Book.Equity[j - 1] - 1);
                var hi = ctx.Hs300.Ix[d]; hs.Add(ctx.Hs300.C[hi] / ctx.Hs300.C[hi - 1] - 1);
            }
            var l = tr.Where(t => t.Regime == rg).ToList();
            double An(List<double> r) => r.Count == 0 ? double.NaN : Math.Pow(r.Aggregate(1.0, (acc, x) => acc * (1 + x)), 252.0 / r.Count) - 1;
            md.AppendLine($"| {rg} | {days.Count} | {P(An(days))} | {P(An(hs))} | {l.Count} | {Pu(l.Count == 0 ? double.NaN : (double)l.Count(t => t.Ret > 0) / l.Count)} | {P(Mean(l.Select(t => t.Ret)))} |");
        }
        md.AppendLine();

        // §6 消融
        RunTable("## 6. 消融（每行只关闭一个子条件，关闭 = 视为通过；L3 关闭项仍计入“满足项数”）", "消融");

        // §7 评分分档
        md.AppendLine("## 7. 评分单调性检查（评分只做诊断、不参与选股；t37 端点；基准全部可成交 BUY 信号）").AppendLine();
        {
            var pool = (noGate ?? bas).Sigs.Where(x => x.Kind == "BUY" && x.Fillable).ToList();
            // 均线变体的 BUY 合并去重（同一股票同一天只算一次），样本更大
            var allSig = runs.Where(r => r.Group is "基准" or "均线").SelectMany(r => r.Sigs).Where(x => x.Kind == "BUY" && x.Fillable)
                .GroupBy(x => x.Code + x.Date).Select(g => g.First()).ToList();
            void Buckets(string title, List<Sig> l)
            {
                md.AppendLine($"**{title}**（{l.Count} 个信号）").AppendLine();
                if (l.Count < 10) { md.AppendLine("_信号太少，不做分档_").AppendLine(); return; }
                md.AppendLine("| 五分位（按分数） | 分数范围 | 信号 | " + string.Join(" | ", fd.Select(d => $"{d}日均值 / 胜率")) + " |");
                md.AppendLine("|---|---|---:|" + string.Concat(fd.Select(_ => "---:|")));
                var sorted = l.OrderBy(x => x.Ev.Score).ToList(); var q = 5; var means = new double[q, fd.Length];
                for (var b = 0; b < q; b++)
                {
                    var part = sorted.Skip(b * sorted.Count / q).Take((b + 1) * sorted.Count / q - b * sorted.Count / q).ToList();
                    var cells = new List<string>();
                    for (var k = 0; k < fd.Length; k++)
                    {
                        var v = part.Select(x => x.Ret[k]).Where(x => !double.IsNaN(x)).ToList();
                        means[b, k] = v.Count > 0 ? v.Average() : double.NaN;
                        cells.Add($"{P(means[b, k])} / {Pu(v.Count == 0 ? double.NaN : (double)v.Count(x => x > 0) / v.Count)}");
                    }
                    md.AppendLine($"| Q{b + 1} | {part.First().Ev.Score:F1}~{part.Last().Ev.Score:F1} | {part.Count} | " + string.Join(" | ", cells) + " |");
                }
                // 5 分一档
                md.AppendLine().AppendLine("| 分数段（5分一档） | 信号 | " + string.Join(" | ", fd.Select(d => $"{d}日均值"))+ " |");
                md.AppendLine("|---|---:|" + string.Concat(fd.Select(_ => "---:|")));
                foreach (var g in l.GroupBy(x => Math.Floor(x.Ev.Score / 5) * 5).OrderBy(g => g.Key))
                    md.AppendLine($"| {g.Key:F0}-{g.Key + 5:F0} | {g.Count()} | " + string.Join(" | ", fd.Select((_, k) => P(Mean(g.Select(x => x.Ret[k]).Where(x => !double.IsNaN(x)))))) + " |");
                var verdict = new List<string>();
                for (var k = 0; k < fd.Length; k++)
                {
                    var inc = true; for (var b = 1; b < q; b++) if (!(means[b, k] >= means[b - 1, k])) inc = false;
                    var pr = l.Where(x => !double.IsNaN(x.Ret[k])).ToList();
                    var rho = Spearman(pr.Select(x => x.Ev.Score).ToList(), pr.Select(x => x.Ret[k]).ToList());
                    verdict.Add($"{fd[k]}日：五分位{(inc ? "单调上升" : "不单调")}，Spearman ρ={F(rho)}（n={pr.Count}）");
                }
                md.AppendLine().AppendLine("结论：" + string.Join("；", verdict) + "。").AppendLine();
            }
            Buckets("基准 BUY 信号", pool);
            Buckets("基准 + 均线 5/20、10/60 变体 BUY 信号合并去重", allSig);
            md.AppendLine("> 判断规则（作者 t37）：分数分档后的后续收益没有单调上升就去掉评分；有单调关系再按训练期取分位数作门槛。本报告未用评分门槛。").AppendLine();
        }

        // §15
        md.AppendLine(Limits(p, stocks.Count, start, end, trainEnd));
        md.AppendLine($"_文件：sr_summary.csv（全部运行×区间）、sr_trades.csv（基准成交明细：买入原因/指标快照/卖出原因/MAE/MFE）、sr_signals.csv（基准全部 BUY/大盘过滤信号及 N 日表现）、sr_funnel.csv、sr_equity.csv、sr_meta.json。用时 {secs:F0}s。_");
        File.WriteAllText(Path.Combine(outDir, "sr_report.md"), md.ToString(), new UTF8Encoding(false));

        // 明细
        var tcsv = new StringBuilder("code,name,industry,sig_date,entry_date,exit_date,entry_fill,shares,cash_in,cash_out,pnl,ret,hold_days,primary,reasons,partials,mae,mfe,init_stop_pct,stop_kind,score,regime,dd,rebound,d0_date,d0_vol_ratio,ma_cross_date,macd_cross_date,er20,r2_20,sigma20,l3_count,open_at_end,buy_reason\n");
        foreach (var t in bas.Book.Closed.OrderBy(x => x.EntryDate, StringComparer.Ordinal))
            tcsv.Append(string.Join(",", t.Code, Q(t.Name), Q(t.Industry), t.SigDate, t.EntryDate, t.ExitDate, N(t.EntryFill), t.Shares0, N(t.CashIn), N(t.CashOut), N(t.Pnl), N(t.Ret), t.HoldDays, t.Primary, Q(t.Reasons), Q(t.Partials), N(t.Mae), N(t.Mfe), N(t.InitStopPct), t.StopKind,
                N(t.Score), t.Regime, N(t.Dd), N(t.Rebound), t.D0Date, N(t.D0VolRatio), t.MaCrossDate, t.MacdCrossDate, N(t.Er), N(t.R2), N(t.Sigma), t.L3Count, t.OpenAtEnd ? 1 : 0, Q(t.Reason))).Append('\n');
        File.WriteAllText(Path.Combine(outDir, "sr_trades.csv"), "\uFEFF" + tcsv, new UTF8Encoding(false));
        var scsv = new StringBuilder("date,code,name,kind,score,outcome,fillable,dd,rebound,d0_date,d0_vol_ratio,er20,r2_20,sigma20,l3_count,ma_cross_date,macd_cross_date," + string.Join(",", fd.Select(d => $"ret{d},mae{d},mfe{d}")) + "\n");
        foreach (var x in bas.Sigs)
        {
            var e = x.Ev; var s = x.S;
            scsv.Append(string.Join(",", x.Date, x.Code, Q(s.Name), x.Kind, N(e.Score), Q(x.Outcome), x.Fillable ? 1 : 0, N(e.Dd), N(e.Rebound), e.D0 >= 0 ? s.Date[e.D0] : "", N(e.D0VolRatio), N(e.Er), N(e.R2), N(e.Sigma), e.L3Count,
                e.MaCross >= 0 ? s.Date[e.MaCross] : "", e.MacdCross >= 0 ? s.Date[e.MacdCross] : "", string.Join(",", Enumerable.Range(0, fd.Length).Select(k => $"{N(x.Ret[k])},{N(x.Mae[k])},{N(x.Mfe[k])}")))).Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "sr_signals.csv"), "\uFEFF" + scsv, new UTF8Encoding(false));
        var ecsv = new StringBuilder("date,equity,hs300\n");
        for (var j = 0; j < bas.Book.EquityDates.Count; j++) { var d = bas.Book.EquityDates[j]; ecsv.Append($"{d},{N(bas.Book.Equity[j])},{(ctx.Hs300.Ix.TryGetValue(d, out var hi) ? N(ctx.Hs300.C[hi]) : "")}\n"); }
        File.WriteAllText(Path.Combine(outDir, "sr_equity.csv"), ecsv.ToString(), new UTF8Encoding(false));
        var meta = new JsonObject
        {
            ["strategy"] = "S28 底部反转缓升", ["version"] = p.Version, ["config"] = p.Cfg.Path, ["run_at"] = BrMarketData.NowCn().ToString("yyyy-MM-dd HH:mm:ss"),
            ["sample"] = sampleNote, ["sample_method"] = p.Cfg.S("backtest.sample_method", "industry_stratified_random"), ["sample_seed"] = p.Cfg.I("backtest.seed", 20261110), ["stocks"] = stocks.Count, ["failed"] = new JsonArray(failed.Select(x => (JsonNode)x).ToArray()), ["start"] = start, ["end"] = end, ["train_end"] = trainEnd,
            ["runs"] = runs.Count, ["elapsed_sec"] = Math.Round(secs, 1), ["codes"] = new JsonArray(stocks.Select(x => (JsonNode)x.Code).ToArray()),
        };
        File.WriteAllText(Path.Combine(outDir, "sr_meta.json"), meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
    }

    public static string Limits(SrParams p, int n, string start, string end, string trainEnd) => $"""
## 15. 数据限制与范围（v2.0 + t37）

1. **市值**：没有历史总市值。回测用“20 日均成交额 ≥{p.MinAmt20 / 1e8:F1} 亿”等流动性条件代替市值门槛；{(p.L0On[3] ? $"实盘扫描用当前总市值 ≥{p.MinCap / 1e8:F0} 亿（L0 0.4）。" : "v2.0 不设市值条件（说明书建议实盘可加总市值 ≥30 亿，未启用）。")}行业也是当前行业。
2. **幸存者偏差**：样本只含当前在市股票（已退市股票不在样本内，说明书 §11.1 要求保留退市股，v1 未满足），ST 也只按当前名称判断。**回测收益应视为上限**；为此同时给出“同一股票池等权买入持有”基准（全区间 / 训练期 / 样本外），与沪深300、中证1000 并列。
3. **复权与涨跌幅口径**：东财前复权（fqt=1）是分段仿射（送转按比例、现金分红按差值），长历史会出现负价、比例失真（例：600519 前复权 2013-04-11 收盘 −171.05），且用到了未来的除权信息。v1 的处理：
   - 价格类指标（均线、MACD、ATR、ER、R²、回撤、反弹、止损价）用由不复权与前复权反推的**等比前复权**（data.adjust="ratio"，可切回 "qfq"）；等比复权只把各段乘常数，比例类指标只依赖 ≤T 的数据。
   - **所有涨跌幅类阈值用不复权（交易所口径）日涨跌幅**：2.2 启动日涨幅 3%~9.5%、3.1 大波动天数、3.2 σ、3.3 最大单日跌幅、S6 −5%、BUY 当日涨幅 <7%、R2 涨幅 <1%；涨停 / 跌停 / 一字板判断用不复权价格与交易所涨跌停价（前收盘×(1±涨跌幅限制)，除权日前收盘用除权参考价）。股价 ≥2 元、成交额也用不复权数据。
   - 残留局限：除权日由两位小数价格反推，<0.012 元的极小分红可能被忽略；送转（因子 <0.9）时历史成交量按因子折算。
4. **样本与区间**：{n} 只股票（当前股票列表按行业分层随机抽样，固定种子，不按近期表现挑选，不强制加入任何股票），{start}~{end}；训练期 ≤{trainEnd}，样本外之后（含 2026 年至今）。上市晚于 {start} 的股票从上市满 {p.MinListDays} 个交易日起参与。
5. **参数扫描只做单维、只按训练期挑选**：回撤 30/40/50%、放量 1.5/2/2.5、均线 5/20·10/30·10/60、止损 6/8/10% 逐维替换（其他保持默认），按训练期夏普（训练期成交 ≥10 笔）挑选，样本外只做检验；未做多维网格与 walk-forward。样本只有 {n} 只、年均 BUY 很少，单维挑选的差异多数在噪声范围内。
6. **成交假设**：T+1 开盘价 ±0.1% 滑点成交；开盘涨停视为买不到、一字跌停视为卖不出（顺延）；止损按收盘价判断、次日开盘执行（说明书原文规则），跳空会使实际亏损大于止损幅度。v2.0 止损价 = min(买入×(1−{p.HardStop:P0}), D0 最低)，最宽 −{p.MaxStop:P0}；D0 低点通常低于买入×0.92，止损多由 D0 低点或 −12% 上限决定，因此“止损 6/8/10%”参数对交易影响很小（见 §4 初始止损统计）。
7. **评分**：v2.0 默认不用评分（score.enabled 只用于报告），同日按 ER 降序取前 {p.MaxNew}；评分按 t37 作者端点计算，仅在 §7 做“评分分组 vs 后续收益”单调性诊断。
8. **去重**：同一个 D0 一轮只出一次 BUY；出 BUY 或被移出后，该股要等出现更新的 D0 才会重新进池（t37 实现时加入，避免同一轮信号每天重复计数）。
9. **v1 配置在新代码下**：bottom_slowrise_v1.json 用 l4.mode=legacy（4.1~4.7）与旧有效期，但评分改为 t37 端点；v1.3 的原始结果保留在 data/bottom_slowrise/backtest/（未重跑覆盖）。
""";

    private static double Spearman(List<double> a, List<double> b)
    {
        if (a.Count < 3) return double.NaN;
        static double[] Rk(List<double> v) { var idx = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray(); var r = new double[v.Count]; var i0 = 0;
            while (i0 < idx.Length) { var j = i0; while (j + 1 < idx.Length && v[idx[j + 1]] == v[idx[i0]]) j++; for (var k = i0; k <= j; k++) r[idx[k]] = (i0 + j) / 2.0; i0 = j + 1; } return r; }
        var ra = Rk(a); var rb = Rk(b); var ma = ra.Average(); var mb = rb.Average();
        double sab = 0, saa = 0, sbb = 0; for (var i = 0; i < ra.Length; i++) { sab += (ra[i] - ma) * (rb[i] - mb); saa += (ra[i] - ma) * (ra[i] - ma); sbb += (rb[i] - mb) * (rb[i] - mb); }
        return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : double.NaN;
    }

    private static string N(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "" : Math.Round(v, 6).ToString("R", Inv);
    private static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
