using System.IO;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.UI.Services;

/// <summary>应用级共享状态：解析项目根与数据目录，提供数据访问、行情服务与内置任务引擎。</summary>
public class AppState
{
    public string ProjectRoot { get; }
    public string DataDir { get; }
    public DataStore Store { get; }
    public QuoteService Quotes { get; }
    public AppConfig Config { get; set; }

    // ── C# 原生任务体系（桌面/移动端通用，不依赖 Python） ──
    public KlineService Klines { get; }
    public TradingCalendar Calendar { get; }
    public SignalLogStore Signals { get; }
    public EastMoneyClient EastMoney { get; }
    public CsIndexClient CsIndex { get; }
    public TencentSnapshot Tencent { get; }
    public IReadOnlyDictionary<string, IBuiltinTask> BuiltinTasks { get; }
    public TaskSchedulerEngine SchedulerEngine { get; }
    public AutoSyncService AutoSync { get; }

    public AppState()
    {
        ProjectRoot = DetectProjectRoot();
        DataDir = Path.Combine(ProjectRoot, "data");
        Store = new DataStore(DataDir);
        Quotes = new QuoteService();
        Config = Store.LoadConfig();
        if (string.IsNullOrEmpty(Config.DataDir)) Config.DataDir = DataDir;
        if (string.IsNullOrEmpty(Config.ProjectRoot)) Config.ProjectRoot = ProjectRoot;

        Klines = new KlineService();
        Calendar = new TradingCalendar(DataDir, Klines);
        Signals = new SignalLogStore(DataDir);
        var cacheDir = Path.Combine(DataDir, "cache");
        EastMoney = new EastMoneyClient(cacheDir);
        CsIndex = new CsIndexClient(cacheDir);
        Tencent = new TencentSnapshot();
        BuiltinTasks = new Dictionary<string, IBuiltinTask>(StringComparer.OrdinalIgnoreCase)
        {
            ["T1"] = new DailyRiskTask(Store, Quotes, Klines, Calendar, Signals),
            ["T2"] = new WeeklyDividendTask(DataDir, Store, Klines, CsIndex, EastMoney, Signals),
            ["T3"] = new MonthlyRebalanceTask(DataDir, Store, Signals),
            ["T4"] = new EarningsScanTask(DataDir, EastMoney, Signals),
            ["T5"] = new AttributionPrepTask(DataDir, Store, Signals, Klines),
            ["T6"] = new CandidatePoolTask(DataDir, CsIndex, EastMoney, Tencent, Klines),
            ["T7"] = new BacktestTask(DataDir, EastMoney, Klines),
            ["T8"] = new SignalLogTask(DataDir, Signals, Klines, Calendar),
        };
        // Func<AppConfig> 实时取最新配置：设置页保存后调度行为立即生效，无需重启
        SchedulerEngine = new TaskSchedulerEngine(() => Config, BuiltinTasks, DataDir);
        // 自动云同步：同样实时读配置，设置页改开关/密钥后无需重启
        AutoSync = new AutoSyncService(() => Config, Store, DataDir);
    }

    /// <summary>从 exe 目录逐级向上查找含 scripts/ 的目录作为项目根；移动端直接用应用沙盒可写目录。</summary>
    private static string DetectProjectRoot()
    {
        // 移动端（Android/iOS）无项目结构与任意文件系统，直接用应用沙盒可写目录作为数据根
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // AppContext.BaseDirectory：单文件发布时 Assembly.Location 为空串，此属性两种模式均返回 exe 实际目录
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "scripts")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        // 兜底：当前目录或 exe 目录
        if (Directory.Exists(Path.Combine(System.Environment.CurrentDirectory, "scripts")))
            return System.Environment.CurrentDirectory;
        return dir;
    }
}
