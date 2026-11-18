using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ThreeBucket.Core.Services;

namespace ThreeBucket.UI.Dialogs;

/// <summary>
/// 巴菲特观察导入弹窗：渲染 LLM 给出的「好公司坏价格」观察条目列表，默认全部勾选，
/// 用户可逐条取消（不想要的观察项不导入），确定后批量写入策略 + 自选。
/// </summary>
public class BuffettWatchDialog : DialogBase
{
    private readonly List<CheckBox> _boxes = new();

    /// <summary>确定后被勾选（要导入）的条目。</summary>
    public List<BuffettWatchItem> Selected { get; } = new();

    public BuffettWatchDialog(List<BuffettWatchItem> items, List<string> discarded)
    {
        Title = "巴菲特观察 · 好公司坏价格提醒（LLM 分析）";
        DialogWidth = 720;
        DialogMaxHeight = 620;

        var list = new StackPanel { Margin = new Thickness(16, 8, 16, 8), Spacing = 6 };
        list.Children.Add(new TextBlock
        {
            Text = $"LLM 在 B 桶分析末尾给出了 {items.Count} 条「好公司但价格未到位」观察条目。默认全部导入，取消勾选即跳过该条：",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            FontSize = 12,
        });

        foreach (var it in items)
        {
            var box = new CheckBox
            {
                IsChecked = true, // 默认全部勾选，用户可逐条取消
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = it.CorrectedFrom is { } bad
                                ? $"⚠️ {bad}→{it.Code} {it.Stock}（已按股名纠正代码）— {it.Name}"
                                : $"{it.Code} {it.Stock}  —  {it.Name}",
                            FontWeight = FontWeight.Bold,
                        },
                        new TextBlock
                        {
                            Text = it.Summary,
                            FontSize = 11,
                            Foreground = Brushes.Gray,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };
            _boxes.Add(box);
            list.Children.Add(box);
        }

        if (discarded.Count > 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"⚠️ 另有 {discarded.Count} 行格式/指标不在支持范围被忽略：" + string.Join("；", discarded),
                FontSize = 11,
                Foreground = Brushes.Orange,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var ok = new Button
        {
            Content = $"导入勾选（{_boxes.Count}）",
            Background = new SolidColorBrush(Color.Parse("#27ae60")),
            Foreground = Brushes.White,
            Width = 140,
        };
        ok.Click += (_, _) =>
        {
            Selected.Clear();
            foreach (var (box, it) in _boxes.Zip(items))
                if (box.IsChecked == true) Selected.Add(it);
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
}
