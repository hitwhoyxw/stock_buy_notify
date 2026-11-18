using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ThreeBucket.Core.Services;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

/// <summary>
/// 「交易纪律审核」页：给出股票 + 操作 → 系统拉客观事实 → LLM 依交易纪律逐条提问 →
/// 用户回答 → LLM 出结论（通过 / 有条件通过 / 否决）→ 用户再决定是否实际操作。
///
/// 设计原则（与知识库 05 DISCIPLINE 节配套）：
/// 1. 纪律条款只写在 md 里，本页只做「读取 + 执行」，避免 UI 与文档两份真源。
/// 2. 程序能算的硬条件（时间/跌幅）本地算好再喂给 LLM，LLM 不做算术。
/// 3. LLM 不可用时降级：只给已知项结论，其余一律「待确认」，绝不乐观放行。
/// </summary>
public partial class DisciplineView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;

    private TradeReviewFacts? _facts;
    private DisciplineReview? _lastReview;
    private bool _running;
    private CancellationTokenSource? _cts;

    private static readonly (string Text, TradeAction Action)[] Actions =
    {
        ("买入", TradeAction.Buy),
        ("加仓", TradeAction.Add),
        ("卖出", TradeAction.Sell),
        ("减仓", TradeAction.Reduce),
    };

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public DisciplineView() : this(new AppState(), _ => { }) { }

    public DisciplineView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;

        ActionCombo.ItemsSource = Actions.Select(a => a.Text).ToList();
        ActionCombo.SelectedIndex = 1;   // 默认「加仓」：本页高频场景（大跌加仓博反弹）
        // 操作变了，适用的纪律条款数量跟着变（DISC-001/002 只管买入/加仓）
        ActionCombo.SelectionChanged += (_, _) => UpdateRulesInfo();

        CollectBtn.Click += async (_, _) => await AskQuestionsAsync();
        ReviewBtn.Click += async (_, _) => await GetVerdictAsync();
        CancelBtn.Click += (_, _) => Cancel();
        CopyBtn.Click += (_, _) => CopyResult();
        SaveBtn.Click += (_, _) => SaveResult();
    }

    public void OnShown()
    {
        _app.RefreshKnowledgeBaseIfChanged();
        UpdateRulesInfo();
    }

    private TradeAction SelectedAction => Actions[Math.Clamp(ActionCombo.SelectedIndex, 0, Actions.Length - 1)].Action;

    private void UpdateRulesInfo()
    {
        var set = _app.DisciplineRules;
        if (!set.IsLoaded)
        {
            RulesInfo.Text = "⚠️ 知识库未找到「05 交易纪律（DISCIPLINE）」章节，"
                             + "请在 trading-system/09_投资经验知识库.md 里补上纪律条款";
            return;
        }
        var forAction = set.ForAction(SelectedAction);
        var blocking = forAction.Count(r => DisciplineRuleSet.IsBlocking(r.Kind));
        RulesInfo.Text = $"共 {set.Rules.Count} 条纪律 / 当前操作「{SelectedAction.ToCn()}」适用 {forAction.Count} 条"
                         + $"（其中 {blocking} 条可否决）";
    }

    // ── ① 拉事实 + 生成问题 ───────────────────────────────────────

    private async Task AskQuestionsAsync()
    {
        if (_running) { _status("正在处理，请等待…"); return; }

        var code = DisciplineFactsCollector.NormalizeCode(CodeBox.Text ?? "");
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            _status("⚠️ 请输入 6 位股票代码（如 600519）");
            return;
        }

        var set = _app.DisciplineRules;
        if (!set.IsLoaded)
        {
            _status("⚠️ 未找到交易纪律章节，无法审核");
            return;
        }

        var action = SelectedAction;
        var rules = set.ForAction(action);
        if (rules.Count == 0)
        {
            _status($"⚠️ 当前操作「{action.ToCn()}」没有适用的纪律条款");
            return;
        }

        SetRunning(true, "⏳ 拉取行情/指数…");
        _cts = new CancellationTokenSource();
        try
        {
            _facts = await _app.DisciplineFacts.CollectAsync(code, ct: _cts.Token);
            SetStatus($"已取到 {(string.IsNullOrWhiteSpace(_facts.Name) ? code : _facts.Name)}"
                      + $" 行情 {(_facts.ChangePct is { } p ? $"{p:+0.00;-0.00}%" : "（缺失）")}"
                      + $"；指数 {(_facts.IndexChangePct is { } i ? $"{i:+0.00;-0.00}%" : "（缺失）")}");

            // 问题区先显示客观事实，再显示 LLM 追问
            RenderFacts(_facts);
            RenderLocalChecks(_facts, action);

            var llm = BuildClient();
            if (!llm.IsConfigured)
            {
                AddNote("⚠️ 未配置 LLM API（设置页填写 URL/Key 后才能自动提问）；"
                        + "下面是程序按纪律列出的待确认问题，可直接在下方补充回答后点「出结论」。");
                RenderRuleQuestions(rules);
                SetRunning(false, "✓ 已按纪律列出待确认项（未配置 LLM，未生成追问）");
                return;
            }

            var prompt = DisciplineReviewEngine.BuildQuestionPrompt(
                _facts, action, AmountBox.Text?.Trim() ?? "", rules, AnswersBox.Text?.Trim());
            var (ok, text) = await llm.ChatAsync(prompt, _cts.Token);
            if (_cts.IsCancellationRequested) { SetRunning(false, "已取消"); return; }
            if (!ok)
            {
                AddNote($"⚠️ LLM 提问失败：{text}\n下面是程序按纪律列出的待确认问题。");
                RenderRuleQuestions(rules);
                SetRunning(false, "LLM 提问失败，已退回程序生成的待确认项");
                return;
            }

            MarkdownRenderer.Render(QuestionPanel, text);
            SetRunning(false, "✓ 已生成待确认问题 —— 请在下方「你的补充回答」里逐条回复，再点「② 出结论」");
        }
        catch (Exception ex)
        {
            SetRunning(false, $"⚠️ 审核异常: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private LlmClient BuildClient()
    {
        var cfg = _app.Config;
        return new LlmClient(cfg.LlmApiUrl, cfg.LlmApiKey, cfg.LlmModel);
    }

    // ── ② 出结论 ──────────────────────────────────────────────────

    private async Task GetVerdictAsync()
    {
        if (_running) { _status("正在处理，请等待…"); return; }
        if (_facts is null)
        {
            _status("⚠️ 请先点「① 拉取事实 + 生成问题」");
            return;
        }

        var set = _app.DisciplineRules;
        var action = SelectedAction;
        var rules = set.ForAction(action);
        if (rules.Count == 0) { _status("⚠️ 没有适用的纪律条款"); return; }

        var amount = AmountBox.Text?.Trim() ?? "";
        var answers = AnswersBox.Text?.Trim() ?? "";
        var localChecks = DisciplineReviewEngine.RunLocalChecks(_facts, action);

        SetRunning(true, "⏳ 判定中…");
        _cts = new CancellationTokenSource();
        try
        {
            var llm = BuildClient();
            DisciplineReview review;
            if (!llm.IsConfigured)
            {
                review = DisciplineReviewEngine.FallbackReview(_facts, action, amount, rules,
                    "未配置 LLM API", set);
            }
            else
            {
                var prompt = DisciplineReviewEngine.BuildVerdictPrompt(
                    _facts, action, amount, rules, answers, localChecks);
                var (ok, text) = await llm.ChatAsync(prompt, _cts.Token);
                if (_cts.IsCancellationRequested) { SetRunning(false, "已取消"); return; }

                var parsed = ok ? DisciplineReviewEngine.ParseReview(text, _facts, action, amount, rules, set) : null;
                review = parsed ?? DisciplineReviewEngine.FallbackReview(_facts, action, amount, rules,
                    ok ? "LLM 返回内容无法解析为结论" : $"LLM 调用失败：{text}", set);
            }

            RenderReview(review);
            SetRunning(false, $"{review.VerdictText} —— "
                              + (review.Allowed ? "可以按计划操作" : "请勿下单，先处理未通过/待确认项"));
        }
        catch (Exception ex)
        {
            SetRunning(false, $"⚠️ 判定异常: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        SetRunning(false, "已取消");
    }

    // ── 渲染 ──────────────────────────────────────────────────────

    private void RenderFacts(TradeReviewFacts f)
    {
        QuestionPanel.Children.Clear();
        QuestionPanel.Children.Add(Header("📊 客观事实（系统采集）"));
        MarkdownRenderer.Render(IntoNew(QuestionPanel), f.ToMarkdown());
    }

    private void RenderLocalChecks(TradeReviewFacts f, TradeAction action)
    {
        var checks = DisciplineReviewEngine.RunLocalChecks(f, action);
        if (checks.Count == 0) return;
        QuestionPanel.Children.Add(Header("⚖️ 程序已判定（无需你回答）"));
        foreach (var c in checks)
        {
            QuestionPanel.Children.Add(new TextBlock
            {
                Text = $"{(c.Verdict == RuleVerdict.Pass ? "✅" : "❌")} 【{c.RuleId}】{c.Text}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse(c.Verdict == RuleVerdict.Pass ? "#27ae60" : "#c0392b")),
                Margin = new Avalonia.Thickness(0, 2, 0, 2),
            });
        }
    }

    /// <summary>未配置 LLM / 提问失败时，直接把纪律里的「判定问题」列出来让用户回答。</summary>
    private void RenderRuleQuestions(IReadOnlyList<DisciplineRule> rules)
    {
        QuestionPanel.Children.Add(Header("❓ 待你确认（按纪律条款列出）"));
        foreach (var r in rules)
        {
            if (r.Questions.Count == 0) continue;
            QuestionPanel.Children.Add(new TextBlock
            {
                Text = $"【{r.Id}】{r.Title}（{r.KindText}）",
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                Margin = new Avalonia.Thickness(0, 8, 0, 2),
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var q in r.Questions)
                QuestionPanel.Children.Add(new TextBlock
                {
                    Text = "· " + q,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(6, 1, 0, 1),
                });
        }
    }

    private void RenderReview(DisciplineReview review)
    {
        _lastReview = review;
        ResultPanel.Children.Clear();

        // 结论横幅
        var color = review.Verdict switch
        {
            ReviewVerdict.Pass => "#27ae60",
            ReviewVerdict.Pending => "#e67e22",
            _ => "#c0392b",
        };
        ResultPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse(color)),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(10, 8),
            Child = new TextBlock
            {
                Text = review.VerdictText,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
            },
        });

        // 逐条判定
        foreach (var r in review.Results)
        {
            // 被例外解除的项按「通过」显示（附解除说明），避免用户以为仍被拒绝
            var effective = r.RelievedBy is not null ? RuleVerdict.Pass : r.Verdict;
            var (icon, rgb) = effective switch
            {
                RuleVerdict.Pass => ("✅", "#27ae60"),
                RuleVerdict.Fail => ("❌", "#c0392b"),
                RuleVerdict.Unknown => ("⚠️", "#e67e22"),
                _ => ("ℹ️", "#7f8c8d"),
            };
            var panel = new StackPanel { Spacing = 1, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            var label = $"{icon} 【{r.RuleId}】{r.RuleTitle} · "
                        + (r.RelievedBy is not null ? "已豁免" : r.VerdictText)
                        + (r.Kind switch
                        {
                            DisciplineKind.Advisory => "（建议参考）",
                            DisciplineKind.Exception => "（例外·不适用不影响交易）",
                            _ => "",
                        });
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(rgb)),
                TextWrapping = TextWrapping.Wrap,
            });
            if (r.Reason.Length > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = r.Reason,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#555")),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(18, 1, 0, 0),
                });
            ResultPanel.Children.Add(panel);
        }

        if (review.Caveats.Count > 0)
        {
            ResultPanel.Children.Add(Header("⚠️ 提醒"));
            foreach (var c in review.Caveats)
                ResultPanel.Children.Add(new TextBlock
                {
                    Text = "· " + c, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#e67e22")),
                    TextWrapping = TextWrapping.Wrap,
                });
        }

        if (review.Summary.Length > 0)
        {
            ResultPanel.Children.Add(Header("📝 总结"));
            ResultPanel.Children.Add(new TextBlock
            {
                Text = review.Summary, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.Parse("#2c5aa0")),
        Margin = new Avalonia.Thickness(0, 10, 0, 3),
        TextWrapping = TextWrapping.Wrap,
    };

    private static StackPanel IntoNew(Panel parent)
    {
        var p = new StackPanel();
        parent.Children.Add(p);
        return p;
    }

    private void AddNote(string text) => QuestionPanel.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#e67e22")),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(0, 6, 0, 2),
    });

    // ── 复制 / 存档 ───────────────────────────────────────────────

    private void CopyResult()
    {
        var text = _lastReview?.ToMarkdown() ?? CollectResultText();
        if (string.IsNullOrWhiteSpace(text)) { _status("没有可复制的结论"); return; }
        if (this.VisualRoot is TopLevel tl) _ = tl.Clipboard?.SetTextAsync(text);
        _status($"已复制审核结论（{text.Length} 字）");
    }

    private string CollectResultText()
        => ResultPanel.Children.Count == 0 ? "" : string.Join("\n",
            ResultPanel.Children.OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    private void SaveResult()
    {
        if (_lastReview is null) { _status("⚠️ 还没有可存档的结论，请先生成"); return; }
        var name = $"discipline_review_{_lastReview.ReviewedAt:yyyyMMdd}_{_lastReview.Code}.md";
        if (_app.Store.SaveText(name, _lastReview.ToMarkdown()))
            _status($"✓ 已存档到 data/{name}");
        else
            _status("✗ 存档失败（磁盘写入异常）");
    }

    // ── 状态 ──────────────────────────────────────────────────────

    private void SetRunning(bool running, string status)
    {
        _running = running;
        CollectBtn.IsEnabled = !running;
        ReviewBtn.IsEnabled = !running;
        CancelBtn.IsVisible = running;
        _status(status);
        SetStatus(status);
    }

    private void SetStatus(string msg) => Dispatcher.UIThread.Post(() => StatusText.Text = msg);
}
