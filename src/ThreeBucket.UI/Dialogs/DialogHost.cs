using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ThreeBucket.UI.Views;

namespace ThreeBucket.UI.Dialogs;

/// <summary>桌面：模态子窗口；移动端（SingleView 无 Window）：MainView.DialogLayer 内的遮罩层。</summary>
public static class DialogHost
{
    public static Task<bool> ShowAsync(DialogBase dialog, Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor) is Window owner)
            return ShowWindowAsync(dialog, owner);

        var layer = anchor.FindAncestorOfType<MainView>(includeSelf: true)?.DialogLayer;
        if (layer == null)
            throw new InvalidOperationException("找不到弹窗宿主：anchor 不在 MainView 内且当前无窗口系统");
        return ShowOverlayAsync(dialog, layer);
    }

    private static async Task<bool> ShowWindowAsync(DialogBase dialog, Window owner)
    {
        var win = new Window
        {
            Title = dialog.Title,
            Width = dialog.DialogWidth,
            MaxHeight = dialog.DialogMaxHeight,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = dialog,
        };
        dialog.CloseRequested = r => win.Close(r);
        // 用户点标题栏 X 关闭时无结果，ShowDialog<bool?> 返回 null → 视为取消
        var result = await win.ShowDialog<bool?>(owner);
        return result ?? false;
    }

    private static Task<bool> ShowOverlayAsync(DialogBase dialog, Panel layer)
    {
        var tcs = new TaskCompletionSource<bool>();

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#2c3e50")),
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Child = new TextBlock
            {
                Text = dialog.Title,
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        DockPanel.SetDock(titleBar, Dock.Top);

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(12),
            MaxWidth = dialog.DialogWidth,
            MaxHeight = dialog.DialogMaxHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 6, Color = Color.Parse("#66000000") }),
            ClipToBounds = false,
            Child = new DockPanel
            {
                Children =
                {
                    titleBar,
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = dialog,
                    },
                },
            },
        };
        var backdrop = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#66000000")),
            Child = card,
        };

        dialog.CloseRequested = r =>
        {
            layer.Children.Remove(backdrop);
            layer.IsVisible = layer.Children.Count > 0;
            tcs.TrySetResult(r);
        };

        layer.Children.Add(backdrop);
        layer.IsVisible = true;
        return tcs.Task;
    }
}
