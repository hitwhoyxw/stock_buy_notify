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
/// 顶层「底部缓升」页：只读展示 T11（S28 底部反转缓升）的最新输出 data/bottom_slowrise/：
/// signals_latest.csv（§12 信号表：BUY/SELL/REDUCE/HOLD/WATCH）、report_latest.md、backtest/sr_report.md。“运行扫描”直接调内置任务 T11。
/// </summary>
public partial class SlowRiseView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private List<Row> _all = new();
    private bool _loadingCombo, _busy;
    private string OutDir => Path.Combine(_app.DataDir, "bottom_slowrise");
    private static readonly string[] Signals = ["ALL", "BUY", "SELL", "REDUCE", "HOLD", "WATCH", "KEY"];
    private static readonly string[] SignalLabels = ["全部", "BUY 可买入", "SELL 卖出", "REDUCE 减仓预警", "HOLD 持有", "WATCH 观察池", "★ 重点观察"];

    public sealed class Row
    {
        public string Signal { get; init; } = ""; public string Key { get; init; } = ""; public string Code { get; init; } = ""; public string Name { get; init; } = "";
        public double? Score { get; init; } public double? Close { get; init; } public double? Pct { get; init; } public double? Dd { get; init; } public double? Rebound { get; init; }
        public string D0Date { get; init; } = ""; public double? D0Vol { get; init; } public string MaCross { get; init; } = ""; public string MacdCross { get; init; } = "";
        public double? Er { get; init; } public double? R2 { get; init; } public double? Sigma { get; init; } public string L3 { get; init; } = "";
        public string Entry { get; init; } = ""; public double? Stop { get; init; } public string Industry { get; init; } = ""; public string PoolDate { get; init; } = ""; public string Note { get; init; } = "";
        public Dictionary<string, string> Raw { get; init; } = new();
    }

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public SlowRiseView() : this(new AppState(), _ => { }) { }

    public SlowRiseView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;
        RefreshBtn.Click += (_, _) => Load();
        RunBtn.Click += async (_, _) => await RunScanAsync();
        ReportBtn.Click += (_, _) => ShowFile(Path.Combine(OutDir, "report_latest.md"));
        BtBtn.Click += (_, _) => ShowFile(File.Exists(Path.Combine(OutDir, "backtest_v2", "sr_report.md")) ? Path.Combine(OutDir, "backtest_v2", "sr_report.md") : Path.Combine(OutDir, "backtest", "sr_report.md"));
        OpenDirBtn.Click += (_, _) => OpenDir();
        SignalCombo.SelectionChanged += (_, _) => { if (!_loadingCombo) ApplyFilter(); };
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        Grid.SelectionChanged += (_, _) => { if (Grid.SelectedItem is Row r) ShowDetail(r); };
    }

    public void OnShown() => Load();

    private static double? Num(Dictionary<string, string> d, string k)
        => d.TryGetValue(k, out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var s) ? s : "";

    private void Load()
    {
        try
        {
            var path = Path.Combine(OutDir, "signals_latest.csv");
            _all = BrReport.ReadCsv(path).Select(d => new Row
            {
                Signal = Get(d, "signal"), Key = Get(d, "key_watch").Length > 0 ? "★" : "", Code = Get(d, "code"), Name = Get(d, "name"), Score = Num(d, "score"), Close = Num(d, "close"),
                Pct = Num(d, "pct") is double p ? p * 100 : null, Dd = Num(d, "dd") is double dd ? dd * 100 : null, Rebound = Num(d, "rebound") is double rb ? rb * 100 : null,
                D0Date = Get(d, "d0_date"), D0Vol = Num(d, "d0_vol_ratio"), MaCross = Get(d, "ma_cross_date"), MacdCross = Get(d, "macd_cross_date"),
                Er = Num(d, "er20"), R2 = Num(d, "r2_20"), Sigma = Num(d, "sigma20") is double sg ? sg * 100 : null, L3 = Get(d, "l3_count"),
                Entry = Get(d, "entry_price").Split('(')[0], Stop = Num(d, "stop_price"), Industry = Get(d, "industry"), PoolDate = Get(d, "pool_enter_date"),
                Note = Get(d, "signal") == "WATCH" ? "缺：" + Get(d, "fail_reason") : (Get(d, "position_note").Length > 0 ? Get(d, "position_note") : Get(d, "reason")),
                Raw = d,
            }).ToList();
            var day = _all.Select(r => Get(r.Raw, "date")).DefaultIfEmpty("").Max() ?? "";
            var mtime = File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm") : "";
            DateText.Text = File.Exists(path)
                ? $"信号日期（日K收盘）：{day}    ·    文件生成：{mtime}{Meta(day)}"
                : "尚无输出：点「▶ 运行扫描(T11)」生成（全市场约数分钟；15:05 前运行用上一交易日收盘数据；第一次运行会回放最近 60 个交易日建立观察池与模拟持仓）";
            _loadingCombo = true;
            var prev = SignalCombo.SelectedIndex;
            SignalCombo.ItemsSource = Signals.Select((s, k) => $"{SignalLabels[k]}（{(s == "ALL" ? _all.Count : s == "KEY" ? _all.Count(r => r.Key != "") : _all.Count(r => r.Signal == s))}）").ToList();
            SignalCombo.SelectedIndex = prev >= 0 ? prev : 0;
            _loadingCombo = false;
            ApplyFilter();
        }
        catch (Exception ex) { _loadingCombo = false; DateText.Text = "读取失败：" + ex.Message; }
    }

    private string Meta(string day)
    {
        try
        {
            var f = Path.Combine(OutDir, $"scan_meta_{day}.json"); if (!File.Exists(f)) return "";
            var j = JsonNode.Parse(File.ReadAllText(f));
            return $"    ·    扫描 {j?["done"]} 只，L0 通过 {j?["l0_pass"]}    ·    大盘{(j?["market_allow"]?.GetValue<bool>() == true ? "允许开仓" : "过滤未通过（BUY 只记录）")}{(j?["market_weak"]?.GetValue<bool>() == true ? "，沪深300 弱势" : "")}    ·    ★重点观察 {j?["key_watch"]}    ·    参数 {j?["config"]}    ·    模拟权益 {j?["equity"]}";
        }
        catch { return ""; }
    }

    private void ApplyFilter()
    {
        var key = SignalCombo.SelectedIndex >= 0 && SignalCombo.SelectedIndex < Signals.Length ? Signals[SignalCombo.SelectedIndex] : "ALL";
        IEnumerable<Row> q = key == "ALL" ? _all : key == "KEY" ? _all.Where(r => r.Key != "") : _all.Where(r => r.Signal == key);
        var text = SearchBox.Text?.Trim() ?? "";
        if (text.Length > 0) q = q.Where(r => (r.Code + r.Name).Contains(text, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        Grid.SetItemsSafe(new ObservableCollection<Row>(list));
        Stats.Text = $"当前列表 {list.Count} 只    ·    " + string.Join(" / ", Signals.Skip(1).Take(5).Select(s => $"{s} {_all.Count(r => r.Signal == s)}")) + $"（★重点观察 {_all.Count(r => r.Key != "")}）" + $"    ·    源文件：{Path.Combine(OutDir, "signals_latest.csv")}";
        if (list.Count > 0) Grid.SelectedIndex = 0;
        else MarkdownRenderer.Render(DetailPanel, key == "BUY" ? "今天没有 BUY：观察池里的股票还缺条件（切到 WATCH 看“缺少条件”）。" : "（列表为空）");
    }

    private void ShowDetail(Row r)
    {
        var d = r.Raw;
        var lines = new List<string>
        {
            $"# {r.Code} {r.Name}（{Get(d, "date")}）· {r.Signal}",
            $"- 行业：{r.Industry}；收盘 {Get(d, "close")}；评分 {Get(d, "score")}",
            $"- 前期调整 DD {Pct(d, "dd")}；自低点反弹 {Pct(d, "rebound")}",
            $"- 放量启动日 D0：{r.D0Date}（放量 {Get(d, "d0_vol_ratio")} 倍）",
            $"- ④a MACD 底部金叉 T_macd：{(r.MacdCross.Length > 0 ? r.MacdCross : "未出现")}；④b 均线金叉：{(r.MaCross.Length > 0 ? r.MaCross : "未出现")}{(r.Key.Length > 0 ? "（★重点观察：等均线金叉）" : "")}",
            $"- 爬升：ER {Get(d, "er20")}，R² {Get(d, "r2_20")}，σ {Pct(d, "sigma20")}；L3 满足 {r.L3}/9",
            $"- 参考价 {Get(d, "entry_price")}；止损 {Get(d, "stop_price")}",
        };
        if (r.PoolDate.Length > 0) lines.Add($"- 进入观察池：{r.PoolDate}");
        if (Get(d, "reason").Length > 0) lines.Add($"- 原因：{Get(d, "reason")}");
        if (Get(d, "fail_reason").Length > 0) lines.Add($"- **缺少条件**：{Get(d, "fail_reason")}");
        if (Get(d, "position_note").Length > 0) lines.Add($"- 组合：{Get(d, "position_note")}");
        MarkdownRenderer.Render(DetailPanel, string.Join("\n", lines));
    }

    private static string Pct(Dictionary<string, string> d, string k) => Num(d, k) is double v ? (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%" : "";

    private void ShowFile(string path)
    {
        if (!File.Exists(path)) { MarkdownRenderer.Render(DetailPanel, $"（文件不存在：{path}）"); return; }
        try { MarkdownRenderer.Render(DetailPanel, $"> {path}\n" + File.ReadAllText(path, new System.Text.UTF8Encoding(false)).TrimStart('\uFEFF')); }
        catch (Exception ex) { MarkdownRenderer.Render(DetailPanel, "读取失败：" + ex.Message); }
    }

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
        if (!_app.BuiltinTasks.TryGetValue("T11", out var task)) { _status("内置任务 T11 未注册"); return; }
        _busy = true; RunBtn.IsEnabled = false; RefreshBtn.IsEnabled = false;
        _status("T11 底部缓升扫描运行中…（全市场约数分钟）");
        try
        {
            var r = await Task.Run(() => task.RunAsync(msg => Dispatcher.UIThread.Post(() => _status(msg))));
            _status(r.Ok ? $"T11 完成：{r.Summary}" : $"T11 失败：{r.Summary}");
        }
        catch (Exception ex) { _status("T11 失败：" + ex.Message); }
        finally { _busy = false; RunBtn.IsEnabled = true; RefreshBtn.IsEnabled = true; Load(); }
    }
}
