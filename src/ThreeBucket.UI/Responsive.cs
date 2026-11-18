using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace ThreeBucket.UI;

/// <summary>
/// 窄屏（手机）下把"左右分栏"的 Grid 自动改为"上下堆叠"：
/// 在 Grid 上设置 <c>local:Responsive.NarrowRows="3*,Auto,2*"</c>，
/// 宽度低于 <see cref="Breakpoint"/> 时列定义换成单列、行定义取 NarrowRows，
/// 每个子控件的 Grid.Column 映射为 Grid.Row；GridSplitter 在窄屏下隐藏。
/// </summary>
public static class Responsive
{
    public const double Breakpoint = 600;

    public static readonly AttachedProperty<string?> NarrowRowsProperty =
        AvaloniaProperty.RegisterAttached<Grid, string?>("NarrowRows", typeof(Responsive));

    public static string? GetNarrowRows(Grid g) => g.GetValue(NarrowRowsProperty);
    public static void SetNarrowRows(Grid g, string? v) => g.SetValue(NarrowRowsProperty, v);

    public static bool IsNarrow(Visual? v) => v is not null && TopLevel.GetTopLevel(v)?.Bounds.Width < Breakpoint;

    static Responsive()
    {
        NarrowRowsProperty.Changed.AddClassHandler<Grid>((g, _) => Attach(g));
    }

    private sealed class State
    {
        public required ColumnDefinitions WideColumns;
        public required RowDefinitions WideRows;
        public required Dictionary<Control, int> WideColumn;
        public bool Narrow;
    }

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<Grid, State?>("State", typeof(Responsive));

    private static void Attach(Grid grid)
    {
        if (grid.GetValue(StateProperty) is not null) return;
        var state = new State
        {
            WideColumns = grid.ColumnDefinitions,
            WideRows = grid.RowDefinitions,
            WideColumn = new Dictionary<Control, int>(),
        };
        grid.SetValue(StateProperty, state);
        grid.SizeChanged += (_, e) => Apply(grid, state, e.NewSize.Width);
    }

    private static void Apply(Grid grid, State state, double width)
    {
        if (width <= 0) return;
        // XAML 里附加属性先于子元素赋值，子控件的原始列号要等首次布局时再采集
        if (state.WideColumn.Count == 0)
            foreach (var c in grid.Children) state.WideColumn[c] = Grid.GetColumn(c);
        var narrow = width < Breakpoint;
        if (narrow == state.Narrow) return;
        state.Narrow = narrow;

        if (narrow)
        {
            var rows = GetNarrowRows(grid);
            grid.ColumnDefinitions = new ColumnDefinitions("*");
            grid.RowDefinitions = string.IsNullOrWhiteSpace(rows)
                ? BuildRows(state.WideColumn.Count)
                : new RowDefinitions(rows);
            foreach (var (c, col) in state.WideColumn)
            {
                Grid.SetColumn(c, 0);
                Grid.SetRow(c, col);
                if (c is GridSplitter) c.IsVisible = false;
            }
        }
        else
        {
            grid.ColumnDefinitions = state.WideColumns;
            grid.RowDefinitions = state.WideRows;
            foreach (var (c, col) in state.WideColumn)
            {
                Grid.SetColumn(c, col);
                Grid.SetRow(c, 0);
                if (c is GridSplitter) c.IsVisible = true;
            }
        }
    }

    private static RowDefinitions BuildRows(int count)
    {
        var rows = new RowDefinitions();
        for (var i = 0; i < count; i++) rows.Add(new RowDefinition(1, GridUnitType.Star));
        return rows;
    }
}
