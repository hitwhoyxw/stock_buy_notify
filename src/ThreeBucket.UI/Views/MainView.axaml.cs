using Avalonia.Controls;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class MainView : UserControl
{
    private readonly AppState _app;

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public MainView() : this(new AppState()) { }

    public MainView(AppState app)
    {
        InitializeComponent();
        _app = app;

        var tabs = Tabs;
        void Add(string header, Control content)
        {
            var item = new TabItem { Header = header, Content = content };
            tabs.Items.Add(item);
        }

        Add("📊 任务面板", new DashboardView(_app, SetStatus));
        Add("💼 持仓总览", new PortfolioView(_app, SetStatus));
        Add("📋 候选池", new CandidatesView(_app, SetStatus));
        Add("🧠 LLM 分析", new AnalysisView(_app, SetStatus));
        Add("🎯 监控自选", new WatchlistView(_app, SetStatus));
        Add("🧭 策略管理", new StrategyView(_app, SetStatus));
        Add("🤖 LLM 桥接", new LlmBridgeView(_app, SetStatus));
        Add("📈 报告", new ReportsView(_app, SetStatus));
        Add("⚙️ 设置", new SettingsView(_app, SetStatus));

        // 切换标签页时刷新对应页面（复刻 PyQt5 showEvent）
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem is TabItem { Content: IRefreshable r })
                r.OnShown();
        };

        // 初始选中页也刷新一次
        if (tabs.SelectedItem is TabItem { Content: IRefreshable init })
            init.OnShown();
    }

    public void SetStatus(string msg) => StatusText.Text = msg;
}
