using System.Collections.ObjectModel;
using Avalonia.Controls;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class ReportsView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;

    public class ReportItem
    {
        public string Path { get; set; } = "";
        public string Display { get; set; } = "";
    }

    private ObservableCollection<ReportItem> _items = new();

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public ReportsView() : this(new AppState(), _ => { }) { }

    public ReportsView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;
        FileList.ItemsSource = _items;
        TypeCombo.ItemsSource = new[] { "策略报告 (report_*.md)", "交易日志 (live_trade_log.csv)", "信号日志 (live_signal_log.csv)" };
        TypeCombo.SelectedIndex = 0;
        RefreshBtn.Click += (_, _) => LoadList();
        TypeCombo.SelectionChanged += (_, _) => LoadList();
        FileList.SelectionChanged += (_, _) =>
        {
            if (FileList.SelectedItem is ReportItem r) Show(r);
        };
    }

    public void OnShown() => LoadList();

    private void LoadList()
    {
        // Clear 前先清选中：SelectionModel 持有旧行索引，清空集合时枚举越界会崩进程
        FileList.SelectedItem = null;
        _items.Clear();
        ContentBox.Text = "";

        switch (TypeCombo.SelectedIndex)
        {
            case 0:
                foreach (var f in _app.Store.ListReports())
                    _items.Add(new ReportItem
                    {
                        Path = f,
                        Display = $"📄 {Path.GetFileName(f)}\n   {File.GetLastWriteTime(f):MM-dd HH:mm}",
                    });
                break;
            case 1:
                AddCsv("live_trade_log.csv");
                break;
            case 2:
                AddCsv("live_signal_log.csv");
                break;
        }

        if (_items.Count > 0)
        {
            FileList.SelectedIndex = 0;
            Show(_items[0]);
        }
        else
        {
            ContentBox.Text = "（该类型暂无文件）";
        }
    }

    private void AddCsv(string name)
    {
        var path = Path.Combine(_app.DataDir, name);
        if (File.Exists(path))
            _items.Add(new ReportItem { Path = path, Display = $"📊 {name}" });
    }

    private void Show(ReportItem r)
    {
        if (!File.Exists(r.Path)) { ContentBox.Text = "（文件不存在）"; return; }
        try { ContentBox.Text = File.ReadAllText(r.Path, new System.Text.UTF8Encoding(false)); }
        catch (Exception ex) { ContentBox.Text = $"读取失败: {ex.Message}"; }
    }
}
