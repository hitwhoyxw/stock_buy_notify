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
    public EastMoneySnapshot EmSnapshot { get; }
    public IReadOnlyDictionary<string, IBuiltinTask> BuiltinTasks { get; }
    public TaskSchedulerEngine SchedulerEngine { get; }
    public AutoSyncService AutoSync { get; }

    /// <summary>
    /// 投资经验知识库（trading-system/09_投资经验知识库.md）。懒加载 + 变更检测：
    /// 用户直接编辑 md 后切回「投资经验」页即自动重载，无需重启。
    /// </summary>
    public InvestmentKnowledgeBase KnowledgeBase => _kb ??= LoadKnowledgeBase();

    private InvestmentKnowledgeBase? _kb;
    private DateTime _kbStamp;
    private string _kbStampPath = "";

    /// <summary>
    /// 交易纪律规则集（知识库 DISCIPLINE 类别解析而来）。与知识库同步失效：
    /// 改了 md 里的纪律条款后，切回审核页即生效。
    /// </summary>
    public DisciplineRuleSet DisciplineRules => _rules ??= DisciplineRuleSet.FromKnowledgeBase(KnowledgeBase);

    private DisciplineRuleSet? _rules;

    /// <summary>纪律审核事实采集器（行情 + 指数 + 交易日历）。</summary>
    public DisciplineFactsCollector DisciplineFacts => _facts ??= new DisciplineFactsCollector(Quotes, Klines, Calendar);

    private DisciplineFactsCollector? _facts;

    private InvestmentKnowledgeBase LoadKnowledgeBase()
    {
        var kb = InvestmentKnowledgeBase.LoadDefault(ProjectRoot);
        _kbStampPath = kb.SourcePath;
        _kbStamp = kb.SourcePath.Length > 0 && kb.SourcePath != "embedded" && File.Exists(kb.SourcePath)
            ? File.GetLastWriteTimeUtc(kb.SourcePath)
            : DateTime.MinValue;
        return kb;
    }

    /// <summary>文件有更新（或从未成功加载）则重载知识库；返回是否发生了重载。</summary>
    public bool RefreshKnowledgeBaseIfChanged()
    {
        var path = _kbStampPath;
        var fileChanged = path.Length > 0 && path != "embedded" && File.Exists(path)
                          && File.GetLastWriteTimeUtc(path) != _kbStamp;
        if (_kb is null || !_kb.IsLoaded || fileChanged)
        {
            _kb = LoadKnowledgeBase();
            _rules = null;   // 知识库换了，纪律规则集必须重新解析（否则改 md 不生效）
            return true;
        }
        return false;
    }

    public AppState()
    {
        ProjectRoot = DetectProjectRoot();
        DataDir = Path.Combine(ProjectRoot, "data");
        Store = new DataStore(DataDir);
        Config = Store.LoadConfig();
        if (string.IsNullOrEmpty(Config.DataDir)) Config.DataDir = DataDir;
        if (string.IsNullOrEmpty(Config.ProjectRoot)) Config.ProjectRoot = ProjectRoot;

        // 同花顺（扶摇）数据源：环境变量 THS_API_KEY → app_config.json 的 ThsApiKey（设置页保存后需重启生效）
        var cacheDir = Path.Combine(DataDir, "cache");
        var ths = new ThsClient(cacheDir);
        Quotes = new QuoteService(ths);
        Klines = new KlineService(ths);
        Calendar = new TradingCalendar(DataDir, Klines);
        Signals = new SignalLogStore(DataDir);
        EastMoney = new EastMoneyClient(cacheDir, ths);
        CsIndex = new CsIndexClient(cacheDir, ths);
        Tencent = new TencentSnapshot();
        EmSnapshot = new EastMoneySnapshot(cacheDir);
        BuiltinTasks = new Dictionary<string, IBuiltinTask>(StringComparer.OrdinalIgnoreCase)
        {
            ["T1"] = new DailyRiskTask(Store, Quotes, Klines, Calendar, Signals, CsIndex, EastMoney),
            ["T2"] = new WeeklyDividendTask(DataDir, Store, Klines, CsIndex, EastMoney, Signals),
            ["T3"] = new MonthlyRebalanceTask(DataDir, Store, Signals),
            ["T4"] = new EarningsScanTask(DataDir, EastMoney, Signals),
            ["T5"] = new AttributionPrepTask(DataDir, Store, Signals, Klines),
            ["T6"] = new CandidatePoolTask(DataDir, CsIndex, EastMoney, Tencent, Klines, EmSnapshot),
            ["T7"] = new BacktestTask(DataDir, EastMoney, Klines),
            ["T8"] = new SignalLogTask(DataDir, Signals, Klines, Calendar),
            ["T9"] = new StrategyScanTask(DataDir, Store, EastMoney, Klines, EmSnapshot),
            ["T10"] = new BottomReversalTask(DataDir),   // S27 增强版底部反转（C# 引擎 Services/BottomReversal）
            ["T11"] = new SlowRiseTask(DataDir),   // S28 底部反转缓升（C# 引擎 Services/SlowRise）
            ["T12"] = new QualityValueTask(DataDir, EastMoney, Quotes, () => Config, Store),   // S29 优质股击球区
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
