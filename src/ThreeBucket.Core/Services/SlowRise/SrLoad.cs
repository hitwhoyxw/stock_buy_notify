using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using ThreeBucket.Core.Services.BottomReversal;

namespace ThreeBucket.Core.Services.SlowRise;

/// <summary>数据装载（回测 / 扫描 / 自检共用）：交易日历 = 沪深300 日K；个股 raw+qfq → SrStock。</summary>
public static class SrLoad
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>盘中（&lt; data.market_close_time）丢弃今日未完成K线。</summary>
    public static SrData.Bars DropIncomplete(SrData.Bars b, SrParams p)
    {
        var now = BrMarketData.NowCn();
        var hm = p.Cfg.S("data.market_close_time", "15:05").Split(':');
        var close = new TimeSpan(int.Parse(hm[0], Inv), int.Parse(hm[1], Inv), 0);
        if (!p.Cfg.B("data.use_incomplete_today_bar") && b.Count > 0 && b.D[^1] == now.ToString("yyyy-MM-dd") && new TimeSpan(now.Hour, now.Minute, 0) < close)
            return b.Take(b.Count - 1);
        return b;
    }

    public static SrData.Bars Upto(SrData.Bars b, string day)
    {
        var n = 0; while (n < b.Count && string.CompareOrdinal(b.D[n], day) <= 0) n++;
        return n == b.Count ? b : b.Take(n);
    }

    public sealed class Ctx
    {
        public SrData.Index Hs300 = null!; public SrData.Index? Csi1000;
        public List<string> Cal = []; public Dictionary<string, int> CalIx = new();
        public string Day = ""; public bool Intraday;
    }

    public static async Task<Ctx> CalendarAsync(SrParams p, int lmt, string? cacheDir, Action<string> log, CancellationToken ct)
    {
        var sid = p.Cfg.S("data.calendar_index_secid", "1.000300");
        var b = await SrData.KlineAsync(sid, lmt, 0, cacheDir, 4, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"指数 {sid} 日K获取失败（交易日历）");
        var now = BrMarketData.NowCn(); var intraday = b.Count > 0 && b.D[^1] == now.ToString("yyyy-MM-dd");
        var b2 = DropIncomplete(b, p); intraday = intraday && b2.Count < b.Count;
        if (intraday) log($"[S28] 当前 {now:HH:mm} 盘中，丢弃今日未完成K线，按上一交易日 {b2.D[^1]} 收盘计算");
        var ctx = new Ctx { Hs300 = SrData.Index.From(sid, "沪深300", b2, p), Cal = b2.D.ToList(), Day = b2.D[^1], Intraday = intraday };
        for (var j = 0; j < ctx.Cal.Count; j++) ctx.CalIx[ctx.Cal[j]] = j;
        var bsid = p.Cfg.S("data.benchmark2_secid", "1.000852");
        var b3 = await SrData.KlineAsync(bsid, lmt, 0, cacheDir, 3, ct).ConfigureAwait(false);
        if (b3 is not null) ctx.Csi1000 = SrData.Index.From(bsid, "中证1000", DropIncomplete(b3, p), p);
        return ctx;
    }

    public static async Task<(List<SrStock> Stocks, List<string> Failed)> StocksAsync(SrParams p, IReadOnlyList<string> codes, Dictionary<string, BrMarketData.UniverseItem> uni, Ctx ctx, int lmt,
        string? cacheDir, Action<string> log, CancellationToken ct, string tag = "S28")
    {
        var adjust = p.Cfg.S("data.adjust", "ratio");
        var list = new ConcurrentBag<SrStock>(); var failed = new ConcurrentBag<string>(); var done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Parallel.ForEachAsync(codes, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, p.Cfg.I("data.concurrency", 8)), CancellationToken = ct }, async (code, tk) =>
        {
            try
            {
                var sid = BrMarketData.Secid(code);
                var raw = await SrData.KlineAsync(sid, lmt, 0, cacheDir, p.Cfg.I("data.retries", 4), tk).ConfigureAwait(false);
                var qfq = raw is null ? null : await SrData.KlineAsync(sid, lmt, 1, cacheDir, p.Cfg.I("data.retries", 4), tk).ConfigureAwait(false);
                if (raw is null || qfq is null) { failed.Add(code); return; }
                raw = Upto(DropIncomplete(raw, p), ctx.Day); qfq = Upto(DropIncomplete(qfq, p), ctx.Day);
                var s = SrData.Build(code, raw, qfq, adjust, ctx.CalIx, p);
                if (s is null) { failed.Add(code); return; }
                if (uni.TryGetValue(code, out var u)) { s.Name = u.Name; s.Industry = u.Industry; s.Mv = u.Mv; }
                list.Add(s);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failed.Add(code); }
            var d = Interlocked.Increment(ref done);
            if (d % 500 == 0 || (codes.Count < 1000 && d % 100 == 0)) log($"[{tag}] 日K {d}/{codes.Count}（{sw.Elapsed.TotalSeconds:F0}s）");
        }).ConfigureAwait(false);
        return (list.OrderBy(x => x.Code, StringComparer.Ordinal).ToList(), failed.OrderBy(x => x).ToList());
    }

    public static async Task<Dictionary<string, BrMarketData.UniverseItem>> UniverseAsync(SrParams p, string dataDir, Action<string> log, CancellationToken ct)
    {
        var (items, _, _) = await BrMarketData.GetUniverseAsync(p.Cfg, Path.Combine(dataDir, "cache"), log, ct).ConfigureAwait(false);
        var d = new Dictionary<string, BrMarketData.UniverseItem>();
        foreach (var x in items) d[x.Code] = x;
        return d;
    }
}
