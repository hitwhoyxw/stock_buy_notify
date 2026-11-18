using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThreeBucket.Core.Services;

/// <summary>单条纪律的判定结果。</summary>
public enum RuleVerdict
{
    /// <summary>通过。</summary>
    Pass,
    /// <summary>否决（硬性禁止不满足，或条件例外的条件不成立）。</summary>
    Fail,
    /// <summary>条件不足以判定（数据缺失 / 需用户确认但未提供）。</summary>
    Unknown,
    /// <summary>建议参考类：仅提示。</summary>
    Advisory,
}

/// <summary>一条纪律的审核结论。</summary>
public sealed class RuleResult
{
    public string RuleId { get; init; } = "";
    public string RuleTitle { get; init; } = "";
    public DisciplineKind Kind { get; init; }
    public RuleVerdict Verdict { get; init; }
    /// <summary>判定依据（引用具体数据，如「现价 9.50，昨收 10.00，跌 5.00%」）。</summary>
    public string Reason { get; init; } = "";
    /// <summary>该结论的数据来源：「系统自动」/「用户确认」/「LLM 判断」。</summary>
    public string Source { get; init; } = "";

    public bool IsBlocking => DisciplineRuleSet.IsBlocking(Kind);
    public bool IsFailure => Verdict == RuleVerdict.Fail;

    /// <summary>本条是否真的构成否决（例外条款「不适用」不算否决）。</summary>
    public bool Blocks => IsBlocking && Verdict == RuleVerdict.Fail;

    /// <summary>是否被某条例外条款解除（由审核引擎填充）。</summary>
    public string? RelievedBy { get; init; }

    public string VerdictText => Verdict switch
    {
        RuleVerdict.Pass => "通过",
        RuleVerdict.Fail => "不通过",
        RuleVerdict.Unknown => "待确认",
        _ => "提示",
    };
}

/// <summary>判定结果的中文文案（规则结论与本地硬条件共用，避免两处口径漂移）。</summary>
public static class RuleVerdictText
{
    public static string ToCn(this RuleVerdict v) => v switch
    {
        RuleVerdict.Pass => "通过",
        RuleVerdict.Fail => "不通过",
        RuleVerdict.Unknown => "待确认",
        _ => "提示",
    };
}

/// <summary>整笔交易的审核结论。</summary>
public enum ReviewVerdict
{
    /// <summary>通过：全部硬性纪律满足。</summary>
    Pass,
    /// <summary>待确认：无否决项，但有关键纪律尚未确认 → 确认前不得下单。</summary>
    Pending,
    /// <summary>否决：至少一条硬性纪律不通过。</summary>
    Reject,
}

/// <summary>一笔交易的完整审核结果。</summary>
public sealed class DisciplineReview
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public TradeAction Action { get; init; }
    public string Amount { get; init; } = "";
    public DateTime ReviewedAt { get; init; }

    public TradeReviewFacts? Facts { get; init; }
    public IReadOnlyList<RuleResult> Results { get; init; } = Array.Empty<RuleResult>();
    /// <summary>给用户看的总结（1~3 句）。</summary>
    public string Summary { get; init; } = "";
    /// <summary>提醒事项（如「数据缺失」「待确认项」）。</summary>
    public IReadOnlyList<string> Caveats { get; init; } = Array.Empty<string>();

    public ReviewVerdict Verdict
    {
        get
        {
            // 只统计真正构成否决的项：例外条款「不适用」不算否决；
            // 被已成立例外解除的限制也不算否决。
            if (Results.Any(r => r.Blocks && r.RelievedBy is null)) return ReviewVerdict.Reject;
            if (Results.Any(r => r.IsBlocking && r.Verdict == RuleVerdict.Unknown && r.RelievedBy is null))
                return ReviewVerdict.Pending;
            return ReviewVerdict.Pass;
        }
    }

    public string VerdictText => Verdict switch
    {
        ReviewVerdict.Pass => "✅ 符合交易纪律",
        ReviewVerdict.Pending => "⏳ 待确认（确认前不要下单）",
        _ => "❌ 不符合交易纪律",
    };

    /// <summary>
    /// 是否已获准操作：**只有「通过」才允许下单**。
    /// 待确认意味着关键纪律（如是否无重大利空）还没核实，此时下单等于绕过纪律，
    /// 因此不计入「通过」。这正是本功能的存在意义。
    /// </summary>
    public bool Allowed => Verdict == ReviewVerdict.Pass;

    /// <summary>渲染成 markdown（UI 展示 / 存档）。</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 交易纪律审核 · {Name}（{Code}）");
        sb.AppendLine();
        sb.AppendLine($"- 操作：{Action.ToCn()}{(Amount.Length > 0 ? " · " + Amount : "")}");
        sb.AppendLine($"- 审核时间：{ReviewedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- 结论：**{VerdictText}**");
        sb.AppendLine();
        sb.AppendLine("## 客观事实");
        sb.AppendLine(Facts?.ToMarkdown() ?? "- 数据缺失（未采集到行情事实）");
        sb.AppendLine();
        sb.AppendLine("## 逐条纪律判定");
        sb.AppendLine();
        sb.AppendLine("| 编号 | 纪律 | 类型 | 判定 | 依据 |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in Results)
        {
            // 被例外解除的项在存档里也要看得出「本来不通过，但被豁免」
            var verdict = r.RelievedBy is not null ? $"已豁免（{r.RelievedBy}）" : r.VerdictText;
            sb.AppendLine($"| {r.RuleId} | {Esc(r.RuleTitle)} | {KindText(r.Kind)} | {verdict} | {Esc(r.Reason)} |");
        }

        if (Caveats.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 提醒");
            foreach (var c in Caveats) sb.AppendLine($"- {c}");
        }
        if (Summary.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 总结");
            sb.AppendLine(Summary);
        }
        return sb.ToString().TrimEnd();
    }

    private static string Esc(string s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");
    private static string KindText(DisciplineKind k) => k switch
    {
        DisciplineKind.HardBan => "硬性禁止",
        DisciplineKind.Restriction => "限制",
        DisciplineKind.Exception => "例外",
        _ => "建议参考",
    };
}

/// <summary>
/// 交易纪律审核引擎：把规则 + 客观事实 + 用户回答拼成提示词交给 LLM 逐条判定，
/// 再解析回结构化结论。
///
/// 设计要点：
/// 1. **先算硬条件**：能由程序判定的（当前时间、跌幅阈值）本地先算出来写进提示词，
///    并作为「权威事实」告知 LLM —— 避免 LLM 算错算术或看错时间。
/// 2. **LLM 只做定性判断**：利空认定、计划性 vs 情绪化等主观项交给 LLM + 用户回答。
/// 3. **解析失败不静默**：JSON 解析不出来时，用本地硬条件兜底给出结论并标注降级，
///    绝不让「解析失败」变成「通过」。
/// </summary>
public static class DisciplineReviewEngine
{
    /// <summary>DISC-002 的跌幅阈值（%）：当日跌幅 ≥ 该值视为「大跌」。</summary>
    public const double BigDropThresholdPct = 5.0;
    /// <summary>DISC-001 的时点阈值：10:00 之前不下单。</summary>
    public static readonly TimeSpan NoTradeBefore = new(10, 0, 0);

    // ── 提示词 ────────────────────────────────────────────────────

    /// <summary>
    /// 生成「逐条询问」提示词：让 LLM 先列出需要用户补充的问题清单。
    /// 用于审核页的交互第一步（用户回答后再点「出结论」）。
    /// </summary>
    public static string BuildQuestionPrompt(
        TradeReviewFacts facts, TradeAction action, string amount, IReadOnlyList<DisciplineRule> rules,
        string? extraNote = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是交易纪律审核助手。用户准备下一笔交易，需要你按他/她的个人交易纪律逐条核查。");
        sb.AppendLine();
        sb.AppendLine("## 待审核交易");
        sb.AppendLine($"- 股票：{(facts.Name.Length > 0 ? facts.Name : "（名称未知）")}（{facts.Code}）");
        sb.AppendLine($"- 操作：{action.ToCn()}{(amount.Length > 0 ? " · " + amount : "")}");
        sb.AppendLine();
        sb.AppendLine("## 客观事实（系统自动采集，以此为准）");
        sb.AppendLine(facts.ToMarkdown());
        sb.AppendLine();

        if (rules.Count > 0)
        {
            sb.AppendLine("## 需要核查的纪律");
            foreach (var r in rules)
            {
                sb.AppendLine($"### {r.Id} · {r.Title}（{r.KindText}）");
                if (r.RuleText.Length > 0) sb.AppendLine(r.RuleText);
                if (r.PassCondition.Length > 0) sb.AppendLine($"通过条件：{r.PassCondition}");
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(extraNote))
        {
            sb.AppendLine("## 用户补充说明");
            sb.AppendLine(extraNote.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## 任务");
        sb.AppendLine("逐条检查上述纪律。凡是**需要用户确认或补充**才能判断的事项（例如个股是否有重大利空、大环境是否有重大利空、");
        sb.AppendLine("资金来源与止损计划等），请整理成问题清单向用户提问。");
        sb.AppendLine("凡是**能从客观事实直接判定**的（例如当前时间是否已过 10:00、跌幅是否达到 5%），直接给出判定，不要再问。");
        sb.AppendLine();
        sb.AppendLine("只输出问题清单，格式：");
        sb.AppendLine("1. 【编号】问题（为什么要问：对应哪条纪律）");
        sb.AppendLine("2. ...");
        sb.AppendLine("如果无需提问，输出：无需补充信息。");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 生成「出结论」提示词：规则 + 客观事实 + 用户回答 → 要求输出严格 JSON 结论。
    /// JSON 结构见 <see cref="ParseReview"/>。
    /// </summary>
    public static string BuildVerdictPrompt(
        TradeReviewFacts facts, TradeAction action, string amount, IReadOnlyList<DisciplineRule> rules,
        string? userAnswers, IReadOnlyList<LocalCheck>? localChecks = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是交易纪律审核助手。请严格依据下列个人交易纪律，判断这笔交易是否合规。");
        sb.AppendLine();
        sb.AppendLine("## 待审核交易");
        sb.AppendLine($"- 股票：{(facts.Name.Length > 0 ? facts.Name : "（名称未知）")}（{facts.Code}）");
        sb.AppendLine($"- 操作：{action.ToCn()}{(amount.Length > 0 ? " · " + amount : "")}");
        sb.AppendLine();
        sb.AppendLine("## 客观事实（系统自动采集，权威，不得改写）");
        sb.AppendLine(facts.ToMarkdown());
        sb.AppendLine();

        if (localChecks is { Count: > 0 })
        {
            sb.AppendLine("## 系统已判定的硬条件（权威结论，直接采用）");
            foreach (var c in localChecks)
                sb.AppendLine($"- 【{c.RuleId}】{c.Text}"
                              + (c.Conclusive ? "（该结论足以决定本条纪律）"
                                              : "（仅确认了部分条件，整条纪律仍须其余条件成立，否则判 unknown）"));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(userAnswers))
        {
            sb.AppendLine("## 用户回答（用户确认，视为事实）");
            sb.AppendLine(userAnswers.Trim());
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## 用户回答");
            sb.AppendLine("（用户未提供补充信息。凡依赖用户确认才能成立的纪律，一律判定为「待确认」，不得默认通过。）");
            sb.AppendLine();
        }

        sb.AppendLine("## 纪律清单");
        foreach (var r in rules)
        {
            sb.AppendLine($"### {r.Id} · {r.Title}（{r.KindText}）");
            if (r.RuleText.Length > 0) sb.AppendLine(r.RuleText);
            if (r.PassCondition.Length > 0) sb.AppendLine($"通过条件：{r.PassCondition}");
            if (r.Caution.Length > 0) sb.AppendLine($"注意事项：{r.Caution}");
            sb.AppendLine();
        }

        sb.AppendLine("## 判定规则（必须遵守）");
        sb.AppendLine("1. 先看「纪律类型」，不同类型后果完全不同：");
        sb.AppendLine("   - **硬性禁止**：不满足 → fail，整笔交易否决。");
        sb.AppendLine("   - **限制**：默认不允许该做法；不满足 → fail。但如果它声明的「可被例外解除」条款成立，则该限制被豁免，不构成否决（reason 里注明被哪个例外解除）。");
        sb.AppendLine("   - **例外**：这是**许可性**条款，不是义务。满足全部条件 → pass，并可解除指向它的限制；");
        sb.AppendLine("     **不满足 → 表示「本例外不适用」，用 unknown（reason 写明「例外不适用」），绝不能判 fail，更不能因此否决正常交易。**");
        sb.AppendLine("     例：DISC-002 要求跌幅≥5%；当天只跌 1% 的正常计划性买入，DISC-002 应为 unknown/不适用，交易可以正常通过。");
        sb.AppendLine("   - **建议参考**：verdict 固定 advisory，只提示，不否决。");
        sb.AppendLine("2. 判定依据必须引用具体数字（时间、跌幅、指数涨跌），不得空泛。");
        sb.AppendLine("3. 系统已判定的硬条件直接采用，不要推翻。");
        sb.AppendLine("4. 不得因为某条例外「不适用」就否决交易；只有限制/硬性禁止不通过才否决。");
        sb.AppendLine();

        sb.AppendLine("## 输出格式（只输出 JSON，不要任何解释文字、不要 markdown 代码块）");
        sb.AppendLine("""{"summary":"1~3 句总结","caveats":["需要提醒用户的事项"],"results":[{"rule_id":"DISC-001","verdict":"pass|fail|unknown|advisory","reason":"判定依据（引用具体数据）","source":"系统自动|用户确认|LLM 判断"}]}""");
        return sb.ToString().TrimEnd();
    }

    // ── 本地硬条件 ────────────────────────────────────────────────

    /// <summary>
    /// 程序可自行判定的硬条件结论（作为权威事实喂给 LLM，也是解析失败时的兜底）。
    ///
    /// <see cref="Conclusive"/> 区分两种情况，这是防「乐观放行」的关键：
    /// - true：这一条检查就能决定整条纪律（例如「已过 10:00」直接满足 DISC-001；
    ///   跌幅不足 5% 直接让 DISC-002 的三条件缺一，必然不成立）。
    /// - false：只确认了**部分子条件**（例如跌幅达标，但「无重大利空」还需用户确认），
    ///   整体纪律必须仍记为「待确认」，不得因为子条件满足就判通过。
    /// </summary>
    public sealed class LocalCheck
    {
        public string RuleId { get; init; } = "";
        public string Text { get; init; } = "";
        public RuleVerdict Verdict { get; init; }
        public string Reason { get; init; } = "";
        /// <summary>该检查是否足以决定整条纪律（false = 仅确认了部分条件）。</summary>
        public bool Conclusive { get; init; }
        public string VerdictText => Verdict.ToCn();
    }

    /// <summary>
    /// 本地能算的硬条件：
    /// DISC-001 的「10:00 前不下单」（时间已知；可被 DISC-002 例外解除）；
    /// DISC-002 的「跌幅 ≥ 5%」（例外的**子条件**之一，还需利空确认）。
    ///
    /// 语义要点：DISC-002 是**例外/许可**条款。跌幅不足 = 本例外不适用，
    /// **不否决正常交易**，因此这里给出 fail 但由 ApplyRelief/调用方按「不否决」处理。
    /// 早期把它当义务条款，会让「11 点正常买入」被误判为违规 —— 与本意完全相反。
    /// </summary>
    public static List<LocalCheck> RunLocalChecks(TradeReviewFacts f, TradeAction action)
    {
        var list = new List<LocalCheck>();

        // DISC-001：10 点前不交易（限制条款，只约束买入/加仓）
        if (action is TradeAction.Buy or TradeAction.Add)
        {
            var t = f.NowCn.TimeOfDay;
            var before = t < NoTradeBefore;
            // 早于 10:00 → 默认不通过，但可被 DISC-002 例外解除（需利空确认），故 Conclusive=false
            // 已过 10:00 → 决定性通过
            list.Add(new LocalCheck
            {
                RuleId = "DISC-001",
                Verdict = before ? RuleVerdict.Fail : RuleVerdict.Pass,
                Conclusive = !before,
                Text = before
                    ? $"当前北京时间 {f.NowCn:HH:mm}，早于 10:00 → 触发「早盘 10 点前不交易」限制"
                      + "（若同时满足大跌加仓的全部条件，可按例外放行 —— 需先确认利空情况）"
                    : $"当前北京时间 {f.NowCn:HH:mm}，已过 10:00 → 不受早盘时点限制",
                Reason = $"审核时间 {f.NowCn:HH:mm}（北京时间），阈值 10:00",
            });
        }

        // DISC-002：大跌例外（许可条款）。跌幅是准入子条件之一：
        // - 达标 → 只确认了子条件，利空仍需确认 → 非决定性（不得判通过）
        // - 不足 → 本例外不适用（不否决交易）
        if (f.ChangePct is { } pct)
        {
            var bigDrop = pct <= -BigDropThresholdPct;
            list.Add(new LocalCheck
            {
                RuleId = "DISC-002",
                Verdict = bigDrop ? RuleVerdict.Pass : RuleVerdict.Fail,
                Conclusive = false,   // 永远不会仅凭跌幅决定整条例外成立
                Text = bigDrop
                    ? $"当日跌幅 {pct:F2}%，已达「超过 {BigDropThresholdPct:F0}%」的大跌标准"
                      + "（是否无重大利空仍需确认，确认后才构成有效例外）"
                    : $"当日跌幅 {pct:F2}%，未达「超过 {BigDropThresholdPct:F0}%」的大跌标准"
                      + " → 大跌例外不适用（不影响按计划正常买入）",
                Reason = $"现价 {f.Price:F2} / 昨收 {f.PrevClose:F2} → {pct:F2}%",
            });
        }
        return list;
    }

    /// <summary>
    /// 应用例外解除：某条限制声明了「可被例外解除：X」，当 X 判定通过时，
    /// 该限制即使不通过也不构成否决（RelievedBy 记下解除者）。
    ///
    /// 例：DISC-001（10 点前不交易）在 9:40 判 fail，但 DISC-002（大跌加仓博反弹）成立时豁免。
    /// </summary>
    public static List<RuleResult> ApplyRelief(
        List<RuleResult> results, DisciplineRuleSet set, IReadOnlyList<DisciplineRule> rules)
    {
        var byId = results.ToDictionary(r => r.RuleId, StringComparer.OrdinalIgnoreCase);
        var patched = new List<RuleResult>(results.Count);

        foreach (var r in results)
        {
            // 例外本身不参与否决（不适用 ≠ 违规），无需解除
            if (r.Kind == DisciplineKind.Exception || r.Kind == DisciplineKind.Advisory || !r.IsBlocking)
            {
                patched.Add(r);
                continue;
            }
            if (r.Verdict != RuleVerdict.Fail) { patched.Add(r); continue; }

            var rule = rules.FirstOrDefault(x => string.Equals(x.Id, r.RuleId, StringComparison.OrdinalIgnoreCase));
            if (rule is null) { patched.Add(r); continue; }

            // 找出声明解除本条的例外，看它们是否已判定通过
            var relievers = rule.AllowedBy
                .Select(id => byId.TryGetValue(id, out var rr) ? rr : null)
                .Where(rr => rr is { Verdict: RuleVerdict.Pass })
                .Select(rr => rr!.RuleId)
                .ToList();

            patched.Add(relievers.Count > 0 ? CloneWith(r, relievers[0]) : r);
        }
        return patched;
    }

    private static RuleResult CloneWith(RuleResult r, string relievedBy) => new()
    {
        RuleId = r.RuleId,
        RuleTitle = r.RuleTitle,
        Kind = r.Kind,
        Verdict = r.Verdict,
        Reason = r.Reason + $"（已由例外条款 {relievedBy} 解除，不构成否决）",
        Source = r.Source,
        RelievedBy = relievedBy,
    };

    /// <summary>
    /// 用本地硬条件兜底出结论（LLM 不可用 / 解析失败时）：
    /// 只判定程序已知的项，其余一律 unknown（绝不乐观放行）。
    /// </summary>
    public static DisciplineReview FallbackReview(TradeReviewFacts f, TradeAction action, string amount,
        IReadOnlyList<DisciplineRule> rules, string reason, DisciplineRuleSet? set = null)
    {
        var checks = RunLocalChecks(f, action);
        var results = new List<RuleResult>();
        foreach (var r in rules)
        {
            if (r.Kind == DisciplineKind.Advisory)
            {
                results.Add(new RuleResult
                {
                    RuleId = r.Id, RuleTitle = r.Title, Kind = r.Kind,
                    Verdict = RuleVerdict.Advisory, Source = "系统自动",
                    Reason = "建议参考项，未参与否决",
                });
                continue;
            }
            var c = checks.FirstOrDefault(x => x.RuleId == r.Id);
            if (c is { Conclusive: true })
            {
                results.Add(new RuleResult
                {
                    RuleId = r.Id, RuleTitle = r.Title, Kind = r.Kind,
                    Verdict = c.Verdict, Reason = c.Text, Source = "系统自动",
                });
            }
            else if (c is not null)
            {
                // 只确认了部分子条件（如跌幅达标但利空未知）：整条纪律仍必须待确认，
                // 不得因为子条件成立就判通过 —— 这正是「乐观放行」的漏洞所在。
                results.Add(new RuleResult
                {
                    RuleId = r.Id, RuleTitle = r.Title, Kind = r.Kind,
                    Verdict = RuleVerdict.Unknown, Source = "部分确认",
                    Reason = c.Text + "；其余条件需人工确认（未取得结论）",
                });
            }
            else if (r.Kind == DisciplineKind.Exception)
            {
                // 例外条款：程序未确认其条件 → 记为「不适用」（不否决交易）
                results.Add(new RuleResult
                {
                    RuleId = r.Id, RuleTitle = r.Title, Kind = r.Kind,
                    Verdict = RuleVerdict.Unknown, Source = "未判定",
                    Reason = "例外条款未确认成立，按「本例外不适用」处理（不影响正常交易）",
                });
            }
            else
            {
                results.Add(new RuleResult
                {
                    RuleId = r.Id, RuleTitle = r.Title, Kind = r.Kind,
                    Verdict = RuleVerdict.Unknown, Source = "未判定",
                    Reason = "需人工/LLM 判断（本次未取得结论）",
                });
            }
        }

        if (set is not null) results = ApplyRelief(results, set, rules);

        return new DisciplineReview
        {
            Code = f.Code, Name = f.Name, Action = action, Amount = amount,
            ReviewedAt = f.NowCn, Facts = f, Results = results,
            Summary = $"未能取得完整判定（{reason}）。已按系统可见的客观数据给出部分结论，"
                      + "涉及利空认定等主观项请自行核对，勿当作已通过。",
            Caveats = new[] { $"降级模式：{reason}", "剩余项未经判定，需人工确认" },
        };
    }

    // ── 解析 LLM 结论 ─────────────────────────────────────────────

    /// <summary>
    /// 解析 LLM 返回的 JSON 结论。容忍 ```json 代码块包裹、前后多余文字。
    /// 解析不出时返回 null（由调用方降级，不静默当通过）。
    /// </summary>
    public static DisciplineReview? ParseReview(string? llmText, TradeReviewFacts facts,
        TradeAction action, string amount, IReadOnlyList<DisciplineRule> rules,
        DisciplineRuleSet? set = null)
    {
        var json = ExtractJsonObject(llmText);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            var caveats = new List<string>();
            if (root.TryGetProperty("caveats", out var cv) && cv.ValueKind == JsonValueKind.Array)
                foreach (var c in cv.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && (c.GetString() ?? "").Trim().Length > 0)
                        caveats.Add(c.GetString()!.Trim());

            var results = new List<RuleResult>();
            if (root.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rs.EnumerateArray())
                {
                    var id = Str(item, "rule_id").ToUpperInvariant();
                    var rule = rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (rule is null) continue;    // LLM 编造的编号直接丢弃
                    var verdict = ParseVerdict(Str(item, "verdict"));
                    // 建议参考类强制 advisory：LLM 若误判为 fail 不能否决交易
                    if (rule.Kind == DisciplineKind.Advisory) verdict = RuleVerdict.Advisory;
                    // 例外类「不满足」= 不适用，不是违规：LLM 误判 fail 时降级为 unknown，
                    // 否则「今天没大跌」会被当成违规，把正常买入全部否决。
                    if (rule.Kind == DisciplineKind.Exception && verdict == RuleVerdict.Fail)
                        verdict = RuleVerdict.Unknown;
                    results.Add(new RuleResult
                    {
                        RuleId = rule.Id,
                        RuleTitle = rule.Title,
                        Kind = rule.Kind,
                        Verdict = verdict,
                        Reason = Str(item, "reason"),
                        Source = Str(item, "source"),
                    });
                }
            }

            // LLM 漏判的规则补 unknown（不能因为没提就算了）
            foreach (var r in rules)
            {
                if (results.Any(x => x.RuleId == r.Id)) continue;
                results.Add(new RuleResult
                {
                    RuleId = r.Id, RuleTitle = r.Title, Kind = r.Kind,
                    Verdict = r.Kind == DisciplineKind.Advisory ? RuleVerdict.Advisory : RuleVerdict.Unknown,
                    Reason = "LLM 未给出该条判定", Source = "未判定",
                });
            }

            if (results.Count == 0) return null;

            // 例外解除：例外成立时，被它解除的限制不再否决
            if (set is not null) results = ApplyRelief(results, set, rules);

            return new DisciplineReview
            {
                Code = facts.Code, Name = facts.Name, Action = action, Amount = amount,
                ReviewedAt = facts.NowCn, Facts = facts, Results = results,
                Summary = summary, Caveats = caveats,
            };
        }
        catch { return null; }
    }

    private static RuleVerdict ParseVerdict(string s) => s.Trim().ToLowerInvariant() switch
    {
        "pass" or "通过" or "ok" => RuleVerdict.Pass,
        "fail" or "不通过" or "否决" or "reject" => RuleVerdict.Fail,
        "advisory" or "提示" => RuleVerdict.Advisory,
        _ => RuleVerdict.Unknown,
    };

    private static string Str(JsonElement o, string prop)
        => o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim() : "";

    private static readonly Regex CodeBlockJsonRe = new(
        @"```(?:json)?\s*(?<json>[\{\[].*?[\}\]])\s*```", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>从可能带代码块/前后缀的文本中提取首个 JSON 对象（按括号配对扫描，容忍字符串内的括号）。</summary>
    public static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var block = CodeBlockJsonRe.Match(text);
        var candidate = block.Success ? block.Groups["json"].Value : text;

        var start = candidate.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < candidate.Length; i++)
        {
            var ch = candidate[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return candidate[start..(i + 1)];
            }
        }
        return null;   // 括号不配对：视为解析失败
    }
}
