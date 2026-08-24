using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThreeBucket.Core;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.DataSources;
using ThreeBucket.Core.DataSources.Sina;
using ThreeBucket.Core.DataSources.Tencent;
using ThreeBucket.Core.DataSources.Ths;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

// ── 第一部分：真实网络连通性验证（腾讯 / 新浪 / 同花顺）──
Console.WriteLine("=== 三桶 · 数据源真实联网验证 ===\n");

var symbols = new[] { "sh600519", "sz000001", "sz000833" };

foreach (var (tag, source) in new (string, IMarketDataSource)[]
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

// 同花顺（扶摇）：需 API Key（环境变量 THS_API_KEY 或 app_config.json 的 ThsApiKey），未配置时跳过
var thsDemo = new ThsClient();
if (thsDemo.IsConfigured)
{
    try
    {
        var quotes = await new ThsMarketDataSource(thsDemo).GetRealTimeQuotesAsync(symbols);
        foreach (var q in quotes)
            Console.WriteLine($"[同花顺] {q.Name}({q.Symbol}) 现价={q.Current} 昨收={q.PrevClose} 时间={q.Timestamp:MM-dd HH:mm}");
        if (quotes.Count == 0)
            Console.WriteLine("[同花顺] 请求成功但解析出 0 条（响应格式可能变化）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[同花顺] 失败（降级链将自动回退腾讯/新浪）: {ex.Message}");
    }
}
else
{
    Console.WriteLine("[同花顺] 未配置 THS_API_KEY，跳过（配置后作为行情/日K/成分股/分红主源）");
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

// ── 第三部分：T1–T8 任务实例化验证 ──
Console.WriteLine("\n=== T1–T8 任务实例化验证 ===");

// 定位项目根（含 scripts/ 的目录）
var rootDir = AppContext.BaseDirectory;
for (var i = 0; i < 10; i++)
{
    if (Directory.Exists(Path.Combine(rootDir, "scripts"))) break;
    var parent = Path.GetDirectoryName(rootDir);
    if (parent == null || parent == rootDir) break;
    rootDir = parent;
}
var dataDir = Path.Combine(rootDir, "data");
var cacheDir = Path.Combine(dataDir, "cache");
Console.WriteLine($"项目根: {rootDir}");
Console.WriteLine($"数据目录: {dataDir}");

var store = new DataStore(dataDir);
var ths = new ThsClient(cacheDir);
var quoteSvc = new QuoteService(ths);
var klines = new KlineService(ths);
var calendar = new TradingCalendar(dataDir, klines);
var signals = new SignalLogStore(dataDir);
var em = new EastMoneyClient(cacheDir, ths);
var csi = new CsIndexClient(cacheDir, ths);
var tencent = new TencentSnapshot();

IBuiltinTask[] tasks =
{
    new DailyRiskTask(store, quoteSvc, klines, calendar, signals, csi, em),
    new WeeklyDividendTask(dataDir, store, klines, csi, em, signals),
    new MonthlyRebalanceTask(dataDir, store, signals),
    new EarningsScanTask(dataDir, em, signals),
    new AttributionPrepTask(dataDir, store, signals, klines),
    new CandidatePoolTask(dataDir, csi, em, tencent, klines),
    new BacktestTask(dataDir, em, klines),
    new SignalLogTask(dataDir, signals, klines, calendar),
};

Console.WriteLine($"成功实例化 {tasks.Length} 个任务：");
foreach (var t in tasks)
    Console.WriteLine($"  {t.Key} → {t.Name}");
Console.WriteLine("\n所有任务实例化验证通过。");

// ── 第四部分：T3 端到端实跑验证（最轻量任务，纯本地文件） ──
Console.WriteLine("\n=== T3 月度再平衡 · 端到端实跑 ===");
var t3 = tasks.First(t => t.Key == "T3");
var t3Result = await t3.RunAsync(msg => Console.WriteLine(msg));
Console.WriteLine($"T3 结果: Ok={t3Result.Ok}, 报告={t3Result.ReportPath}, 摘要={t3Result.Summary}");
