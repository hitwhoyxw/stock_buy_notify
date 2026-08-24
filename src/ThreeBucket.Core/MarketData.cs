using ThreeBucket.Core.DataSources;
using ThreeBucket.Core.DataSources.Sina;
using ThreeBucket.Core.DataSources.Tencent;

namespace ThreeBucket.Core;

/// <summary>
/// 便捷入口：返回默认装配好的数据源注册表（新浪 + 腾讯）。
/// UI / 测试 / 服务都从这里拿数据源，无需关心具体实现。
/// </summary>
public static class MarketData
{
    public static MarketDataSourceRegistry DefaultRegistry()
    {
        var registry = new MarketDataSourceRegistry();
        registry.Register(new SinaMarketDataSource());
        registry.Register(new TencentMarketDataSource());
        return registry;
    }

    /// <summary>返回一个“主源优先、自动回退”的聚合数据源。</summary>
    public static AggregatedMarketDataSource DefaultAggregated()
    {
        return new AggregatedMarketDataSource(DefaultRegistry().All);
    }
}
