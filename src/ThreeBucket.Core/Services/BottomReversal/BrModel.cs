using System.Text.Json.Nodes;
using static ThreeBucket.Core.Services.BottomReversal.BrMath;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>单只股票某一日的评分结果；Modules 保存每个子项的得分与原始计算值（可追溯）。</summary>
public sealed class BrResult
{
    public string Code = "", Name = "", Industry = "", Date = "";
    public double Close;
    public int? Total;
    public string Rating = "", Stage = "";
    public Dictionary<string, int> ModuleScores = new();
    public JsonObject Modules = new();
    public List<string> Flags = [], Notes = [], Insufficient = [];
    public int? BreakoutIndex, DaysSinceBreakout;
    public bool ChaseRisk;
    public JsonObject Observation = new();
    public JsonObject Returns = new();
    public double LimitPct;
    public double? AvgAmount20, Mv, FloatMv;

    // 便于报告/回测直接读取
    public string? BreakoutDate;
    public double? Platform;
    public double? VolumePullbackRatio;
    public string? Support, RiskReference;

    /// <summary>V1.1 交易阶段/信号（独立于趋势评分）。</summary>
    public BrTrade? Trade;

    public int Sub(string mod, string key) => Modules[mod]?[key]?["score"]?.GetValue<int>() ?? 0;

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["code"] = Code, ["date"] = Date, ["close"] = R(Close, 3),
            ["flags"] = new JsonArray(Flags.Select(x => (JsonNode?)x).ToArray()),
            ["notes"] = new JsonArray(Notes.Select(x => (JsonNode?)x).ToArray()),
            ["insufficient"] = new JsonArray(Insufficient.Select(x => (JsonNode?)x).ToArray()),
            ["total"] = Total, ["module_scores"] = new JsonObject(ModuleScores.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["modules"] = Modules.DeepClone(), ["rating"] = Rating, ["stage"] = Stage,
            ["days_since_breakout"] = DaysSinceBreakout, ["chase_risk"] = ChaseRisk,
            ["observation"] = Observation.DeepClone(), ["returns"] = Returns.DeepClone(), ["limit_pct"] = LimitPct,
            ["name"] = Name, ["industry"] = Industry, ["avg_amount20"] = AvgAmount20, ["mv"] = Mv, ["float_mv"] = FloatMv,
        };
        if (Trade is not null) o["trade"] = Trade.ToJson();
        return o;
    }
}

public sealed class BrMarketEnv
{
    public bool Weak;
    public string Label = "未计算";
    public JsonObject Indexes = new();
}

/// <summary>
/// 100 分底部反转评分引擎（A 底部结构20 / B 放量突破25 / C 突破后承接20 / D 均线趋势20 / E 动能15）。
/// 无未来函数：ScoreAt(s, i) 只读下标 ≤ i；波段低点需右侧 3 根已完成；20 日均量/最高收盘不含当根；突破不足 5 根标记“承接观察中”。
/// 算法与原 Python 版逐项对齐（见 docs）。
/// </summary>
public static class BrModel
{
    public static int Band(double v, List<(double Thr, double Score)> bands, int below = 0)
    {
        var s = below;
        foreach (var (t, sc) in bands) if (v >= t) s = (int)sc;
        return s;
    }

    public static int Downgrade(int score, List<(double Thr, double Score)> bands)
    {
        var levels = new List<int> { 0 };
        levels.AddRange(bands.Select(b => (int)b.Score));
        for (var k = levels.Count - 1; k > 0; k--) if (score >= levels[k]) return levels[k - 1];
        return 0;
    }

    public static (double Pos, bool Special) ClosePosition(double h, double l, double c, double? prevC)
    {
        if (h - l > 1e-9) return ((c - l) / (h - l), false);
        if (prevC is double p && !double.IsNaN(p))
        {
            if (c > p) return (1.0, true);
            if (c < p) return (0.0, true);
        }
        return (0.5, true);
    }

    public static List<int> SwingLows(double[] low, int i, int lookback, int w)
    {
        var o = new List<int>();
        var start = Math.Max(w, i - lookback + 1);
        for (var j = start; j < i - w + 1; j++)
        {
            var lj = low[j];
            if (lj < Min(low.AsSpan(j - w, w)) && lj < Min(low.AsSpan(j + 1, w))) o.Add(j);
        }
        return o;
    }

    public static int? FindBreakout(BrSeries s, int i, BrConfig cfg)
    {
        var sw = cfg.I("model.breakout.search_window", 10);
        var prior = cfg.I("model.breakout.prior_window", 20);
        var lb = Math.Max(sw, cfg.I("model.breakout.episode_lookback", 20));
        var minVr = cfg.D("model.breakout.event_min_volume_ratio", 1.2);
        var minChg = cfg.D("model.breakout.event_min_change", 0.0);
        var c = s.Close; var cpm = s.ClosePrevMax; var vpm = s.VolPrevMean; var chg = s.Chg; var vol = s.Volume;
        var cand = new List<int>();
        for (var t = Math.Max(prior, i - lb + 1); t < i + 1; t++)
        {
            if (Nan(cpm[t]) || Nan(vpm[t]) || vpm[t] <= 0) continue;
            var vr = vol[t] / vpm[t];
            if (c[t] > cpm[t] && vr >= minVr && (double.IsNaN(chg[t]) || chg[t] >= minChg)) cand.Add(t);
        }
        if (cand.Count == 0) return null;
        var cur = cand[0];
        foreach (var t in cand.Skip(1))
        {
            var plat = cpm[cur];
            var failed = false;
            for (var k = cur + 1; k < t; k++) if (c[k] < plat) { failed = true; break; }
            if (failed) cur = t;
        }
        return cur;
    }

    private static JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode?)x).ToArray());

    public static BrResult ScoreAt(BrSeries s, int i, BrConfig cfg, string code = "", BrMarketEnv? market = null)
    {
        double[] o = s.Open, h = s.High, l = s.Low, c = s.Close, v = s.Volume;
        var d = s.Date;
        var res = new BrResult { Code = code, Date = d[i], Close = c[i] };
        var minBars = cfg.I("model.min_bars", 80);
        if (i + 1 < minBars)
        {
            res.Total = null; res.Rating = "数据不足"; res.Stage = "数据不足";
            res.Insufficient.Add($"K线 {i + 1} 根 < min_bars {minBars}");
            return res;
        }

        // ───────── A 底部结构 ─────────
        var A = new JsonObject();
        var lb = cfg.I("model.A1.lookback", 60);
        var ws0 = i - lb + 1;
        var win = h.AsSpan(ws0, lb);
        var hh = Max(win); var ll = Min(l.AsSpan(ws0, lb));
        var hhIdx = ws0; for (var k = ws0; k <= i; k++) if (h[k] > h[hhIdx]) hhIdx = k;   // numpy argmax 取首个最大
        var dd = c[i] / hh - 1;
        double peak = double.NegativeInfinity, mdd60 = double.PositiveInfinity;
        for (var k = ws0; k <= i; k++) { peak = Math.Max(peak, c[k]); mdd60 = Math.Min(mdd60, c[k] / peak - 1); }
        var a1 = Band(-dd, cfg.Bands("model.A1.bands"));
        A["A1"] = new JsonObject
        {
            ["score"] = a1, ["max"] = 5, ["drawdown60"] = R(dd), ["high60"] = R(hh, 3), ["high60_date"] = d[hhIdx],
            ["low60"] = R(ll, 3), ["max_drawdown60_close"] = R(mdd60), ["rule"] = "Drawdown60=收盘/近60日最高价-1",
        };

        var a2Bands = cfg.Bands("model.A2.bands");
        var sw = SwingLows(l, i, cfg.I("model.A2.lookback", 40), cfg.I("model.A2.swing_window", 3));
        var minSep = cfg.I("model.A2.min_separation", 5);
        var a2 = 0; (int Idx, double Low)? L1 = null, L2 = null; double? rise = null, crise = null; var note2 = "";
        if (sw.Count >= 2)
        {
            var j2 = sw[^1];
            var j1s = sw.Take(sw.Count - 1).Where(j => j <= j2 - minSep).ToList();
            if (j1s.Count > 0)
            {
                var j1 = j1s[^1];
                L1 = (j1, l[j1]); L2 = (j2, l[j2]);
                rise = l[j2] / l[j1] - 1;
                crise = Min(c.AsSpan(Math.Max(0, j2 - 1), Math.Min(c.Length, j2 + 2) - Math.Max(0, j2 - 1)))
                      / Min(c.AsSpan(Math.Max(0, j1 - 1), Math.Min(c.Length, j1 + 2) - Math.Max(0, j1 - 1))) - 1;
                if (rise > 0)
                {
                    a2 = Band(rise.Value, a2Bands);
                    if (cfg.B("model.A2.require_close_confirm", true) && crise <= 0) { a2 = Downgrade(a2, a2Bands); note2 = "收盘口径低点未抬高，降一档"; }
                }
                else note2 = "L2 不高于 L1";
                if (c[i] < l[j2]) { a2 = 0; note2 = "收盘已跌破最近波段低点 L2"; }
            }
            else note2 = $"两低点间隔不足 {minSep} 根";
        }
        else note2 = $"{cfg.I("model.A2.lookback", 40)} 日内有效波段低点不足 2 个";
        var lowRiseR = R(rise);
        A["A2"] = new JsonObject
        {
            ["score"] = a2, ["max"] = 8, ["swing_low_dates"] = Arr(sw.Select(j => d[j])),
            ["L1"] = L1 is null ? null : new JsonObject { ["date"] = d[L1.Value.Idx], ["low"] = R(L1.Value.Low, 3) },
            ["L2"] = L2 is null ? null : new JsonObject { ["date"] = d[L2.Value.Idx], ["low"] = R(L2.Value.Low, 3) },
            ["low_rise"] = lowRiseR, ["close_rise"] = R(crise), ["note"] = note2,
            ["rule"] = "波段低点=最低价低于前后各3根；仅用右侧已完成3根的低点",
        };

        var T = FindBreakout(s, i, cfg);
        var W = cfg.I("model.A3.window", 20);
        var e = T is int tt0 ? tt0 - 1 : i;
        var a3 = 0;
        var a3d = new JsonObject();
        if (e - W + 1 >= 0)
        {
            var half = W / 2;
            int ws = e - W + 1, f1s = ws, f1n = half, f2s = ws + half, f2n = e + 1 - f2s;
            var rng = Max(h.AsSpan(ws, W)) / Min(l.AsSpan(ws, W)) - 1;
            var stable = Min(l.AsSpan(f2s, f2n)) >= Min(l.AsSpan(f1s, f1n));
            var amp = new double[c.Length];
            for (var k = ws; k <= e; k++) amp[k] = (h[k] - l[k]) / (c[k] > 0 ? c[k] : double.NaN);
            var conv = NanMean(amp.AsSpan(f2s, f2n)) <= NanMean(amp.AsSpan(f1s, f1n)) * cfg.D("model.A3.converge_ratio", 0.9);
            var bse = Min(l.AsSpan(ws, W));
            var tol = cfg.D("model.A3.retest_tol", 0.03);
            var runs = 0; var prev = false;
            for (var k = ws; k <= e; k++) { var tch = l[k] <= bse * (1 + tol); if (tch && !prev) runs++; prev = tch; }
            var shrink = NpMean(v.AsSpan(f2s, f2n)) <= NpMean(v.AsSpan(f1s, f1n)) * cfg.D("model.A3.volume_shrink_ratio", 0.9);
            var consol = rng <= cfg.D("model.A3.range_max", 0.2);
            if (stable)
            {
                a3 = 3;
                if (consol && conv)
                {
                    a3 = 5;
                    if (runs >= cfg.I("model.A3.min_touches", 2) && shrink) a3 = 7;
                }
            }
            a3d = new JsonObject
            {
                ["window"] = $"{d[ws]}~{d[e]}", ["range_pct"] = R(rng), ["platform_high"] = R(Max(h.AsSpan(ws, W)), 3),
                ["platform_low"] = R(bse, 3), ["no_new_low_recent_half"] = stable, ["volatility_converging"] = conv,
                ["touch_runs_near_low"] = runs, ["volume_shrinking"] = shrink, ["consolidation"] = consol,
            };
        }
        else res.Insufficient.Add("A3 整理窗口数据不足");
        var a3o = new JsonObject { ["score"] = a3, ["max"] = 7 };
        foreach (var kv in a3d.ToList()) { a3d.Remove(kv.Key); a3o[kv.Key] = kv.Value; }
        a3o["rule"] = "突破前（无突破则截至当日）20 根：后10根未创新低/振幅收敛/多次探底/量能萎缩";
        A["A3"] = a3o;

        // ───────── B 放量突破 ─────────
        var B = new JsonObject();
        var t = T ?? i;
        var vpm = s.VolPrevMean[t];
        double? vr = !Nan(vpm) && vpm > 0 ? v[t] / vpm : null;
        var (cp, cpSpecial) = ClosePosition(h[t], l[t], c[t], t > 0 ? c[t - 1] : null);
        var chgT = s.Chg[t];
        var b1Bands = cfg.Bands("model.B1.bands");
        var b1MinChg = cfg.D("model.B1.min_change", 0.03);
        var b1 = 0; var nb1 = "";
        if (vr is null) res.Insufficient.Add("B1 20日均量不可计算");
        else if (!Nan(chgT) && chgT > b1MinChg)
        {
            b1 = vr.Value >= b1Bands[0].Thr ? Band(vr.Value, b1Bands) : 0;
            if (b1 > 0 && cp < cfg.D("model.B1.close_pos_min", 0.7)) { b1 = Downgrade(b1, b1Bands); nb1 = "收盘位置<0.7，降一档"; }
        }
        else nb1 = $"当日涨幅未超过 {Math.Round(b1MinChg * 100)}%";
        B["B1"] = new JsonObject
        {
            ["score"] = b1, ["max"] = 8, ["bar_date"] = d[t], ["volume"] = R(v[t], 0), ["avg_vol20_excl_today"] = R(vpm, 0),
            ["volume_ratio"] = R(vr, 3), ["change"] = R(chgT), ["close_position"] = R(cp, 3),
            ["close_position_special"] = cpSpecial ? "最高=最低（一字），单独处理" : "", ["note"] = nb1,
        };
        var plat = s.ClosePrevMax[t];
        double? br = !Nan(plat) && plat > 0 ? c[t] / plat - 1 : null;
        var b2Bands = cfg.Bands("model.B2.bands");
        var b2 = 0;
        if (br is double brv && brv > 0)
        {
            b2 = 6;
            if (brv >= b2Bands[1].Thr) b2 = 8;
            if (brv >= b2Bands[2].Thr && vr is double vv && vv >= cfg.D("model.B2.top_requires_volume_ratio", 1.5)) b2 = 10;
        }
        B["B2"] = new JsonObject
        {
            ["score"] = b2, ["max"] = 10, ["breakout_date"] = T is int ti ? d[ti] : null,
            ["platform_close_high20"] = R(plat, 3), ["breakout_ratio"] = R(br), ["rule"] = "BreakoutRatio=收盘/前20日最高收盘(不含当日)-1",
        };
        var b3 = 0; double? upper = null; var fell = false;
        if (br is double brv3 && brv3 > 0)
        {
            var rngT = h[t] - l[t];
            upper = rngT > 1e-9 ? (h[t] - Math.Max(o[t], c[t])) / rngT : 0.0;
            var ftol = cfg.D("model.B3.fallback_tol", 0.0);
            for (var k = t + 1; k <= i; k++) if (c[k] < plat * (1 - ftol)) { fell = true; break; }
            if (fell) b3 = 0;
            else if (upper >= cfg.D("model.B3.long_upper_shadow", 0.4)) b3 = 2;
            else if (brv3 >= cfg.D("model.B3.good_min_breakout", 0.01) && vr is double vv3 && vv3 >= cfg.D("model.B3.good_min_volume_ratio", 1.5)
                     && cp >= cfg.D("model.B3.good_min_close_pos", 0.7) && upper <= cfg.D("model.B3.good_upper_shadow_max", 0.25)) b3 = 7;
            else b3 = 5;
        }
        B["B3"] = new JsonObject { ["score"] = b3, ["max"] = 7, ["upper_shadow_ratio"] = R(upper, 3), ["fell_back_below_platform"] = fell };

        // ───────── C 突破后承接 ─────────
        var C = new JsonObject();
        int? n = T is int tn ? i - tn : null;
        var observing = T is not null && n < 5;
        int c1 = 0, c2 = 0, c3 = 0;
        var c1d = new JsonObject(); var c2d = new JsonObject(); var c3d = new JsonObject();
        var failedPlatform = false;
        double? ratio = null;
        var score3d = cfg.I("model.C1.score_3d", 4); var score5d = cfg.I("model.C1.score_5d", 6); var score5dr = cfg.I("model.C1.score_5d_rising", 8);
        if (T is int T0)
        {
            var nn = n!.Value;
            var lvl = plat * (1 - cfg.D("model.C1.fail_tol", 0.0));
            var bigDrop = cfg.D("model.C1.big_drop", -0.04);
            int? failK = null;
            for (var k = 1; k <= Math.Min(nn, 5); k++)
            {
                var big2 = k >= 2 && s.Chg[T0 + k] <= bigDrop && s.Chg[T0 + k - 1] <= bigDrop;
                if (c[T0 + k] < lvl || big2) { failK = k; break; }
            }
            failedPlatform = c[i] < lvl;
            for (var k = T0 + 1; k <= i && !failedPlatform; k++) if (c[k] < lvl) failedPlatform = true;
            if (failK is int fk && fk <= 3) c1 = 0;
            else if (nn < 3) c1 = 0;
            else if (nn < 5 || failK is not null) c1 = score3d;
            else
            {
                c1 = score5d;
                double mx = double.NegativeInfinity; for (var k = T0 + 1; k < T0 + 6; k++) mx = Math.Max(mx, c[k]);
                if (c[T0 + 5] > c[T0] && mx > c[T0]) c1 = score5dr;
            }
            c1d = new JsonObject
            {
                ["days_since_breakout"] = nn, ["fail_day_offset"] = failK, ["platform"] = R(plat, 3),
                ["status"] = observing ? "承接观察中（突破不足5个交易日，未发生的交易日不计为通过）" : "已满5日",
            };
            // C2
            var c2Min = cfg.I("model.C2.min_days", 2);
            if (nn >= c2Min)
            {
                var segEnd = Math.Min(i, T0 + cfg.I("model.C2.max_days", 10));
                var seg = Enumerable.Range(T0 + 1, segEnd - T0).ToList();
                var downs = seg.Where(k => s.Chg[k] < 0).ToList();
                var use = downs.Count > 0 ? downs : seg;
                var pv = NpMean(use.Select(k => v[k]).ToArray());
                ratio = v[T0] > 0 ? pv / v[T0] : null;
                if (ratio is double r0)
                {
                    if (r0 > cfg.D("model.C2.near_ratio", 1.0)) c2 = 0;
                    else if (r0 > cfg.D("model.C2.ratio_70", 0.7)) c2 = 3;
                    else if (r0 > cfg.D("model.C2.ratio_50", 0.5)) c2 = 5;
                    else c2 = seg.Min(k => c[k]) >= plat ? 7 : 5;
                }
                c2d = new JsonObject
                {
                    ["pullback_days"] = Arr(downs.Select(k => d[k])), ["pullback_avg_vol"] = R(pv, 0), ["breakout_vol"] = R(v[T0], 0),
                    ["volume_pullback_ratio"] = R(ratio, 3), ["note"] = downs.Count > 0 ? "" : "突破后无下跌日，按突破后全部日均量计算",
                };
            }
            else c2d = new JsonObject { ["note"] = $"突破后不足 {c2Min} 根，暂不评分" };
            // C3
            var c3Min = cfg.I("model.C3.min_days", 2);
            if (nn >= c3Min)
            {
                var touches = new List<int>(); var broken = false; var runs = 0; var prevT = false; var belowMa10 = 0;
                double ptt = cfg.D("model.C3.platform_touch_tol", 0.02), pbt = cfg.D("model.C3.platform_break_tol", 0.01),
                       mtt = cfg.D("model.C3.ma10_touch_tol", 0.01), mbt = cfg.D("model.C3.ma10_break_tol", 0.02);
                var mbd = cfg.I("model.C3.ma10_break_days", 2);
                var ma10 = s.Ma10;
                for (var k = T0 + 1; k <= i; k++)
                {
                    var tp = l[k] <= plat * (1 + ptt);
                    var tm = !Nan(ma10[k]) && l[k] <= ma10[k] * (1 + mtt);
                    if (c[k] < plat * (1 - pbt)) broken = true;
                    belowMa10 = (!Nan(ma10[k]) && c[k] < ma10[k] * (1 - mbt)) ? belowMa10 + 1 : 0;
                    if (belowMa10 >= mbd) broken = true;
                    var cur = tp || tm;
                    if (cur) { touches.Add(k); if (!prevT) runs++; }
                    prevT = cur;
                }
                if (broken) c3 = 0;
                else if (runs >= 2) c3 = 5;
                else if (runs == 1)
                {
                    var lastTouch = touches[^1];
                    c3 = c[i] > c[lastTouch] || lastTouch < i ? 3 : 0;
                }
                c3d = new JsonObject
                {
                    ["support_platform"] = R(plat, 3), ["ma5"] = R(s.Ma5[i], 3), ["ma10"] = R(ma10[i], 3),
                    ["touch_dates"] = Arr(touches.Select(k => d[k])), ["touch_runs"] = runs, ["effective_break"] = broken,
                    ["note"] = runs == 0 && !broken ? "尚未回踩，支撑未检验" : "",
                };
            }
            else c3d = new JsonObject { ["note"] = $"突破后不足 {c3Min} 根，暂不评分" };
        }
        else
        {
            c1d = new JsonObject { ["note"] = "近期无有效放量突破" };
            c2d = new JsonObject { ["note"] = "近期无有效放量突破" };
            c3d = new JsonObject { ["note"] = "近期无有效放量突破" };
        }
        C["C1"] = Merge(c1, 8, c1d); C["C2"] = Merge(c2, 7, c2d); C["C3"] = Merge(c3, 5, c3d);

        // ───────── D 均线趋势 ─────────
        var D = new JsonObject();
        double ma5 = s.Ma5[i], ma10i = s.Ma10[i], ma20 = s.Ma20[i], ma60 = s.Ma60[i];
        var d1 = 0;
        if (c[i] > ma20) d1 = c[i] > ma60 ? 5 : 3;
        D["D1"] = new JsonObject { ["score"] = d1, ["max"] = 5, ["close"] = R(c[i], 3), ["ma20"] = R(ma20, 3), ["ma60"] = R(ma60, 3) };
        var crossRecent = false;
        for (var k = i - 4; k <= i; k++) if (s.Ma5[k - 1] <= s.Ma10[k - 1] && s.Ma5[k] > s.Ma10[k]) { crossRecent = true; break; }
        int d2 = ma5 > ma10i && ma10i > ma20 ? 6 : (ma5 > ma20 && ma10i > ma20 ? 4 : (ma5 > ma10i ? 2 : 0));
        D["D2"] = new JsonObject { ["score"] = d2, ["max"] = 6, ["ma5"] = R(ma5, 3), ["ma10"] = R(ma10i, 3), ["ma20"] = R(ma20, 3), ["ma5_cross_ma10_5d"] = crossRecent };
        var flat = cfg.D("model.D3.flat", 0.005);
        var s20 = ma20 / s.Ma20[i - 5] - 1;
        var s20p = s.Ma20[i - 5] / s.Ma20[i - 10] - 1;
        var d3 = s20 < -flat ? 0 : (s20 <= flat ? 2 : (s20p > 0 ? 5 : 4));
        D["D3"] = new JsonObject { ["score"] = d3, ["max"] = 5, ["ma20_slope5"] = R(s20), ["ma20_slope5_prev"] = R(s20p), ["flat_band"] = flat };
        var sd = cfg.I("model.D4.slope_days", 10); var f60 = cfg.D("model.D4.flat", 0.005);
        double? s60 = double.IsNaN(s.Ma60[i - sd]) ? null : ma60 / s.Ma60[i - sd] - 1;
        int d4;
        if (s60 is null) { d4 = 0; res.Insufficient.Add("MA60 斜率不可计算"); }
        else d4 = s60 < -f60 ? 0 : (s60 <= f60 ? 2 : 4);
        D["D4"] = new JsonObject { ["score"] = d4, ["max"] = 4, ["ma60_slope"] = R(s60), ["slope_days"] = sd };

        // ───────── E 动能 ─────────
        var E = new JsonObject();
        var rl = cfg.I("model.E1.rise_lookback", 3); var oh = cfg.D("model.E1.overheat", 75);
        var r = s.Rsi[i]; var rp = s.Rsi[i - rl];
        var rising = !double.IsNaN(rp) && r > rp;
        int e1;
        if (double.IsNaN(r)) { e1 = 0; res.Insufficient.Add("RSI 不可计算"); }
        else if (r < 30) e1 = 2;
        else if (r < 40) e1 = 3;
        else if (r < 50) e1 = rising ? 5 : 3;
        else { e1 = rising ? 7 : 5; if (r > oh) e1 = 5; }
        var overheat = !double.IsNaN(r) && r > oh;
        E["E1"] = new JsonObject { ["score"] = e1, ["max"] = 7, ["rsi14"] = R(r, 2), ["rsi_prev"] = R(rp, 2), ["rising"] = rising, ["overheat"] = overheat };
        double[] dif = s.Dif, dea = s.Dea, hist = s.Hist;
        var cl = cfg.I("model.E2.cross_lookback", 10);
        var crosses = new List<int>();
        for (var k = Math.Max(1, i - cl + 1); k <= i; k++) if (dif[k - 1] <= dea[k - 1] && dif[k] > dea[k]) crosses.Add(k);
        var expanding = hist[i] > 0 && hist[i] > hist[i - 1] && hist[i - 1] > hist[i - 2];
        var e2 = dif[i] <= dea[i] ? 0 : (expanding ? 5 : 3);
        E["E2"] = new JsonObject
        {
            ["score"] = e2, ["max"] = 5, ["dif"] = R(dif[i]), ["dea"] = R(dea[i]), ["hist"] = R(hist[i]),
            ["golden_cross_dates"] = Arr(crosses.Select(k => d[k])), ["cross_below_zero"] = crosses.Count > 0 && dif[crosses[^1]] < 0,
            ["red_hist_expanding"] = expanding, ["worsening"] = dif[i] <= dea[i] && hist[i] < hist[i - 1],
        };
        var udw = cfg.I("model.E3.up_down_window", 10);
        var v5 = NpMean(v.AsSpan(i - 4, 5)); var v20 = NpMean(v.AsSpan(i - 19, 20));
        var ups = new List<double>(); var dns = new List<double>();
        for (var k = i - udw + 1; k <= i; k++) { if (s.Chg[k] > 0) ups.Add(v[k]); else if (s.Chg[k] < 0) dns.Add(v[k]); }
        double? ud = ups.Count > 0 && dns.Count > 0 ? NpMean(ups) / NpMean(dns) : null;
        int e3;
        if (ud is double udv && udv >= cfg.D("model.E3.up_down_ratio", 1.2) && v5 >= v20 * cfg.D("model.E3.v5_v20_for_3", 0.9)) e3 = 3;
        else if (v5 >= v20 * cfg.D("model.E3.v5_v20_for_2", 1.0)) e3 = 2;
        else e3 = 0;
        E["E3"] = new JsonObject
        {
            ["score"] = e3, ["max"] = 3, ["vol5_avg"] = R(v5, 0), ["vol20_avg"] = R(v20, 0), ["v5_v20"] = R(v20 > 0 ? v5 / v20 : double.NaN, 3),
            ["up_vol_vs_down_vol_10d"] = R(ud, 3),
        };

        var mods = new JsonObject { ["A"] = A, ["B"] = B, ["C"] = C, ["D"] = D, ["E"] = E };
        var ms = new Dictionary<string, int>();
        foreach (var kv in mods) ms[kv.Key] = ((JsonObject)kv.Value!).Sum(x => x.Value!["score"]!.GetValue<int>());
        var total = ms.Values.Sum();

        // ───────── 异常交易 ─────────
        var lp = code.Length > 0 ? BrMarketData.LimitPct(code) : 0.10;
        var yz = 0;
        for (var k = i; k > Math.Max(0, i - 10); k--) { if (h[k] - l[k] < 1e-9 && s.Chg[k] >= lp - 0.005) yz++; else break; }
        var gaps = 0; var gapPct = cfg.D("abnormal.gap_up_pct", 0.03);
        for (var k = i; k > Math.Max(0, i - 10); k--) { if (o[k] / c[k - 1] - 1 >= gapPct) gaps++; else break; }
        var r5 = c[i] / c[i - 5] - 1; var r10 = c[i] / c[i - 10] - 1;
        var dev = c[i] / ma20 - 1;
        double? vrToday = s.VolPrevMean[i] > 0 ? v[i] / s.VolPrevMean[i] : null;
        var tToday = s.Turnover[i];
        double? tRatio = !Nan(s.TurnPrevMean[i]) && s.TurnPrevMean[i] > 0 ? tToday / s.TurnPrevMean[i] : null;
        var fl = res.Flags;
        if (yz >= cfg.I("abnormal.consecutive_limit_days", 2)) fl.Add($"连续一字涨停{yz}天");
        if (gaps >= cfg.I("abnormal.gap_up_days", 2)) fl.Add($"连续大幅高开{gaps}天");
        if (vrToday is double vt && vt >= cfg.D("abnormal.volume_spike_ratio", 5)) fl.Add($"成交量异常放大{BrMath.PyFmt(vt, 1)}倍");
        if (r5 >= cfg.D("abnormal.surge_5d", 0.15) || r10 >= cfg.D("abnormal.surge_10d", 0.2)) fl.Add($"短期涨幅过高(5日{Pct1(r5)}/10日{Pct1(r10)})");
        if (!double.IsNaN(tToday) && (tToday / 100 >= cfg.D("abnormal.turnover_abs", 0.2) || (tRatio is double tr && tr >= cfg.D("abnormal.turnover_ratio", 4))))
            fl.Add($"换手率异常({BrMath.PyFmt(tToday, 1)}%)");
        if (dev >= cfg.D("abnormal.ma20_deviation", 0.15)) fl.Add($"严重偏离MA20({Pct1(dev)})");
        if (overheat) fl.Add("RSI>75过热");
        var chase = fl.Any(x => x.StartsWith("连续一字") || x.StartsWith("短期涨幅") || x.StartsWith("严重偏离") || x.StartsWith("RSI>75"));
        if (chase) fl.Add("反转已发生，等待回踩");

        // ───────── 阶段 ─────────
        double? L2v = L2?.Low;
        var stage = "无明确阶段";
        if (T is not null && failedPlatform) stage = "反转失败";
        else if (L2v is double l2a && lowRiseR is double lrr && lrr > 0 && c[i] < l2a) stage = "反转失败";
        else if (T is not null && n >= 5 && ms["D"] >= 16 && c1 >= score5d) stage = "趋势形成";
        else if (T is not null && n >= 3 && c2 >= 5 && c3 >= 3) stage = "回踩确认";
        else if (T is not null && n >= 3 && c1 >= score3d) stage = "突破确认";
        else if (T is not null) stage = "放量突破";
        else if (a2 >= 3) stage = "底部形成中";
        if (stage == "反转失败") res.Notes.Add("反转失败：评分按当日重新计算，不保留历史高分");
        if (market is { Weak: true } && stage == "放量突破") fl.Add("市场环境偏弱，激进突破信号降低优先级");

        var rating = cfg.RatingBands().First(x => total >= x.Thr).Name;

        // ───────── 交易观察（模型判断，非买卖指令）─────────
        var obs = new JsonObject();
        double oap = cfg.D("model.levels.observe_above_platform", 0.03), obp = cfg.D("model.levels.observe_below_platform", 0.01),
               rb = cfg.D("model.levels.risk_buffer", 0.03), cmx = cfg.D("model.levels.chase_max_above_platform", 0.05);
        if (T is not null)
        {
            obs["observe_zone"] = new JsonArray(R(plat * (1 - obp), 3), R(plat * (1 + oap), 3));
            obs["risk_reference"] = R(plat * (1 - rb), 3);
            obs["failure_condition"] = $"收盘有效跌破突破平台 {BrMath.PyFmt(plat, 3)}" + (L2v is double l2f && l2f != 0 ? $" 或跌破波段低点 L2 {BrMath.PyFmt(l2f, 3)}" : "");
            obs["chase_ok"] = !chase && c[i] <= plat * (1 + cmx);
        }
        else if (L2v is double l2b)
        {
            obs["observe_zone"] = new JsonArray(R(l2b, 3), R(l2b * 1.05, 3));
            obs["risk_reference"] = R(l2b * (1 - rb), 3);
            obs["failure_condition"] = $"收盘跌破波段低点 L2 {BrMath.PyFmt(l2b, 3)}";
            obs["chase_ok"] = false;
        }
        else obs["chase_ok"] = false;
        var supports = new List<double>();
        foreach (var x in new double?[] { T is not null ? plat : null, ma10i, ma20, L2v })
            if (x is double xv && !Nan(xv) && xv < c[i]) supports.Add(xv);
        obs["support"] = supports.Count > 0 ? R(supports.Max(), 3) : null;
        obs["pressure"] = hh > c[i] ? R(hh, 3) : null;
        obs["how_to_watch"] = stage == "放量突破" && !chase ? "突破不足3日，等待承接确认或缩量回踩平台"
            : (stage is "突破确认" or "回踩确认" or "趋势形成") && !chase ? "关注回踩平台/MA10 是否缩量企稳"
            : chase ? "反转已发生，等待回踩，不宜追涨"
            : stage == "底部形成中" ? "等待放量突破前20日最高收盘价"
            : stage == "反转失败" ? "反转失败，重新观察底部结构"
            : "反转条件不足";

        res.Total = total; res.ModuleScores = ms; res.Modules = mods; res.Rating = rating; res.Stage = stage;
        res.BreakoutIndex = T; res.DaysSinceBreakout = n; res.ChaseRisk = chase; res.Observation = obs;
        res.Returns = new JsonObject { ["r5"] = R(r5), ["r10"] = R(r10), ["ma20_dev"] = R(dev) };
        res.LimitPct = lp;
        res.BreakoutDate = T is int tb ? d[tb] : null;
        res.Platform = T is not null ? R(plat, 3) : null;
        res.VolumePullbackRatio = R(ratio, 3);
        return res;
    }

    private static JsonObject Merge(int score, int max, JsonObject extra)
    {
        var o = new JsonObject { ["score"] = score, ["max"] = max };
        foreach (var kv in extra.ToList()) { extra.Remove(kv.Key); o[kv.Key] = kv.Value; }
        return o;
    }
}
