using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ThreeBucket.Core.Services;
using ThreeBucket.UI.Dialogs;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class LlmBridgeView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;

    private class SkillDef
    {
        public string Key = "";
        public string Label = "";
        public string Input = "";
        public string Output = "";
        public string Script = "";      // 生成输入对应的脚本（相对项目根）
        public string Args = "";
        public string Prompt = "";       // skills/ 下的判定指令文件，如 t6_a_dividend_analysis.md
    }

    private static readonly SkillDef[] Skills =
    {
        new() { Key = "T4C", Label = "T4 财报文本扫描", Input = "skill_input_T4C.md", Output = "skill_output_T4C.md", Script = "scripts/t4_ingest.py", Args = "--prepare", Prompt = "t4_c_text_scan.md" },
        new() { Key = "T5",  Label = "T5 季度归因",     Input = "skill_input_T5.md",  Output = "skill_output_T5.md",  Script = "scripts/t5_prepare.py",  Args = "", Prompt = "t5_attribution.md" },
        new() { Key = "T6A", Label = "T6 A桶·红利逆向", Input = "skill_input_T6_A.md", Output = "skill_output_T6_A.md", Script = "scripts/t6_candidate_pool.py", Args = "--bucket A --top 200", Prompt = "t6_a_dividend_analysis.md" },
        new() { Key = "T6B", Label = "T6 B桶·成长",     Input = "skill_input_T6_B.md", Output = "skill_output_T6_B.md", Script = "scripts/t6_candidate_pool.py", Args = "--bucket B --top 200", Prompt = "t6_b_growth_analysis.md" },
        new() { Key = "T6C", Label = "T6 C桶·热点周期", Input = "skill_input_T6_C.md", Output = "skill_output_T6_C.md", Script = "scripts/t6_candidate_pool.py", Args = "--bucket C --top 200", Prompt = "t6_semantic_ranking.md" },
    };

    private bool _running;
    private CancellationTokenSource? _cts;             // 取消令牌：单次/全桶共用，取消时中断 HTTP 请求
    private readonly DispatcherTimer _elapsedTimer;    // 已用秒数计时器：运行中每秒刷新「已用 0:45」
    private Stopwatch _sw = new();                      // 单次调用/单桶的耗时计时
    private int _batchTotal;                            // 全桶总桶数（用于 [i/total] 分步显示）
    private int _batchIndex;                            // 当前正在跑第几桶（0-based）

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public LlmBridgeView() : this(new AppState(), _ => { }) { }

    public LlmBridgeView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;

        SkillCombo.ItemsSource = Skills.Select(s => $"{s.Key} — {s.Label}").ToList();
        SkillCombo.SelectedIndex = 0;

        GenBtn.Click += async (_, _) => await GenInput();
        CopyBtn.Click += (_, _) => CopyInput();
        CallLlmBtn.Click += async (_, _) => await CallLlmAsync();
        AllBucketsBtn.Click += async (_, _) => await CallAllBucketsAsync();
        CancelBtn.Click += (_, _) => CancelRun();
        ReloadBtn.Click += (_, _) => Load();
        SaveBtn.Click += (_, _) => SaveOutput();
        ImportBtn.Click += (_, _) => ImportFile();
        SkillCombo.SelectionChanged += (_, _) => Load();

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => OnElapsedTick();
    }

    public void OnShown() => Load();

    private SkillDef Cur => Skills[SkillCombo.SelectedIndex < 0 ? 0 : SkillCombo.SelectedIndex];

    private void Load()
    {
        var s = Cur;
        InputText.Text = _app.Store.LoadText(s.Input);
        var mi = _app.Store.FileMtime(s.Input);
        InputInfo.Text = mi.Length > 0 ? $"{_app.Store.LoadText(s.Input).Length} | {mi}" : "未生成";

        OutputText.Text = _app.Store.LoadText(s.Output);
        var mo = _app.Store.FileMtime(s.Output);
        OutputInfo.Text = mo.Length > 0 ? $"{_app.Store.LoadText(s.Output).Length} | {mo}" : "未保存";
    }

    private async Task GenInput()
    {
        if (_running) { _status("生成中，请等待…"); return; }
        // 移动端（iOS/Android）无本地 Python 环境，Process.Start 也不受支持
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            _status("移动端不支持运行本地脚本，请在桌面端生成后查看");
            return;
        }
        var s = Cur;
        var py = string.IsNullOrWhiteSpace(_app.Config.PythonExe) ? "python" : _app.Config.PythonExe;
        var script = Path.Combine(_app.ProjectRoot, s.Script);
        if (!File.Exists(script))
        {
            _status($"脚本不存在: {s.Script}");
            return;
        }
        _running = true;
        GenBtn.IsEnabled = false;
        GenBtn.Content = "⏳ 运行中…";
        _status($"运行 {s.Script} …");

        var psi = new ProcessStartInfo
        {
            FileName = py,
            Arguments = $"\"{script}\" {s.Args}".Trim(),
            WorkingDirectory = _app.ProjectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            var sharedCli=Path.Combine(AppContext.BaseDirectory,"cli","ThreeBucket.Cli.exe");
            if(File.Exists(sharedCli))psi.Environment["THREEBUCKET_FINANCIAL_CLI"]=sharedCli;
            psi.Environment["THREEBUCKET_FINANCIAL_DATA"]=_app.DataDir;
            _status("财务输入仅复用C#共享缓存；缺失不会静默下载。行情/公告仍走原有独立流程。");
            using var proc = Process.Start(psi)!;
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0)
            {
                Load();
                _status($"✓ {s.Key} skill_input 已生成，请点击右侧「复制」");
            }
            else
            {
                _status($"✗ {s.Key} 生成失败 (exit {proc.ExitCode}) {err[..Math.Min(120, err.Length)]}");
            }
        }
        catch (Exception ex)
        {
            _status($"✗ 执行异常: {ex.Message}");
        }
        finally
        {
            _running = false;
            GenBtn.IsEnabled = true;
            GenBtn.Content = "⚡ 生成输入（运行脚本）";
        }
    }

    private void CopyInput()
    {
        var text = InputText.Text;
        if (string.IsNullOrEmpty(text)) { _status("skill_input 为空，请先生成"); return; }
        if (this.VisualRoot is TopLevel tl) _ = tl.Clipboard?.SetTextAsync(text);
        _status("已复制到剪贴板，请粘贴到 Qoder 对话框获取 LLM 分析");
    }

    /// <summary>
    /// 对单个 skill 跑一次 LLM：拼 skills 指令 + skill_input 数据 → OpenAI 兼容 /chat/completions
    /// → 直接写 skill_output 文件。不碰按钮状态、不更新右侧输出框（由调用方决定是否刷新视图）。
    /// 未配置/缺输入/缺指令文件提前拦住，不发空请求。cancellationToken 传入 LlmClient 支持取消。
    /// 返回 (是否成功, 状态文案)；取消时返回 (false, "已取消")，不抛异常。
    ///
    /// T4C 特殊：输入可能达 MB 级（500 只票 × 预告+活动记录正文），单请求必超模型上下文
    /// 或撞 5 分钟超时 → 自动分批（SplitT4Input），逐批调用后合并 JSON 写盘。
    /// </summary>
    private async Task<(bool Ok, string Message)> RunOneAsync(SkillDef s, CancellationToken ct)
    {
        var cfg = _app.Config;
        var llm = new LlmClient(cfg.LlmApiUrl, cfg.LlmApiKey, cfg.LlmModel);
        if (!llm.IsConfigured)
            return (false, "⚠️ 未配置 LLM API（URL/Key 为空），去设置页填写后重试");

        var input = _app.Store.LoadText(s.Input);
        if (string.IsNullOrWhiteSpace(input))
            return (false, $"[{s.Key}] skill_input 为空，请先「生成输入」");

        var promptPath = Path.Combine(_app.ProjectRoot, "skills", s.Prompt);
        if (!File.Exists(promptPath))
            return (false, $"[{s.Key}] skill 指令文件不存在: skills/{s.Prompt}");
        var instruction = await File.ReadAllTextAsync(promptPath, new UTF8Encoding(false));

        // T4C：输入超阈值 → 分批跑（每批独立请求，最后合并 JSON 数组）
        if (s.Key == "T4C" && input.Length > T4BatchThresholdChars)
            return await RunT4BatchedAsync(s, llm, instruction, input, ct);

        var message = instruction + "\n\n---\n\n" + input;
        var (ok, text) = await llm.ChatAsync(message, ct);
        if (ct.IsCancellationRequested) return (false, "已取消");
        if (!ok) return (false, $"[{s.Key}] LLM 调用失败: {text}");
        if (!_app.Store.SaveText(s.Output, text)) return (false, $"[{s.Key}] 保存 skill_output 失败");
        return (true, $"✓ {s.Key} 分析完成，已写入 data/{s.Output}");
    }

    // ── T4C 分批调用 ──────────────────────────────────────────────

    /// <summary>超过此字符数的 T4C 输入自动分批（~40 万 token 上下的单请求会超上下文/超时）。</summary>
    private const int T4BatchThresholdChars = 200_000;

    /// <summary>每批目标字符数（约 6-8 万 token，主流模型 128K 上下文内 comfortably）。</summary>
    private const int T4BatchTargetChars = 250_000;

    /// <summary>
    /// 按 `=== 代码 · 名称` 单票块切分输入。返回 (header, blocks)：header 含板块热度榜等全局信息，
    /// 每批都要带上（LLM 需要板块热度做交叉印证）。
    /// </summary>
    internal static (string Header, List<string> Blocks) SplitT4Input(string input)
    {
        var headerEnd = input.IndexOf("=== ", StringComparison.Ordinal);
        var header = headerEnd > 0 ? input[..headerEnd] : "";
        var blocks = new List<string>();
        var rest = headerEnd > 0 ? input[headerEnd..] : input;

        var parts = rest.Split("=== ");
        // parts[0] 已并入 header（或为空）；其余每段以 "600256 · ..." 开头，
        // 但 "=== " 也可能出现在正文里——用"行首 === 数字代码"校验
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            var nl = p.IndexOf('\n');
            var first = nl > 0 ? p[..nl] : p;
            if (first.Length >= 6 && char.IsDigit(first[0]) && first[..6].All(char.IsDigit))
                blocks.Add(p);
        }
        return (header, blocks);
    }

    /// <summary>
    /// 分批调 LLM 判定 T4C，合并各批 JSON 数组写盘。
    /// 单批失败不中断后续批（结果按批落盘到 part 文件，重试只补失败批）；
    /// 全部完成后合并 → data/skill_output_T4C.md。
    /// </summary>
    private async Task<(bool Ok, string Message)> RunT4BatchedAsync(
        SkillDef s, LlmClient llm, string instruction, string input, CancellationToken ct)
    {
        var (header, blocks) = SplitT4Input(input);
        if (blocks.Count == 0)
            return (false, "[T4C] 输入中未解析出单票块，无法分批");

        // 分批：按目标字符数贪心装箱（单票块不跨批）
        var batches = new List<List<string>>();
        var cur = new List<string>();
        var curLen = 0;
        foreach (var b in blocks)
        {
            if (cur.Count > 0 && curLen + b.Length > T4BatchTargetChars)
            {
                batches.Add(cur);
                cur = new List<string>();
                curLen = 0;
            }
            cur.Add(b);
            curLen += b.Length;
        }
        if (cur.Count > 0) batches.Add(cur);

        _status($"[T4C] 输入 {input.Length / 1024}KB / {blocks.Count} 只 → 分 {batches.Count} 批"
            + $"（每批 ≤{T4BatchTargetChars / 1000}KB，逐批调用）");

        // part 文件：data/.t4_parts/part_{i}.md —— 已成功的批跳过（断点续跑）
        var partDir = Path.Combine(_app.ProjectRoot, "data", ".t4_parts");
        Directory.CreateDirectory(partDir);

        var okCount = 0;
        for (var i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var partFile = Path.Combine(partDir, $"part_{i}.md");
            if (File.Exists(partFile))
            {
                _status($"[T4C] [{i + 1}/{batches.Count}] 已有本批结果，跳过（断点续跑）");
                okCount++;
                continue;
            }

            // 重组与原文格式一致：header 后每块以 "\n=== " 接续（原文块间是 "\n\n=== "，
            // split 时首块把 "=== " 吃进去了，故这里首块也补前缀）
            var batchInput = header + "=== " + string.Join("\n\n=== ", batches[i]);
            _batchIndex = i;
            SetProgressLabel($"⏳ [批 {i + 1}/{batches.Count}] 调用中（本批 {batches[i].Count} 只）");
            _status($"⏳ [T4C] 批 {i + 1}/{batches.Count} 调用中（{batches[i].Count} 只）…");
            _sw.Restart();

            var message = instruction + "\n\n---\n\n" + batchInput;
            var (ok, text) = await llm.ChatAsync(message, ct);
            if (ct.IsCancellationRequested) return (false, $"已取消（完成 {okCount}/{batches.Count} 批，"
                + "重试将从断点续跑）");
            if (!ok)
            {
                _status($"[T4C] 批 {i + 1}/{batches.Count} 失败: {text}");
                continue;   // 单批失败不中断，继续下一批
            }

            File.WriteAllText(partFile, text, new UTF8Encoding(false));
            okCount++;
            SetProgressDetail($"✓ 批 {i + 1}/{batches.Count} 完成");
            _status($"✓ [T4C] 批 {i + 1}/{batches.Count} 完成（{batches[i].Count} 只）");
        }

        if (okCount < batches.Count)
            return (false, $"[T4C] {okCount}/{batches.Count} 批完成（失败批已跳过，"
                + "重试将从断点续跑；全部完成后才写正式输出）");

        // ── 合并各批 JSON 数组 → skill_output_T4C.md ──
        var merged = new List<string>();
        for (var i = 0; i < batches.Count; i++)
        {
            var raw = File.ReadAllText(Path.Combine(partDir, $"part_{i}.md"));
            var arr = ExtractJsonArray(raw);
            if (arr is null)
            {
                return (false, $"[T4C] 批 {i + 1} 输出无法解析为 JSON 数组（part_{i}.md），"
                    + "可在输出框检查该批原文");
            }
            if (merged.Count > 0) merged.Add(",");
            merged.Add(arr.Trim().TrimStart('[').TrimEnd(']'));
        }
        var final = "[\n" + string.Join("\n", merged) + "\n]";
        if (!_app.Store.SaveText(s.Output, final))
            return (false, "[T4C] 合并结果保存失败");

        // 清理 part 文件（合并成功后）
        try { Directory.Delete(partDir, true); } catch { }

        return (true, $"✓ [T4C] 分批分析完成：{batches.Count} 批 × {blocks.Count} 只，"
            + $"已合并写入 data/{s.Output}");
    }

    /// <summary>从 LLM 回复中提取 JSON 数组（复用 EarningsScanTask 的三级降级思路，独立实现以避免 UI↔Core 反向依赖）。</summary>
    private static string? ExtractJsonArray(string text)
    {
        text = text.Trim();
        // 1. 整体是数组
        if (text.StartsWith('[') && text.EndsWith(']')) return text;
        // 2. ```json 围栏
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     text, @"```(?:json)?\s*\n(.*?)\n```", System.Text.RegularExpressions.RegexOptions.Singleline))
            if (m.Groups[1].Value.TrimStart().StartsWith('[')) return m.Groups[1].Value.Trim();
        // 3. [ .. ] 最大区间
        var s = text.IndexOf('[');
        var e = text.LastIndexOf(']');
        if (s >= 0 && e > s) return text[s..(e + 1)];
        return null;
    }

    /// <summary>
    /// T6B 输出末尾若带「巴菲特观察」块，弹勾选列表供用户挑选导入监控页
    /// （默认全选、可逐条取消）。解析为空/用户取消均静默返回，不打断主流程。
    /// 两道防 LLM 张冠李戴：① skill_input 候选行 name→code 反查纠正抄错的代码
    /// （实测 002050 三花智控被写成 002001 新和成）；② 行情官方名终验拦截池外幻觉。
    /// </summary>
    private async Task TryOfferBuffettWatchAsync(SkillDef s)
    {
        if (s.Key != "T6B") return;
        try
        {
            // 候选池反查表：skill_input 每行 "code,name,industry,..." → name→code
            var codeByName = new Dictionary<string, string>();
            foreach (var line in _app.Store.LoadText(s.Input).Split('\n'))
            {
                var csv = line.Trim();
                if (csv.Length < 8 || !char.IsDigit(csv[0])) continue;
                var parts = csv.Split(',');
                if (parts.Length >= 2 && parts[0].Length == 6 && parts[0].All(char.IsDigit) && parts[1].Length >= 2)
                    codeByName[parts[1].Trim()] = parts[0];
            }

            var (items, discarded) = BuffettWatchImporter.Parse(_app.Store.LoadText(s.Output), codeByName);
            if (items.Count == 0)
            {
                if (discarded.Count > 0) _status($"⚠️ 巴菲特观察 {discarded.Count} 行格式非法未导入");
                return;
            }
            // 行情官方名终验（失败静默放行，不阻断导入主流程）
            var (kept, quoteRejected) = await BuffettWatchImporter.VerifyByQuotesAsync(
                codes => _app.Quotes.FetchAsync(codes), items);
            discarded.AddRange(quoteRejected);
            if (kept.Count == 0) { _status($"⚠️ 巴菲特观察全部条目校验未过，未导入"); return; }

            var dlg = new BuffettWatchDialog(kept, discarded);
            if (await dlg.ShowAsync(this))
            {
                var results = BuffettWatchImporter.Import(_app.Store, dlg.Selected);
                _status(results.Count > 0
                    ? "🎯 " + string.Join("；", results)
                    : "未勾选任何条目，跳过导入");
            }
        }
        catch (Exception ex)
        {
            _status($"⚠️ 巴菲特观察解析异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 单桶调用：对当前下拉选中的 skill 调一次 LLM，写 skill_output 并刷新右侧输出框。
    /// 与「复制→Qoder→粘贴」手动路径并行。失败仅状态栏上报，不抛异常。
    /// 进度反馈：进度条不确定模式 + 已用秒数计时；冗长时每 15s 状态栏报「仍在等待」心跳。
    /// </summary>
    private async Task CallLlmAsync()
    {
        if (_running) { _status("处理中，请等待…"); return; }
        var s = Cur;

        _running = true;
        _cts = new CancellationTokenSource();
        CallLlmBtn.IsEnabled = false;
        CallLlmBtn.Content = "⏳ 调用中…";
        AllBucketsBtn.IsEnabled = false;
        CancelBtn.IsVisible = true;
        // T4C 输入超阈值会自动分批 → 进度条按批推进（RunT4BatchedAsync 更新 _batchIndex）；
        // 预先按输入大小估算批数，让进度条上限正确
        _batchTotal = 0;
        if (s.Key == "T4C")
        {
            var inputLen = _app.Store.LoadText(s.Input).Length;
            if (inputLen > T4BatchThresholdChars)
                _batchTotal = Math.Max(1, (int)Math.Ceiling((double)inputLen / T4BatchTargetChars));
        }
        BeginProgress();
        SetProgressLabel($"⏳ 调用 LLM 分析 {s.Key}" + (_batchTotal > 0 ? $"（约 {_batchTotal} 批）" : ""));
        _status($"⏳ 调用 LLM 分析 {s.Key}" + (_batchTotal > 0
            ? $"（输入 {_batchTotal} 批量级，每批 1-3 分钟，全程可能 10-30 分钟）…"
            : "（可能需 1–3 分钟）…"));

        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        heartbeat.Tick += (_, _) =>
        {
            if (!_running) return;
            _status($"⏳ {s.Key} 仍在等待 LLM 响应（已用 {FormatElapsed(_sw.Elapsed)})，长文本分析较慢请耐心…");
        };
        try
        {
            heartbeat.Start();
            var (ok, msg) = await RunOneAsync(s, _cts.Token);
            heartbeat.Stop();
            if (ok)
            {
                OutputText.Text = _app.Store.LoadText(s.Output);
                OutputInfo.Text = $"已保存 | {_app.Store.FileMtime(s.Output)}";
                await TryOfferBuffettWatchAsync(s);
            }
            _status(msg);
        }
        catch (OperationCanceledException) { _status("已取消"); }
        finally
        {
            heartbeat.Stop();
            EndProgress();
            _running = false;
            _cts?.Dispose(); _cts = null;
            CallLlmBtn.IsEnabled = true;
            CallLlmBtn.Content = "⚡ 调用 LLM";
            AllBucketsBtn.IsEnabled = true;
            CancelBtn.IsVisible = false;
        }
    }

    /// <summary>
    /// 全桶调用：依次对 T6A → T6B → T6C 跑 LLM，逐桶写 skill_output。
    /// 单桶失败不中断后续桶；每桶完成后立即刷新右侧输出框（如果选中的是刚完成的桶），
    /// 让用户在等待中看到结果陆续出现。结束后重载视图并汇总上报成败数。
    /// 进度反馈：分步 [i/total] + 进度条按桶推进 + 已用秒数计时。
    /// </summary>
    private async Task CallAllBucketsAsync()
    {
        if (_running) { _status("处理中，请等待…"); return; }
        var buckets = Skills.Where(x => x.Key is "T6A" or "T6B" or "T6C").ToArray();
        if (buckets.Length == 0) { _status("未找到 T6 桶任务"); return; }

        _running = true;
        _cts = new CancellationTokenSource();
        AllBucketsBtn.IsEnabled = false;
        AllBucketsBtn.Content = "⏳ 全桶调用中…";
        CallLlmBtn.IsEnabled = false;
        CancelBtn.IsVisible = true;
        _batchTotal = buckets.Length;
        _batchIndex = 0;
        BeginProgress();
        var okCount = 0;
        var fails = new List<string>();
        try
        {
            for (var i = 0; i < buckets.Length; i++)
            {
                _batchIndex = i;
                var s = buckets[i];
                _sw.Restart();
                SetProgressLabel($"⏳ [{i + 1}/{buckets.Length}] {s.Key} 调用中");
                SetProgressDetail("");
                _status($"⏳ [{i + 1}/{buckets.Length}] 调用 LLM 分析 {s.Key}…");
                var (ok, msg) = await RunOneAsync(s, _cts.Token);
                _sw.Stop();
                if (_cts.IsCancellationRequested) { _status("已取消，已完成的桶结果已保留"); break; }
                if (ok)
                {
                    okCount++;
                    SetProgressDetail($"✓ {s.Key} 完成");
                    // 若右侧正在显示刚完成的桶，立即刷新看到结果
                    if (Cur.Key == s.Key)
                    {
                        OutputText.Text = _app.Store.LoadText(s.Output);
                        OutputInfo.Text = $"已保存 | {_app.Store.FileMtime(s.Output)}";
                    }
                    await TryOfferBuffettWatchAsync(s);
                }
                else
                {
                    fails.Add(s.Key);
                    SetProgressDetail($"✗ {s.Key} 失败");
                }
                _status(msg);
            }
            // 重载当前选中项的输入/输出，右侧框反映最新状态
            Load();
            _status(fails.Count == 0
                ? $"✓ 全桶完成（{okCount}/{buckets.Length}）：{string.Join("、", buckets.Select(b => b.Key))}"
                : $"⚠️ 全桶完成 {okCount}/{buckets.Length}，失败：{string.Join("、", fails)}");
        }
        catch (OperationCanceledException) { _status("已取消"); }
        finally
        {
            EndProgress();
            _running = false;
            _cts?.Dispose(); _cts = null;
            AllBucketsBtn.IsEnabled = true;
            AllBucketsBtn.Content = "⚡ 全桶调用（T6A→C）";
            CallLlmBtn.IsEnabled = true;
            CancelBtn.IsVisible = false;
        }
    }

    /// <summary>取消按钮：触发 token，HTTP 请求中断，循环跳出。已完成桶的结果已落盘保留。</summary>
    private void CancelRun()
    {
        if (!_running) return;
        _cts?.Cancel();
        _status("⏹ 正在取消…");
        CancelBtn.IsEnabled = false;
        CancelBtn.Content = "⏹ 取消中…";
    }

    // ── 进度/计时辅助 ──

    /// <summary>开启进度面板与计时器：单桶为不确定进度，全桶按 [i/total] 分步（RunOneAsync 间推进）。</summary>
    private void BeginProgress()
    {
        ProgBar.IsIndeterminate = _batchTotal == 0; // 单桶：不确定条；全桶：按桶推进
        if (_batchTotal > 0) { ProgBar.Minimum = 0; ProgBar.Maximum = _batchTotal; ProgBar.Value = 0; }
        ProgressPanel.IsVisible = true;
        ProgressTimer.Text = "已用 0:00";
        _sw.Restart();
        _elapsedTimer.Start();
        CancelBtn.IsEnabled = true;
        CancelBtn.Content = "✖ 取消";
    }

    /// <summary>收尾：停计时、隐藏进度面板、清空文案。</summary>
    private void EndProgress()
    {
        _elapsedTimer.Stop();
        _sw.Stop();
        ProgressPanel.IsVisible = false;
        ProgressLabel.Text = "";
        ProgressTimer.Text = "";
        ProgressDetail.Text = "";
        ProgBar.Value = 0;
        ProgBar.IsIndeterminate = false;
    }

    /// <summary>每秒刷新已用时显示；全桶模式下同步推进进度条到当前桶。</summary>
    private void OnElapsedTick()
    {
        if (!_running) return;
        ProgressTimer.Text = $"已用 {FormatElapsed(_sw.Elapsed)}";
        if (_batchTotal > 0) ProgBar.Value = _batchIndex;
    }

    private void SetProgressLabel(string text) => ProgressLabel.Text = text;
    private void SetProgressDetail(string text) => ProgressDetail.Text = text;

    /// <summary>秒级耗时格式 mm:ss（单桶最长 5min 超时，全桶最长约 15min，mm:ss 足够）。</summary>
    private static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    private async void SaveOutput()
    {
        var s = Cur;
        var text = OutputText.Text;
        if (string.IsNullOrWhiteSpace(text)) { _status("输出内容为空"); return; }
        if (_app.Store.SaveText(s.Output, text))
        {
            var mo = _app.Store.FileMtime(s.Output);
            OutputInfo.Text = $"已保存 | {mo}";
            _status($"已保存到 data/{s.Output}");
            // 手动工作流（Qoder 粘贴→保存）同样触发巴菲特观察导入弹窗
            await TryOfferBuffettWatchAsync(s);
        }
        else
        {
            _status("保存失败，请检查文件权限");
        }
    }

    private async void ImportFile()
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is not { CanOpen: true }) { _status("当前平台不支持选择文件"); return; }
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 skill_output",
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Markdown") { Patterns = new[] { "*.md" }, MimeTypes = new[] { "text/markdown", "text/plain" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0) return;
        // 移动端返回的是 content:// URI，没有本地路径，统一走流读取
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            OutputText.Text = await reader.ReadToEndAsync();
            _status($"已从 {files[0].Name} 导入");
        }
        catch (Exception ex) { _status($"导入失败: {ex.Message}"); }
    }
}
