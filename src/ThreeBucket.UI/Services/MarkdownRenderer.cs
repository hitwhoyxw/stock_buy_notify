using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace ThreeBucket.UI.Services;

/// <summary>
/// 轻量 markdown → Avalonia 控件渲染（无第三方依赖）：
/// 标题按层级给字号、引用变灰小字、`| 表格 |` 解析成 DataGrid 动态列、段落折行。
///
/// 原为 AnalysisView 私有实现，知识库页复用同一套渲染，避免两处行为漂移
/// （详见 AnalysisView.RenderMarkdown 的历史注释：索引器绑定在 Avalonia 版本间行为不一致，
/// 表格列必须绑行对象 + ConverterParameter 传列名）。
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>把 markdown 渲染进 target 面板（先清空）。</summary>
    public static void Render(Panel target, string? text)
    {
        target.Children.Clear();
        if (string.IsNullOrEmpty(text)) return;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // 表格块：以 | 开头的连续行，第二行是 |---|---| 分隔行
            if (line.TrimStart().StartsWith("|") && i + 1 < lines.Length && IsSeparatorRow(lines[i + 1]))
            {
                i = RenderTable(target, lines, i);
                continue;
            }

            var t = line.Trim();
            if (t.Length == 0) { i++; continue; }

            // 分隔线（---）在详情渲染里只作留白，不画线
            if (t is "---" or "***" or "___") { i++; continue; }

            if (t.StartsWith("### "))
                target.Children.Add(Heading(t[4..], 14, new Avalonia.Thickness(0, 12, 0, 4)));
            else if (t.StartsWith("## "))
                target.Children.Add(Heading(t[3..], 15, new Avalonia.Thickness(0, 12, 0, 4)));
            else if (t.StartsWith("# "))
                target.Children.Add(Heading(t[2..], 17, new Avalonia.Thickness(0, 8, 0, 6)));
            else if (t.StartsWith("> "))
                target.Children.Add(new TextBlock
                {
                    Text = t[2..],
                    Foreground = Brushes.Gray,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 2, 0, 2),
                });
            else
                target.Children.Add(new TextBlock
                {
                    Text = StripInlineMarkdown(t),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 2, 0, 2),
                });
            i++;
        }
    }

    private static TextBlock Heading(string text, double size, Avalonia.Thickness margin) => new()
    {
        Text = StripInlineMarkdown(text),
        FontWeight = FontWeight.Bold,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin,
    };

    /// <summary>去掉行内 `**粗体**` / `` `代码` `` 标记，保留可读文本（TextBlock 不解析 md）。</summary>
    internal static string StripInlineMarkdown(string text)
        => text.Replace("**", "").Replace("`", "").Replace("　", " ");

    /// <summary>渲染从 header 行开始的表格块，返回下一行下标。</summary>
    private static int RenderTable(Panel target, string[] lines, int start)
    {
        var headers = SplitMdRow(lines[start]);
        var rows = new List<Dictionary<string, string>>();
        int i = start + 2; // 跳过 header 与 |---| 分隔行
        while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
        {
            var cells = SplitMdRow(lines[i]);
            var row = new Dictionary<string, string>();
            for (int c = 0; c < headers.Count; c++)
                row[headers[c]] = c < cells.Count ? cells[c] : "";
            rows.Add(row);
            i++;
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,   // t41：可拖列宽
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            Margin = new Avalonia.Thickness(0, 4, 0, 8),
            MaxHeight = 420,   // 限制高度避免长表撑爆外层 ScrollViewer
        };
        foreach (var h in headers)
        {
            // 列名来自 markdown 表头，无强类型属性可用；索引器路径在 Avalonia 版本间
            // 行为不一致会导致整列空白——改绑行对象本身，ConverterParameter 传列名取值
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = h,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                CellTemplate = new FuncDataTemplate<Dictionary<string, string>>((_, _) => new TextBlock
                {
                    [!TextBlock.TextProperty] = new Avalonia.Data.Binding
                    {
                        Converter = new DictValueConverter(),
                        ConverterParameter = h,
                    },
                }),
            });
        }
        grid.SetItemsSafe(new System.Collections.ObjectModel.ObservableCollection<Dictionary<string, string>>(rows));
        target.Children.Add(grid);
        return i;
    }

    internal static List<string> SplitMdRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>markdown 表格分隔行：每个单元格都是 :--- / --- / ---: 形态。</summary>
    internal static bool IsSeparatorRow(string line)
    {
        var cells = SplitMdRow(line);
        return cells.Count > 0 && cells.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"^:?-{3,}:?$"));
    }
}
