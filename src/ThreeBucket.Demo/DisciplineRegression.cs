using ThreeBucket.Core.Services;

namespace ThreeBucket.Demo;

/// <summary>
/// 交易纪律审核离线回归：规则解析、操作筛选、本地硬条件、LLM 结论解析容错、降级不放行。
/// 全部为合成文本 + 注入固定时间，不依赖网络与 LLM。
/// </summary>
public static class DisciplineRegression
{
    public static bool Run()
    {
        Console.WriteLine("=== 交易纪律审核离线回归（规则解析 / 硬条件 / 结论解析 / 降级） ===\n");
        var ok = true;
        ok &= CaseParseRules();
        ok &= CaseFilterByAction();
        ok &= CaseLocalChecks();
        ok &= CaseLocalChecksNoQuote();
        ok &= CaseParseLlmVerdict();
        ok &= CaseRejectFromRule();
        ok &= CaseExceptionNotApplicable();
        ok &= CaseExceptionRelievesRestriction();
        ok &= CaseAdvisoryNeverBlocks();
        ok &= CaseMissingRuleBecomesUnknown();
        ok &= CaseMalformedDegradesToFallback();
        ok &= CaseRealKnowledgeBase();
        Console.WriteLine(ok ? "\n交易纪律回归：全部通过 ✅" : "\n交易纪律回归：存在失败 ❌");
        return ok;
    }

    /// <summary>与仓库内真实 md 同构的合成纪律知识库。</summary>
    private const string Sample = """
        # 个人投资知识库 V1.0
        ## A股投资经验与决策规则

        **使用原则：**
        1. 经验不代表客观定律。

        ---

        # 05 交易纪律（DISCIPLINE）

        本节是下单前的硬性自检清单。

        ---

        ## DISC-001：早盘 10 点前不交易

        **纪律类型：** 限制

        **适用操作：** 买入 / 加仓

        **规则：**
        一般情况下不在上午 10:00 之前下单交易。

        **判定问题：**
        1. 当前北京时间是否已经过了 10:00？
        2. 是否属于大跌加仓例外？

        **通过条件：**
        - 当前时间 ≥ 10:00；或完全满足 DISC-002。

        **可被例外解除：** DISC-002

        **数据来源：** 系统自动

        标签：#交易时点 #纪律

        ---

        ## DISC-002：个股大跌可加仓博反弹

        **纪律类型：** 例外

        **适用操作：** 买入 / 加仓

        **规则：**
        当日跌幅超 5% 且个股与大环境均无重大利空时可加仓博反弹。

        **判定问题：**
        1. 当日跌幅是否 ≥ 5%？
        2. 个股近期是否有重大利空？
        3. 大环境是否有重大利空？

        **通过条件：**
        - 跌幅 ≥ 5% 且个股无重大利空 且 大环境无重大利空。

        **数据来源：** 系统自动 + 用户确认

        标签：#大跌加仓 #反弹

        ---

        ## DISC-004：加仓的仓位与止损边界

        **纪律类型：** 建议参考

        **适用操作：** 买入 / 加仓

        **规则：**
        博反弹属于短线仓位，必须有止损与止盈计划。

        **判定问题：**
        1. 是否已想好止损位？

        **通过条件：**
        - 建议参考，不否决。

        **数据来源：** 用户填写

        标签：#仓位管理
        """;

    private static DisciplineRuleSet BuildSet() =>
        DisciplineRuleSet.FromKnowledgeBase(InvestmentKnowledgeBase.FromMarkdown(Sample, "test"));

    /// <summary>固定事实：指定时间与跌幅，避免依赖真实行情。</summary>
    private static TradeReviewFacts Facts(DateTime now, double? changePct = -6.0,
        double? indexPct = -0.8, string code = "600519", string name = "贵州茅台")
        => new()
        {
            InputCode = code, Code = code, Name = name,
            Price = 1000m, PrevClose = 1063.83m, ChangePct = changePct,
            NowCn = now, IsTradingDay = true, IsMarketTime = true,
            IndexChangePct = indexPct,
        };

    /// <summary>用例 1：纪律字段解析（类型/适用操作/问题拆分/数据来源）。</summary>
    private static bool CaseParseRules()
    {
        var set = BuildSet();
        var r1 = set.Rules.FirstOrDefault(r => r.Id == "DISC-001");
        var r2 = set.Rules.FirstOrDefault(r => r.Id == "DISC-002");
        var r4 = set.Rules.FirstOrDefault(r => r.Id == "DISC-004");

        var pass = set.IsLoaded
            && set.Rules.Count == 3
            && r1 is { Kind: DisciplineKind.Restriction }        // 限制（可被例外解除）
            && r2 is { Kind: DisciplineKind.Exception }          // 例外（许可性）
            && r4 is { Kind: DisciplineKind.Advisory }
            && r1.Actions.SequenceEqual(new[] { TradeAction.Buy, TradeAction.Add })
            && r1.AllowedBy.SequenceEqual(new[] { "DISC-002" })  // 声明了可被 DISC-002 解除
            && r1.Questions.Count == 2
            && r2.Questions.Count == 3
            && r1.IsAutoJudgeable          // 数据来源=系统自动
            && !r2.IsAutoJudgeable         // 含用户确认 → 需追问
            && r2.NeedsUserInput
            && r2.RuleText.Contains("5%")
            && r1.BlocksWhenUnmet          // 限制不满足 → 否决
            && !r2.BlocksWhenUnmet;        // 例外不满足 → 不否决（关键）

        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D1: 纪律解析"
            + $"（{set.Rules.Count} 条；DISC-001 问题 {r1?.Questions.Count} 个 / 自动判定={r1?.IsAutoJudgeable}"
            + $"，DISC-002 需追问={r2?.NeedsUserInput}）");
        return pass;
    }

    /// <summary>用例 2：按操作筛选 —— 买入/加仓适用，卖出/减仓不适用这些条款。</summary>
    private static bool CaseFilterByAction()
    {
        var set = BuildSet();
        var add = set.ForAction(TradeAction.Add).Select(r => r.Id).ToList();
        var buy = set.ForAction(TradeAction.Buy).Select(r => r.Id).ToList();
        var sell = set.ForAction(TradeAction.Sell).ToList();

        var pass = add.SequenceEqual(new[] { "DISC-001", "DISC-002", "DISC-004" })
            && buy.SequenceEqual(add)
            && sell.Count == 0;   // 样例里没有适用于卖出的条款
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D2: 按操作筛选"
            + $"（加仓 {string.Join("/", add)}；卖出 {sell.Count} 条）");
        return pass;
    }

    /// <summary>
    /// 用例 3：本地硬条件 —— 10:00 前后、跌幅是否达标。
    /// 09:40 的 DISC-001 判 fail（限制未满足），但可被 DISC-002 例外解除 → Conclusive=false。
    /// DISC-002 的跌幅无论达标与否都不 Conclusive（它是例外的子条件，还需利空确认）。
    /// </summary>
    private static bool CaseLocalChecks()
    {
        var before = DisciplineReviewEngine.RunLocalChecks(
            Facts(new DateTime(2026, 11, 3, 9, 40, 0)), TradeAction.Add);
        var after = DisciplineReviewEngine.RunLocalChecks(
            Facts(new DateTime(2026, 11, 3, 10, 5, 0)), TradeAction.Add);

        var c1Before = before.First(c => c.RuleId == "DISC-001");
        var c1After = after.First(c => c.RuleId == "DISC-001");
        var c2Drop = before.First(c => c.RuleId == "DISC-002");

        var pass = c1Before.Verdict == RuleVerdict.Fail       // 9:40 → 限制未满足
            && !c1Before.Conclusive                            // 但可被例外解除
            && c1After.Verdict == RuleVerdict.Pass             // 10:05 → 不受限
            && c1After.Conclusive                              // 足以决定
            && c1Before.Text.Contains("09:40")
            && c2Drop.Verdict == RuleVerdict.Pass              // -6% 达到 5% 阈值
            && !c2Drop.Conclusive                              // 仍需利空确认
            && c2Drop.Text.Contains("6.00");
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D3: 本地硬条件"
            + $"（09:40→{c1Before.VerdictText}(决定性={c1Before.Conclusive})，"
            + $"10:05→{c1After.VerdictText}(决定性={c1After.Conclusive})，"
            + $"跌幅→{c2Drop.VerdictText}(决定性={c2Drop.Conclusive})）");
        return pass;
    }

    /// <summary>用例 3b：跌幅不足 5% → 大跌例外不适用（不否决）；行情缺失时不产出该结论（不猜）。</summary>
    private static bool CaseLocalChecksNoQuote()
    {
        var small = DisciplineReviewEngine.RunLocalChecks(
            Facts(new DateTime(2026, 11, 3, 11, 0, 0), changePct: -3.2), TradeAction.Add);
        var missing = DisciplineReviewEngine.RunLocalChecks(
            Facts(new DateTime(2026, 11, 3, 11, 0, 0), changePct: null), TradeAction.Add);

        var smallDrop = small.First(c => c.RuleId == "DISC-002");
        var pass = smallDrop.Verdict == RuleVerdict.Fail
            && !smallDrop.Conclusive
            && smallDrop.Text.Contains("不影响")
            && missing.All(c => c.RuleId != "DISC-002")   // 无行情不下结论
            && missing.First(c => c.RuleId == "DISC-001").Verdict == RuleVerdict.Pass;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D3b: 跌幅不足/行情缺失"
            + $"（-3.2%→{smallDrop.VerdictText}「例外不适用」；"
            + $"无行情时 DISC-002 结论数={missing.Count(c => c.RuleId == "DISC-002")}）");
        return pass;
    }

    /// <summary>用例 4：LLM JSON 解析（含代码块包裹、编造编号丢弃、漏判补待确认）。</summary>
    private static bool CaseParseLlmVerdict()
    {
        var set = BuildSet();
        var rules = set.ForAction(TradeAction.Add);
        var facts = Facts(new DateTime(2026, 11, 3, 10, 30, 0));

        const string llm = """
            好的，以下是审核结论：
            ```json
            {"summary":"跌幅达标且无利空，符合纪律。","caveats":["注意止损"],
             "results":[
              {"rule_id":"DISC-001","verdict":"pass","reason":"10:30 已过 10:00","source":"系统自动"},
              {"rule_id":"DISC-002","verdict":"pass","reason":"跌 6.00%，无利空","source":"用户确认"},
              {"rule_id":"DISC-999","verdict":"fail","reason":"编造的编号"}
             ]}
            ```
            以上。
            """;

        var review = DisciplineReviewEngine.ParseReview(llm, facts, TradeAction.Add, "2000股", rules, set);

        var pass = review is not null
            && review.Results.Count == 3                       // 999 被丢弃，3 条真实规则齐全
            && review.Results.All(r => r.RuleId != "DISC-999")
            && review.Results.First(r => r.RuleId == "DISC-001").Verdict == RuleVerdict.Pass
            && review.Results.First(r => r.RuleId == "DISC-004").Verdict == RuleVerdict.Advisory
            && review.Verdict == ReviewVerdict.Pass
            && review.Allowed
            && review.Caveats.Contains("注意止损")
            && review.Summary.Contains("符合纪律");
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D4: LLM 结论解析"
            + $"（结论={review?.VerdictText}，条目 {review?.Results.Count}，"
            + $"编造编号已丢弃={review?.Results.All(r => r.RuleId != "DISC-999")}）");
        return pass;
    }

    /// <summary>用例 5：LLM 判 fail → 整笔否决（硬条件优先于其它通过项）。</summary>
    private static bool CaseRejectFromRule()
    {
        var set = BuildSet();
        var rules = set.ForAction(TradeAction.Add);
        var facts = Facts(new DateTime(2026, 11, 3, 9, 40, 0));

        const string llm = """
            {"summary":"早盘时点不满足。","caveats":[],
             "results":[
              {"rule_id":"DISC-001","verdict":"fail","reason":"09:40 早于 10:00","source":"系统自动"},
              {"rule_id":"DISC-002","verdict":"fail","reason":"个股有业绩暴雷利空","source":"用户确认"},
              {"rule_id":"DISC-004","verdict":"advisory","reason":"未说明止损","source":"用户填写"}
             ]}
            """;

        var review = DisciplineReviewEngine.ParseReview(llm, facts, TradeAction.Add, "", rules, set);
        var pass = review is not null
            && review.Verdict == ReviewVerdict.Reject
            && !review.Allowed
            && review.VerdictText.Contains("不符合");
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D5: 否决判定（{review?.VerdictText}，可操作={review?.Allowed}）");
        return pass;
    }

    /// <summary>
    /// 用例 5b：**例外条款「不适用」绝不能否决正常交易**。
    /// 回归背景：DISC-002 曾按「条件例外」当义务条款，导致「当天没跌 5%」把 11:00 的正常买入判成违规。
    /// 例外不成立只代表「本次不适用」，交易仍应正常通过。
    /// </summary>
    private static bool CaseExceptionNotApplicable()
    {
        var set = BuildSet();
        var rules = set.ForAction(TradeAction.Add);
        // 11:00（已过 10:00）+ 只跌 1%（未达大跌标准）→ 完全正常的计划性买入
        var facts = Facts(new DateTime(2026, 11, 3, 11, 0, 0), changePct: -1.0);

        // ① LLM 正确判 unknown（不适用）→ 整笔通过
        var llmOk = DisciplineReviewEngine.ParseReview("""
            {"summary":"正常计划买入。","caveats":[],
             "results":[
              {"rule_id":"DISC-001","verdict":"pass","reason":"11:00 已过 10:00","source":"系统自动"},
              {"rule_id":"DISC-002","verdict":"unknown","reason":"跌幅 1%，大跌例外不适用","source":"系统自动"},
              {"rule_id":"DISC-004","verdict":"advisory","reason":"已设止损","source":"用户填写"}
             ]}
            """, facts, TradeAction.Add, "", rules, set);

        // ② LLM 误判 fail → 解析层必须降级为 unknown，不允许否决
        var llmBad = DisciplineReviewEngine.ParseReview("""
            {"summary":"跌得不够。","caveats":[],
             "results":[
              {"rule_id":"DISC-001","verdict":"pass","reason":"11:00","source":"系统自动"},
              {"rule_id":"DISC-002","verdict":"fail","reason":"跌幅不足5%","source":"系统自动"},
              {"rule_id":"DISC-004","verdict":"advisory","reason":"ok","source":"用户填写"}
             ]}
            """, facts, TradeAction.Add, "", rules, set);

        var d2Bad = llmBad?.Results.FirstOrDefault(r => r.RuleId == "DISC-002");

        // ③ 降级路径同样不能因例外不适用而否决
        var fb = DisciplineReviewEngine.FallbackReview(facts, TradeAction.Add, "", rules, "x", set);

        var pass = llmOk is not null
            && llmOk.Verdict == ReviewVerdict.Pass
            && llmOk.Allowed
            && d2Bad?.Verdict == RuleVerdict.Unknown          // 误判 fail 被降级
            && llmBad.Verdict == ReviewVerdict.Pass           // 不否决
            && fb.Verdict == ReviewVerdict.Pass               // 降级模式也放行
            && fb.Allowed;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D5b: 例外不适用不否决"
            + $"（LLM 判 unknown→{llmOk?.VerdictText}；LLM 误判 fail→{d2Bad?.VerdictText}/整笔{llmBad?.VerdictText}；"
            + $"降级→{fb.VerdictText}）");
        return pass;
    }

    /// <summary>
    /// 用例 5c：例外成立时解除限制 —— 9:40 大跌 6% 且无利空 → DISC-002 通过 → DISC-001 被豁免 → 整笔放行。
    /// 这正是用户描述的「10 点前不交易，但大跌可博反弹」规则。
    /// </summary>
    private static bool CaseExceptionRelievesRestriction()
    {
        var set = BuildSet();
        var rules = set.ForAction(TradeAction.Add);
        var facts = Facts(new DateTime(2026, 11, 3, 9, 40, 0), changePct: -6.0);

        var review = DisciplineReviewEngine.ParseReview("""
            {"summary":"大跌无利空，按例外放行。","caveats":[],
             "results":[
              {"rule_id":"DISC-001","verdict":"fail","reason":"09:40 早于 10:00","source":"系统自动"},
              {"rule_id":"DISC-002","verdict":"pass","reason":"跌 6.00%，个股与大环境均无利空（用户确认）","source":"用户确认"},
              {"rule_id":"DISC-004","verdict":"advisory","reason":"已设止损 -3%","source":"用户填写"}
             ]}
            """, facts, TradeAction.Add, "", rules, set);

        var d1 = review?.Results.FirstOrDefault(r => r.RuleId == "DISC-001");
        var pass = review is not null
            && d1?.Verdict == RuleVerdict.Fail          // 限制本身仍记 fail（如实记录）
            && d1.RelievedBy == "DISC-002"              // 但被例外解除
            && review.Verdict == ReviewVerdict.Pass     // 因此整笔放行
            && review.Allowed;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D5c: 例外解除限制"
            + $"（DISC-001={d1?.VerdictText}，被 {d1?.RelievedBy} 解除；整笔={review?.VerdictText}）");
        return pass;
    }

    /// <summary>用例 6：建议参考类即使 LLM 误判 fail 也不能否决（强制降级为 advisory）。</summary>
    private static bool CaseAdvisoryNeverBlocks()
    {
        var set = BuildSet();
        var rules = set.ForAction(TradeAction.Add);
        var facts = Facts(new DateTime(2026, 11, 3, 10, 30, 0));

        const string llm = """
            {"summary":"仓位安排不足。","caveats":[],
             "results":[
              {"rule_id":"DISC-001","verdict":"pass","reason":"10:30","source":"系统自动"},
              {"rule_id":"DISC-002","verdict":"pass","reason":"跌6%无利空","source":"用户确认"},
              {"rule_id":"DISC-004","verdict":"fail","reason":"没设止损","source":"用户填写"}
             ]}
            """;

        var review = DisciplineReviewEngine.ParseReview(llm, facts, TradeAction.Add, "", rules, set);
        var d4 = review?.Results.FirstOrDefault(r => r.RuleId == "DISC-004");
        var pass = review is not null
            && d4?.Verdict == RuleVerdict.Advisory     // 被强制改回 advisory
            && review.Verdict == ReviewVerdict.Pass;   // 不否决
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D6: 建议参考不否决"
            + $"（DISC-004 原 fail → {d4?.VerdictText}；整笔={review?.VerdictText}）");
        return pass;
    }

    /// <summary>
    /// 用例 7：LLM 漏判时补「待确认」——但只有**限制/硬性禁止**漏判才卡住交易；
    /// 例外条款漏判按「不适用」处理，不影响正常交易（否则没大跌就永远过不了）。
    /// </summary>
    private static bool CaseMissingRuleBecomesUnknown()
    {
        var set = BuildSet();
        var rules = set.ForAction(TradeAction.Add);

        // ① 10:30 + 跌 6%：LLM 只判了 DISC-001，漏了 DISC-002（例外成立与否未知）
        //    但已过 10:00 时 DISC-001 已通过，例外是否成立不影响放行 → 整笔通过
        var lateFacts = Facts(new DateTime(2026, 11, 3, 10, 30, 0), changePct: -6.0);
        var late = DisciplineReviewEngine.ParseReview("""
            {"summary":"只判了一条。","caveats":[],
             "results":[{"rule_id":"DISC-001","verdict":"pass","reason":"10:30","source":"系统自动"}]}
            """, lateFacts, TradeAction.Add, "", rules, set);
        var lateD2 = late?.Results.FirstOrDefault(r => r.RuleId == "DISC-002");

        // ② 9:40：DISC-001 是限制且未满足，LLM 漏判 DISC-002（能不能豁免不知道）→ 必须待确认
        var earlyFacts = Facts(new DateTime(2026, 11, 3, 9, 40, 0), changePct: -6.0);
        var early = DisciplineReviewEngine.ParseReview("""
            {"summary":"只判了一条。","caveats":[],
             "results":[{"rule_id":"DISC-001","verdict":"fail","reason":"09:40 早于 10:00","source":"系统自动"}]}
            """, earlyFacts, TradeAction.Add, "", rules, set);
        var earlyD1 = early?.Results.FirstOrDefault(r => r.RuleId == "DISC-001");

        var pass = late is not null
            && lateD2?.Verdict == RuleVerdict.Unknown      // 例外漏判记 unknown
            && late.Verdict == ReviewVerdict.Pass          // 但不卡交易
            && late.Allowed
            && early is not null
            && earlyD1?.Verdict == RuleVerdict.Fail        // 限制未满足
            && early.Verdict == ReviewVerdict.Reject       // 例外未成立 → 否决 10 点前下单
            && !early.Allowed;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D7: 漏判补待确认"
            + $"（10:30 例外漏判→{late?.VerdictText} 可操作={late?.Allowed}；"
            + $"09:40 限制未满足→{early?.VerdictText} 可操作={early?.Allowed}）");
        return pass;
    }

    /// <summary>
    /// 用例 8：LLM 返回垃圾文本 → 解析返回 null，降级结论不乐观放行。
    /// 回归背景：DISC-002 是「跌幅够大 + 个股无利空 + 大环境无利空」三条件与，
    /// 早期本地检查只验跌幅，跌幅达标就判整条通过 —— 等于把没核实利空的交易直接放行。
    /// 现要求：只有「足以决定整条纪律」的检查才算数，仅确认子条件一律待确认。
    /// </summary>
    private static bool CaseMalformedDegradesToFallback()
    {
        var set = BuildSet();
        var rules = set.ForAction(TradeAction.Add);

        // ① 9:40 + 跌幅达标：限制未满足、例外条件（利空）未确认 → 不能放行（否决）
        var early = Facts(new DateTime(2026, 11, 3, 9, 40, 0));
        var parsed = DisciplineReviewEngine.ParseReview("这不是 JSON，模型跑题了。", early,
            TradeAction.Add, "", rules, set);
        var fb = DisciplineReviewEngine.FallbackReview(early, TradeAction.Add, "", rules, "LLM 返回无法解析", set);

        var d1 = fb.Results.First(r => r.RuleId == "DISC-001");
        var d2 = fb.Results.First(r => r.RuleId == "DISC-002");

        // ② 10:30 + 跌幅达标：时点已过（决定性通过）→ 正常放行
        var late = Facts(new DateTime(2026, 11, 3, 10, 30, 0));
        var fbLate = DisciplineReviewEngine.FallbackReview(late, TradeAction.Add, "", rules, "x", set);
        var lateD1 = fbLate.Results.First(r => r.RuleId == "DISC-001");
        var lateD2 = fbLate.Results.First(r => r.RuleId == "DISC-002");

        // ③ 跌幅不足：例外不适用 → 不否决正常交易
        var small = Facts(new DateTime(2026, 11, 3, 10, 30, 0), changePct: -1.5);
        var fbSmall = DisciplineReviewEngine.FallbackReview(small, TradeAction.Add, "", rules, "x", set);
        var smallD2 = fbSmall.Results.First(r => r.RuleId == "DISC-002");

        var pass = parsed is null                       // 解析失败要暴露
            // ① 9:40：限制默认未满足，但例外（大跌豁免）尚未确认 → 既不能放行也不能断言违规
            //    → 「待确认」同样是**不许下单**（不乐观放行的关键）
            && fb.Verdict == ReviewVerdict.Pending
            && !fb.Allowed
            && d1.Verdict == RuleVerdict.Unknown
            && d2.Verdict == RuleVerdict.Unknown        // 例外子条件达标但仍需确认
            && fb.Caveats.Count >= 1
            && fb.Summary.Contains("未能取得完整判定")
            // ② 10:30：时点已过 → 放行（例外是否成立都无所谓）
            && lateD1.Verdict == RuleVerdict.Pass
            && lateD2.Verdict == RuleVerdict.Unknown
            && fbLate.Verdict == ReviewVerdict.Pass
            && fbLate.Allowed
            // ③ 跌幅不足 → 例外不适用，交易照常通过
            && smallD2.Verdict == RuleVerdict.Unknown
            && fbSmall.Verdict == ReviewVerdict.Pass;

        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D8: 解析失败降级 / 不乐观放行"
            + $"（parsed=null：{parsed is null}；09:40→{fb.VerdictText}，"
            + $"10:30→{fbLate.VerdictText}，跌幅不足→{fbSmall.VerdictText}）");
        return pass;
    }

    /// <summary>
    /// 用例 9：真实知识库里的 DISCIPLINE 条款能被解析出来（结构不变量，不写死条数）。
    /// 防止「改了 md 但解析器认不出来」导致审核页无纪律可查。
    /// </summary>
    private static bool CaseRealKnowledgeBase()
    {
        var kb = InvestmentKnowledgeBase.LoadDefault(AppContext.BaseDirectory);
        var set = DisciplineRuleSet.FromKnowledgeBase(kb);
        var ids = set.Rules.Select(r => r.Id).ToList();

        var pass = kb.IsLoaded
            && set.IsLoaded
            && ids.Contains("DISC-001")
            && ids.Contains("DISC-002")
            && set.Rules.All(r => r.Title.Length > 0)
            && set.Rules.All(r => r.Questions.Count > 0)
            // 真实文档里应同时存在「限制」（10 点前）与「例外」（大跌豁免）两种类型
            && set.Rules.Any(r => r.Kind == DisciplineKind.Restriction)
            && set.Rules.Any(r => r.Kind == DisciplineKind.Exception)
            // 例外声明要能被解析出来（否则豁免机制失效）
            && set.Rules.Any(r => r.AllowedBy.Count > 0)
            && set.ForAction(TradeAction.Add).Count >= 2;

        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} D9: 真实知识库纪律"
            + $"（{set.Rules.Count} 条：{string.Join("/", ids)}；"
            + $"例外解除声明 {set.Rules.Sum(r => r.AllowedBy.Count)} 条；"
            + $"加仓适用 {set.ForAction(TradeAction.Add).Count} 条）");
        return pass;
    }
}
