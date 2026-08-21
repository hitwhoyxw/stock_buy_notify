using System;
using System.Linq;
using System.Threading.Tasks;
using ThreeBucket.Core;
using ThreeBucket.Core.DataSources;
using ThreeBucket.Core.DataSources.Sina;
using ThreeBucket.Core.DataSources.Tencent;
using ThreeBucket.Core.Models;

// ── 第一部分：真实网络连通性验证（腾讯 / 新浪）──
Console.WriteLine("=== 三桶 · 数据源真实联网验证 ===\n");

var symbols = new[] { "sh600519", "sz000001", "sz000833" };

foreach (var (tag, source) in new (string, HttpMarketDataSourceBase)[]
         {
             ("腾讯", new TencentMarketDataSource()),
             ("新浪", new SinaMarketDataSource()),
         })
{
    try
    {
        var quotes = await source.GetRealTimeQuotesAsync(symbols);
        foreach (var q in quotes)
            Console.WriteLine($"[{tag}] {q.Name}({q.Symbol}) 现价={q.Current} 昨收={q.PrevClose} 时间={q.Timestamp:MM-dd HH:mm}");
        if (quotes.Count == 0)
            Console.WriteLine($"[{tag}] 请求成功但解析出 0 条（响应格式可能变化）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{tag}] 失败: {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
            Console.WriteLine($"        内因: {ex.InnerException.Message}");
    }
}

// ── 第二部分：离线样本解析回归（防止改坏解析逻辑）──
Console.WriteLine("\n=== 离线解析回归 ===");

const string sinaSample =
    "var hq_str_sh600519=\"贵州茅台,1684.000,1680.000,1690.000,1695.000,1682.000,1690.000,1690.000," +
    "100,169000000,10,1690.000,20,1689.000,30,1688.000,40,1687.000,50,1686.000,60,1685.000," +
    "10,1690.000,20,1691.000,30,1692.000,40,1693.000,2026-08-20,15:00:00,00\";\n";

const string tencentSample =
    "v_sh600519=\"1~贵州茅台~600519~1690.500~1680.000~1692.000~100~50~50~1695.000~1682.000~" +
    "1690.000~1691.000~10~1690.000~20~1689.000~30~1688.000~40~1687.000~50~1686.000~2026-08-20~15:00:00\";\n";

Print("新浪", new SinaMarketDataSource().ParseRealTimeResponse(sinaSample));
Print("腾讯", new TencentMarketDataSource().ParseRealTimeResponse(tencentSample));

Console.WriteLine("\n=== 多数据源注册表 ===");
var registry = MarketData.DefaultRegistry();
Console.WriteLine("已注册: " + string.Join(", ", registry.All.Select(s => s.DisplayName)));

var aggregated = MarketData.DefaultAggregated();
Console.WriteLine($"聚合层: {aggregated.DisplayName}（主源故障自动回退）");

static void Print(string tag, System.Collections.Generic.IReadOnlyList<RealTimeQuote> quotes)
{
    foreach (var q in quotes)
    {
        var ts = q.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(无时间)";
        Console.WriteLine(
            $"[{tag}] {q.Name}({q.Symbol}) 现价={q.Current} 开={q.Open} 高={q.High} " +
            $"低={q.Low} 昨收={q.PrevClose} 量(手)={q.VolumeLots} 买一={q.Bid1} 卖一={q.Ask1} 时间={ts}");
    }
}
