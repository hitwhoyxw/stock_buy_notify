using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.DataSources;

/// <summary>
/// 多数据源聚合层：按注册顺序依次尝试各数据源，主源失败自动回退到下一个。
/// 这样“多数据源”不只是“能切换”，而是“高可用”——任一家接口限流/故障不影响取数。
/// </summary>
public class AggregatedMarketDataSource : IMarketDataSource
{
    private readonly IReadOnlyList<IMarketDataSource> _ordered;

    public AggregatedMarketDataSource(IEnumerable<IMarketDataSource> ordered)
    {
        _ordered = ordered?.ToList() ?? new List<IMarketDataSource>();
    }

    // 聚合层本身没有单一身份
    public MarketDataSourceId Id => MarketDataSourceId.Unknown;
    public string DisplayName => "聚合(多源)";

    public IReadOnlyList<RealTimeQuote> ParseRealTimeResponse(string raw) =>
        throw new NotSupportedException("聚合层不支持直接解析，请调用 GetRealTimeQuotesAsync。");

    public async Task<IReadOnlyList<RealTimeQuote>> GetRealTimeQuotesAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default)
    {
        foreach (var source in _ordered)
        {
            try
            {
                return await source.GetRealTimeQuotesAsync(symbols, cancellationToken);
            }
            catch
            {
                // 该源失败，回退到下一个
            }
        }

        return Array.Empty<RealTimeQuote>();
    }
}
