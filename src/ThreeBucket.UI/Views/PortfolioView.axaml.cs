using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.UI.Dialogs;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class PortfolioView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private readonly DataStore _store;
    private ObservableCollection<Position> _positions = new();
    private ObservableCollection<Trade> _trades = new();
    private DateTime? _lastFetch;
    private readonly DispatcherTimer _autoTimer;
    private bool _fetching;

    /// <summary>流水表列（与 Trade 属性一一对应，用于内联编辑回写）。</summary>
    private static readonly (string Header, string Prop)[] TradeCols =
    {
        ("日期", nameof(Trade.Date)),
        ("方向", nameof(Trade.Direction)),
        ("桶", nameof(Trade.Bucket)),
        ("代码", nameof(Trade.Code)),
        ("名称", nameof(Trade.Name)),
        ("行业", nameof(Trade.Industry)),
        ("价格", nameof(Trade.Price)),
        ("股数", nameof(Trade.Shares)),
        ("金额", nameof(Trade.Amount)),
        ("规则ID", nameof(Trade.RuleId)),
        ("决策理由", nameof(Trade.Reason)),
    };

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public PortfolioView() : this(new AppState(), _ => { }) { }

    public PortfolioView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status; _store = app.Store;

        BuildPosColumns();
        BuildTradesColumns();

        ViewCombo.SelectionChanged += (_, _) =>
        {
            var showTrades = ViewCombo.SelectedIndex == 1;
            PosPanel.IsVisible = !showTrades;
            TradesPanel.IsVisible = showTrades;
            if (showTrades) LoadTrades();
        };

        AddTradeBtn.Click += (_, _) => _ = OnAddTradeAsync();
        RefreshBtn.Click += async (_, _) => await RefreshQuotesAsync();
        EditTradeBtn.Click += (_, _) => _ = OnEditTradeAsync();
        DelTradeBtn.Click += (_, _) => _ = OnDeleteTradeAsync();

        MenuAdd.Click += (_, _) => _ = QuickTradeAsync("买入", 100);
        MenuReduce.Click += (_, _) => _ = QuickTradeAsync("卖出", ReduceShares());
        MenuClear.Click += (_, _) => _ = QuickClearAsync();
        MenuTrades.Click += (_, _) => { ViewCombo.SelectedIndex = 1; };

        // 流水表双击内联编辑：校验 → 归一化 → 回写 CSV → 持仓/图表联动刷新
        TradesGrid.CellEditEnding += (_, e) => OnTradeCellEditEnding(e);

        // 60s 自动刷新：盘中持续拉；盘外数据不变，仅在最近定盘后拉一次（交易时段节流）
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoTimer.Tick += (_, _) => _ = AutoTickAsync();
        _autoTimer.Start();
    }

    public void OnShown()
    {
        Load();
        _ = AutoTickAsync(); // 进入页面拉一次（盘外已拉过则跳过）
    }

    // ── 列定义 ──

    private void BuildPosColumns()
    {
        var b = new BucketColorConverter(); var p = new PnlColorConverter();
        AddCol("代码", nameof(Position.Code), 90);
        AddCol("名称", nameof(Position.Name), 90);
        ColorCol("桶", nameof(Position.Bucket), 50, b, null);
        AddCol("股数", nameof(Position.Shares), 80, fmt: "F0", right: true);
        AddCol("成本", nameof(Position.AvgCost), 80, fmt: "F2", right: true);
        AddCol("现价", nameof(Position.CurrentPrice), 80, fmt: "F2", right: true);
        AddCol("市值", nameof(Position.MarketValue), 90, fmt: "F0", right: true);
        ColorCol("盈亏", nameof(Position.Pnl), 90, p, "+#,##0;-#,##0", right: true);
        ColorCol("盈亏%", nameof(Position.PnlPct), 80, p, "+0.0\\%;-0.0\\%", right: true);

        void AddCol(string h, string prop, int w, IValueConverter? conv = null, string fmt = "", bool right = false)
        {
            // StringFormat 为空串时 Avalonia 会执行 string.Format("", v) 得到空文本，必须传 null
            var col = new DataGridTextColumn
            {
                Header = h,
                Binding = new Avalonia.Data.Binding(prop)
                {
                    Converter = conv,
                    StringFormat = string.IsNullOrEmpty(fmt) ? null : fmt,
                },
                Width = new DataGridLength(w),
            };
            if (right) col.CellStyleClasses.Add("right");
            PosGrid.Columns.Add(col);
        }

        // 需要按值上色的列（桶色/红绿盈亏）：Text 绑值 + Foreground 绑颜色，两个绑定同一属性。
        // 不能用 DataGridTextColumn——Converter 返回 Brush 会被当文本渲染成 "#ff27ae60"。
        // 格式串必须含数字占位符（0/#）："+F0" 这类无占位符串会把数字丢掉只输出字面量。
        void ColorCol(string h, string prop, int w, IValueConverter conv, string? fmt, bool right = false)
        {
            var col = new DataGridTemplateColumn
            {
                Header = h,
                Width = new DataGridLength(w),
                CellTemplate = new FuncDataTemplate<Position>((_, _) =>
                {
                    // 注意：View 基类（Layoutable）有同名实例属性会遮蔽枚举类型名，必须命名空间限定
                    var tb = new TextBlock
                    {
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    };
                    if (right)
                    {
                        tb.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                        tb.Margin = new Thickness(0, 0, 8, 0);
                    }
                    tb[!TextBlock.TextProperty] = new Avalonia.Data.Binding(prop) { StringFormat = fmt };
                    tb[!TextBlock.ForegroundProperty] = new Avalonia.Data.Binding(prop) { Converter = conv };
                    return tb;
                }),
            };
            PosGrid.Columns.Add(col);
        }
    }

    private void BuildTradesColumns()
    {
        foreach (var (h, prop) in TradeCols)
        {
            TradesGrid.Columns.Add(new DataGridTextColumn
            {
                Header = h,
                Binding = new Avalonia.Data.Binding(prop),
                Width = new DataGridLength(h is "决策理由" ? 180 : 90),
            });
        }
    }

    // ── 加载 ──

    public void Load()
    {
        // 旧行已拉到的现价按代码继承：切回页面重建集合时现价/盈亏不闪 0
        var old = _positions.Where(p => p.CurrentPrice > 0)
            .ToDictionary(p => p.Code, p => p.CurrentPrice);
        _positions = new ObservableCollection<Position>(_store.LoadPositions());
        foreach (var p in _positions)
            if (old.TryGetValue(p.Code, out var px) && p.CurrentPrice <= 0)
                p.CurrentPrice = px;
        PosGrid.SetItemsSafe(_positions);
        UpdateCards();
        UpdateAllocation();
        DrawNav();
        LoadTrades();
    }

    private void LoadTrades()
    {
        _trades = new ObservableCollection<Trade>(_store.ReadTrades());
        TradesGrid.SetItemsSafe(_trades);
    }

    private void UpdateCards()
    {
        Cards.Children.Clear();
        double totalCost = 0, totalMv = 0;
        foreach (var p in _positions)
        {
            totalCost += p.CostPool;
            totalMv += p.MarketValue;
        }
        var pnl = totalMv - totalCost;
        var pnlPct = totalCost > 0 ? pnl / totalCost * 100 : 0;
        var nav = _store.LoadNav();
        var navVal = nav.Count > 0 ? nav[0].GetValueOrDefault("nav", "1.0000") : "1.0000";
        var dd = nav.Count > 0 ? nav[0].GetValueOrDefault("drawdown_pct", "0") : "0";
        Card("总成本", $"{totalCost:#,0}");
        Card("总市值", $"{totalMv:#,0}");
        Card("浮盈亏", $"{pnl:+#,0} ({pnlPct:+0.0}%)", pnl > 0 ? "#e74c3c" : pnl < 0 ? "#27ae60" : "#2c3e50");
        Card("净值", navVal);
        Card("回撤", $"{dd}%");
    }

    private void Card(string title, string value, string color = "#2c3e50")
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#e0e0e0")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Background = Brushes.White, Padding = new Thickness(10),
            Margin = new Thickness(3),
        };
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse(color)) },
                new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeight.Bold },
            }
        };
        Cards.Children.Add(border);
    }

    private void UpdateAllocation()
    {
        AllocBar.Children.Clear();
        var w = _store.BucketWeights();
        var labels = new[] { ("A", "红利逆向"), ("B", "成长"), ("C", "热点周期"), ("D", "弹药库") };
        var sb = new System.Text.StringBuilder();
        foreach (var (b, name) in labels)
        {
            var pct = w.GetValueOrDefault(b);
            if (pct <= 0.0001) continue;
            var seg = new Border
            {
                Width = pct * 560,
                Background = new SolidColorBrush(Color.Parse(BucketColors.ColorOf(b))),
            };
            AllocBar.Children.Add(seg);
            sb.Append($"{b} {name} {pct * 100:F1}%   ");
        }
        AllocText.Text = sb.Length == 0 ? "暂无持仓" : sb.ToString();
    }

    private void DrawNav()
    {
        NavCanvas.Children.Clear();
        var nav = _store.LoadNav();
        var vals = nav.Count > 0 && nav[0].ContainsKey("nav")
            ? nav.Select(r => double.TryParse(r.GetValueOrDefault("nav", "1"), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 1.0).ToList()
            : new List<double>();
        // 数据不足时显示引导提示而非空白框（至少 2 个交易日才成图）
        NavHint.IsVisible = vals.Count < 2;
        if (vals.Count < 2) return;
        double w = NavCanvas.Bounds.Width > 0 ? NavCanvas.Bounds.Width : 400;
        double h = NavCanvas.Bounds.Height > 0 ? NavCanvas.Bounds.Height : 150;
        double min = vals.Min(), max = vals.Max();
        double range = max - min < 1e-6 ? 1 : max - min;
        var pts = new List<Point>();
        for (int i = 0; i < vals.Count; i++)
            pts.Add(new Point(i * w / (vals.Count - 1), h - (vals[i] - min) / range * (h - 10) - 5));
        NavCanvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = new SolidColorBrush(Color.Parse("#2980b9")),
            StrokeThickness = 2,
        });
    }

    // ── 行情刷新（盘中恒刷；盘外按最近定盘节流） ──

    private async Task AutoTickAsync()
    {
        if (!MarketTimes.ShouldRefreshQuotes(_lastFetch)) return;
        // DispatcherTimer.Tick 里丢弃的 async 任务异常默认不进 UnhandledException，
        // 但同步段抛出会直接崩进程（WinExe 无控制台=静默退出），这里整体兜底
        try { await RefreshQuotesAsync(); }
        catch (Exception ex) { _status($"⚠️ 行情刷新异常: {ex.Message}"); }
    }

    public async Task RefreshQuotesAsync()
    {
        if (_fetching) return;
        // codes 从流水全量代码取（含清仓股，便于名称补全）—— 与 Python 口径一致
        var codes = _store.ReadTrades().Select(t => t.Code).Where(c => c.Length > 0).Distinct().ToList();
        if (codes.Count == 0) return;
        _fetching = true;
        try
        {
            _status("⏳ 拉取实时行情…");
            var map = await _app.Quotes.FetchAsync(codes);
            if (map.Count == 0) { _status("⚠️ 行情获取失败（腾讯/新浪均无返回）"); return; }
            _lastFetch = DateTime.Now;

            foreach (var p in _positions)
            {
                if (map.TryGetValue(DataStore.NormalizeCode(p.Code), out var q))
                {
                    if (q.Price > 0) p.CurrentPrice = (double)q.Price;
                    if (string.IsNullOrEmpty(p.Name) && !string.IsNullOrEmpty(q.Name))
                        p.Name = q.Name;
                }
            }
            // 直接重赋 ItemsSource 会因选中索引越界崩进程（详见 SetItemsSafe 注释）
            PosGrid.SetItemsSafe(_positions);
            UpdateCards();

            // 名称自动补全：流水里名称空白的行用行情名称回写 CSV
            var filled = _store.FillTradeNames(map.ToDictionary(kv => kv.Key, kv => kv.Value.Name));
            if (filled > 0) LoadTrades();

            _status($"✅ 行情已更新（{map.Count} 只）");
        }
        finally { _fetching = false; }
    }

    // ── 交易操作 ──

    private async Task OnAddTradeAsync()
    {
        if (VisualRoot is not Window owner) return;
        var dlg = new TradeDialog(_app.Quotes) { Title = "➕ 记一笔交易" };
        if (await dlg.ShowAsync(owner) && dlg.GetRecord() is { } rec)
        {
            if (rec.Direction == "卖出" && rec.Shares > _store.SharesOf(rec.Code))
            {
                if (!await MessageBox.Ask(owner, "负持仓确认",
                    $"{rec.Code} 当前净持仓 {_store.SharesOf(rec.Code):F0} 股，本次卖出 {rec.Shares:F0} 股，将出现负持仓。仍要保存？")) return;
            }
            _store.AppendTrade(rec); AfterTradeChanged();
        }
    }

    private async Task QuickTradeAsync(string direction, double defaultShares)
    {
        if (VisualRoot is not Window owner) return;
        if (PosGrid.SelectedItem is not Position pos) { _status("请先选中一行持仓"); return; }
        var dlg = new TradeDialog(_app.Quotes) { Title = direction == "买入" ? "📈 加仓" : "📉 减仓" };
        dlg.Prefill(new Trade
        {
            Date = DateTime.Today.ToString("yyyy-MM-dd"),
            Direction = direction, Bucket = pos.Bucket, Code = pos.Code, Name = pos.Name,
            Price = pos.CurrentPrice, Shares = defaultShares,
            Amount = pos.CurrentPrice * defaultShares,
            Reason = direction == "买入" ? "右键加仓" : "右键减仓",
        });
        if (await dlg.ShowAsync(owner) && dlg.GetRecord() is { } rec)
        {
            if (rec.Direction == "卖出" && rec.Shares > _store.SharesOf(rec.Code))
                if (!await MessageBox.Ask(owner, "负持仓确认",
                    $"卖出 {rec.Shares:F0} 股超过持仓 {_store.SharesOf(rec.Code):F0} 股，仍要保存？")) return;
            _store.AppendTrade(rec); AfterTradeChanged();
        }
    }

    private async Task QuickClearAsync()
    {
        if (VisualRoot is not Window owner) return;
        if (PosGrid.SelectedItem is not Position pos || pos.Shares <= 0) { _status("请先选中一行持仓"); return; }
        if (!await MessageBox.Ask(owner, "清仓确认",
            $"确定清仓 {pos.Code} {pos.Name}？\n当前持仓 {pos.Shares:F0} 股，将追加一笔卖出记录归零。")) return;
        var dlg = new TradeDialog(_app.Quotes) { Title = "🗑 清仓" };
        dlg.Prefill(new Trade
        {
            Date = DateTime.Today.ToString("yyyy-MM-dd"), Direction = "卖出",
            Bucket = pos.Bucket, Code = pos.Code, Name = pos.Name,
            Price = pos.CurrentPrice, Shares = pos.Shares,
            Amount = pos.CurrentPrice * pos.Shares, Reason = "右键清仓",
        });
        if (await dlg.ShowAsync(owner) && dlg.GetRecord() is { } rec)
        {
            if (rec.Shares > _store.SharesOf(rec.Code))
                if (!await MessageBox.Ask(owner, "负持仓确认",
                    $"清仓 {rec.Shares:F0} 股超过持仓 {_store.SharesOf(rec.Code):F0} 股，仍要保存？")) return;
            _store.AppendTrade(rec); AfterTradeChanged();
        }
    }

    private double ReduceShares()
    {
        if (PosGrid.SelectedItem is not Position pos) return 100;
        var half = Math.Max(100, (int)(pos.Shares / 2 / 100) * 100);
        return Math.Min(half, (int)pos.Shares);
    }

    private async Task OnEditTradeAsync()
    {
        if (VisualRoot is not Window owner) return;
        if (TradesGrid.SelectedItem is not Trade t) { _status("请先选中一行"); return; }
        var dlg = new TradeDialog(_app.Quotes) { Title = "编辑交易" };
        dlg.Prefill(t);
        if (await dlg.ShowAsync(owner) && dlg.GetRecord() is { } rec)
        {
            var idx = _trades.IndexOf(t);
            _store.UpdateTradeAt(idx, rec); AfterTradeChanged();
        }
    }

    private async Task OnDeleteTradeAsync()
    {
        if (VisualRoot is not Window owner) return;
        if (TradesGrid.SelectedItem is not Trade t) { _status("请先选中一行"); return; }
        if (!await MessageBox.Ask(owner, "删除确认",
            $"确定删除这条交易？\n{t.Date} {t.Direction} {t.Code} {t.Name} {t.Shares:F0} 股")) return;
        var idx = _trades.IndexOf(t);
        _store.DeleteTradeAt(idx); AfterTradeChanged();
    }

    // ── 流水表内联编辑（对应 Python _on_trade_item_changed） ──

    private async void OnTradeCellEditEnding(DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.EditingElement is not TextBox tb) return;
        var idx = e.Row.Index;
        if (idx < 0 || idx >= _trades.Count) return;
        if ((e.Column.Header as string) is not { } header) return;
        var prop = TradeCols.FirstOrDefault(c => c.Header == header).Prop;
        if (string.IsNullOrEmpty(prop)) return;

        var row = _trades[idx];
        var newVal = tb.Text?.Trim() ?? "";
        var oldVal = GetTradeField(row, prop);
        if (newVal == oldVal) return;

        var (err, norm) = ValidateNormalizeTradeField(header, newVal);
        if (err != null)
        {
            e.Cancel = true; // 阻止绑定写回
            tb.Text = oldVal;
            if (VisualRoot is Window owner)
                await MessageBox.Show(owner, "修改无效", $"【{header}】{err}");
            return;
        }

        e.Cancel = true; // 统一手动写回，避免绑定二次赋值
        tb.Text = norm;
        SetTradeField(row, prop, norm);
        if (_store.UpdateTradeAt(idx, row))
        {
            AfterTradeChanged(); // 持仓/卡片/图表联动重算
        }
        else
        {
            SetTradeField(row, prop, oldVal); // 写文件失败回滚
            tb.Text = oldVal;
            _status("⚠️ 写入 live_trade_log.csv 失败，请检查文件是否被占用");
        }
    }

    private static string GetTradeField(Trade t, string prop) => prop switch
    {
        nameof(Trade.Date) => t.Date,
        nameof(Trade.Direction) => t.Direction,
        nameof(Trade.Bucket) => t.Bucket,
        nameof(Trade.Code) => t.Code,
        nameof(Trade.Name) => t.Name,
        nameof(Trade.Industry) => t.Industry,
        nameof(Trade.Price) => t.Price.ToString(CultureInfo.InvariantCulture),
        nameof(Trade.Shares) => t.Shares.ToString(CultureInfo.InvariantCulture),
        nameof(Trade.Amount) => t.Amount.ToString(CultureInfo.InvariantCulture),
        nameof(Trade.RuleId) => t.RuleId,
        _ => t.Reason,
    };

    private static void SetTradeField(Trade t, string prop, string val)
    {
        switch (prop)
        {
            case nameof(Trade.Date): t.Date = val; break;
            case nameof(Trade.Direction): t.Direction = val; break;
            case nameof(Trade.Bucket): t.Bucket = val; break;
            case nameof(Trade.Code): t.Code = val; break;
            case nameof(Trade.Name): t.Name = val; break;
            case nameof(Trade.Industry): t.Industry = val; break;
            case nameof(Trade.Price): t.Price = double.Parse(val, CultureInfo.InvariantCulture); break;
            case nameof(Trade.Shares): t.Shares = double.Parse(val, CultureInfo.InvariantCulture); break;
            case nameof(Trade.Amount): t.Amount = double.Parse(val, CultureInfo.InvariantCulture); break;
            case nameof(Trade.RuleId): t.RuleId = val; break;
            default: t.Reason = val; break;
        }
    }

    /// <summary>校验 + 归一化（对应 Python _validate_trade_field / _normalize_trade_field）。</summary>
    private static (string? Error, string Norm) ValidateNormalizeTradeField(string field, string val)
    {
        var required = field is "日期" or "方向" or "桶" or "代码" or "价格" or "股数" or "金额";
        if (required && string.IsNullOrEmpty(val)) return ("不能为空", val);
        switch (field)
        {
            case "代码":
                if (!(val.Length is 5 or 6 && val.All(char.IsDigit)))
                    return ("必须是 5-6 位数字（5 位自动补零，如 600519）", val);
                return (null, val.PadLeft(6, '0'));
            case "方向":
                if (val is not ("买入" or "卖出")) return ("只能是 买入 / 卖出", val);
                return (null, val);
            case "桶":
                var up = val.ToUpperInvariant();
                if (up is not ("A" or "B" or "C" or "D")) return ("只能是 A / B / C / D", val);
                return (null, up);
            case "日期":
                if (!DateTime.TryParseExact(val, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return ("格式必须是 YYYY-MM-DD（如 2026-08-20）", val);
                return (null, val);
            case "价格":
            case "股数":
            case "金额":
                if (!double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                    return ($"必须是数字（当前输入：{val}）", val);
                if (num <= 0) return ("必须大于 0", val);
                var fmt = field switch { "价格" => "F3", "股数" => "F0", _ => "F2" };
                return (null, num.ToString(fmt, CultureInfo.InvariantCulture));
            default:
                return (null, val);
        }
    }

    private void AfterTradeChanged() => Load();
}
