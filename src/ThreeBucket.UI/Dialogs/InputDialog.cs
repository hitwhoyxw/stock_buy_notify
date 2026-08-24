using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ThreeBucket.UI.Dialogs;

/// <summary>
/// 单行/双行文本输入对话框。ShowAsync 返回 true 时 Value1/Value2 有效。
/// </summary>
public class InputDialog : Window
{
    private readonly TextBox _t1;
    private readonly TextBox _t2;

    public string Value1 { get; private set; } = "";
    public string Value2 { get; private set; } = "";

    public InputDialog(string title, string label1, string? default1 = null,
        string? label2 = null, string? default2 = null)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _t1 = new TextBox { Text = default1 ?? "", Margin = new Thickness(0, 0, 0, 6), Watermark = label1 };
        _t2 = new TextBox { Text = default2 ?? "", IsVisible = label2 != null, Watermark = label2 ?? "" };

        var ok = new Button
        {
            Content = "确定",
            Background = new SolidColorBrush(Color.Parse("#27ae60")),
            Foreground = Brushes.White,
            Width = 100,
        };
        ok.Click += (_, _) => { Value1 = _t1.Text ?? ""; Value2 = _t2.Text ?? ""; Close(true); };
        var no = new Button { Content = "取消", Width = 100 };
        no.Click += (_, _) => Close(false);
        _t1.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); };

        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = label1 });
        stack.Children.Add(_t1);
        if (label2 != null)
        {
            stack.Children.Add(new TextBlock { Text = label2, Margin = new Thickness(0, 6, 0, 0) });
            stack.Children.Add(_t2);
        }
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { ok, no },
        });
        Content = stack;
    }

    public Task<bool> ShowAsync(Window owner) => ShowDialog<bool>(owner);
}
