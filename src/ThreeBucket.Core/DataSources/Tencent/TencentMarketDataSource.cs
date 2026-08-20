using System.Collections.Generic;
using System.Text.RegularExpressions;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.DataSources.Tencent;

/// <summary>
/// 腾讯财经实时行情数据源。
/// <para>接口：https://qt.gtimg.cn/q=sh600519,sz000001</para>
/// <para>返回 GBK 编码；字段以 ~ 分隔。</para>
/// </summary>
/// <remarks>
/// 腾讯字段（波浪号分隔，下标从 0 开始）：
/// 0 未知, 1 名称, 2 代码, 3 当前价, 4 昨收, 5 今开, 6 成交量(手), 7 外盘, 8 内盘,
/// 9 最高, 10 最低, 11 买一价, 12 卖一价, 13~20 买二~卖五, ... 末尾两字段为 日期/时间。
/// 注：腾讯基础行情对“成交额”无固定字段位置，此处不解析（Amount 保持 0）。
/// </remarks>
public class TencentMarketDataSource : HttpMarketDataSourceBase
{
    public override MarketDataSourceId Id => MarketDataSourceId.Tencent;
    public override string DisplayName => "腾讯财经";
    protected override string Endpoint => "https://qt.gtimg.cn/q=";

    private static readonly Regex LineRegex =
        new(@"v_(?<sym>[a-zA-Z0-9]+)\s*=\s*""(?<data>[^""]*)""\s*;", RegexOptions.Compiled);

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

            var p = data.Split('~');
            if (p.Length < 13)
                continue;

            var q = new RealTimeQuote { Symbol = sym, Source = Id, Name = p[1] };
            TryDecimal(p, 3, v => q.Current = v);
            TryDecimal(p, 4, v => q.PrevClose = v);
            TryDecimal(p, 5, v => q.Open = v);
            TryLong(p, 6, v => q.VolumeLots = v);
            TryDecimal(p, 9, v => q.High = v);
            TryDecimal(p, 10, v => q.Low = v);
            TryDecimal(p, 11, v => q.Bid1 = v);
            TryDecimal(p, 12, v => q.Ask1 = v);
            if (p.Length >= 2)
                TryTimestamp(p[p.Length - 2], p[p.Length - 1], t => q.Timestamp = t);

            result.Add(q);
        }

        return result;
    }
}
