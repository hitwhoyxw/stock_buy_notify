using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.UI.Dialogs;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class WatchlistView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private ObservableCollection<WatchRow> _rows = new();
    private DateTime? _lastFetch;
    private readonly DispatcherTimer _autoTimer;
    private bool _fetching;

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public WatchlistView() : this(new AppState(), _ => { }) { }

    public WatchlistView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;

        AddBtn.Click += (_, _) => _ = AddManualAsync();
        DelBtn.Click += (_, _) => RemoveSelected();
        BindBtn.Click += (_, _) => _ = BindStrategiesAsync();
        NoteBtn.Click += (_, _) => _ = EditNoteAsync();
        RefreshBtn.Click += async (_, _) => await ForceRefreshAsync();
        ClearBtn.Click += (_, _) => { _app.Store.ClearHistory(); LoadHistory(); };
        WatchGrid.DoubleTapped += (_, _) => _ = BindStrategiesAsync();

        // 60s 自动拉行情：盘中持续刷新；盘外数据不变，仅在最近定盘后拉一次（交易时段节流）
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoTimer.Tick += (_, _) => _ = AutoRefreshAsync();
        _autoTimer.Start();
    }

    public void OnShown()
    {
        Load();
        _ = AutoRefreshAsync(); // 进入页面拉一次（盘外已拉过则跳过）
    }

    private void Load()
    {
        _rows = new ObservableCollection<WatchRow>(
            _app.Store.ListWatchlist().Select(w => new WatchRow
            {
                Code = w.Code, Name = w.Name, Strategies = w.Strategies,
                Note = w.Note, AddedAt = w.AddedAt,
            }));
        WatchGrid.ItemsSource = _rows;
        Info.Text = $"共 {_rows.Count} 只";
        LoadHistory();
    }

    private void LoadHistory()
    {
        HistoryGrid.ItemsSource = new ObservableCollection<AlertEntry>(_app.Store.LoadHistory());
    }

    // ── 行情刷新 ──

    /// <summary>自动刷新入口：盘外数据不变时跳过（手动刷新不受此节流限制）。</summary>
    private async Task AutoRefreshAsync()
    {
        if (!MarketTimes.ShouldRefreshQuotes(_lastFetch)) return;
        // 兜底定时器链路异常，避免 DispatcherTimer.Tick 同步段抛异常直接崩进程
        try { await FetchAndEvalAsync(); }
        catch (Exception ex) { _status($"⚠️ 自选刷新异常: {ex.Message}"); }
    }

    /// <summary>手动刷新：重置节流锚点后强制拉一次。</summary>
    private async Task ForceRefreshAsync()
    {
        _lastFetch = null;
        await FetchAndEvalAsync();
    }

    private async Task FetchAndEvalAsync()
    {
        if (_rows.Count == 0 || _fetching) return;
        _fetching = true;
        try
        {
            _status("⏳ 拉取行情…");
            var map = await _app.Quotes.FetchAsync(_rows.Select(r => r.Code));
            if (map.Count == 0) { _status("⚠️ 行情获取失败（腾讯/新浪均无返回）"); return; }
            _lastFetch = DateTime.Now;

            foreach (var r in _rows)
            {
                if (!map.TryGetValue(DataStore.NormalizeCode(r.Code), out var q)) continue;
                r.Price = (double)q.Price;
                r.ChangePct = q.ChangePct;
                if (string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(q.Name))
                {
                    r.Name = q.Name;
                    _app.Store.SetName(r.Code, q.Name); // 名称空白自动补全回写 CSV
                }
            }
            WatchGrid.ItemsSource = null; WatchGrid.ItemsSource = _rows;
            EvaluateStrategies();
            LoadHistory();
            _status($"✅ 行情已更新（{map.Count} 只）");
        }
        finally { _fetching = false; }
    }

    /// <summary>
    /// 策略评估（当前为简化版）：简单策略按「现价 vs 阈值」近似评估，
    /// 完整指标体系（MA/回撤/量比等需 K 线）后续随监控引擎迁移接入。
    /// </summary>
    private void EvaluateStrategies()
    {
        var alerts = new List<AlertEntry>();
        var all = _app.Store.ListStrategies().Where(s => s.Enabled).ToList();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        foreach (var r in _rows)
        {
            var ids = r.Strategies.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0) continue;
            foreach (var s in all.Where(s => ids.Contains(s.Id)))
            {
                if (s.Indicator != "price" || !double.TryParse(s.Threshold, out var thr)) continue;
                var hit = s.Operator switch
                {
                    "<" => r.Price > 0 && r.Price < thr,
                    "<=" => r.Price > 0 && r.Price <= thr,
                    ">" => r.Price > thr,
                    ">=" => r.Price >= thr,
                    _ => false,
                };
                if (hit)
                    alerts.Add(new AlertEntry
                    {
                        Code = r.Code, Name = r.Name, StrategyId = s.Id,
                        StrategyName = s.Name, Action = s.Action, Time = now,
                    });
            }
        }
        if (alerts.Count > 0)
        {
            _app.Store.RecordAlerts(alerts);
            _status($"🔔 触发 {alerts.Count} 条策略提醒");
        }
    }

    // ── 行操作 ──

    private async Task AddManualAsync()
    {
        if (VisualRoot is not Window owner) return;
        var input = new InputDialog("手动添加自选", "代码（6 位数字）", "", "名称（可选，留空自动获取）", "");
        if (await input.ShowAsync(owner))
        {
            var (ok, msg) = _app.Store.AddWatch(input.Value1, input.Value2);
            _status(msg);
            if (ok) Load();
        }
    }

    private void RemoveSelected()
    {
        if (WatchGrid.SelectedItem is not WatchRow w) { _status("请先选中一行"); return; }
        if (_app.Store.RemoveWatch(w.Code))
        {
            Load();
            _status($"已移除 {w.Code}");
        }
    }

    private WatchRow? Selected => WatchGrid.SelectedItem is WatchRow w ? w : null;

    private async Task BindStrategiesAsync()
    {
        if (Selected is not { } w) { _status("请先选中一行"); return; }
        if (VisualRoot is not Window owner) return;
        var all = _app.Store.ListStrategies();
        var current = w.Strategies.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var dlg = new StrategyPickDialog(w.Code, w.Name, all, current);
        if (await dlg.ShowAsync(owner))
        {
            _app.Store.SetStrategies(w.Code, dlg.SelectedIds);
            Load();
            _status($"{w.Code} 已绑定 {dlg.SelectedIds.Count} 条策略");
        }
    }

    private async Task EditNoteAsync()
    {
        if (Selected is not { } w) { _status("请先选中一行"); return; }
        if (VisualRoot is not Window owner) return;
        var input = new InputDialog("编辑备注", $"备注（{w.Code} {w.Name}）", w.Note);
        if (await input.ShowAsync(owner))
        {
            _app.Store.SetNote(w.Code, input.Value1);
            Load();
        }
    }

    public class WatchRow
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public double Price { get; set; }
        public double ChangePct { get; set; }
        public string Strategies { get; set; } = "";
        public string Note { get; set; } = "";
        public string AddedAt { get; set; } = "";
    }
}
