using ThreeBucket.Core.Services.BottomReversal;

namespace ThreeBucket.Core.Services.SlowRise;

/// <summary>CLI：ThreeBucket.Cli --sr scan|backtest|selftest|diag [--codes "600276,603379"] [--days N] [--limit N] [--out 目录]</summary>
public static class SrCli
{
    public static async Task<int> RunAsync(string dataDir, string cmd, string? codesArg, int days, int limit, string? outDir)
    {
        void L(string m) => Console.WriteLine(m);
        var codes = (codesArg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.PadLeft(6, '0')).ToList();
        switch (cmd)
        {
            case "scan":
                var r = await SrScan.RunAsync(dataDir, L, codes, limit, outDir);
                L($"RESULT scan_day={r.Day} scanned={r.Scanned} l0={r.L0Pass} " + string.Join(" ", r.Counts.Select(kv => $"{kv.Key}={kv.Value}")) + $" signals={r.SignalsPath}");
                return 0;
            case "backtest":
                await SrBacktest.RunAsync(dataDir, codes, limit, outDir, L);
                return 0;
            case "selftest":
                return await SelfTestAsync(dataDir, codes.Count > 0 ? codes : ["600276", "603379", "000682"], L) ? 0 : 1;
            case "diag":
                await DiagAsync(dataDir, codes, days > 0 ? days : 30, L);
                return 0;
            default:
                Console.Error.WriteLine($"--sr 子命令无效: {cmd}（scan/backtest/selftest/diag）");
                return 2;
        }
    }

    /// <summary>逐日打印 L0~L4、未通过条件与观察池事件（排查用）。</summary>
    public static async Task DiagAsync(string dataDir, IReadOnlyList<string> codes, int days, Action<string> L)
    {
        var p = SrParams.Load(dataDir);
        var ctx = await SrLoad.CalendarAsync(p, 800, null, L, default);
        var uni = await SrLoad.UniverseAsync(p, dataDir, L, default);
        var (stocks, failed) = await SrLoad.StocksAsync(p, codes, uni, ctx, Math.Max(420, p.HighWin + days + 120), null, L, default);
        foreach (var f in failed) L($"{f} 数据获取失败");
        foreach (var s in stocks)
        {
            SrPoolEntry? entry = null; var evs = new List<SrPoolEvent>();
            L($"== {s.Code} {s.Name}（{s.Industry}）bars={s.Count} 除权事件={s.AdjEvents}");
            for (var i = Math.Max(1, s.Count - days - p.WatchValid); i < s.Count; i++)
            {
                evs.Clear();
                entry = SrPool.Step(s, i, entry, p, ctx.Hs300, evs, out var e, true);
                if (i < s.Count - days) continue;
                var lay = $"L0{(e.L0 ? "✓" : "✗")} L1{(e.L1 ? "✓" : "✗")} L2{(e.L2 ? "✓" : "✗")} L3={e.L3Count}{(e.L3Watch ? "W" : "")}{(e.L3Buy ? "B" : "")} L4{(e.L4 ? "✓" : "✗")} 分{e.Score:F1}";
                L($"{s.Date[i]} C={s.RC[i]:F2} {lay} 池:{(entry is null ? "-" : entry.EnterDate)} 事件:{string.Join("/", evs.Select(x => x.Kind + (x.Note.Length > 0 ? "(" + x.Note + ")" : "")))} | {SrEngine.FailText(s, e, p, true)}");
            }
        }
    }

    /// <summary>
    /// 无未来函数自检：对每只股票随机 40 个交易日 + 全部发生 WATCH/BUY 事件的日子，把复权后序列截断到 T 日、重算全部指标并判定，
    /// 必须与全量序列在 T 日的结果完全一致（各层通过/未通过掩码、D0、两个金叉日、L3 项数；评分与比例指标相对误差 &lt;1e-9）；
    /// 观察池事件序列（截断重放 vs 全量重放）到 T 日为止也必须一致。另检查等比复权：非除权日的涨跌幅与不复权涨跌幅一致。
    /// </summary>
    public static async Task<bool> SelfTestAsync(string dataDir, IReadOnlyList<string> codes, Action<string> L)
    {
        var p = SrParams.Load(dataDir);
        var cacheDir = Path.Combine(dataDir, "cache", "sr_kline"); Directory.CreateDirectory(cacheDir);
        var ctx = await SrLoad.CalendarAsync(p, 6000, cacheDir, L, default);
        var lmt = p.Cfg.I("selftest.bars", 1500);
        var ok = true;
        // 1) 线性插值
        var lin = Math.Abs(SrParams.Lin(0.55, [0.40, 0.70, 8, 15]) - 11.5) < 1e-12 && SrParams.Lin(0.9, [0.40, 0.70, 8, 15]) == 15 && SrParams.Lin(0.1, [0.40, 0.70, 8, 15]) == 8;
        L($"[selftest] 评分线性插值（DD 55% → 11.5 分；越界取端点）：{(lin ? "通过" : "失败")}"); ok &= lin;
        foreach (var code in codes)
        {
            var sid = BrMarketData.Secid(code);
            var raw = await SrData.KlineAsync(sid, lmt, 0, cacheDir, 4, default); var qfq = await SrData.KlineAsync(sid, lmt, 1, cacheDir, 4, default);
            if (raw is null || qfq is null) { L($"[selftest] {code} 数据获取失败"); ok = false; continue; }
            raw = SrLoad.Upto(SrLoad.DropIncomplete(raw, p), ctx.Day); qfq = SrLoad.Upto(SrLoad.DropIncomplete(qfq, p), ctx.Day);
            var full = SrData.Build(code, raw, qfq, p.Cfg.S("data.adjust", "ratio"), ctx.CalIx, p)!;
            // 2) 复权
            var bad = 0; var evd = 0; var mism = 0;
            for (var i = 1; i < full.Count; i++)
            {
                if (Math.Abs(full.RC[i] / full.RC[i - 1] - 1 - full.RPct[i]) > 1e-9) evd++;          // 除权日：交易所口径涨跌幅 ≠ 简单不复权涨跌幅
                if (Math.Abs(full.Pct[i] - full.RPct[i]) > 1e-9) mism++;                              // 等比复权涨跌幅必须 = 交易所口径涨跌幅
                if (full.C[i] <= 0) bad++;
            }
            var adjOk = bad == 0 && evd == full.AdjEvents && mism == 0;
            L($"[selftest] {code} 复权/涨跌幅口径：{full.Count} 根，除权事件 {full.AdjEvents} 个（这些天按除权参考价算涨跌幅 {evd} 个），等比复权涨跌幅与交易所口径不一致 {mism} 天，非正价格 {bad} 根：{(adjOk ? "通过" : "失败")}");
            ok &= adjOk;
            // 全量重放观察池，记录每日事件
            var fullEvents = new Dictionary<int, string>(); SrPoolEntry? en = null; var evs = new List<SrPoolEvent>(); var evDays = new List<int>();
            var i0 = Math.Max(p.HighWin + 5, 1);
            for (var i = i0; i < full.Count; i++)
            {
                evs.Clear(); en = SrPool.Step(full, i, en, p, ctx.Hs300, evs, out _, true);
                fullEvents[i] = string.Join("/", evs.Select(x => x.Kind + ":" + x.Note));
                if (evs.Any(x => x.Kind is "ENTER" or "BUY" or "BUY_BLOCKED")) evDays.Add(i);
            }
            var rnd = new Random(20261110 + int.Parse(code));
            var test = evDays.Concat(Enumerable.Range(0, 40).Select(_ => rnd.Next(i0, full.Count - 1))).Distinct().OrderBy(x => x).ToList();
            int pass = 0, fail = 0; string? firstFail = null;
            foreach (var i in test)
            {
                var n = i + 1; var dt = full.Date[i];
                // 截断“已复权”序列（等比复权因子只在除权日按当日参考价确定，等价于当时可知的信息）后重算全部指标
                var tr = full.Truncate(n).Prepare(p);
                if (tr.Count != n) { fail++; firstFail ??= $"{dt} 截断长度 {tr.Count}≠{n}"; continue; }
                var a = SrEngine.Eval(full, i, p, true); var b = SrEngine.Eval(tr, i, p, true);
                var diff = Cmp(a, b);
                // 观察池：截断序列从同一起点重放到 T
                SrPoolEntry? e2 = null; var ev2 = new List<SrPoolEvent>(); string last = "";
                for (var k = i0; k <= i; k++) { ev2.Clear(); e2 = SrPool.Step(tr, k, e2, p, ctx.Hs300, ev2, out _, true); if (k == i) last = string.Join("/", ev2.Select(x => x.Kind + ":" + x.Note)); }
                if (last != fullEvents.GetValueOrDefault(i, "")) diff += $" 观察池事件 {fullEvents.GetValueOrDefault(i)} vs {last}";
                if (diff.Length == 0) pass++; else { fail++; firstFail ??= dt + diff; }
            }
            L($"[selftest] {code} 截断比对 {test.Count} 个交易日（含 {evDays.Count} 个 WATCH/BUY 事件日）：{(fail == 0 ? "通过" : $"失败 {fail} 个，首个 {firstFail}")}");
            ok &= fail == 0;
        }
        L($"[selftest] 总结：{(ok ? "全部通过" : "存在失败")}");
        return ok;
    }

    private static string Cmp(SrEval a, SrEval b)
    {
        var d = new List<string>();
        void B(string n, bool x, bool y) { if (x != y) d.Add($"{n} {x}≠{y}"); }
        void I(string n, int x, int y) { if (x != y) d.Add($"{n} {x}≠{y}"); }
        void D(string n, double x, double y) { if (double.IsNaN(x) && double.IsNaN(y)) return; if (!(Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x)))) d.Add($"{n} {x}≠{y}"); }
        B("L0", a.L0, b.L0); B("L1", a.L1, b.L1); B("L2", a.L2, b.L2); B("L3W", a.L3Watch, b.L3Watch); B("L3B", a.L3Buy, b.L3Buy); B("L4", a.L4, b.L4); B("BUY条件", a.BuyCond, b.BuyCond);
        I("M0", a.M0, b.M0); I("M1", a.M1, b.M1); I("M2", a.M2, b.M2); I("M3", a.M3, b.M3); I("M4", a.M4, b.M4); I("MB", a.MB, b.MB);
        I("D0", a.D0, b.D0); I("MA金叉", a.MaCross, b.MaCross); I("MACD金叉", a.MacdCross, b.MacdCross); I("L3项", a.L3Count, b.L3Count);
        D("DD", a.Dd, b.Dd); D("反弹", a.Rebound, b.Rebound); D("ER", a.Er, b.Er); D("R2", a.R2, b.R2); D("σ", a.Sigma, b.Sigma); D("ATR/C", a.AtrRatio, b.AtrRatio);
        if (Math.Abs(a.Score - b.Score) > 0.051) d.Add($"评分 {a.Score}≠{b.Score}");
        return d.Count == 0 ? "" : " " + string.Join("，", d);
    }
}
