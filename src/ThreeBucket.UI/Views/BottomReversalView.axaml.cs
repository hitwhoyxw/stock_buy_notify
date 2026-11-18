using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using ThreeBucket.Core.Services.BottomReversal;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

/// <summary>
/// 顶层「底部反转」页：只读展示 T10（S27 增强版底部反转 V1.2）的最新输出 data/bottom_reversal/：
/// br_trade_latest.csv（全部股票的阶段/信号/止损/压力位/R·R/分组）、br_top20_latest.csv、details/&lt;日期&gt;/&lt;代码&gt;.md、report_latest.md。
/// 不改任何数据；“运行扫描”直接调内置任务 T10（与任务面板/定时器相同）。t40：「📈 跟踪表现」子页展示 tracking/br_track_latest.csv（B1/B2/B3 信号 30 日跟踪）。
/// </summary>
public partial class BottomReversalView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private List<Row> _all = new();
    private List<string> _top20 = new();
    private string _barDate = "";
    private bool _loadingCombo, _busy;

    private string OutDir => Path.Combine(_app.DataDir, "bottom_reversal");

    public sealed class Row
    {
        public int? Rank { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Group { get; init; } = "";
        public int? Total { get; init; }
        public string Stage { get; init; } = "";
        public string Signal { get; init; } = "";
        public string SignalDate { get; init; } = "";
        public string B1Grade { get; init; } = "";
        public double? Platform { get; init; }
        public double? Close { get; init; }
        public double? Ma20Dev { get; init; }
        public double? Entry { get; init; }
        public double? Stop { get; init; }
        public double? Resistance { get; init; }
        public double? RewardPct { get; init; }
        public double? RiskPct { get; init; }
        public double? Rr { get; init; }
        public string Tags { get; init; } = "";
        public string OpStatus { get; init; } = "";
        public string OpNote { get; init; } = "";
        public Dictionary<string, string> Raw { get; init; } = new();
    }

    private static readonly (string Key, string Label)[] GroupItems =
    [
        ("A", "A组：趋势强且介入位置合理"),
        ("B", "B组：趋势已出现，等待确认或回踩"),
        ("C", "C组：评分较高但涨幅过大，暂不追"),
        ("TOP", "趋势评分前20（V1 评分）"),
        ("ALL", "全部已评分股票"),
    ];

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public BottomReversalView() : this(new AppState(), _ => { }) { }

    public BottomReversalView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app;
        _status = status;
        RefreshBtn.Click += (_, _) => Load();
        RunBtn.Click += async (_, _) => await RunScanAsync();
        ReportBtn.Click += (_, _) => ShowFile(Path.Combine(OutDir, "report_latest.md"), "当日日报 report_latest.md");
        Bt12Btn.Click += (_, _) => ShowFile(Path.Combine(OutDir, "backtest_v12", "bt12_report.md"), "V1.2 回测报告 bt12_report.md（运行：ThreeBucket.Cli --br backtest12）");
        OpenDirBtn.Click += (_, _) => OpenDir();
        GroupCombo.SelectionChanged += (_, _) => { if (!_loadingCombo) ApplyFilter(); };
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        Grid.SelectionChanged += (_, _) => { if (Grid.SelectedItem is Row r) ShowDetail(r); };
        // t40：跟踪表现
        TrackTypeCombo.ItemsSource = TrackTypes.Select(x => x == "ALL" ? "信号：全部" : "信号：" + x).ToList(); TrackTypeCombo.SelectedIndex = 0;
        TrackStatusCombo.ItemsSource = TrackStatuses.Select(x => x == "ALL" ? "状态：全部" : "状态：" + x).ToList(); TrackStatusCombo.SelectedIndex = 0;
        TrackGroupCombo.ItemsSource = TrackGroups.Select(x => x == "ALL" ? "组：全部" : x == "" ? "组：未分组" : "组：" + x).ToList(); TrackGroupCombo.SelectedIndex = 0;
        TrackTypeCombo.SelectionChanged += (_, _) => ApplyTrackFilter();
        TrackStatusCombo.SelectionChanged += (_, _) => ApplyTrackFilter();
        TrackGroupCombo.SelectionChanged += (_, _) => ApplyTrackFilter();
        TrackDateCombo.SelectionChanged += (_, _) => { if (!_loadingTrack) ApplyTrackFilter(); };
        TrackSearch.TextChanged += (_, _) => ApplyTrackFilter();
        TrackGrid.SelectionChanged += (_, _) => { if (TrackGrid.SelectedItem is TrackRow r) ShowTrackDetail(r); };
        TrackRunBtn.Click += async (_, _) => await RunTrackAsync();
        TrackSummaryBtn.Click += (_, _) => ShowTrackFile(Path.Combine(TrackDir, "br_track_summary_latest.md"));
    }

    // ── t40：信号跟踪表现（data/bottom_reversal/tracking/br_track_latest.csv）──
    public sealed class TrackRow
    {
        public string Code { get; init; } = ""; public string Name { get; init; } = ""; public string Signal { get; init; } = ""; public string SignalDate { get; init; } = "";
        public string Group { get; init; } = ""; public string Status { get; init; } = ""; public int? Days { get; init; } public string Exec { get; init; } = "";
        public double? SignalClose { get; init; } public double? Plan { get; init; } public double? Stop { get; init; } public double? Cur { get; init; }
        public double? Ret { get; init; } public double? RetPlan { get; init; } public double? MaxGain { get; init; } public double? MaxDd { get; init; }
        public string StopDate { get; init; } = ""; public double? D5 { get; init; } public double? D10 { get; init; } public double? D20 { get; init; } public string Note { get; init; } = "";
        public Dictionary<string, string> Raw { get; init; } = new();
    }
    private static readonly string[] TrackTypes = ["ALL", "B1", "B2", "B3"];
    private static readonly string[] TrackStatuses = ["ALL", "跟踪中", "已到期", "触及止损", "数据缺失"];
    private static readonly string[] TrackGroups = ["ALL", "A", "B", "C", ""];
    private List<TrackRow> _track = new();
    private List<string> _trackDates = new();
    private bool _loadingTrack, _trackBusy;
    private string TrackDir => Path.Combine(OutDir, "tracking");
    private static double? Pct100(Dictionary<string, string> d, string k) => Num(d, k) is double v ? v * 100 : null;

    private void LoadTrack()
    {
        try
        {
            var path = Path.Combine(TrackDir, "br_track_latest.csv");
            _track = BrReport.ReadCsv(path).Select(d => new TrackRow
            {
                Code = Get(d, "code"), Name = Get(d, "name"), Signal = Get(d, "signal"), SignalDate = Get(d, "signal_date"), Group = Get(d, "group"), Status = Get(d, "status"),
                Days = int.TryParse(Get(d, "days"), out var dd) ? dd : null, Exec = Get(d, "exec_status"),
                SignalClose = Num(d, "signal_close"), Plan = Num(d, "plan_price"), Stop = Num(d, "stop"), Cur = Num(d, "cur_close_raw"),
                Ret = Pct100(d, "ret_signal"), RetPlan = Pct100(d, "ret_plan"), MaxGain = Pct100(d, "max_gain"), MaxDd = Pct100(d, "max_drawdown"),
                StopDate = Get(d, "stop_date"), D5 = Pct100(d, "ret_d5"), D10 = Pct100(d, "ret_d10"), D20 = Pct100(d, "ret_d20"), Note = Get(d, "note"), Raw = d,
            }).ToList();
            _loadingTrack = true;
            var prevDate = TrackDateCombo.SelectedItem as string;
            _trackDates = _track.Select(r => r.SignalDate).Distinct().OrderByDescending(x => x, StringComparer.Ordinal).ToList();
            var items = new List<string> { "信号日：全部" }.Concat(_trackDates.Select(x => "信号日：" + x)).ToList();
            TrackDateCombo.ItemsSource = items;
            TrackDateCombo.SelectedIndex = prevDate is not null && items.IndexOf(prevDate) >= 0 ? items.IndexOf(prevDate) : 0;
            _loadingTrack = false;
            ApplyTrackFilter();
        }
        catch (Exception ex) { _loadingTrack = false; TrackStats.Text = "读取跟踪表失败：" + ex.Message; }
    }

    private void ApplyTrackFilter()
    {
        if (TrackStats is null) return;
        IEnumerable<TrackRow> q = _track;
        var ty = TrackTypes[Math.Max(0, TrackTypeCombo.SelectedIndex)]; if (ty != "ALL") q = q.Where(r => r.Signal == ty);
        var st = TrackStatuses[Math.Max(0, TrackStatusCombo.SelectedIndex)]; if (st != "ALL") q = q.Where(r => r.Status == st);
        var gr = TrackGroups[Math.Max(0, TrackGroupCombo.SelectedIndex)]; if (gr != "ALL") q = q.Where(r => r.Group == gr);
        var di = TrackDateCombo.SelectedIndex; if (di > 0 && di - 1 < _trackDates.Count) { var dt = _trackDates[di - 1]; q = q.Where(r => r.SignalDate == dt); }
        var text = TrackSearch.Text?.Trim() ?? ""; if (text.Length > 0) q = q.Where(r => (r.Code + r.Name).Contains(text, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        TrackGrid.SetItemsSafe(new ObservableCollection<TrackRow>(list));
        var withRet = list.Where(r => r.Ret is not null).ToList();
        var asOf = _track.Select(r => Get(r.Raw, "as_of")).DefaultIfEmpty("").Max() ?? "";
        TrackStats.Text = _track.Count == 0
            ? "尚无跟踪数据：点「▶ 更新跟踪」生成（或运行一次 T10 扫描 / ThreeBucket.Cli --br track）。"
            : $"截至 {asOf}    ·    当前列表 {list.Count} 个信号（跟踪中 {list.Count(r => r.Status == "跟踪中")} / 已到期 {list.Count(r => r.Status == "已到期")} / 触及止损 {list.Count(r => r.Status == "触及止损")}）" +
              (withRet.Count > 0 ? $"    ·    有收益 {withRet.Count} 个：胜率 {withRet.Count(r => r.Ret > 0) * 100.0 / withRet.Count:F1}%，平均收益 {withRet.Average(r => r.Ret!.Value):+0.00;-0.00}%，平均最大涨幅 {withRet.Average(r => r.MaxGain ?? 0):+0.00;-0.00}%，平均最大回撤 {withRet.Average(r => r.MaxDd ?? 0):+0.00;-0.00}%" : "    ·    尚无信号日之后的K线") +
              $"    ·    源文件：{Path.Combine(TrackDir, "br_track_latest.csv")}";
        if (list.Count > 0) TrackGrid.SelectedIndex = 0;
        else MarkdownRenderer.Render(TrackDetail, "（列表为空）");
    }

    private void ShowTrackDetail(TrackRow r)
    {
        var d = r.Raw;
        string P(string k) => Num(d, k) is double v ? (v * 100).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%" : "—";
        var lines = new List<string>
        {
            $"# {r.Code} {r.Name} · {r.Signal}（{r.SignalDate}）",
            $"- 状态：**{r.Status}**；已跟踪 {Get(d, "days")} / 30 个交易日（截至 {Get(d, "as_of")}{(Get(d, "end_date").Length > 0 ? "，到期日 " + Get(d, "end_date") : "")}）",
            $"- 信号日：组 {(r.Group.Length > 0 ? r.Group : "未分组")}；阶段 {Get(d, "stage")}；趋势分 {Get(d, "total")}；操作状态 {Get(d, "op_status")}",
            $"- T+1 检查：{r.Exec}{(Get(d, "exec_source").Length > 0 ? "（" + Get(d, "exec_source") + "）" : "")}",
            $"- 信号日收盘 {Get(d, "signal_close")}；平台 {Get(d, "platform")}；计划价 {Get(d, "plan_price")}（{Get(d, "plan_basis")}）；止损 {Get(d, "stop")}",
            $"- 现价（不复权）{Get(d, "cur_close_raw")}；按信号日口径 {Get(d, "cur_close_adj")}（除权调整系数 {Get(d, "adj_factor")}）",
            $"- 收益：相对信号收盘 {P("ret_signal")}；相对计划价 {P("ret_plan")}",
            $"- 期间最大涨幅 {P("max_gain")}（{Get(d, "max_gain_date")}）；最大回撤 {P("max_drawdown")}（{Get(d, "max_dd_date")}）",
            $"- 触及止损：{Get(d, "stop_hit")}{(Get(d, "stop_date").Length > 0 ? "（" + Get(d, "stop_date") + "）" : "")}",
            $"- 第 5 / 10 / 20 日收益：{P("ret_d5")} / {P("ret_d10")} / {P("ret_d20")}（未到的为 —）",
        };
        if (r.Note.Length > 0) lines.Add($"- 备注：{r.Note}");
        MarkdownRenderer.Render(TrackDetail, string.Join("\n", lines));
    }

    private void ShowTrackFile(string path)
    {
        if (!File.Exists(path)) { MarkdownRenderer.Render(TrackDetail, $"（文件不存在：{path}；先点「▶ 更新跟踪」）"); return; }
        try { MarkdownRenderer.Render(TrackDetail, $"> {path}\n" + File.ReadAllText(path, new System.Text.UTF8Encoding(false)).TrimStart('\uFEFF')); }
        catch (Exception ex) { MarkdownRenderer.Render(TrackDetail, "读取失败：" + ex.Message); }
    }

    private async Task RunTrackAsync()
    {
        if (_trackBusy) return;
        _trackBusy = true; TrackRunBtn.IsEnabled = false;
        _status("S27 信号跟踪更新中…");
        try
        {
            var dataDir = _app.DataDir;
            var r = await Task.Run(() => BrTrack.RunAsync(BrConfig.Load(dataDir), dataDir, msg => Dispatcher.UIThread.Post(() => _status(msg))));
            _status("S27 信号跟踪完成：" + r.Summary);
        }
        catch (Exception ex) { _status("S27 信号跟踪失败：" + ex.Message); }
        finally { _trackBusy = false; TrackRunBtn.IsEnabled = true; LoadTrack(); }
    }

    public void OnShown() => Load();

    private static double? Num(Dictionary<string, string> d, string k)
    {
        if (!d.TryGetValue(k, out var s) || string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('%');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var s) ? s : "";

    private void Load()
    {
        try
        {
            var path = Path.Combine(OutDir, "br_trade_latest.csv");
            var rows = BrReport.ReadCsv(path);
            _all = rows.Select(d => new Row
            {
                Rank = int.TryParse(Get(d, "group_rank"), out var gr) ? gr : null,
                Code = Get(d, "code"), Name = Get(d, "name"), Group = Get(d, "group"),
                Total = int.TryParse(Get(d, "total"), out var tt) ? tt : null,
                Stage = Get(d, "stage"), Signal = Get(d, "signal") is "无" ? "" : Get(d, "signal"), SignalDate = Get(d, "signal_date"), B1Grade = Get(d, "b1_grade"),
                Platform = Num(d, "platform"), Close = Num(d, "close"), Ma20Dev = Num(d, "ma20_dev"),
                Entry = Num(d, "entry"), Stop = Num(d, "stop"), Resistance = Num(d, "resistance"),
                RewardPct = Num(d, "reward_pct"), RiskPct = Num(d, "risk_pct") is double rp ? Math.Abs(rp) : null, Rr = Num(d, "rr"),
                Tags = Get(d, "risk_tags"), OpStatus = Get(d, "op_status"), OpNote = Get(d, "op_note"), Raw = d,
            }).ToList();
            _top20 = BrReport.ReadCsv(Path.Combine(OutDir, "br_top20_latest.csv")).Select(d => Get(d, "code")).Where(c => c.Length > 0).ToList();
            _barDate = _all.Select(r => Get(r.Raw, "bar_date")).Where(s => s.Length > 0).DefaultIfEmpty("").Max() ?? "";

            var meta = LatestMeta();
            var mtime = File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm") : "";
            DateText.Text = _all.Count == 0
                ? "尚无 V1.2 输出：点「▶ 运行扫描(T10)」生成（约 1 分钟；15:05 前运行用上一交易日收盘数据）"
                : $"数据日期（日K收盘）：{_barDate}    ·    文件生成：{mtime}{meta}";

            _loadingCombo = true;
            var prev = GroupCombo.SelectedIndex;
            GroupCombo.ItemsSource = GroupItems.Select(g => g.Key switch
            {
                "A" or "B" or "C" => $"{g.Label}（{_all.Count(r => r.Group == g.Key)}）",
                "TOP" => $"{g.Label}（{_top20.Count}）",
                _ => $"{g.Label}（{_all.Count}）",
            }).ToList();
            GroupCombo.SelectedIndex = prev >= 0 ? prev : 0;
            _loadingCombo = false;
            ApplyFilter();
            if (_all.Count == 0) DetailPanelText("（暂无数据）");
            LoadTrack();
        }
        catch (Exception ex)
        {
            _loadingCombo = false;
            DateText.Text = "读取失败：" + ex.Message;
        }
    }

    private string LatestMeta()
    {
        try
        {
            var f = Directory.Exists(OutDir) ? Directory.GetFiles(OutDir, "scan_meta_*.json").OrderBy(x => x, StringComparer.Ordinal).LastOrDefault() : null;
            if (f is null) return "";
            var j = JsonNode.Parse(File.ReadAllText(f));
            var env = j?["market_label"]?.ToString() ?? "";
            var ver = j?["model_version"]?.ToString() ?? "";
            var scored = j?["scored"]?.ToString() ?? "";
            return (scored.Length > 0 ? $"    ·    评分 {scored} 只" : "") + (env.Length > 0 ? $"    ·    市场环境：{env}" : "") + (j?["intraday_run"]?.GetValue<bool>() == true ? $"    ·    运行于 {j?["run_at"]}（15:05 前运行，用上一交易日收盘）" : "") + (ver.Length > 0 ? $"    ·    模型 {ver}" : "");
        }
        catch { return ""; }
    }

    private void ApplyFilter()
    {
        var key = GroupCombo.SelectedIndex >= 0 && GroupCombo.SelectedIndex < GroupItems.Length ? GroupItems[GroupCombo.SelectedIndex].Key : "A";
        IEnumerable<Row> q = key switch
        {
            "A" or "B" or "C" => _all.Where(r => r.Group == key).OrderBy(r => r.Rank ?? int.MaxValue),
            "TOP" => _top20.Select(c => _all.FirstOrDefault(r => r.Code == c)).Where(r => r is not null).Select(r => r!),
            _ => _all.OrderByDescending(r => r.Total ?? -1),
        };
        var text = SearchBox.Text?.Trim() ?? "";
        if (text.Length > 0) q = q.Where(r => (r.Code + r.Name).Contains(text, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        Grid.SetItemsSafe(new ObservableCollection<Row>(list));
        var a = _all.Count(r => r.Group == "A"); var b = _all.Count(r => r.Group == "B"); var c = _all.Count(r => r.Group == "C");
        Stats.Text = $"当前列表 {list.Count} 只    ·    A组 {a} / B组 {b} / C组 {c}    ·    源文件：{Path.Combine(OutDir, "br_trade_latest.csv")}";
        if (list.Count > 0) Grid.SelectedIndex = 0;
        else DetailPanelText(key == "A" ? "A组今天为空：没有同时满足“B2/B3 信号 + T+1 规则下可介入 + R/R 合格”的股票。可切到 B 组看等待确认的股票。" : "（列表为空）");
    }

    private void ShowDetail(Row r)
    {
        var day = Get(r.Raw, "bar_date");
        var md = Path.Combine(OutDir, "details", day, r.Code + ".md");
        if (File.Exists(md)) { ShowFile(md, null); return; }
        // 没有生成详情文件的股票（只为前20和各组前20生成）：用 br_trade 行拼一份摘要
        var d = r.Raw;
        var lines = new List<string>
        {
            $"# {r.Code} {r.Name}（{day}）",
            $"> 未生成完整个股报告（只为评分前20和各组前20生成），以下为 br_trade_latest.csv 中的交易时机字段。",
            $"## 交易时机（V1.2）",
            $"- 分组：{(r.Group.Length > 0 ? r.Group + "组" : "未分组")}；趋势分 {Get(d, "total")}（{Get(d, "rating")}）；阶段：{r.Stage}",
            $"- 当日信号：{Get(d, "signal")} {r.SignalDate}；最近信号：{Get(d, "recent_signal")} {Get(d, "recent_exec")}",
            $"- 首次突破日：{Get(d, "first_breakout_date")}；平台：{Get(d, "platform")}；收盘：{Get(d, "close")}；MA20乖离：{Get(d, "ma20_dev")}",
            $"- 计划价（模拟）：{Get(d, "entry")}（{Get(d, "entry_basis")}）",
            $"- 止损：{Get(d, "stop")}（{Get(d, "stop_basis")}）；潜在风险 {Get(d, "risk_pct")}",
            $"- 压力位：{Get(d, "resistance")}（{Get(d, "resistance_basis")}）；潜在收益 {Get(d, "reward_pct")}",
            $"- 风险收益比：{Get(d, "rr")}（{Get(d, "rr_label")}）",
            $"- 风险标签：{(r.Tags.Length > 0 ? r.Tags : "无")}",
            $"- B1 分级（V1.2）：{(r.B1Grade.Length > 0 ? r.B1Grade + "；" + Get(d, "b1_grade_note") : "—")}",
            $"- 操作状态：{r.OpStatus}；{r.OpNote}",
            $"- 仓位：{Get(d, "position")}",
        };
        MarkdownRenderer.Render(DetailPanel, string.Join("\n", lines));
    }

    private void ShowFile(string path, string? title)
    {
        if (!File.Exists(path)) { DetailPanelText($"（文件不存在：{path}）"); return; }
        try
        {
            var text = File.ReadAllText(path, new System.Text.UTF8Encoding(false)).TrimStart('\uFEFF');
            MarkdownRenderer.Render(DetailPanel, (title is null ? "" : $"> {path}\n") + text);
        }
        catch (Exception ex) { DetailPanelText("读取失败：" + ex.Message); }
    }

    private void DetailPanelText(string s) => MarkdownRenderer.Render(DetailPanel, s);

    private void OpenDir()
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutDir}\"") { UseShellExecute = true });
            else _status("输出目录：" + OutDir);
        }
        catch (Exception ex) { _status("打开目录失败：" + ex.Message); }
    }

    private async Task RunScanAsync()
    {
        if (_busy) return;
        if (!_app.BuiltinTasks.TryGetValue("T10", out var task)) { _status("内置任务 T10 未注册"); return; }
        _busy = true; RunBtn.IsEnabled = false; RefreshBtn.IsEnabled = false;
        _status("T10 底部反转扫描运行中…（约 1 分钟）");
        try
        {
            var r = await Task.Run(() => task.RunAsync(msg => Dispatcher.UIThread.Post(() => _status(msg))));
            _status(r.Ok ? $"T10 完成：{r.Summary}" : $"T10 失败：{r.Summary}");
        }
        catch (Exception ex) { _status("T10 失败：" + ex.Message); }
        finally { _busy = false; RunBtn.IsEnabled = true; RefreshBtn.IsEnabled = true; Load(); }
    }
}
