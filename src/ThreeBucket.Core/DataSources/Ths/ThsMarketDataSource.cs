using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.Core.DataSources.Ths;

/// <summary>
/// 同花顺（扶摇）实时行情数据源：IMarketDataSource 的 JSON REST 适配层。
/// <para>接口：https://fuyao.aicubes.cn/api/a-share/prices/snapshot（需 X-api-key 请求头）。</para>
/// <para>与腾讯/新浪的 GBK 文本接口不同，这里直接实现接口并委托 <see cref="ThsClient"/>，
/// 以便与免费数据源共用同一套聚合降级链（配置了 API Key 时为主源）。</para>
/// <para>注意：上游不返回股票名称（Name 为空），UI 名称由本地 CSV 数据兜底；
/// 成交量为股（腾讯/新浪为手），已换算保持 RealTimeQuote 口径一致。</para>
/// </summary>
public class ThsMarketDataSource : IMarketDataSource
{
    private readonly ThsClient _client;

    public ThsMarketDataSource(ThsClient? client = null) => _client = client ?? new ThsClient();

    public MarketDataSourceId Id => MarketDataSourceId.Ths;
    public string DisplayName => "同花顺(扶摇)";

    public async Task<IReadOnlyList<RealTimeQuote>> GetRealTimeQuotesAsync(
        IEnumerable<string> symbols, CancellationToken cancellationToken = default)
    {
        var items = await _client.GetQuotesSnapshotAsync(symbols, cancellationToken);
        return items.Select(ToQuote).ToList();
    }

    /// <summary>解析快照 JSON 的 data 节点（离线样本回归测试入口）。</summary>
    public IReadOnlyList<RealTimeQuote> ParseRealTimeResponse(string rawResponse)
    {
        using var doc = JsonDocument.Parse(rawResponse);
        return ThsClient.ParseSnapshotItems(doc.RootElement).Select(ToQuote).ToList();
    }

    // thscode（600519.SH）→ 标准化符号（sh600519），与腾讯/新浪链路同格式
    private static RealTimeQuote ToQuote(ThsQuoteItem it)
    {
        var pure = it.Ticker.Length == 6 ? it.Ticker : ThsClient.NormalizeCode(it.Thscode);
        var symbol = (pure.StartsWith("4") || pure.StartsWith("8") || pure.StartsWith("920") ? "bj"
            : pure[0] is '6' or '5' or '9' ? "sh" : "sz") + pure;
        return new RealTimeQuote
        {
            Symbol = symbol,
            Name = it.Name ?? "",
            Source = MarketDataSourceId.Ths,
            Open = (decimal)(it.Open ?? 0),
            PrevClose = (decimal)(it.Prev ?? 0),
            Current = (decimal)(it.Last ?? 0),
            High = (decimal)(it.High ?? 0),
            Low = (decimal)(it.Low ?? 0),
            VolumeLots = (long)Math.Round((it.Volume ?? 0) / 100.0), // 股 → 手
            Amount = (decimal)(it.Turnover ?? 0),
            Timestamp = it.TimestampMs is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime
                : null,
        };
    }
}
