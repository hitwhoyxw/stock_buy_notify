using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class DashboardView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private bool _running;

    private record TaskInfo(string Key, string Name, string Script, string Description, string Schedule, bool NeedsLlm, string DefaultArgs);

    private static readonly Dictionary<string, TaskInfo> Tasks = new()
    {
        ["T1"] = new("T1", "每日风控", "scripts/t1_daily_risk.py", "MA择时、仓位计算、风控检查", "工作日 16:30", false, ""),
        ["T2"] = new("T2", "周度红利", "scripts/t2_weekly_dividend.py", "红利股息率检查", "周一 08:30", false, ""),
        ["T3"] = new("T3", "月度再平衡", "scripts/t3_monthly_rebalance.py", "组合再平衡", "每月1日", false, ""),
        ["T4"] = new("T4", "财报文本扫描", "scripts/t4_ingest.py", "财报抓取 → LLM 景气判定", "财报季", true, "--prepare"),
        ["T5"] = new("T5", "季度归因", "scripts/t5_prepare.py", "归因准备 → LLM 分析", "季末", true, ""),
        ["T6"] = new("T6", "候选池筛选", "scripts/t6_candidate_pool.py", "三桶筛选 → LLM 排序", "周一 08:30", true, "--bucket ABC --top 200"),
        ["T7"] = new("T7", "回测验证", "scripts/t7_backtest.py", "策略回测", "月度/季度", false, ""),
        ["T8"] = new("T8", "信号台账", "scripts/t8_signal_log.py", "信号记录与台账更新", "工作日 17:00", false, ""),
    };

    private class CardRefs
    {
        public Border Border = null!;
        public Button RunBtn = null!;
        public TextBox Args = null!;
        public TextBlock Status = null!;
    }
    private readonly Dictionary<string, CardRefs> _cards = new();

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public DashboardView() : this(new AppState(), _ => { }) { }

    public DashboardView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;

        DailyBtn.Click += (_, _) => _ = RunTaskAsync("T1", "");
        T6Btn.Click += (_, _) => _ = RunTaskAsync("T6", "");
        ClearLogBtn.Click += (_, _) => Log.Text = "";

        BuildCards();
    }

    public void OnShown() { /* 任务面板无需刷新 */ }

    private void BuildCards()
    {
        foreach (var (key, t) in Tasks)
        {
            var argsBox = new TextBox
            {
                Watermark = "参数（如 --bucket A）",
                Text = t.DefaultArgs,
                FontSize = 12, Margin = new Thickness(0, 4, 0, 4),
            };
            var runBtn = new Button
            {
                Content = $"▶  {key} 运行",
                Background = new SolidColorBrush(Color.Parse("#3498db")),
                Foreground = Brushes.White,
            };
            runBtn.Click += (_, _) => _ = RunTaskAsync(key, argsBox.Text);

            var status = new TextBlock { Text = "就绪", FontSize = 11, Foreground = Brushes.Gray };

            var stack = new StackPanel
            {
                Margin = new Thickness(12, 8, 12, 8), Spacing = 3,
                Children =
                {
                    new TextBlock { Text = $"{key}  {t.Name}", FontWeight = FontWeight.Bold, FontSize = 14 },
                    new TextBlock { Text = t.Description, FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"📅 {t.Schedule}", FontSize = 11, Foreground = Brushes.Gray },
                    argsBox,
                    runBtn,
                    status,
                },
            };
            if (t.NeedsLlm)
                stack.Children.Insert(1, new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#e74c3c")),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = "LLM", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeight.Bold },
                });

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#bdc3c7")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Width = 270, Margin = new Thickness(4),
                Child = stack,
            };

            Cards.Children.Add(border);
            _cards[key] = new CardRefs { Border = border, RunBtn = runBtn, Args = argsBox, Status = status };
        }
    }

    /// <summary>后台运行任务脚本，实时输出到日志区（async，不阻塞 UI 线程）。</summary>
    private async Task RunTaskAsync(string key, string argsStr)
    {
        if (_running) { _status("有任务正在运行，请等待完成"); return; }
        if (!Tasks.TryGetValue(key, out var t)) return;

        var py = string.IsNullOrWhiteSpace(_app.Config.PythonExe) ? "python" : _app.Config.PythonExe;
        var script = Path.Combine(_app.ProjectRoot, t.Script);
        if (!File.Exists(script))
        {
            _status($"脚本不存在: {t.Script}");
            return;
        }

        _running = true;
        var refs = _cards[key];
        refs.RunBtn.IsEnabled = false;
        refs.Status.Text = "运行中…";
        refs.Border.Background = new SolidColorBrush(Color.Parse("#fff3cd"));
        Log.Text += $"[{DateTime.Now:HH:mm:ss}] ===== 启动 {key} =====\n";
        _status($"运行 {key} …");

        var psi = new ProcessStartInfo
        {
            FileName = py,
            Arguments = $"\"{script}\" {argsStr}".Trim(),
            WorkingDirectory = _app.ProjectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi)!;
            // 输出回调在线程池线程触发，必须调度回 UI 线程
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    Dispatcher.UIThread.Post(() => AppendLog(e.Data));
            };
            proc.BeginOutputReadLine();
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (!string.IsNullOrEmpty(err)) AppendLog(err);
            var ok = proc.ExitCode == 0;
            refs.Status.Text = ok ? $"✓ 成功 (exit 0)" : $"✗ 失败 (exit {proc.ExitCode})";
            refs.Border.Background = new SolidColorBrush(ok ? Color.Parse("#d4edda") : Color.Parse("#f8d7da"));
            Log.Text += $"[{DateTime.Now:HH:mm:ss}] ===== [{(ok ? "✓ 成功" : "✗ 失败")}] {key} =====\n\n";
            _status($"{key} {(ok ? "成功" : "失败")}");
        }
        catch (Exception ex)
        {
            refs.Status.Text = $"✗ 异常: {ex.Message}";
            refs.Border.Background = new SolidColorBrush(Color.Parse("#f8d7da"));
            AppendLog(ex.Message);
        }
        finally
        {
            refs.RunBtn.IsEnabled = true;
            _running = false;
        }
    }

    private void AppendLog(string line)
    {
        Log.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        Log.CaretIndex = Log.Text.Length;
    }
}
