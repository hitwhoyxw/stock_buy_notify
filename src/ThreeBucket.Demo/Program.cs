using System;
using System.Linq;
using ThreeBucket.Core;
using ThreeBucket.Core.DataSources;
using ThreeBucket.Core.DataSources.Sina;
using ThreeBucket.Core.DataSources.Tencent;
using ThreeBucket.Core.Models;

// 离线样本（格式取自真实接口；此处用 UTF-8 字面量表示 GBK 原文内容，仅用于验证解析逻辑）
const string sinaSample =
    "var hq_str_sh600519=\"贵州茅台,1684.000,1680.000,1690.000,1695.000,1682.000,1690.000,1690.000," +
    "100,169000000,10,1690.000,20,1689.000,30,1688.000,40,1687.000,50,1686.000,60,1685.000," +
    "10,1690.000,20,1691.000,30,1692.000,40,1693.000,2026-08-20,15:00:00,00\";\n";

const string tencentSample =
    "v_sh600519=\"1~贵州茅台~600519~1690.500~1680.000~1692.000~100~50~50~1695.000~1682.000~" +
    "1690.000~1691.000~10~1690.000~20~1689.000~30~1688.000~40~1687.000~50~1686.000~2026-08-20~15:00:00\";\n";

Console.WriteLine("=== 三桶 · 数据源解析离线验证 ===\n");

var sina = new SinaMarketDataSource();
var tencent = new TencentMarketDataSource();

Print("新浪", sina.ParseRealTimeResponse(sinaSample));
Print("腾讯", tencent.ParseRealTimeResponse(tencentSample));

Console.WriteLine("\n=== 多数据源注册表 ===");
var registry = MarketData.DefaultRegistry();
Console.WriteLine("已注册: " + string.Join(", ", registry.All.Select(s => s.DisplayName)));

Console.WriteLine("\n=== 聚合层（主源优先 + 自动回退）===");
var aggregated = MarketData.DefaultAggregated();
Console.WriteLine($"聚合层包含 {aggregated.DisplayName}，可自动在主源故障时回退。");

Console.WriteLine("\n解析验证完成（未发起真实网络请求）。");

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
