using System.Text;
using System.Text.Json;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Services;

namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// --qv review / review-test / review-add / review-revoke / review-list。
/// 测试路径默认不发送飞书。
/// </summary>
public static class QvReviewCli
{
    public static readonly string[] OfficialStrikeV151 =
    [
        "000915", // 华特达因
        "600273", // 嘉化能源
        "600507", // 方大特钢
        "600585", // 海螺水泥
        "002027", // 分众传媒
        "002807", // 江阴银行
        "600971", // 恒源煤电
        "600398", // 海澜之家
        "000932", // 华菱钢铁
        "000651", // 格力电器
        "603886", // 元祖股份
        "605377", // 华旺科技
        "002043", // 兔宝宝
    ];

    public static bool Handles(string cmd) => cmd is "review" or "review-test" or "strike-review"
        or "review-list" or "review-add" or "review-revoke" or "review-edit";

    public static async Task<int> RunAsync(string dataDir, string cmd, string? codes, string? extra, CancellationToken ct)
    {
        void L(string m) => Console.WriteLine(m);
        var outDir = Path.Combine(dataDir, "quality_value");
        Directory.CreateDirectory(outDir);
        cmd = cmd.ToLowerInvariant();

        if (cmd == "review-list")
        {
            var rows = QvReviewHistory.Load(outDir);
            if (rows.Count == 0) { L("（尚无人工复核历史）"); return 0; }
            foreach (var r in rows)
                L($"{r.Id}\t{r.Code} {r.Name}\t{r.ReviewTime}\t{r.Verdict}\t有效至{r.ValidUntil}\t撤销={r.Revoked}\t{r.TriggerReason}\t{r.Note}");
            return 0;
        }
        if (cmd == "review-add")
        {
            // codes 格式：code,verdict  或 code；extra 为备注；有效期默认 90 天
            // 也允许 codes=code
            var parts = (codes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                L("用法: --qv review-add --codes 代码,结论[,有效天数]   结论=" + string.Join("/", QvReviewHistory.Verdicts));
                return 2;
            }
            var code = parts[0].PadLeft(6, '0');
            var verdict = parts[1];
            if (!QvReviewHistory.Verdicts.Contains(verdict))
            {
                L("未知结论 " + verdict + "，允许：" + string.Join("/", QvReviewHistory.Verdicts));
                return 2;
            }
            var days = 90;
            if (parts.Length >= 3 && int.TryParse(parts[2], out var d)) days = d;
            var name = NameOf(outDir, code);
            var rec = QvReviewHistory.Add(outDir, new QvReviewRecord
            {
                Code = code,
                Name = name,
                Verdict = verdict,
                Note = extra ?? "",
                TriggerReason = "cli-add",
                SubsequentSignalStatus = "pending",
                NextRereviewTrigger = "新财报、新预告/修正、新重大公告、风险指标明显恶化、估值模型实质变化、或价格进入更深安全边际",
                ValidUntil = DateTime.Now.Date.AddDays(days).ToString("yyyy-MM-dd"),
                Source = "cli",
            });
            L($"已记录 {rec.Id} {rec.Code} {rec.Name} {rec.Verdict} 有效至 {rec.ValidUntil}");
            return 0;
        }
        if (cmd == "review-revoke")
        {
            var id = (codes ?? extra ?? "").Trim();
            if (id.Length == 0) { L("用法: --qv review-revoke --codes 记录ID"); return 2; }
            if (!QvReviewHistory.Revoke(outDir, id, "cli-revoke"))
            { L("未找到记录 " + id); return 2; }
            L("已撤销 " + id);
            return 0;
        }
        if (cmd == "review-edit")
        {
            // --codes id,verdict  --out 作为备注
            var parts = (codes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 1) { L("用法: --qv review-edit --codes 记录ID,新结论"); return 2; }
            var ok = QvReviewHistory.Update(outDir, parts[0], rec =>
            {
                if (parts.Length >= 2)
                {
                    if (!QvReviewHistory.Verdicts.Contains(parts[1]))
                        throw new ArgumentException("未知结论 " + parts[1]);
                    rec.Verdict = parts[1];
                }
                if (!string.IsNullOrWhiteSpace(extra)) rec.Note = extra;
                rec.Source = string.IsNullOrWhiteSpace(rec.Source) ? "cli-edit" : rec.Source + ",cli-edit";
            });
            L(ok ? "已修改 " + parts[0] : "未找到 " + parts[0]);
            return ok ? 0 : 2;
        }

        // review / review-test / strike-review：对击球区做复核（不重算评分/估值，不发飞书）
        return await RunTestAsync(dataDir, L, ct).ConfigureAwait(false);
    }

    public static async Task<int> RunTestAsync(string dataDir, Action<string> log, CancellationToken ct)
    {
        var outDir = Path.Combine(dataDir, "quality_value");
        var cfg = QvConfig.Load(dataDir);
        var cache = Path.Combine(dataDir, "cache");
        var em = new EastMoneyClient(cache);
        var data = new QvData(dataDir, em, log);
        data.LoadSidecars();

        var snap = Path.Combine(outDir, "state_v1.5.1_20261113_175001.json");
        var live = Path.Combine(outDir, "state.json");
        var statePath = File.Exists(snap) ? snap : live;
        if (!File.Exists(statePath))
        {
            log("找不到 state 快照或 state.json，无法在不重扫的前提下复核。");
            return 2;
        }
        var json = File.ReadAllText(statePath);
        var prev = JsonSerializer.Deserialize<QvOutput>(json, JsonOpt());
        if (prev is null || prev.Rows.Count == 0)
        {
            log("state 无法反序列化或没有行。");
            return 2;
        }

        // 对比基准：v1.5.1 快照（若当前读的已是快照，则自身即基准）
        var baseline = prev;
        if (statePath != snap && File.Exists(snap))
            baseline = JsonSerializer.Deserialize<QvOutput>(File.ReadAllText(snap), JsonOpt()) ?? prev;

        var strike = prev.Rows.Where(r => r.Zone == "strike").ToList();
        log($"复核测试：从 {Path.GetFileName(statePath)} 读取 {prev.Rows.Count} 行，击球区 {strike.Count} 只。不重算评分/估值，飞书 dry-run。");

        var work = strike.Select(r => new QvStock
        {
            Code = r.Code,
            Name = r.Name,
            Type = r.Type,
            IndustryRaw = r.Industry,
        }).ToList();

        // 补数：季报缓存 + 预告 + 公告（失败则标记缺失）
        foreach (var s in work)
            await QvReview.BackfillAsync(s, data, cfg, log, ct).ConfigureAwait(false);

        // 清空本轮将写入的复核字段，避免旧 flags 干扰（但不动评分估值池区）
        foreach (var r in strike)
            ClearReviewFields(r);

        await QvReview.ApplyAsync(work, prev.Rows, data, cfg, outDir, log, ct).ConfigureAwait(false);

        var ts = TradingCalendar.NowCn().ToString("yyyyMMdd_HHmmss");
        var report = BuildReport(prev, baseline, work, cfg, outDir, statePath, ts);
        var reportPath = Path.Combine(outDir, $"review_report_{TradingCalendar.NowCn():yyyy-MM-dd}_{ts[9..]}.md");
        File.WriteAllText(reportPath, report, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "review_report_latest.md"), report, new UTF8Encoding(false));
        log("复核报告 " + reportPath);

        // 验收：华特达因 TEST 结论，再跑一遍去重，然后撤销 TEST
        var hutai = strike.FirstOrDefault(x => x.Code == "000915");
        string? testId = null;
        if (hutai is not null)
        {
            var rec = QvReviewHistory.Add(outDir, new QvReviewRecord
            {
                Code = hutai.Code,
                Name = hutai.Name,
                Verdict = "uncertain_wait",
                Note = "TEST：v1.5.2 验收写入，随后将撤销，不作为正式结论。",
                TriggerReason = hutai.ReviewTriggers,
                Fingerprint = hutai.ReviewFingerprint,
                SubsequentSignalStatus = "TEST",
                NextRereviewTrigger = "新财报、新预告/修正、新重大公告、风险指标明显恶化、估值模型实质变化、或价格进入更深安全边际",
                ValidUntil = DateTime.Now.Date.AddDays(90).ToString("yyyy-MM-dd"),
                Source = "acceptance-TEST",
                PriceAtReview = (hutai.Price ?? 0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                PSafeAtReview = (hutai.PSafe ?? 0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            });
            testId = rec.Id;
            log($"已写入 TEST 结论 {rec.Id}（000915 华特达因 / uncertain_wait），即将二次扫描验证去重。");

            var history = QvReviewHistory.Load(outDir);
            var card2 = QvReview.Evaluate(hutai, work.First(s => s.Code == "000915"), prev.Rows, work, cfg, history, TradingCalendar.NowCn());
            log(card2.DedupSuppressed
                ? $"二次扫描：000915 已去重，不重复推送（有效结论 {card2.ActiveVerdict?.Id}）。"
                : "二次扫描：000915 未被去重（异常，请检查指纹）。");
            report += "\n\n## 去重验收（000915 TEST）\n\n"
                      + (card2.DedupSuppressed
                          ? $"二次扫描已抑制重复推送。指纹={card2.Fingerprint}。有效结论 {card2.ActiveVerdict?.Id} / {card2.ActiveVerdict?.Verdict}。\n"
                          : "二次扫描未被去重，需排查。\n");

            if (QvReviewHistory.Revoke(outDir, rec.Id, "验收结束，撤销 TEST 结论"))
                log($"已撤销 TEST 结论 {rec.Id}。历史行保留，Revoked=true。");
            else
                log("撤销 TEST 失败。");
            File.WriteAllText(reportPath, report, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "review_report_latest.md"), report, new UTF8Encoding(false));
        }
        else log("击球区未找到 000915 华特达因，跳过 TEST 结论。");

        // 把复核字段写回 latest state，不改评分/估值/池/区；不覆盖 v1.5.1 快照。
        if (File.Exists(live))
        {
            try
            {
                var liveObj = JsonSerializer.Deserialize<QvOutput>(File.ReadAllText(live), JsonOpt());
                if (liveObj is not null)
                {
                    var map = prev.Rows.ToDictionary(x => x.Code, x => x);
                    foreach (var r in liveObj.Rows)
                    {
                        if (!map.TryGetValue(r.Code, out var src)) continue;
                        CopyReview(src, r);
                    }
                    var bak = Path.Combine(outDir, "state_before_review_fields.json");
                    if (!File.Exists(bak)) File.Copy(live, bak, overwrite: false);
                    File.WriteAllText(live, JsonSerializer.Serialize(liveObj, JsonWrite()), new UTF8Encoding(false));
                    log("已将复核字段写回 state.json（评分/估值/池/区未改）。");
                }
            }
            catch (Exception ex) { log("写回 state.json 复核字段失败：" + ex.Message); }
        }

        // 打印 13 只表头
        log("──── 13 只击球区分流 ────");
        log("代码 名称 分流 优先级 级别 恶化 入队 去重 强提醒 触发");
        foreach (var code in OfficialStrikeV151)
        {
            var r = prev.Rows.FirstOrDefault(x => x.Code == code);
            if (r is null) { log(code + " 本轮 state 中未找到"); continue; }
            log($"{r.Code} {r.Name} {r.ReviewClass} {r.ReviewPriority} {r.ReviewPriorityLevel} {r.EarningsSeverity} queued={r.ReviewQueued} dedup={r.ReviewDeduped} {r.StrongAlertReviewAction} | {Trim(r.ReviewTriggers, 120)}");
        }
        log("报告 " + reportPath);
        if (hutai is not null)
            log("华特达因卡片 " + Path.Combine(outDir, "review_cards", "000915.md"));
        _ = testId;
        return 0;
    }

    static void ClearReviewFields(QvRow r)
    {
        // 去掉先前复核 flag，保留原扫描 flag
        if (r.FlagList is { Count: > 0 })
            r.FlagList = r.FlagList.Where(f => !f.StartsWith("review_class_")
                && f is not "earnings_deterioration" and not "valuation_invalidity_risk"
                and not "strong_alert_held" and not "strong_alert_frozen").ToList();
        r.Flags = string.Join(",", r.FlagList ?? []);
        r.ReviewClass = "";
        r.ReviewPriority = 0;
        r.ReviewPriorityLevel = "";
        r.ReviewTriggers = "";
        r.ReviewQueued = false;
        r.ReviewDeduped = false;
        r.ReviewFingerprint = "";
        r.ReviewCard = "";
        r.EarningsSeverity = "";
        r.EarningsSummary = "";
        r.ValuationInvalidityRisk = false;
        r.StrongAlertReviewAction = "";
        r.ReviewSuggestedAction = "";
    }

    static void CopyReview(QvRow src, QvRow dst)
    {
        dst.ReviewClass = src.ReviewClass;
        dst.ReviewPriority = src.ReviewPriority;
        dst.ReviewPriorityLevel = src.ReviewPriorityLevel;
        dst.ReviewTriggers = src.ReviewTriggers;
        dst.ReviewQueued = src.ReviewQueued;
        dst.ReviewDeduped = src.ReviewDeduped;
        dst.ReviewFingerprint = src.ReviewFingerprint;
        dst.ReviewCard = src.ReviewCard;
        dst.EarningsSeverity = src.EarningsSeverity;
        dst.EarningsSummary = src.EarningsSummary;
        dst.ValuationInvalidityRisk = src.ValuationInvalidityRisk;
        dst.StrongAlertReviewAction = src.StrongAlertReviewAction;
        dst.ReviewSuggestedAction = src.ReviewSuggestedAction;
        dst.Flags = src.Flags;
        dst.FlagList = src.FlagList?.ToList() ?? new List<string>();
    }

    static string BuildReport(QvOutput cur, QvOutput baseline, List<QvStock> work, QvConfig cfg, string outDir, string statePath, string ts)
    {
        var sb = new StringBuilder();
        var now = TradingCalendar.NowCn();
        sb.AppendLine("# v1.5.2 击球区智能复核报告");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间（Asia/Shanghai）：{now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 读取状态：{statePath}");
        sb.AppendLine("- 本模块不修改基本面评分和三档估值，不提高击球/评分门槛，不删除周期股。");
        sb.AppendLine($"- 飞书：review.feishu_dry_run={cfg.B("review.feishu_dry_run", true)}，本测试未发送真实推送。");
        sb.AppendLine();

        // 池区对比
        var bStrike = baseline.Rows.Where(x => x.Zone == "strike").Select(x => x.Code).OrderBy(x => x).ToList();
        var cStrike = cur.Rows.Where(x => x.Zone == "strike").Select(x => x.Code).OrderBy(x => x).ToList();
        var bPool = baseline.Rows.ToDictionary(x => x.Code, x => (x.Pool, x.Zone, x.QualityScore, x.PFair, x.PSafe, x.POpt));
        var unchanged = 0; var changed = new List<string>();
        foreach (var r in cur.Rows)
        {
            if (!bPool.TryGetValue(r.Code, out var b)) { changed.Add(r.Code + " 基准中不存在"); continue; }
            if (b.Pool == r.Pool && b.Zone == r.Zone && Eq(b.QualityScore, r.QualityScore) && Eq(b.PFair, r.PFair) && Eq(b.PSafe, r.PSafe) && Eq(b.POpt, r.POpt))
                unchanged++;
            else
                changed.Add($"{r.Code}{r.Name} 池 {b.Pool}->{r.Pool} 区 {b.Zone}->{r.Zone} 评分 {b.QualityScore}->{r.QualityScore} 合理价 {b.PFair}->{r.PFair}");
        }
        sb.AppendLine("## 与 v1.5.1 快照对比（池/区/评分/估值）");
        sb.AppendLine();
        sb.AppendLine($"- 基准击球区 {bStrike.Count} 只：{string.Join("、", baseline.Rows.Where(x => x.Zone=="strike").Select(x => x.Code + x.Name))}");
        sb.AppendLine($"- 本轮击球区 {cStrike.Count} 只：{string.Join("、", cur.Rows.Where(x => x.Zone=="strike").Select(x => x.Code + x.Name))}");
        sb.AppendLine($"- 击球区集合{(bStrike.SequenceEqual(cStrike) ? "不变" : "有变化（异常）")}");
        sb.AppendLine($"- 池/区/评分/三档价格未改行数 {unchanged} / {cur.Rows.Count}；变化 {changed.Count}。");
        if (changed.Count > 0)
            foreach (var c in changed.Take(20)) sb.AppendLine("  - " + c);
        sb.AppendLine();

        var strike = cur.Rows.Where(x => x.Zone == "strike").ToList();
        sb.AppendLine("## 分流汇总");
        sb.AppendLine();
        sb.AppendLine($"- A {strike.Count(x => x.ReviewClass=="A")}（稳定，可自动放行强提醒候选）");
        sb.AppendLine($"- B {strike.Count(x => x.ReviewClass=="B")}（边际恶化，暂缓强提醒，入队）");
        sb.AppendLine($"- C {strike.Count(x => x.ReviewClass=="C")}（重大不确定性，冻结强提醒）");
        sb.AppendLine($"- D {strike.Count(x => x.ReviewClass=="D")}（数据/估值问题）");
        sb.AppendLine($"- 人工队列（去重后需推送）{strike.Count(x => x.ReviewQueued)} / 击球 {strike.Count}");
        sb.AppendLine();

        sb.AppendLine("## 13 只击球区明细");
        sb.AppendLine();
        sb.AppendLine("| 代码 | 名称 | 分流 | 优先级 | 级别 | 恶化程度 | 入队 | 去重 | 强提醒 | 估值失效风险 | 评分 | 现价 | 触发摘要 |");
        sb.AppendLine("|---|---|---|---:|---|---|---|---|---|---|---:|---:|---|");
        foreach (var code in OfficialStrikeV151)
        {
            var r = cur.Rows.FirstOrDefault(x => x.Code == code);
            if (r is null) { sb.AppendLine($"| {code} | （state 中未找到） | | | | | | | | | | | |"); continue; }
            sb.AppendLine($"| {r.Code} | {r.Name} | {r.ReviewClass} | {r.ReviewPriority} | {r.ReviewPriorityLevel} | {r.EarningsSeverity} | {(r.ReviewQueued ? "是" : "否")} | {(r.ReviewDeduped ? "是" : "否")} | {r.StrongAlertReviewAction} | {(r.ValuationInvalidityRisk ? "是" : "否")} | {r.QualityScore:F1} | {r.Price:F2} | {Trim(r.ReviewTriggers, 80).Replace("|", "/")} |");
        }
        sb.AppendLine();

        var hutai = cur.Rows.FirstOrDefault(x => x.Code == "000915");
        sb.AppendLine("## 华特达因（000915）验收清单");
        sb.AppendLine();
        if (hutai is null)
            sb.AppendLine("state 击球区中未找到 000915。");
        else
        {
            sb.AppendLine($"- 识别近期业绩下滑：{(hutai.EarningsSeverity is not "none" and not "" ? "是（" + hutai.EarningsSeverity + "）" : "否/未触发阈值")}。");
            sb.AppendLine($"- 摘要：{hutai.EarningsSummary}");
            sb.AppendLine($"- 估值可能失效：{(hutai.ValuationInvalidityRisk ? "是（原估值未重算）" : "否")}");
            sb.AppendLine($"- 暂缓强提醒：{(hutai.StrongAlertReviewAction is "hold" or "freeze" ? "是（" + hutai.StrongAlertReviewAction + "）" : "否（" + hutai.StrongAlertReviewAction + "）")}");
            sb.AppendLine($"- 分流 {hutai.ReviewClass}，优先级 {hutai.ReviewPriority}/{hutai.ReviewPriorityLevel}，入队 {hutai.ReviewQueued}。");
            sb.AppendLine("- 卡片见 `review_cards/000915.md`（含人工需判断的问题与数据来源）。");
            sb.AppendLine("- TEST 结论写入后二次扫描应去重，随后撤销 TEST。见文末去重验收。");
        }
        sb.AppendLine();

        var would = QvReview.BuildWouldPush(cur.Rows, cfg);
        sb.AppendLine("## 本应发送的飞书消息（未实际发送）");
        sb.AppendLine();
        sb.AppendLine($"共 {would.Count} 条，分类：" + string.Join("、", would.GroupBy(x => x.Category).Select(g => g.Key + " " + g.Count())));
        if (would.Count == 0) sb.AppendLine("（无）");
        int i = 1;
        foreach (var w in would)
        {
            sb.AppendLine($"### {i}. [{w.Category}] {w.Title}");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(w.Body);
            sb.AppendLine("```");
            sb.AppendLine();
            i++;
        }

        sb.AppendLine("## 数据补数情况");
        sb.AppendLine();
        foreach (var s in work.OrderBy(x => x.Code))
        {
            sb.AppendLine($"- {s.Code} {s.Name}：报告期 {s.Periods.Count} 条，预告 {(s.ForecastsMissing ? "缺失" : s.Forecasts.Count + " 条")}，公告 {(s.NoticesMissing ? "缺失" : s.Notices.Count + " 条（重大 " + s.Notices.Count(n => n.Major) + "）")}。");
        }
        sb.AppendLine();
        sb.AppendLine("阈值见 config/quality_value/review.yaml。所有判断可追溯至本报告与复核卡片。");
        return sb.ToString();
    }

    static bool Eq(double? a, double? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return Math.Abs(a.Value - b.Value) < 1e-9;
    }

    static string Trim(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n] + "…";
    }

    static string NameOf(string outDir, string code)
    {
        var live = Path.Combine(outDir, "state.json");
        if (!File.Exists(live)) return "";
        try
        {
            var o = JsonSerializer.Deserialize<QvOutput>(File.ReadAllText(live), JsonOpt());
            return o?.Rows.FirstOrDefault(x => x.Code == code)?.Name ?? "";
        }
        catch { return ""; }
    }

    static JsonSerializerOptions JsonOpt() => new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    static JsonSerializerOptions JsonWrite() => new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
