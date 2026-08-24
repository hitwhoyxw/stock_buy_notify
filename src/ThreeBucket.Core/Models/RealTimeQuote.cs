using System;

namespace ThreeBucket.Core.Models;

/// <summary>
/// 实时行情快照。字段尽量对齐新浪/腾讯两家共同能稳定提供的部分；
/// 各数据源只填充自己能解析的字段，无法获取的字段保持默认值（0 / null）。
/// </summary>
public class RealTimeQuote
{
    /// <summary>标准化代码，如 sh600519。</summary>
    public string Symbol { get; set; } = "";

    /// <summary>证券名称。</summary>
    public string Name { get; set; } = "";

    public decimal Open { get; set; }
    public decimal PrevClose { get; set; }
    public decimal Current { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }

    /// <summary>成交量（手）。新浪/腾讯均返回“手”，未换算成股。</summary>
    public long VolumeLots { get; set; }

    /// <summary>成交额（元）。腾讯基础行情无固定位置，可能为 0。</summary>
    public decimal Amount { get; set; }

    public decimal? Bid1 { get; set; }
    public decimal? Ask1 { get; set; }

    /// <summary>行情时间戳（交易所时间）。解析失败时为 null。</summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>本条行情来自哪个数据源。</summary>
    public MarketDataSourceId Source { get; set; }
}
