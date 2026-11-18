using System.Globalization;
using System.Text;
using ThreeBucket.Core.Services.BottomReversal;

namespace ThreeBucket.Core.Services.SlowRise;

/// <summary>
/// 单只股票日K（复用 BrMarketData 东财 push2his）+ 预计算指标。
/// 价格口径：A = 调整后价格（默认 data.adjust="ratio"：由不复权 fqt=0 与东财前复权 fqt=1 反推每个除权日的参考价，构造“等比前复权”，
/// 最新一日 = 实际价；东财 fqt=1 是“差值”前复权，长历史会出现负价（如 600519 2013 年），比例指标失真）；Raw = 不复权实际成交价（涨跌停、股价≥2元、成交额、股数）。
/// 等比复权只按段乘常数，所有比例类条件（涨跌幅、ATR/C、DIF/C、R²…）只依赖 ≤T 的数据，不受后续除权影响。
/// </summary>
public sealed class SrStock
{
    public string Code = "", Name = "", Industry = "", Board = "";
    public double Lp;          // 涨跌停幅度
    public double? Mv;         // 当前总市值（仅实盘扫描用）
    public string[] Date = [];
    public double[] O = [], H = [], L = [], C = [], V = [], Amt = [];
    public double[] RO = [], RH = [], RL = [], RC = [];
    public double[] RefPrev = [];   // 交易所口径前收盘：普通日 = 前一日不复权收盘；除权日 = 除权参考价（涨跌幅、涨跌停价都按它算）
    public int[] Cal = [];     // 在交易日历中的位置
    public Dictionary<string, int> Ix = new();
    public int AdjEvents;
    // 指标
    public double[] Pct = [], Ma5 = [], Ma10 = [], Ma20 = [], Ma60 = [], MaS = [], MaL = [], Vma5 = [], Vma20 = [], VmaPrev = [], Vma60Prev = [], Dif = [], Dea = [], Hist = [], Atr = [], Ll250 = [], RPct = [];
    public double[] Hh = []; public int[] HhIx = []; public double[] Llw = []; public int[] LlwIx = [];
    public bool[] OneWord = [], ZeroVol = [];
    public int Count => Date.Length;

    public int MaSP, MaLP;
    /// <summary>回测变体换了均线周期时按需重算 MA_S / MA_L（t37：均线组合在回测里真正生效）。同一只股票同一时刻只被一个线程处理。</summary>
    public void EnsureMa(SrParams p)
    {
        if (MaSP != p.MaS) { MaS = BrIndicators.Sma(C, p.MaS); MaSP = p.MaS; }
        if (MaLP != p.MaL) { MaL = BrIndicators.Sma(C, p.MaL); MaLP = p.MaL; }
    }
    public SrStock Prepare(SrParams p)
    {
        var n = Count;
        Pct = new double[n]; RPct = new double[n]; Pct[0] = double.NaN; RPct[0] = double.NaN;
        if (RefPrev.Length != n) { RefPrev = new double[n]; for (var i = 1; i < n; i++) RefPrev[i] = RC[i - 1]; }
        for (var i = 1; i < n; i++) { Pct[i] = C[i] / C[i - 1] - 1; RPct[i] = RC[i] / RefPrev[i] - 1; }
        Ma5 = BrIndicators.Sma(C, 5); Ma10 = BrIndicators.Sma(C, 10); Ma20 = BrIndicators.Sma(C, 20); Ma60 = BrIndicators.Sma(C, 60);
        MaS = BrIndicators.Sma(C, p.MaS); MaL = BrIndicators.Sma(C, p.MaL); MaSP = p.MaS; MaLP = p.MaL;
        Vma5 = BrIndicators.Sma(V, 5); Vma20 = BrIndicators.Sma(V, p.VmaWin);
        VmaPrev = BrIndicators.PrevMean(V, p.VmaWin); Vma60Prev = BrIndicators.PrevMean(V, p.DryVmaWin);
        (Dif, Dea, Hist) = BrIndicators.Macd(C, p.MacdF, p.MacdSl, p.MacdSig);
        // ATR(14)：TR 的 14 日简单平均
        var tr = new double[n];
        for (var i = 0; i < n; i++) tr[i] = i == 0 ? H[i] - L[i] : Math.Max(H[i] - L[i], Math.Max(Math.Abs(H[i] - C[i - 1]), Math.Abs(L[i] - C[i - 1])));
        Atr = BrIndicators.Sma(tr, 14);
        (Hh, HhIx) = RollExt(H, p.HighWin, true); (Llw, LlwIx) = RollExt(L, p.LowWin, false);
        (Ll250, _) = RollExt(L, p.HighWin, false);
        OneWord = new bool[n]; ZeroVol = new bool[n];
        for (var i = 1; i < n; i++)
        {
            OneWord[i] = RH[i] - RL[i] < 1e-6 && Math.Abs(RPct[i]) >= Lp - 0.005;   // 一字板：不复权最高=最低 且 涨跌幅（交易所口径）达到涨跌停
            ZeroVol[i] = !(V[i] > 0);
        }
        return this;
    }

    /// <summary>滚动窗口极值与位置（同值取最近一次出现）；不足窗口 → NaN / -1。</summary>
    private static (double[], int[]) RollExt(double[] x, int w, bool max)
    {
        var n = x.Length; var v = new double[n]; var ix = new int[n];
        for (var i = 0; i < n; i++)
        {
            if (i < w - 1) { v[i] = double.NaN; ix[i] = -1; continue; }
            var best = x[i]; var bi = i;
            for (var k = i - 1; k > i - w; k--)
                if (max ? x[k] > best : x[k] < best) { best = x[k]; bi = k; }
            v[i] = best; ix[i] = bi;
        }
        return (v, ix);
    }

    public SrStock Truncate(int n) => new()
    {
        Code = Code, Name = Name, Industry = Industry, Board = Board, Lp = Lp, Mv = Mv,
        Date = Date[..n], RO = RO[..n], RH = RH[..n], RL = RL[..n], RC = RC[..n], RefPrev = RefPrev[..n], V = V[..n], Amt = Amt[..n], Cal = Cal[..n],
        O = O[..n], H = H[..n], L = L[..n], C = C[..n],
    };
}

public static class SrData
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed class Bars
    {
        public string[] D = []; public double[] O = [], C = [], H = [], L = [], V = [], A = [];
        public int Count => D.Length;
        public Bars Take(int n) => new() { D = D[..n], O = O[..n], C = C[..n], H = H[..n], L = L[..n], V = V[..n], A = A[..n] };
    }

    public static Bars FromBr(BrSeries s) => new() { D = s.Date, O = s.Open, C = s.Close, H = s.High, L = s.Low, V = s.Volume, A = s.Amount };

    /// <summary>东财日K；cacheDir 非空时按“当日有效”缓存（data/cache/sr_kline/&lt;code&gt;_&lt;fqt&gt;.csv，首行 #日期|lmt）。</summary>
    public static async Task<Bars?> KlineAsync(string secid, int lmt, int fqt, string? cacheDir, int retries, CancellationToken ct)
    {
        string? f = null; var today = BrMarketData.NowCn().ToString("yyyy-MM-dd");
        if (cacheDir is not null)
        {
            f = Path.Combine(cacheDir, $"{secid.Replace('.', '_')}_{fqt}.csv");
            if (File.Exists(f))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(f, ct).ConfigureAwait(false);
                    if (lines.Length > 1 && lines[0] == $"#{today}|{lmt}")
                    {
                        var rows = lines.Skip(1).Select(l => l.Split(',')).ToList();
                        double[] Col(int k) => rows.Select(r => double.Parse(r[k], Inv)).ToArray();
                        return new Bars { D = rows.Select(r => r[0]).ToArray(), O = Col(1), C = Col(2), H = Col(3), L = Col(4), V = Col(5), A = Col(6) };
                    }
                }
                catch { }
            }
        }
        var k = await BrMarketData.FetchKlineAsync(secid, lmt, fqt, retries, ct).ConfigureAwait(false);
        if (k is null) return null;
        var b = FromBr(k.S);
        if (f is not null)
        {
            var sb = new StringBuilder(); sb.Append($"#{today}|{lmt}\n");
            for (var j = 0; j < b.Count; j++) sb.Append(string.Join(",", b.D[j], R(b.O[j]), R(b.C[j]), R(b.H[j]), R(b.L[j]), R(b.V[j]), R(b.A[j]))).Append('\n');
            try { await File.WriteAllTextAsync(f, sb.ToString(), ct).ConfigureAwait(false); } catch { }
        }
        return b;
        static string R(double v) => double.IsNaN(v) ? "NaN" : v.ToString("R", Inv);
    }

    /// <summary>
    /// 组装一只股票：raw=fqt0、qfq=fqt1 按日期对齐；adjust="ratio" 时构造等比前复权（见类注释），="qfq" 时直接用东财前复权。
    /// 除权日判定：|(raw−qfq)[k] − (raw−qfq)[k−1]| &gt; 0.011（排除两位小数舍入噪声）；参考价 ref = raw[k] − (qfq[k] − qfq[k−1])。
    /// 送转（因子 &lt; 0.9）同时把之前的成交量按因子折算，避免放量倍数被拆股扭曲；成交额不调整。
    /// </summary>
    public static SrStock? Build(string code, Bars raw, Bars? qfq, string adjust, IReadOnlyDictionary<string, int> cal, SrParams p)
    {
        var ixq = new Dictionary<string, int>();
        if (qfq is not null) for (var j = 0; j < qfq.Count; j++) ixq[qfq.D[j]] = j;
        var keep = new List<int>();
        for (var j = 0; j < raw.Count; j++)
            if (cal.ContainsKey(raw.D[j]) && (qfq is null || ixq.ContainsKey(raw.D[j])) && raw.C[j] > 0 && raw.O[j] > 0) keep.Add(j);
        if (keep.Count < 30) return null;
        var n = keep.Count;
        var s = new SrStock { Code = code, Board = BrMarketData.Board(code), Lp = BrMarketData.LimitPct(code) };
        s.Date = keep.Select(j => raw.D[j]).ToArray();
        s.RO = keep.Select(j => raw.O[j]).ToArray(); s.RH = keep.Select(j => raw.H[j]).ToArray(); s.RL = keep.Select(j => raw.L[j]).ToArray(); s.RC = keep.Select(j => raw.C[j]).ToArray();
        s.V = keep.Select(j => raw.V[j]).ToArray(); s.Amt = keep.Select(j => raw.A[j]).ToArray();
        s.Cal = s.Date.Select(d => cal[d]).ToArray();
        for (var i = 0; i < n; i++) s.Ix[s.Date[i]] = i;
        var f = new double[n]; var vf = new double[n]; f[n - 1] = 1; vf[n - 1] = 1;
        s.RefPrev = new double[n]; for (var i = 1; i < n; i++) s.RefPrev[i] = s.RC[i - 1];
        if (qfq is not null)
        {
            var qc = keep.Select(j => qfq.C[ixq[raw.D[j]]]).ToArray();
            // 东财前复权是分段仿射：qfq = a·raw − b（送转按比例、现金分红按差值，各段 a、b 不同）。从最新一段往前走：
            // 前一日仍满足本段 (a,b) → 同一段；否则 k 为除权日，参考价 ref = raw[k] − (qfq[k] − qfq[k−1]) / a（本段的 a），再用前面几天拟合新一段的 (a,b)。
            var (sa, sb) = Fit(s.RC, qc, n - 1);
            for (var k = n - 1; k >= 1; k--)
            {
                f[k - 1] = f[k]; vf[k - 1] = vf[k];
                var pred = sa * s.RC[k - 1] - sb;
                if (Math.Abs(pred - qc[k - 1]) <= Tol(qc[k - 1])) continue;
                var refp = s.RC[k] - (qc[k] - qc[k - 1]) / sa;
                var fac = refp / s.RC[k - 1];
                (sa, sb) = Fit(s.RC, qc, k - 1);
                if (!(fac > 0.05 && fac < 1.5)) continue;   // 异常值不调整
                f[k - 1] = f[k] * fac; s.AdjEvents++; s.RefPrev[k] = refp;
                if (fac < 0.9) vf[k - 1] = vf[k] * fac;
            }
        }
        if (adjust == "qfq" && qfq is not null)
        {
            s.O = keep.Select(j => qfq.O[ixq[raw.D[j]]]).ToArray(); s.H = keep.Select(j => qfq.H[ixq[raw.D[j]]]).ToArray(); s.L = keep.Select(j => qfq.L[ixq[raw.D[j]]]).ToArray(); s.C = keep.Select(j => qfq.C[ixq[raw.D[j]]]).ToArray();
            return s.Prepare(p);
        }
        s.O = new double[n]; s.H = new double[n]; s.L = new double[n]; s.C = new double[n];
        for (var i = 0; i < n; i++) { s.O[i] = s.RO[i] * f[i]; s.H[i] = s.RH[i] * f[i]; s.L[i] = s.RL[i] * f[i]; s.C[i] = s.RC[i] * f[i]; s.V[i] = s.V[i] / vf[i]; }
        return s.Prepare(p);
    }

    private static double Tol(double q) => 0.012 + Math.Abs(q) * 2e-5;

    /// <summary>用 hi 及之前最多 10 天拟合 qfq = a·raw − b（残差都在舍入误差内才接受，窗口逐步缩小）；价格几乎不变时退回纯比例或纯差值中较吻合者。</summary>
    private static (double A, double B) Fit(double[] raw, double[] q, int hi)
    {
        foreach (var m in new[] { 10, 6, 4, 3 })
        {
            var lo = hi - m + 1; if (lo < 0) continue;
            double sx = 0, sy = 0, sxx = 0, sxy = 0; var nn = m;
            for (var t = lo; t <= hi; t++) { sx += raw[t]; sy += q[t]; sxx += raw[t] * raw[t]; sxy += raw[t] * q[t]; }
            var den = nn * sxx - sx * sx; if (Math.Abs(den) < 1e-9) continue;
            var a = (nn * sxy - sx * sy) / den; var b = -(sy - a * sx) / nn;
            if (!(a > 0)) continue;
            var ok = true; for (var t = lo; t <= hi && ok; t++) if (Math.Abs(a * raw[t] - b - q[t]) > Tol(q[t])) ok = false;
            if (ok && (raw.Skip(lo).Take(m).Max() - raw.Skip(lo).Take(m).Min()) > 0.2) return (a, b);
        }
        // 兜底：纯比例 (q/raw, 0) 或 纯差值 (1, raw−q)，用 hi−1 检验哪个更吻合
        var ap = q[hi] / raw[hi]; var bs = raw[hi] - q[hi];
        if (hi >= 1)
        {
            var ep = Math.Abs(ap * raw[hi - 1] - q[hi - 1]); var es = Math.Abs(raw[hi - 1] - bs - q[hi - 1]);
            return ep <= es ? (ap, 0) : (1, bs);
        }
        return (1, bs);
    }

    /// <summary>指数（日历 + 大盘过滤 + 基准）。</summary>
    public sealed class Index
    {
        public string Secid = "", Name = "";
        public string[] D = []; public double[] C = [], Ma = [], WeakMa = [];
        public Dictionary<string, int> Ix = new();
        public static Index From(string secid, string name, Bars b, SrParams p)
        {
            var x = new Index { Secid = secid, Name = name, D = b.D, C = b.C, Ma = BrIndicators.Sma(b.C, p.MktMa), WeakMa = BrIndicators.Sma(b.C, p.WeakMa) };
            for (var i = 0; i < x.D.Length; i++) x.Ix[x.D[i]] = i;
            return x;
        }
        /// <summary>大盘过滤（8.2-7）：logic=or：收盘≥MA60 或 近5日涨跌幅 &gt; −5% 即允许开仓；logic=and：两者都要满足。</summary>
        public bool Allow(string date, SrParams p)
        {
            if (!p.MktOn || !Ix.TryGetValue(date, out var i) || i < p.MktRetDays || double.IsNaN(Ma[i])) return true;
            var a = C[i] >= Ma[i]; var b = C[i] / C[i - p.MktRetDays] - 1 > p.MktRet5Min;
            return p.MktLogic switch { "and" => a && b, "ma" => a, _ => a || b };   // v2.0：logic=ma 只看 沪深300 C ≥ MA60
        }
        /// <summary>弱势（§10）：收盘 &lt; MA60 且 MA60 较 5 日前下降。</summary>
        public bool Weak(string date, SrParams p)
        {
            if (!Ix.TryGetValue(date, out var i) || i < p.WeakSlope || double.IsNaN(WeakMa[i - p.WeakSlope])) return false;
            return C[i] < WeakMa[i] && WeakMa[i] < WeakMa[i - p.WeakSlope];
        }
        /// <summary>市场环境：牛 = 收盘&gt;MA60 且 MA60 较20日前上升；熊 = 收盘&lt;MA60 且 MA60 下降；其余震荡。</summary>
        public string Regime(string date)
        {
            if (!Ix.TryGetValue(date, out var i) || i < 80) return "震荡";
            var m = WeakMa; if (double.IsNaN(m[i - 20])) return "震荡";
            if (C[i] > m[i] && m[i] > m[i - 20]) return "牛";
            if (C[i] < m[i] && m[i] < m[i - 20]) return "熊";
            return "震荡";
        }
    }
}
