using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ThreeBucket.Core.Models;

namespace ThreeBucket.UI.Dialogs;

/// <summary>
/// 策略编辑对话框：简单模式（指标+操作符+阈值）+ 复合条件 JSON 文本框。
/// 条件树可视化构建器后续迭代，当前先支持 JSON 粘贴（与 Python 端条件树 Schema 兼容）。
/// </summary>
public class StrategyDialog : Window
{
    private readonly TextBlock _error = new() { Foreground = Brushes.Red, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private static readonly string[] Indicators =
    {
        "price_vs_ma60", "drawdown_from_high_180d", "gain_from_low_180d",
        "cost_basis_gain", "volume_ratio_20d", "ma5_vs_ma10",
        "macd_dif", "rsi_14", "turnover_rate", "close_vs_ma20",
    };

    private readonly TextBox _name = new() { Watermark = "如：跌破MA60减仓 / MACD金叉放量" };
    private readonly ComboBox _type = new() { ItemsSource = new[] { "buy", "hold", "sell" }, SelectedIndex = 2 };
    private readonly ComboBox _indicator = new() { ItemsSource = Indicators, SelectedIndex = 0 };
    private readonly ComboBox _op = new() { ItemsSource = new[] { "<", "<=", ">", ">=" }, SelectedIndex = 0 };
    private readonly NumericUpDown _threshold = new() { Minimum = -9999m, Maximum = 999999m, Increment = 0.5m, Value = 0, FormatString = "F2" };
    private readonly TextBox _action = new() { Watermark = "如：现价跌破MA60，建议减仓1/3观察" };
    private readonly ComboBox _prio = new() { ItemsSource = new[] { "P0", "P1", "P2", "P3" }, SelectedIndex = 1 };
    private readonly TextBox _condition = new() { Watermark = "复合条件树 JSON（高级模式）。留空=简单策略。", Height = 96, AcceptsReturn = true };

    public StrategyDialog(Strategy? existing = null)
    {
        Title = existing == null ? "➕ 新建策略" : "编辑策略";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("110,*,*"),
        };
        void Row(int i, string label, Control ctl)
        {
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetRow(grid.Children[^1], i);
            Grid.SetColumn(grid.Children[^1], 0);
            grid.Children.Add(ctl);
            Grid.SetRow(ctl, i);
            Grid.SetColumn(ctl, 1);
            Grid.SetColumnSpan(ctl, 2);
        }
        Row(0, "策略名称:", _name);
        Row(1, "建议类型:", _type);
        Row(2, "监控指标:", _indicator);
        Row(3, "触发条件:", _op);
        Row(4, "阈值:", _threshold);
        Row(5, "触发后建议:", _action);
        Row(6, "优先级:", _prio);
        Row(7, "复合条件:", _condition);

        var ok = new Button { Content = "保存", Background = new SolidColorBrush(Color.Parse("#27ae60")), Foreground = Brushes.White, Width = 100 };
        var no = new Button { Content = "取消", Width = 100 };
        ok.Click += (_, _) => { if (Validate()) Close(true); };
        no.Click += (_, _) => Close(false);

        var header = new TextBlock
        {
            Text = "策略 = 触发条件 → 建议动作。简单策略填「监控指标+条件+阈值」；复合策略把条件树 JSON 粘到「复合条件」框（留空指标列）。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10, Margin = new Thickness(0, 12, 0, 0),
            Children = { ok, no },
        };
        var stack = new StackPanel { Spacing = 4, Children = { header, grid, _error, buttons } };
        Content = stack;

        if (existing != null) Prefill(existing);
    }

    private void Prefill(Strategy s)
    {
        _name.Text = s.Name;
        _type.SelectedItem = s.Type;
        if (!string.IsNullOrWhiteSpace(s.Indicator))
        {
            _indicator.SelectedItem = s.Indicator;
            _op.SelectedItem = s.Operator;
            if (double.TryParse(s.Threshold, out var t)) _threshold.Value = (decimal)t;
        }
        _action.Text = s.Action;
        _prio.SelectedItem = s.Priority;
        _condition.Text = s.Condition;
    }

    private bool Validate()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        { _error.Text = "请填写策略名称"; return false; }
        if (string.IsNullOrWhiteSpace(_action.Text))
        { _error.Text = "请填写触发后的建议动作"; return false; }
        if (!string.IsNullOrWhiteSpace(_condition.Text))
        {
            try { System.Text.Json.JsonDocument.Parse(_condition.Text); }
            catch (Exception ex) { _error.Text = $"复合条件不是合法 JSON：{ex.Message}"; return false; }
        }
        _error.Text = "";
        return true;
    }

    public Strategy? GetRecord()
    {
        if (!Validate()) return null;
        var useComposite = !string.IsNullOrWhiteSpace(_condition.Text);
        return new Strategy
        {
            Name = _name.Text!.Trim(),
            Type = (_type.SelectedItem as string) ?? "sell",
            Indicator = useComposite ? "" : (_indicator.SelectedItem as string ?? ""),
            Operator = useComposite ? "" : (_op.SelectedItem as string ?? ""),
            Threshold = useComposite ? "" : (_threshold.Value ?? 0).ToString("F2"),
            Action = _action.Text!.Trim(),
            Priority = (_prio.SelectedItem as string) ?? "P1",
            Condition = _condition.Text?.Trim() ?? "",
            Enabled = true,
        };
    }

    public Task<bool> ShowAsync(Window owner) => ShowDialog<bool>(owner);
}
