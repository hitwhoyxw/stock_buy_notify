using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.UI.Dialogs;

/// <summary>
/// 交易录入/编辑对话框（纯代码构建，无 XAML）。
/// 只填代码时自动拉取名称（名称框留空触发，对应 Python TradeDialog 的 editingFinished 行为）。
/// </summary>
public class TradeDialog : Window
{
    private readonly TextBox _code = new() { Watermark = "6 位数字，如 600519" };
    private readonly TextBox _name = new() { Watermark = "名称（可选，只填代码可自动获取）" };
    private readonly TextBox _industry = new() { Watermark = "申万一级行业（可选）" };
    private readonly ComboBox _direction = new() { ItemsSource = new[] { "买入", "卖出" }, SelectedIndex = 0 };
    private readonly ComboBox _bucket = new() { ItemsSource = new[] { "A", "B", "C", "D" }, SelectedIndex = 0 };
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly NumericUpDown _price = new() { Minimum = 0.001m, Maximum = 999999m, Increment = 0.01m, Value = 0, FormatString = "F3" };
    private readonly NumericUpDown _shares = new() { Minimum = 0, Maximum = 100_000_000, Increment = 100, Value = 100, FormatString = "F0" };
    private readonly NumericUpDown _amount = new() { Minimum = 0, Maximum = 999_999_999m, Increment = 1, Value = 0, FormatString = "F2" };
    private readonly TextBox _rule = new() { Watermark = "触发规则 ID（可选）" };
    private readonly TextBox _reason = new() { Watermark = "一句话决策理由（可选）" };
    private readonly TextBlock _error = new() { Foreground = Brushes.Red, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    private readonly QuoteService? _quotes;
    private bool _fetchingName;

    public TradeDialog(QuoteService? quotes = null)
    {
        _quotes = quotes;
        Title = "记一笔交易";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
        for (var i = 0; i < 11; i++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        void Row(int i, string label, Control ctl)
        {
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) });
            Grid.SetRow(grid.Children[^1], i);
            Grid.SetColumn(grid.Children[^1], 0);
            grid.Children.Add(ctl);
            Grid.SetRow(ctl, i);
            Grid.SetColumn(ctl, 1);
        }
        Row(0, "日期:", _date);
        Row(1, "方向:", _direction);
        Row(2, "桶:", _bucket);
        Row(3, "代码:", _code);
        Row(4, "名称:", _name);
        Row(5, "行业:", _industry);
        Row(6, "价格:", _price);
        Row(7, "股数:", _shares);
        Row(8, "金额(元):", _amount);
        Row(9, "规则ID:", _rule);
        Row(10, "决策理由:", _reason);

        _price.ValueChanged += (_, _) => _amount.Value = Math.Round((_price.Value ?? 0m) * (_shares.Value ?? 0m), 2);
        _shares.ValueChanged += (_, _) => _amount.Value = Math.Round((_price.Value ?? 0m) * (_shares.Value ?? 0m), 2);

        // 只填代码自动取名：代码框失焦时若名称为空则后台拉取回填
        _code.LostFocus += (_, _) => _ = TryFetchNameAsync();

        var ok = new Button
        {
            Content = "💾 保存",
            Background = new SolidColorBrush(Color.Parse("#27ae60")),
            Foreground = Brushes.White,
            Width = 100,
        };
        ok.Click += (_, _) => { if (Validate()) Close(true); };
        var cancel = new Button { Content = "取消", Width = 100 };
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 8,
            Children =
            {
                grid,
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { ok, cancel },
                },
            },
        };
    }

    private async Task TryFetchNameAsync()
    {
        if (_quotes == null || _fetchingName) return;
        var code = (_code.Text ?? "").Trim();
        if (code.Length is 5 or 6 && code.All(char.IsDigit)
            && string.IsNullOrWhiteSpace(_name.Text))
        {
            _fetchingName = true;
            try
            {
                var map = await _quotes.FetchAsync(new[] { code });
                var pure = code.PadLeft(6, '0');
                if (map.TryGetValue(pure, out var q) && !string.IsNullOrWhiteSpace(q.Name)
                    && string.IsNullOrWhiteSpace(_name.Text))
                {
                    _name.Text = q.Name;
                }
            }
            catch { /* 自动取名失败不影响录入 */ }
            finally { _fetchingName = false; }
        }
    }

    public void Prefill(Trade t)
    {
        _date.SelectedDate = DateTime.TryParse(t.Date, out var d) ? d : DateTime.Today;
        _direction.SelectedItem = t.Direction is "卖出" ? "卖出" : "买入";
        _bucket.SelectedItem = string.IsNullOrEmpty(t.Bucket) ? "A" : t.Bucket;
        _code.Text = t.Code;
        _name.Text = t.Name;
        _industry.Text = t.Industry;
        _price.Value = (decimal)t.Price;
        _shares.Value = (decimal)t.Shares;
        _amount.Value = (decimal)t.Amount;
        _rule.Text = t.RuleId;
        _reason.Text = t.Reason;
    }

    private bool Validate()
    {
        var code = (_code.Text ?? "").Trim();
        if (!(code.Length == 6 && code.All(char.IsDigit)))
        { _error.Text = "代码必须是 6 位数字，如 600519"; return false; }
        if (_shares.Value <= 0) { _error.Text = "股数必须大于 0"; return false; }
        if (_price.Value <= 0) { _error.Text = "价格必须大于 0"; return false; }
        _error.Text = "";
        return true;
    }

    public Trade? GetRecord()
    {
        if (!Validate()) return null;
        return new Trade
        {
            Date = (_date.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd"),
            Direction = _direction.SelectedItem as string ?? "买入",
            Bucket = _bucket.SelectedItem as string ?? "A",
            Code = (_code.Text ?? "").Trim(),
            Name = (_name.Text ?? "").Trim(),
            Industry = (_industry.Text ?? "").Trim(),
            Price = (double)(_price.Value ?? 0),
            Shares = (double)(_shares.Value ?? 0),
            Amount = (double)(_amount.Value ?? 0),
            RuleId = (_rule.Text ?? "").Trim(),
            Reason = (_reason.Text ?? "").Trim(),
        };
    }

    public Task<bool> ShowAsync(Window owner) => ShowDialog<bool>(owner);
}
