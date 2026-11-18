using ThreeBucket.Core.Services;

namespace ThreeBucket.Demo;

/// <summary>
/// C 桶文本信号「时效性」离线回归：分档边界、多来源取最新、加权分不变式、无日期降级、
/// 活动记录按 URL 去重。全部合成数据，不依赖网络。
///
/// 核心不变式（本特性的硬约束）：**时效只影响排序，绝不改变 weighted_score 与 PASS/REJECT**。
/// </summary>
public static class FreshnessRegression
{
    public static bool Run()
    {
        Console.WriteLine("=== 时效性离线回归（分档 / 多来源 / 加权分不变式 / 降级） ===\n");
        var ok = true;
        ok &= CaseTierBoundaries();
        ok &= CaseLatestSourceWins();
        ok &= CaseScoreInvariance();
        ok &= CaseFreshScoreFloor();
        ok &= CaseNoDateDegrades();
        ok &= CaseSurveyOnlyHit();
        ok &= CaseYamlLoad();
        ok &= CasePerSourceAttribution();
        ok &= CaseSurveyGrouping();
        ok &= CaseLookbackWindow();
        ok &= CaseKeywordContextExtraction();
        Console.WriteLine(ok ? "\n时效性回归：全部通过 ✅" : "\n时效性回归：存在失败 ❌");
        return ok;
    }

    /// <summary>
    /// 用例 10：语义粒度过滤 —— 无关的问答对/闲聊整段丢弃，含关键词的问答保留。
    /// 背景（2026-09-29 用户指出）：互动平台问答里"分红/股东户数/产品闲聊"与景气无关，
    /// 却占输入文件 ~60% 体积，全部喂给了 LLM。
    /// </summary>
    private static bool CaseKeywordContextExtraction()
    {
        var pass = true;

        // ① 问答对粒度：无关问答对整对丢弃，命中问答对保留
        var qa = "问题1:公司分红比例未来会提升吗？\n" +
                 "回复:公司分红政策保持稳定，请关注后续公告。感谢关注。\n" +
                 "问题2:近期产品订单和价格情况如何？\n" +
                 "回复:受益于下游需求旺盛，公司在手订单充足，产品价格持续上涨，产能已排至年底。\n" +
                 "问题3:股东户数是多少？\n" +
                 "回复:截至上月末股东户数6.2万户，感谢关注。";
        var r1 = EarningsScanTask.ExtractKeywordContext(qa, 2000);
        Console.WriteLine($"  ①问答过滤({r1.Length}字): {r1.Replace("\n", "⏎")[..Math.Min(120, r1.Length)]}…");
        // 注：对2 含「价格持续上涨」而非连续子串「价格上涨」，断言按实际词写
        pass &= r1.Contains("需求旺盛") && r1.Contains("订单充足") && r1.Contains("持续上涨")
            && !r1.Contains("分红") && !r1.Contains("股东户数")   // 无关对整对消失
            && r1.Contains("问题2");                              // 命中对带问题原文

        // ② 问答式文本但全部无关 → 整场丢弃（注意"户"字不在词表、"增长"不在——
        //    若误命中说明测试文本选词不当；6.2万户 中"户"单字不构成任何词表词）
        var r2 = EarningsScanTask.ExtractKeywordContext(
            "问题1:分红方案如何？\n回复:维持既有政策。\n问题2:办公地址在哪？\n回复:上海市浦东新区某路88号。", 2000);
        Console.WriteLine($"  ②全无关问答 → 长度={r2.Length}（期望 0）");
        pass &= r2.Length == 0;

        // ③ 无问答边界的整段陈述 → 句窗口：命中句+语境在，闲聊不在
        var monologue = "投资者您好。" + new string('x', 50) +
            "感谢各位长期的陪伴与支持，公司始终重视股东回报。" +
            "行业层面，受益于下游需求旺盛，公司在手订单充足。谢谢大家。" +
            new string('y', 50);
        var r3 = EarningsScanTask.ExtractKeywordContext(monologue, 2000);
        Console.WriteLine($"  ③句窗口({r3.Length}字): {r3[..Math.Min(80, r3.Length)]}…");
        pass &= r3.Contains("需求旺盛") && r3.Contains("在手订单充足")
            && !r3.Contains("股东回报");

        // ④ pair 超限降级 + 大段无关内容被砍：
        //   回复正文与 600 字闲聊粘连成一个 673 字 pair（超 limit=200）→ pair 内句窗口，
        //   命中句+语境保留、闲聊丢弃；而未粘连的极小 limit 下标注截断。
        var long1 = string.Join("", Enumerable.Repeat("这是无关闲聊，讨论天气与吃饭，字数凑长一些。", 30));
        var long2 = "问题1:订单情况？\n回复:订单饱满，产品涨价。" + long1;
        var r4 = EarningsScanTask.ExtractKeywordContext(long2, 200);
        Console.WriteLine($"  ④pair降级({r4.Length}字): {r4.Replace("\n", "⏎")}");
        // 语境句按比例保留少量相邻文本是设计行为；断言验证闲聊**大部分**被砍
        pass &= r4.Contains("订单饱满") && r4.Contains("产品涨价") && r4.Length < 100;

        // ④b 多个**含词** pair 超 limit → 标注截断（无关对不占额度，触发不了截断）
        var long3 = "问题1:订单情况？\n回复:订单饱满，产品涨价，产能已排至年底。\n" +
            string.Join("", Enumerable.Repeat("问题9:价格？\n回复:价格上涨，供不应求，需求旺盛。", 30));
        var r4b = EarningsScanTask.ExtractKeywordContext(long3, 120);
        Console.WriteLine($"  ④b多pair限长({r4b.Length}字) 标注截断={r4b.Contains("全文")}");
        pass &= r4b.Contains("订单饱满") && r4b.Contains("全文");

        // ⑤ 短文原样返回
        var shortTxt = "产品价格上涨。";
        pass &= EarningsScanTask.ExtractKeywordContext(shortTxt, 600) == shortTxt;

        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F10: 语义粒度过滤"
            + "（无关问答对丢弃 / 命中对保留 / 句窗口 / 限长 / 短文原样）");
        return pass;
    }

    private static readonly DateTime Today = new(2026, 9, 18);

    /// <summary>用例 1：分档边界 45/46、90/91、180/181 天。</summary>
    private static bool CaseTierBoundaries()
    {
        var tiers = FreshnessConfig.Defaults().Tiers;
        var cases = new (int Days, double Want, string Label)[]
        {
            (0, 0.0, "新鲜"),
            (45, 0.0, "新鲜"),        // 边界：含 45
            (46, -0.5, "偏旧"),       // 边界：45 之外
            (90, -0.5, "偏旧"),       // 边界：含 90
            (91, -1.0, "陈旧"),
            (180, -1.0, "陈旧"),
            (181, -2.0, "极旧"),
            (9999, -2.0, "极旧"),
        };
        var pass = true;
        foreach (var (days, want, label) in cases)
        {
            var r = FreshnessResult.Evaluate(days, tiers);
            if (r.Penalty != want || r.Label != label)
            {
                Console.WriteLine($"  FAIL: {days} 天 → 期望 {want}/{label}，实得 {r.Penalty}/{r.Label}");
                pass = false;
            }
        }
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F1: 分档边界（0/45/46/90/91/180/181/9999 天）");
        return pass;
    }

    /// <summary>用例 2：多来源取最新时间戳（旧的预告 + 新的调研 → 按新的算）。</summary>
    private static bool CaseLatestSourceWins()
    {
        var yjyg = new List<YjygRow>
        {
            // 5 个月前的预告（陈旧），命中 price
            new("600519", "测试甲", "预增", 80, "预计净利润大幅增长", "受产品涨价影响，净利润大幅增长",
                "20260331", new DateTime(2026, 4, 25)),
        };
        var org = new Dictionary<string, List<OrgSurveyRow>>
        {
            // 3 周前的电话会议（新鲜），命中 demand
            ["600519"] = new()
            {
                new OrgSurveyRow("600519", "测试甲", new DateTime(2026, 8, 28), new DateTime(2026, 8, 27),
                    "电话会议", "机构投资者", "公司订单饱满，需求旺盛，产能已排至年底。", "u1"),
            },
        };
        var kw = EarningsScanTask.ScanKeywordHitsForTest(yjyg, org, Today, FreshnessConfig.Defaults());
        var hit = kw.GetValueOrDefault("600519");
        var pass = hit is not null
            && hit.LatestHitDate == new DateTime(2026, 8, 27)   // 取活动记录（更新）
            && hit.LatestHitKind == "电话会议"
            && hit.AgeDays == 22                                 // 9/18 − 8/27
            && hit.FreshnessPenalty == 0.0                       // ≤45 天 → 不扣
            && hit.Sources.Count == 2
            && hit.Hits.ContainsKey("price") && hit.Hits.ContainsKey("demand"); // 两来源都计入
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F2: 多来源取最新"
            + $"（阶段={hit?.LatestHitKind} 距今={hit?.AgeDays}天 扣分={hit?.FreshnessPenalty}）");
        return pass;
    }

    /// <summary>
    /// 用例 3（核心不变式）：相同文本下，加权分与是否命中**不受时效配置影响**。
    /// 同一个陈旧票，把分档调成"全 0 扣分"或"全 -2 扣分"，Score 必须完全相同。
    /// </summary>
    private static bool CaseScoreInvariance()
    {
        var yjyg = new List<YjygRow>
        {
            new("000001", "测试乙", "预增", 60, "", "产品涨价，需求旺盛，行业出清，产能紧张",
                "20260331", new DateTime(2026, 4, 25)),   // 距今 146 天 → 陈旧档
        };
        var none = new FreshnessConfig
        {
            Enabled = true,
            Tiers = new List<FreshnessTier> { new(9999, 0.0) },       // 全不扣
        };
        var harsh = new FreshnessConfig
        {
            Enabled = true,
            Tiers = new List<FreshnessTier> { new(1, -2.0), new(9999, -2.0) }, // 全扣 2
        };
        var a = EarningsScanTask.ScanKeywordHitsForTest(
            yjyg, new Dictionary<string, List<OrgSurveyRow>>(), Today, none).GetValueOrDefault("000001");
        var b = EarningsScanTask.ScanKeywordHitsForTest(
            yjyg, new Dictionary<string, List<OrgSurveyRow>>(), Today, harsh).GetValueOrDefault("000001");

        var pass = a is not null && b is not null
            && a.Score == b.Score                                     // ★ 加权分不变
            && a.Hits.Count == b.Hits.Count && a.Neg.Count == b.Neg.Count
            && a.FreshnessPenalty == 0.0 && b.FreshnessPenalty == -2.0 // 扣分确实不同
            && a.FreshScore == a.Score                                // 不扣 → 时效分=原分
            && b.FreshScore == Math.Max(0.0, b.Score - 2.0);          // 扣 2 分，且下限 0
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F3: 加权分不变式"
            + $"（Score={a?.Score} 不随时效配置变化；时效分 {a?.FreshScore} vs {b?.FreshScore}）");
        return pass;
    }

    /// <summary>
    /// 用例 3b：时效分下限为 0（不出现负分）。
    /// 注意：取数窗口（180 天）生效时，≥180 天的来源已被整条剔除，故 -2.0 档在**有日期**时
    /// 不可达；下限只在两种情况下真正生效——窗口关闭，或来源无日期但被外部赋予了扣分。
    /// 这里用"窗口关闭"构造（与 F9 互补：F9 验证窗口生效时会剔除，本用例验证窗口关闭时不剔除且下限成立）。
    /// </summary>
    private static bool CaseFreshScoreFloor()
    {
        var yjyg = new List<YjygRow>
        {
            // 只命中 1 个 price 词 = 1.5 分；扣 2 分应落到 0 而非 −0.5
            new("000004", "测试戊", "预增", 60, "", "产品涨价", "20260331", new DateTime(2026, 1, 1)),
        };
        var noWindow = new FreshnessConfig
        {
            Enabled = true,
            NoticeWindowDays = 0,        // 0 = 不启用取数窗口
            LookbackDays = 0,
            Tiers = FreshnessConfig.Defaults().Tiers,
        };
        var kw = EarningsScanTask.ScanKeywordHitsForTest(
            yjyg, new Dictionary<string, List<OrgSurveyRow>>(), Today, noWindow);
        var hit = kw.GetValueOrDefault("000004");
        var pass = hit is not null && hit.Score == 1.5 && hit.FreshnessPenalty == -2.0
            && hit.FreshScore == 0.0;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F3b: 时效分下限 0"
            + $"（窗口关闭，原分 {hit?.Score} − 2.0 → {hit?.FreshScore}）");
        return pass;
    }

    /// <summary>
    /// 用例 9：取数窗口（半年内）——公告日超过 lookback_days 的来源**整条不进扫描**，
    /// 窗口内的照常参与；无日期的来源保留（不误杀旧缓存/缺字段）。
    /// </summary>
    private static bool CaseLookbackWindow()
    {
        var yjyg = new List<YjygRow>
        {
            new("000010", "窗口外", "预增", 60, "", "产品涨价", "20260331", new DateTime(2026, 1, 1)),  // 261 天
            new("000011", "窗口内", "预增", 60, "", "产品涨价", "20260630", new DateTime(2026, 7, 1)),  // 79 天
            new("000012", "无日期", "预增", 60, "", "产品涨价", "20260630", null),                      // 保留
        };
        var kw = EarningsScanTask.ScanKeywordHitsForTest(
            yjyg, new Dictionary<string, List<OrgSurveyRow>>(), Today, FreshnessConfig.Defaults());

        var pass = !kw.ContainsKey("000010")        // 超半年 → 整条剔除
            && kw.ContainsKey("000011")             // 窗口内 → 保留
            && kw["000011"].AgeDays == 79
            && kw.ContainsKey("000012")             // 无日期 → 保留（宁放行不误杀）
            && kw["000012"].AgeDays is null;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F9: 取数窗口 180 天"
            + $"（261天剔除={!kw.ContainsKey("000010")}、79天保留={kw.ContainsKey("000011")}、"
            + $"无日期保留={kw.ContainsKey("000012")}）");
        return pass;
    }

    /// <summary>用例 4：无日期 → 未知，且不扣分（旧缓存/接口缺日期时的降级路径）。</summary>
    private static bool CaseNoDateDegrades()
    {
        var yjyg = new List<YjygRow>
        {
            new("000002", "测试丙", "预增", 60, "", "产品涨价", "20260331", null), // 无公告日
        };
        var kw = EarningsScanTask.ScanKeywordHitsForTest(
            yjyg, new Dictionary<string, List<OrgSurveyRow>>(), Today, FreshnessConfig.Defaults());
        var hit = kw.GetValueOrDefault("000002");
        var pass = hit is not null
            && hit.AgeDays is null
            && hit.FreshnessPenalty == 0.0            // 未知不惩罚
            && hit.LatestHitDate is null;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F4: 无日期降级（AgeDays=null 扣分 0，不误判为陈旧）");
        return pass;
    }

    /// <summary>用例 5：只有活动记录命中（预告文本无关键词）时也能成立——R2 的核心效果。</summary>
    private static bool CaseSurveyOnlyHit()
    {
        var yjyg = new List<YjygRow>
        {
            // 预告原因里**没有**任何关键词
            new("000003", "测试丁", "预增", 60, "", "本期政府补助增加", "20260331", new DateTime(2026, 8, 1)),
        };
        var org = new Dictionary<string, List<OrgSurveyRow>>
        {
            ["000003"] = new()
            {
                new OrgSurveyRow("000003", "测试丁", new DateTime(2026, 9, 10), new DateTime(2026, 9, 9),
                    "特定对象调研", "基金", "公司产能利用率维持高位，订单饱满，部分产品已提价。", "u2"),
            },
        };
        var kw = EarningsScanTask.ScanKeywordHitsForTest(yjyg, org, Today, FreshnessConfig.Defaults());
        var hit = kw.GetValueOrDefault("000003");
        var pass = hit is not null
            && hit.Score > 0
            && hit.Hits.ContainsKey("demand")
            && hit.Sources.Any(s => s.Kind == "特定对象调研")
            && hit.AgeDays == 9;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F5: 仅活动记录命中（Score={hit?.Score}，"
            + $"来源={string.Join("/", hit?.Sources.Select(s => s.Kind) ?? Array.Empty<string>())}）");
        return pass;
    }

    /// <summary>用例 7：逐来源归属 + 去重（见方法体注释）。</summary>
    private static bool CasePerSourceAttribution()
    {
        var yjyg = new List<YjygRow>
        {
            // 预告只含 1 个 price 词「涨价」= 1.5 分
            new("000005", "测试己", "预增", 60, "", "产品涨价", "20260331", new DateTime(2026, 5, 1)),
        };
        var org = new Dictionary<string, List<OrgSurveyRow>>
        {
            // 活动记录含「涨价」(与预告重复) + 「订单饱满」(demand，150 天前的旧活动)
            ["000005"] = new()
            {
                new OrgSurveyRow("000005", "测试己", new DateTime(2026, 4, 21), new DateTime(2026, 4, 20),
                    "业绩说明会", "机构", "产品涨价，订单饱满。", "u3"),
            },
        };
        var kw = EarningsScanTask.ScanKeywordHitsForTest(yjyg, org, Today, FreshnessConfig.Defaults());
        var hit = kw.GetValueOrDefault("000005");

        // 合并文本 = "产品涨价" + 活动正文：涨价 ×1（1.5）+ 订单饱满 ×1（1.2）= 2.7
        // 若按"逐来源相加"的错口径会算成 1.5 + (1.5+1.2) = 4.2
        // 注意：List<string> 的 == 是引用比较，必须用 SequenceEqual
        var pass = hit is not null
            && hit.Score == 2.7
            && hit.Hits["price"].SequenceEqual(new[] { "涨价" })      // 去重后只 1 个
            && hit.Hits["demand"].SequenceEqual(new[] { "订单饱满" }) // 来自活动记录
            && hit.AgeDays == 140                                     // 预告 5/1 更新（活动 4/20 更旧）
            && hit.FreshnessPenalty == -1.0;                          // 140 天 → 陈旧
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F7: 逐来源归属 + 去重"
            + $"（Score={hit?.Score} 应为 2.7 而非 4.2；price={string.Join("/", hit?.Hits.GetValueOrDefault("price") ?? new())}"
            + $" demand={string.Join("/", hit?.Hits.GetValueOrDefault("demand") ?? new())}"
            + $" days={hit?.AgeDays} penalty={hit?.FreshnessPenalty}）");
        return pass;
    }

    /// <summary>
    /// 用例 8：活动记录按 (Code, Url) 去重 —— 实测接口把**一场活动拆成多行**（每个接待对象一行，
    /// 同一 URL 共享同一份 CONTENT）。若不去重，同一份正文会被重复计入关键词命中。
    /// </summary>
    private static bool CaseSurveyGrouping()
    {
        var rows = new List<OrgSurveyRow>
        {
            // 同一场（同 URL）3 行：不同接待对象，Way 一个带逗号
            new("688719", "爱科赛博", new DateTime(2026, 9, 18), new DateTime(2026, 9, 17),
                "特定对象调研", "中信证券", "公司订单饱满。", "url-A"),
            new("688719", "爱科赛博", new DateTime(2026, 9, 18), new DateTime(2026, 9, 17),
                "特定对象调研,业绩说明会", "华夏基金", "公司订单饱满。", "url-A"),
            new("688719", "爱科赛博", new DateTime(2026, 9, 18), new DateTime(2026, 9, 17),
                "业绩说明会", "个人投资者", "公司订单饱满。", "url-A"),
            // 另一场（不同 URL）
            new("688719", "爱科赛博", new DateTime(2026, 8, 1), new DateTime(2026, 7, 31),
                "网络互动", "投资者", "公司产能利用率高位。", "url-B"),
            // 另一只票
            new("600531", "豫光金铅", new DateTime(2026, 9, 18), new DateTime(2026, 9, 18),
                "网络互动", "投资者", "产品价格上涨。", "url-C"),
        };
        var grouped = EastMoneyClient.GroupOrgSurveys(rows);
        var a = grouped.FirstOrDefault(r => r.Url == "url-A");
        var pass = grouped.Count == 3                       // 5 行 → 3 场
            && a is not null
            && a.Way == "业绩说明会、特定对象调研"            // 拆逗号后去重并按序合并
            && a.Object.Contains("中信证券") && a.Object.Contains("华夏基金")
            && a.Content == "公司订单饱满。"                 // 正文只留一份
            && a.ReceiveDate == new DateTime(2026, 9, 17)
            && grouped[0].ReceiveDate >= grouped[^1].ReceiveDate; // 按日期降序
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F8: 活动记录去重（5 行 → {grouped.Count} 场；"
            + $"Way=\"{a?.Way}\"）");
        return pass;
    }

    /// <summary>用例 6：yaml 分档解析（含列表形态 + 缺项回退 Defaults）。</summary>
    private static bool CaseYamlLoad()
    {
        var yaml = """
            bucket_C:
              text_signal:
                min_weighted_score: 1.5
                freshness:
                  enabled: true
                  activity_lookback_days: 120
                  tiers:
                    - {max_days: 30,   penalty: 0.0}
                    - {max_days: 60,   penalty: -0.25}
                    - {max_days: 120,  penalty: -1.5}
                    - {max_days: 9999, penalty: -3.0}
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "tb_fresh_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var file = Path.Combine(tmp, "02_strategy_config.yaml");
        try
        {
            File.WriteAllText(file, yaml);
            var th = PoolThresholds.Load(file);
            var f = th.Freshness;

            // 缺文件 → Defaults
            var fallback = PoolThresholds.Load(Path.Combine(tmp, "nope.yaml")).Freshness;

            var pass = f.Enabled
                && f.LookbackDays == 120
                && f.Tiers.Count == 4
                && f.Tiers[0].MaxDays == 30 && f.Tiers[0].Penalty == 0.0
                && f.Tiers[3].Penalty == -3.0
                && FreshnessResult.Evaluate(30, f.Tiers).Penalty == 0.0
                && FreshnessResult.Evaluate(31, f.Tiers).Penalty == -0.25
                && FreshnessResult.Evaluate(121, f.Tiers).Penalty == -3.0
                // 缺文件回退默认档
                && fallback.Tiers.Count == 4 && fallback.Tiers[3].Penalty == -2.0
                && fallback.LookbackDays == 180;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} F6: yaml 分档解析"
                + $"（读入 {f.Tiers.Count} 档 / 窗口 {f.LookbackDays} 天；缺文件回退默认 {fallback.Tiers.Count} 档）");
            return pass;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* 清理失败不影响结论 */ }
        }
    }
}
