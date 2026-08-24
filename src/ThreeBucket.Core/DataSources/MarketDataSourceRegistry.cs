using System.Collections.Generic;
using System.Linq;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.DataSources;

/// <summary>
/// 数据源注册表：集中管理所有已接入的数据源，按 Id 检索。
/// 新增一个数据源 = 新建一个 IMarketDataSource 实现 + 在此注册一行。
/// </summary>
public class MarketDataSourceRegistry
{
    private readonly List<IMarketDataSource> _sources = new();

    public void Register(IMarketDataSource source) => _sources.Add(source);

    public IReadOnlyList<IMarketDataSource> All => _sources;

    public IMarketDataSource? Get(MarketDataSourceId id) =>
        _sources.FirstOrDefault(s => s.Id == id);
}
