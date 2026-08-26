using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 基于实时行情 + 日K + 持仓成本的带 offset 指标计算器
/// （Python IndicatorContext 的 C# 移植版，日K为升序 DailyBar 列表）。
///
/// 所有序列计算均带缓存（同一 ctx 内 MA/MACD 只算一次），
/// 供一只股票的多个策略复用；数据不足统一返回 null（三值逻辑的 None）。
/// </summary>
public sealed class IndicatorContext
{
    private readonly IReadOnlyList<DailyBar>? _kline;

    // 实时行情（腾讯口径：price/change_pct/pe_ttm；无则 null）
    public double? QuotePrice { get; }
    public double? QuoteChangePct { get; }
    public double? QuotePeTtm { get; }
    public double? Cost { get; }

    private double[]? _close, _volume, _high, _low;
    private readonly Dictionary<int, double[]> _maCache = new();
    private readonly Dictionary<int, double[]> _rsiCache = new();
    private readonly Dictionary<int, double[]> _kdjCache = new();
    private (double[] Dif, double[] Dea, double[] Hist)? _macd;

    public IndicatorContext(double? quotePrice, double? quoteChangePct,
        IReadOnlyList<DailyBar>? kline, double? cost = null,
        double? quotePeTtm = null)
    {
        QuotePrice = quotePrice is > 0 ? quotePrice : null;
        QuoteChangePct = quoteChangePct;
        QuotePeTtm = quotePeTtm;
        Cost = cost;
        _kline = kline is { Count: > 0 } ? kline : null;
    }

    // ── 序列（懒算 + 缓存；MA 前段不足为 NaN）──

    private double[]? Close()
    {
        if (_close is null && _kline is not null)
            _close = _kline.Select(b => b.Close).ToArray();
        return _close;
    }

    private double[]? Volume()
    {
        if (_volume is null && _kline is not null)
            _volume = _kline.Select(b => b.Volume).ToArray();
        return _volume;
    }

    private double[]? High()
    {
        if (_high is null && _kline is not null)
            _high = _kline.Select(b => b.High).ToArray();
        return _high;
    }

    private double[]? Low()
    {
        if (_low is null && _kline is not null)
            _low = _kline.Select(b => b.Low).ToArray();
        return _low;
    }

    private double[]? Ma(int period)
    {
        if (!_maCache.TryGetValue(period, out var ma))
        {
            var c = Close();
            if (c is null || c.Length < period)
            {
                _maCache[period] = ma = [];
            }
            else
            {
                ma = new double[c.Length];
                double sum = 0;
                for (var i = 0; i < c.Length; i++)
                {
                    sum += c[i];
                    if (i >= period) sum -= c[i - period];
                    ma[i] = i >= period - 1 ? sum / period : double.NaN;
                }
            }
            _maCache[period] = ma;
        }
        return ma.Length == 0 ? null : ma;
    }

    /// <summary>国内口径 MACD：DIF=EMA12-EMA26，DEA=EMA9(DIF)，HIST=2*(DIF-DEA)。
    /// 与 pandas ewm(span=N, adjust=False) 等价：alpha=2/(N+1)，首值=首样本。</summary>
    private (double[] Dif, double[] Dea, double[] Hist)? Macd()
    {
        if (_macd is null)
        {
            var c = Close();
            if (c is null || c.Length < 30)
                _macd = null;
            else
            {
                var ema12 = Ema(c, 12);
                var ema26 = Ema(c, 26);
                var dif = new double[c.Length];
                for (var i = 0; i < c.Length; i++) dif[i] = ema12[i] - ema26[i];
                var dea = Ema(dif, 9);
                var hist = new double[c.Length];
                for (var i = 0; i < c.Length; i++) hist[i] = (dif[i] - dea[i]) * 2;
                _macd = (dif, dea, hist);
            }
        }
        return _macd;
    }

    private static double[] Ema(double[] x, int span)
    {
        var alpha = 2.0 / (span + 1);
        var ema = new double[x.Length];
        ema[0] = x[0];
        for (var i = 1; i < x.Length; i++)
            ema[i] = ema[i - 1] + alpha * (x[i] - ema[i - 1]);
        return ema;
    }

    /// <summary>Wilder RSI：首值取前 period 个涨跌幅简单均值，其后递推平滑。
    /// 序列前 period 位为 NaN（数据不足）。</summary>
    private double[] RsiSeries(int period)
    {
        if (_rsiCache.TryGetValue(period, out var cached)) return cached;
        var c = Close();
        double[] rsi;
        if (c is null || c.Length < period + 1)
        {
            rsi = [];
        }
        else
        {
            rsi = new double[c.Length];
            for (var i = 0; i < period; i++) rsi[i] = double.NaN;
            double avgGain = 0, avgLoss = 0;
            for (var i = 1; i <= period; i++)
            {
                var d = c[i] - c[i - 1];
                avgGain += Math.Max(d, 0);
                avgLoss += Math.Max(-d, 0);
            }
            avgGain /= period;
            avgLoss /= period;
            rsi[period] = avgLoss <= 1e-12 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
            for (var i = period + 1; i < c.Length; i++)
            {
                var d = c[i] - c[i - 1];
                avgGain = (avgGain * (period - 1) + Math.Max(d, 0)) / period;
                avgLoss = (avgLoss * (period - 1) + Math.Max(-d, 0)) / period;
                rsi[i] = avgLoss <= 1e-12 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
            }
        }
        _rsiCache[period] = rsi;
        return rsi;
    }

    /// <summary>国内标准 KDJ(n,3,3) 的 J 值序列：J=3K-2D，K/D 初值 50。
    /// 前 n-1 位为 NaN。</summary>
    private double[] KdjJSeries(int n)
    {
        if (_kdjCache.TryGetValue(n, out var cached)) return cached;
        var c = Close();
        var hi = High();
        var lo = Low();
        double[] j;
        if (c is null || hi is null || lo is null || c.Length < n)
        {
            j = [];
        }
        else
        {
            j = new double[c.Length];
            for (var i = 0; i < n - 1; i++) j[i] = double.NaN;
            double k = 50, d = 50;
            for (var i = n - 1; i < c.Length; i++)
            {
                double hh = double.MinValue, ll = double.MaxValue;
                for (var t = i - n + 1; t <= i; t++)
                {
                    if (hi[t] > hh) hh = hi[t];
                    if (lo[t] < ll) ll = lo[t];
                }
                var rsv = hh > ll ? (c[i] - ll) / (hh - ll) * 100 : 50;
                k = k * 2.0 / 3 + rsv / 3;
                d = d * 2.0 / 3 + k / 3;
                j[i] = 3 * k - 2 * d;
            }
        }
        _kdjCache[n] = j;
        return j;
    }

    // ── 单值访问（offset: 0=今天, 1=昨天, …）──

    private static double? At(double[]? series, int offset)
    {
        if (series is null) return null;
        var i = series.Length - 1 - offset;
        if (i < 0) return null;
        var v = series[i];
        return double.IsNaN(v) ? null : v;
    }

    /// <summary>现价：offset=0 优先实时行情，否则取 K 线收盘。</summary>
    public double? GetPrice(int offset = 0)
    {
        if (offset == 0 && QuotePrice is { } p) return p;
        return At(Close(), offset);
    }

    public double? GetMa(int period, int offset = 0) => At(Ma(period), offset);

    /// <summary>乖离率：现价相对 MA(period) 的偏离 (%)。</summary>
    public double? GetBias(int period, int offset = 0)
    {
        var ma = GetMa(period, offset);
        var price = GetPrice(offset);
        if (ma is null || price is null || ma <= 0) return null;
        return (price / ma - 1) * 100;
    }

    /// <summary>均线发散强度：(MA_fast - MA_slow) / MA_slow (%)。</summary>
    public double? GetMaSpread(int fast = 5, int slow = 60, int offset = 0)
    {
        var maF = GetMa(fast, offset);
        var maS = GetMa(slow, offset);
        if (maF is null || maS is null || maS <= 0) return null;
        return (maF / maS - 1) * 100;
    }

    public double? GetMacd(string field, int offset = 0)
    {
        var m = Macd();
        if (m is null) return null;
        return field.ToLowerInvariant() switch
        {
            "dif" => At(m.Value.Dif, offset),
            "dea" => At(m.Value.Dea, offset),
            "hist" => At(m.Value.Hist, offset),
            _ => null,
        };
    }

    /// <summary>RSI（Wilder）。超买 >= 70 / 超卖 <= 30。</summary>
    public double? GetRsi(int period = 14, int offset = 0) => At(RsiSeries(period), offset);

    /// <summary>KDJ 的 J 值。超买 >= 100 / 超卖 <= 0。</summary>
    public double? GetKdjJ(int n = 9, int offset = 0) => At(KdjJSeries(n), offset);

    /// <summary>DIF 距 window 日高点差 (%)：负值= DIF 未随价格创新高（顶背离证据）。
    /// 分母取 |窗口高点|，过小（DIF 贴零）时返回 null。</summary>
    public double? GetDifHhvGap(int window = 60, int offset = 0)
        => DifWindowGap(window, offset, upper: true);

    /// <summary>DIF 距 window 日低点差 (%)：正值= DIF 未随价格创新低（底背离证据）。</summary>
    public double? GetDifLlvGap(int window = 60, int offset = 0)
        => DifWindowGap(window, offset, upper: false);

    private double? DifWindowGap(int window, int offset, bool upper)
    {
        var m = Macd();
        if (m is null) return null;
        var dif = m.Value.Dif;
        var end = dif.Length - offset; // 当日位于 end-1
        if (end < window) return null;
        var start = end - window;
        double extreme = upper ? double.MinValue : double.MaxValue;
        for (var i = start; i < end; i++)
            extreme = upper ? Math.Max(extreme, dif[i]) : Math.Min(extreme, dif[i]);
        if (Math.Abs(extreme) < 1e-6) return null;
        return (dif[end - 1] - extreme) / Math.Abs(extreme) * 100;
    }

    /// <summary>量比 = 当日量 / 前 window 日均量（分母不含当日）。</summary>
    public double? GetVolumeRatio(int window = 20, int offset = 0)
    {
        var v = Volume();
        if (v is null) return null;
        var end = v.Length - offset; // 当日位于 end-1
        if (end < window + 1) return null;
        var cur = v[end - 1];
        double sum = 0;
        for (var i = end - 1 - window; i <= end - 2; i++) sum += v[i];
        var avg = sum / window;
        if (avg <= 0) return null;
        return cur / avg;
    }

    public double? GetVolume(int offset = 0) => At(Volume(), offset);

    /// <summary>日涨跌幅(%)（K 线口径：close / 前收 - 1）。</summary>
    public double? GetPctChange(int offset = 0)
    {
        var c = Close();
        if (c is null || c.Length < offset + 2) return null;
        var i = c.Length - 1 - offset;
        var prev = c[i - 1];
        if (prev <= 0) return null;
        return (c[i] / prev - 1) * 100;
    }

    /// <summary>当日涨跌幅：offset=0 用实时行情，历史日回落 K 线口径。</summary>
    public double? GetDayChange(int offset = 0)
        => offset == 0 ? QuoteChangePct : GetPctChange(offset);

    /// <summary>距 window 日高点回撤 (%)。</summary>
    public double? GetDrawdownFromHigh(int window = 180, int offset = 0)
    {
        var hi = High();
        if (hi is null) return null;
        var end = hi.Length - offset;
        var start = Math.Max(0, end - window);
        if (end <= start) return null;
        var peak = double.MinValue;
        for (var i = start; i < end; i++) peak = Math.Max(peak, hi[i]);
        var price = GetPrice(offset);
        if (peak <= 0 || price is null) return null;
        return (price / peak - 1) * 100;
    }

    /// <summary>距 window 日低点涨幅 (%)。</summary>
    public double? GetGainFromLow(int window = 180, int offset = 0)
    {
        var lo = Low();
        if (lo is null) return null;
        var end = lo.Length - offset;
        var start = Math.Max(0, end - window);
        if (end <= start) return null;
        var trough = double.MaxValue;
        for (var i = start; i < end; i++) trough = Math.Min(trough, lo[i]);
        var price = GetPrice(offset);
        if (trough <= 0 || price is null) return null;
        return (price / trough - 1) * 100;
    }

    /// <summary>持仓浮盈 (%)：无成本或无现价返回 null。</summary>
    public double? GetCostBasisGain()
    {
        if (Cost is not > 0) return null;
        var price = GetPrice(0);
        if (price is null) return null;
        return (price / Cost.Value - 1) * 100;
    }
}
