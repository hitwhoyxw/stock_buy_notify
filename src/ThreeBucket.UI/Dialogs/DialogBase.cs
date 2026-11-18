using Avalonia;
using Avalonia.Controls;

namespace ThreeBucket.UI.Dialogs;

/// <summary>
/// 弹窗内容基类。不继承 Window：移动端（Android/iOS）无窗口系统，new Window() 直接抛异常。
/// 由 DialogHost 决定呈现方式——桌面包成模态 Window，移动端渲染为页内遮罩层。
/// </summary>
public abstract class DialogBase : UserControl
{
    public string Title { get; set; } = "";
    public double DialogWidth { get; set; } = 400;
    public double DialogMaxHeight { get; set; } = double.PositiveInfinity;

    internal Action<bool>? CloseRequested;

    protected void Close(bool result) => CloseRequested?.Invoke(result);

    public Task<bool> ShowAsync(Visual anchor) => DialogHost.ShowAsync(this, anchor);
}
