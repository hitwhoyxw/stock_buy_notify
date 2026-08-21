using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ThreeBucket.UI.Services;

/// <summary>
/// SelectingItemsControl（DataGrid/ListBox/ComboBox）的 ItemsSource 安全重绑扩展。
/// </summary>
public static class GridResetExtensions
{
    /// <summary>
    /// 安全重建 ItemsSource：先清选中再换源。
    /// Avalonia 的 SelectionModel 会持有旧行索引，直接把集合换成更短/为空的源时，
    /// OnSelectionModelSelectionChanged 枚举 SelectedItems 会越界
    /// （ArgumentOutOfRangeException，未处理异常直接终止进程——
    /// 用户点选一行后切 tab 或行情定时刷新即触发，Windows 事件日志实测）。
    /// </summary>
    public static void SetItemsSafe(this DataGrid grid, System.Collections.IEnumerable? source)
    {
        grid.SelectedItem = null;   // 先清选中（防旧行索引越界崩进程）
        grid.ItemsSource = null;    // 先置 null 再赋源：两步真实变更强制 DataGrid 重建重读行值
        grid.ItemsSource = source;  // （行对象未实现 INotifyPropertyChanged，同引用赋值不触发刷新）
    }

    /// <inheritdoc cref="SetItemsSafe(DataGrid, IEnumerable?)"/>
    public static void SetItemsSafe(this SelectingItemsControl ctrl, System.Collections.IEnumerable? source)
    {
        ctrl.SelectedItem = null;
        ctrl.ItemsSource = null;
        ctrl.ItemsSource = source;
    }
}
