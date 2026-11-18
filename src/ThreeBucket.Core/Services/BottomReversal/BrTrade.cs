using System.Globalization;
using System.Text.Json.Nodes;
using static ThreeBucket.Core.Services.BottomReversal.BrMath;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>V1.1 介入价 / 结构止损 / 上方压力位 / 风险收益比（全部基于 ≤ 当日的历史行情，可追溯）。</summary>
public sealed class BrLevels
{
    public double? Entry; public string EntryBasis = ""; public bool EntryIsReference = true;
    public double? Stop; public string StopBasis = ""; public double? StructStop; public string StructBasis = ""; public bool StopReliable; public bool StopTooFar; public double? StopBase; public double Atr;
    public double? Resistance; public string ResistanceBasis = ""; public bool ResistanceEstimated;
    public List<(double Price, string Basis)> ResCands = [];
    public double FallbackEstimate; public string FallbackBasis = "";
    public double? RewardPct, RiskPct, Rr; public string RrLabel = ""; public bool RrOk;

    /// <summary>用新的介入价重新选压力位、重算风险收益比（止损与压力候选不变）。</summary>
    public BrLevels WithEntry(double entry, string basis, bool isRef, BrConfig cfg)
    {
        var o = new BrLevels
        {
            StopBase = StopBase, StructStop = StructStop, StructBasis = StructBasis, Atr = Atr, ResCands = ResCands,
            FallbackEstimate = FallbackEstimate, FallbackBasis = FallbackBasis,
        };
        o.Finalize(entry, basis, isRef, cfg);
        return o;
    }

    private static string PyFmt3(double v) => PyFmt(v, 3);

    public void Finalize(double entry, string basis, bool isRef, BrConfig cfg)
    {
        Entry = entry; EntryBasis = basis; EntryIsReference = isRef;
        var gap = cfg.D("trade.resistance.min_gap", 0.002);   // 只排除与介入价几乎重合的位；贴近的压力位照样采用（R/R 会很低）
        var above = ResCands.Where(x => x.Price > entry * (1 + gap)).OrderBy(x => x.Price).ToList();
        if (above.Count > 0) { Resistance = above[0].Price; ResistanceBasis = above[0].Basis; ResistanceEstimated = false; }
        else
        {
            var est = Math.Max(FallbackEstimate, entry * (1 + gap));
            Resistance = est; ResistanceBasis = FallbackBasis; ResistanceEstimated = true;
        }
        // ── 止损：默认取“结构止损（加大缓冲）”与“固定 fixed_pct”中更宽（更低）的一个，再按 max_stop_pct 封顶 ──
        StructStop ??= Stop; if (StructBasis.Length == 0) StructBasis = StopBasis;
        var maxPct = cfg.D("trade.stop.max_stop_pct", 0.10);
        StopReliable = StructStop is double ss && ss > 0 && ss < entry;
        if (!StopReliable) { Stop = StructStop; StopBasis = StructBasis; }
        else
        {
            var sst = StructStop!.Value;
            if (cfg.S("trade.stop.mode", "wider") == "structural") { Stop = sst; StopBasis = "结构止损：" + StructBasis; }
            else
            {
                var fp = cfg.D("trade.stop.fixed_pct", 0.08); var fx = entry * (1 - fp); var fpn = PyFmt(fp * 100, 0);
                if (fx < sst) { Stop = fx; StopBasis = $"固定{fpn}%止损 {PyFmt3(fx)}（比结构止损 {PyFmt3(sst)} 更宽；结构位：{StructBasis}）"; }
                else { Stop = sst; StopBasis = $"结构止损（比固定{fpn}% {PyFmt3(fx)} 更宽）：{StructBasis}"; }
            }
            if (cfg.S("trade.stop.too_far_action", "cap") == "cap" && (entry - Stop!.Value) / entry > maxPct + 1e-12)
            {
                Stop = entry * (1 - maxPct);
                StopBasis += $"；超过上限{PyFmt(maxPct * 100, 0)}%，封顶为 {PyFmt3(Stop.Value)}（结构支撑在止损下方）";
            }
        }
        RewardPct = Resistance is double r ? r / entry - 1 : null;
        if (!StopReliable)
        {
            RiskPct = null; Rr = null; StopTooFar = false; RrOk = false;
            RrLabel = "结构止损位无法可靠识别，不输出风险收益比结论";
            return;
        }
        RiskPct = (entry - Stop!.Value) / entry;
        // rr_basis=final（默认）：R/R 用最终止损；=structural：R/R 仍按纯结构止损评估（只用于对比试验），实际止损用最终止损
        var rrRisk = cfg.S("trade.stop.rr_basis", "final") == "structural" ? (entry - StructStop!.Value) / entry : RiskPct;
        Rr = rrRisk > 0 ? RewardPct / rrRisk : null;
        StopTooFar = RiskPct > maxPct + 1e-9;
        double good = cfg.D("trade.rr.attractive", 2.0), ok = cfg.D("trade.rr.watch", 1.5);
        if (StopTooFar) { RrLabel = $"止损距离过大({PyFmt(RiskPct!.Value * 100, 1)}%)，不生成介入建议"; RrOk = false; }
        else if (Rr >= good) { RrLabel = "较有吸引力(≥" + PyRepr(good) + ")"; RrOk = true; }
        else if (Rr >= ok) { RrLabel = "可以观察(" + PyRepr(ok) + "~" + PyRepr(good) + ")"; RrOk = true; }
        else { RrLabel = "不建议主动介入(<" + PyRepr(ok) + ")"; RrOk = false; }
        if (ResistanceEstimated) RrLabel += "（压力位为估算口径）";
    }

    public JsonObject ToJson() => new()
    {
        ["entry"] = R(Entry, 3), ["entry_basis"] = EntryBasis, ["entry_is_reference"] = EntryIsReference,
        ["stop"] = R(Stop, 3), ["stop_basis"] = StopBasis, ["stop_reliable"] = StopReliable, ["stop_too_far"] = StopTooFar, ["struct_stop"] = R(StructStop, 3), ["struct_stop_basis"] = StructBasis, ["atr14"] = R(Atr, 3),
        ["resistance"] = R(Resistance, 3), ["resistance_basis"] = ResistanceBasis, ["resistance_estimated"] = ResistanceEstimated,
        ["resistance_candidates"] = new JsonArray(ResCands.OrderBy(x => x.Price).Select(x => (JsonNode?)new JsonObject { ["price"] = R(x.Price, 3), ["basis"] = x.Basis }).ToArray()),
        ["reward_pct"] = R(RewardPct), ["risk_pct"] = R(RiskPct), ["rr"] = R(Rr, 2), ["rr_label"] = RrLabel, ["rr_ok"] = RrOk,
        ["note"] = "压力位不是必达目标价；止损价不保证按该价成交（跳空/跌停时实际亏损可能更大）",
    };
}

/// <summary>B1/B2/B3 信号（在信号日收盘后确认）。</summary>
public sealed class BrTradeSignal
{
    public string Type = "", Date = "", BreakoutDate = "", Note = "";
    public int Index, BreakoutIndex;
    public double Platform;
    public bool NewEpisode, FarFromPlatform;
    public JsonObject Conditions = new();
    public List<string> Tags = [];
    public BrLevels Levels = new();

    public JsonObject ToJson() => new()
    {
        ["type"] = Type, ["date"] = Date, ["breakout_date"] = BreakoutDate, ["platform"] = R(Platform, 3), ["new_episode"] = NewEpisode,
        ["far_from_platform"] = FarFromPlatform, ["note"] = Note, ["conditions"] = Conditions.DeepClone(),
        ["risk_tags"] = new JsonArray(Tags.Select(x => (JsonNode?)x).ToArray()), ["levels"] = Levels.ToJson(),
    };
}

/// <summary>交易阶段识别结果（独立于 100 分趋势评分）。</summary>
public sealed class BrTrade
{
    public string Code = "", Date = "";
    public bool Insufficient;
    public string Stage = "", OpStatus = "", OpNote = "", Group = "";
    public BrTradeSignal? Signal, Recent;
    public string RecentExecStatus = "", RecentExecNote = ""; public double? RecentEntry; public BrLevels? RecentLevels;
    public string? FirstBreakoutDate, LastBreakoutDate, FailDate, FailReason;
    public double? Platform, PlatformLast;
    public int? DaysSinceBreakout;
    public List<string> Tags = [];
    public BrLevels Levels = new();
    public JsonObject Conditions = new();
    public JsonObject Position = new();
    public double Close, Ma20Dev, R5, Rsi;
    public List<BrTradeSignal> Events = [];
    public string ModelVersion = "V1.1";
    /// <summary>V1.2 B1 分级：信号日给预分级（A/B/C），T+1 已发生后给最终分级；B1-B 进入回踩观察池 3–10 日等待 B3。</summary>
    public string B1Grade = "", B1GradeNote = "";

    public string SignalType => Signal?.Type ?? "无";
    public bool Hot => Tags.Any(BrTradeEngine.IsHotTag);

    public JsonObject ToJson() => new()
    {
        ["model_version"] = ModelVersion, ["code"] = Code, ["date"] = Date, ["close"] = R(Close, 3), ["stage"] = Stage, ["op_status"] = OpStatus, ["op_note"] = OpNote,
        ["signal"] = SignalType, ["signal_detail"] = Signal?.ToJson(),
        ["recent_signal"] = Recent is null ? null : new JsonObject
        {
            ["type"] = Recent.Type, ["date"] = Recent.Date, ["exec_status"] = RecentExecStatus, ["exec_note"] = RecentExecNote,
            ["planned_entry_simulated"] = R(RecentEntry, 3), ["levels_at_entry"] = RecentLevels?.ToJson(),
        },
        ["first_breakout_date"] = FirstBreakoutDate, ["last_breakout_date"] = LastBreakoutDate, ["platform"] = R(Platform, 3), ["platform_last"] = R(PlatformLast, 3),
        ["days_since_breakout"] = DaysSinceBreakout, ["fail_date"] = FailDate, ["fail_reason"] = FailReason,
        ["ma20_dev"] = R(Ma20Dev), ["r5"] = R(R5), ["rsi14"] = R(Rsi, 2),
        ["risk_tags"] = new JsonArray(Tags.Select(x => (JsonNode?)x).ToArray()), ["levels"] = Levels.ToJson(), ["conditions"] = Conditions.DeepClone(),
        ["position"] = Position.DeepClone(), ["b1_grade"] = B1Grade, ["b1_grade_note"] = B1GradeNote,
        ["episode_signals"] = new JsonArray(Events.Select(e => (JsonNode?)new JsonObject { ["type"] = e.Type, ["date"] = e.Date, ["note"] = e.Note }).ToArray()),
    };
}

/// <summary>
/// V1.1 交易阶段引擎：底部形成 / 放量突破 / 突破确认 / 缩量回踩 / 趋势形成 / 反转失败（不满足任何一个时标“未进入反转结构”）。
/// 逐根状态机只读 ≤ i 的数据：从 i-walk_window 起顺序推进，第 k 根的判断只用 ≤ k 的数据；波段高低点需右侧 3 根确认。
/// 信号在收盘后确认；T+1 介入检查（高开>3% 取消等）只在 T+1 已发生时计算。
/// </summary>
public static class BrTradeEngine
{
    public const string S1 = "底部形成", S2 = "放量突破", S3 = "突破确认", S4 = "缩量回踩", S5 = "趋势形成", S6 = "反转失败", S0 = "未进入反转结构";
    public static readonly string[] Stages = [S1, S2, S3, S4, S5, S6, S0];
    private static readonly string[] HotPrefixes = ["短期过热", "均线乖离过大", "动能过热", "短期加速"];
    public static bool IsHotTag(string t) => HotPrefixes.Any(t.StartsWith);

    private static double Cd(BrConfig cfg, string k, double def) => cfg.D("trade." + k, def);
    private static int Ci(BrConfig cfg, string k, int def) => cfg.I("trade." + k, def);
    private static string F3(double v) => PyFmt(v, 3);
    private static string P1(double v) => PyFmtSigned(v * 100, 1) + "%";

    public static double Atr(BrSeries s, int i, int n)
    {
        double sum = 0; var cnt = 0;
        for (var k = Math.Max(1, i - n + 1); k <= i; k++)
        {
            var tr = Math.Max(s.High[k] - s.Low[k], Math.Max(Math.Abs(s.High[k] - s.Close[k - 1]), Math.Abs(s.Low[k] - s.Close[k - 1])));
            sum += tr; cnt++;
        }
        return cnt > 0 ? sum / cnt : double.NaN;
    }

    public static List<int> SwingHighs(double[] high, int i, int lookback, int w)
    {
        var o = new List<int>();
        for (var j = Math.Max(w, i - lookback + 1); j < i - w + 1; j++)
            if (high[j] > Max(high.AsSpan(j - w, w)) && high[j] > Max(high.AsSpan(j + 1, w))) o.Add(j);
        return o;
    }

    private static double Mean(double[] a, int from, int to)
    {
        if (to < from) return double.NaN;
        double s = 0; for (var k = from; k <= to; k++) s += a[k];
        return s / (to - from + 1);
    }

    /// <summary>有效放量突破：收盘 &gt; 前20日最高收盘、量 ≥ 1.5×20日均量(不含当日)、涨幅 &gt; 3%、收盘位置 &gt; 0.7、无明显冲高回落。</summary>
    public static (bool Ok, JsonObject D) BreakoutCheck(BrSeries s, int k, BrConfig cfg)
    {
        var cpm = s.ClosePrevMax[k]; var vpm = s.VolPrevMean[k];
        if (k < 1 || Nan(cpm) || Nan(vpm) || vpm <= 0) return (false, new JsonObject { ["note"] = "数据不足" });
        double o = s.Open[k], h = s.High[k], l = s.Low[k], c = s.Close[k];
        var vr = s.Volume[k] / vpm; var chg = s.Chg[k];
        var (cp, _) = BrModel.ClosePosition(h, l, c, s.Close[k - 1]);
        var rng = h - l; var upper = rng > 1e-9 ? (h - Math.Max(o, c)) / rng : 0.0;
        bool a = c > cpm, b = vr >= Cd(cfg, "breakout.min_volume_ratio", 1.5), d = chg > Cd(cfg, "breakout.min_change", 0.03),
             e = cp > Cd(cfg, "breakout.min_close_position", 0.7), f = upper <= Cd(cfg, "breakout.max_upper_shadow", 0.3);
        return (a && b && d && e && f, new JsonObject
        {
            ["close_above_prior20_max_close"] = a, ["prior20_max_close"] = R(cpm, 3), ["volume_ratio"] = R(vr, 3), ["volume_ok"] = b,
            ["change"] = R(chg), ["change_ok"] = d, ["close_position"] = R(cp, 3), ["close_position_ok"] = e, ["upper_shadow"] = R(upper, 3), ["no_spike_reversal"] = f,
        });
    }

    /// <summary>追涨过滤器风险标签（标签而非剔除）。</summary>
    public static List<string> ChaseTags(BrSeries s, int k, BrConfig cfg)
    {
        var t = new List<string>();
        double[] c = s.Close;
        var r5 = c[k] / c[k - 5] - 1;
        if (r5 > Cd(cfg, "chase.surge_5d", 0.15)) t.Add($"短期过热(5日{P1(r5)})");
        var dev = c[k] / s.Ma20[k] - 1;
        if (dev > Cd(cfg, "chase.ma20_deviation", 0.10)) t.Add($"均线乖离过大(MA20{P1(dev)})");
        if (!double.IsNaN(s.Rsi[k]) && s.Rsi[k] > Cd(cfg, "chase.rsi_overheat", 75)) t.Add($"动能过热(RSI {PyFmt(s.Rsi[k], 1)})");
        var nBig = Ci(cfg, "chase.big_bull_count", 2); var bigChg = Cd(cfg, "chase.big_bull_change", 0.05); var bigVr = Cd(cfg, "chase.big_bull_volume_ratio", 1.5);
        var allBig = true;
        for (var j = k - nBig + 1; j <= k; j++)
        {
            var vr = s.VolPrevMean[j] > 0 ? s.Volume[j] / s.VolPrevMean[j] : 0;
            if (!(s.Chg[j] >= bigChg && c[j] > s.Open[j] && vr >= bigVr)) { allBig = false; break; }
        }
        if (allBig) t.Add($"短期加速(连续{nBig}根放量大阳)");
        return t;
    }

    /// <summary>压力位候选：60日波段高点、整理平台高点、最近放量冲高、MA60/MA120、成交密集区；都没有时用 250 日最高或波动率估算（标注口径）。</summary>
    public static void FillResistance(BrSeries s, int i, BrConfig cfg, BrLevels lv, double? platformHigh)
    {
        double[] h = s.High, l = s.Low, c = s.Close, v = s.Volume;
        var lb = Ci(cfg, "resistance.lookback", 60);
        foreach (var j in SwingHighs(h, i, lb, Ci(cfg, "resistance.swing_window", 3))) lv.ResCands.Add((h[j], $"60日波段高点({s.Date[j]})"));
        if (platformHigh is double ph && !Nan(ph)) lv.ResCands.Add((ph, "前期整理平台高点"));
        var spikeVr = Cd(cfg, "resistance.spike_volume_ratio", 2.0); var spikeUp = Cd(cfg, "resistance.spike_upper_shadow", 0.3);
        for (var j = i; j >= Math.Max(1, i - lb + 1); j--)
        {
            var rng = h[j] - l[j]; if (rng <= 1e-9 || !(s.VolPrevMean[j] > 0)) continue;
            var up = (h[j] - Math.Max(s.Open[j], c[j])) / rng;
            if (v[j] / s.VolPrevMean[j] >= spikeVr && up >= spikeUp) { lv.ResCands.Add((h[j], $"最近放量冲高({s.Date[j]})")); break; }
        }
        if (!Nan(s.Ma60[i])) lv.ResCands.Add((s.Ma60[i], "MA60均线压力"));
        if (i >= 119) lv.ResCands.Add((Mean(c, i - 119, i), "MA120均线压力"));
        var pw = Math.Min(i + 1, Ci(cfg, "resistance.profile_window", 120)); var bins = Ci(cfg, "resistance.profile_bins", 20);
        double lo = Min(l.AsSpan(i - pw + 1, pw)), hi = Max(h.AsSpan(i - pw + 1, pw));
        if (hi > lo)
        {
            var vb = new double[bins]; double tot = 0;
            for (var k = i - pw + 1; k <= i; k++) { var b = Math.Clamp((int)((c[k] - lo) / (hi - lo) * bins), 0, bins - 1); vb[b] += v[k]; tot += v[k]; }
            var avg = tot / bins; var dense = Cd(cfg, "resistance.dense_ratio", 1.5);
            for (var b = 0; b < bins; b++)
                if (vb[b] >= avg * dense)
                {
                    double blo = lo + (hi - lo) * b / bins, bhi = lo + (hi - lo) * (b + 1) / bins;
                    lv.ResCands.Add((blo, $"成交密集区({F3(blo)}~{F3(bhi)})"));
                }
        }
        var w250 = Math.Min(i + 1, 250);
        var mx250 = Max(h.AsSpan(i - w250 + 1, w250));
        lv.ResCands.Add((mx250, $"{w250}日最高价"));
        var rets = new List<double>(); for (var k = Math.Max(1, i - 59); k <= i; k++) rets.Add(c[k] / c[k - 1] - 1);
        var m = rets.Average(); var sd = Math.Sqrt(rets.Sum(x => (x - m) * (x - m)) / Math.Max(1, rets.Count - 1));
        var mult = Cd(cfg, "resistance.fallback_sigma_mult", 2.0);
        lv.FallbackEstimate = c[i] * (1 + mult * sd * Math.Sqrt(20));
        lv.FallbackBasis = $"估算：近60日日收益波动率×√20×{PyRepr(mult)}（上方无历史压力位）";
    }

    /// <summary>结构止损（最终止损在 BrLevels.Finalize 里与固定比例止损取更宽并封顶）：按顺序取第一个低于介入价的结构位（波段低点/平台下沿/回踩支撑），加缓冲；距离小于 1×ATR 时按 ATR 放宽。</summary>
    public static void FillStop(BrSeries s, int i, double entry, List<(double V, string Basis)> cands, BrConfig cfg, BrLevels lv)
    {
        lv.Atr = Atr(s, i, Ci(cfg, "stop.atr_period", 14));
        foreach (var (b, basis) in cands)
        {
            if (Nan(b) || b <= 0 || b >= entry) continue;
            var buf = Math.Max(Cd(cfg, "stop.buffer_pct", 0.02) * b, Cd(cfg, "stop.buffer_atr_mult", 0.5) * lv.Atr);
            var sp = b - buf; var why = basis + $" {F3(b)} 下方缓冲 {F3(buf)}";
            var minDist = Cd(cfg, "stop.min_atr_mult", 1.0) * lv.Atr;
            if (entry - sp < minDist) { sp = entry - minDist; why += $"；距离<{PyRepr(Cd(cfg, "stop.min_atr_mult", 1.0))}×ATR，按 ATR 放宽"; }
            if (sp <= 0) continue;
            lv.StopBase = b; lv.StructStop = sp; lv.StructBasis = why;
            return;
        }
        lv.StructStop = null; lv.StructBasis = "无可靠结构支撑位（候选均不低于介入价）";
    }

    private static BrLevels BuildLevels(BrSeries s, int k, double entry, string basis, bool isRef, List<(double, string)> stopCands, double? platformHigh, BrConfig cfg)
    {
        var lv = new BrLevels();
        FillStop(s, k, entry, stopCands, cfg, lv);
        FillResistance(s, k, cfg, lv, platformHigh);
        lv.Finalize(entry, basis, isRef, cfg);
        return lv;
    }

    private sealed class Ep
    {
        public int Anchor, LastT, Below, LastB3 = -1;
        public double P0, PLast, PlatHigh, PreLow = double.NaN;
        public bool B2Done, EverConfirmed;
        /// <summary>同一轮里的历次突破（趋势中再突破入栈；再突破失败时出栈回到前一平台，首个平台失败才算反转失败）。</summary>
        public readonly List<(int T, double P, double PlatHigh, bool B2Done)> Stack = [];
        public void Push(int t, double p, double ph) { if (Stack.Count > 0) Stack[^1] = (LastT, PLast, PlatHigh, B2Done); Stack.Add((t, p, ph, false)); LastT = t; PLast = p; PlatHigh = ph; B2Done = false; Below = 0; LastB3 = -1; }
        public bool Pop() { if (Stack.Count <= 1) return false; Stack.RemoveAt(Stack.Count - 1); (LastT, PLast, PlatHigh, B2Done) = Stack[^1]; Below = 0; return true; }
    }

    private static double PreSwingLow(BrSeries s, int k, BrConfig cfg)
    {
        var sw = BrModel.SwingLows(s.Low, k, Ci(cfg, "swing.lookback", 60), Ci(cfg, "swing.window", 3));
        return sw.Count > 0 ? s.Low[sw[^1]] : double.NaN;
    }

    private static List<(double, string)> BaseStops(BrSeries s, int k, Ep ep, string type, double pullLow)
    {
        var t = ep.LastT;
        var list = new List<(double, string)>();
        if (type == "B3" && !double.IsNaN(pullLow)) list.Add((pullLow, "回踩形成的支撑(回踩最低价)"));
        double lowAfter = s.Low[t]; for (var j = t; j <= k; j++) lowAfter = Math.Min(lowAfter, s.Low[j]);
        if (type == "B1") list.Add((Math.Min(ep.PLast, s.Low[t]), "突破平台下沿(平台价/突破日低点)"));
        else list.Add((Math.Min(ep.PLast, lowAfter), "突破平台下沿(平台价/突破后最低价)"));
        if (!double.IsNaN(ep.PreLow)) list.Add((ep.PreLow, "突破前最近有效波段低点"));
        return list;
    }

    private static BrTradeSignal MakeSignal(string type, BrSeries s, int k, Ep ep, BrConfig cfg, string note, JsonObject cond, double pullLow = double.NaN)
    {
        var sig = new BrTradeSignal
        {
            Type = type, Index = k, Date = s.Date[k], BreakoutIndex = ep.LastT, BreakoutDate = s.Date[ep.LastT], Platform = ep.PLast, Note = note, Conditions = cond,
        };
        sig.Levels = BuildLevels(s, k, s.Close[k], "参考价=信号日收盘（T+1 成交价未知，非成交价）", true, BaseStops(s, k, ep, type, pullLow), ep.PlatHigh, cfg);
        sig.Tags = ChaseTags(s, k, cfg);
        if (sig.Levels.RewardPct is double rw && rw < Cd(cfg, "chase.near_resistance", 0.03)) sig.Tags.Add($"接近压力位(距{P1(rw)})");
        return sig;
    }

    private static bool B2Check(BrSeries s, int k, Ep ep, BrConfig cfg, out JsonObject cond)
    {
        double[] c = s.Close, v = s.Volume; var t = ep.LastT; var P = ep.PLast;
        var tol = Cd(cfg, "fail.platform_tol", 0.01);
        var held = true; for (var j = t + 1; j <= k; j++) if (c[j] < P * (1 - tol)) held = false;
        var bigDrop = Cd(cfg, "b2.big_drop", -0.04); var consec = false;
        for (var j = t + 2; j <= k; j++) if (s.Chg[j] <= bigDrop && s.Chg[j - 1] <= bigDrop) consec = true;
        var down = Enumerable.Range(t + 1, k - t).Where(j => s.Chg[j] < 0).ToList();
        double pv; string pvBasis; double lim;
        if (down.Count > 0) { pv = down.Average(j => v[j]); pvBasis = "回调日均量"; lim = Cd(cfg, "b2.pullback_volume_ratio", 0.7); }
        else { pv = Mean(v, t + 1, k); pvBasis = "突破后日均量(无回调日)"; lim = Cd(cfg, "b2.no_pullback_volume_ratio", 1.0); }
        var volOk = pv < v[t] * lim;
        var near = c[k] >= P * (1 - tol);
        cond = new JsonObject
        {
            ["days_after_breakout"] = k - t, ["no_close_back_below_platform"] = held, ["no_consecutive_big_drop"] = !consec,
            ["pullback_volume"] = R(pv, 0), ["pullback_volume_basis"] = pvBasis, ["breakout_volume"] = R(v[t], 0), ["volume_ratio_vs_breakout"] = R(pv / v[t], 3),
            ["volume_limit"] = lim, ["volume_shrink_ok"] = volOk, ["at_or_above_platform"] = near, ["close_vs_platform"] = R(c[k] / P - 1),
        };
        return held && !consec && volOk && near;
    }

    private static bool B3Check(BrSeries s, int k, Ep ep, BrConfig cfg, out JsonObject cond, out double pullLow)
    {
        double[] o = s.Open, h = s.High, l = s.Low, c = s.Close, v = s.Volume; var t = ep.LastT; var P = ep.PLast;
        cond = new JsonObject(); pullLow = double.NaN;
        var ph = t; for (var j = t; j <= k - 1; j++) if (h[j] >= h[ph]) ph = j;
        if (ep.LastB3 >= 0 && ph <= ep.LastB3) { cond["note"] = "上一次 B3 后尚未创出新高，不重复发信号"; return false; }
        if (ph >= k) return false;
        pullLow = Min(l.AsSpan(ph + 1, k - ph));
        var depth = 1 - pullLow / h[ph];
        var tol = Cd(cfg, "fail.platform_tol", 0.01);
        var nearPlat = pullLow <= P * (1 + Cd(cfg, "b3.near_platform", 0.03));
        var nearMa10 = false; for (var j = ph + 1; j <= k; j++) if (l[j] <= s.Ma10[j] * (1 + Cd(cfg, "b3.near_ma10", 0.01))) nearMa10 = true;
        var pvEnd = k - ph > 1 ? k - 1 : k;
        var pv = Mean(v, ph + 1, pvEnd);
        var volOk = pv < v[t] * Cd(cfg, "b3.pullback_volume_ratio", 0.7);
        var support = c[k] >= P * (1 - tol);
        var rng = h[k] - l[k];
        var lowerShadow = rng > 1e-9 && (Math.Min(o[k], c[k]) - l[k]) / rng >= Cd(cfg, "b3.lower_shadow", 0.4);
        var reclaim5 = c[k] > s.Ma5[k] && c[k - 1] <= s.Ma5[k - 1];
        var smallBull = c[k] > o[k] && s.Chg[k] > 0;
        var engulf = c[k - 1] < o[k - 1] && c[k] > o[k] && c[k] >= o[k - 1];
        var volUp = v[k] > v[k - 1] * 1.1 && s.Chg[k] > 0;
        var stabs = new List<string>();
        if (lowerShadow) stabs.Add("下影线明显"); if (reclaim5) stabs.Add("收盘重新站回MA5"); if (smallBull) stabs.Add("小阳线");
        if (engulf) stabs.Add("反包"); if (volUp) stabs.Add("成交量温和放大");
        var notDown = s.Chg[k] > Cd(cfg, "b3.max_day_drop", -0.02);
        cond = new JsonObject
        {
            ["post_breakout_high"] = R(h[ph], 3), ["post_breakout_high_date"] = s.Date[ph], ["pullback_low"] = R(pullLow, 3), ["pullback_depth"] = R(depth),
            ["depth_ok"] = depth >= Cd(cfg, "b3.min_depth", 0.03), ["near_platform"] = nearPlat, ["near_ma10"] = nearMa10,
            ["pullback_avg_volume"] = R(pv, 0), ["breakout_volume"] = R(v[t], 0), ["volume_ratio_vs_breakout"] = R(pv / v[t], 3), ["volume_shrink_ok"] = volOk,
            ["support_held"] = support, ["stabilization"] = new JsonArray(stabs.Select(x => (JsonNode?)x).ToArray()), ["not_down_day"] = notDown,
        };
        return depth >= Cd(cfg, "b3.min_depth", 0.03) && (nearPlat || nearMa10) && volOk && support && stabs.Count > 0 && notDown;
    }

    public static (bool Ok, JsonObject Cond) TrendCheck(BrSeries s, int i, BrConfig cfg)
    {
        double[] c = s.Close, l = s.Low, v = s.Volume;
        var align = s.Ma5[i] > s.Ma10[i] && s.Ma10[i] > s.Ma20[i];
        var slope = s.Ma20[i] / s.Ma20[i - 5] - 1;
        var up = slope > Cd(cfg, "trend.ma20_min_slope", 0.0);
        var nAbove = Ci(cfg, "trend.above_ma20_days", 5); var above = true;
        for (var k = i - nAbove + 1; k <= i; k++) if (c[k] < s.Ma20[k]) above = false;
        var sw = BrModel.SwingLows(l, i, 60, 3);
        bool lowsUp = sw.Count >= 2 ? l[sw[^1]] > l[sw[^2]] : Min(l.AsSpan(i - 9, 10)) > Min(l.AsSpan(i - 19, 10));
        var ups = new List<double>(); var dns = new List<double>();
        for (var k = i - 9; k <= i; k++) { if (s.Chg[k] > 0) ups.Add(v[k]); else if (s.Chg[k] < 0) dns.Add(v[k]); }
        var volOk = ups.Count > 0 && (dns.Count == 0 || ups.Average() >= dns.Average() * Cd(cfg, "trend.up_down_volume_ratio", 1.0));
        return (align && up && above && lowsUp && volOk, new JsonObject
        {
            ["ma_bull_alignment"] = align, ["ma20_slope5"] = R(slope), ["ma20_rising"] = up, ["above_ma20_days"] = above, ["lows_rising"] = lowsUp, ["volume_confirms"] = volOk,
        });
    }

    public static (bool Ok, JsonObject Cond) BottomCheck(BrSeries s, int i, BrConfig cfg)
    {
        double[] h = s.High, l = s.Low, c = s.Close, v = s.Volume;
        var hh = Max(h.AsSpan(i - 59, 60)); var dd = c[i] / hh - 1;
        double peak = double.NegativeInfinity, mdd = 0;
        for (var k = i - 59; k <= i; k++) { peak = Math.Max(peak, c[k]); mdd = Math.Min(mdd, c[k] / peak - 1); }
        var ddOk = dd <= -Cd(cfg, "bottom.min_drawdown", 0.15) || mdd <= -Cd(cfg, "bottom.min_max_drawdown", 0.20);
        var sw = BrModel.SwingLows(l, i, 60, 3); var sep = Ci(cfg, "bottom.min_separation", 5);
        bool lowsUp = false; string? l1 = null, l2 = null;
        if (sw.Count >= 2)
        {
            var j2 = sw[^1]; var j1s = sw.Where(j => j <= j2 - sep).ToList();
            if (j1s.Count > 0) { var j1 = j1s[^1]; lowsUp = l[j2] > l[j1] && c[i] >= l[j2]; l1 = $"{s.Date[j1]}:{F3(l[j1])}"; l2 = $"{s.Date[j2]}:{F3(l[j2])}"; }
        }
        var slope = s.Ma20[i] / s.Ma20[i - 5] - 1;
        var flat = slope >= Cd(cfg, "bottom.ma20_slope_min", -0.01) && slope <= Cd(cfg, "bottom.ma20_slope_max", 0.015);
        // “成交量出现收缩”：近20日内任一5日均量 ≤ 此前（i-59..i-20）日均量 × 阈值（底部末端放量不否定前面的缩量）
        var basev = Mean(v, i - 59, i - 20); var minv5 = double.PositiveInfinity;
        for (var k = i - 19; k <= i; k++) minv5 = Math.Min(minv5, Mean(v, k - 4, k));
        var vr = minv5 / basev;
        var volOk = vr <= Cd(cfg, "bottom.volume_shrink_ratio", 0.8);
        var notBroken = !(c[i] > s.ClosePrevMax[i]);
        return (ddOk && lowsUp && flat && volOk && notBroken, new JsonObject
        {
            ["drawdown_from_high60"] = R(dd), ["max_drawdown60"] = R(mdd), ["drawdown_ok"] = ddOk, ["swing_low_L1"] = l1, ["swing_low_L2"] = l2, ["lows_rising"] = lowsUp,
            ["ma20_slope5"] = R(slope), ["ma20_flattening"] = flat, ["min_vol5_20d_vs_prior40"] = R(vr, 3), ["volume_shrinking"] = volOk, ["not_broken_out"] = notBroken,
        });
    }

    /// <summary>T+1 介入检查（只在 T+1 已发生时调用）。返回 (状态, 模拟计划价=T+1开盘, 说明, 以开盘价重算的价位)。</summary>
    public static (string Status, double? Entry, string Note, BrLevels? Lv) ExecCheck(BrSeries s, BrTradeSignal sig, BrConfig cfg)
    {
        var k = sig.Index; var e = k + 1;
        if (e >= s.Count) return ("T+1未发生", null, $"T+1 价格未知；参考价 {F3(s.Close[k])}（信号日收盘）。规则：T+1 高开>{PyFmt(Cd(cfg, "exec.max_gap_up", 0.03) * 100, 0)}% 取消直接介入；B1 低开并跌回平台 {F3(sig.Platform)} 下方取消；R/R<{PyRepr(Cd(cfg, "rr.watch", 1.5))} 不追", null);
        var gap = s.Open[e] / s.Close[k] - 1;
        if (gap > Cd(cfg, "exec.max_gap_up", 0.03)) return ("取消：T+1高开超阈值不追", null, $"高开 {P1(gap)}", null);
        if (sig.Type == "B1" && s.Open[e] < s.Close[k] && s.Open[e] < sig.Platform) return ("取消：低开跌回突破平台下方", null, $"开盘 {F3(s.Open[e])} < 平台 {F3(sig.Platform)}", null);
        if (cfg.B("trade.exec.skip_hot", true) && sig.Tags.Any(IsHotTag)) return ("取消：信号日已过热不追涨", null, string.Join("；", sig.Tags.Where(IsHotTag)), null);
        if (sig.Type == "B2" && sig.FarFromPlatform && cfg.B("trade.exec.skip_far_b2", true)) return ("取消：已明显远离平台不追涨", null, "", null);
        var lv = sig.Levels.WithEntry(s.Open[e], "模拟计划价=T+1开盘价（非真实成交）", false, cfg);
        if (!lv.StopReliable) return ("取消：开盘已低于结构止损/无可靠止损", null, lv.StopBasis, lv);
        if (lv.StopTooFar) return ("取消：止损距离过大", null, lv.RrLabel, lv);
        if (cfg.B("trade.exec.require_rr", true) && !lv.RrOk) return ("取消：风险收益比不足", null, $"R/R {PyFmt(lv.Rr ?? 0, 2)}", lv);
        return ("可介入(模拟)", s.Open[e], $"T+1 开盘 {F3(s.Open[e])}，高开 {P1(gap)}", lv);
    }

    /// <summary>B1 取消原因分类（V1.2）：1 高开/短期过热 2 均线乖离过大 3 RSI过热 4 风险收益比不足 5 跌回突破平台/开盘低于止损 6 停牌或无法成交 7 其他。</summary>
    public static readonly string[] B1CatNames = ["", "高开过热", "均线乖离过大", "RSI过热", "风险收益比不足", "跌回突破平台", "停牌或无法成交", "其他原因"];

    public sealed class B1Eval
    {
        public string Grade = "", Status = "", Note = "", FirstReason = "";
        public int FirstCat;
        public List<(int Cat, string Detail)> All = [];
        public double? Entry; public BrLevels? Lv;
    }

    /// <summary>信号日预分级（只用 ≤ 信号日数据）：C=无可靠止损/止损过远；B=过热标签或参考 R/R 不足；A=其余（仍需 T+1 开盘检查）。</summary>
    public static (string Grade, string Note) B1PreGrade(BrTradeSignal sig, BrConfig cfg)
    {
        var lv = sig.Levels;
        if (!lv.StopReliable) return ("C", "无可靠结构止损");
        if (lv.StopTooFar) return ("C", lv.RrLabel);
        var hot = sig.Tags.Where(IsHotTag).ToList();
        var why = new List<string>(hot);
        if (!lv.RrOk) why.Add($"参考R/R {PyFmt(lv.Rr ?? 0, 2)}<{PyRepr(Cd(cfg, "rr.watch", 1.5))}");
        return why.Count > 0 ? ("B", string.Join("；", why)) : ("A", "突破有效、R/R≥" + PyRepr(Cd(cfg, "rr.watch", 1.5)) + "、未过热（T+1 开盘仍需检查）");
    }

    /// <summary>T+1 已发生后的 B1 最终分级 + 首次取消原因 + 全部触发原因（各条件独立判断）。suspended/limitUpOpen 由调用方用交易日历/不复权价给出。</summary>
    public static B1Eval B1Final(BrSeries s, BrTradeSignal sig, BrConfig cfg, bool suspended = false, bool limitUpOpen = false)
    {
        var r = new B1Eval(); var k = sig.Index; var e = k + 1;
        if (e >= s.Count) { r.Status = "T+1未发生"; return r; }
        var gap = s.Open[e] / s.Close[k] - 1;
        if (suspended) r.All.Add((6, "停牌：次一交易日无成交"));
        if (limitUpOpen) r.All.Add((6, "涨停开盘无法成交"));
        if (gap > Cd(cfg, "exec.max_gap_up", 0.03)) r.All.Add((1, $"高开 {P1(gap)}"));
        foreach (var tg in sig.Tags.Where(IsHotTag))
            r.All.Add((tg.StartsWith("均线乖离过大") ? 2 : tg.StartsWith("动能过热") ? 3 : 1, tg));
        if (s.Open[e] < s.Close[k] && s.Open[e] < sig.Platform) r.All.Add((5, $"低开 {F3(s.Open[e])} 跌回平台 {F3(sig.Platform)} 下方"));
        var lv = sig.Levels.WithEntry(s.Open[e], "模拟计划价=T+1开盘价（非真实成交）", false, cfg);
        if (!lv.StopReliable) r.All.Add((5, "开盘已低于结构止损/无可靠止损"));
        else if (lv.StopTooFar) r.All.Add((7, "止损距离过大：" + lv.RrLabel));
        else if (!lv.RrOk) r.All.Add((4, $"T+1开盘重算 R/R {PyFmt(lv.Rr ?? 0, 2)}"));
        var (st, en, note, l2) = suspended ? ("取消：停牌", (double?)null, "", (BrLevels?)null) : limitUpOpen ? ("取消：涨停开盘无法成交", null, "", null) : ExecCheck(s, sig, cfg);
        r.Status = st; r.Note = note; r.Entry = en; r.Lv = l2 ?? lv;
        if (en is not null) { r.Grade = "A"; return r; }
        r.FirstCat = st.Contains("停牌") || st.Contains("涨停") ? 6 : st.Contains("高开") ? 1 : st.Contains("跌回") || st.Contains("低于结构止损") ? 5
                   : st.Contains("过热") ? (note.StartsWith("均线乖离过大") ? 2 : note.StartsWith("动能过热") ? 3 : 1) : st.Contains("风险收益比") ? 4 : 7;
        r.FirstReason = st + (note.Length > 0 ? "（" + note + "）" : "");
        if (r.All.Count == 0) r.All.Add((r.FirstCat, r.FirstReason));
        r.Grade = r.All.Any(x => x.Cat is 5 or 6 or 7) ? "C" : "B";
        return r;
    }

    public static BrTrade Evaluate(BrSeries s, int i, BrConfig cfg, string code = "")
    {
        var tr = new BrTrade { Code = code, Date = s.Date[i], Close = s.Close[i], ModelVersion = "V" + cfg.Version };
        if (i + 1 < cfg.I("model.min_bars", 80)) { tr.Insufficient = true; tr.Stage = "数据不足"; tr.OpStatus = "观察"; return tr; }
        double[] c = s.Close, l = s.Low;
        var start = Math.Max(30, i - Ci(cfg, "walk_window", 120) + 1);
        Ep? ep = null; int? failedAt = null; string failReason = "";
        var tol = Cd(cfg, "fail.platform_tol", 0.01);
        var events = new List<BrTradeSignal>();
        var lastFailAnchor = "";
        for (var k = start; k <= i; k++)
        {
            var (bo, bod) = BreakoutCheck(s, k, cfg);
            if (bo)
            {
                var isNew = ep is null;
                if (ep is null)
                {
                    ep = new Ep { Anchor = k, P0 = s.ClosePrevMax[k], PreLow = PreSwingLow(s, k, cfg) };
                    failedAt = null;
                }
                ep.Push(k, s.ClosePrevMax[k], Max(s.High.AsSpan(k - 20, 20)));
                events.Add(MakeSignal("B1", s, k, ep, cfg, isNew ? "首次放量突破（新一轮）" : "趋势中再次放量突破", bod));
                events[^1].NewEpisode = isNew;
                continue;
            }
            if (ep is null)
            {
                if (failedAt is int f && k - f > Ci(cfg, "fail.memory_days", 10)) failedAt = null;
                continue;
            }
            var vrk = s.VolPrevMean[k] > 0 ? s.Volume[k] / s.VolPrevMean[k] : double.NaN;
            string? why = null;
            while (true)
            {
                // 有效跌破：结合收盘价、跌破幅度、成交量；只看收盘，不因盘中刺破判失败
                var P = ep.PLast; var lvl = P * (1 - tol);
                if (c[k] < lvl) ep.Below++; else ep.Below = 0;
                why = null;
                if (ep.Below >= Ci(cfg, "fail.consecutive_days", 2)) why = $"连续{ep.Below}日收盘跌破突破平台 {F3(P)}";
                else if (c[k] < P * (1 - Cd(cfg, "fail.deep_break", 0.05))) why = $"收盘跌破平台 {F3(P)} 超过{PyFmt(Cd(cfg, "fail.deep_break", 0.05) * 100, 0)}%";
                else if (c[k] < lvl && vrk >= Cd(cfg, "fail.volume_break_ratio", 1.5)) why = $"放量跌破平台 {F3(P)}(量比{PyFmt(vrk, 2)})";
                else if (s.Chg[k] <= Cd(cfg, "fail.big_drop", -0.05) && vrk >= Cd(cfg, "fail.big_drop_volume_ratio", 2.0) && c[k] < P)
                    why = $"明显放量下跌({P1(s.Chg[k])}，量比{PyFmt(vrk, 2)})并跌回平台";
                if (why is null || !ep.Pop()) break;   // 再突破失败 → 回到前一平台重新判断
            }
            if (why is null && !double.IsNaN(ep.PreLow) && c[k] < ep.PreLow * (1 - tol)) why = $"跌破最近有效波段低点 {F3(ep.PreLow)}";
            if (why is not null)
            {
                failedAt = k; failReason = why; lastFailAnchor = s.Date[ep.Anchor]; ep = null;
                continue;
            }
            var n = k - ep.LastT;
            if (n > Ci(cfg, "episode_max_days", 60) || (!ep.EverConfirmed && n > Ci(cfg, "unconfirmed_max_days", 10))) { ep = null; continue; }
            if (!ep.B2Done && n >= Ci(cfg, "b2.min_days", 3) && n <= Ci(cfg, "b2.max_days", 5) && B2Check(s, k, ep, cfg, out var c2))
            {
                ep.B2Done = true; ep.EverConfirmed = true;
                var sig = MakeSignal("B2", s, k, ep, cfg, $"突破后第{n}个交易日确认", c2);
                sig.FarFromPlatform = c[k] / ep.PLast - 1 > Cd(cfg, "b2.far_above_platform", 0.10);
                if (sig.FarFromPlatform) sig.Note += $"；已明显远离平台({P1(c[k] / ep.PLast - 1)})，不追涨";
                events.Add(sig);
                continue;
            }
            if (n >= Ci(cfg, "b3.min_days", 2) && B3Check(s, k, ep, cfg, out var c3, out var pl))
            {
                var sig = MakeSignal("B3", s, k, ep, cfg, "缩量回踩确认支撑", c3, pl);
                if (cfg.B("trade.b3.require_rr", true) && !sig.Levels.RrOk)
                {
                    c3["rr_rejected"] = $"R/R {PyFmt(sig.Levels.Rr ?? 0, 2)}：{sig.Levels.RrLabel}";
                    continue;
                }
                ep.LastB3 = k; ep.EverConfirmed = true;
                events.Add(sig);
            }
        }

        // ── 当日状态 ──
        tr.Events = ep is not null ? events.Where(e => e.Index >= ep.Anchor).ToList() : events.Where(e => i - e.Index <= Ci(cfg, "recent_signal_days", 10)).ToList();
        tr.Signal = events.LastOrDefault(e => e.Index == i);
        tr.Ma20Dev = c[i] / s.Ma20[i] - 1; tr.R5 = c[i] / c[i - 5] - 1; tr.Rsi = s.Rsi[i];
        tr.Tags = tr.Signal?.Tags.ToList() ?? ChaseTags(s, i, cfg);
        var (trendOk, trendC) = TrendCheck(s, i, cfg);
        tr.Conditions["trend"] = trendC;
        int? n0 = ep is not null ? i - ep.LastT : null;
        tr.DaysSinceBreakout = n0;
        if (ep is not null)
        {
            tr.FirstBreakoutDate = s.Date[ep.Anchor]; tr.LastBreakoutDate = s.Date[ep.LastT]; tr.Platform = ep.P0; tr.PlatformLast = ep.PLast;
            var n = n0!.Value;
            if (tr.Signal?.Type == "B3") { tr.Stage = S4; }
            else if (n == 0) tr.Stage = S2;
            else if (n < Ci(cfg, "b2.min_days", 3)) { tr.Stage = S2; tr.OpNote = $"突破第{n}日，确认中（不足3日不得提前认定）"; }
            else if (n <= Ci(cfg, "b2.max_days", 5)) { tr.Stage = ep.B2Done ? S3 : S2; if (!ep.B2Done) tr.OpNote = $"突破第{n}日，确认条件暂未满足"; }
            else if (trendOk) tr.Stage = S5;
            else if (ep.EverConfirmed) { tr.Stage = S3; tr.OpNote = c[i] >= ep.PLast ? "突破已确认，等待回踩或方向确认" : "回落至平台下方（未达有效跌破），观察能否站回"; }
            else { tr.Stage = S2; tr.OpNote = "突破后未通过确认（量能未收缩或曾大跌），观察"; }
        }
        else if (failedAt is not null) { tr.Stage = S6; tr.FailDate = s.Date[failedAt.Value]; tr.FailReason = failReason; tr.FirstBreakoutDate = lastFailAnchor; }
        else
        {
            var (bOk, bC) = BottomCheck(s, i, cfg);
            tr.Conditions["bottom"] = bC;
            if (!bOk && (bool)bC["not_broken_out"]! && (bool)bC["drawdown_ok"]!)
            {
                // 底部状态保持：近 hold_days 内曾满足全部底部条件，且此后未突破、底部区间最低价未被跌破
                var hold = Ci(cfg, "bottom.hold_days", 10);
                for (var j = i - 1; j >= Math.Max(60, i - hold); j--)
                {
                    if (!BottomCheck(s, j, cfg).Ok) continue;
                    double lo = double.PositiveInfinity; for (var q = Math.Max(0, j - 19); q <= j; q++) lo = Math.Min(lo, l[q]);
                    if (c[i] >= lo) { bOk = true; bC["held_from"] = s.Date[j]; bC["held_support"] = R(lo, 3); }
                    break;
                }
            }
            tr.Stage = trendOk ? S5 : bOk ? S1 : S0;
        }

        // ── 价位：有当日信号用信号价位；否则以最新收盘为参考价 ──
        if (tr.Signal is not null) tr.Levels = tr.Signal.Levels;
        else if (tr.Stage != S6)
        {
            var cands = new List<(double, string)>();
            if (ep is not null)
            {
                double lowAfter = s.Low[ep.LastT]; for (var j = ep.LastT; j <= i; j++) lowAfter = Math.Min(lowAfter, s.Low[j]);
                cands.Add((Math.Min(ep.PLast, lowAfter), "突破平台下沿(平台价/突破后最低价)"));
            }
            var sw = BrModel.SwingLows(l, i, 60, 3).Where(j => l[j] < c[i]).ToList();
            if (sw.Count > 0) cands.Add((l[sw[^1]], $"最近有效波段低点({s.Date[sw[^1]]})"));
            if (ep is not null && !double.IsNaN(ep.PreLow)) cands.Add((ep.PreLow, "突破前最近有效波段低点"));
            tr.Levels = BuildLevels(s, i, c[i], "参考价=最新完整交易日收盘（无当日信号，非计划成交价）", true, cands, ep?.PlatHigh, cfg);
        }
        else tr.Levels = new BrLevels { RrLabel = "反转失败，不计算介入价位" };
        if (tr.Levels.RewardPct is double rw && rw < Cd(cfg, "chase.near_resistance", 0.03) && !tr.Tags.Any(x => x.StartsWith("接近压力位"))) tr.Tags.Add($"接近压力位(距{P1(rw)})");
        if (tr.Stage == S6) tr.Tags.Add("跌破支撑/反转失败");

        // ── 最近一次信号的 T+1 检查（T+1 已发生，属于 ≤ i 的数据）──
        tr.Recent = tr.Signal is null ? events.LastOrDefault(e => e.Index < i && i - e.Index <= Ci(cfg, "recent_signal_days", 10)) : null;
        if (tr.Recent is not null)
        {
            var (st, en, note, lv) = ExecCheck(s, tr.Recent, cfg);
            tr.RecentExecStatus = st; tr.RecentExecNote = note; tr.RecentEntry = en; tr.RecentLevels = lv;
        }

        // ── 操作状态 ──
        var hot = tr.Hot;
        switch (tr.Stage)
        {
            case S6: tr.OpStatus = "反转失败"; tr.OpNote = $"停止新增介入，重新评估：{tr.FailReason}（{tr.FailDate}）"; break;
            case S1: tr.OpStatus = "观察"; tr.OpNote = "底部形成中，无买入信号"; break;
            case S0: tr.OpStatus = "观察"; tr.OpNote = "未进入反转结构"; break;
            case S2:
                tr.OpStatus = "等待确认";
                if (tr.Signal?.Type == "B1") tr.OpNote = "放量突破(B1)：等待次日确认；T+1 高开>3% 取消直接追入，低开跌回平台取消" + (hot ? "；已过热，不追涨" : "");
                break;
            case S3:
                if (tr.Signal?.Type == "B2")
                {
                    if (hot || tr.Signal.FarFromPlatform) { tr.OpStatus = "不追涨"; tr.OpNote = "B2 确认但已涨高/远离平台"; }
                    else if (tr.Levels.RrOk) { tr.OpStatus = "介入候选"; tr.OpNote = $"B2 突破确认；{tr.Levels.RrLabel}"; }
                    else { tr.OpStatus = "等待确认"; tr.OpNote = $"B2 突破确认但 {tr.Levels.RrLabel}"; }
                }
                else { tr.OpStatus = hot ? "不追涨" : "等待确认"; tr.OpNote = "突破已确认，等待缩量回踩"; }
                break;
            case S4:
                tr.OpStatus = hot ? "不追涨" : "介入候选"; tr.OpNote = $"B3 缩量回踩（优先关注）；{tr.Levels.RrLabel}"; break;
            case S5:
                tr.OpStatus = hot ? "不追涨" : "观察"; tr.OpNote = hot ? "趋势强但已涨高，不追涨" : "持仓跟踪或等待新的回踩"; break;
        }
        // ── V1.2 B1 分级 ──
        if (tr.Signal?.Type == "B1")
        {
            (tr.B1Grade, tr.B1GradeNote) = B1PreGrade(tr.Signal, cfg);
            if (cfg.B("trade.b1_grade.enabled", false) && tr.Stage == S2)
            {
                if (tr.B1Grade == "A" && !hot) { tr.OpStatus = "介入候选"; tr.OpNote = "B1-A：突破有效、R/R 合格、未过热；T+1 开盘检查（高开>3% 或低开跌回平台则取消）后介入"; }
                else if (tr.B1Grade == "B") { tr.OpStatus = "等待确认"; tr.OpNote = $"B1-B：{tr.B1GradeNote}；转入回踩观察池，观察 3–10 日是否出现 B3"; }
                else if (tr.B1Grade == "C") { tr.OpStatus = "观察"; tr.OpNote = $"B1-C：{tr.B1GradeNote}；取消介入资格"; }
            }
        }
        else if (tr.Recent?.Type == "B1" && tr.Stage != S6)
        {
            var fe = B1Final(s, tr.Recent, cfg);
            tr.B1Grade = fe.Grade; tr.B1GradeNote = fe.Grade == "A" ? "T+1 检查通过" : fe.FirstReason;
            var dsi = i - tr.Recent.Index;
            if (cfg.B("trade.b1_grade.enabled", false) && fe.Grade == "B")
            {
                int w0 = Ci(cfg, "b1_grade.watch_min_days", 3), w1 = Ci(cfg, "b1_grade.watch_max_days", 10);
                tr.OpNote += dsi <= w1 ? $"；B1-B 回踩观察池第{dsi}日（{tr.Recent.Date} 起，第{w0}–{w1}日内等 B3）" : "";
            }
        }
        tr.Position = Position(tr, cfg);
        return tr;
    }

    /// <summary>仓位参考（按单笔风险预算；未配置账户规模时只给公式）。</summary>
    public static JsonObject Position(BrTrade tr, BrConfig cfg)
    {
        var pref = cfg.S("position.risk_preference", "medium");
        double rlo = cfg.D("position.risk_per_trade_min", 0.005), rhi = cfg.D("position.risk_per_trade_max", 0.01);
        var sig = tr.SignalType;
        string eligible; double mult;
        switch (pref)
        {
            case "low": eligible = sig is "B2" or "B3" ? "可考虑" : "仅关注 B2/B3"; mult = sig is "B2" or "B3" ? 1 : 0; break;
            case "high": eligible = sig != "无" ? "可考虑（严格风控）" : "无信号"; mult = sig != "无" ? 1 : 0; break;
            default: eligible = sig == "B1" ? "B1 小仓观察" : sig is "B2" or "B3" ? "确认后可考虑" : "无信号"; mult = sig == "B1" ? cfg.D("position.b1_risk_multiplier_medium", 0.5) : sig is "B2" or "B3" ? 1 : 0; break;
        }
        var o = new JsonObject
        {
            ["risk_preference"] = pref, ["eligibility"] = eligible, ["risk_per_trade"] = $"{PyFmt(rlo * 100, 1)}%~{PyFmt(rhi * 100, 1)}%", ["risk_multiplier"] = mult,
            ["formula"] = "计划持仓股数 = 账户权益 × 单笔风险比例 × 系数 ÷ (计划介入价 − 止损价)，按100股取整；并受可用资金、佣金、流动性、已有持仓约束",
        };
        var eq = cfg.Node("position.account_equity");
        if (eq is JsonValue ev && ev.TryGetValue<double>(out var equity) && equity > 0 && tr.Levels.StopReliable && mult > 0 && tr.Levels.Entry is double en && tr.Levels.Stop is double sp)
        {
            int Sh(double r) { var raw = equity * r * mult / (en - sp); var cap = equity * cfg.D("position.max_position_pct", 0.2) / en; return (int)(Math.Floor(Math.Min(raw, cap) / 100) * 100); }
            o["shares_min"] = Sh(rlo); o["shares_max"] = Sh(rhi); o["account_equity"] = equity;
        }
        else o["shares"] = "未配置账户规模，只给风险比例与公式";
        return o;
    }

    /// <summary>排行榜分组：A 趋势强且位置合理；B 趋势已出现，等待确认/回踩；C 评分较高但涨幅过大，暂不追。</summary>
    public static string AssignGroup(BrTrade tr, int total, BrConfig cfg)
    {
        if (tr.Stage is S6 or S0 or S1 or "数据不足") return "";
        if (tr.OpStatus == "介入候选") return "A";
        if (tr.Hot || tr.OpStatus == "不追涨") return total >= cfg.I("trade.groups.c_min_score", 50) ? "C" : "";
        return tr.Stage is S2 or S3 or S4 or S5 ? "B" : "";
    }
}
