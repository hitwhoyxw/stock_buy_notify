using System.Collections.ObjectModel;
using Avalonia.Controls;
using ThreeBucket.Core.Models;
using ThreeBucket.UI.Dialogs;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class StrategyView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private ObservableCollection<Strategy> _rows = new();

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public StrategyView() : this(new AppState(), _ => { }) { }

    public StrategyView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;
        Grid.ItemsSource = _rows;

        AddBtn.Click += (_, _) => _ = OnAddAsync();
        EditBtn.Click += (_, _) => _ = OnEditAsync();
        ToggleBtn.Click += (_, _) => OnToggle();
        DelBtn.Click += (_, _) => _ = OnDeleteAsync();
        Grid.DoubleTapped += (_, _) => _ = OnEditAsync();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _rows = new ObservableCollection<Strategy>(_app.Store.ListStrategies());
        Grid.ItemsSource = _rows;
        _status($"共 {_rows.Count} 条策略");
    }

    private Strategy? Selected =>
        Grid.SelectedItem is Strategy s ? s : null;

    private async Task OnAddAsync()
    {
        if (VisualRoot is not Window owner) return;
        var dlg = new StrategyDialog(null);
        if (await dlg.ShowAsync(owner) && dlg.GetRecord() is { } s)
        {
            _app.Store.AddStrategy(s);
            Load();
            _status($"已新增策略 {s.Id}");
        }
    }

    private async Task OnEditAsync()
    {
        if (VisualRoot is not Window owner) return;
        var cur = Selected;
        if (cur == null) { _status("请先选中一条策略"); return; }
        var dlg = new StrategyDialog(cur);
        if (await dlg.ShowAsync(owner) && dlg.GetRecord() is { } s)
        {
            s.Id = cur.Id;
            _app.Store.UpdateStrategy(s);
            Load();
            _status($"已更新策略 {s.Id}");
        }
    }

    private void OnToggle()
    {
        var cur = Selected;
        if (cur == null) { _status("请先选中一条策略"); return; }
        _app.Store.ToggleStrategy(cur.Id);
        Load();
    }

    private async Task OnDeleteAsync()
    {
        if (VisualRoot is not Window owner) return;
        var cur = Selected;
        if (cur == null) { _status("请先选中一条策略"); return; }
        if (!await MessageBox.Ask(owner, "确认删除",
            $"确定删除策略 {cur.Id}（{cur.Name}）吗？\n已应用该策略的自选会解除引用。")) return;
        _app.Store.DeleteStrategy(cur.Id);
        Load();
        _status($"已删除策略 {cur.Id}");
    }
}
