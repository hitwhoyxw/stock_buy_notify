using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using ThreeBucket.Core.Models;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

/// <summary>
/// 顶层「策略扫描 T9」页：维护扫描名单（从策略库加入/移出并持久化）、手动跑 T9、按策略查看命中。
/// 不在此新建或编辑策略定义——那是「策略管理」页的职责。
/// </summary>
public partial class StrategyScanView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private List<Dictionary<string, string>> _full = new();
    private List<Strategy> _scanStrats = new();
    private bool _loadingStrat;
    private bool _running;

    /// <summary>加入下拉项：展示策略库全部策略；已在名单的置灰不可选。</summary>
    private sealed record AddItem(string Id, string Label, bool AlreadyInScan, bool Evaluable)
    {
        public override string ToString() => Label;
    }

    private static readonly Dictionary<string, string> ColLabels = new()
    {
        ["strategy_id"] = "策略ID",
        ["strategy_name"] = "策略名",
        ["priority"] = "优先级",
        ["code"] = "代码",
        ["name"] = "名称",
        ["price"] = "现价",
        ["change_pct"] = "涨跌%",
        ["action"] = "建议",
    };

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public StrategyScanView() : this(new AppState(), _ => { }) { }

    public StrategyScanView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app;
        _status = status;

        RunBtn.Click += (_, _) => _ = RunScanAsync();
        RefreshBtn.Click += (_, _) => LoadResults();
        ClearLogBtn.Click += (_, _) => Log.Text = "";
        AddBtn.Click += (_, _) => AddFromStore();
        RemoveBtn.Click += (_, _) => RemoveFromScan();
        StratCombo.SelectionChanged += (_, _) => { if (_loadingStrat) return; ApplyFilter(); };
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        AddWatch.Click += (_, _) => AddToWatch();
    }

    public void OnShown()
    {
        LoadScanList();
        LoadResults();
    }

    private void LoadScanList()
    {
        _scanStrats = _app.Store.ListStrategyScanStrategies();
        StratGrid.SetItemsSafe(new ObservableCollection<Strategy>(_scanStrats));
        RefreshAddCombo();
    }

    /// <summary>
    /// 下拉展示策略库全部策略（不过滤类型/启用/条件）。
    /// 已在扫描名单的置灰并标「已在名单」；不可求值的仍可见并标「不可求值」。
    /// </summary>
    private void RefreshAddCombo()
    {
        var inScan = _app.Store.ListStrategyScanIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = _app.Store.ListStrategies()
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        AddCombo.Items.Clear();
        AddCombo.ItemsSource = null;
        AddItem? firstSelectable = null;
        foreach (var s in all)
        {
            var already = inScan.Contains(s.Id);
            var evaluable = !string.IsNullOrWhiteSpace(s.Condition) || !string.IsNullOrWhiteSpace(s.Indicator);
            var flags = new List<string>();
            if (already) flags.Add("已在名单");
            if (!s.Enabled) flags.Add("停用");
            if (!evaluable) flags.Add("不可求值");
            var suffix = flags.Count > 0 ? "（" + string.Join("·", flags) + "）" : "";
            var item = new AddItem(s.Id, $"{s.Id} {s.Name} [{s.Type}]{suffix}", already, evaluable);
            var box = new ComboBoxItem
            {
                Content = item.Label,
                Tag = item,
                IsEnabled = !already, // 已在名单：可见但不可选
                Opacity = already ? 0.55 : (evaluable ? 1.0 : 0.75),
            };
            AddCombo.Items.Add(box);
            if (!already && firstSelectable is null) firstSelectable = item;
        }

        // 选中第一项可加入的；若全在名单则选中第一项（仅展示）
        var selectIdx = -1;
        for (var i = 0; i < AddCombo.Items.Count; i++)
        {
            if (AddCombo.Items[i] is ComboBoxItem { IsEnabled: true }) { selectIdx = i; break; }
        }
        if (selectIdx < 0 && AddCombo.Items.Count > 0) selectIdx = 0;
        AddCombo.SelectedIndex = selectIdx;
        AddBtn.IsEnabled = firstSelectable is not null;
    }

    private void AddFromStore()
    {
        AddItem? item = AddCombo.SelectedItem switch
        {
            AddItem ai => ai,
            ComboBoxItem { Tag: AddItem tagged } => tagged,
            ComboBoxItem { Content: string label } =>
                _app.Store.ListStrategies()
                    .Select(s => new AddItem(s.Id, label, false, true))
                    .FirstOrDefault(a => label.StartsWith(a.Id + " ", StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(label, a.Id, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        // ComboBoxItem.Content 是 Label 字符串时，从 Tag 取；上面已覆盖 Tag
        if (item is null && AddCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is AddItem tag2)
            item = tag2;

        if (item is null)
        {
            _status("请先在下拉框选择要加入的策略（来自策略库）");
            return;
        }
        if (item.AlreadyInScan || _app.Store.ListStrategyScanIds()
                .Any(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _status($"{item.Id} 已在扫描名单中（左侧列表可见；无需再加）");
            return;
        }
        if (_app.Store.AddStrategyScanId(item.Id))
        {
            _status($"已加入扫描名单：{item.Id}");
            AppendLog($"加入扫描：{item.Id}");
        }
        else
            _status($"{item.Id} 已在扫描名单中");
        LoadScanList();
        RefreshStratCombo();
        ApplyFilter();
    }

    private void RemoveFromScan()
    {
        if (StratGrid.SelectedItem is not Strategy s)
        {
            _status("请先在左侧扫描名单选中要移出的策略");
            return;
        }
        if (_app.Store.RemoveStrategyScanId(s.Id))
        {
            _status($"已移出扫描名单：{s.Id} {s.Name}");
            AppendLog($"移出扫描：{s.Id} {s.Name}");
        }
        else
            _status($"{s.Id} 不在扫描名单中");
        LoadScanList();
        RefreshStratCombo();
        ApplyFilter();
    }

    private void LoadResults()
    {
        RefreshStratCombo();
        ApplyFilter();
    }

    private void RefreshStratCombo()
    {
        _loadingStrat = true;
        try
        {
            var (headers, rows) = _app.Store.LoadStrategyScan();
            _ = headers;
            _full = rows;
            var items = new List<string> { "全部策略" };
            foreach (var s in _scanStrats)
            {
                var n = rows.Count(r => r.GetValueOrDefault("strategy_id", "") == s.Id);
                items.Add($"{s.Id} {s.Name}（{n}）");
            }
            foreach (var id in rows.Select(r => r.GetValueOrDefault("strategy_id", ""))
                         .Where(id => id.Length > 0 && _scanStrats.All(s => s.Id != id))
                         .Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                var name = rows.First(r => r.GetValueOrDefault("strategy_id", "") == id)
                    .GetValueOrDefault("strategy_name", "");
                var n = rows.Count(r => r.GetValueOrDefault("strategy_id", "") == id);
                items.Add(string.IsNullOrEmpty(name) ? $"{id}（{n}）" : $"{id} {name}（{n}）");
            }
            StratCombo.ItemsSource = items;
            StratCombo.SelectedIndex = 0;
        }
        finally { _loadingStrat = false; }
    }

    private string? SelectedStrategyId()
    {
        if (StratCombo.SelectedIndex <= 0 || StratCombo.SelectedItem is not string sel) return null;
        var sp = sel.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return sp.Length > 0 ? sp[0] : null;
    }

    private void ApplyFilter()
    {
        var sid = SelectedStrategyId();
        var q = _full.AsEnumerable();
        if (sid is not null)
            q = q.Where(r => r.GetValueOrDefault("strategy_id", "") == sid);
        var text = SearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(text))
            q = q.Where(r => (r.GetValueOrDefault("code", "") + r.GetValueOrDefault("name", ""))
                .Contains(text, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        BuildResultColumns();
        ResultGrid.SetItemsSafe(list.Count == 0
            ? null
            : new ObservableCollection<Dictionary<string, string>>(list));

        var mtime = _app.Store.FileMtime(sid is null ? "strategy_scan.csv" : $"strategy_scan_{sid}.csv");
        var stratHint = _scanStrats.Count == 0
            ? "扫描名单为空（用上方下拉从策略库加入）"
            : $"扫描名单 {_scanStrats.Count} 条";
        Stats.Text = $"{stratHint}  |  结果 {(sid ?? "全部")}: {list.Count} 条"
                     + (string.IsNullOrEmpty(mtime) ? "  |  尚未跑过 T9" : $"  |  更新于 {mtime}");
    }

    private void BuildResultColumns()
    {
        if (ResultGrid.Columns.Count > 0) return;
        foreach (var h in new[] { "strategy_id", "strategy_name", "priority", "code", "name", "price", "change_pct", "action" })
        {
            var label = ColLabels.TryGetValue(h, out var lbl) ? lbl : h;
            ResultGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = label,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                CellTemplate = new FuncDataTemplate<Dictionary<string, string>>((_, _) => new TextBlock
                {
                    [!TextBlock.TextProperty] = new Avalonia.Data.Binding
                    {
                        Converter = new DictValueConverter(),
                        ConverterParameter = h,
                    },
                }),
            });
        }
    }

    private async Task RunScanAsync()
    {
        if (_running) { _status("T9 正在运行，请等待完成"); return; }
        if (!_app.BuiltinTasks.TryGetValue("T9", out var task))
        {
            _status("内置任务 T9 未注册");
            return;
        }
        LoadScanList();
        if (_scanStrats.Count == 0)
        {
            _status("扫描名单为空：请先从策略库加入策略");
            AppendLog("扫描名单为空，已取消。");
            return;
        }

        _running = true;
        RunBtn.IsEnabled = false;
        AppendLog($"===== 启动 T9 · 名单 {_scanStrats.Count} 条：{string.Join(", ", _scanStrats.Select(s => s.Id))} =====");
        _status("运行 T9 策略全市场扫描…");
        try
        {
            var result = await task.RunAsync(msg => Dispatcher.UIThread.Post(() => AppendLog(msg)));
            AppendLog($"===== [{(result.Ok ? "成功" : "失败")}] {result.Summary} =====");
            _status($"T9 {(result.Ok ? "成功" : "失败")}：{result.Summary}");
            LoadScanList();
            LoadResults();
        }
        catch (Exception ex)
        {
            AppendLog("异常: " + ex.Message);
            _status("T9 异常：" + ex.Message);
        }
        finally
        {
            RunBtn.IsEnabled = true;
            _running = false;
        }
    }

    private void AppendLog(string line)
    {
        Log.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        Log.CaretIndex = Log.Text?.Length ?? 0;
    }

    private void AddToWatch()
    {
        if (ResultGrid.SelectedItem is not Dictionary<string, string> row)
        {
            _status("请先选中一行命中结果");
            return;
        }
        var code = row.GetValueOrDefault("code", "").Trim();
        var name = row.GetValueOrDefault("name", "").Trim();
        var (ok, msg) = _app.Store.AddWatch(code, name, "strategy_scan");
        _status(msg);
        if (ok) AppendLog($"已加入监控：{code} {name}");
    }
}