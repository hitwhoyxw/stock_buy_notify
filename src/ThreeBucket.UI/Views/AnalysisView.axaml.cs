using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class AnalysisView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;

    // 与 Python engine.DataManager.SKILLS 对应
    private static readonly (string Key, string Label, string Output)[] Skills =
    {
        ("T4C", "T4 财报文本扫描", "skill_output_T4C.md"),
        ("T5",  "T5 季度归因",     "skill_output_T5.md"),
        ("T6A", "T6 A桶·红利逆向", "skill_output_T6_A.md"),
        ("T6B", "T6 B桶·成长",     "skill_output_T6_B.md"),
        ("T6C", "T6 C桶·热点周期", "skill_output_T6_C.md"),
    };

    public class FileItem
    {
        public string FileName { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Exists { get; set; }
        public string Mtime { get; set; } = "";
        /// <summary>文件修改时间原始值（默认选中"最新生成的输出"用，Mtime 字符串跨年排序会错）。</summary>
        public DateTime MtimeDt { get; set; }
        public string Display => Exists
            ? $"📄 {Label}\n   {FileName}  |  {Mtime}"
            : $"◻ {Label}\n   {FileName}  |  未生成";
    }

    private ObservableCollection<FileItem> _items = new();

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public AnalysisView() : this(new AppState(), _ => { }) { }

    public AnalysisView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;
        FileList.SetItemsSafe(_items);
        RefreshBtn.Click += (_, _) => LoadList();
        FileList.SelectionChanged += (_, _) =>
        {
            if (FileList.SelectedItem is FileItem f) Show(f);
        };
    }

    public void OnShown() => LoadList();

    private void LoadList()
    {
        // Clear 前先清选中：SelectionModel 持有旧行索引，清空集合时枚举越界会崩进程
        FileList.SelectedItem = null;
        _items.Clear();
        var seen = new HashSet<string>();

        foreach (var (_, label, output) in Skills)
        {
            seen.Add(output);
            AddItem(label, output);
        }
        // 补充其它未登记的 skill_output_*.md
        try
        {
            foreach (var f in Directory.GetFiles(_app.DataDir, "skill_output_*.md").OrderBy(n => n))
            {
                var name = Path.GetFileName(f);
                if (seen.Add(name)) AddItem(name, name);
            }
        }
        catch { }

        if (_items.Count == 0 || _items.All(i => !i.Exists))
        {
            Doc.Children.Clear();
            Doc.Children.Add(new TextBlock
            {
                Text = "还没有任何 skill_output 文件。\n先在「任务面板」跑 T4/T5/T6 生成 skill_input，再到「LLM 桥接」粘贴 LLM 分析结果并保存。",
                Foreground = Avalonia.Media.Brushes.Gray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            return;
        }

        // 自动选中最新生成的文件（此前固定选列表第一个 = T4C 旧输出，用户误以为"只有 C 桶结果"）
        var latest = _items.Where(i => i.Exists).OrderByDescending(i => i.MtimeDt).First();
        FileList.SelectedItem = latest;
    }

    private void AddItem(string label, string fileName)
    {
        var path = Path.Combine(_app.DataDir, fileName);
        var exists = File.Exists(path);
        _items.Add(new FileItem
        {
            FileName = fileName,
            Label = label,
            Exists = exists,
            Mtime = exists ? File.GetLastWriteTime(path).ToString("MM-dd HH:mm") : "",
            MtimeDt = exists ? File.GetLastWriteTime(path) : DateTime.MinValue,
        });
    }

    private void Show(FileItem f)
    {
        var path = Path.Combine(_app.DataDir, f.FileName);
        Doc.Children.Clear();
        if (!File.Exists(path))
        {
            InfoText.Text = "";
            Doc.Children.Add(new TextBlock { Text = $"{f.FileName} 尚未生成。", Foreground = Avalonia.Media.Brushes.Gray });
            return;
        }
        var text = _app.Store.LoadText(f.FileName);
        var size = new FileInfo(path).Length;
        InfoText.Text = $"{f.FileName}  |  {size:N0} bytes  |  {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}";
        RenderMarkdown(text);
    }

    // ── 轻量 markdown 渲染：标题/引用/段落用 TextBlock，| 表格 | 解析成 DataGrid ──

    private void RenderMarkdown(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // 表格块：以 | 开头的连续行，第二行是 |---|---| 分隔行
            if (line.TrimStart().StartsWith("|") && i + 1 < lines.Length && IsSeparatorRow(lines[i + 1]))
            {
                i = RenderTable(lines, i);
                continue;
            }

            var t = line.Trim();
            if (t.Length == 0) { i++; continue; }
            if (t.StartsWith("### "))
                Doc.Children.Add(new TextBlock { Text = t[4..], FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 14, Margin = new Avalonia.Thickness(0, 12, 0, 4) });
            else if (t.StartsWith("## "))
                Doc.Children.Add(new TextBlock { Text = t[3..], FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 15, Margin = new Avalonia.Thickness(0, 12, 0, 4) });
            else if (t.StartsWith("# "))
                Doc.Children.Add(new TextBlock { Text = t[2..], FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 17, Margin = new Avalonia.Thickness(0, 8, 0, 6) });
            else if (t.StartsWith("> "))
                Doc.Children.Add(new TextBlock { Text = t[2..], Foreground = Avalonia.Media.Brushes.Gray, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 2, 0, 2) });
            else
                Doc.Children.Add(new TextBlock { Text = t, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 2, 0, 2) });
            i++;
        }
    }

    /// <summary>渲染从 header 行开始的表格块，返回下一行下标。</summary>
    private int RenderTable(string[] lines, int start)
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
        grid.SetItemsSafe(new ObservableCollection<Dictionary<string, string>>(rows));
        Doc.Children.Add(grid);
        return i;
    }

    private static List<string> SplitMdRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|")) t = t[1..];
        if (t.EndsWith("|")) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>markdown 表格分隔行：每个单元格都是 :--- / --- / ---: 形态。</summary>
    private static bool IsSeparatorRow(string line)
    {
        var cells = SplitMdRow(line);
        return cells.Count > 0 && cells.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"^:?-{3,}:?$"));
    }
}
