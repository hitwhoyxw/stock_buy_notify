using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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

        _apiUrl.Watermark = "https://api.openai.com/v1/chat/completions（留空=用 Qoder）";
        _apiKey.Watermark = "sk-...（留空=手动模式）";
        Form.Children.Add(Group("LLM API（预留，当前用 Qoder 对话）", new Control[]
        {
            Row("API URL:", _apiUrl),
            Row("API Key:", _apiKey),
            Row("模型:", _model),
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

        _app.Store.SaveConfig(c);
        if (VisualRoot is Window owner)
            await MessageBox.Show(owner, "已保存", "设置已保存。");
        _status("设置已保存");
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
}
