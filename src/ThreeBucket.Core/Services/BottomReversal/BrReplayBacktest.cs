using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>单股历史复盘：逐日只用截至当日的数据评分（无未来函数）。</summary>
public static class BrReplay
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<(BrMarketData.Kline K, BrSeries S, List<BrResult> Rows)> DailyScoresAsync(string code, BrConfig cfg, int days = 500, int warmup = 130, CancellationToken ct = default)
    {
        var k = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), days + warmup, 1, cfg.I("data.retries", 4), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{code} 日K获取失败");
        var s0 = BrScan.DropIncomplete(k.S, cfg);
        var s = s0.Take(s0.Count).Prepare(cfg);
        var rows = new List<BrResult>();
        for (var i = Math.Max(cfg.I("model.min_bars", 80) - 1, s.Count - days); i < s.Count; i++)
        {
            var r = BrModel.ScoreAt(s, i, cfg, code);
            if (r.Total is null) continue;
            rows.Add(r);
        }
        return (k, s, rows);
    }

    public static JsonObject Summarize(string code, string name, BrSeries s, List<BrResult> rows)
    {
        var c = s.Close; var d = s.Date;
        var di = new Dictionary<string, int>(); for (var j = 0; j < d.Length; j++) di[d[j]] = j;
        var events = new List<string>(); string? lastBo = null;
        var bottom = new List<(string A, string B)>(); (string A, string B)? cur = null;
        foreach (var r in rows)
        {
            if (r.Stage == "底部形成中") cur = cur is null ? (r.Date, r.Date) : (cur.Value.A, r.Date);
            else if (cur is not null) { bottom.Add(cur.Value); cur = null; }
            if (r.BreakoutIndex is int bi && d[bi] != lastBo)
            {
                lastBo = d[bi];
                if (d[bi] == r.Date) events.Add(d[bi]);   // 当天确认的新突破；滑出追溯窗口的“续接”不算
            }
        }
        if (cur is not null) bottom.Add(cur.Value);
        var byDate = rows.ToDictionary(r => r.Date);
        var outArr = new JsonArray();
        foreach (var ev in events)
        {
            var t = di[ev];
            byDate.TryGetValue(ev, out var r0);
            var path = new List<(string D, string St, int Tot)>();
            for (var k = t; k < Math.Min(d.Length, t + 11); k++)
                if (byDate.TryGetValue(d[k], out var rr) && (path.Count == 0 || path[^1].St != rr.Stage)) path.Add((d[k], rr.Stage, rr.Total!.Value));
            double? Fwd(int hz) => t + hz < c.Length ? BrMath.R(c[t + hz] / c[t] - 1) : null;
            var st10 = path.Select(p => p.St).ToHashSet();
            var failed10 = st10.Contains("反转失败");
            var firstConf = path.FirstOrDefault(q => q.St is "突破确认" or "回踩确认" or "趋势形成").D ?? "9999";
            var confirmed = st10.Overlaps(["突破确认", "回踩确认", "趋势形成"]) && !path.Any(p => p.St == "反转失败" && string.CompareOrdinal(p.D, firstConf) < 0);
            var f20 = Fwd(20);
            var kind = failed10 && !confirmed ? "短期反弹/假突破（10日内跌回平台）"
                : confirmed && f20 is > 0 && !failed10 ? "强势反转（确认且20日收益为正）"
                : confirmed ? "确认后走弱" : "待观察/数据不足";
            double? pre10 = t >= 10 ? BrMath.R(c[t] / c[t - 10] - 1) : null;
            outArr.Add(new JsonObject
            {
                ["breakout_date"] = ev, ["score_on_breakout_day"] = r0?.Total, ["stage_on_breakout_day"] = r0?.Stage,
                ["flags_on_breakout_day"] = r0 is null ? "" : string.Join("；", r0.Flags), ["pre_breakout_10d_return"] = pre10,
                ["chase_risk_flag"] = r0?.ChaseRisk == true, ["stage_path_10d"] = string.Join(" → ", path.Select(p => $"{p.D}:{p.St}({p.Tot})")),
                ["fwd5"] = Fwd(5), ["fwd10"] = Fwd(10), ["fwd20"] = f20, ["fwd40"] = Fwd(40), ["classification"] = kind,
            });
        }
        var bp = bottom.Where(p => di[p.B] - di[p.A] >= 2).ToList();
        if (bp.Count == 0) bp = bottom;
        var best = rows.Count > 0 ? rows.OrderByDescending(r => r.Total).First() : null;
        return new JsonObject
        {
            ["code"] = code, ["name"] = name, ["first_day"] = rows.FirstOrDefault()?.Date, ["last_day"] = rows.LastOrDefault()?.Date,
            ["bottom_forming_periods"] = new JsonArray(bp.Select(p => (JsonNode?)new JsonArray(p.A, p.B)).ToArray()),
            ["breakouts"] = outArr, ["max_score"] = best?.Total, ["max_score_date"] = best?.Date,
        };
    }

    public static async Task<List<JsonObject>> RunAsync(BrConfig cfg, string dataDir, IReadOnlyList<string> codes, int days, Action<string> log, CancellationToken ct = default)
    {
        var outDir = Path.Combine(dataDir, "bottom_reversal", "replay"); Directory.CreateDirectory(outDir);
        var sums = new List<JsonObject>();
        foreach (var code in codes)
        {
            var (k, s, rows) = await DailyScoresAsync(code, cfg, days, 130, ct).ConfigureAwait(false);
            var sb = new StringBuilder("date,close,total,A,B,C,D,E,stage,rating,breakout_date,days_since_breakout,flags\r\n");
            foreach (var r in rows)
            {
                var ms = r.ModuleScores;
                sb.Append(string.Join(",", new[] { r.Date, BrReport.Num(BrMath.R(r.Close, 3)), r.Total!.Value.ToString(Inv), ms["A"].ToString(Inv), ms["B"].ToString(Inv), ms["C"].ToString(Inv),
                    ms["D"].ToString(Inv), ms["E"].ToString(Inv), r.Stage, r.Rating, r.BreakoutDate ?? "", r.DaysSinceBreakout?.ToString(Inv) ?? "", string.Join("；", r.Flags) }.Select(BrReport.Esc))).Append("\r\n");
            }
            File.WriteAllText(Path.Combine(outDir, $"replay_{code}.csv"), sb.ToString(), new UTF8Encoding(true));
            var sm = Summarize(code, k.Name, s, rows);
            sm["source"] = k.Source; sm["fetched_at"] = k.FetchedAt;
            sums.Add(sm);
            log($"[S27] 复盘 {code} {k.Name}: {rows.Count} 个交易日，突破事件 {((JsonArray)sm["breakouts"]!).Count} 次");
        }
        var arr = new JsonArray(sums.Select(x => (JsonNode?)x.DeepClone()).ToArray());
        File.WriteAllText(Path.Combine(outDir, "replay_summary.json"), arr.ToJsonString(BrReport.JsonOpts), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "replay_report.md"), Md(sums), new UTF8Encoding(false));
        return sums;
    }

    private static string F(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<double>(out var x) ? (BrMath.PyFmtSigned(x * 100, 1) + "%") : "-";

    private static string Md(List<JsonObject> sums)
    {
        var L = new List<string> { "# S27 增强版底部反转 · 历史信号复盘", "", "> 逐日只使用截至当日的K线评分（无未来函数）；前瞻收益仅用于复盘评估。前复权价格。", "" };
        foreach (var s in sums)
        {
            L.Add($"## {s["code"]} {s["name"]}（{s["first_day"]} ~ {s["last_day"]}）");
            L.Add($"- 区间最高分 {s["max_score"]}（{s["max_score_date"]}）");
            var bp = ((JsonArray)s["bottom_forming_periods"]!).Select(p => $"{p![0]}~{p[1]}").ToList();
            L.Add("- 底部形成中区间：" + (bp.Count > 0 ? string.Join("；", bp) : "无"));
            L.Add("");
            L.Add("| 突破日 | 当日分 | 前10日涨幅 | 追涨风险 | 10日阶段路径 | 5日 | 10日 | 20日 | 40日 | 判定 |");
            L.Add("|---|---:|---:|---|---|---:|---:|---:|---:|---|");
            foreach (var b in (JsonArray)s["breakouts"]!)
                L.Add($"| {b!["breakout_date"]} | {b["score_on_breakout_day"]?.ToString() ?? "None"} | {F(b["pre_breakout_10d_return"])} | {(b["chase_risk_flag"]!.GetValue<bool>() ? "是：" + b["flags_on_breakout_day"] : "否")} | {b["stage_path_10d"]} | {F(b["fwd5"])} | {F(b["fwd10"])} | {F(b["fwd20"])} | {F(b["fwd40"])} | {b["classification"]} |");
            L.Add("");
        }
        return string.Join("\n", L);
    }
}

/// <summary>
/// 回测引擎（骨架，仅在指定股票上运行；不是全市场无幸存者偏差回测）。
/// T 日收盘确认 → T+1 开盘成交；成本含佣金(最低5元)/印花税(卖)/过户费/滑点；涨停开盘不成交、跌停封死顺延卖出。
/// 收益用前复权价比值（含分红效应），涨跌停判断用不复权实际价。
/// </summary>
public static class BrBacktest
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static double Cost(BrConfig cfg, bool sell)
    {
        var com = Math.Max(cfg.D("backtest.commission"), cfg.D("backtest.commission_min") / cfg.D("backtest.notional"));
        var c = cfg.D("backtest.slippage") + com + cfg.D("backtest.transfer_fee");
        return c + (sell ? cfg.D("backtest.stamp_tax_sell") : 0);
    }

    private static bool LimitUpOpen(BrSeries raw, int j, double lp) => raw.Open[j] >= raw.Close[j - 1] * (1 + lp) - 0.011;
    private static bool LockedDown(BrSeries raw, int j, double lp) => raw.High[j] - raw.Low[j] < 1e-6 && raw.Close[j] <= raw.Close[j - 1] * (1 - lp) + 0.011;

    private static (int Exit, double Gross)? Simulate(BrSeries q, BrSeries raw, int entry, (string Kind, double Val) rule, BrConfig cfg, double lp)
    {
        var n = q.Count; var eq = q.Open[entry]; var maxh = cfg.I("backtest.max_hold_for_stops", 40);
        if (rule.Kind == "hold")
        {
            var j = entry + (int)rule.Val - 1;
            while (j < n && LockedDown(raw, j, lp)) j++;
            if (j >= n) return null;
            return (j, q.Close[j] / eq - 1);
        }
        var mx = q.Close[entry]; var pending = false;
        for (var j = entry; j < n; j++)
        {
            if (pending)
            {
                if (!LockedDown(raw, j, lp)) return (j, q.Open[j] / eq - 1);
            }
            else if (rule.Kind == "stop")
            {
                var sp = eq * (1 - rule.Val);
                if (q.Low[j] <= sp && !LockedDown(raw, j, lp)) return (j, (j > entry ? Math.Min(q.Open[j], sp) : sp) / eq - 1);
                if (q.Low[j] <= sp) pending = true;
            }
            else if (rule.Kind == "trail")
            {
                mx = Math.Max(mx, q.Close[j]);
                if (q.Close[j] <= mx * (1 - rule.Val)) pending = true;
            }
            if (j - entry + 1 >= maxh && !pending) return (j, q.Close[j] / eq - 1);
        }
        return null;
    }

    private static BrSeries Align(BrSeries k, List<string> common)
    {
        var ix = new Dictionary<string, int>(); for (var j = 0; j < k.Count; j++) ix[k.Date[j]] = j;
        double[] P(double[] a) => common.Select(x => a[ix[x]]).ToArray();
        return new BrSeries { Date = common.ToArray(), Open = P(k.Open), Close = P(k.Close), High = P(k.High), Low = P(k.Low), Volume = P(k.Volume), Amount = P(k.Amount), Turnover = P(k.Turnover) };
    }

    private sealed record Trade(string Code, string Strategy, string Rule, string SignalDate, int Score, string Status,
        string EntryDate = "", double EntryPriceRaw = 0, string ExitDate = "", double Gross = 0, double Net = 0, string Year = "", string Market = "");

    private static JsonObject Stats(List<double> rets)
    {
        if (rets.Count == 0) return new JsonObject { ["trades"] = 0 };
        var w = rets.Where(x => x > 0).ToList(); var l = rets.Where(x => x <= 0).ToList();
        double eqv = 1, peak = double.NegativeInfinity, mdd = 0;   // 与 numpy cumprod / maximum.accumulate 一致：峰值从第一笔后的净值起算
        foreach (var x in rets) { eqv *= 1 + x; peak = Math.Max(peak, eqv); mdd = Math.Min(mdd, eqv / peak - 1); }
        var aw = w.Count > 0 ? BrMath.NpMean(w) : 0.0; var al = l.Count > 0 ? BrMath.NpMean(l) : 0.0;
        var sorted = rets.OrderBy(x => x).ToList();
        var med = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return new JsonObject
        {
            ["trades"] = rets.Count, ["win_rate"] = BrMath.PyRound((double)w.Count / rets.Count, 3), ["avg_ret"] = BrMath.PyRound(BrMath.NpMean(rets), 4),
            ["median_ret"] = BrMath.PyRound(med, 4), ["payoff_ratio"] = al < 0 ? BrMath.PyRound(aw / Math.Abs(al), 2) : null,
            ["max_single_loss"] = BrMath.PyRound(rets.Min(), 4), ["max_drawdown_seq"] = BrMath.PyRound(mdd, 4), ["avg_win"] = BrMath.PyRound(aw, 4), ["avg_loss"] = BrMath.PyRound(al, 4),
        };
    }

    public static async Task<List<JsonObject>> RunAsync(BrConfig cfg, string dataDir, IReadOnlyList<string> codes, int days, Action<string> log, CancellationToken ct = default)
    {
        var outDir = Path.Combine(dataDir, "bottom_reversal", "backtest"); Directory.CreateDirectory(outDir);
        var idx = await BrMarketData.FetchKlineAsync("1.000985", days + 160, 0, 3, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("中证全指日K失败");
        var ima = BrIndicators.Sma(idx.S.Close, 20);
        var env = new Dictionary<string, string>();
        for (var j = 0; j < idx.S.Count; j++) if (!double.IsNaN(ima[j])) env[idx.S.Date[j]] = idx.S.Close[j] > ima[j] ? "指数在MA20上" : "指数在MA20下";
        var rules = cfg.Doubles("backtest.hold_days").Select(x => ("hold", x))
            .Concat(cfg.Doubles("backtest.stop_losses").Select(x => ("stop", x)))
            .Concat(cfg.Doubles("backtest.trailing_stops").Select(x => ("trail", x))).ToList();
        string RuleName((string K, double V) r) => r.K + (r.K == "hold" ? ((int)r.V).ToString(Inv) : BrMath.PyRepr(r.V));
        var trades = new List<Trade>();
        var minScore = cfg.D("backtest.entry_min_score", 50);
        foreach (var code in codes)
        {
            var qk = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), days + 130, 1, 4, ct).ConfigureAwait(false);
            var rk = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), days + 130, 0, 4, ct).ConfigureAwait(false);
            if (qk is null || rk is null) throw new InvalidOperationException($"{code} K线失败");
            var q0 = BrScan.DropIncomplete(qk.S, cfg); var r0 = BrScan.DropIncomplete(rk.S, cfg);
            var common = q0.Date.Intersect(r0.Date).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var q = Align(q0, common).Prepare(cfg); var raw = Align(r0, common);
            var sigA = new List<(int I, int Sc)>(); var sigB = new List<(int I, int Sc)>();
            var n = common.Count;
            for (var i = Math.Max(cfg.I("model.min_bars", 80) - 1, n - days); i < n - 1; i++)
            {
                var r = BrModel.ScoreAt(q, i, cfg, code);
                if (r.Total is null) continue;
                if (r.BreakoutIndex == i && r.Total >= minScore && !r.ChaseRisk) sigA.Add((i, r.Total.Value));
                var nn = r.DaysSinceBreakout;
                if (r.BreakoutIndex is not null && nn >= cfg.I("backtest.retest_wait_min", 3) && nn <= cfg.I("backtest.retest_wait_max", 5) && r.Stage != "反转失败")
                {
                    var plat = r.Modules["B"]!["B2"]!["platform_close_high20"]!.GetValue<double>();
                    var vpr = r.VolumePullbackRatio;
                    if (q.Low[i] <= plat * (1 + cfg.D("backtest.retest_near_platform", 0.02)) && q.Close[i] >= plat && vpr is double vp && vp < cfg.D("backtest.retest_volume_ratio", 0.7))
                        sigB.Add((i, r.Total.Value));
                }
            }
            var lp = BrMarketData.LimitPct(code);
            foreach (var (strat, lst) in new[] { ("A", sigA), ("B", sigB) })
                foreach (var rule in rules)
                {
                    var busy = -1;
                    foreach (var (i, sc) in lst)
                    {
                        var e = i + 1;
                        if (e <= busy || e >= q.Count) continue;
                        if (LimitUpOpen(raw, e, lp)) { trades.Add(new Trade(code, strat, RuleName(rule), q.Date[i], sc, "涨停开盘未成交")); continue; }
                        var res = Simulate(q, raw, e, rule, cfg, lp);
                        if (res is null) continue;
                        var (x, gross) = res.Value;
                        var net = (1 + gross) * (1 - Cost(cfg, true)) / (1 + Cost(cfg, false)) - 1;
                        busy = x;
                        trades.Add(new Trade(code, strat, RuleName(rule), q.Date[i], sc, "成交", q.Date[e], raw.Open[e], q.Date[x],
                            BrMath.PyRound(gross, 4), BrMath.PyRound(net, 4), q.Date[i][..4], env.GetValueOrDefault(q.Date[i], "未知")));
                    }
                }
            log($"[S27] 回测 {code}: 突破信号 {sigA.Count}，回踩信号 {sigB.Count}");
        }
        var filled = trades.Where(t => t.Status == "成交").ToList();
        var allD = filled.Select(t => t.SignalDate).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var split = allD.Count > 0 ? allD[(int)(allD.Count * (1 - cfg.D("backtest.oos_fraction", 0.4)))] : null;
        var rows = new List<JsonObject>();
        foreach (var strat in new[] { "A", "B" })
            foreach (var rule in rules)
            {
                var rn = RuleName(rule);
                var ts = filled.Where(t => t.Strategy == strat && t.Rule == rn).OrderBy(t => t.ExitDate, StringComparer.Ordinal).ToList();
                JsonObject Row(string sample, List<Trade> sub, bool extra)
                {
                    var o = new JsonObject { ["strategy"] = strat == "A" ? "A突破买入" : "B回踩买入", ["rule"] = rn, ["sample"] = sample };
                    foreach (var kv in Stats(sub.Select(t => t.Net).ToList())) o[kv.Key] = kv.Value?.DeepClone();
                    if (extra)
                    {
                        o["signals_per_year"] = new JsonObject(sub.GroupBy(t => t.Year).OrderBy(g => g.Key).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))).PyJson();
                        o["win_by_market"] = new JsonObject(sub.GroupBy(t => t.Market).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)BrMath.PyRound(g.Average(t => t.Net > 0 ? 1.0 : 0.0), 2)))).PyJson();
                        var byScore = new JsonObject();
                        foreach (var (k, lo, hi) in new[] { ("50-64", 50, 65), ("65-79", 65, 80), ("80+", 80, 101) })
                        {
                            var g = sub.Where(t => t.Score >= lo && t.Score < hi).ToList();
                            if (g.Count > 0) byScore[k] = BrMath.PyRound(BrMath.NpMean(g.Select(t => t.Net).ToArray()), 4);
                        }
                        o["avg_by_score"] = byScore.PyJson();
                    }
                    return o;
                }
                rows.Add(Row("全部", ts, true));
                if (split is not null)
                {
                    rows.Add(Row($"样本内(<{split})", ts.Where(t => string.CompareOrdinal(t.SignalDate, split) < 0).ToList(), false));
                    rows.Add(Row($"样本外(>={split})", ts.Where(t => string.CompareOrdinal(t.SignalDate, split) >= 0).ToList(), false));
                }
            }
        string[] cols = ["strategy", "rule", "sample", "trades", "win_rate", "avg_ret", "median_ret", "payoff_ratio", "max_single_loss", "max_drawdown_seq", "avg_win", "avg_loss", "signals_per_year", "win_by_market", "avg_by_score"];
        BrReport.WriteCsv(Path.Combine(outDir, "bt_summary.csv"), rows.Select(r => (IReadOnlyDictionary<string, string>)cols.ToDictionary(c => c, c => NodeStr(r[c]))), cols);
        string[] tcols = ["code", "strategy", "rule", "signal_date", "score", "entry_date", "entry_price_raw", "exit_date", "gross_ret", "net_ret", "year", "market", "status"];
        BrReport.WriteCsv(Path.Combine(outDir, "bt_trades.csv"), trades.Select(t => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["code"] = t.Code, ["strategy"] = t.Strategy, ["rule"] = t.Rule, ["signal_date"] = t.SignalDate, ["score"] = t.Score.ToString(Inv),
            ["entry_date"] = t.EntryDate, ["entry_price_raw"] = t.Status == "成交" ? BrMath.PyRepr(t.EntryPriceRaw) : "", ["exit_date"] = t.ExitDate,
            ["gross_ret"] = t.Status == "成交" ? BrMath.PyRepr(t.Gross) : "", ["net_ret"] = t.Status == "成交" ? BrMath.PyRepr(t.Net) : "",
            ["year"] = t.Year, ["market"] = t.Market, ["status"] = t.Status,
        }), tcols);
        File.WriteAllText(Path.Combine(outDir, "bt_report.md"), Md(rows, codes, days, split, cfg), new UTF8Encoding(false));
        return rows;
    }

    /// <summary>配置值按 Python str() 显示（JSON 里写 5.0 显示 5.0，写 5 显示 5）。</summary>
    private static string CfgStr(BrConfig cfg, string path)
    {
        var n = cfg.Node(path);
        if (n is JsonValue v && v.TryGetValue<System.Text.Json.JsonElement>(out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            var raw = e.GetRawText();
            return raw.Contains('.') || raw.Contains('e') || raw.Contains('E') ? BrMath.PyRepr(e.GetDouble()) : raw;
        }
        return n?.ToJsonString() ?? "";
    }

    private static string NodeStr(JsonNode? n)
    {
        if (n is null) return "";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<int>(out var i)) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue<long>(out var l)) return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var d)) return BrMath.PyRepr(d);   // 与 Python csv 写 float 一致（0.0 / 1.0）
        }
        return n.ToJsonString();
    }

    private static string Md(List<JsonObject> rows, IReadOnlyList<string> codes, int days, string? split, BrConfig cfg)
    {
        string P(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<double>(out var x) ? (BrMath.PyFmtSigned(x * 100, 1) + "%") : "-";
        var L = new List<string>
        {
            "# S27 回测报告（仅限指定股票，样本极小，不能据此判断策略有效）", "",
            $"- 股票：{string.Join(", ", codes)}；窗口约 {days} 个交易日；样本内/外分界 {split}",
            $"- 成本：佣金 {BrMath.PyFmt(cfg.D("backtest.commission") * 100, 3)}%(最低{CfgStr(cfg, "backtest.commission_min")}元，按每笔{BrMath.PyFmt(cfg.D("backtest.notional"), 0)}元折算) + 过户费 {BrMath.PyFmt(cfg.D("backtest.transfer_fee") * 100, 3)}% 双边 + 印花税 {BrMath.PyFmt(cfg.D("backtest.stamp_tax_sell") * 100, 2)}% 卖出 + 滑点 {BrMath.PyFmt(cfg.D("backtest.slippage") * 100, 2)}% 双边",
            "- **未完成**：全市场5年、含退市股的无幸存者偏差股票池回测；按行业统计；滚动窗口优化。", "",
            "| 策略 | 规则 | 样本 | 笔数 | 胜率 | 平均 | 中位 | 盈亏比 | 最大单笔亏损 | 顺序最大回撤 |", "|---|---|---|---:|---:|---:|---:|---:|---:|---:|",
        };
        foreach (var r in rows)
        {
            var tr = r["trades"]!.GetValue<int>();
            if (tr == 0) { L.Add($"| {r["strategy"]} | {r["rule"]} | {r["sample"]} | 0 | - | - | - | - | - | - |"); continue; }
            L.Add($"| {r["strategy"]} | {r["rule"]} | {r["sample"]} | {tr} | {BrMath.PyFmt(r["win_rate"]!.GetValue<double>() * 100, 0)}% | {P(r["avg_ret"])} | {P(r["median_ret"])} | {(r["payoff_ratio"] is null ? "-" : BrMath.PyRepr(r["payoff_ratio"]!.GetValue<double>()))} | {P(r["max_single_loss"])} | {P(r["max_drawdown_seq"])} |");
        }
        return string.Join("\n", L);
    }
}

/// <summary>自检：无未来函数（全量数据第 i 根评分 == 截断到 i 根后评分）+ 最高=最低不除零。</summary>
public static class BrSelfTest
{
    public static async Task<bool> RunAsync(BrConfig cfg, IReadOnlyList<string> codes, Action<string> log, int samples = 40, CancellationToken ct = default)
    {
        var ok = true;
        if (BrModel.ClosePosition(10, 10, 10, 9).Pos != 1.0 || BrModel.ClosePosition(10, 10, 10, 11).Pos != 0.0) { ok = false; log("[selftest] ClosePosition 一字处理失败"); }
        // R/R 公式：介入10、止损9、压力12 → 收益20%、风险10%、R/R=2.0（较有吸引力）；止损≥介入价 → 不输出结论
        var lvT = new BrLevels { Stop = 9 }; lvT.ResCands.Add((12, "测试")); lvT.FallbackEstimate = 13; lvT.Finalize(10, "测试", true, cfg);
        var lvU = new BrLevels { Stop = 10.5 }; lvU.ResCands.Add((12, "测试")); lvU.Finalize(10, "测试", true, cfg);
        if (Math.Abs((lvT.Rr ?? 0) - 2.0) > 1e-9 || !lvT.RrOk || lvU.Rr is not null || lvU.StopReliable) { ok = false; log("[selftest] 风险收益比公式检查失败"); }
        else log("[selftest] 风险收益比公式（10/9/12 → R/R 2.0；止损≥介入价 → 不输出结论）：通过");
        foreach (var code in codes)
        {
            var k = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), 400, 1, 4, ct).ConfigureAwait(false);
            if (k is null) { log($"[selftest] {code} 日K获取失败"); ok = false; continue; }
            var full = k.S.Take(k.S.Count).Prepare(cfg);
            var n = full.Count; var rnd = new Random(7);
            var minB = cfg.I("model.min_bars", 80);
            var idx = Enumerable.Range(minB, Math.Max(0, n - 1 - minB)).OrderBy(_ => rnd.Next()).Take(samples).OrderBy(x => x).ToList();
            var bad = 0; var badT = 0; var sigCnt = 0; var stopCnt = 0; var resCnt = 0; var stages = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var i in idx)
            {
                var a = BrModel.ScoreAt(full, i, cfg, code).ToJson().ToJsonString();
                var cut = k.S.Take(i + 1).Prepare(cfg);
                var b = BrModel.ScoreAt(cut, i, cfg, code).ToJson().ToJsonString();
                if (a != b) { bad++; log($"[selftest] {code} 第{i}根({k.S.Date[i]}) 评分 全量 vs 截断 不一致 → 存在未来函数"); }
                // V1.1：交易阶段 / B1B2B3 信号 / 结构止损 / 压力位 / R/R / 近期信号 T+1 检查
                var ta = BrTradeEngine.Evaluate(full, i, cfg, code).ToJson().ToJsonString();
                var tb = BrTradeEngine.Evaluate(cut, i, cfg, code).ToJson().ToJsonString();
                if (ta != tb) { badT++; log($"[selftest] {code} 第{i}根({k.S.Date[i]}) 交易阶段/信号/止损/压力位 全量 vs 截断 不一致 → 存在未来函数"); }
                var st = BrTradeEngine.Evaluate(full, i, cfg, code);
                stages[st.Stage] = stages.GetValueOrDefault(st.Stage) + 1;
                if (st.Signal is not null) sigCnt++;
                if (st.Levels.Stop is not null) stopCnt++;
                if (st.Levels.Resistance is not null) resCnt++;
            }
            // 额外：把所有出现信号的交易日也逐一做截断比对（随机抽样可能抽不到信号日）
            var sigDays = 0; var badSig = 0; var execDays = 0; var badExec = 0;
            for (var i = minB; i < n - 1; i++)
            {
                var tf = BrTradeEngine.Evaluate(full, i, cfg, code);
                if (tf.Signal is null) continue;
                sigDays++;
                var tc = BrTradeEngine.Evaluate(k.S.Take(i + 1).Prepare(cfg), i, cfg, code);
                if (tf.ToJson().ToJsonString() != tc.ToJson().ToJsonString()) { badSig++; log($"[selftest] {code} 信号日 {k.S.Date[i]} 全量 vs 截断 不一致"); }
                // V1.2：T+1 执行检查/B1 分级只能用到 T+1 开盘价——把 T+1 的高/低/收抹成开盘价、并截掉之后的K线，结论必须不变
                var m = k.S.Take(i + 2); m.High[i + 1] = m.Open[i + 1]; m.Low[i + 1] = m.Open[i + 1]; m.Close[i + 1] = m.Open[i + 1]; m.Prepare(cfg);
                var sigM = BrTradeEngine.Evaluate(m, i, cfg, code).Signal!;
                var a1 = BrTradeEngine.B1Final(full, tf.Signal, cfg); var a2 = BrTradeEngine.B1Final(m, sigM, cfg);
                var x1 = $"{a1.Status}|{a1.Entry}|{a1.Grade}|{string.Join(",", a1.All.Select(z => z.Cat))}|{a1.Lv?.Rr}|{a1.Lv?.Stop}";
                var x2 = $"{a2.Status}|{a2.Entry}|{a2.Grade}|{string.Join(",", a2.All.Select(z => z.Cat))}|{a2.Lv?.Rr}|{a2.Lv?.Stop}";
                execDays++;
                if (x1 != x2) { badExec++; log($"[selftest] {code} 信号日 {k.S.Date[i]} T+1 执行检查用到了开盘以外的数据：{x1} vs {x2}"); }
            }
            if (bad > 0 || badT > 0 || badSig > 0 || badExec > 0) ok = false;
            log($"[selftest] {code} {idx.Count} 个随机交易日 评分一致性：{(bad == 0 ? "通过" : "失败")}；交易阶段/信号/止损/压力位一致性：{(badT == 0 ? "通过" : "失败")}" +
                $"（抽样覆盖阶段 {string.Join("/", stages.Select(kv => $"{kv.Key}{kv.Value}"))}，含止损 {stopCnt}、含压力位 {resCnt}）；全部 {sigDays} 个信号日逐一截断比对：{(badSig == 0 ? "通过" : $"失败 {badSig}")}；T+1 执行检查/B1分级只用开盘价（{execDays} 个）：{(badExec == 0 ? "通过" : $"失败 {badExec}")}");
        }
        return ok;
    }
}


internal static class BrPyJsonExt
{
    /// <summary>扁平对象按 Python json.dumps(ensure_ascii=False) 默认格式输出：{"k": v, "k2": v2}</summary>
    public static string PyJson(this JsonObject o) => "{" + string.Join(", ", o.Select(kv => System.Text.Json.JsonSerializer.Serialize(kv.Key, BrReport.JsonOpts) + ": " + Val(kv.Value))) + "}";

    private static string Val(JsonNode? n)
    {
        if (n is null) return "null";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue<long>(out var l)) return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var d)) return BrMath.PyRepr(d);
        }
        return n.ToJsonString(BrReport.JsonOpts);
    }
}
