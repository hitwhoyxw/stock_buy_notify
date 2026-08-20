using System.Collections.Generic;
using System.Text.RegularExpressions;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.DataSources.Sina;

/// <summary>
/// 新浪财经实时行情数据源。
/// <para>接口：https://hq.sinajs.cn/list=sh600519,sz000001</para>
/// <para>返回 GBK 编码；字段以逗号分隔。需带 Referer 请求头，否则返回 403。</para>
/// </summary>
/// <remarks>
/// 新浪字段（逗号分隔，下标从 0 开始）：
/// 0 名称, 1 今开, 2 昨收, 3 当前价, 4 最高, 5 最低, 6 竞买价, 7 竞卖价,
/// 8 成交量(手), 9 成交额(元), 10/11 买一量/价, 12/13 买二..., 20/21 卖一量/价 ...,
/// 30 日期, 31 时间, 32 状态。
/// </remarks>
public class SinaMarketDataSource : HttpMarketDataSourceBase
{
    public override MarketDataSourceId Id => MarketDataSourceId.Sina;
    public override string DisplayName => "新浪财经";
    protected override string Endpoint => "https://hq.sinajs.cn/list=";

    protected override IEnumerable<KeyValuePair<string, string>>? ExtraHeaders =>
        new[] { new KeyValuePair<string, string>("Referer", "https://finance.sina.com.cn") };

    private static readonly Regex LineRegex =
        new(@"var\s+hq_str_(?<sym>[a-zA-Z0-9]+)\s*=\s*""(?<data>[^""]*)""\s*;", RegexOptions.Compiled);

    public override IReadOnlyList<RealTimeQuote> ParseRealTimeResponse(string raw)
    {
        var result = new List<RealTimeQuote>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (Match m in LineRegex.Matches(raw))
        {
            var sym = m.Groups["sym"].Value.ToLowerInvariant();
            var data = m.Groups["data"].Value;
            if (string.IsNullOrEmpty(data))
                continue;

            var p = data.Split(',');
            if (p.Length < 6)
                continue;

            var q = new RealTimeQuote { Symbol = sym, Source = Id, Name = p[0] };
            TryDecimal(p, 1, v => q.Open = v);
            TryDecimal(p, 2, v => q.PrevClose = v);
            TryDecimal(p, 3, v => q.Current = v);
            TryDecimal(p, 4, v => q.High = v);
            TryDecimal(p, 5, v => q.Low = v);
            TryLong(p, 8, v => q.VolumeLots = v);
            TryDecimal(p, 9, v => q.Amount = v);
            TryDecimal(p, 11, v => q.Bid1 = v);
            TryDecimal(p, 21, v => q.Ask1 = v);
            if (p.Length > 31)
                TryTimestamp(p[30], p[31], t => q.Timestamp = t);

            result.Add(q);
        }

        return result;
    }
}
