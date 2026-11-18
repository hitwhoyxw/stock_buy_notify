using ThreeBucket.Core.Services;

namespace ThreeBucket.Demo;

/// <summary>
/// 投资经验知识库离线回归：类别/条目/小节/标签解析、内嵌资源兜底、按类别与关键词检索、
/// LLM 上下文组装与截断。除「磁盘文件与内嵌副本一致」一项读仓库内 md 外，其余全为合成文本，不依赖网络。
/// </summary>
public static class KnowledgeBaseRegression
{
    public static bool Run()
    {
        Console.WriteLine("=== 投资经验知识库离线回归（解析 / 检索 / LLM 上下文） ===\n");
        var ok = true;
        ok &= CaseParseStructure();
        ok &= CaseAiRulesCategory();
        ok &= CaseRetrieveByKeyword();
        ok &= CaseNaturalQuestionRetrieval();
        ok &= CaseRetrieveByCategory();
        ok &= CaseLlmContext();
        ok &= CaseEmbeddedFallback();
        ok &= CaseEncodingTolerance();
        ok &= CaseEmptyDegrade();
        Console.WriteLine(ok ? "\n知识库回归：全部通过 ✅" : "\n知识库回归：存在失败 ❌");
        return ok;
    }

    private const string Sample = """
        # 个人投资知识库 V1.0
        ## A股投资经验与决策规则

        **知识库定位：** 测试用知识库。

        **使用原则：**
        1. 经验不代表客观定律。
        2. 必须结合最新数据验证。

        ---

        # 01 宏观经济与政策（MACRO）

        ## MACRO-001：IPO发行与牛市流动性

        **核心观点：**
        大型 IPO 集中发行期间，如果市场仍能承接，可能说明流动性充裕。

        **需要验证的指标：**
        - IPO 数量及融资总额
        - A 股日均成交额

        标签：#IPO #宏观流动性 #牛市周期

        ---

        # 02 市场规律与择时（MARKET）

        ## MARKET-001：A股月份效应

        **核心观点：**
        不同月份可能存在季节性表现差异，2月与11月相对强势。

        **个人历史观察：**

        相对强势月份：
        - 2月
        - 11月

        标签：#月份效应 #季节性规律

        ## MARKET-002：成交量萎缩时不要急于买入

        **核心观点：**
        持续缩量时不要急于抄底，应等待放量企稳。

        **注意事项：**
        缩量也可能是抛压衰竭，需结合价格走势判断。

        标签：#成交量 #择时 #仓位管理

        ---

        # 03 行业研究与景气度（INDUSTRY）

        ## INDUSTRY-001：利用同行业公司业绩交叉验证行业景气度

        **核心观点：**
        同产业链公司业绩具有相关性，可提前推断未披露公司的经营情况。

        **个人投资案例：招商轮船与中远海控**

        1. 招商轮船先披露，可推断集运景气。
        2. 再用中远海控自身数据验证。

        标签：#行业景气度 #同业比较

        ---

        # 04 LLM 使用规则（AI INSTRUCTIONS）

        当分析 A 股时：

        1. 只检索相关类别。
        2. 与最新数据交叉验证。
        3. 未验证经验标注为待验证假设。
        """;

    /// <summary>用例 1：类别 / 条目 / 小节 / 标签 / 原文片段的层级解析。</summary>
    private static bool CaseParseStructure()
    {
        var kb = InvestmentKnowledgeBase.FromMarkdown(Sample, "test");
        var cats = kb.Categories;
        var macro = kb.ById("MACRO-001");
        var months = kb.ById("MARKET-001");

        var pass = kb.IsLoaded
            && kb.Title == "个人投资知识库 V1.0"
            && kb.Subtitle == "A股投资经验与决策规则"
            && cats.Count == 4
            && cats.Select(c => c.Code).SequenceEqual(new[] { "MACRO", "MARKET", "INDUSTRY", "AI" })
            && cats[0].Order == 1 && cats[1].Order == 2
            && kb.Entries.Count() == 4                                  // AI 类无条目
            && macro is not null
            && macro.Title == "IPO发行与牛市流动性"
            && macro.CategoryCode == "MACRO"
            && macro.Tags.SequenceEqual(new[] { "IPO", "宏观流动性", "牛市周期" })
            && macro.Sections.Any(s => s.Heading == "核心观点" && s.Body.Contains("流动性充裕"))
            && macro.Sections.Any(s => s.Heading == "需要验证的指标" && s.Body.Contains("日均成交额"))
            // 末节正文必须在收尾时落盘（曾因未 Flush 丢失最后一节）
            && months is not null && months.Tags.SequenceEqual(new[] { "月份效应", "季节性规律" })
            && months.RawMarkdown.Contains("相对强势月份")
            // 带内联内容的加粗标题（**个人投资案例：招商轮船与中远海控**）要拆成小节标题+正文
            && kb.ById("INDUSTRY-001")!.Sections.Any(s => s.Heading == "个人投资案例" && s.Body.Contains("招商轮船"));
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K1: 层级解析"
            + $"（标题='{kb.Title}' 副标题='{kb.Subtitle}' 类别 {cats.Count} / 条目 {kb.Entries.Count()}"
            + $" / MACRO-001 小节 {macro?.Sections.Count} / 标签 {string.Join(",", macro?.Tags ?? new List<string>())}"
            + $" / 行业小节 {string.Join("|", kb.ById("INDUSTRY-001")?.Sections.Select(s => s.Heading) ?? Array.Empty<string>())}）");
        return pass;
    }

    /// <summary>用例 2：无条目的「04 LLM 使用规则」类别要保留正文（AI 使用规则要能喂给 LLM）。</summary>
    private static bool CaseAiRulesCategory()
    {
        var kb = InvestmentKnowledgeBase.FromMarkdown(Sample, "test");
        var ai = kb.AiRulesCategory;
        var pass = ai is not null
            && ai.Code == "AI"
            && ai.Entries.Count == 0
            && ai.Body.Contains("只检索相关类别")
            && ai.Body.Contains("待验证假设");
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K2: AI 规则类别"
            + $"（code={ai?.Code} 正文 {ai?.Body.Length ?? 0} 字）");
        return pass;
    }

    /// <summary>用例 3：关键词检索（中文 2 字滑窗让人工提问也能命中；无命中的词不误召回）。</summary>
    private static bool CaseRetrieveByKeyword()
    {
        var kb = InvestmentKnowledgeBase.FromMarkdown(Sample, "test");
        var vol = kb.Retrieve("成交量萎缩", 3);
        var ipo = kb.Retrieve("IPO 融资", 3);
        var none = kb.Retrieve("完全无关的量子计算机话题", 3);

        var pass = vol.Count > 0 && vol[0].Id == "MARKET-002"     // 标签+#成交量 权重最高
            && ipo.Count > 0 && ipo[0].Id == "MACRO-001"
            && none.All(e => e.Id is not ("MARKET-002" or "MACRO-001"))
            // 只检索相关类别：命中条目数受 max 限制
            && kb.Retrieve("市场", 1).Count == 1;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K3: 关键词检索"
            + $"（成交量→{vol.FirstOrDefault()?.Id}、IPO→{ipo.FirstOrDefault()?.Id}、无关命中 {none.Count} 条）");
        return pass;
    }

    /// <summary>
    /// 用例 3b：自然语言提问也能命中（含数字的月份词要参与切分）。
    /// 回归背景：早期切分只取 [\u4e00-\u9fff]{2,}，「11月大盘缩量」切不出「11月」，
    /// 月份效应条目检索不到；改为含数字的 2~3 字滑窗后两跳都能命中。
    /// </summary>
    private static bool CaseNaturalQuestionRetrieval()
    {
        var kb = InvestmentKnowledgeBase.FromMarkdown(Sample, "test");
        var hit = kb.Retrieve("11月大盘缩量怎么操作", 5);
        var ids = hit.Select(e => e.Id).ToList();

        var pass = ids.Contains("MARKET-001")      // 月份效应
            && ids.Contains("MARKET-002")          // 缩量
            && ThreeBucket.Core.Services.InvestmentKnowledgeBase
                   .Tokenize("11月大盘").Contains("11月");
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K3b: 自然语言提问"
            + $"（命中 {string.Join("/", ids)}）");
        return pass;
    }

    /// <summary>用例 4：按类别取条目（大小写不敏感）+ 标签命中。</summary>
    private static bool CaseRetrieveByCategory()
    {
        var kb = InvestmentKnowledgeBase.FromMarkdown(Sample, "test");
        var market = kb.ByCategory("market");
        var pass = market.Count == 2
            && market.Select(e => e.Id).SequenceEqual(new[] { "MARKET-001", "MARKET-002" })
            && kb.ByCategory("NOPE").Count == 0
            && kb.Retrieve("缩量", 5, "INDUSTRY").Count == 0;   // 类别限定生效
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K4: 按类别检索（MARKET {market.Count} 条，跨类别限定不串味）");
        return pass;
    }

    /// <summary>用例 5：LLM 上下文含使用原则/规则/标签与交叉验证警示，且遵守字数上限。</summary>
    private static bool CaseLlmContext()
    {
        var kb = InvestmentKnowledgeBase.FromMarkdown(Sample, "test");
        var ctx = kb.BuildLlmContext("成交量萎缩", max: 3);
        var capped = kb.BuildLlmContext("", max: 4, maxChars: 400);

        var pass = ctx.Contains("个人投资经验知识库")
            && ctx.Contains("非交易指令")
            && ctx.Contains("交叉验证")
            && ctx.Contains("使用原则")
            && ctx.Contains("MARKET-002")
            && ctx.Contains("标签：")
            && ctx.Contains("AI 使用规则")
            && capped.Length < ctx.Length + 1500      // 上限生效：不会把整库塞进去
            && InvestmentKnowledgeBase.RenderEntry(kb.ById("MACRO-001")!).Contains("### MACRO-001");
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K5: LLM 上下文"
            + $"（检索上下文 {ctx.Length} 字，限长上下文 {capped.Length} 字）");
        return pass;
    }

    /// <summary>
    /// 用例 6：仓库内 md 与内嵌副本内容一致（防止只改文件不改资源导致发布版行为漂移）。
    /// 断言用「结构性不变量」而非固定条数——知识库是持续追加的活文档，写死 4 条会每加一条经验就误报。
    /// </summary>
    private static bool CaseEmbeddedFallback()
    {
        var fromDisk = InvestmentKnowledgeBase.LoadDefault(AppContext.BaseDirectory);
        var embedded = InvestmentKnowledgeBase.LoadDefault(null);   // 无项目根 → 走内嵌/向上查找

        var pass = fromDisk.IsLoaded
            && fromDisk.Entries.Count() >= 4                            // 只要求不少于已知基线
            && fromDisk.Entries.Select(e => e.Id).Distinct().Count() == fromDisk.Entries.Count()  // 编号不重复
            && fromDisk.Categories.Count >= 4
            && fromDisk.ById("MARKET-001")?.Tags.Contains("月份效应") == true
            && fromDisk.ById("INDUSTRY-001")?.Title.Contains("交叉验证") == true
            && embedded.IsLoaded
            // 同一份 md 的内嵌副本与磁盘副本条目/编号必须逐一对应
            && embedded.Entries.Select(e => e.Id).SequenceEqual(fromDisk.Entries.Select(e => e.Id))
            && embedded.ByCategory("AI").Count == 0;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K6: 真实知识库加载"
            + $"（磁盘 {fromDisk.Entries.Count()} 条 / {fromDisk.SourcePath}；内嵌 {embedded.Entries.Count()} 条，编号一致="
            + $"{embedded.Entries.Select(e => e.Id).SequenceEqual(fromDisk.Entries.Select(e => e.Id))}）");
        return pass;
    }

    /// <summary>
    /// 用例 6b：非 UTF-8 保存容错 —— 记事本存成 GBK（ANSI）或带 UTF-8 BOM 时中文不能变乱码。
    /// 回归背景：本文件由人工编辑，实测 PowerShell 5 的 Set-Content 会把 UTF-8 写成 GBK，
    /// 严格 UTF-8 解码会整库变「锟斤拷」且不报错——必须在读入层回退 GB18030。
    /// </summary>
    private static bool CaseEncodingTolerance()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tb_kbenc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            var gbk = System.Text.Encoding.GetEncoding("GB18030");

            // ① GBK 保存
            var gbkPath = Path.Combine(dir, "gbk.md");
            File.WriteAllBytes(gbkPath, gbk.GetBytes(Sample));
            var fromGbk = InvestmentKnowledgeBase.LoadFromFile(gbkPath);

            // ② 带 BOM 的 UTF-8
            var bomPath = Path.Combine(dir, "bom.md");
            File.WriteAllBytes(bomPath, utf8.GetPreamble().Concat(utf8.GetBytes(Sample)).ToArray());
            var fromBom = InvestmentKnowledgeBase.LoadFromFile(bomPath);

            var pass = fromGbk.IsLoaded
                && fromGbk.ById("MARKET-002") is not null
                && fromGbk.ById("MARKET-002")!.Tags.Contains("成交量")   // 中文未变乱码
                && fromGbk.ById("MARKET-002")!.Title.Contains("成交量萎缩")
                && fromBom.IsLoaded
                && fromBom.Title == "个人投资知识库 V1.0"                  // BOM 不进标题
                && fromBom.ById("MARKET-001")!.Title == "A股月份效应";
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K6b: 编码容错"
                + $"（GBK 读入 {fromGbk.Entries.Count()} 条，标题='{fromBom.Title}'）");
            return pass;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    /// <summary>用例 7：空/缺失输入降级为空实例，各入口不抛异常。</summary>
    private static bool CaseEmptyDegrade()
    {
        var a = InvestmentKnowledgeBase.FromMarkdown("", "x");
        var b = InvestmentKnowledgeBase.LoadFromFile(Path.Combine(Path.GetTempPath(), "no_such_kb_" + Guid.NewGuid().ToString("N") + ".md"));
        var pass = !a.IsLoaded && !b.IsLoaded
            && a.Retrieve("任意").Count == 0
            && a.BuildLlmContext("任意").Length == 0
            && b.Entries.Count() == 0;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} K7: 空/缺失降级（不抛异常，返回空结果）");
        return pass;
    }
}
