using System.Diagnostics;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Services;

// ── 三桶策略系统 CLI：无 UI 运行内置任务 + Supabase 云同步（CI / 服务器 / 手动批处理通用）──
//
// 用法:
//   ThreeBucket.Cli --list                                    列出全部内置任务
//   ThreeBucket.Cli --task T1,T8                              运行指定任务（逗号分隔）
//   ThreeBucket.Cli --task T3 --data D:\path\to\data           指定 data 目录
//   ThreeBucket.Cli --sync pull                               从 Supabase 拉最新用户数据落盘
//   ThreeBucket.Cli --sync push                               推本地数据到 Supabase
//   ThreeBucket.Cli --sync pull --task T1,T8 --sync push       组合：先拉→跑任务→推
//   Supabase 配置优先读环境变量 SUPABASE_URL / SUPABASE_KEY（CI secrets），
//   其次读 app_config.json（本地运行）。未配置时 --sync 跳过不阻断。
// 退出码: 0=全部成功  1=任一任务失败  2=参数/用法错误
// 注: 交易日判断内建在任务里（非交易日自动跳过并记成功），无需外部日历预判。
//     --sync pull/push 失败不改变退出码（辅助操作，不阻断 CI）。

var rest = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? taskArg = null, dataArg = null;
var listOnly = false;
var syncOps = new List<string>();   // "pull" / "push"，可各出现一次
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
        case "--sync":
            if (i + 1 >= rest.Length) { Console.Error.WriteLine("--sync 缺少参数 (pull/push)"); return 2; }
            var sa = rest[++i].ToLowerInvariant();
            if (sa is not ("pull" or "push"))
            { Console.Error.WriteLine($"--sync 参数无效: {sa}（应为 pull 或 push）"); return 2; }
            syncOps.Add(sa); break;
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
// 同花顺（扶摇）数据源：环境变量 THS_API_KEY（CI secrets）→ app_config.json 的 ThsApiKey（本地）；
// 未配置时 IsConfigured=false，全部自动走免费源（腾讯/新浪/东财/中证官网），行为与接入前一致
var store = new DataStore(dataDir);
var cacheDir = Path.Combine(dataDir, "cache");
var ths = new ThsClient(cacheDir);
Console.WriteLine(ths.IsConfigured
    ? "同花顺数据源: 已启用（行情快照/日K/成分股/分红主源，失败自动降级免费源）"
    : "同花顺数据源: 未启用（未配置 THS_API_KEY 或 app_config.json 的 ThsApiKey，走免费源）");
var quoteSvc = new QuoteService(ths);
var klines = new KlineService(ths);
var calendar = new TradingCalendar(dataDir, klines);
var signals = new SignalLogStore(dataDir);
var em = new EastMoneyClient(cacheDir, ths);
var csi = new CsIndexClient(cacheDir, ths);
var tencent = new TencentSnapshot();

var tasks = new Dictionary<string, IBuiltinTask>(StringComparer.OrdinalIgnoreCase)
{
    ["T1"] = new DailyRiskTask(store, quoteSvc, klines, calendar, signals, csi, em),
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

// 至少要有 --task 或 --sync，否则提示用法
if (string.IsNullOrEmpty(taskArg) && syncOps.Count == 0)
{
    Console.Error.WriteLine("\n用法: ThreeBucket.Cli --task T1,T8 [--sync pull] [--sync push] [--data <dir>] | --list");
    return 2;
}

// ── --sync pull：跑任务前从 Supabase 拉最新用户数据 ──
// pulledFileTimes 记录 pull 落盘时刻：push 时文件时间未变（任务没碰过）的种类不回推，
// 避免拉下来的原样数据刷新云端 updated_at → 客户端误判"云端较新"产生无效覆盖。
var pulledFileTimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
if (syncOps.Contains("pull"))
{
    var (sUrl, sKey) = ReadSupabase(store);
    if (sUrl.Length == 0 || sKey.Length == 0)
        Console.WriteLine("⚠ --sync pull 跳过：未配置 Supabase（设 SUPABASE_URL/SUPABASE_KEY 环境变量或 app_config.json）");
    else
    {
        Console.WriteLine("\n──────── ☁ 从 Supabase 拉取 ────────");
        try
        {
            var svc = new CloudSyncService(sUrl, sKey);
            var (rows, error) = await svc.PullAsync();
            if (error.Length > 0) Console.WriteLine($"  ⚠ 拉取失败: {error}");
            else if (rows.Count == 0) Console.WriteLine("  云端无数据（首次使用？先从客户端上传一次）");
            else
            {
                var payloads = rows.ToDictionary(r => r.Kind, r => r.Payload);
                var (count, details) = store.ImportSyncSnapshot(payloads);
                Console.WriteLine($"  已从云端更新 {count} 类数据:");
                foreach (var d in details) Console.WriteLine($"    {d}");
                foreach (var kind in payloads.Keys)
                {
                    var f = SyncFileOf(kind);
                    if (f.Length > 0 && File.Exists(Path.Combine(dataDir, f)))
                        pulledFileTimes[kind] = File.GetLastWriteTime(Path.Combine(dataDir, f));
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"  ⚠ --sync pull 异常: {ex.Message}"); }
    }
}

// ── 运行任务 ──
var failed = 0;
if (taskArg is not null)
{
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
    failed = results.Count(x => !x.R.Ok);
    Console.WriteLine(failed == 0 ? "全部成功 ✓" : $"{failed} 个任务失败 ✗");
}

// ── --sync push：跑任务后推本地数据到 Supabase ──
if (syncOps.Contains("push"))
{
    var (sUrl, sKey) = ReadSupabase(store);
    if (sUrl.Length == 0 || sKey.Length == 0)
        Console.WriteLine("⚠ --sync push 跳过：未配置 Supabase");
    else
    {
        Console.WriteLine("\n──────── ☁ 推送到 Supabase ────────");
        try
        {
            var svc = new CloudSyncService(sUrl, sKey);
            var snapshot = store.ExportSyncSnapshot();
            if (snapshot.Count == 0) Console.WriteLine("  无可推送数据");
            else
            {
                // 只推本地比云端新的种类（与 AutoSyncService 时间比对一致，留 1 分钟容差）：
                // 避免 pull 下来的原样 strategies/trades/watchlist 回推刷新 updated_at，
                // 触发客户端"云端较新"误判 → 无效覆盖 + sync_backup 垃圾。
                var (cloudRows, pullErr) = await svc.PullAsync();
                var cloudTime = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                if (pullErr.Length == 0)
                    foreach (var r in cloudRows)
                        if (r.Kind.Length > 0) cloudTime[r.Kind] = r.UpdatedAt.ToLocalTime();

                var changed = new Dictionary<string, object>();
                foreach (var kv in snapshot)
                {
                    var f = SyncFileOf(kv.Key);
                    var p = f.Length > 0 ? Path.Combine(dataDir, f) : "";
                    var has = p.Length > 0 && File.Exists(p);
                    // 规则1：pull 刚落盘且任务没碰过（文件时间停在 pull 时刻）→ 原样数据不回推
                    if (has && pulledFileTimes.TryGetValue(kv.Key, out var pt) && File.GetLastWriteTime(p) == pt)
                        continue;
                    // 规则2：云端比本地新（含容差）→ 本地无新东西，不推
                    if (has && cloudTime.TryGetValue(kv.Key, out var ct) && File.GetLastWriteTime(p) <= ct.AddMinutes(1))
                        continue;
                    changed[kv.Key] = kv.Value;
                }
                if (changed.Count == 0) Console.WriteLine("  本地无较云端新的数据，跳过推送");
                else
                {
                    var (ok, msg) = await svc.PushAsync(changed, $"ci-{Environment.MachineName}");
                    Console.WriteLine(ok ? $"  ✅ {msg}" : $"  ❌ {msg}");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"  ⚠ --sync push 异常: {ex.Message}"); }
    }
}

// 退出码：仅任务失败才非零（--sync 是辅助操作，失败不阻断）
return failed == 0 ? 0 : 1;

// ── Supabase 配置读取：环境变量优先（CI secrets），其次 app_config.json（本地）──
static (string url, string key) ReadSupabase(DataStore ds)
{
    var url = Environment.GetEnvironmentVariable("SUPABASE_URL") ?? "";
    var key = Environment.GetEnvironmentVariable("SUPABASE_KEY") ?? "";
    if (url.Length == 0 || key.Length == 0)
    {
        try { var cfg = ds.LoadConfig(); url = url.Length == 0 ? cfg.SupabaseUrl : url; key = key.Length == 0 ? cfg.SupabaseKey : key; }
        catch { /* app_config.json 不存在或损坏：用环境变量值（可能为空） */ }
    }
    return (url, key);
}

// kind → 同步文件名（与 DataStore.SyncFiles 一致）
static string SyncFileOf(string kind) => kind switch
{
    "strategies" => "strategies.csv",
    "trades" => "live_trade_log.csv",
    "watchlist" => "watchlist.csv",
    "alerts" => "monitor_alerts.json",
    _ => "",
};
