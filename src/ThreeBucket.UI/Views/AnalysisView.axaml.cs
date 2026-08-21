using System.Collections.ObjectModel;
using Avalonia.Controls;
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
        FileList.ItemsSource = _items;
        RefreshBtn.Click += (_, _) => LoadList();
        FileList.SelectionChanged += (_, _) =>
        {
            if (FileList.SelectedItem is FileItem f) Show(f);
        };
    }

    public void OnShown() => LoadList();

    private void LoadList()
    {
        _items.Clear();
        Viewer.Text = "← 选择左侧文件查看分析结果";
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
            Viewer.Text = "还没有任何 skill_output 文件。\n先在「任务面板」跑 T4/T5/T6 生成 skill_input，再到「LLM 桥接」粘贴 LLM 分析结果并保存。";

        // 自动选中第一个已生成文件
        var first = _items.FirstOrDefault(i => i.Exists);
        FileList.SelectedItem = first;
        if (first != null) Show(first);
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
        });
    }

    private void Show(FileItem f)
    {
        var path = Path.Combine(_app.DataDir, f.FileName);
        if (!File.Exists(path))
        {
            Viewer.Text = $"{f.FileName} 尚未生成。";
            InfoText.Text = "";
            return;
        }
        Viewer.Text = _app.Store.LoadText(f.FileName);
        var size = new FileInfo(path).Length;
        InfoText.Text = $"{f.FileName}  |  {size:N0} bytes  |  {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}";
    }
}
