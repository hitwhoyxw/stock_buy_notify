using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ThreeBucket.Core.Models;

namespace ThreeBucket.UI.Dialogs;

/// <summary>
/// 为一只自选股绑定监控策略（CheckBox 多选）。
/// 对应 Python tab_watchlist 的策略选择对话框：勾选结果分号拼接写入 watchlist.strategies。
/// </summary>
public class StrategyPickDialog : Window
{
    private readonly List<CheckBox> _boxes = new();

    /// <summary>确定后选中的策略 id 列表。</summary>
    public List<string> SelectedIds { get; } = new();

    public StrategyPickDialog(string code, string name, List<Strategy> strategies, List<string> currentIds)
    {
        Title = $"绑定策略 — {code} {name}";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new StackPanel { Margin = new Thickness(16, 8, 16, 8), Spacing = 6 };
        if (strategies.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "还没有策略。先到「策略管理」页新建。",
                Foreground = Brushes.Gray,
            });
        }
        foreach (var s in strategies)
        {
            var box = new CheckBox
            {
                Content = $"{s.Id}  {s.Name}" + (s.Enabled ? "" : "（已停用）"),
                IsChecked = currentIds.Contains(s.Id),
                IsEnabled = s.Enabled,
            };
            _boxes.Add(box);
            list.Children.Add(box);
        }

        var ok = new Button
        {
            Content = "确定",
            Background = new SolidColorBrush(Color.Parse("#27ae60")),
            Foreground = Brushes.White,
            Width = 100,
        };
        ok.Click += (_, _) =>
        {
            SelectedIds.Clear();
            foreach (var b in _boxes)
                if (b.IsChecked == true && b.Content is string c && c.Length > 2)
                    SelectedIds.Add(c.Split(' ')[0]); // "S1  名称" → "S1"
            Close(true);
        };
        var no = new Button { Content = "取消", Width = 100 };
        no.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(16, 8, 16, 12),
            Children = { ok, no },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        Content = new DockPanel
        {
            Children =
            {
                buttons,
                new ScrollViewer { Content = list },
            },
        };
    }

    public Task<bool> ShowAsync(Window owner) => ShowDialog<bool>(owner);
}
