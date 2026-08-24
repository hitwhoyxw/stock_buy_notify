using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ThreeBucket.Core.Data;
using ThreeBucket.UI.Dialogs;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class CandidatesView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private List<Dictionary<string, string>> _full = new();

    private static readonly HashSet<string> InstCols = new() { "has_insurance", "has_social_security", "has_pension", "has_qfii" };
    private static readonly HashSet<string> Hidden = new() { "inst_detail", "pick_reason" };

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public CandidatesView() : this(new AppState(), _ => { }) { }

    public CandidatesView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;

        BucketCombo.SelectionChanged += (_, _) => Load();
        RefreshBtn.Click += (_, _) => Load();
        InstFilter.IsCheckedChanged += (_, _) => Apply();
        SearchBox.TextChanged += (_, _) => Apply();
        Grid.SelectionChanged += (_, _) => ShowDetail();
        AddWatch.Click += (_, _) => AddToWatch();
    }

    public void OnShown() => Load();

    public void Load()
    {
        var bucket = "ABC"[BucketCombo.SelectedIndex].ToString();
        var (headers, rows) = _app.Store.LoadCandidates(bucket);
        _full = rows;
        BuildColumns(headers);
        Apply();
        Stats.Text = $"{bucket}桶: {rows.Count} 只  |  更新于 {_app.Store.FileMtime($"candidates_{bucket}.csv")}";
    }

    private void BuildColumns(List<string> headers)
    {
        Grid.Columns.Clear();
        foreach (var h in headers.Where(h => !Hidden.Contains(h)))
        {
            // 列名来自 CSV 表头，无法用强类型属性；索引器路径（[key]/['key']）在 Avalonia 版本间
            // 行为不一致会导致整列空白——改绑行对象本身，ConverterParameter 传列名取值
            var col = new DataGridTemplateColumn
            {
                Header = h,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                CellTemplate = new FuncDataTemplate<Dictionary<string, string>>((_, _) => new TextBlock
                {
                    [!TextBlock.TextProperty] = new Avalonia.Data.Binding
                    {
                        Converter = new DictValueConverter(),
                        ConverterParameter = h,
                    },
                }),
            };
            Grid.Columns.Add(col);
        }
    }

    private void Apply()
    {
        if (_full.Count == 0) { Grid.SetItemsSafe(null); return; }
        var q = _full;
        if (InstFilter.IsChecked == true)
            q = q.Where(r => InstCols.Any(c => r.GetValueOrDefault(c, "") == "是")).ToList();
        var text = SearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(text))
            q = q.Where(r => (r.GetValueOrDefault("code", "") + r.GetValueOrDefault("name", "")).Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        Grid.SetItemsSafe(new ObservableCollection<Dictionary<string, string>>(q));
    }

    private void ShowDetail()
    {
        if (Grid.SelectedItem is not Dictionary<string, string> row) { Detail.Text = ""; return; }
        var parts = new List<string>();
        if (row.TryGetValue("inst_detail", out var id) && !string.IsNullOrEmpty(id)) parts.Add($"🏛️ 机构持仓: {id}");
        if (row.TryGetValue("pick_reason", out var pr) && !string.IsNullOrEmpty(pr)) parts.Add($"📋 入选理由: {pr}");
        Detail.Text = string.Join("  |  ", parts);
    }

    private void AddToWatch()
    {
        if (Grid.SelectedItem is not Dictionary<string, string> row) { _status("请先选中一行"); return; }
        var code = row.GetValueOrDefault("code", "").Trim();
        var name = row.GetValueOrDefault("name", "").Trim();
        var (ok, msg) = _app.Store.AddWatch(code, name, "candidates");
        _status(msg);
        if (ok) Load();
    }
}
