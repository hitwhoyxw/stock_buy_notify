using System.Diagnostics;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Services;

// ── 三桶策略系统 CLI：无 UI 运行内置任务（CI / 服务器 / 手动批处理通用）──
//
// 用法:
//   ThreeBucket.Cli --list               列出全部内置任务
//   ThreeBucket.Cli --task T1,T8         运行指定任务（逗号分隔）
//   ThreeBucket.Cli --task T3 --data D:\path\to\data   指定 data 目录
//                                          （默认自动定位含 scripts/ 的项目根下 data/，
//                                            CI 里 dotnet run 时当前目录即仓库根）
// 退出码: 0=全部成功  1=任一任务失败  2=参数/用法错误
// 注: 交易日判断内建在任务里（非交易日自动跳过并记成功），无需外部日历预判。

var rest = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? taskArg = null, dataArg = null;
var listOnly = false;
for (var i = 0; i < rest.Length; i++)
{
    switch (rest[i])
    {
        case "--list": listOnly = true; break;
        case "--task":
            if (i + 1 >= rest.Length) { Console.Error.WriteLine("--task 缺少参数"); return 2; }
            taskArg = rest[++i]; break;
        case "--data":
            if (i + 1 >= rest.Length) { Console.Error.WriteLine("--data 缺少参数"); return 2; }
            dataArg = rest[++i]; break;
        default:
            Console.Error.WriteLine($"未知参数: {rest[i]}"); return 2;
    }
}

// ── 定位 data 目录 ──
string dataDir;
if (!string.IsNullOrEmpty(dataArg))
{
    dataDir = dataArg;
}
else
{
    // 从 exe 目录逐级向上找含 scripts/ 的目录（与 AppState.DetectProjectRoot 同逻辑）
    var root = AppContext.BaseDirectory;
    for (var i = 0; i < 10; i++)
    {
        if (Directory.Exists(Path.Combine(root, "scripts"))) break;
        var parent = Directory.GetParent(root)?.FullName;
        if (parent is null || parent == root) break;
        root = parent;
    }
    // 兜底当前目录（dotnet run --project 时 cwd 通常是仓库根）
    if (!Directory.Exists(Path.Combine(root, "scripts"))
        && Directory.Exists(Path.Combine(Environment.CurrentDirectory, "scripts")))
        root = Environment.CurrentDirectory;
    dataDir = Path.Combine(root, "data");
}
Directory.CreateDirectory(dataDir);
Console.WriteLine($"数据目录: {Path.GetFullPath(dataDir)}");

// ── 组装服务与任务（与 UI AppState / Demo 相同的依赖图）──
var store = new DataStore(dataDir);
var quoteSvc = new QuoteService();
var klines = new KlineService();
var calendar = new TradingCalendar(dataDir, klines);
var signals = new SignalLogStore(dataDir);
var cacheDir = Path.Combine(dataDir, "cache");
var em = new EastMoneyClient(cacheDir);
var csi = new CsIndexClient(cacheDir);
var tencent = new TencentSnapshot();

var tasks = new Dictionary<string, IBuiltinTask>(StringComparer.OrdinalIgnoreCase)
{
    ["T1"] = new DailyRiskTask(store, quoteSvc, klines, calendar, signals),
    ["T2"] = new WeeklyDividendTask(dataDir, store, klines, csi, em, signals),
    ["T3"] = new MonthlyRebalanceTask(dataDir, store, signals),
    ["T4"] = new EarningsScanTask(dataDir, em, signals),
    ["T5"] = new AttributionPrepTask(dataDir, store, signals, klines),
    ["T6"] = new CandidatePoolTask(dataDir, csi, em, tencent, klines),
    ["T7"] = new BacktestTask(dataDir, em, klines),
    ["T8"] = new SignalLogTask(dataDir, signals, klines, calendar),
};

if (listOnly)
{
    Console.WriteLine("\n内置任务:");
    foreach (var t in tasks.Values)
        Console.WriteLine($"  {t.Key}  {t.Name}");
    return 0;
}

if (string.IsNullOrEmpty(taskArg))
{
    Console.Error.WriteLine("\n用法: ThreeBucket.Cli --task T1,T8 [--data <dir>] | --list");
    return 2;
}

// ── 运行 ──
var keys = taskArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(k => k.ToUpperInvariant()).Distinct().ToList();
var missing = keys.Where(k => !tasks.ContainsKey(k)).ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"未知任务: {string.Join(", ", missing)}（--list 查看全部）");
    return 2;
}

Console.WriteLine();
var results = new List<(string Key, string Name, TaskRunResult R)>();
foreach (var key in keys)
{
    var t = tasks[key];
    Console.WriteLine($"──────── ▶ {t.Key} {t.Name} ────────");
    var sw = Stopwatch.StartNew();
    var r = await t.RunAsync(msg => Console.WriteLine($"  {DateTime.Now:HH:mm:ss} {msg}"));
    sw.Stop();
    Console.WriteLine(r.Ok
        ? $"✅ {t.Key} 完成（{sw.Elapsed.TotalSeconds:0}s）{r.Summary}  报告: {r.ReportPath}"
        : $"❌ {t.Key} 失败（{sw.Elapsed.TotalSeconds:0}s）{r.Summary}");
    results.Add((t.Key, t.Name, r));
}

Console.WriteLine("\n════════ 结果汇总 ════════");
foreach (var (key, name, r) in results)
    Console.WriteLine($"{(r.Ok ? "✅" : "❌")} {key} {name}: {r.Summary}");
var failed = results.Count(x => !x.R.Ok);
Console.WriteLine(failed == 0 ? "全部成功 ✓" : $"{failed} 个任务失败 ✗");
return failed == 0 ? 0 : 1;
