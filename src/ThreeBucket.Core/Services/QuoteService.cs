using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThreeBucket.Core.DataSources;
using ThreeBucket.Core.DataSources.Sina;
using ThreeBucket.Core.DataSources.Tencent;
using ThreeBucket.Core.DataSources.Ths;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>单只股票的实时行情摘要（UI 展示 + 名称补全 + 盈亏计算用）。</summary>
public record QuoteInfo(string Name, decimal Price, decimal PrevClose)
{
    /// <summary>市盈率 TTM（数据源未提供时为 null，pe_ttm 类监控策略将跳过）。</summary>
    public double? PeTtm { get; init; }

    /// <summary>当日涨跌幅 %（昨收无效时为 0）。</summary>
    public double ChangePct =>
        PrevClose > 0 ? (double)((Price - PrevClose) / PrevClose * 100m) : 0;
}

/// <summary>行情服务：多数据源聚合拉取 name / 现价 / 昨收 / PE(TTM)，供 UI 算盈亏与监控评估。</summary>
public class QuoteService
{
    private readonly IMarketDataSource _source;
    private readonly ThsClient? _ths;

    /// <param name="ths">同花顺（扶摇）客户端；配置了 API Key 时作为主源，其后降级腾讯 → 新浪。</param>
    public QuoteService(ThsClient? ths = null)
    {
        _ths = ths;
        ths ??= new ThsClient();
        var chain = new List<IMarketDataSource>();
        if (ths.IsConfigured)
            chain.Add(new ThsMarketDataSource(ths)); // 同花顺无名称字段，UI 名称由本地 CSV 兜底
        chain.Add(new TencentMarketDataSource());
        chain.Add(new SinaMarketDataSource());
        _source = new AggregatedMarketDataSource(chain);
    }

    /// <summary>把 6 位代码转成接口符号（sh/sz/bj 前缀）。</summary>
    public static string ToSymbol(string code)
    {
        var pure = code.Split('.')[0].Trim().PadLeft(6, '0');
        if (pure.StartsWith("6") || pure.StartsWith("5") || pure.StartsWith("9"))
            return "sh" + pure;
        if (pure.StartsWith("4") || pure.StartsWith("8"))
            return "bj" + pure;
        return "sz" + pure;
    }

    /// <summary>
    /// 拉取一组代码的实时行情，返回 6 位纯代码 -> QuoteInfo（名称/现价/昨收/PE）。
    /// 主源无 PE 字段（同花顺）或未覆盖的代码，用腾讯快照 f[39] 兜底补 PE。
    /// 网络失败返回空字典，不抛异常。
    /// </summary>
    public async Task<Dictionary<string, QuoteInfo>> FetchAsync(
        IEnumerable<string> codes, CancellationToken cancellationToken = default)
    {
        var list = codes.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
        if (list.Count == 0) return new();

        var symbols = list.Select(ToSymbol).ToList();
        IReadOnlyList<RealTimeQuote> quotes;
        try
        {
            quotes = await _source.GetRealTimeQuotesAsync(symbols, cancellationToken);
        }
        catch
        {
            return new();
        }

        var result = new Dictionary<string, QuoteInfo>();
        var needPe = new List<string>();
        foreach (var q in quotes)
        {
            var pure = q.Symbol.Length > 2 ? q.Symbol[2..] : q.Symbol;
            var pe = q.PeTtm is { } v ? (double?)v : null;
            result[pure] = new QuoteInfo(q.Name, q.Current, q.PrevClose) { PeTtm = pe };
            if (pe is null) needPe.Add(pure);
        }

        // PE 兜底：主源（如同花顺）不带 PE 时，向腾讯快照批量补齐（失败静默，PE 留空）
        if (needPe.Count > 0)
        {
            try
            {
                var fund = await new TencentSnapshot().GetBatchAsync(needPe, ct: cancellationToken);
                foreach (var (code, f) in fund)
                    if (f.PeTtm is { } v && result.TryGetValue(code, out var info))
                        result[code] = info with { PeTtm = v };
            }
            catch { /* PE 兜底失败不影响行情主流程 */ }
        }
        return result;
    }
}
