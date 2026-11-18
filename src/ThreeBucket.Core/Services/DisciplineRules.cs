using System.Text.RegularExpressions;

namespace ThreeBucket.Core.Services;

/// <summary>纪律类型：决定该条规则不满足时的后果。</summary>
public enum DisciplineKind
{
    /// <summary>硬性禁止：不满足即不得下单，无条件否决。</summary>
    HardBan,
    /// <summary>
    /// 限制：默认不允许该做法，但可被声明的「例外」条款解除。
    /// 例：「10 点前不交易」默认不通过，但 DISC-002 成立时解除。
    /// </summary>
    Restriction,
    /// <summary>
    /// 例外：**许可性**条款 —— 满足全部条件即解锁对应做法（并解除指向它的限制）。
    /// 不满足表示「本例外不适用」，**不是违规**，因此单独出现时不否决交易。
    /// </summary>
    Exception,
    /// <summary>建议参考：仅提示，不参与否决。</summary>
    Advisory,
}

/// <summary>交易操作类型（与知识库「适用操作」字段对应）。</summary>
public enum TradeAction
{
    Buy,
    Add,
    Sell,
    Reduce,
}

public static class TradeActionText
{
    /// <summary>中文名（UI 显示 / 提示词用）。</summary>
    public static string ToCn(this TradeAction a) => a switch
    {
        TradeAction.Buy => "买入",
        TradeAction.Add => "加仓",
        TradeAction.Sell => "卖出",
        TradeAction.Reduce => "减仓",
        _ => a.ToString(),
    };

    /// <summary>解析单个操作词；无法识别返回 null（「任意」由调用方表示"不限"）。</summary>
    public static TradeAction? Parse(string word)
    {
        var w = (word ?? "").Trim();
        if (w.Length == 0) return null;
        if (w.Contains("加仓", StringComparison.Ordinal) || w.Contains("增持", StringComparison.Ordinal)) return TradeAction.Add;
        if (w.Contains("减仓", StringComparison.Ordinal) || w.Contains("减持", StringComparison.Ordinal)) return TradeAction.Reduce;
        if (w.Contains("买入", StringComparison.Ordinal) || w.Contains("建仓", StringComparison.Ordinal)) return TradeAction.Buy;
        if (w.Contains("卖出", StringComparison.Ordinal) || w.Contains("清仓", StringComparison.Ordinal)) return TradeAction.Sell;
        return null;
    }
}

/// <summary>
/// 一条交易纪律（从知识库 DISCIPLINE 类别的条目解析而来）。
/// 「判定问题」用于让 LLM 逐条追问；「通过条件」是判定口径。
/// </summary>
public sealed class DisciplineRule
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DisciplineKind Kind { get; init; } = DisciplineKind.Advisory;
    /// <summary>适用操作；null/空 表示不限于特定操作（「任意」）。</summary>
    public IReadOnlyList<TradeAction> Actions { get; init; } = Array.Empty<TradeAction>();
    public string RuleText { get; init; } = "";
    public IReadOnlyList<string> Questions { get; init; } = Array.Empty<string>();
    public string PassCondition { get; init; } = "";
    /// <summary>数据来源原文：「系统自动」「用户确认」「用户填写」等。</summary>
    public string DataSource { get; init; } = "";
    public string Caution { get; init; } = "";
    /// <summary>
    /// 「可被例外解除：DISC-002」声明的例外条款编号。
    /// 该例外成立时，本限制即解除（否则限制仍然生效）。
    /// </summary>
    public IReadOnlyList<string> AllowedBy { get; init; } = Array.Empty<string>();

    /// <summary>该规则是否可由程序自动判定（数据来源含「系统自动」，且不需要用户确认）。</summary>
    public bool IsAutoJudgeable =>
        DataSource.Contains("系统自动", StringComparison.Ordinal)
        && !DataSource.Contains("用户确认", StringComparison.Ordinal)
        && !DataSource.Contains("用户填写", StringComparison.Ordinal);

    /// <summary>是否需要对用户追问（有利空/仓位这类主观事实的规则需要问）。</summary>
    public bool NeedsUserInput =>
        DataSource.Contains("用户确认", StringComparison.Ordinal)
        || DataSource.Contains("用户填写", StringComparison.Ordinal);

    /// <summary>该规则是否适用于指定操作（无操作限制时恒 true）。</summary>
    public bool AppliesTo(TradeAction action)
        => Actions.Count == 0 || Actions.Contains(action);

    /// <summary>
    /// 该条不通过时是否应否决交易。
    /// 例外条款属于「许可」，不满足只是不适用 → 不否决；限制/硬性禁止才否决。
    /// </summary>
    public bool BlocksWhenUnmet => Kind is DisciplineKind.HardBan or DisciplineKind.Restriction;

    public string KindText => Kind switch
    {
        DisciplineKind.HardBan => "硬性禁止",
        DisciplineKind.Restriction => "限制",
        DisciplineKind.Exception => "例外",
        _ => "建议参考",
    };
}

/// <summary>
/// 交易纪律规则集：从 <see cref="InvestmentKnowledgeBase"/> 的 DISCIPLINE 类别解析，
/// 并支持按操作类型筛选。知识库缺失时返回空集（审核页据此提示"未找到纪律章节"）。
/// </summary>
public sealed class DisciplineRuleSet
{
    /// <summary>知识库中的类别代码（`# 05 交易纪律（DISCIPLINE）`）。</summary>
    public const string CategoryCode = "DISCIPLINE";

    public IReadOnlyList<DisciplineRule> Rules { get; }
    public bool IsLoaded => Rules.Count > 0;

    private DisciplineRuleSet(IReadOnlyList<DisciplineRule> rules) => Rules = rules;

    public static DisciplineRuleSet Empty { get; } = new(Array.Empty<DisciplineRule>());

    /// <summary>只保留适用于该操作的规则（按知识库顺序，编号升序）。</summary>
    public IReadOnlyList<DisciplineRule> ForAction(TradeAction action)
        => Rules.Where(r => r.AppliesTo(action)).ToList();

    /// <summary>参与否决判定的规则（硬性禁止 + 限制；例外是许可、建议参考只提示）。</summary>
    public static bool IsBlocking(DisciplineKind kind)
        => kind is DisciplineKind.HardBan or DisciplineKind.Restriction;

    /// <summary>
    /// 找出解除某条限制的例外条款（按 「可被例外解除」 声明解析）。
    /// 例外成立时，该限制即不再否决。
    /// </summary>
    public IReadOnlyList<DisciplineRule> ReliefFor(DisciplineRule restriction)
        => restriction.AllowedBy
            .Select(id => Rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

    public static DisciplineRuleSet FromKnowledgeBase(InvestmentKnowledgeBase kb)
    {
        if (kb is null || !kb.IsLoaded) return Empty;

        var cat = kb.Categories.FirstOrDefault(c =>
            string.Equals(c.Code, CategoryCode, StringComparison.OrdinalIgnoreCase));
        if (cat is null || cat.Entries.Count == 0) return Empty;

        var rules = new List<DisciplineRule>();
        foreach (var e in cat.Entries)
        {
            var rule = FromEntry(e);
            if (rule is not null) rules.Add(rule);
        }
        return new DisciplineRuleSet(rules);
    }

    /// <summary>解析单条纪律；缺少「纪律类型」时按建议参考处理（保守：不因格式不全而误否决）。</summary>
    public static DisciplineRule? FromEntry(KnowledgeEntry e)
    {
        if (e is null || e.Id.Length == 0) return null;

        var kindText = Section(e, "纪律类型");
        // 顺序有讲究：「硬性禁止」优先；「例外」与「限制」分别对应许可型与可解除型；
        // 旧写法「条件例外」按限制处理（与历史行为一致，避免老文档判定口径突变）。
        var kind = kindText.Contains("硬性禁止", StringComparison.Ordinal) ? DisciplineKind.HardBan
            : kindText.Contains("例外", StringComparison.Ordinal) && !kindText.Contains("条件", StringComparison.Ordinal)
                ? DisciplineKind.Exception
            : kindText.Contains("限制", StringComparison.Ordinal) || kindText.Contains("条件例外", StringComparison.Ordinal)
                ? DisciplineKind.Restriction
            : DisciplineKind.Advisory;

        // 「适用操作：买入 / 加仓」→ [Buy, Add]；「任意」或缺失 → 空（不限）
        var actionText = Section(e, "适用操作");
        var actions = new List<TradeAction>();
        if (!actionText.Contains("任意", StringComparison.Ordinal))
        {
            foreach (var w in actionText.Split(new[] { '/', '、', ',', '，', ' ', '|' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (TradeActionText.Parse(w) is { } a && !actions.Contains(a)) actions.Add(a);
            }
        }

        // 「可被例外解除：DISC-002, DISC-005」→ [DISC-002, DISC-005]
        var allowedBy = new List<string>();
        foreach (var w in Section(e, "可被例外解除")
                     .Split(new[] { '/', '、', ',', '，', ' ', '|', ';', '；' },
                         StringSplitOptions.RemoveEmptyEntries))
        {
            var id = w.Trim().ToUpperInvariant();
            if (id.StartsWith("DISC-", StringComparison.Ordinal) && !allowedBy.Contains(id))
                allowedBy.Add(id);
        }

        return new DisciplineRule
        {
            Id = e.Id,
            Title = e.Title,
            Kind = kind,
            Actions = actions,
            RuleText = Section(e, "规则"),
            Questions = SplitQuestions(Section(e, "判定问题")),
            PassCondition = Section(e, "通过条件"),
            DataSource = Section(e, "数据来源"),
            Caution = Section(e, "注意事项"),
            AllowedBy = allowedBy,
        };
    }

    private static string Section(KnowledgeEntry e, string heading)
        => e.Sections.FirstOrDefault(s =>
               string.Equals(s.Heading.Trim(), heading, StringComparison.Ordinal))?.Body?.Trim() ?? "";

    private static readonly Regex QuestionLineRe = new(@"^\s*\d+\s*[.、)）]\s*(?<q>.+)$", RegexOptions.Compiled);

    /// <summary>把「判定问题」的编号清单拆成问句数组（无编号时按整段作为一个问题）。</summary>
    public static List<string> SplitQuestions(string body)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return list;

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = QuestionLineRe.Match(line);
            list.Add(m.Success ? m.Groups["q"].Value.Trim() : line);
        }
        return list;
    }
}
