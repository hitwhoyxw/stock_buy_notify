using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.DataSources;

/// <summary>
/// 行情数据源抽象。所有数据源（新浪、腾讯、未来东方财富/网易等）都实现此接口。
/// 设计要点：
/// 1. <see cref="GetRealTimeQuotesAsync"/> 负责“网络请求 + 解析”，由基类统一处理 HTTP；
/// 2. <see cref="ParseRealTimeResponse"/> 只负责“把原始文本解析成行情”，由各数据源独立实现，
///    因此可完全脱离网络做单元测试（传入样本字符串即可）。
/// </summary>
public interface IMarketDataSource
{
    /// <summary>数据源标识。</summary>
    MarketDataSourceId Id { get; }

    /// <summary>展示名（中文）。</summary>
    string DisplayName { get; }

    /// <summary>批量获取实时行情。symbols 形如 sh600519 / sz000001。</summary>
    Task<IReadOnlyList<RealTimeQuote>> GetRealTimeQuotesAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default);

    /// <summary>将原始 HTTP 响应解析为行情列表（由各数据源独立实现）。</summary>
    IReadOnlyList<RealTimeQuote> ParseRealTimeResponse(string rawResponse);
}
