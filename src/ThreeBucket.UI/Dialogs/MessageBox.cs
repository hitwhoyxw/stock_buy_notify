using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ThreeBucket.UI.Dialogs;

/// <summary>
/// 极简确认/提示对话框（Avalonia 无内建 MessageBox）。
/// 必须通过 Ask/Show 静态方法异步等待，同步阻塞 UI 线程会死锁。
/// </summary>
public class MessageBox : Window
{
    private MessageBox(string title, string message, bool yesNo)
    {
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var yes = new Button
        {
            Content = yesNo ? "确定" : "知道了",
            Background = new SolidColorBrush(Color.Parse("#27ae60")),
            Foreground = Brushes.White,
            Width = 100,
        };
        yes.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(yes);
        if (yesNo)
        {
            var no = new Button { Content = "取消", Width = 100 };
            no.Click += (_, _) => Close(false);
            buttons.Children.Add(no);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };
    }

    /// <summary>是/否确认框，返回 true=确定。</summary>
    public static Task<bool> Ask(Window owner, string title, string message)
        => new MessageBox(title, message, true).ShowDialog<bool>(owner);

    /// <summary>仅提示框（单按钮）。</summary>
    public static Task Show(Window owner, string title, string message)
        => new MessageBox(title, message, false).ShowDialog<bool>(owner);
}
