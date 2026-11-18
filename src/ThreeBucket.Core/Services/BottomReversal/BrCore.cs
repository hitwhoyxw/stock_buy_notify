using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>S27 参数：优先 data/bottom_reversal_v1_2.json（V1.2），依次回退 v1_1 / v1；每次运行重新读取。V1.1 文件保留作对照组。</summary>
public sealed class BrConfig
{
    public JsonNode Root { get; }
    public string Path { get; }

    private BrConfig(JsonNode root, string path) { Root = root; Path = path; }

    public const string FileName = "bottom_reversal_v1_2.json";
    public const string V11FileName = "bottom_reversal_v1_1.json";
    public const string LegacyFileName = "bottom_reversal_v1.json";
    public string ConfigHash => ConfigGovernance.Hash(Root);
    public string ConfigContract => ConfigGovernance.SchemaVersion;
    public string Version => S("version", "1.0");

    public static BrConfig Load(string dataDir, string? path = null)
    {
        var def = System.IO.Path.Combine(dataDir, FileName);
        if (path is null && !File.Exists(def)) def = System.IO.Path.Combine(dataDir, V11FileName);
        if (path is null && !File.Exists(def)) def = System.IO.Path.Combine(dataDir, LegacyFileName);
        var p = System.IO.Path.GetFullPath(path ?? def);
        if (!File.Exists(p)) throw new FileNotFoundException($"参数文件不存在: {p}");
        var schema = System.IO.Path.GetFileName(p);
        if(!new[]{FileName,V11FileName,LegacyFileName,"bottom_slowrise_v1.json","bottom_slowrise_v2.json"}.Contains(schema))schema=FileName;
        var node = ConfigGovernance.ReadJson(p,schema);
        return new BrConfig(node, p);
    }

    /// <summary>复制一份参数并替换某个节点（如 "trade.stop"），原对象不变；用于回测实验组。</summary>
    public BrConfig With(string path, JsonNode? value)
    {
        var root = Root.DeepClone(); var parts = path.Split('.'); JsonNode? n = root;
        for (var i = 0; i < parts.Length - 1; i++) n = n![parts[i]] ??= new JsonObject();
        n![parts[^1]] = value?.DeepClone();
        return new BrConfig(root, Path);
    }

    public JsonNode? Node(string path)
    {
        JsonNode? n = Root;
        foreach (var k in path.Split('.'))
        {
            if (n is not JsonObject o || !o.TryGetPropertyValue(k, out n)) return null;
        }
        return n;
    }

    public double D(string path, double def = double.NaN)
    {
        var n = Node(path);
        return n is null ? def : n.GetValue<double>();
    }

    public int I(string path, int def = 0)
    {
        var n = Node(path);
        return n is null ? def : (int)Math.Round(n.GetValue<double>());
    }

    public bool B(string path, bool def = false)
    {
        var n = Node(path);
        return n is null ? def : n.GetValue<bool>();
    }

    public string S(string path, string def = "")
    {
        var n = Node(path);
        return n is null ? def : n.GetValue<string>();
    }

    /// <summary>[[阈值, 分数], ...]</summary>
    public List<(double Thr, double Score)> Bands(string path)
    {
        var list = new List<(double, double)>();
        if (Node(path) is JsonArray arr)
            foreach (var x in arr)
                if (x is JsonArray p && p.Count >= 2) list.Add((p[0]!.GetValue<double>(), p[1]!.GetValue<double>()));
        return list;
    }

    public List<(double Thr, string Name)> RatingBands()
    {
        var list = new List<(double, string)>();
        if (Node("model.rating_bands") is JsonArray arr)
            foreach (var x in arr)
                if (x is JsonArray p && p.Count >= 2) list.Add((p[0]!.GetValue<double>(), p[1]!.GetValue<string>()));
        return list;
    }

    public List<double> Doubles(string path)
    {
        var list = new List<double>();
        if (Node(path) is JsonArray arr) foreach (var x in arr) list.Add(x!.GetValue<double>());
        return list;
    }

    public List<(string Name, string Secid)> Indexes()
    {
        var list = new List<(string, string)>();
        if (Node("market.indexes") is JsonObject o)
            foreach (var kv in o) list.Add((kv.Key, kv.Value!.GetValue<string>()));
        return list;
    }
}

/// <summary>与 numpy 行为对齐的数值工具（求和用 numpy 的 pairwise 算法，保证与 Python 版逐位一致）。</summary>
public static class BrMath
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool Nan(double v) => double.IsNaN(v) || double.IsInfinity(v);

    /// <summary>numpy pairwise_sum（float64, 连续数组）。</summary>
    public static double NpSum(ReadOnlySpan<double> a)
    {
        var n = a.Length;
        if (n < 8)
        {
            double res = 0.0;  // numpy: res = 0.; res += a[i]  （注：-0.0 细节忽略）
            for (var i = 0; i < n; i++) res += a[i];
            return res;
        }
        if (n <= 128)
        {
            double r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3], r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            var i = 8;
            for (; i < n - (n % 8); i += 8)
            {
                r0 += a[i]; r1 += a[i + 1]; r2 += a[i + 2]; r3 += a[i + 3];
                r4 += a[i + 4]; r5 += a[i + 5]; r6 += a[i + 6]; r7 += a[i + 7];
            }
            var res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++) res += a[i];
            return res;
        }
        var n2 = n / 2;
        n2 -= n2 % 8;
        return NpSum(a[..n2]) + NpSum(a[n2..]);
    }

    public static double NpMean(ReadOnlySpan<double> a) => a.Length == 0 ? double.NaN : NpSum(a) / a.Length;

    public static double NpMean(IReadOnlyList<double> a) => NpMean(a.ToArray());

    public static double NanMean(ReadOnlySpan<double> a)
    {
        var buf = new double[a.Length];
        var cnt = 0;
        for (var i = 0; i < a.Length; i++) { if (double.IsNaN(a[i])) buf[i] = 0; else { buf[i] = a[i]; cnt++; } }
        return cnt == 0 ? double.NaN : NpSum(buf) / cnt;
    }

    public static double Max(ReadOnlySpan<double> a) { var m = double.NegativeInfinity; foreach (var x in a) { if (double.IsNaN(x)) return double.NaN; if (x > m) m = x; } return m; }
    public static double Min(ReadOnlySpan<double> a) { var m = double.PositiveInfinity; foreach (var x in a) { if (double.IsNaN(x)) return double.NaN; if (x < m) m = x; } return m; }

    /// <summary>Python round(v, nd) 的近似（四舍六入五成双，十进制）；NaN/Inf → null。</summary>
    public static double? R(double v, int nd = 4)
    {
        if (Nan(v)) return null;
        return PyRound(v, nd);
    }

    // ── 与 Python 完全一致的数值格式：round(x, nd) / f"{x:.nf}" 按双精度的精确二进制值做“银行家舍入”，str(float) 用最短往返表示 ──
    private static (bool Neg, System.Numerics.BigInteger Q) ExactScaledRound(double v, int nd)
    {
        bool neg = v < 0 || (v == 0 && double.IsNegative(v));
        long bits = BitConverter.DoubleToInt64Bits(Math.Abs(v));
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else man |= 1L << 52;
        exp -= 1075;                                    // |v| = man * 2^exp
        var num = new System.Numerics.BigInteger(man) * System.Numerics.BigInteger.Pow(10, nd);
        var den = System.Numerics.BigInteger.One;
        if (exp >= 0) num <<= exp; else den <<= -exp;
        var q = System.Numerics.BigInteger.DivRem(num, den, out var rem);
        int cmp = (rem * 2).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven)) q += 1;
        return (neg, q);
    }

    private static string Digits(System.Numerics.BigInteger q, int nd)
    {
        var s = q.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (nd <= 0) return s;
        s = s.PadLeft(nd + 1, '0');
        return s[..^nd] + "." + s[^nd..];
    }

    /// <summary>Python round(float, nd)。</summary>
    public static double PyRound(double v, int nd)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || nd > 300) return v;
        var (neg, q) = ExactScaledRound(v, nd);
        var x = double.Parse(Digits(q, nd), System.Globalization.CultureInfo.InvariantCulture);
        return neg ? -x : x;
    }

    /// <summary>Python f"{v:.{nd}f}"。</summary>
    public static string PyFmt(double v, int nd)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        var (neg, q) = ExactScaledRound(v, nd);
        return (neg ? "-" : "") + Digits(q, nd);
    }

    /// <summary>Python f"{v:+.{nd}f}"。</summary>
    public static string PyFmtSigned(double v, int nd)
    {
        var s = PyFmt(v, nd);
        return s.StartsWith('-') ? s : "+" + s;
    }

    /// <summary>Python str(float) / repr(float)。</summary>
    public static string PyRepr(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        var s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        int e = s.IndexOfAny(['E', 'e']);
        if (e < 0) return s.Contains('.') ? s : s + ".0";
        var mant = s[..e]; int ex = int.Parse(s[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (ex >= -5 && ex < 16)
        {   // Python 在 1e-4 ≤ |x| < 1e16 用定点表示
            var neg = mant.StartsWith('-'); var m = mant.TrimStart('-').Replace(".", "");
            int pointPos = 1 + ex;                     // 小数点在 m 中的位置
            string body;
            if (pointPos <= 0) body = "0." + new string('0', -pointPos) + m;
            else if (pointPos >= m.Length) body = m + new string('0', pointPos - m.Length) + ".0";
            else body = m[..pointPos] + "." + m[pointPos..];
            if (ex == -5) goto sci;                   // 1e-05 级别 Python 仍是科学计数
            return (neg ? "-" : "") + body;
        }
        sci:
        return mant + "e" + (ex < 0 ? "-" : "+") + Math.Abs(ex).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static double? R(double? v, int nd = 4) => v is null ? null : R(v.Value, nd);

    public static string Pct1(double v) => PyFmtSigned(v * 100, 1) + "%";
}

/// <summary>技术指标（因果：第 i 根只依赖 ≤ i 的数据）。</summary>
public static class BrIndicators
{
    public static double[] Sma(double[] x, int n)
    {
        var o = Enumerable.Repeat(double.NaN, x.Length).ToArray();
        if (x.Length < n) return o;
        var c = new double[x.Length + 1];
        for (var i = 0; i < x.Length; i++) c[i + 1] = c[i] + x[i];   // numpy cumsum 顺序累加
        for (var i = n - 1; i < x.Length; i++) o[i] = (c[i + 1] - c[i + 1 - n]) / n;
        return o;
    }

    public static double[] PrevMean(double[] x, int n)
    {
        var s = Sma(x, n);
        var o = Enumerable.Repeat(double.NaN, x.Length).ToArray();
        for (var i = 1; i < x.Length; i++) o[i] = s[i - 1];
        return o;
    }

    public static double[] PrevMax(double[] x, int n)
    {
        var o = Enumerable.Repeat(double.NaN, x.Length).ToArray();
        for (var i = n; i < x.Length; i++) o[i] = BrMath.Max(x.AsSpan(i - n, n));
        return o;
    }

    public static double[] Ema(double[] x, int n)
    {
        var o = new double[x.Length];
        if (x.Length == 0) return o;
        var a = 2.0 / (n + 1);
        o[0] = x[0];
        for (var i = 1; i < x.Length; i++) o[i] = a * x[i] + (1 - a) * o[i - 1];
        return o;
    }

    public static (double[] Dif, double[] Dea, double[] Hist) Macd(double[] c, int fast, int slow, int signal)
    {
        var ef = Ema(c, fast); var es = Ema(c, slow);
        var dif = new double[c.Length];
        for (var i = 0; i < c.Length; i++) dif[i] = ef[i] - es[i];
        var dea = Ema(dif, signal);
        var h = new double[c.Length];
        for (var i = 0; i < c.Length; i++) h[i] = 2.0 * (dif[i] - dea[i]);
        return (dif, dea, h);
    }

    /// <summary>标准 Wilder RSI。</summary>
    public static double[] RsiWilder(double[] c, int n)
    {
        var o = Enumerable.Repeat(double.NaN, c.Length).ToArray();
        if (c.Length <= n) return o;
        var g = new double[c.Length - 1]; var l = new double[c.Length - 1];
        for (var i = 1; i < c.Length; i++) { var d = c[i] - c[i - 1]; g[i - 1] = d > 0 ? d : 0.0; l[i - 1] = d < 0 ? -d : 0.0; }
        var ag = BrMath.NpMean(g.AsSpan(0, n)); var al = BrMath.NpMean(l.AsSpan(0, n));
        o[n] = al == 0 ? 100.0 : 100 - 100 / (1 + ag / al);
        for (var i = n + 1; i < c.Length; i++)
        {
            ag = (ag * (n - 1) + g[i - 1]) / n;
            al = (al * (n - 1) + l[i - 1]) / n;
            o[i] = al == 0 ? 100.0 : 100 - 100 / (1 + ag / al);
        }
        return o;
    }
}

/// <summary>日K序列（东财字段）+ 预计算指标。</summary>
public sealed class BrSeries
{
    public string[] Date = [];
    public double[] Open = [], Close = [], High = [], Low = [], Volume = [], Amount = [], Turnover = [];
    public double[] Chg = [], Ma5 = [], Ma10 = [], Ma20 = [], Ma60 = [], VolPrevMean = [], ClosePrevMax = [], Rsi = [], Dif = [], Dea = [], Hist = [], TurnPrevMean = [];
    public int Count => Date.Length;

    public BrSeries Take(int n) => new()
    {
        Date = Date[..n], Open = Open[..n], Close = Close[..n], High = High[..n], Low = Low[..n],
        Volume = Volume[..n], Amount = Amount[..n], Turnover = Turnover[..n],
    };

    public BrSeries Prepare(BrConfig cfg)
    {
        var c = Close; var n = c.Length;
        Chg = Enumerable.Repeat(double.NaN, n).ToArray();
        for (var i = 1; i < n; i++) Chg[i] = c[i] / c[i - 1] - 1;
        Ma5 = BrIndicators.Sma(c, 5); Ma10 = BrIndicators.Sma(c, 10); Ma20 = BrIndicators.Sma(c, 20); Ma60 = BrIndicators.Sma(c, 60);
        VolPrevMean = BrIndicators.PrevMean(Volume, cfg.I("model.B1.avg_window", 20));
        ClosePrevMax = BrIndicators.PrevMax(c, cfg.I("model.breakout.prior_window", 20));
        Rsi = BrIndicators.RsiWilder(c, cfg.I("model.E1.period", 14));
        (Dif, Dea, Hist) = BrIndicators.Macd(c, cfg.I("model.E2.fast", 12), cfg.I("model.E2.slow", 26), cfg.I("model.E2.signal", 9));
        TurnPrevMean = BrIndicators.PrevMean(Turnover, 20);
        return this;
    }
}
