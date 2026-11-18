using System.Text;
using System.Text.RegularExpressions;

namespace ThreeBucket.Core.Services;

/// <summary>知识库条目中的一个小节，如「核心观点」「注意事项」。</summary>
public sealed class KnowledgeSection
{
    public string Heading { get; init; } = "";
    public string Body { get; init; } = "";
}

/// <summary>单条投资经验（如 MACRO-001）。</summary>
public sealed class KnowledgeEntry
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string CategoryCode { get; init; } = "";
    public string CategoryTitle { get; init; } = "";
    public int CategoryOrder { get; init; }
    public List<string> Tags { get; init; } = new();
    public List<KnowledgeSection> Sections { get; init; } = new();
    /// <summary>原文片段（从 `## 条目` 到下一个分隔符），用于详情展示与整段喂 LLM。</summary>
    public string RawMarkdown { get; init; } = "";

    public string Display => $"{Id} · {Title}";
    public string TagsText => string.Join(" ", Tags.Select(t => "#" + t));

    /// <summary>小节正文拼接（检索用，不含标题行）。</summary>
    public string BodyText => string.Join("\n", Sections.Select(s => s.Body));
}

/// <summary>知识库类别（如 MACRO 宏观经济与政策）。AI 规则类可能只有正文没有条目。</summary>
public sealed class KnowledgeCategory
{
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public int Order { get; init; }
    /// <summary>类别下的自由正文（无条目的类别，如「04 LLM 使用规则」的编号清单）。</summary>
    public string Body { get; init; } = "";
    public List<KnowledgeEntry> Entries { get; init; } = new();

    public string Display => string.IsNullOrEmpty(Code) ? Title : $"{Order:D2} {Title}（{Code}）";
}

/// <summary>
/// 投资经验知识库（trading-system/09_投资经验知识库.md）的解析与检索。
///
/// 定位：**辅助参考**，不是交易指令。解析结果供
/// ① 「投资经验」页浏览/检索；
/// ② 后续 LLM 分析按类别检索相关规则（<see cref="Retrieve"/> + <see cref="BuildLlmContext"/>），
///    对应知识库第 04 节「只检索相关类别，不需要阅读全部知识库」的要求。
///
/// 数据源优先级：仓库内 trading-system/09_投资经验知识库.md（可人工编辑，改完即时生效）
/// → 程序集内嵌副本（单文件发布/移动端无项目结构时的兜底）。
/// </summary>
public sealed class InvestmentKnowledgeBase
{
    /// <summary>仓库内相对路径（项目根下）。</summary>
    /// <summary>仓库内相对路径（项目根下，正斜杠形式便于跨平台拼接）。</summary>
    public const string RelativePath = "trading-system/09_投资经验知识库.md";

    /// <summary>内嵌资源名（ThreeBucket.Core.csproj 把上面的 md 链接为资源，单一真源）。</summary>
    private const string EmbeddedResource = "ThreeBucket.Core.Resources.InvestmentKnowledgeBase.md";

    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    /// <summary>知识库定位/使用原则等文首说明（原文）。</summary>
    public string Preamble { get; private set; } = "";
    public string RawMarkdown { get; private set; } = "";
    /// <summary>实际加载来源：文件绝对路径，或 "embedded"。</summary>
    public string SourcePath { get; private set; } = "";
    public bool IsLoaded { get; private set; }
    public List<KnowledgeCategory> Categories { get; private set; } = new();

    /// <summary>全部条目（按类别顺序展开）。</summary>
    public IEnumerable<KnowledgeEntry> Entries => Categories.SelectMany(c => c.Entries);

    /// <summary>「04 LLM 使用规则」类别（无条目时为空）。</summary>
    public KnowledgeCategory? AiRulesCategory =>
        Categories.FirstOrDefault(c => c.Code == "AI" || c.Title.Contains("LLM", StringComparison.OrdinalIgnoreCase));

    private InvestmentKnowledgeBase() { }

    /// <summary>空实例：知识库缺失时的降级对象（IsLoaded=false，各方法返回空结果）。</summary>
    public static InvestmentKnowledgeBase Empty { get; } = new();

    // ── 加载 ───────────────────────────────────────────────────────

    /// <summary>从仓库根加载：优先磁盘文件，回退内嵌副本。</summary>
    public static InvestmentKnowledgeBase LoadDefault(string? projectRoot)
    {
        var path = LocateOnDisk(projectRoot);
        if (path is not null)
        {
            try { return FromMarkdown(ReadTextTolerant(path), path); }
            catch { /* 文件损坏：继续尝试内嵌副本 */ }
        }
        return LoadEmbedded();
    }

    /// <summary>从指定文件加载（不存在或异常返回 <see cref="Empty"/>）。</summary>
    public static InvestmentKnowledgeBase LoadFromFile(string path)
    {
        try
        {
            return File.Exists(path) ? FromMarkdown(ReadTextTolerant(path), path) : Empty;
        }
        catch { return Empty; }
    }

    /// <summary>
    /// 读取知识库文本，容忍非 UTF-8 保存：本文件是**人工编辑**的，Windows 记事本/部分编辑器
    /// 可能存成 GBK（ANSI）或带 BOM。先按 UTF-8 严格解码，非法字节序列则回退 GB18030
    /// （GBK 超集），避免整库中文变乱码后 UI 与 LLM 上下文一起报废。
    /// </summary>
    internal static string ReadTextTolerant(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // 去掉 UTF-8 BOM（ReadAllText 会吞掉，但手工解码需要自己处理）
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);

        try
        {
            // throwOnInvalidBytes: 合法 UTF-8 正常通过，GBK 文本在此抛异常走下面的回退
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch { return new UTF8Encoding(false).GetString(bytes); }
        }
    }

    private static InvestmentKnowledgeBase LoadEmbedded()
    {
        try
        {
            using var s = typeof(InvestmentKnowledgeBase).Assembly
                .GetManifestResourceStream(EmbeddedResource);
            if (s is null) return Empty;
            using var reader = new StreamReader(s, new UTF8Encoding(false));
            return FromMarkdown(reader.ReadToEnd(), "embedded");
        }
        catch { return Empty; }
    }

    /// <summary>逐级向上查找 trading-system/09_投资经验知识库.md；找不到返回 null。</summary>
    private static string? LocateOnDisk(string? projectRoot)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectRoot)) roots.Add(projectRoot);
        roots.Add(AppContext.BaseDirectory);
        roots.Add(Directory.GetCurrentDirectory());

        foreach (var start in roots)
        {
            var dir = start;
            for (var i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "trading-system", "09_投资经验知识库.md");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
                if (parent is null || parent == dir) break;
                dir = parent;
            }
        }
        return null;
    }

    // ── 解析 ───────────────────────────────────────────────────────

    // 类别标题：`# 01 宏观经济与政策（MACRO）`、`# 04 LLM 使用规则（AI INSTRUCTIONS）`
    // —— 括号内可能含空格，取首个词作为类别代码
    private static readonly Regex CategoryRe = new(
        @"^#\s+(?<num>\d+)\s+(?<title>.+?)\s*[（(](?<code>[A-Za-z][A-Za-z ]*)[)）]\s*$",
        RegexOptions.Compiled);

    private static readonly Regex EntryRe = new(
        @"^##\s+(?<id>[A-Za-z]+-\d+)\s*[：:]\s*(?<title>.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary>带冒号的小节标题：`**核心观点：**`（冒号可在 ** 内，也可在正文开头）。</summary>
    private static readonly Regex SectionRe = new(
        @"^\*\*(?<name>[^*]{1,40}?)[：:]\s*\*\*\s*(?<rest>.*)$",
        RegexOptions.Compiled);

    /// <summary>纯加粗独占一行的小节标题：`**个人投资案例：招商轮船与中远海控**`。</summary>
    private static readonly Regex BoldHeadingRe = new(
        @"^\*\*(?<name>[^*\r\n]{1,60})\*\*\s*(?<rest>)$",
        RegexOptions.Compiled);

    private static readonly Regex TagsRe = new(
        @"^标签\s*[：:]\s*(?<tags>.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析知识库 markdown。层级约定：
    /// <c># NN 类别标题（CODE）</c> = 类别；<c>## CODE-001：条目标题</c> = 条目；
    /// <c>**小节名：**</c> = 小节；<c>标签：#a #b</c> = 标签；<c>---</c> = 条目分隔。
    /// 无编号的 `##`（如文档副标题）与首个类别前的内容归入 Preamble。
    /// </summary>
    public static InvestmentKnowledgeBase FromMarkdown(string markdown, string sourcePath = "")
    {
        var kb = new InvestmentKnowledgeBase
        {
            RawMarkdown = markdown,
            SourcePath = sourcePath,
        };
        if (string.IsNullOrWhiteSpace(markdown)) return Empty;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var preamble = new StringBuilder();
        var cats = new List<KnowledgeCategory>();

        // 当前类别 / 当前条目 / 当前小节 的累积状态
        CategoryDraft? cat = null;
        EntryDraft? entry = null;

        void CloseEntry()
        {
            if (entry is null || cat is null) { entry = null; return; }
            entry.Flush();                       // 落盘最后一个小节（否则末节正文丢失）
            cat.Entries.Add(new KnowledgeEntry
            {
                Id = entry.Id,
                Title = entry.Title,
                CategoryCode = cat.Code,
                CategoryTitle = cat.Title,
                CategoryOrder = cat.Order,
                // 标签按首次出现顺序去重（LINQ Distinct 保序）
                Tags = entry.Tags.Distinct().ToList(),
                Sections = entry.Sections
                    .Select(s => new KnowledgeSection { Heading = s.Heading, Body = s.Body.ToString().Trim() })
                    .ToList(),
                RawMarkdown = entry.Raw.ToString().Trim(),
            });
            entry = null;
        }

        void CloseCategory()
        {
            CloseEntry();
            if (cat is null) return;
            cats.Add(new KnowledgeCategory
            {
                Code = cat.Code,
                Title = cat.Title,
                Order = cat.Order,
                Body = cat.Body.ToString().Trim(),
                Entries = cat.Entries,
            });
            cat = null;
        }

        void AppendText(string line)
        {
            if (entry is not null)
            {
                entry.Append(line);
                entry.Raw.AppendLine(line);   // 原文片段要含正文（详情页/整条喂 LLM 用）
            }
            else if (cat is not null) cat.Body.AppendLine(line);
            else preamble.AppendLine(line);
        }

        foreach (var raw in lines)
        {
            var line = raw;
            var trimmed = line.Trim();

            // 类别标题（`# 01 宏观经济与政策（MACRO）`）
            var cm = CategoryRe.Match(trimmed);
            if (trimmed.StartsWith("# ") && !trimmed.StartsWith("## ") && cm.Success)
            {
                CloseCategory();
                cat = new CategoryDraft
                {
                    Order = int.TryParse(cm.Groups["num"].Value, out var n) ? n : 0,
                    Title = cm.Groups["title"].Value.Trim(),
                    // 括号内可能写成「AI INSTRUCTIONS」——取首个词作类别代码，便于检索
                    Code = cm.Groups["code"].Value.Trim().Split(' ')[0].ToUpperInvariant(),
                };
                continue;   // 标题行不进正文（Body 只留类别说明，供 LLM 直接引用）
            }

            // 文档主标题
            if (trimmed.StartsWith("# ") && !trimmed.StartsWith("## ") && kb.Title.Length == 0)
            {
                kb.Title = trimmed[2..].Trim();
                continue;
            }

            // 条目标题（`## MACRO-001：IPO发行与牛市流动性`）
            var em = EntryRe.Match(trimmed);
            if (trimmed.StartsWith("## ") && em.Success)
            {
                CloseEntry();
                if (cat is null)
                {
                    // 条目出现在任何类别之前：归入一个占位类别，避免内容丢失
                    cat = new CategoryDraft { Order = 0, Title = "未分类", Code = "MISC" };
                }
                entry = new EntryDraft
                {
                    Id = em.Groups["id"].Value.Trim().ToUpperInvariant(),
                    Title = em.Groups["title"].Value.Trim(),
                };
                entry.Raw.AppendLine(line);   // 标题行进原文；正文等到首个小节才有内容
                continue;
            }

            // 副标题（`## A股投资经验与决策规则`，无条目编号）
            if (trimmed.StartsWith("## ") && kb.Subtitle.Length == 0 && cat is null && entry is null)
            {
                kb.Subtitle = trimmed[3..].Trim();
                continue;
            }

            // 条目分隔
            if (trimmed == "---")
            {
                CloseEntry();               // 类别正文保留（`---` 本身不进正文）
                continue;
            }

            // 标签
            var tm = TagsRe.Match(trimmed);
            if (tm.Success && entry is not null)
            {
                foreach (var t in tm.Groups["tags"].Value
                             .Split(new[] { ' ', '\t', '，', ',', '、' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var tag = t.TrimStart('#', '＃').Trim();
                    if (tag.Length > 0) entry.Tags.Add(tag);
                }
                entry.Raw.AppendLine(line);
                continue;
            }

            // 小节标题（`**核心观点：**`）
            var sm = SectionRe.Match(trimmed);
            if (sm.Success)
            {
                var name = sm.Groups["name"].Value.Trim();
                var rest = sm.Groups["rest"].Value.Trim();
                if (entry is not null)
                {
                    entry.StartSection(name, rest);
                    entry.Raw.AppendLine(line);
                    continue;
                }
                // 类别/文首的加粗小标题：当普通正文保留
                AppendText(line);
                continue;
            }

            // 整行加粗的带内联标题（`**个人投资案例：招商轮船与中远海控**`）
            var bm = BoldHeadingRe.Match(trimmed);
            if (bm.Success && entry is not null)
            {
                var name = bm.Groups["name"].Value.Trim();
                var colon = name.IndexOfAny(new[] { '：', ':' });
                var heading = colon > 0 && colon <= 20 ? name[..colon] : name;
                var rest = colon > 0 && colon <= 20 ? name[(colon + 1)..].Trim() : "";
                entry.StartSection(heading, rest);
                entry.Raw.AppendLine(line);
                continue;
            }

            AppendText(line);
        }

        CloseCategory();

        kb.Preamble = preamble.ToString().Trim();
        kb.Categories = cats;
        kb.IsLoaded = true;
        return kb;
    }

    private sealed class CategoryDraft
    {
        public int Order;
        public string Title = "";
        public string Code = "";
        public readonly StringBuilder Body = new();
        public readonly List<KnowledgeEntry> Entries = new();
    }

    private sealed class EntryDraft
    {
        public string Id = "";
        public string Title = "";
        public readonly List<string> Tags = new();
        public readonly List<SectionDraft> Sections = new();
        public readonly StringBuilder Raw = new();
        private StringBuilder? _cur;
        private string _curHeading = "";

        public void Append(string line)
        {
            if (_cur is null) StartSection("正文", "");
            _cur!.AppendLine(line);
        }

        public void StartSection(string heading, string inlineRest)
        {
            Commit();
            _curHeading = heading;
            _cur = new StringBuilder();
            if (inlineRest.Length > 0) _cur.AppendLine(inlineRest);
        }

        private void Commit()
        {
            if (_cur is null) return;
            var body = _cur.ToString().Trim();
            if (body.Length > 0)
                Sections.Add(new SectionDraft { Heading = _curHeading, Body = _cur });
            _cur = null;
        }

        /// <summary>收尾：把最后一个小节落盘（由 CloseEntry 调用前触发）。</summary>
        public void Flush() => Commit();
    }

    private sealed class SectionDraft
    {
        public string Heading = "";
        public StringBuilder Body = new();
    }

    // ── 检索 ───────────────────────────────────────────────────────

    /// <summary>
    /// 按自由文本检索条目（标题/标签/正文加权打分），返回得分最高的前 max 条。
    /// 对应知识库使用原则：只检索相关规则，不整库塞给 LLM。
    /// category 非空时限定在该类别内（"MACRO" / "MARKET" / "INDUSTRY"，大小写不敏感）。
    /// </summary>
    public IReadOnlyList<KnowledgeEntry> Retrieve(string query, int max = 5, string? category = null)
    {
        if (!IsLoaded) return Array.Empty<KnowledgeEntry>();
        var pool = Entries.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            var code = category.Trim().ToUpperInvariant();
            pool = pool.Where(e => e.CategoryCode == code);
        }

        var terms = Tokenize(query);
        if (terms.Count == 0)
            return pool.Take(max).ToList();

        var scored = pool
            .Select(e => (Entry: e, Score: Score(e, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            // 同分按类别顺序 + 编号，保证输出稳定（回归测试可复现）
            .ThenBy(x => x.Entry.CategoryOrder)
            .ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.Entry)
            .ToList();
        return scored;
    }

    /// <summary>取某类别全部条目（大小写不敏感）；类别不存在返回空。</summary>
    public IReadOnlyList<KnowledgeEntry> ByCategory(string code)
    {
        var c = code.Trim().ToUpperInvariant();
        return Entries.Where(e => e.CategoryCode == c).ToList();
    }

    /// <summary>按编号取单条（如 "MARKET-001"）。</summary>
    public KnowledgeEntry? ById(string id)
        => Entries.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static double Score(KnowledgeEntry e, List<string> terms)
    {
        double score = 0;
        foreach (var t in terms)
        {
            // 标签权重最高（人工标注的主题词），其次标题，最后正文。
            // 命中长度加权：2 字滑窗噪声大，3 字以上更可信。
            var w = t.Length >= 3 ? 1.0 : 0.5;
            if (e.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase))) score += 3 * w;
            if (e.Title.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 2 * w;
            if (e.BodyText.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 1 * w;
        }
        return score;
    }

    /// <summary>
    /// 检索词切分：整串 + 空白/标点分隔词 + 中文 2~3 字滑窗（无分词器时的近似方案）。
    /// 含数字一起切（「11月」「2月」这类月份词才能命中月份效应条目）。
    /// </summary>
    public static List<string> Tokenize(string query)
    {
        var terms = new List<string>();
        var q = (query ?? "").Trim();
        if (q.Length == 0) return terms;
        terms.Add(q);

        foreach (var part in q.Split(new[] { ' ', '\t', '，', ',', '、', '。', '？', '?', '！', '!', '；', ';', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 2) terms.Add(part);
        }

        foreach (Match m in Regex.Matches(q, @"[\u4e00-\u9fff0-9]{2,}"))
        {
            var s = m.Value;
            for (var i = 0; i < s.Length; i++)
            {
                if (i + 2 <= s.Length) terms.Add(s.Substring(i, 2));
                if (i + 3 <= s.Length) terms.Add(s.Substring(i, 3));
            }
        }
        return terms.Distinct().ToList();
    }

    // ── 给 LLM 的上下文 ────────────────────────────────────────────

    /// <summary>
    /// 把选中条目（默认自动检索）拼成一段可直接追加到 prompt 的 markdown 上下文，
    /// 含使用原则与「AI 使用规则」，并按 maxChars 截断（超长时只保留前若干条完整内容）。
    ///
    /// 调用方约定（写进提示词）：个人经验必须与最新数据交叉验证，冲突时以核实事实为准，
    /// 未验证经验标注为待验证假设。
    /// </summary>
    public string BuildLlmContext(string? query = null, string? category = null,
        int max = 5, int maxChars = 6000)
    {
        if (!IsLoaded) return "";

        var picked = string.IsNullOrWhiteSpace(query)
            ? (string.IsNullOrWhiteSpace(category) ? Entries.Take(max).ToList() : ByCategory(category).Take(max).ToList())
            : Retrieve(query, max, category);

        var sb = new StringBuilder();
        sb.AppendLine("## 个人投资经验知识库（辅助决策参考，非交易指令）");
        sb.AppendLine();
        sb.AppendLine("> 下列内容为个人经验与待验证假设：必须结合最新可核实数据交叉验证；");
        sb.AppendLine("> 若与当前市场数据矛盾，以核实过的事实为准并明确指出矛盾；不得机械套用。");

        var principles = ExtractPrinciples();
        if (principles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【使用原则】");
            foreach (var p in principles) sb.AppendLine(p);
        }

        if (picked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【相关规则】");
            foreach (var e in picked)
            {
                var block = RenderEntry(e);
                if (sb.Length + block.Length > maxChars)
                {
                    sb.AppendLine($"> （已达上下文上限 {maxChars} 字，其余规则未展开）");
                    break;
                }
                sb.AppendLine(block);
            }
        }

        var rules = AiRulesCategory;
        if (rules is not null && sb.Length + rules.Body.Length <= maxChars + 1500)
        {
            sb.AppendLine();
            sb.AppendLine("【AI 使用规则】");
            sb.AppendLine(rules.Body.Trim());
        }
        return sb.ToString().Trim();
    }

    /// <summary>单条规则的 markdown 渲染（小节标题 + 正文 + 标签）。</summary>
    public static string RenderEntry(KnowledgeEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {e.Id} · {e.Title}（{e.CategoryCode}）");
        foreach (var s in e.Sections)
        {
            if (s.Body.Length == 0) continue;
            sb.AppendLine($"- **{s.Heading}**：{s.Body.Replace("\n", "\n  ")}");
        }
        if (e.Tags.Count > 0) sb.AppendLine($"- 标签：{e.TagsText}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>从文首「使用原则」抽取编号条目（供 LLM 上下文复用）。</summary>
    public List<string> ExtractPrinciples()
    {
        var list = new List<string>();
        var inBlock = false;
        foreach (var raw in Preamble.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Contains("使用原则", StringComparison.Ordinal)) { inBlock = true; continue; }
            if (!inBlock) continue;
            if (t.Length == 0) continue;
            if (!(char.IsDigit(t[0]) || t[0] == '(' || t[0] == '（')) break;
            list.Add(t);
        }
        return list;
    }
}
