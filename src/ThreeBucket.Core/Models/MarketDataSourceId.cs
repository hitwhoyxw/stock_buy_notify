namespace ThreeBucket.Core.Models;

/// <summary>
/// 数据源标识。新增数据源时在此追加枚举值（如 EastMoney / NetEase ...）。
/// 各数据源类通过 <see cref="IMarketDataSource.Id"/> 暴露自身标识。
/// </summary>
public enum MarketDataSourceId
{
    Unknown = 0,
    Sina = 1,
    Tencent = 2,
    // 预留：EastMoney = 3, NetEase = 4, SinaFinance = 5, ...
    Ths = 6, // 同花顺（扶摇，需 API Key）
}
