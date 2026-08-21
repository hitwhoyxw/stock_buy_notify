using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThreeBucket.Core.DataSources;
using ThreeBucket.Core.DataSources.Sina;
using ThreeBucket.Core.DataSources.Tencent;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>单只股票的实时行情摘要（UI 展示 + 名称补全 + 盈亏计算用）。</summary>
public record QuoteInfo(string Name, decimal Price, decimal PrevClose)
{
    /// <summary>当日涨跌幅 %（昨收无效时为 0）。</summary>
    public double ChangePct =>
        PrevClose > 0 ? (double)((Price - PrevClose) / PrevClose * 100m) : 0;
}

/// <summary>行情服务：多数据源聚合拉取 name / 现价 / 昨收，供 UI 算盈亏与涨跌幅。</summary>
public class QuoteService
{
    private readonly IMarketDataSource _source;

    public QuoteService()
    {
        // 主源腾讯，回退新浪（各自独立解析）
        _source = new AggregatedMarketDataSource(new IMarketDataSource[]
        {
            new TencentMarketDataSource(),
            new SinaMarketDataSource(),
        });
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
    /// 拉取一组代码的实时行情，返回 6 位纯代码 -> QuoteInfo（名称/现价/昨收）。
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
        foreach (var q in quotes)
        {
            var pure = q.Symbol.Length > 2 ? q.Symbol[2..] : q.Symbol;
            result[pure] = new QuoteInfo(q.Name, q.Current, q.PrevClose);
        }
        return result;
    }
}
