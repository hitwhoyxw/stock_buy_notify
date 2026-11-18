using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using ThreeBucket.Core.Services;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

/// <summary>
/// 「投资经验」页：浏览 / 检索 trading-system/09_投资经验知识库.md，并把命中规则拼成
/// 可直接粘贴给 LLM 的上下文片段（为后续自动接入 LLM 分析预留人手入口）。
///
/// 只读页面：知识库内容通过编辑 md 文件维护（「打开源文件」→ 改 → 「刷新」即时生效），
/// 不在 UI 里改数据，避免出现"UI 记录"和"文档记录"两份真源。
/// </summary>
public partial class KnowledgeView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;

    /// <summary>列表项：条目 / 类别正文（AI 使用规则这类无条目的类别）。</summary>
    public class Item
    {
        public KnowledgeEntry? Entry { get; init; }
        public KnowledgeCategory? Category { get; init; }
        public string Display { get; init; } = "";
        public string Key => Entry?.Id ?? Category?.Code ?? "";
    }

    private ObservableCollection<Item> _items = new();
    private bool _showRaw;
    private Item? _current;
    /// <summary>防重入：Reload 里改 CategoryCombo.SelectedIndex 会触发 SelectionChanged → Reload 递归。</summary>
    private bool _loading;

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public KnowledgeView() : this(new AppState(), _ => { }) { }

    public KnowledgeView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;
        ItemList.SetItemsSafe(_items);

        CategoryCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Reload(keepSelection: true);
        };
        SearchBtn.Click += (_, _) => ApplyFilter();
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyFilter();
        };
        RefreshBtn.Click += (_, _) =>
        {
            // 手动刷新同样先检测文件变更（改完 md 直接点刷新即可，不必切页）
            _app.RefreshKnowledgeBaseIfChanged();
            Reload(keepSelection: false);
        };
        CopyContextBtn.Click += (_, _) => CopyContext();
        OpenFileBtn.Click += (_, _) => OpenSourceFile();
        RawToggleBtn.Click += (_, _) => { _showRaw = !_showRaw; RawToggleBtn.Content = _showRaw ? "结构化" : "原文"; RenderCurrent(); };
        CopyEntryBtn.Click += (_, _) => CopyEntry();
        ItemList.SelectionChanged += (_, _) =>
        {
            if (ItemList.SelectedItem is Item it) { _current = it; RenderCurrent(); }
        };
    }

    public void OnShown()
    {
        // 用户可能刚编辑过 md：切页时自动检测重载
        _app.RefreshKnowledgeBaseIfChanged();
        Reload(keepSelection: true);
    }

    private void Reload(bool keepSelection)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var kb = _app.KnowledgeBase;

            // 类别下拉：首项「全部类别」+ 有内容的类别
            var prevIndex = CategoryCombo.SelectedIndex;
            var options = new List<string> { "全部类别" };
            options.AddRange(kb.Categories.Where(HasContent).Select(c => c.Display));
            CategoryCombo.ItemsSource = options;
            CategoryCombo.SelectedIndex = keepSelection && prevIndex >= 0 && prevIndex < options.Count ? prevIndex : 0;

            var selectedKey = keepSelection ? _current?.Key : null;
            ApplyFilter(selectedKey);

            InfoText.Text = kb.IsLoaded
                ? $"{kb.Title}  |  {kb.Entries.Count()} 条经验 / {kb.Categories.Count} 个类别  |  "
                  + $"来源 {(kb.SourcePath == "embedded" ? "内置副本" : Path.GetFileName(kb.SourcePath))}"
                : "⚠️ 未找到知识库文件 trading-system/09_投资经验知识库.md";
        }
        finally { _loading = false; }
    }

    private static bool HasContent(KnowledgeCategory c) => c.Entries.Count > 0 || c.Body.Length > 0;

    private InvestmentKnowledgeBase Kb => _app.KnowledgeBase;

    /// <summary>按当前类别 + 检索词生成列表项（检索命中优先，无检索词则按类别展开）。</summary>
    private void ApplyFilter(string? selectKey = null)
    {
        var kb = Kb;
        var category = SelectedCategoryCode();
        var query = SearchBox.Text?.Trim() ?? "";

        IEnumerable<KnowledgeEntry> entries = string.IsNullOrEmpty(query)
            ? (category is null ? kb.Entries : kb.ByCategory(category))
            : kb.Retrieve(query, Math.Max(1, kb.Entries.Count()), category);

        var items = new List<Item>();
        foreach (var e in entries)
            items.Add(new Item { Entry = e, Display = $"📌 {e.Display}\n   {e.TagsText}" });

        // 类别正文（如「04 LLM 使用规则」）：无检索词且类别匹配时列出
        if (string.IsNullOrEmpty(query))
        {
            foreach (var c in kb.Categories)
            {
                if (c.Entries.Count > 0 || c.Body.Length == 0) continue;
                if (category is not null && c.Code != category) continue;
                items.Add(new Item { Category = c, Display = $"📜 {c.Display}\n   规则正文 / AI 指令" });
            }
        }
        _items = new ObservableCollection<Item>(items);
        ItemList.SetItemsSafe(_items);

        if (_items.Count == 0)
        {
            _current = null;
            DetailTitle.Text = "";
            Doc.Children.Clear();
            Doc.Children.Add(new TextBlock
            {
                Text = kb.IsLoaded ? "没有命中任何经验条目，换个关键词试试。" : "知识库文件缺失。",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var target = selectKey is null ? null : _items.FirstOrDefault(i => i.Key == selectKey);
        ItemList.SelectedItem = target ?? _items[0];
    }

    private string? SelectedCategoryCode()
    {
        var idx = CategoryCombo.SelectedIndex;
        if (idx <= 0) return null;
        var cats = Kb.Categories.Where(HasContent).ToList();
        return idx - 1 < cats.Count ? cats[idx - 1].Code : null;
    }

    private void RenderCurrent()
    {
        Doc.Children.Clear();
        var it = _current;
        if (it is null) { DetailTitle.Text = ""; return; }

        if (it.Entry is { } e)
        {
            DetailTitle.Text = $"{e.Id} · {e.Title}";
            if (_showRaw)
            {
                MarkdownRenderer.Render(Doc, e.RawMarkdown);
                return;
            }
            AddMeta($"类别：{e.CategoryCode} {e.CategoryTitle}");
            foreach (var s in e.Sections)
            {
                Doc.Children.Add(new TextBlock
                {
                    Text = s.Heading,
                    FontWeight = FontWeight.Bold,
                    FontSize = 13,
                    Margin = new Avalonia.Thickness(0, 10, 0, 3),
                    Foreground = new SolidColorBrush(Color.Parse("#2c5aa0")),
                });
                MarkdownRenderer.Render(IntoNew(Doc), s.Body);
            }
            if (e.Tags.Count > 0) AddMeta("标签：" + e.TagsText);
            return;
        }

        if (it.Category is { } c)
        {
            DetailTitle.Text = c.Display;
            if (_showRaw)
            {
                MarkdownRenderer.Render(Doc, c.Body);
                return;
            }
            MarkdownRenderer.Render(Doc, c.Body);
            AddMeta("该类别为指令型内容（无独立经验条目），已随「复制 LLM 上下文」一并输出。");
        }
    }

    private static StackPanel IntoNew(Panel parent)
    {
        var p = new StackPanel();
        parent.Children.Add(p);
        return p;
    }

    private void AddMeta(string text) => Doc.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 11,
        Foreground = Brushes.Gray,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(0, 8, 0, 2),
    });

    private void CopyEntry()
    {
        var text = _current?.Entry is { } e
            ? InvestmentKnowledgeBase.RenderEntry(e)
            : _current?.Category?.Body ?? "";
        Copy(text, "本条内容");
    }

    /// <summary>
    /// 复制 LLM 上下文：带上当前检索词/类别，让「AI 只检索相关类别」的规则落到实处。
    /// 后续自动接入 LLM 分析时，后台直接调用 InvestmentKnowledgeBase.BuildLlmContext 即可。
    /// </summary>
    private void CopyContext()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var category = SelectedCategoryCode();
        var ctx = Kb.BuildLlmContext(query, category, max: 8);
        Copy(ctx, "LLM 上下文");
    }

    private void Copy(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) { _status("没有可复制的内容"); return; }
        if (this.VisualRoot is TopLevel tl) _ = tl.Clipboard?.SetTextAsync(text);
        _status($"已复制{what}（{text.Length} 字），可粘贴到 LLM 对话框");
    }

    private void OpenSourceFile()
    {
        // 移动端（iOS/Android）无本地项目目录，Process.Start 也不受支持：直接提示
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            _status("移动端无本地项目目录，请在桌面端编辑知识库文件");
            return;
        }
        var kb = Kb;
        var path = kb.SourcePath;
        if (path.Length == 0 || path == "embedded" || !File.Exists(path))
        {
            _status("未找到知识库源文件（当前用的是内置副本），请确认 trading-system/09_投资经验知识库.md 存在");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            _status($"已打开 {Path.GetFileName(path)}，编辑保存后点「刷新」生效");
        }
        catch (Exception ex) { _status($"打开失败: {ex.Message}"); }
    }
}
