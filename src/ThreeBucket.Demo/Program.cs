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
using ThreeBucket.Demo;

// ── 第零部分：策略引擎离线回归（--engine 只跑离线回归后退出，无网络依赖）──
if (args.Contains("--toast")) { ToastDiagnostic.Run(); return 0; }

// --kb [查询词]：打印投资经验知识库的检索结果与 LLM 上下文（人工核对用，不联网）
if (args.Contains("--kb"))
{
    var q = args.SkipWhile(a => a != "--kb").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "";
    var kb = InvestmentKnowledgeBase.LoadDefault(AppContext.BaseDirectory);
    Console.WriteLine($"知识库: IsLoaded={kb.IsLoaded} 条目={kb.Entries.Count()} 来源={kb.SourcePath}");
    Console.WriteLine("\n--- 检索命中 ---");
    foreach (var e in kb.Retrieve(q, 5))
        Console.WriteLine($"  {e.Id}  {e.Title}  [{string.Join(" ", e.Tags)}]");
    Console.WriteLine("\n--- LLM 上下文 ---");
    Console.WriteLine(kb.BuildLlmContext(q, max: 3));
    return kb.IsLoaded ? 0 : 1;
}

// --disc <代码> [buy|add|sell|reduce]：实跑一次纪律审核的事实采集（会联网，人工核对用）
if (args.Contains("--disc"))
{
    var rest = args.SkipWhile(a => a != "--disc").Skip(1).ToList();
    var code = rest.FirstOrDefault(a => !a.StartsWith("--")) ?? "600519";
    var actText = rest.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "add";
    var act = TradeActionText.Parse(actText) ?? TradeAction.Add;

    var kb = InvestmentKnowledgeBase.LoadDefault(AppContext.BaseDirectory);
    var set = DisciplineRuleSet.FromKnowledgeBase(kb);
    var rules = set.ForAction(act);
    Console.WriteLine($"纪律: {set.Rules.Count} 条，操作「{act.ToCn()}」适用 {rules.Count} 条");
    foreach (var r in rules)
        Console.WriteLine($"  {r.Id} [{r.KindText}] {r.Title}（问题 {r.Questions.Count} 个）");

    var discThs = new ThsClient();
    var discKlines = new KlineService(discThs);
    var collector = new DisciplineFactsCollector(new QuoteService(discThs), discKlines,
        new TradingCalendar(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"), discKlines));
    var facts = await collector.CollectAsync(code);
    Console.WriteLine("\n--- 客观事实 ---");
    Console.WriteLine(facts.ToMarkdown());
    Console.WriteLine("\n--- 本地硬条件 ---");
    foreach (var c in DisciplineReviewEngine.RunLocalChecks(facts, act))
        Console.WriteLine($"  [{c.RuleId}] {c.VerdictText}（决定性={c.Conclusive}）{c.Text}");

    // LLM 配置：优先环境变量，其次仓库根 app_config.json（与桌面端同一份配置）
    var cfg = new DataStore(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data")).LoadConfig();
    var discLlm = new LlmClient(
        Environment.GetEnvironmentVariable("LLM_API_URL") is { Length: > 0 } u ? u : cfg.LlmApiUrl,
        Environment.GetEnvironmentVariable("LLM_API_KEY") is { Length: > 0 } k ? k : cfg.LlmApiKey,
        Environment.GetEnvironmentVariable("LLM_MODEL") is { Length: > 0 } m ? m : cfg.LlmModel);
    Console.WriteLine($"\nLLM 已配置: {discLlm.IsConfigured}（url={cfg.LlmApiUrl} model={cfg.LlmModel}）");

    var prompt = DisciplineReviewEngine.BuildQuestionPrompt(facts, act, "", rules);
    if (!discLlm.IsConfigured || !args.Contains("--live"))
    {
        Console.WriteLine("\n--- 待确认问题提示词（前 1200 字；加 --live 实际调用）---");
        Console.WriteLine(prompt.Length <= 1200 ? prompt : prompt[..1200] + "…");
        return 0;
    }

    Console.WriteLine("\n--- ① 实际调用：生成追问 ---");
    var (okQ, questionText) = await discLlm.ChatAsync(prompt);
    Console.WriteLine($"ok={okQ}");
    Console.WriteLine(questionText.Length <= 1500 ? questionText : questionText[..1500] + "…");

    Console.WriteLine("\n--- ② 实际调用：出结论（用户回答设为「无重大利空」）---");
    var verdictPrompt = DisciplineReviewEngine.BuildVerdictPrompt(facts, act, "1000股",
        rules, "1. 该股无重大利空，仅跟随板块回调。\n2. 大盘无系统性风险。\n3. 资金来自弹药库，止损 -3%。",
        DisciplineReviewEngine.RunLocalChecks(facts, act));
    var (okV, verdictText) = await discLlm.ChatAsync(verdictPrompt);
    Console.WriteLine($"ok={okV}");
    Console.WriteLine(verdictText.Length <= 1500 ? verdictText : verdictText[..1500] + "…");

    var review = DisciplineReviewEngine.ParseReview(verdictText, facts, act, "1000股", rules);
    Console.WriteLine("\n--- ③ 解析结果 ---");
    if (review is null) Console.WriteLine("❌ 解析失败（会走降级，不会乐观放行）");
    else
    {
        Console.WriteLine($"结论：{review.VerdictText}（可操作={review.Allowed}）");
        foreach (var r in review.Results)
            Console.WriteLine($"  {r.RuleId} {r.VerdictText}：{r.Reason}");
    }
    return 0;
}

var engineOk = StrategyEngineRegression.Run();
var notifyOk = NotifyRegression.Run();
var buffettOk = BuffettWatchRegression.Run();
var freshOk = FreshnessRegression.Run();
var kbOk = KnowledgeBaseRegression.Run();
var discOk = DisciplineRegression.Run();
if (args.Contains("--engine")) return engineOk && notifyOk && buffettOk && freshOk && kbOk && discOk ? 0 : 1;

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
var emSnap = new EastMoneySnapshot(cacheDir);

IBuiltinTask[] tasks =
{
    new DailyRiskTask(store, quoteSvc, klines, calendar, signals, csi, em),
    new WeeklyDividendTask(dataDir, store, klines, csi, em, signals),
    new MonthlyRebalanceTask(dataDir, store, signals),
    new EarningsScanTask(dataDir, em, signals),
    new AttributionPrepTask(dataDir, store, signals, klines),
    new CandidatePoolTask(dataDir, csi, em, tencent, klines, emSnap),
    new BacktestTask(dataDir, em, klines),
    new SignalLogTask(dataDir, signals, klines, calendar),
    new StrategyScanTask(dataDir, store, em, klines, emSnap),
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
return 0;
