using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;
using ThreeBucket.UI.Dialogs;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;

    private readonly TextBox _root = new();
    private readonly TextBox _python = new();
    private readonly TextBox _data = new();
    private readonly TextBox _apiUrl = new();
    private readonly TextBox _apiKey = new() { PasswordChar = '*' };
    private readonly TextBox _model = new();
    private readonly TextBox _thsKey = new() { PasswordChar = '*' };
    private readonly CheckBox _autoRefresh = new();
    private readonly NumericUpDown _refreshInterval = new() { Minimum = 10, Maximum = 600, Increment = 5, FormatString = "F0" };
    private readonly CheckBox _schedEnabled = new();
    private readonly TimePicker _schedTime = new();
    private readonly TextBox _schedTasks = new();
    private readonly NumericUpDown _monInterval = new() { Minimum = 10, Maximum = 3600, Increment = 5, FormatString = "F0" };
    private readonly CheckBox _emailEnabled = new();
    private readonly TextBox _smtpHost = new();
    private readonly NumericUpDown _smtpPort = new() { Minimum = 1, Maximum = 65535, Value = 465, FormatString = "F0" };
    private readonly TextBox _smtpUser = new();
    private readonly TextBox _smtpPass = new() { PasswordChar = '*' };
    private readonly TextBox _smtpTo = new();
    private readonly TextBox _sbUrl = new();
    private readonly TextBox _sbKey = new() { PasswordChar = '*' };
    private readonly CheckBox _autoSync = new();

    // 提醒通知（飞书 webhook + 系统通知）
    private readonly CheckBox _larkEnabled = new();
    private readonly TextBox _larkHook = new();
    private readonly TextBox _larkSecret = new() { PasswordChar = '*' };
    private readonly CheckBox _sysNotifyEnabled = new();

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public SettingsView() : this(new AppState(), _ => { }) { }

    public SettingsView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;
        SaveBtn.Click += (_, _) => _ = SaveAsync();
        BuildForm();
        LoadValues();
    }

    public void OnShown() => LoadValues();

    // ── 表单构建 ──

    private void BuildForm()
    {
        Form.Children.Add(Group("路径配置", new Control[]
        {
            Row("项目根目录:", WithBrowse(_root, BrowseRoot)),
            Row("Python 路径:", WithBrowse(_python, BrowsePython)),
            Row("数据目录:", WithBrowse(_data, BrowseData)),
        }));

        _apiUrl.Watermark = "完整 endpoint，含 /chat/completions，如 https://api.deepseek.com/chat/completions";
        _apiKey.Watermark = "sk-...（留空=手动模式，填了才能在桥接页点「调用 LLM」）";
        Form.Children.Add(Group("LLM API（OpenAI 兼容 · 桥接页直连调用）", new Control[]
        {
            Row("API URL:", _apiUrl),
            Row("API Key:", _apiKey),
            Row("模型:", _model),
        }));

        _thsKey.Watermark = "在 fuyao.aicubes.cn 注册获取（留空=走腾讯/新浪/东财/中证免费源）";
        var thsHint = new TextBlock
        {
            Text = "同花顺（扶摇）金融数据 API 作为行情快照/历史日K/指数成分股/个股分红的主数据源，"
                 + "失败自动降级免费源。中证红利股息率始终走中证官网官方口径。保存后需重启客户端生效。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
        };
        Form.Children.Add(Group("数据源（同花顺扶摇，可选）", new Control[]
        {
            Row("API Key:", _thsKey),
            thsHint,
        }));

        _autoRefresh.Content = "切换到候选池/报告页时自动刷新数据";
        _refreshInterval.FormatString = "F0";
        Form.Children.Add(Group("自动刷新", new Control[]
        {
            _autoRefresh,
            Row("刷新间隔(秒):", _refreshInterval),
        }));

        _schedEnabled.Content = "启用定时执行（工作日）";
        _schedTasks.Watermark = "如 T1 T8";
        Form.Children.Add(Group("内置定时器（本地自动跑任务）", new Control[]
        {
            _schedEnabled,
            Row("每日运行时间:", _schedTime),
            Row("运行任务:", _schedTasks),
        }));

        _emailEnabled.Content = "策略触发时发邮件提醒";
        _smtpHost.Watermark = "如 smtp.qq.com";
        _smtpUser.Watermark = "发件邮箱账号";
        _smtpPass.Watermark = "授权码（非登录密码）";
        _smtpTo.Watermark = "收件邮箱，多个逗号分隔";
        Form.Children.Add(Group("策略监控与邮箱提醒", new Control[]
        {
            Row("盘中检查间隔(秒):", _monInterval),
            _emailEnabled,
            Row("SMTP 服务器:", _smtpHost),
            Row("端口(465=SSL):", _smtpPort),
            Row("账号:", _smtpUser),
            Row("授权码:", _smtpPass),
            Row("收件人:", _smtpTo),
        }));

        _larkEnabled.Content = "启用飞书推送（策略触发当日首次提醒时发送到群）";
        _larkHook.Watermark = "https://open.feishu.cn/open-apis/bot/v2/hook/xxxxxxxx";
        _larkSecret.Watermark = "机器人安全设置选\"签名校验\"时必填，其他方式留空";
        _sysNotifyEnabled.Content = "启用系统通知（Windows 10/11 通知中心 toast 弹窗，不必盯着 app）";
        var btnNotifyTest = new Button { Content = "🔔 发送测试提醒" };
        btnNotifyTest.Click += (_, _) => _ = TestNotifyAsync();
        var notifyBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        notifyBtns.Children.Add(btnNotifyTest);
        var notifyHint = new TextBlock
        {
            Text = "获取 webhook：飞书群 → 设置 → 群机器人 → 添加“自定义机器人” → 复制 Webhook 地址填到下面。"
                 + "安全设置三选一：①自定义关键词（填“三桶”，消息标题自带【三桶监控】）；②签名校验（把密钥填到下方签名密钥）；③IP 白名单（无需填）。"
                 + "同一策略同一股票每天最多提醒一次；系统通知仅在 Windows 桌面端生效。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
        };
        Form.Children.Add(Group("提醒通知（飞书 & 系统通知）", new Control[]
        {
            _larkEnabled,
            Row("飞书 Webhook:", _larkHook),
            Row("签名密钥:", _larkSecret),
            _sysNotifyEnabled,
            notifyBtns,
            notifyHint,
        }));

        _sbUrl.Watermark = "https://xxxxx.supabase.co";
        _sbKey.Watermark = "anon public key（Project Settings → API）";
        _autoSync.Content = "自动同步（启动后自动拉取云端较新数据；本地数据变化 30 秒后自动上传，无需手动点上传）";
        var btnTest = new Button { Content = "🔌 测试连接" };
        var btnSql = new Button { Content = "📋 复制建表 SQL" };
        var btnPush = new Button { Content = "☁️ 上传到云端", Background = new SolidColorBrush(Color.Parse("#27ae60")), Foreground = Brushes.White };
        var btnPull = new Button { Content = "⬇️ 从云端恢复", Background = new SolidColorBrush(Color.Parse("#2980b9")), Foreground = Brushes.White };
        btnTest.Click += (_, _) => _ = SyncTestAsync();
        btnSql.Click += (_, _) => CopyTableSql();
        btnPush.Click += (_, _) => _ = SyncPushAsync();
        btnPull.Click += (_, _) => _ = SyncPullAsync();
        var syncBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        syncBtns.Children.Add(btnTest);
        syncBtns.Children.Add(btnSql);
        syncBtns.Children.Add(btnPush);
        syncBtns.Children.Add(btnPull);
        var syncHint = new TextBlock
        {
            Text = "首次使用：在 supabase.com 免费建项目 → 复制建表 SQL 到 SQL Editor 执行 →"
                 + " 填入项目 URL 与 anon key → 测试连接 → 上传。同步范围：策略/交易流水/监控自选/提醒历史（含策略绑定与备注）。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
        };
        Form.Children.Add(Group("云同步（Supabase 免费层，跨平台同步）", new Control[]
        {
            Row("Supabase URL:", _sbUrl),
            Row("API Key:", _sbKey),
            _autoSync,
            syncBtns,
            syncHint,
        }));
    }

    private static Border Group(string title, Control[] children)
    {
        var sp = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#cfd8dc")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4) },
                    sp,
                },
            },
        };
    }

    private static DockPanel Row(string label, Control ctl)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var lbl = new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(lbl, Dock.Left);
        dock.Children.Add(lbl);
        dock.Children.Add(ctl);
        return dock;
    }

    private static DockPanel WithBrowse(TextBox box, Action browse)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var btn = new Button { Content = "浏览…" };
        DockPanel.SetDock(btn, Dock.Right);
        btn.Click += (_, _) => browse();
        dock.Children.Add(btn);
        dock.Children.Add(box);
        return dock;
    }

    // ── 值读写 ──

    private void LoadValues()
    {
        var c = _app.Config;
        _root.Text = c.ProjectRoot;
        _python.Text = c.PythonExe;
        _data.Text = c.DataDir;
        _apiUrl.Text = c.LlmApiUrl;
        _apiKey.Text = c.LlmApiKey;
        _model.Text = c.LlmModel;
        _thsKey.Text = c.ThsApiKey;
        _autoRefresh.IsChecked = c.AutoRefresh;
        _refreshInterval.Value = c.RefreshInterval;
        _schedEnabled.IsChecked = c.SchedulerEnabled;
        if (TimeSpan.TryParse(c.SchedulerTime, out var ts)) _schedTime.SelectedTime = ts;
        else _schedTime.SelectedTime = new TimeSpan(16, 30, 0);
        _schedTasks.Text = c.SchedulerTasksStr;
        _monInterval.Value = c.MonitorInterval;
        _emailEnabled.IsChecked = c.MonitorEmailEnabled;
        _smtpHost.Text = c.SmtpHost;
        _smtpPort.Value = c.SmtpPort;
        _smtpUser.Text = c.SmtpUser;
        _smtpPass.Text = c.SmtpPass;
        _smtpTo.Text = c.SmtpTo;
        _sbUrl.Text = c.SupabaseUrl;
        _sbKey.Text = c.SupabaseKey;
        _autoSync.IsChecked = c.AutoSync;
        _larkEnabled.IsChecked = c.NotifyLarkEnabled;
        _larkHook.Text = c.LarkWebhook;
        _larkSecret.Text = c.LarkSecret;
        _sysNotifyEnabled.IsChecked = c.NotifySystemEnabled;
    }

    private async Task SaveAsync()
    {
        var c = _app.Config;
        c.ProjectRoot = _root.Text?.Trim() ?? "";
        c.PythonExe = _python.Text?.Trim() ?? "";
        c.DataDir = string.IsNullOrWhiteSpace(_data.Text) ? Path.Combine(c.ProjectRoot, "data") : _data.Text.Trim();
        c.LlmApiUrl = _apiUrl.Text?.Trim() ?? "";
        c.LlmApiKey = _apiKey.Text?.Trim() ?? "";
        c.LlmModel = _model.Text?.Trim() ?? "gpt-4o";
        c.ThsApiKey = _thsKey.Text?.Trim() ?? "";
        c.AutoRefresh = _autoRefresh.IsChecked == true;
        c.RefreshInterval = (int)_refreshInterval.Value;
        c.SchedulerEnabled = _schedEnabled.IsChecked == true;
        c.SchedulerTime = _schedTime.SelectedTime?.ToString(@"hh\:mm") ?? "16:30";
        c.SchedulerTasksStr = _schedTasks.Text?.Trim() ?? "T1 T8";
        c.MonitorInterval = (int)_monInterval.Value;
        c.MonitorEmailEnabled = _emailEnabled.IsChecked == true;
        c.SmtpHost = _smtpHost.Text?.Trim() ?? "";
        c.SmtpPort = (int)_smtpPort.Value;
        c.SmtpUser = _smtpUser.Text?.Trim() ?? "";
        c.SmtpPass = _smtpPass.Text?.Trim() ?? "";
        c.SmtpTo = _smtpTo.Text?.Trim() ?? "";
        c.SupabaseUrl = _sbUrl.Text?.Trim() ?? "";
        c.SupabaseKey = _sbKey.Text?.Trim() ?? "";
        c.AutoSync = _autoSync.IsChecked == true;
        c.NotifyLarkEnabled = _larkEnabled.IsChecked == true;
        c.LarkWebhook = _larkHook.Text?.Trim() ?? "";
        c.LarkSecret = _larkSecret.Text?.Trim() ?? "";
        c.NotifySystemEnabled = _sysNotifyEnabled.IsChecked == true;

        _app.Store.SaveConfig(c);
        if (VisualRoot is Window owner)
            await MessageBox.Show(owner, "已保存", "设置已保存。");
        _status("设置已保存");
    }

    /// <summary>
    /// 把输入框中的 Supabase 配置立即持久化到 app_config.json。
    /// 云同步三个操作（测试/上传/恢复）前自动调用——用户填完点“测试连接”即已保存，
    /// 退出后不丢（此前必须手动点“保存”按钮，极易漏掉导致配置丢失）。
    /// </summary>
    private void SaveSupabaseConfig()
    {
        _app.Config.SupabaseUrl = _sbUrl.Text?.Trim() ?? "";
        _app.Config.SupabaseKey = _sbKey.Text?.Trim() ?? "";
        _app.Config.AutoSync = _autoSync.IsChecked == true;
        try
        {
            _app.Store.SaveConfig(_app.Config);
            if (_app.Config.SupabaseUrl.Length > 0)
                _status("Supabase 配置已自动保存");
        }
        catch (Exception ex)
        {
            _status($"⚠️ Supabase 配置保存失败: {ex.Message}");
        }
    }

    // ── 提醒通知（飞书 & 系统通知）──

    /// <summary>把输入框中的通知配置立即持久化（同 Supabase 模式：填完点测试即已保存）。</summary>
    private void SaveNotifyConfig()
    {
        _app.Config.NotifyLarkEnabled = _larkEnabled.IsChecked == true;
        _app.Config.LarkWebhook = _larkHook.Text?.Trim() ?? "";
        _app.Config.LarkSecret = _larkSecret.Text?.Trim() ?? "";
        _app.Config.NotifySystemEnabled = _sysNotifyEnabled.IsChecked == true;
        try
        {
            _app.Store.SaveConfig(_app.Config);
            _status("通知配置已自动保存");
        }
        catch (Exception ex)
        {
            _status($"⚠️ 通知配置保存失败: {ex.Message}");
        }
    }

    /// <summary>发送测试提醒：飞书 webhook + 系统通知双通道，结果弹窗汇总。</summary>
    private async Task TestNotifyAsync()
    {
        if (VisualRoot is not Window owner) return;
        SaveNotifyConfig();
        var c = _app.Config;

        var lines = new List<string>();

        if (!c.NotifyLarkEnabled)
            lines.Add("· 飞书推送：未启用（勾选后重试）");
        else if (!LarkNotifier.IsValidWebhook(c.LarkWebhook))
            lines.Add("· 飞书推送：Webhook 未填或格式无效");
        else
        {
            _status("⏳ 发送测试消息到飞书…");
            var sample = LarkNotifier.BuildAlertMessage(new List<AlertEntry>
            {
                new()
                {
                    Code = "600519", Name = "贵州茅台", StrategyId = "TEST",
                    StrategyName = "测试提醒（非真实信号）", Action = "这是设置页发送的测试消息，收到即表示飞书通道配置成功。",
                    Priority = "P1", Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                },
            });
            var (ok, msg) = await LarkNotifier.SendAsync(c.LarkWebhook, sample, c.LarkSecret);
            lines.Add($"· 飞书推送：{(ok ? "✅ " : "❌ ")}{msg}");
        }

        if (!c.NotifySystemEnabled)
            lines.Add("· 系统通知：未启用（勾选后重试）");
        else if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            lines.Add("· 系统通知：当前平台不支持（仅 Windows 10/11）");
        else
        {
            WindowsToastNotifier.Show("🎯 三桶监控 · 测试通知", "这是设置页发送的测试消息，收到即表示系统通知通道配置成功。");
            lines.Add("✅ 系统通知：已弹出（若未见弹窗，请检查系统“通知设置”中是否允许本应用）");
        }

        _status("测试提醒已发送");
        await MessageBox.Show(owner, "提醒通知 · 测试", string.Join("\n", lines));
    }

    // ── 浏览 ──

    private async void BrowseRoot()
    {
        var d = await new OpenFolderDialog { Title = "选择项目根目录", Directory = _root.Text }.ShowAsync(this.VisualRoot as Window);
        if (d != null) _root.Text = d;
    }
    private async void BrowsePython()
    {
        var d = await new OpenFileDialog
        {
            Title = "选择 Python 可执行文件",
            Filters = { new FileDialogFilter { Name = "Python", Extensions = { "exe", "python" } } },
        }.ShowAsync(this.VisualRoot as Window);
        if (d is { Length: > 0 }) _python.Text = d[0];
    }
    private async void BrowseData()
    {
        var d = await new OpenFolderDialog { Title = "选择数据目录", Directory = _data.Text }.ShowAsync(this.VisualRoot as Window);
        if (d != null) _data.Text = d;
    }

    // ── 云同步（Supabase）──

    /// <summary>用输入框当前值构建同步服务（未保存也能直接测）。</summary>
    private CloudSyncService SyncSvcFromInput() => new(_sbUrl.Text ?? "", _sbKey.Text ?? "");

    private async Task SyncTestAsync()
    {
        if (VisualRoot is not Window owner) return;
        SaveSupabaseConfig();
        _status("⏳ 测试 Supabase 连接…");
        var (ok, msg) = await SyncSvcFromInput().TestAsync();
        _status(msg);
        await MessageBox.Show(owner, "云同步 · 测试连接", msg);
    }

    private async Task SyncPushAsync()
    {
        if (VisualRoot is not Window owner) return;
        SaveSupabaseConfig();
        var snapshot = _app.Store.ExportSyncSnapshot();
        if (snapshot.Count == 0)
        { await MessageBox.Show(owner, "云同步 · 上传", "本地没有可同步的数据"); return; }
        var counts = string.Join(" / ", snapshot.Select(kv => $"{kv.Key} {RowsOf(kv.Value)} 行"));
        if (!await MessageBox.Ask(owner, "云同步 · 上传",
            $"将把以下本地数据覆盖上传到云端：\n\n{counts}\n\n确定上传？"))
            return;
        _status("⏳ 上传到 Supabase…");
        var (ok, msg) = await SyncSvcFromInput().PushAsync(snapshot, Environment.MachineName);
        _status(msg);
        await MessageBox.Show(owner, "云同步 · 上传", msg);
    }

    private async Task SyncPullAsync()
    {
        if (VisualRoot is not Window owner) return;
        SaveSupabaseConfig();
        _status("⏳ 从 Supabase 拉取…");
        var (rows, error) = await SyncSvcFromInput().PullAsync();
        if (error.Length > 0)
        { _status(error); await MessageBox.Show(owner, "云同步 · 恢复", error); return; }
        if (rows.Count == 0)
        { await MessageBox.Show(owner, "云同步 · 恢复", "云端还没有数据（先在某一端上传一次）"); return; }
        var summary = string.Join("\n", rows.Select(r => $"{r.Kind,-11} {r.UpdatedAt:MM-dd HH:mm}（{r.Device}）"));
        if (!await MessageBox.Ask(owner, "云同步 · 从云端恢复",
            $"将用以下云端数据覆盖本地对应文件\n（原文件自动备份到 data/sync_backup/）：\n\n{summary}\n\n确定继续？"))
            return;
        var (count, details) = _app.Store.ImportSyncSnapshot(rows.ToDictionary(r => r.Kind, r => r.Payload));
        _status($"云端恢复完成：覆盖 {count} 类文件");
        await MessageBox.Show(owner, "云同步 · 恢复", string.Join("\n", details) + $"\n\n共 {count} 类文件已覆盖（可切到各页刷新查看）");
    }

    /// <summary>payload 里行数（仅用于上传确认提示）。</summary>
    private static string RowsOf(object payload)
        => payload is Dictionary<string, object> d && d.TryGetValue("rows", out var r) && r is List<Dictionary<string, string>> list
            ? list.Count.ToString() : "-";

    private void CopyTableSql()
    {
        if (VisualRoot is not Window { Clipboard: { } clip } owner) return;
        _ = clip.SetTextAsync(CloudSyncService.CreateTableSql);
        _status("建表 SQL 已复制，粘贴到 Supabase SQL Editor 执行一次即可");
    }
}
