using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static ThreeBucket.Core.Services.BottomReversal.BrMath;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>V1.1 单股复盘：逐日 Evaluate（只用截至当日数据），输出阶段/信号/价位，并按验收第十一节逐项给出证据。</summary>
public static class BrTradeReplay
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(double? v, int nd = 3) => v is double x && !double.IsNaN(x) ? PyFmt(x, nd) : "-";
    private static string Pc(double? v) => v is double x && !double.IsNaN(x) ? PyFmtSigned(x * 100, 1) + "%" : "-";

    public sealed class Day { public BrResult Score = null!; public BrTrade Trade = null!; public string Exec = "", ExecNote = ""; public double? ExecEntry; public BrLevels? ExecLv; }

    public static async Task<(string Name, BrSeries S, List<Day> Days)> RunOneAsync(string code, BrConfig cfg, int days, CancellationToken ct)
    {
        var k = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), days + 200, 1, cfg.I("data.retries", 4), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{code} 日K获取失败");
        var s0 = BrScan.DropIncomplete(k.S, cfg);
        var s = s0.Take(s0.Count).Prepare(cfg);
        var list = new List<Day>();
        for (var i = Math.Max(cfg.I("model.min_bars", 80) - 1, s.Count - days); i < s.Count; i++)
        {
            var sc = BrModel.ScoreAt(s, i, cfg, code);
            if (sc.Total is null) continue;
            var tr = BrTradeEngine.Evaluate(s, i, cfg, code);
            tr.Group = BrTradeEngine.AssignGroup(tr, sc.Total.Value, cfg);
            var d = new Day { Score = sc, Trade = tr };
            if (tr.Signal is not null)
            {
                // 事后检查 T+1（复盘用；当日的判断本身不使用 T+1 数据）
                var (st, en, note, lv) = BrTradeEngine.ExecCheck(s, tr.Signal, cfg);
                d.Exec = st; d.ExecEntry = en; d.ExecNote = note; d.ExecLv = lv;
            }
            list.Add(d);
        }
        return (k.Name, s, list);
    }

    private static List<(string A, string B, int Len)> Periods(List<Day> days, Func<Day, bool> f)
    {
        var o = new List<(string, string, int)>(); string? a = null, b = null; var len = 0;
        foreach (var d in days)
        {
            if (f(d)) { a ??= d.Trade.Date; b = d.Trade.Date; len++; }
            else if (a is not null) { o.Add((a, b!, len)); a = null; len = 0; }
        }
        if (a is not null) o.Add((a, b!, len));
        return o;
    }

    public static async Task RunAsync(BrConfig cfg, string dataDir, IReadOnlyList<string> codes, int days, Action<string> log, CancellationToken ct = default)
    {
        var outDir = Path.Combine(dataDir, "bottom_reversal", "replay"); Directory.CreateDirectory(outDir);
        var L = new List<string>
        {
            "# S27 V1.1 三只股票历史信号复盘（交易阶段 / B1·B2·B3 / 介入价·止损·压力位·风险收益比）", "",
            $"> 逐日只用截至当日的前复权日K判断（无未来函数）；“T+1 检查”与“事后收益”只在复盘中使用、标注为事后数据。计划介入价均为模拟价格，非真实成交。参数：`{cfg.Path}`（version {cfg.Version}）。", "",
        };
        foreach (var code in codes)
        {
            var (name, s, dl) = await RunOneAsync(code, cfg, days, ct).ConfigureAwait(false);
            var di = new Dictionary<string, int>(); for (var j = 0; j < s.Count; j++) di[s.Date[j]] = j;
            var sb = new StringBuilder("date,close,total,rating,v1_stage,stage,signal,op_status,group,first_breakout_date,platform,entry,stop,resistance,reward_pct,risk_pct,rr,rr_label,risk_tags,exec_check,exec_entry_t1_open,exec_rr,fail_reason\r\n");
            foreach (var d in dl)
            {
                var t = d.Trade; var lv = t.Levels;
                sb.Append(string.Join(",", new[]
                {
                    t.Date, N(t.Close), d.Score.Total!.Value.ToString(Inv), d.Score.Rating, d.Score.Stage, t.Stage, t.SignalType, t.OpStatus, t.Group, t.FirstBreakoutDate ?? "",
                    N(t.PlatformLast ?? t.Platform), N(lv.Entry), N(lv.Stop), N(lv.Resistance), Pc(lv.RewardPct), Pc(lv.RiskPct), N(lv.Rr, 2), lv.RrLabel, string.Join("；", t.Tags),
                    d.Exec, N(d.ExecEntry), N(d.ExecLv?.Rr, 2), t.Stage == BrTradeEngine.S6 ? t.FailReason ?? "" : "",
                }.Select(BrReport.Esc))).Append("\r\n");
            }
            File.WriteAllText(Path.Combine(outDir, $"replay11_{code}.csv"), sb.ToString(), new UTF8Encoding(true));

            var sigs = dl.Where(d => d.Trade.Signal is not null).ToList();
            double? Fwd(int idx, int h) => idx + h < s.Count ? s.Close[idx + h] / s.Open[idx] - 1 : null;
            L.Add($"## {code} {name}（{dl.First().Trade.Date} ~ {dl.Last().Trade.Date}，{dl.Count} 个交易日）");
            L.Add("");
            L.Add("阶段天数：" + string.Join("，", dl.GroupBy(d => d.Trade.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
            L.Add("");
            var bottom = Periods(dl, d => d.Trade.Stage == BrTradeEngine.S1).Where(p => p.Len >= 2).ToList();
            L.Add($"**1. 底部形成阶段**：{(bottom.Count > 0 ? string.Join("；", bottom.Select(p => $"{p.A}~{p.B}({p.Len}日)")) : "未识别到（≥2日）")}");
            var firsts = sigs.Where(d => d.Trade.Signal!.Type == "B1" && d.Trade.Signal.NewEpisode).ToList();
            L.Add($"**2. 首次放量突破（新一轮）**：{(firsts.Count > 0 ? string.Join("；", firsts.Select(d => $"{d.Trade.Date}(平台 {N(d.Trade.Signal!.Platform)}，量比 {d.Trade.Signal.Conditions["volume_ratio"]}，涨幅 {Pc(d.Trade.Signal.Conditions["change"]?.GetValue<double>())})")) : "无")}；趋势中再突破 {sigs.Count(d => d.Trade.Signal!.Type == "B1" && !d.Trade.Signal.NewEpisode)} 次");
            var b2 = sigs.Where(d => d.Trade.Signal!.Type == "B2").ToList();
            var conf = Periods(dl, d => d.Trade.Stage == BrTradeEngine.S3).ToList();
            L.Add($"**3. 突破确认**：B2 {b2.Count} 次（{string.Join("、", b2.Select(d => d.Trade.Date + (d.Trade.Signal!.FarFromPlatform ? "[远离平台]" : "")))}）；突破确认阶段区间 {conf.Count} 段");
            var b3 = sigs.Where(d => d.Trade.Signal!.Type == "B3").ToList();
            L.Add($"**4. 缩量回踩**：B3 {b3.Count} 次（{string.Join("、", b3.Select(d => $"{d.Trade.Date}(回踩低 {d.Trade.Signal!.Conditions["pullback_low"]}，量/突破日 {d.Trade.Signal.Conditions["volume_ratio_vs_breakout"]}，止跌：{string.Join("/", ((JsonArray)d.Trade.Signal.Conditions["stabilization"]!).Select(x => x!.ToString()))})"))}）");
            var hotDays = dl.Where(d => d.Trade.OpStatus == "不追涨").ToList();
            var hotHigh = hotDays.Where(d => d.Score.Total >= 65).ToList();
            var hotSigCancel = sigs.Where(d => d.Exec.StartsWith("取消：信号日已过热")).ToList();
            var r5Big = dl.Where(d => d.Trade.R5 > cfg.D("trade.chase.surge_5d", 0.15)).ToList();
            var r5Entered = r5Big.Where(d => d.Exec == "可介入(模拟)").ToList();
            L.Add($"**5. 避免追高**：5日涨幅>15% 的交易日 {r5Big.Count} 个，其中给出可介入 {r5Entered.Count} 次；“不追涨”状态 {hotDays.Count} 日（其中趋势分≥65 的 {hotHigh.Count} 日{(hotHigh.Count > 0 ? "，例：" + string.Join("、", hotHigh.Take(5).Select(d => $"{d.Trade.Date}({d.Score.Total}分,{string.Join("/", d.Trade.Tags.Where(BrTradeEngine.IsHotTag))})")) : "")}）；信号日过热被取消 {hotSigCancel.Count} 次");
            L.Add($"**6. 明确信号**：B1 {sigs.Count(d => d.Trade.Signal!.Type == "B1")} / B2 {b2.Count} / B3 {b3.Count}，明细见下表");
            L.Add("**7/8. 介入价·止损·压力位·风险收益比**：见下表（信号日参考价=收盘；T+1 检查通过时给出模拟计划价=T+1 开盘并重算 R/R）");
            L.Add("");
            L.Add("| 信号日 | 信号 | 阶段 | 趋势分 | 平台 | 参考价 | 止损(口径) | 压力位(口径) | R/R(参考) | 风险标签 | T+1 检查 | 模拟计划价 | R/R(计划价) | 事后10日 | 事后20日 |");
            L.Add("|---|---|---|---:|---:|---:|---|---|---:|---|---|---:|---:|---:|---:|");
            foreach (var d in sigs)
            {
                var sg = d.Trade.Signal!; var lv = sg.Levels; var idx = di[d.Trade.Date];
                string f10 = d.ExecEntry is not null ? Pc(Fwd(idx + 1, 9)) : "-", f20 = d.ExecEntry is not null ? Pc(Fwd(idx + 1, 19)) : "-";
                L.Add($"| {sg.Date} | {sg.Type}{(sg.NewEpisode ? "(首次)" : "")} | {d.Trade.Stage} | {d.Score.Total} | {N(sg.Platform)} | {N(lv.Entry)} | {N(lv.Stop)}（{lv.StopBasis}） | {N(lv.Resistance)}（{lv.ResistanceBasis}） | {N(lv.Rr, 2)} | {string.Join("；", sg.Tags)} | {d.Exec}{(d.ExecNote.Length > 0 ? "：" + d.ExecNote : "")} | {N(d.ExecEntry)} | {N(d.ExecLv?.Rr, 2)} | {f10} | {f20} |");
            }
            var fails = Periods(dl, d => d.Trade.Stage == BrTradeEngine.S6);
            L.Add("");
            L.Add($"反转失败区间：{(fails.Count > 0 ? string.Join("；", fails.Select(p => $"{p.A}~{p.B}")) : "无")}");
            L.Add("");
            log($"[S27 V1.1] 复盘 {code} {name}: {dl.Count} 日，B1 {sigs.Count(d => d.Trade.Signal!.Type == "B1")} / B2 {b2.Count} / B3 {b3.Count}，底部形成段 {bottom.Count}");
        }
        L.Add("**9. 三种介入方式的收益与回撤**：见 `backtest_v11/bt11_report.md`（B1/B2/B3 × 持有期 × 止损方案，含 MAE/MFE/净值最大回撤）。");
        L.Add("**10. 未来函数**：`--br selftest` 对随机交易日比较“全量序列第 i 根”与“截断到第 i 根”的评分与交易阶段/信号/止损/压力位 JSON，必须完全一致。");
        File.WriteAllText(Path.Combine(outDir, "replay11_report.md"), string.Join("\n", L), new UTF8Encoding(false));
    }
}

/// <summary>
/// V1.1 回测：B1/B2/B3 独立统计，持有 5/10/20/40 日 × 默认止损/纯结构止损/固定 5%/8%/10%。
/// T 日收盘确认 → T+1 开盘按执行规则成交（高开>3%取消、B1 低开跌回平台取消、R/R 不足取消、过热不追）；
/// 停牌（次一交易日无K线）、涨停开盘不成交；跌停封死顺延；A股 T+1：买入当日不止损。成本含佣金/印花税/过户费/滑点。
/// 统计：胜率、均值、中位数、盈亏比、MAE、MFE、等权子账户组合净值最大回撤（时间/持续/恢复）、逐年、牛熊震荡。
/// </summary>
public static class BrTradeBacktest
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(double? v, int nd = 4) => v is double x && !double.IsNaN(x) ? PyFmt(x, nd) : "";
    private static string Pc(double? v) => v is double x && !double.IsNaN(x) ? PyFmtSigned(x * 100, 1) + "%" : "-";

    private sealed record Sig(string Code, string Type, int I, BrTradeSignal S, int Score, string Regime);
    private sealed class Trade
    {
        public string Code = "", Type = "", Stop = "", SignalDate = "", EntryDate = "", ExitDate = "", Reason = "", Year = "", Regime = "", Tags = "";
        public int Hold, E, X, Score; public double EntryPx, EntryRaw, StopPx, ExitPx, Gross, Net, Mae, Mfe, RrAtEntry;
    }

    private static double Cost(BrConfig cfg, bool sell)
    {
        var com = Math.Max(cfg.D("backtest.commission"), cfg.D("backtest.commission_min") / cfg.D("backtest.notional"));
        return cfg.D("backtest.slippage") + com + cfg.D("backtest.transfer_fee") + (sell ? cfg.D("backtest.stamp_tax_sell") : 0);
    }
    private static bool LimitUpOpen(BrSeries raw, int j, double lp) => raw.Open[j] >= raw.Close[j - 1] * (1 + lp) - 0.011;
    private static bool Locked(BrSeries raw, int j, double lp) => raw.High[j] - raw.Low[j] < 1e-6 && raw.Close[j] <= raw.Close[j - 1] * (1 - lp) + 0.011;

    private static BrSeries Align(BrSeries k, List<string> common)
    {
        var ix = new Dictionary<string, int>(); for (var j = 0; j < k.Count; j++) ix[k.Date[j]] = j;
        double[] P(double[] a) => common.Select(x => a[ix[x]]).ToArray();
        return new BrSeries { Date = common.ToArray(), Open = P(k.Open), Close = P(k.Close), High = P(k.High), Low = P(k.Low), Volume = P(k.Volume), Amount = P(k.Amount), Turnover = P(k.Turnover) };
    }

    private static (int X, double Px, string Why)? Simulate(BrSeries q, BrSeries raw, int e, int hold, double? stop, double lp)
    {
        var target = e + hold - 1; var pending = false;
        for (var j = e + 1; j < q.Count; j++)
        {
            var lk = Locked(raw, j, lp);
            if (pending) { if (!lk) return (j, q.Open[j], "跌停顺延卖出"); continue; }
            if (stop is double sp)
            {
                if (q.Open[j] <= sp) { if (lk) { pending = true; continue; } return (j, q.Open[j], "跳空低开止损"); }
                if (q.Low[j] <= sp) { if (lk) { pending = true; continue; } return (j, sp, "止损"); }
            }
            if (j >= target) { if (lk) { pending = true; continue; } return (j, q.Close[j], "持有到期"); }
        }
        return null;
    }

    public static async Task RunAsync(BrConfig cfg, string dataDir, IReadOnlyList<string> codes, int days, Action<string> log, CancellationToken ct = default)
    {
        var outDir = Path.Combine(dataDir, "bottom_reversal", "backtest_v11"); Directory.CreateDirectory(outDir);
        if (days <= 0) days = cfg.I("backtest_v11.days", 750);
        var idx = await BrMarketData.FetchKlineAsync(cfg.S("backtest_v11.regime_index_secid", "1.000985"), days + 300, 0, 3, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("指数日K获取失败");
        var ix = BrScan.DropIncomplete(idx.S, cfg);
        var maN = cfg.I("backtest_v11.regime_ma", 60); var sd = cfg.I("backtest_v11.regime_slope_days", 20); var thr = cfg.D("backtest_v11.regime_slope_threshold", 0.02);
        var ima = BrIndicators.Sma(ix.Close, maN);
        var regime = new Dictionary<string, string>(); var calNext = new Dictionary<string, string>();
        for (var j = 0; j < ix.Count; j++)
        {
            if (j + 1 < ix.Count) calNext[ix.Date[j]] = ix.Date[j + 1];
            if (j < sd || double.IsNaN(ima[j - sd])) continue;
            var sl = ima[j] / ima[j - sd] - 1;
            regime[ix.Date[j]] = ix.Close[j] > ima[j] && sl > thr ? "牛市" : ix.Close[j] < ima[j] && sl < -thr ? "熊市" : "震荡市";
        }
        var holds = cfg.Doubles("backtest_v11.hold_days").Select(x => (int)x).ToList();
        var stops = new List<string>();
        if (cfg.Node("backtest_v11.stops") is JsonArray sa) foreach (var x in sa) stops.Add(x is JsonValue v && v.TryGetValue<string>(out var str) ? str : PyRepr(x!.GetValue<double>()));
        var types = new[] { "B1", "B2", "B3" };
        var trades = new List<Trade>(); var cancels = new List<(string Code, string Type, string Date, string Why, string Tags)>();
        var signalsAll = new List<Sig>();
        var perCode = new Dictionary<string, (BrSeries Q, BrSeries Raw, int Start)>();
        double buyC = Cost(cfg, false), sellC = Cost(cfg, true);
        foreach (var code in codes)
        {
            var qk = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), days + 200, 1, 4, ct).ConfigureAwait(false);
            var rk = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), days + 200, 0, 4, ct).ConfigureAwait(false);
            if (qk is null || rk is null) throw new InvalidOperationException($"{code} K线失败");
            var q0 = BrScan.DropIncomplete(qk.S, cfg); var r0 = BrScan.DropIncomplete(rk.S, cfg);
            var common = q0.Date.Intersect(r0.Date).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var q = Align(q0, common).Prepare(cfg); var raw = Align(r0, common);
            var n = q.Count; var start = Math.Max(cfg.I("model.min_bars", 80) - 1, n - days);
            perCode[code] = (q, raw, start);
            var lp = BrMarketData.LimitPct(code);
            var sigs = new List<Sig>();
            for (var i = start; i < n - 1; i++)
            {
                var tr = BrTradeEngine.Evaluate(q, i, cfg, code);
                if (tr.Signal is null) continue;
                var sc = BrModel.ScoreAt(q, i, cfg, code).Total ?? 0;
                sigs.Add(new Sig(code, tr.Signal.Type, i, tr.Signal, sc, regime.GetValueOrDefault(q.Date[i], "未知")));
            }
            signalsAll.AddRange(sigs);
            // 执行检查（与组合无关，每个信号一次）
            var exec = new Dictionary<int, (double Entry, BrLevels Lv)>();
            foreach (var sg in sigs)
            {
                var e = sg.I + 1; string? why = null;
                if (calNext.TryGetValue(q.Date[sg.I], out var nx) && nx != q.Date[e]) why = "停牌：次一交易日无成交";
                else if (LimitUpOpen(raw, e, lp)) why = "涨停开盘无法成交";
                BrLevels? lv = null;
                if (why is null)
                {
                    var (st, en, note, l2) = BrTradeEngine.ExecCheck(q, sg.S, cfg);
                    if (en is null) why = st + (note.Length > 0 ? "（" + note + "）" : ""); else lv = l2;
                }
                if (why is not null) { cancels.Add((code, sg.Type, q.Date[sg.I], why, string.Join("；", sg.S.Tags))); continue; }
                exec[sg.I] = (q.Open[e], lv!);
            }
            foreach (var type in types)
                foreach (var hold in holds)
                    foreach (var stop in stops)
                    {
                        var busy = -1;
                        foreach (var sg in sigs.Where(x => x.Type == type))
                        {
                            if (!exec.TryGetValue(sg.I, out var ex)) continue;
                            var e = sg.I + 1; if (e <= busy) continue;
                            double stopPx = stop == "default" ? ex.Lv.Stop!.Value : stop == "structural" ? ex.Lv.StructStop!.Value : ex.Entry * (1 - double.Parse(stop, Inv));
                            var res = Simulate(q, raw, e, hold, stopPx, lp);
                            if (res is null) continue;   // 数据末尾未平仓，不计入
                            var (x, px, rsn) = res.Value;
                            busy = x;
                            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                            for (var j = e; j <= x; j++)
                            {
                                var stopExit = j == x && rsn.Contains("止损");
                                lo = Math.Min(lo, stopExit ? px : q.Low[j]);
                                if (!stopExit) hi = Math.Max(hi, q.High[j]);
                            }
                            var gross = px / ex.Entry - 1;
                            trades.Add(new Trade
                            {
                                Code = code, Type = type, Stop = StopName(stop), Hold = hold, SignalDate = q.Date[sg.I],
                                EntryDate = q.Date[e], ExitDate = q.Date[x], E = e, X = x, EntryPx = ex.Entry, EntryRaw = raw.Open[e], StopPx = stopPx, ExitPx = px, Reason = rsn,
                                Gross = gross, Net = (1 + gross) * (1 - sellC) / (1 + buyC) - 1, Mae = lo / ex.Entry - 1, Mfe = hi / ex.Entry - 1,
                                Year = q.Date[sg.I][..4], Regime = sg.Regime, Score = sg.Score, Tags = string.Join("；", sg.S.Tags), RrAtEntry = ex.Lv.Rr ?? double.NaN,
                            });
                        }
                    }
            log($"[S27 V1.1] 回测 {code}: B1 {sigs.Count(x => x.Type == "B1")} / B2 {sigs.Count(x => x.Type == "B2")} / B3 {sigs.Count(x => x.Type == "B3")} 个信号，可成交 {exec.Count}");
        }

        // ── 统计 ──
        var stopNames = stops.Select(StopName).ToList();
        var summary = new List<Dictionary<string, string>>(); var byYear = new List<Dictionary<string, string>>(); var byReg = new List<Dictionary<string, string>>();
        foreach (var type in types)
            foreach (var hold in holds)
                foreach (var stop in stopNames)
                {
                    var ts = trades.Where(t => t.Type == type && t.Hold == hold && t.Stop == stop).OrderBy(t => t.EntryDate, StringComparer.Ordinal).ToList();
                    var row = Basic(ts);
                    var dd = Equity(ts, perCode, buyC, sellC, out var yearDd);
                    row["signal"] = type; row["hold"] = hold.ToString(Inv); row["stop"] = stop;
                    foreach (var kv in dd) row[kv.Key] = kv.Value;
                    row["signals"] = signalsAll.Count(x => x.Type == type).ToString(Inv);
                    row["cancelled"] = cancels.Count(c => c.Type == type).ToString(Inv);
                    summary.Add(row);
                    foreach (var g in ts.GroupBy(t => t.Year).OrderBy(g => g.Key))
                    {
                        var r = Basic(g.ToList()); r["signal"] = type; r["hold"] = hold.ToString(Inv); r["stop"] = stop; r["year"] = g.Key;
                        r["year_max_dd"] = yearDd.TryGetValue(g.Key, out var y) ? N(y) : ""; byYear.Add(r);
                    }
                    foreach (var y in yearDd.Keys.Except(ts.Select(t => t.Year)).OrderBy(x => x))
                        byYear.Add(new Dictionary<string, string> { ["signal"] = type, ["hold"] = hold.ToString(Inv), ["stop"] = stop, ["year"] = y, ["trades"] = "0", ["year_max_dd"] = N(yearDd[y]) });
                    foreach (var g in ts.GroupBy(t => t.Regime).OrderBy(g => g.Key))
                    {
                        var r = Basic(g.ToList()); r["signal"] = type; r["hold"] = hold.ToString(Inv); r["stop"] = stop; r["regime"] = g.Key; byReg.Add(r);
                    }
                }
        string[] scol = ["signal", "hold", "stop", "signals", "cancelled", "trades", "win_rate", "avg_ret", "median_ret", "payoff_ratio", "avg_mae", "worst_mae", "avg_mfe", "best_mfe", "avg_hold_days",
            "max_dd", "max_dd_peak_date", "max_dd_trough_date", "max_dd_duration_days", "max_dd_recovery_date", "max_dd_recovery_days", "final_equity"];
        BrReport.WriteCsv(Path.Combine(outDir, "bt11_summary.csv"), summary, scol);
        BrReport.WriteCsv(Path.Combine(outDir, "bt11_by_year.csv"), byYear, ["signal", "hold", "stop", "year", "trades", "win_rate", "avg_ret", "median_ret", "payoff_ratio", "avg_mae", "avg_mfe", "year_max_dd"]);
        BrReport.WriteCsv(Path.Combine(outDir, "bt11_by_regime.csv"), byReg, ["signal", "hold", "stop", "regime", "trades", "win_rate", "avg_ret", "median_ret", "payoff_ratio", "avg_mae", "avg_mfe"]);
        BrReport.WriteCsv(Path.Combine(outDir, "bt11_trades.csv"), trades.Select(t => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["code"] = t.Code, ["signal"] = t.Type, ["hold"] = t.Hold.ToString(Inv), ["stop"] = t.Stop, ["signal_date"] = t.SignalDate, ["score"] = t.Score.ToString(Inv),
            ["entry_date"] = t.EntryDate, ["entry_px_qfq"] = N(t.EntryPx, 3), ["entry_px_raw"] = N(t.EntryRaw, 3), ["stop_px"] = N(t.StopPx, 3), ["rr_at_entry"] = N(t.RrAtEntry, 2),
            ["exit_date"] = t.ExitDate, ["exit_px"] = N(t.ExitPx, 3), ["exit_reason"] = t.Reason, ["gross_ret"] = N(t.Gross), ["net_ret"] = N(t.Net), ["mae"] = N(t.Mae), ["mfe"] = N(t.Mfe),
            ["hold_days"] = (t.X - t.E + 1).ToString(Inv), ["year"] = t.Year, ["regime"] = t.Regime, ["risk_tags"] = t.Tags,
        }), ["code", "signal", "hold", "stop", "signal_date", "score", "entry_date", "entry_px_qfq", "entry_px_raw", "stop_px", "rr_at_entry", "exit_date", "exit_px", "exit_reason", "gross_ret", "net_ret", "mae", "mfe", "hold_days", "year", "regime", "risk_tags"]);
        BrReport.WriteCsv(Path.Combine(outDir, "bt11_cancelled.csv"), cancels.Select(c => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        { ["code"] = c.Code, ["signal"] = c.Type, ["signal_date"] = c.Date, ["reason"] = c.Why, ["risk_tags"] = c.Tags }), ["code", "signal", "signal_date", "reason", "risk_tags"]);
        File.WriteAllText(Path.Combine(outDir, "bt11_report.md"), Md(summary, byYear, byReg, cancels, signalsAll, codes, days, cfg, holds, stopNames), new UTF8Encoding(false));
        log($"[S27 V1.1] 回测完成：成交记录 {trades.Count} 条（{types.Length}种信号×{holds.Count}持有期×{stops.Count}止损），取消 {cancels.Count} 个信号；输出 {outDir}");
    }

    private static Dictionary<string, string> Basic(List<Trade> ts)
    {
        var d = new Dictionary<string, string> { ["trades"] = ts.Count.ToString(Inv) };
        if (ts.Count == 0) return d;
        var r = ts.Select(t => t.Net).ToList(); var w = r.Where(x => x > 0).ToList(); var l = r.Where(x => x <= 0).ToList();
        var so = r.OrderBy(x => x).ToList(); var med = so.Count % 2 == 1 ? so[so.Count / 2] : (so[so.Count / 2 - 1] + so[so.Count / 2]) / 2;
        d["win_rate"] = N((double)w.Count / r.Count, 3); d["avg_ret"] = N(r.Average()); d["median_ret"] = N(med);
        d["payoff_ratio"] = l.Count > 0 && w.Count > 0 && l.Average() < 0 ? N(w.Average() / Math.Abs(l.Average()), 2) : "";
        d["avg_mae"] = N(ts.Average(t => t.Mae)); d["worst_mae"] = N(ts.Min(t => t.Mae)); d["avg_mfe"] = N(ts.Average(t => t.Mfe)); d["best_mfe"] = N(ts.Max(t => t.Mfe));
        d["avg_hold_days"] = N(ts.Average(t => t.X - t.E + 1), 1);
        return d;
    }

    /// <summary>等权子账户组合：每只股票一个子账户（初始 1，持仓时全仓、空仓时现金），每日收盘盯市，组合净值=各子账户均值。</summary>
    private static string StopName(string s) => s == "default" ? "默认止损" : s == "structural" ? "纯结构止损" : $"固定{PyFmt(double.Parse(s, Inv) * 100, 0)}%";

    private static Dictionary<string, string> Equity(List<Trade> ts, Dictionary<string, (BrSeries Q, BrSeries Raw, int Start)> perCode, double buyC, double sellC, out Dictionary<string, double> yearDd)
    {
        var curves = new List<Dictionary<string, double>>();
        var allDates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (code, (q, _, start)) in perCode)
        {
            var my = ts.Where(t => t.Code == code).OrderBy(t => t.E).ToList();
            var eq = new Dictionary<string, double>(); double cash = 1, units = 0; var k = 0;
            for (var j = start; j < q.Count; j++)
            {
                if (k < my.Count && my[k].E == j) { units = cash / (my[k].EntryPx * (1 + buyC)); cash = 0; }
                if (k < my.Count && my[k].X == j) { cash = units * my[k].ExitPx * (1 - sellC); units = 0; k++; }
                eq[q.Date[j]] = cash + units * q.Close[j];
                allDates.Add(q.Date[j]);
            }
            curves.Add(eq);
        }
        var dates = allDates.ToList(); var port = new List<double>();
        var last = curves.Select(_ => 1.0).ToArray();
        foreach (var d in dates)
        {
            for (var c = 0; c < curves.Count; c++) if (curves[c].TryGetValue(d, out var v)) last[c] = v;
            port.Add(last.Average());
        }
        yearDd = new Dictionary<string, double>();
        var o = new Dictionary<string, string>();
        if (port.Count == 0) return o;
        double peak = port[0], mdd = 0; int pk = 0, pkAt = 0, tr = 0;
        for (var j = 0; j < port.Count; j++)
        {
            if (port[j] > peak) { peak = port[j]; pk = j; }
            var ddv = port[j] / peak - 1;
            if (ddv < mdd) { mdd = ddv; tr = j; pkAt = pk; }
        }
        o["max_dd"] = N(mdd); o["final_equity"] = N(port[^1]);
        if (mdd < 0)
        {
            o["max_dd_peak_date"] = dates[pkAt]; o["max_dd_trough_date"] = dates[tr]; o["max_dd_duration_days"] = (tr - pkAt).ToString(Inv);
            var rec = -1; for (var j = tr + 1; j < port.Count; j++) if (port[j] >= port[pkAt]) { rec = j; break; }
            o["max_dd_recovery_date"] = rec >= 0 ? dates[rec] : "未恢复"; o["max_dd_recovery_days"] = rec >= 0 ? (rec - tr).ToString(Inv) : "未恢复";
        }
        foreach (var g in dates.Select((d, j) => (d, j)).GroupBy(x => x.d[..4]))
        {
            var first = g.First().j; var pkv = first > 0 ? port[first - 1] : port[first]; double m = 0;
            foreach (var (_, j) in g) { pkv = Math.Max(pkv, port[j]); m = Math.Min(m, port[j] / pkv - 1); }
            yearDd[g.Key] = m;
        }
        return o;
    }

    private static string Md(List<Dictionary<string, string>> sum, List<Dictionary<string, string>> byYear, List<Dictionary<string, string>> byReg,
        List<(string Code, string Type, string Date, string Why, string Tags)> cancels, List<Sig> sigs, IReadOnlyList<string> codes, int days, BrConfig cfg, List<int> holds, List<string> stops)
    {
        string G(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) && v.Length > 0 ? v : "-";
        string P(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, Inv, out var x) ? PyFmtSigned(x * 100, 1) + "%" : "-";
        string W(Dictionary<string, string> r) => r.TryGetValue("win_rate", out var v) && double.TryParse(v, NumberStyles.Float, Inv, out var x) ? PyFmt(x * 100, 0) + "%" : "-";
        var L = new List<string>
        {
            "# S27 V1.1 · B1/B2/B3 介入方式回测对比（含 MAE / MFE / 净值最大回撤）", "",
            $"> **样本极小（仅 {string.Join(", ", codes)}，约 {days} 个交易日），只能说明程序口径，不能据此判断哪种介入方式长期更优。** 参数需全市场、多年、含退市股样本验证。", "",
            "- 执行：T 日收盘确认信号 → T+1 开盘成交（前复权价计收益、不复权价判涨跌停）；高开>3% 取消；B1 低开跌回平台取消；B2 远离平台>10% 不追；信号日过热（5日>15%/MA20乖离>10%/RSI>75/连续放量大阳）不追；以 T+1 开盘价重算止损与 R/R，R/R<1.5 取消；默认止损=结构止损(缓冲 max(2%,0.5×ATR))与固定8%取更宽、最多10%（参数 trade.stop）；停牌、涨停开盘不成交。",
            "- 卖出：A股 T+1（买入当日不触发止损）；止损价触发按止损价、跳空低开按开盘价；跌停封死顺延到次日开盘；到期按收盘。不在回踩最低价成交，不用未来数据确认回踩。",
            $"- 成本：佣金 {PyFmt(cfg.D("backtest.commission") * 100, 3)}%(最低 {PyRepr(cfg.D("backtest.commission_min"))} 元，按每笔 {PyFmt(cfg.D("backtest.notional"), 0)} 元折算)、过户费、卖出印花税 {PyFmt(cfg.D("backtest.stamp_tax_sell") * 100, 2)}%、双边滑点 {PyFmt(cfg.D("backtest.slippage") * 100, 2)}%。",
            "- 最大回撤：每只股票一个等权子账户（持仓全仓/空仓现金，每日收盘盯市），组合净值=子账户均值；同一股票同一组合不重叠持仓。MAE/MFE 为持仓期内相对买入价的最大不利/有利波动。",
            $"- 市场环境：中证全指相对 MA{cfg.I("backtest_v11.regime_ma", 60)} 位置与 {cfg.I("backtest_v11.regime_slope_days", 20)} 日斜率（±{PyFmt(cfg.D("backtest_v11.regime_slope_threshold", 0.02) * 100, 0)}%）划分 牛市/熊市/震荡市（按信号日）。", "",
            "## 信号与执行", "",
            "| 信号 | 信号数 | 可成交 | 取消 | 主要取消原因 |", "|---|---:|---:|---:|---|",
        };
        foreach (var t in new[] { "B1", "B2", "B3" })
        {
            var cs = cancels.Where(c => c.Type == t).ToList();
            var why = string.Join("；", cs.GroupBy(c => c.Why.Split('（')[0]).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            L.Add($"| {t} | {sigs.Count(x => x.Type == t)} | {sigs.Count(x => x.Type == t) - cs.Count} | {cs.Count} | {(why.Length > 0 ? why : "-")} |");
        }
        L.Add("");
        L.Add("## 全部组合（信号 × 持有期 × 止损）");
        L.Add("");
        L.Add("| 信号 | 持有 | 止损 | 笔数 | 胜率 | 平均 | 中位数 | 盈亏比 | 平均MAE | 最差MAE | 平均MFE | 净值最大回撤 | 回撤区间 | 持续(日) | 恢复 |");
        L.Add("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---|");
        foreach (var r in sum)
            L.Add($"| {r["signal"]} | {r["hold"]} | {r["stop"]} | {r["trades"]} | {W(r)} | {P(r, "avg_ret")} | {P(r, "median_ret")} | {G(r, "payoff_ratio")} | {P(r, "avg_mae")} | {P(r, "worst_mae")} | {P(r, "avg_mfe")} | {P(r, "max_dd")} | {G(r, "max_dd_peak_date")}→{G(r, "max_dd_trough_date")} | {G(r, "max_dd_duration_days")} | {G(r, "max_dd_recovery_date")} |");
        L.Add("");
        var keyHold = holds.Contains(20) ? 20 : holds[^1]; var keyStop = stops[0];
        L.Add($"## 逐年表现（持有 {keyHold} 日 · {keyStop}）");
        L.Add("");
        L.Add("| 信号 | 年份 | 笔数 | 胜率 | 平均 | 中位数 | 平均MAE | 平均MFE | 当年净值最大回撤 |"); L.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in byYear.Where(r => r["hold"] == keyHold.ToString(Inv) && r["stop"] == keyStop).OrderBy(r => r["signal"]).ThenBy(r => r["year"]))
            L.Add($"| {r["signal"]} | {r["year"]} | {r["trades"]} | {W(r)} | {P(r, "avg_ret")} | {P(r, "median_ret")} | {P(r, "avg_mae")} | {P(r, "avg_mfe")} | {P(r, "year_max_dd")} |");
        L.Add("");
        L.Add($"## 牛市 / 熊市 / 震荡市（持有 {keyHold} 日 · {keyStop}）");
        L.Add("");
        L.Add("| 信号 | 市场 | 笔数 | 胜率 | 平均 | 中位数 | 平均MAE | 平均MFE |"); L.Add("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var r in byReg.Where(r => r["hold"] == keyHold.ToString(Inv) && r["stop"] == keyStop))
            L.Add($"| {r["signal"]} | {r["regime"]} | {r["trades"]} | {W(r)} | {P(r, "avg_ret")} | {P(r, "median_ret")} | {P(r, "avg_mae")} | {P(r, "avg_mfe")} |");
        L.Add("");
        L.Add("_明细：bt11_trades.csv（每笔含介入/止损/退出价、MAE/MFE、退出原因）、bt11_cancelled.csv（未成交信号及原因）、bt11_by_year.csv、bt11_by_regime.csv。止盈规则未预设，可在此框架下另行加测。_");
        return string.Join("\n", L);
    }
}
