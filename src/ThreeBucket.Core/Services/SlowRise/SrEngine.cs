using System.Text;

namespace ThreeBucket.Core.Services.SlowRise;

/// <summary>某只股票某个交易日（T 日收盘）的 L0~L4 判定、中间变量、评分。只用 ≤T 的数据。</summary>
public sealed class SrEval
{
    public int I; public string Date = "";
    public bool L0, L1, L2, L3Watch, L3Buy, L4, BuyCond, Deep;
    public int M0, M1, M2, M3, M4, MB;          // 各层未通过条件位掩码（第 k 位 = 第 k 个子条件未通过；关闭的条件视为通过）
    public double Close, Pct, Ma20Dev, AvgAmt20; public int Susp20, OneWord20;
    public double Hh, PLow, Dd, Rebound, RangePos; public int THigh = -1, TLow = -1, Dsl, HighAge;
    public double LowAfterMin;
    public int D0 = -1; public double D0Low = double.NaN, D0VolRatio = double.NaN, D0Pct = double.NaN, D0ClosePos = double.NaN, VolAfterRatio = double.NaN; public bool Dry, Pull;
    public double DryRatio = double.NaN;
    public int L3Count, WinLen, BigDays; public double Sigma = double.NaN, MinPct = double.NaN, Er = double.NaN, R2 = double.NaN, Slope = double.NaN, Gain = double.NaN, UpRatio = double.NaN, AboveRatio = double.NaN, AtrRatio = double.NaN;
    public int MaCross = -1, MacdCross = -1; public double DifAtCross = double.NaN, MaLSlope = double.NaN, HistNow = double.NaN, HistPrev = double.NaN;
    public double Vma5Ratio = double.NaN; public bool LimitUpClose;
    public bool KeyWatch;   // t37：a（MACD 底部金叉）已出现、b（均线金叉确认）未出现 → “重点观察”
    public bool HistUp;
    public double Score; public double[] Parts = new double[6];
    public static readonly string[] PartNames = ["调整深度", "放量质量", "爬升平滑度", "均线结构", "MACD质量", "位置安全边际"];
}

public static class SrEngine
{
    private static string F(double v, int nd = 2) => SrParams.F(v, nd);
    private static string P(double v) => SrParams.Pu(v, 1);

    /// <summary>
    /// T 日判定。deep=false 时 L0/L1 未通过就不再计算 L2~L4（回测提速，漏斗只需要逐层剩余）；scan/diag/selftest 用 deep=true 全算。
    /// d0Hint：观察池中已记录的 D0（当天 L2 找不到 D0 时用于 L3 排除 D0）。
    /// </summary>
    public static SrEval Eval(SrStock s, int i, SrParams p, bool deep = false, int d0Hint = -1, bool useCap = true)
    {
        var e = new SrEval { I = i, Date = s.Date[i], Close = s.C[i], Pct = s.RPct[i], Deep = deep };
        e.Ma20Dev = s.C[i] / s.Ma20[i] - 1;
        // ── L0 ──
        int m = 0;
        if (p.L0On[0] && (s.Name.Contains("ST", StringComparison.OrdinalIgnoreCase) || s.Name.Contains('退'))) m |= 1 << 0;
        if (p.L0On[1] && i + 1 < p.MinListDays) m |= 1 << 1;
        double amt = 0; var cnt = 0;
        for (var k = Math.Max(0, i - 19); k <= i; k++) { amt += s.Amt[k]; cnt++; }
        e.AvgAmt20 = cnt > 0 ? amt / cnt : double.NaN;
        if (p.L0On[2] && !(e.AvgAmt20 >= p.MinAmt20)) m |= 1 << 2;
        if (p.L0On[3] && useCap && s.Mv is double mv && mv < p.MinCap) m |= 1 << 3;
        // 停牌：日历最近 20 个交易日中缺失的天数 + 成交量为 0 的天数
        var calFrom = s.Cal[i] - 19; var have = 0; var zero = 0; var ow = 0;
        for (var k = i; k >= 0 && s.Cal[k] >= calFrom; k--) { have++; if (s.ZeroVol[k]) zero++; if (s.OneWord[k]) ow++; }
        e.Susp20 = 20 - have + zero; e.OneWord20 = ow;
        if (i < 19) e.Susp20 = 0;
        if (p.L0On[4] && e.Susp20 > p.MaxSusp20) m |= 1 << 4;
        if (p.L0On[5] && e.OneWord20 > p.MaxOneWord20) m |= 1 << 5;
        if (p.L0On[6] && s.RC[i] < p.MinPrice) m |= 1 << 6;
        e.M0 = m; e.L0 = m == 0;
        // ── L1 ──
        m = 0;
        if (i < p.HighWin - 1 || i < p.LowWin - 1 || double.IsNaN(s.Hh[i]))
        {
            e.M1 = (1 << 6) - 1; e.L1 = false; return e;
        }
        e.Hh = s.Hh[i]; e.THigh = s.HhIx[i]; e.PLow = s.Llw[i]; e.TLow = s.LlwIx[i];
        e.Dd = 1 - e.PLow / e.Hh; e.Rebound = s.C[i] / e.PLow - 1; e.Dsl = i - e.TLow; e.HighAge = i - e.THigh;
        var ll = s.Ll250[i]; e.RangePos = e.Hh > ll ? (s.C[i] - ll) / (e.Hh - ll) : double.NaN;
        var orderOk = e.THigh < e.TLow;
        if (p.L1On[0] && !(e.Dd >= p.DdMin && orderOk)) m |= 1 << 0;
        if (p.L1On[1] && !(e.HighAge >= p.HighAgeMin)) m |= 1 << 1;
        if (p.L1On[2] && !(e.Dsl >= p.DslMin && e.Dsl <= p.DslMax)) m |= 1 << 2;
        if (p.L1On[3] && !(e.Rebound >= p.RebMin && e.Rebound <= p.RebMax)) m |= 1 << 3;
        // 1.5 低点抬高：all_after_low = T_low 之后每天最低价 ≥ P_low×1.02（t34 修订）；after_low_last10 = 近10日中 T_low 之后的天；literal = 近10日最低价（含 T_low）
        double lmin = double.PositiveInfinity;
        var from = p.LowLiftMode switch { "literal" => i - 9, "after_low_last10" => Math.Max(e.TLow + 1, i - 9), _ => e.TLow + 1 };
        for (var k = Math.Max(0, from); k <= i; k++) lmin = Math.Min(lmin, s.L[k]);
        e.LowAfterMin = double.IsPositiveInfinity(lmin) ? double.NaN : lmin;
        if (p.L1On[4] && !(double.IsNaN(e.LowAfterMin) || e.LowAfterMin >= e.PLow * p.LowLift)) m |= 1 << 4;
        if (p.L1On[5] && !(e.RangePos <= p.RangePosMax)) m |= 1 << 5;
        e.M1 = m; e.L1 = m == 0;
        if (!deep && !(e.L0 && e.L1)) return e;
        if (e.TLow < 0) return e;

        // ── L2：T_low（含）之后、距今 ≤ d0_max_age 的最近一个满足 2.1~2.4 的放量启动日 ──
        m = 0; var d0 = -1;
        var lo = Math.Max(e.TLow, p.L2On[3] ? i - p.D0MaxAge : e.TLow);
        int bestPartial = -1, bestMask = int.MaxValue;
        for (var d = i; d >= Math.Max(1, lo); d--)
        {
            var mm = 0;
            var vr = s.V[d] / s.VmaPrev[d];
            if (p.L2On[0] && !(vr >= p.VolMult)) mm |= 1 << 0;
            if (p.L2On[1] && !(s.RPct[d] >= p.PctMin && s.RPct[d] <= p.PctMax)) mm |= 1 << 1;   // 涨幅用不复权（交易所口径）
            var rng = s.H[d] - s.L[d]; var cp = rng > 0 ? (s.C[d] - s.L[d]) / rng : double.NaN;
            if (p.L2On[2] && !(cp >= p.ClosePosMin)) mm |= 1 << 2;
            if (mm == 0) { d0 = d; break; }
            if (BitCount(mm) < BitCount(bestMask)) { bestMask = mm; bestPartial = d; }
        }
        if (d0 < 0) m = bestMask == int.MaxValue ? 0b111 : bestMask;
        if (d0 < 0 && !p.L2On[0] && !p.L2On[1] && !p.L2On[2]) d0 = lo;   // 三项都关闭时取窗口内最早一天（消融用）
        if (d0 >= 0)
        {
            e.D0 = d0; e.D0Low = s.L[d0]; e.D0VolRatio = s.V[d0] / s.VmaPrev[d0]; e.D0Pct = s.RPct[d0];
            var rng = s.H[d0] - s.L[d0]; e.D0ClosePos = rng > 0 ? (s.C[d0] - s.L[d0]) / rng : double.NaN;
            var hold = true; double vs = 0; var vn = 0;
            for (var k = d0 + 1; k <= i; k++) { if (s.C[k] < s.L[d0]) hold = false; vs += s.V[k]; vn++; }
            e.VolAfterRatio = vn > 0 ? vs / vn / s.V[d0] : double.NaN;
            if (p.L2On[4] && !hold) m |= 1 << 4;
            if (p.L2On[5] && vn > 0 && !(vs / vn <= s.V[d0])) m |= 1 << 5;
            // 加分 2.7 地量在先：T_low 前后 10 日（不超过 T 日）最小量 / VMA60(前一日) ≤ 0.6
            double vmin = double.PositiveInfinity;
            for (var k = Math.Max(0, e.TLow - p.DryWin); k <= Math.Min(i, e.TLow + p.DryWin); k++) vmin = Math.Min(vmin, s.V[k]);
            e.DryRatio = vmin / s.Vma60Prev[e.TLow]; e.Dry = e.DryRatio <= p.DryRatio;
            // 加分 2.8 D0 后出现缩量（量 < 前一日VMA20）回踩 MA10/MA20（最低价进入均线 1% 内）且收盘不破该均线
            for (var k = d0 + 1; k <= i && !e.Pull; k++)
            {
                if (!(s.V[k] < s.VmaPrev[k])) continue;
                bool Touch(double ma) => !double.IsNaN(ma) && s.L[k] <= ma * (1 + p.PullTouch) && s.C[k] >= ma;
                if (Touch(s.Ma10[k]) || Touch(s.Ma20[k])) e.Pull = true;
            }
        }
        else if (bestPartial >= 0) { e.D0VolRatio = s.V[bestPartial] / s.VmaPrev[bestPartial]; }
        e.M2 = m; e.L2 = m == 0 && d0 >= 0;

        // ── L3：统计窗口（since_low：T_low 之后，最多 20 天；fixed：最近 20 天），排除 D0 ──
        var dEx = d0 >= 0 ? d0 : d0Hint;
        int ws = p.L3Mode == "fixed" ? i - p.L3Win + 1 : Math.Max(e.TLow + 1, i - p.L3Win + 1);
        if (i - ws + 1 < p.L3MinWin) ws = i - p.L3MinWin + 1;
        ws = Math.Max(1, ws);
        var rets = new List<double>(); int up = 0, above = 0, big = 0, nd = 0; double minPct = double.PositiveInfinity, sumAbs = 0, sumLog = 0;
        for (var k = ws; k <= i; k++)
        {
            if (k == dEx) continue;
            nd++;
            var r = s.RPct[k]; rets.Add(r);   // 3.1/3.2/3.3 用不复权（交易所口径）日涨跌幅
            if (Math.Abs(r) >= p.BigMove) big++;
            minPct = Math.Min(minPct, r);
            var lr = Math.Log(s.C[k] / s.C[k - 1]); sumLog += lr; sumAbs += Math.Abs(lr);
            if (s.C[k] > s.O[k]) up++;
            if (!double.IsNaN(s.Ma20[k]) && s.C[k] > s.Ma20[k]) above++;
        }
        e.WinLen = nd; e.BigDays = big; e.MinPct = nd > 0 ? minPct : double.NaN;
        if (nd > 1) { var mean = rets.Average(); e.Sigma = Math.Sqrt(rets.Sum(x => (x - mean) * (x - mean)) / (nd - 1)); }
        e.Er = sumAbs > 0 ? Math.Abs(sumLog) / sumAbs : double.NaN;
        // ln(C) 回归：用排除 D0 后的累计对数收益路径 y_0=0, y_j=Σr（D0 的跳空被剔除，衡量“爬升是否规整”）
        {
            var ys = new List<double> { 0 }; double acc = 0;
            for (var k = ws; k <= i; k++) { if (k == dEx) continue; acc += Math.Log(s.C[k] / s.C[k - 1]); ys.Add(acc); }
            var nn = ys.Count;
            if (nn >= 3)
            {
                double mx = (nn - 1) / 2.0, my = ys.Average(), sxy = 0, sxx = 0, syy = 0;
                for (var j = 0; j < nn; j++) { sxy += (j - mx) * (ys[j] - my); sxx += (j - mx) * (j - mx); syy += (ys[j] - my) * (ys[j] - my); }
                e.Slope = sxy / sxx; e.R2 = syy > 0 ? sxy * sxy / (sxx * syy) : 0;
            }
        }
        e.Gain = Math.Exp(sumLog) - 1;
        e.UpRatio = nd > 0 ? (double)up / nd : double.NaN; e.AboveRatio = nd > 0 ? (double)above / nd : double.NaN;
        e.AtrRatio = s.Atr[i] / s.C[i];
        var pass = new bool[9];
        pass[0] = big <= p.BigMoveMax; pass[1] = e.Sigma <= p.SigmaMax; pass[2] = e.MinPct >= p.MaxDrop; pass[3] = e.Er >= p.ErMin;
        pass[4] = e.Slope > 0 && e.R2 >= p.R2Min; pass[5] = e.Gain >= p.GainMin && e.Gain <= p.GainMax; pass[6] = e.UpRatio >= p.UpRatio;
        pass[7] = e.AboveRatio >= p.AboveRatio; pass[8] = e.AtrRatio <= p.AtrMax;
        m = 0; var cntPass = 0;
        for (var j = 0; j < 9; j++) { if (!p.L3On[j]) pass[j] = true; if (pass[j]) cntPass++; else m |= 1 << j; }
        e.M3 = m; e.L3Count = cntPass;
        e.L3Watch = cntPass >= p.WatchMin;
        e.L3Buy = cntPass >= p.BuyMin && p.BuyMandatory.All(j => pass[j]);

        // ── L4 ──
        m = 0;
        if (p.L4Mode != "legacy")
        {
            // t37 三段式（按先后顺序，彼此独立，各自可开关）：
            // a. [D0+offset(默认−3), T] 内 DIF 上穿 DEA 且金叉当日 DIF/C ≤ 1%，取最近一次 = T_macd
            // b. T_macd 当日或之后（require_order）MA_S 上穿 MA_L，且该金叉距今 ≤ ma_cross_max_age 日（取最近一次）
            // c. 当前 DIF > DEA 且 C > MA_L
            var d0r = d0Hint >= 0 ? d0Hint : e.D0;
            if (d0r >= 0)
                for (var k = i; k >= Math.Max(1, d0r + p.MacdFromD0Off); k--)
                    if (s.Dif[k - 1] <= s.Dea[k - 1] && s.Dif[k] > s.Dea[k] && s.Dif[k] / s.C[k] <= p.DifRatioMax) { e.MacdCross = k; break; }
            if (e.MacdCross >= 0) e.DifAtCross = s.Dif[e.MacdCross] / s.C[e.MacdCross];
            var bFrom = Math.Max(1, i - p.MaCrossMaxAge);
            if (p.RequireOrder && p.L4OOn[0] && e.MacdCross >= 0) bFrom = Math.Max(bFrom, e.MacdCross);
            for (var k = i; k >= bFrom; k--)
                if (s.MaS[k - 1] <= s.MaL[k - 1] && s.MaS[k] > s.MaL[k]) { e.MaCross = k; break; }
            var aOk = e.MacdCross >= 0; var bOk = e.MaCross >= 0 && (!p.RequireOrder || !p.L4OOn[0] || aOk);
            if (p.L4OOn[0] && !aOk) m |= 1 << 0;
            if (p.L4OOn[1] && !bOk) m |= 1 << 1;
            if (p.L4OOn[2] && !(s.Dif[i] > s.Dea[i] && s.C[i] > s.MaL[i])) m |= 1 << 2;
            e.HistNow = s.Hist[i]; e.HistPrev = s.Hist[i - 1];
            e.MaLSlope = s.MaL[i] - s.MaL[i - p.MaLSlopeDays];
            e.KeyWatch = aOk && !bOk;
            e.M4 = m; e.L4 = m == 0;
            goto afterL4;
        }
        for (var k = i; k > i - p.CrossLb && k >= 1; k--)
            if (s.MaS[k - 1] <= s.MaL[k - 1] && s.MaS[k] > s.MaL[k]) { e.MaCross = k; break; }
        for (var k = i; k > i - p.CrossLb && k >= 1; k--)
            if (s.Dif[k - 1] <= s.Dea[k - 1] && s.Dif[k] > s.Dea[k]) { e.MacdCross = k; break; }
        if (p.L4On[0] && e.MaCross < 0) m |= 1 << 0;
        if (p.L4On[1] && !(s.C[i] > s.MaL[i])) m |= 1 << 1;
        e.MaLSlope = s.MaL[i] - s.MaL[i - p.MaLSlopeDays];
        if (p.L4On[2] && !(e.MaLSlope >= 0)) m |= 1 << 2;
        if (p.L4On[3] && e.MacdCross < 0) m |= 1 << 3;
        if (e.MacdCross >= 0) e.DifAtCross = s.Dif[e.MacdCross] / s.C[e.MacdCross];
        if (p.L4On[4] && !(e.MacdCross >= 0 && e.DifAtCross <= p.DifRatioMax)) m |= 1 << 4;
        e.HistNow = s.Hist[i]; e.HistPrev = s.Hist[i - 1];
        if (p.L4On[5] && !(s.Dif[i] > s.Dea[i] && s.Hist[i] >= s.Hist[i - 1])) m |= 1 << 5;
        if (p.L4On[6] && !(e.MaCross >= 0 && e.MacdCross >= 0 && Math.Abs(e.MaCross - e.MacdCross) <= p.CrossGap)) m |= 1 << 6;
        e.M4 = m; e.L4 = m == 0;
        afterL4:

        // ── 评分（绝对阈值线性插值，t34 修订：不用横截面分位数）──
        Score(s, i, e, p);

        // ── BUY 附加条件 ──
        m = 0;
        e.Vma5Ratio = s.Vma5[i] / s.Vma20[i];
        e.LimitUpClose = s.RC[i] >= Math.Round(s.RefPrev[i] * (1 + s.Lp), 2) - 0.0001;   // 收盘 ≥ 涨停价（前收盘×(1+涨跌幅限制)，除权日用参考价）
        if (p.BuyOn[0] && !(s.RPct[i] < p.BuyMaxPct)) m |= 1 << 0;
        if (p.BuyOn[1] && !(e.Ma20Dev < p.BuyMaxDev)) m |= 1 << 1;
        if (p.BuyOn[2] && !(e.Vma5Ratio >= p.Vma5Min)) m |= 1 << 2;
        if (p.BuyOn[3] && e.LimitUpClose) m |= 1 << 3;
        if (p.BuyOn[4] && !(e.Score >= p.ScoreMin)) m |= 1 << 4;
        e.MB = m; e.BuyCond = m == 0;
        return e;
    }

    private static int BitCount(int x) => System.Numerics.BitOperations.PopCount((uint)x);

    private static void Score(SrStock s, int i, SrEval e, SrParams p)
    {
        // t37 端点（说明书作者给的初始值，不调参）：
        // 调整深度 15：DD 40%→8，≥70%→15 | 放量质量 20：放量倍数 2→6、≥4→12；收盘位置 0.6→2、1.0→4；地量在先 +2；回踩不破 +2
        // 爬升平滑度 20：ER 0.30→3、≥0.60→8；R² 0.5→3、0.9→6；σ 2.5%→2、≤1%→6 | 均线结构 15：金叉后站稳 MA_L 天数 0→0、≥5→8；MA20 走平转头 +4；MA60 走平 +3
        // MACD 质量 15：金叉日 DIF/C +1%→3、≤−3%→9；红柱放大 +3 | 位置安全 15：反弹 30%→0、5%→8；MA20 乖离 12%→0、0%→7
        // 低于起点（DD<40%、ER<0.30、R²<0.5、σ>2.5%）记 0 分；无 D0/无金叉的项记 0 分
        var a = e.Dd >= p.ScDd[0] ? SrParams.Lin(e.Dd, p.ScDd) : 0;
        var b = (double.IsNaN(e.D0VolRatio) || e.D0 < 0 || e.D0VolRatio < p.ScVol[0] ? 0 : SrParams.Lin(e.D0VolRatio, p.ScVol)) + (e.D0 < 0 || double.IsNaN(e.D0ClosePos) || e.D0ClosePos < p.ScClosePos[0] ? 0 : SrParams.Lin(e.D0ClosePos, p.ScClosePos))
              + (e.Dry ? p.ScDry : 0) + (e.Pull ? p.ScPull : 0);
        var c = (e.Er >= p.ScEr[0] ? SrParams.Lin(e.Er, p.ScEr) : 0) + (e.R2 >= p.ScR2[0] && e.Slope > 0 ? SrParams.Lin(e.R2, p.ScR2) : 0) + (e.Sigma <= p.ScSigma[0] ? SrParams.Lin(e.Sigma, p.ScSigma) : 0);
        var stand = 0;
        if (e.MaCross >= 0) for (var k = e.MaCross; k <= i; k++) if (s.C[k] > s.MaL[k]) stand++;
        var d = (e.MaCross >= 0 ? SrParams.Lin(stand, p.ScStand) : 0) + (i >= 5 && s.Ma20[i] >= s.Ma20[i - 5] ? p.ScMa20Up : 0)
              + (i >= 5 && s.Ma60[i] >= s.Ma60[i - 5] * (1 - p.Ma60FlatTol) ? p.ScMa60Flat : 0);
        e.HistUp = s.Hist[i] > 0 && s.Hist[i] >= s.Hist[i - 1];
        var f = (e.MacdCross >= 0 && !double.IsNaN(e.DifAtCross) ? SrParams.Lin(e.DifAtCross, p.ScMacd) : 0) + (e.HistUp ? p.ScHistPts : 0);
        var g = SrParams.Lin(e.Rebound, p.ScReb) + SrParams.Lin(Math.Max(0, e.Ma20Dev), p.ScDev);
        e.Parts[0] = a; e.Parts[1] = b; e.Parts[2] = c; e.Parts[3] = d; e.Parts[4] = f; e.Parts[5] = g;
        e.Score = Math.Round(a + b + c + d + f + g, 1);
    }

    // ── 文本：未通过条件（fail_reason）──
    public static string FailText(SrStock s, SrEval e, SrParams p, bool forBuy)
    {
        var parts = new List<string>();
        void Add(int mask, string[] names, Func<int, string> val) { for (var j = 0; j < names.Length; j++) if ((mask & (1 << j)) != 0) parts.Add(names[j] + val(j)); }
        Add(e.M0, SrParams.L0Names, j => j switch { 2 => $"({F(e.AvgAmt20 / 1e8)}亿)", 4 => $"({e.Susp20}天)", 5 => $"({e.OneWord20}天)", 6 => $"({F(s.RC[e.I])})", _ => "" });
        Add(e.M1, SrParams.L1Names, j => j switch
        {
            0 => $"({P(e.Dd)}{(e.THigh >= e.TLow ? "，高点晚于低点" : "")})", 1 => $"({e.HighAge}日)", 2 => $"({e.Dsl}日∉[{p.DslMin},{p.DslMax}])", 3 => $"({P(e.Rebound)})",
            4 => $"(低点后最低{F(e.LowAfterMin)}<{F(e.PLow * p.LowLift)})", 5 => $"({P(e.RangePos)})", _ => "",
        });
        if (!(e.L0 && e.L1) && !e.Deep) return string.Join("；", parts);
        Add(e.M2, SrParams.L2Names, j => j switch { 0 => e.D0 < 0 ? "(无满足的D0)" : "", 5 => double.IsNaN(e.VolAfterRatio) ? "" : $"(启动后均量/D0量 {F(e.VolAfterRatio)})", _ => "" });
        var l3 = new List<string>();
        for (var j = 0; j < 9; j++) if ((e.M3 & (1 << j)) != 0) l3.Add(SrParams.L3Names[j] + j switch
        {
            0 => $"({e.BigDays}天)", 1 => $"({P(e.Sigma)})", 2 => $"({P(e.MinPct)})", 3 => $"({F(e.Er)})", 4 => $"(R²={F(e.R2)}{(e.Slope <= 0 ? ",斜率≤0" : "")})",
            5 => $"({P(e.Gain)})", 6 => $"({P(e.UpRatio)})", 7 => $"({P(e.AboveRatio)})", 8 => $"({P(e.AtrRatio)})", _ => "",
        });
        if (forBuy ? !e.L3Buy : !e.L3Watch) parts.Add($"L3 满足{e.L3Count}/9（{(forBuy ? $"BUY需≥{p.BuyMin}" + (p.BuyMandatory.Length > 0 ? "且" + string.Join("/", p.BuyMandatory.Select(k => SrParams.L3Names[k].Split(' ')[0])) + "必须" : "") + (p.L3On.Count(x => !x) > 0 ? $"；关闭的 {p.L3On.Count(x => !x)} 项视为满足" : "") : $"WATCH需≥{p.WatchMin}")}）：" + string.Join("、", l3));
        else if (forBuy && l3.Count > 0) { }
        if (forBuy)
        {
            if (p.L4Mode != "legacy")
                Add(e.M4, SrParams.L4ONames, j => j switch
                {
                    0 => "(D0前3日至今无 DIF/C≤1% 的金叉)",
                    1 => e.MacdCross >= 0 ? $"(T_macd {s.Date[e.MacdCross]} 之后近{p.MaCrossMaxAge}日无 MA{p.MaS}/{p.MaL} 金叉)" : $"(近{p.MaCrossMaxAge}日无 MA{p.MaS}/{p.MaL} 金叉)",
                    2 => $"(DIF{(s.Dif[e.I] > s.Dea[e.I] ? ">" : "≤")}DEA，收盘{(s.C[e.I] > s.MaL[e.I] ? ">" : "≤")}MA{p.MaL})",
                    _ => "",
                });
            else Add(e.M4, SrParams.L4Names, j => j switch { 4 => double.IsNaN(e.DifAtCross) ? "" : $"(DIF/C {P(e.DifAtCross)})", 6 => e.MaCross >= 0 && e.MacdCross >= 0 ? $"(相差{Math.Abs(e.MaCross - e.MacdCross)}日)" : "", _ => "" });
            Add(e.MB, SrParams.BuyNames, j => j switch { 0 => $"({P(e.Pct)})", 1 => $"({P(e.Ma20Dev)})", 2 => $"({F(e.Vma5Ratio)})", 4 => $"({F(e.Score, 1)})", _ => "" });
        }
        return string.Join("；", parts);
    }

    public static string ReasonText(SrStock s, SrEval e, SrParams p)
    {
        var sb = new StringBuilder();
        sb.Append($"DD {P(e.Dd)}·反弹 {P(e.Rebound)}；D0 {(e.D0 >= 0 ? s.Date[e.D0] : "-")} 放量{F(e.D0VolRatio)}倍");
        sb.Append($"；L3 {e.L3Count}/9（ER {F(e.Er)} R² {F(e.R2)} σ {P(e.Sigma)}）");
        if (p.L4Mode != "legacy")
        {
            if (e.MacdCross >= 0) sb.Append($"；MACD {(e.DifAtCross <= 0 ? "零轴下" : "近零轴")}金叉 T_macd {s.Date[e.MacdCross]}");
            if (e.MaCross >= 0) sb.Append($" → MA{p.MaS}/{p.MaL} 金叉 {s.Date[e.MaCross]}");
            if (e.KeyWatch) sb.Append("（重点观察：等均线金叉）");
        }
        else if (e.MaCross >= 0 && e.MacdCross >= 0) sb.Append($"；MA{p.MaS}/{p.MaL} 金叉 {s.Date[e.MaCross]} + MACD {(e.DifAtCross <= 0 ? "零轴下" : "近零轴")}金叉 {s.Date[e.MacdCross]} 共振");
        return sb.ToString();
    }
}

/// <summary>观察池条目（持久化用日期，不用调整后价格：复权因子会随新除权变化）。</summary>
public sealed class SrPoolEntry
{
    public string Code { get; set; } = "";
    public string EnterDate { get; set; } = "";
    public string D0Date { get; set; } = "";
    public string LowDate { get; set; } = "";
    public double ScoreAtEnter { get; set; }
    /// <summary>本轮（同一个 D0）已结束（出过 BUY 或已移出）：不再重复进池/出 BUY，直到出现更新的 D0（t37 去重；避免同一 D0 到期后当天又重新进池）。</summary>
    public bool Bought { get; set; }
    public string ClosedReason { get; set; } = "";
}

public sealed class SrPoolEvent
{
    public string Date = "", Kind = "", Note = "";   // Kind: ENTER / REMOVE / BUY / BUY_BLOCKED / WATCH
    public SrEval? Ev;
}

/// <summary>观察池状态机（每只股票独立；与资金/持仓解耦：BUY 信号即视为“升级”并移出观察池，是否成交由组合层决定）。</summary>
public static class SrPool
{
    public static SrPoolEntry? Step(SrStock s, int i, SrPoolEntry? entry, SrParams p, SrData.Index mkt, List<SrPoolEvent>? events, out SrEval ev, bool deep = false, bool useCap = true)
    {
        var d0Hint = entry is not null && s.Ix.TryGetValue(entry.D0Date, out var dh) ? dh : -1;
        ev = SrEngine.Eval(s, i, p, deep || entry is not null, d0Hint, useCap);
        var date = s.Date[i];
        if (entry is not null)
        {
            var why = new List<string>();
            var enterI = s.Ix.TryGetValue(entry.EnterDate, out var ei) ? ei : i;
            if (p.ValidFromD0 > 0) { if (d0Hint >= 0 && s.Cal[i] - s.Cal[d0Hint] >= p.ValidFromD0) why.Add($"超过有效期（D0起{p.ValidFromD0}日）"); }
            else if (s.Cal[i] - s.Cal[enterI] >= p.WatchValid) why.Add($"超过有效期{p.WatchValid}日");
            if (d0Hint >= 0 && s.C[i] < s.L[d0Hint]) why.Add("收盘跌破D0最低价");
            if (s.Ix.TryGetValue(entry.LowDate, out var li) && s.C[i] < s.L[li]) why.Add("收盘跌破P_low");
            if (!ev.L0) why.Add("L0失效");
            if (!ev.L1) why.Add("L1失效");
            if (entry.Bought)
            {
                var newerD0 = ev.L2 && ev.D0 >= 0 && ev.D0 > d0Hint;
                if (!newerD0) return entry;   // 同一轮已结束：静默，等更新的 D0
                entry = null;
            }
            else if (why.Count > 0)
            {
                events?.Add(new SrPoolEvent { Date = date, Kind = "REMOVE", Note = string.Join("、", why), Ev = ev });
                entry.Bought = true; entry.ClosedReason = "移出：" + string.Join("、", why);
                var newerD0 = ev.L2 && ev.D0 >= 0 && ev.D0 > d0Hint;
                if (!newerD0) return entry;
                entry = null;
            }
        }
        var newToday = false;
        if (entry is null && ev.L0 && ev.L1 && ev.L2 && ev.L3Watch && (!p.RequireNoDouble || !ev.L4))
        {
            entry = new SrPoolEntry { Code = s.Code, EnterDate = date, D0Date = s.Date[ev.D0], LowDate = s.Date[ev.TLow], ScoreAtEnter = ev.Score };
            newToday = true;
            events?.Add(new SrPoolEvent { Date = date, Kind = "ENTER", Ev = ev });
        }
        if (entry is null) return null;
        if (newToday && !p.AllowSameDay) { events?.Add(new SrPoolEvent { Date = date, Kind = "WATCH", Ev = ev }); return entry; }
        if (ev.L3Buy && ev.L4 && ev.BuyCond)
        {
            if (!mkt.Allow(date, p)) { events?.Add(new SrPoolEvent { Date = date, Kind = "BUY_BLOCKED", Note = "大盘过滤：不开新仓，信号只记录", Ev = ev }); return entry; }
            events?.Add(new SrPoolEvent { Date = date, Kind = "BUY", Note = entry.EnterDate, Ev = ev });
            entry.Bought = true; entry.ClosedReason = "已出 BUY";
            return entry;
        }
        events?.Add(new SrPoolEvent { Date = date, Kind = "WATCH", Note = entry.EnterDate, Ev = ev });
        return entry;
    }
}
