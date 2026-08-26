using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;
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
    private bool _loading; // 程序化设置 IsChecked 时屏蔽 IsCheckedChanged，避免覆盖配置/刷状态
    private readonly HashSet<string> _cfgErrors = new(); // 策略配置错误上报去重

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

        // 通知开关（纯开关，webhook/密钥在设置页配置）：切换即写 _app.Config 并持久化
        LarkToggle.IsCheckedChanged += (_, _) => OnNotifyToggleChanged(isLark: true);
        SysToggle.IsCheckedChanged += (_, _) => OnNotifyToggleChanged(isLark: false);
        SyncNotifyToggles(); // 构造期从配置初始化开关（IsCheckedChanged 被 _loading 屏蔽）

        // 60s 自动拉行情：盘中持续刷新；盘外数据不变，仅在最近定盘后拉一次（交易时段节流）
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoTimer.Tick += (_, _) => _ = AutoRefreshAsync();
        _autoTimer.Start();
    }

    public void OnShown()
    {
        SyncNotifyToggles(); // 设置页可能已改开关，切回本页同步开关视觉
        Load();
        _ = AutoRefreshAsync(); // 进入页面拉一次（盘外已拉过则跳过）
    }

    private void Load()
    {
        // 旧行已拉到的行情按代码继承：切回页面重建集合时不闪 0
        // （盘外行情有节流，切回后不一定重拉；不继承会一直显示 0.00）
        var old = _rows.Where(r => r.Price > 0)
            .ToDictionary(r => DataStore.NormalizeCode(r.Code), r => (r.Price, r.ChangePct));
        _rows = new ObservableCollection<WatchRow>(
            _app.Store.ListWatchlist().Select(w => new WatchRow
            {
                Code = w.Code, Name = w.Name, Strategies = w.Strategies,
                Note = w.Note, AddedAt = w.AddedAt,
                Price = old.TryGetValue(DataStore.NormalizeCode(w.Code), out var o) ? o.Item1 : 0,
                ChangePct = old.TryGetValue(DataStore.NormalizeCode(w.Code), out var o2) ? o2.Item2 : 0,
            }));
        WatchGrid.SetItemsSafe(_rows);
        Info.Text = $"共 {_rows.Count} 只";
        LoadHistory();
    }

    private void LoadHistory()
    {
        HistoryGrid.SetItemsSafe(new ObservableCollection<AlertEntry>(_app.Store.LoadHistory()));
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
            // 直接重赋 ItemsSource 会因选中索引越界崩进程（详见 SetItemsSafe 注释）
            WatchGrid.SetItemsSafe(_rows);
            await EvaluateStrategiesAsync();
            LoadHistory();
            _status($"✅ 行情已更新（{map.Count} 只）");
        }
        finally { _fetching = false; }
    }

    /// <summary>
    /// 策略评估（StrategyEngine 条件树引擎）：
    /// 条件树 JSON 与旧扁平三列均支持（MA/MACD/量比/金叉死叉等，需日K），
    /// K 线按股票并发拉取（盘中强制刷新，捕捉当日金叉/放量）；
    /// 持仓浮盈类指标取加权成本法聚合的平均成本；无持仓为 null（数据不足跳过）。
    /// </summary>
    private async Task EvaluateStrategiesAsync()
    {
        var all = _app.Store.ListStrategies().Where(s => s.Enabled).ToList();
        if (all.Count == 0) return;

        // 成本表：{code: 平均成本}（持仓浮盈类指标用）
        var costs = _app.Store.LoadPositions()
            .Where(p => p.AvgCost > 0)
            .ToDictionary(p => DataStore.NormalizeCode(p.Code), p => p.AvgCost);

        // 待评估行 → 并发拉日K（仅绑定了需要 K 线的策略才拉）
        var pending = _rows
            .Select(r => (Row: r, Ids: r.Strategies.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList()))
            .Where(x => x.Ids.Count > 0 && x.Ids.Any(id => all.Any(s => s.Id == id)))
            .ToList();
        if (pending.Count == 0) return;

        var klineMap = new Dictionary<string, IReadOnlyList<DailyBar>?>();
        var needKline = pending.Where(x => x.Ids.Any(id =>
        {
            var s = all.FirstOrDefault(v => v.Id == id);
            return s is not null && (!string.IsNullOrWhiteSpace(s.Condition) || s.Indicator != "price");
        })).Select(x => x.Row).ToList();
        if (needKline.Count > 0)
        {
            var tasks = needKline.Select(async r =>
            {
                try { return (r.Code, await _app.Klines.GetStockDailyFreshAsync(r.Code)); }
                catch { return (r.Code, (IReadOnlyList<DailyBar>?)null); }
            });
            foreach (var (code, bars) in await Task.WhenAll(tasks))
                klineMap[code] = bars;
        }

        var alerts = new List<AlertEntry>();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        foreach (var (r, ids) in pending)
        {
            var code = DataStore.NormalizeCode(r.Code);
            var ctx = new IndicatorContext(
                r.Price > 0 ? r.Price : null, r.ChangePct,
                klineMap.GetValueOrDefault(r.Code) ?? klineMap.GetValueOrDefault(code),
                costs.GetValueOrDefault(code));
            foreach (var s in all.Where(s => ids.Contains(s.Id)))
            {
                try
                {
                    if (StrategyEngine.EvaluateStrategy(s, ctx) is not true) continue;
                }
                catch (StrategyConfigError e)
                {
                    // 配置错误：状态栏上报一次（去重）并跳过，绝不静默当 false
                    var msg = $"策略 {s.Id} 配置错误: {e.Message}";
                    if (_cfgErrors.Add(msg)) _status($"⚠️ [监控] {msg}");
                    continue;
                }
                alerts.Add(new AlertEntry
                {
                    Code = r.Code, Name = r.Name, StrategyId = s.Id,
                    StrategyName = s.Name, Action = s.Action, Priority = s.Priority, Time = now,
                });
            }
        }
        if (alerts.Count > 0)
        {
            // 只对当日首次触发的提醒外发通知（同策略同股票每天最多提醒一次，防骚扰）
            var fresh = _app.Store.RecordAlerts(alerts);
            _status(fresh.Count > 0
                ? $"🔔 触发 {alerts.Count} 条策略提醒（新增 {fresh.Count} 条，已推送通知）"
                : $"🔔 触发 {alerts.Count} 条策略提醒（今日已提醒过，不重复推送）");
            if (fresh.Count > 0) _ = NotifyExternalAsync(fresh);
        }
    }

    /// <summary>
    /// 从 _app.Config 同步两个通知开关的 IsChecked（构造期与每次 OnShown 调用）。
    /// 程序化赋值会触发 IsCheckedChanged，用 _loading 屏蔽以免回调覆盖配置/刷状态。
    /// </summary>
    private void SyncNotifyToggles()
    {
        _loading = true;
        try
        {
            LarkToggle.IsChecked = _app.Config.NotifyLarkEnabled;
            SysToggle.IsChecked = _app.Config.NotifySystemEnabled;
        }
        finally { _loading = false; }
    }

    /// <summary>
    /// 工具栏通知开关切换：立即写 _app.Config 并持久化，与设置页同一 AppConfig 对象同步。
    /// 飞书开启但 webhook 未配置时仅告警不阻止（NotifyExternalAsync 已有 IsValidWebhook 兜底）。
    /// </summary>
    private void OnNotifyToggleChanged(bool isLark)
    {
        if (_loading) return; // 程序化赋值触发的，忽略
        if (isLark) _app.Config.NotifyLarkEnabled = LarkToggle.IsChecked == true;
        else       _app.Config.NotifySystemEnabled = SysToggle.IsChecked == true;

        try { _app.Store.SaveConfig(_app.Config); }
        catch (Exception ex) { _status($"⚠️ 通知开关保存失败: {ex.Message}"); return; }

        if (isLark)
        {
            var on = _app.Config.NotifyLarkEnabled;
            _status(on ? "🔔 飞书推送已开启" : "🔕 飞书推送已关闭");
            if (on && !LarkNotifier.IsValidWebhook(_app.Config.LarkWebhook))
                _status("⚠️ 飞书 webhook 未配置，去设置页填写");
        }
        else _status(_app.Config.NotifySystemEnabled ? "💻 系统通知已开启" : "🔕 系统通知已关闭");
    }

    /// <summary>
    /// 外发提醒（app 内历史表格之外的通道）：飞书 webhook + 系统通知。
    /// fire-and-forget：失败仅状态栏上报，绝不影响监控主流程。
    /// </summary>
    private async Task NotifyExternalAsync(List<AlertEntry> fresh)
    {
        var cfg = _app.Config;
        var message = LarkNotifier.BuildAlertMessage(fresh);

        if (cfg.NotifyLarkEnabled && LarkNotifier.IsValidWebhook(cfg.LarkWebhook))
        {
            var (ok, msg) = await LarkNotifier.SendAsync(cfg.LarkWebhook, message, cfg.LarkSecret);
            if (!ok) _status($"⚠️ 飞书推送失败: {msg}");
        }

        if (cfg.NotifySystemEnabled)
        {
            // toast 摘要：P0/P1 在前，最多展开 3 条（完整列表看 app 内"提醒历史"表格）
            var digest = string.Join("\n", fresh
                .OrderBy(a => a.Priority switch { "P0" => 0, "P1" => 1, "P2" => 2, _ => 3 })
                .Take(3)
                .Select(a => $"{a.Code} {a.Name} · {a.StrategyName}"));
            if (fresh.Count > 3) digest += $"\n… 另有 {fresh.Count - 3} 条";
            WindowsToastNotifier.Show($"🎯 三桶监控 · 触发 {fresh.Count} 条策略提醒", digest);
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
