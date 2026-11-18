using System.Globalization;
using System.Text;

namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// v1.5.2 击球区智能复核。只做风险识别、任务排序、提醒分流和人工结论管理。
/// 不修改 QualityScore / PFair / PSafe / POpt / Pool / Zone。
/// </summary>
public static class QvReview
{
    public static async Task ApplyAsync(
        IReadOnlyList<QvStock> work,
        IReadOnlyList<QvRow> rows,
        QvData data,
        QvConfig cfg,
        string outDir,
        Action<string>? log,
        CancellationToken ct,
        bool persistStateFields = true)
    {
        Directory.CreateDirectory(outDir);
        var asOf = TradingCalendar.NowCn();
        var strike = rows.Where(r => r.Zone == "strike").ToList();
        log?.Invoke($"复核：击球区 {strike.Count} 只，开始补数（季报已缓存/预告/公告）…");

        var workList = work.ToList();
        var byCode = workList.ToDictionary(s => s.Code, s => s);
        foreach (var r in strike)
        {
            if (!byCode.TryGetValue(r.Code, out var s)) continue;
            await BackfillAsync(s, data, cfg, log, ct).ConfigureAwait(false);
        }

        // 同行业对比：从本轮行里取同行业样本，只补季报缓存，不改评分估值。
        var inds = strike.Select(r => r.Industry).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        var peerRows = rows.Where(r => r.Zone != "strike" && inds.Contains(r.Industry) && r.Pool is "qualified" or "watch")
            .GroupBy(r => r.Industry)
            .SelectMany(g => g.OrderByDescending(x => x.QualityScore ?? 0).Take(12))
            .ToList();
        var added = 0;
        foreach (var pr in peerRows)
        {
            if (byCode.ContainsKey(pr.Code)) continue;
            var s = new QvStock { Code = pr.Code, Name = pr.Name, Type = pr.Type, IndustryRaw = pr.Industry };
            try { await data.EnsurePeriodsAsync(s, ct).ConfigureAwait(false); }
            catch { }
            if (s.Periods.Count == 0) continue;
            workList.Add(s);
            byCode[s.Code] = s;
            added++;
        }
        if (added > 0) log?.Invoke($"复核：为同行业对比从缓存补季报 {added} 只（不改评分/估值）");

        var history = QvReviewHistory.Load(outDir);
        var cardsDir = Path.Combine(outDir, "review_cards");
        Directory.CreateDirectory(cardsDir);

        foreach (var r in strike)
        {
            byCode.TryGetValue(r.Code, out var s);
            var card = Evaluate(r, s, rows, workList, cfg, history, asOf);
            ApplyToRow(r, card);
            File.WriteAllText(Path.Combine(cardsDir, $"{r.Code}.md"), card.Markdown, new UTF8Encoding(false));
        }

        var queued = strike.Where(r => r.ReviewQueued).OrderByDescending(r => r.ReviewPriority).ToList();
        log?.Invoke($"复核完成：A {strike.Count(x => x.ReviewClass=="A")} / B {strike.Count(x => x.ReviewClass=="B")} / C {strike.Count(x => x.ReviewClass=="C")} / D {strike.Count(x => x.ReviewClass=="D")}；入队 {queued.Count}（去重后将推送见报告）");
        _ = persistStateFields;
    }

    public static async Task BackfillAsync(QvStock s, QvData data, QvConfig cfg, Action<string>? log, CancellationToken ct)
    {
        try
        {
            if (s.Periods.Count == 0)
                await data.EnsurePeriodsAsync(s, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  {s.Code} 季报补数失败：{ex.Message}");
        }

        try
        {
            if (s.Forecasts.Count == 0)
                s.Forecasts = await data.FetchForecastsAsync(s.Code, ct).ConfigureAwait(false);
        }
        catch
        {
            s.ForecastsMissing = s.Forecasts.Count == 0;
        }
        if (s.Forecasts.Count == 0) s.ForecastsMissing = true;

        try
        {
            if (s.Notices.Count == 0)
            {
                var raw = await data.FetchNoticesAsync(s.Code, 180, ct).ConfigureAwait(false);
                var keys = MajorKeys(cfg);
                s.Notices = raw.Select(a => new QvNoticeItem
                {
                    Date = a.Date,
                    Title = a.Title,
                    Major = keys.Any(k => a.Title.Contains(k, StringComparison.Ordinal)),
                }).ToList();
            }
        }
        catch
        {
            s.NoticesMissing = s.Notices.Count == 0;
        }
        if (s.Notices.Count == 0) s.NoticesMissing = true;
    }

    public static QvReviewCard Evaluate(
        QvRow r, QvStock? s, IReadOnlyList<QvRow> allRows,
        IReadOnlyList<QvStock> work, QvConfig cfg,
        IReadOnlyList<QvReviewRecord> history, DateTime asOf)
    {
        var card = new QvReviewCard
        {
            Code = r.Code,
            Name = r.Name,
            Industry = r.Industry ?? "",
            Type = r.Type,
            Price = r.Price ?? 0,
            Zone = r.Zone,
            Pool = r.Pool,
            QualityScore = r.QualityScore ?? 0,
            QualityLabel = r.QualityLabel ?? "",
            PSafe = r.PSafe ?? 0,
            PFair = r.PFair ?? 0,
            POpt = r.POpt ?? 0,
            ValuationModel = r.ValuationDetail ?? "",
            ValuationConfidence = r.ValuationConfidence ?? "",
            DataQualityGrade = r.DataQualityGrade ?? "",
            DataConfidence = r.DataConfidenceScore,
            Flags = r.Flags ?? "",
        };

        var det = DetectEarnings(s, cfg, card);
        card.Detection = det;

        var major = DetectMajorRisks(r, s, cfg, card);
        var dataProblem = DetectDataProblem(r, s, det);
        var valInvalid = det.Severity is "moderate" or "high"
                         || (r.Flags ?? "").Contains("valuation_uncertain")
                         || (r.Flags ?? "").Contains("valuation_outlier");
        card.ValuationInvalidityRisk = valInvalid && det.Severity is "moderate" or "high";

        // 分流：重大风险强制 C；数据/估值源问题优先 D；边际恶化 B；否则 A。
        // 业绩下滑本身不等于不合格，也不因周期属性直接升为 C。
        string cls;
        if (major.Count > 0) cls = "C";
        else if (dataProblem && det.Severity is "none" or "") cls = "D";
        else if (dataProblem && det.HasAnySignal) cls = "D"; // 有信号但关键数据仍缺，先走 D 提示补数
        else if (det.HasAnySignal || card.ValuationInvalidityRisk) cls = "B";
        else cls = "A";

        // 若 D 经补数后已能计算且存在恶化信号，仍按 B（补数已在 Evaluate 之前完成）。
        if (cls == "D" && det.HasAnySignal && !det.CoreMissing)
            cls = "B";
        if (major.Count > 0) cls = "C";

        card.ReviewClass = cls;
        card.Triggers.AddRange(det.Triggers);
        card.Triggers.AddRange(major);
        if (card.ValuationInvalidityRisk)
            card.Triggers.Add("估值失效风险：盈利已明显恶化，原有估值未重算，需人工判断是否仍可用");
        if (dataProblem)
            card.Triggers.AddRange(card.DataIssues);

        card.Priority = ScorePriority(r, det, major.Count > 0, dataProblem, card.ValuationInvalidityRisk, cfg);
        card.PriorityLevel = LevelOf(card.Priority, cfg);

        card.IndustryCompare = CompareIndustry(r, s, work, allRows, det, cfg);

        var fp = Fingerprint(r.Code, card.Triggers, det.LatestReportDate, det.LatestForecastKey);
        card.Fingerprint = fp;

        var active = QvReviewHistory.LatestActive(history, r.Code, asOf);
        card.ActiveVerdict = active;
        var requeue = ShouldRequeue(active, det, r, fp, cfg);
        card.DedupSuppressed = active is not null && !requeue;
        card.RequeueReason = requeue ? card.RequeueReason : "";

        card.Queued = cls is "B" or "C" or "D" && !card.DedupSuppressed;
        card.StrongAlertAction = cls switch
        {
            "A" => "auto_candidate",
            "B" => "hold",
            "C" => "freeze",
            "D" => "hold",
            _ => "hold",
        };

        card.SuggestedAction = Suggest(cls, det, major, dataProblem, card.ValuationInvalidityRisk);
        card.HumanQuestions = Questions(det, major, card, s);
        card.Markdown = Render(card, s, asOf);
        return card;
    }

    public static void ApplyToRow(QvRow r, QvReviewCard card)
    {
        r.ReviewClass = card.ReviewClass;
        r.ReviewPriority = card.Priority;
        r.ReviewPriorityLevel = card.PriorityLevel;
        r.ReviewTriggers = string.Join("；", card.Triggers);
        r.ReviewQueued = card.Queued;
        r.ReviewDeduped = card.DedupSuppressed;
        r.ReviewFingerprint = card.Fingerprint;
        r.ReviewCard = card.Markdown;
        r.EarningsSeverity = card.Detection.Severity;
        r.EarningsSummary = card.Detection.Summary;
        r.ValuationInvalidityRisk = card.ValuationInvalidityRisk;
        r.StrongAlertReviewAction = card.StrongAlertAction;
        r.ReviewSuggestedAction = card.SuggestedAction;

        void Flag(string f)
        {
            r.FlagList ??= new List<string>();
            if (!r.FlagList.Contains(f)) r.FlagList.Add(f);
            var cur = r.Flags ?? "";
            if (!cur.Contains(f, StringComparison.Ordinal))
                r.Flags = string.IsNullOrWhiteSpace(cur) ? f : cur + "," + f;
        }
        Flag("review_class_" + card.ReviewClass);
        if (card.Detection.HasAnySignal) Flag("earnings_deterioration");
        if (card.ValuationInvalidityRisk) Flag("valuation_invalidity_risk");
        if (card.StrongAlertAction == "hold") Flag("strong_alert_held");
        if (card.StrongAlertAction == "freeze") Flag("strong_alert_frozen");
    }

    // ───────── 检测 ─────────

    public static EarningsDetection DetectEarnings(QvStock? s, QvConfig cfg, QvReviewCard card)
    {
        var d = new EarningsDetection();
        if (s is null)
        {
            d.CoreMissing = true;
            d.Triggers.Add("缺少标的财务对象，无法计算业绩变化（数据缺失）");
            card.DataIssues.Add("标的财务对象缺失");
            d.Summary = "数据缺失：无财务对象。";
            return d;
        }

        var periods = s.Periods ?? [];
        if (periods.Count == 0)
        {
            d.CoreMissing = true;
            d.Triggers.Add("缺少季度/年度利润表序列，无法计算同比（数据缺失）");
            card.DataIssues.Add("季度利润表序列缺失");
            card.Sources.Add("东财 MAINFINADATA：未取到或未解析出报告期");
            d.Summary = "数据缺失：无可用报告期。";
            return d;
        }

        var singles = DeriveSingles(periods);
        d.Singles = singles;
        d.LatestReportDate = periods
            .Where(p => !string.IsNullOrWhiteSpace(p.ReportDate) && p.IsCumulative)
            .Select(p => p.ReportDate)
            .OrderByDescending(x => x)
            .FirstOrDefault() ?? "";

        var latestCum = periods.Where(p => p.IsCumulative && Looked(p.ReportDate))
            .OrderByDescending(p => p.ReportDate).FirstOrDefault();
        if (latestCum is not null)
        {
            d.LatestPeriodType = latestCum.PeriodType;
            d.LatestRevenue = latestCum.Revenue;
            d.LatestNp = latestCum.Np;
            d.LatestDeduct = latestCum.DeductNp;
            d.LatestGm = latestCum.GrossMargin;
            d.LatestRoe = latestCum.Roe;
            d.LatestOcfToNp = latestCum.OcfToNp ?? Ratio(latestCum.Ocf, latestCum.Np);
            var priorCum = periods.FirstOrDefault(p => p.IsCumulative && p.PeriodType == latestCum.PeriodType
                && YearOf(p.ReportDate) == YearOf(latestCum.ReportDate) - 1);
            if (priorCum is not null)
            {
                d.RevYoyCum = Yoy(latestCum.Revenue, priorCum.Revenue);
                d.NpYoyCum = Yoy(latestCum.Np, priorCum.Np);
                d.DeductYoyCum = Yoy(latestCum.DeductNp, priorCum.DeductNp);
                d.GmChangePpCum = Diff(latestCum.GrossMargin, priorCum.GrossMargin);
                d.RoeChangePpCum = Diff(latestCum.Roe, priorCum.Roe);
                d.ArGrowthCum = Yoy(latestCum.Ar, priorCum.Ar);
                d.InvGrowthCum = Yoy(latestCum.Inventory, priorCum.Inventory);
            }
            else
            {
                card.DataIssues.Add($"缺少上年同期累计（{latestCum.PeriodType} {YearOf(latestCum.ReportDate) - 1}）");
            }
            card.Sources.Add($"东财 MAINFINADATA 累计 {latestCum.ReportDate}（{latestCum.PeriodType}）来源={latestCum.Source}");
        }

        // 单季度：最近一个已拆出的单季 vs 上年同季
        var lastSq = singles.OrderByDescending(p => p.ReportDate).ThenByDescending(p => p.PeriodType).FirstOrDefault();
        if (lastSq is not null)
        {
            var priorSq = singles.FirstOrDefault(p => p.PeriodType == lastSq.PeriodType
                && YearOf(p.ReportDate) == YearOf(lastSq.ReportDate) - 1);
            d.LatestSingleLabel = $"{YearOf(lastSq.ReportDate)}{lastSq.PeriodType}";
            if (priorSq is not null)
            {
                d.RevYoySq = Yoy(lastSq.Revenue, priorSq.Revenue);
                d.NpYoySq = Yoy(lastSq.Np, priorSq.Np);
                d.DeductYoySq = Yoy(lastSq.DeductNp, priorSq.DeductNp);
                d.GmChangePpSq = Diff(lastSq.GrossMargin, priorSq.GrossMargin);
            }
            else
                card.DataIssues.Add($"缺少上年同季单季度（{lastSq.PeriodType}）");
        }

        // TTM：最近四个单季之和 vs 再往前四个
        var ttm = TtmPair(singles, "Np");
        d.NpYoyTtm = ttm.yoy;
        d.RevYoyTtm = TtmPair(singles, "Revenue").yoy;
        d.DeductYoyTtm = TtmPair(singles, "DeductNp").yoy;
        if (ttm.yoy is null && singles.Count < 8)
            card.DataIssues.Add("单季度序列不足 8 期，TTM 同比标记为缺失");

        // 年度：最近年报
        var anns = periods.Where(p => p.IsCumulative && p.PeriodType == "annual" && Looked(p.ReportDate))
            .OrderByDescending(p => p.ReportDate).ToList();
        if (anns.Count >= 2)
        {
            d.RevYoyAnn = Yoy(anns[0].Revenue, anns[1].Revenue);
            d.NpYoyAnn = Yoy(anns[0].Np, anns[1].Np);
            d.DeductYoyAnn = Yoy(anns[0].DeductNp, anns[1].DeductNp);
        }

        // 最近四个单季利润趋势（扣非优先，否则归母）
        var last4 = singles.OrderByDescending(p => SortKey(p)).Take(4).OrderBy(p => SortKey(p)).ToList();
        d.Last4Trend = last4.Select(p =>
        {
            var v = p.DeductNp ?? p.Np;
            return $"{YearOf(p.ReportDate)}{p.PeriodType}={(v is null ? "缺失" : v.Value.ToString("0.##", CultureInfo.InvariantCulture))}";
        }).ToList();
        d.Last4Direction = TrendDir(last4.Select(p => p.DeductNp ?? p.Np).ToList());

        // OCF vs NP：最近累计
        if (latestCum is not null)
        {
            d.OcfToNp = latestCum.OcfToNp ?? Ratio(latestCum.Ocf, latestCum.Np);
            if (d.OcfToNp is null && latestCum.Ocf is null)
                card.DataIssues.Add("经营现金流或 OCF/净利润 缺失");
        }

        // 应收/存货增速相对营收
        if (d.ArGrowthCum is not null && d.RevYoyCum is not null)
            d.ArMinusRev = d.ArGrowthCum - d.RevYoyCum;
        else if (latestCum is not null && latestCum.Ar is null)
            card.DataIssues.Add("应收账款缺失，无法比较增速差");
        if (d.InvGrowthCum is not null && d.RevYoyCum is not null)
            d.InvMinusRev = d.InvGrowthCum - d.RevYoyCum;
        else if (latestCum is not null && latestCum.Inventory is null)
            card.DataIssues.Add("存货缺失，无法比较增速差");

        // 一次性损益：归母与扣非同比缺口
        var gap = AbsDiff(Primary(d.DeductYoyCum, d.DeductYoyTtm, d.DeductYoySq),
                          Primary(d.NpYoyCum, d.NpYoyTtm, d.NpYoySq));
        d.OneOffGap = gap;

        // 预告
        var recentFc = (s.Forecasts ?? []).Where(IsRecentForecast).ToList();
        if (s.ForecastsMissing && recentFc.Count == 0)
            card.DataIssues.Add("业绩预告/快报：未取到（标记缺失，未编造）");
        else if (recentFc.Count > 0)
        {
            d.LatestForecastKey = recentFc[0].Period + "|" + recentFc[0].Type;
            card.Sources.Add("东财近两年业绩预告/快报：" + string.Join("；", recentFc.Take(3).Select(f => $"{f.Period} {f.Type} {f.Summary}")));
        }
        else if (s.Forecasts.Count > 0)
            card.Sources.Add("东财仅有更早的预告/快报（早于近两年，不纳入本次指纹与判断）：" + s.Forecasts[0].Period);

        // 行业/周期语境（仅注释，不升为否决）
        d.CycleContext = rType(s);

        var revTh = cfg.D("review.earnings.revenue_yoy_mild", -0.15);
        var npTh = cfg.D("review.earnings.np_yoy_moderate", -0.30);
        var dedMod = cfg.D("review.earnings.deduct_yoy_moderate", -0.30);
        var dedHigh = cfg.D("review.earnings.deduct_yoy_high", -0.50);
        var gmTh = cfg.D("review.earnings.gross_margin_drop_pp", 0.03);
        var roeTh = cfg.D("review.earnings.roe_drop_pp", 0.02);
        var ocfTh = cfg.D("review.earnings.ocf_np_mismatch_below", 0.30);
        var arTh = cfg.D("review.earnings.ar_minus_rev_growth", 0.20);
        var invTh = cfg.D("review.earnings.inv_minus_rev_growth", 0.20);
        var oneOffTh = cfg.D("review.earnings.one_off_gap", 0.20);

        int sev = 0;
        void Hit(int level, string msg)
        {
            d.Triggers.Add(msg);
            if (level > sev) sev = level;
        }

        // 用「各口径中较严重的可用同比」判断，全部写入触发说明。优先扣非。
        EvaluateYoy("营收", new[] { ("累计", d.RevYoyCum), ("单季度", d.RevYoySq), ("TTM", d.RevYoyTtm), ("年度", d.RevYoyAnn) },
            revTh, 1, Hit, "轻度预警");
        EvaluateYoy("归母净利润", new[] { ("累计", d.NpYoyCum), ("单季度", d.NpYoySq), ("TTM", d.NpYoyTtm), ("年度", d.NpYoyAnn) },
            npTh, 2, Hit, "中度预警");
        EvaluateDeduct(d, dedMod, dedHigh, Hit);
        var gmCh = Primary(d.GmChangePpCum, d.GmChangePpSq, null);
        if ((s.Type ?? "") != "T2" && gmCh is not null && gmCh < -gmTh)
            Hit(1, $"毛利率同比下降 {gmCh.Value * 100:F1} 个百分点（阈值 {gmTh * 100:F0}pp）→ 经营质量预警");
        var roeCh = d.RoeChangePpCum;
        if (roeCh is not null && roeCh < -roeTh)
            Hit(1, $"ROE 同比下降 {roeCh.Value * 100:F1} 个百分点（阈值 {roeTh * 100:F0}pp）→ 盈利能力预警");
        var isBank = (s.Type ?? "") == "T2";
        if (!isBank && d.OcfToNp is not null && d.OcfToNp < ocfTh)
            Hit(1, $"经营现金流/净利润 {d.OcfToNp.Value:P0} 低于 {ocfTh:P0}，匹配偏弱");
        else if (isBank && d.OcfToNp is not null)
            card.Sources.Add("银行不适用经营现金流/净利润匹配作为恶化触发，仅记录：" + d.OcfToNp.Value.ToString("P0"));
        if (!isBank && d.ArMinusRev is not null && d.ArMinusRev > arTh)
            Hit(1, $"应收账款增速比营收增速高 {d.ArMinusRev.Value:P0}");
        if (!isBank && d.InvMinusRev is not null && d.InvMinusRev > invTh)
            Hit(1, $"存货增速比营收增速高 {d.InvMinusRev.Value:P0}");
        if (isBank)
        {
            // 不把银行缺失应收/存货当作待补数据问题
            card.DataIssues.RemoveAll(x => x.Contains("应收") || x.Contains("存货") || x.Contains("毛利率"));
        }
        if (gap is not null && gap >= oneOffTh)
            Hit(Math.Max(sev, 1) == 0 ? 1 : sev, $"归母与扣非同比缺口 {gap.Value:P0}，需区分一次性损益（不以此否决）");
        if (d.Last4Direction == "down" && sev == 0)
            Hit(1, "最近四个单季利润趋势向下（尚未触发同比阈值，列为关注）");

        d.Severity = sev switch { >= 3 => "high", 2 => "moderate", 1 => "mild", _ => "none" };
        d.HasAnySignal = sev > 0;
        d.CoreMissing = latestCum is null || (d.RevYoyCum is null && d.NpYoyCum is null && d.DeductYoyCum is null
                                              && d.RevYoySq is null && d.NpYoySq is null);
        d.Summary = BuildSummary(d);
        return d;

        static string rType(QvStock st) => st.Type ?? "";
    }

    static void EvaluateYoy(string name, (string view, double? yoy)[] xs, double th, int level,
        Action<int, string> hit, string label)
    {
        foreach (var (view, yoy) in xs)
        {
            if (yoy is not null && yoy < th)
                hit(level, $"{name}{view}同比 {yoy.Value:P1}（阈值 {th:P0}）→ {label}");
        }
    }

    static void EvaluateDeduct(EarningsDetection d, double mod, double high, Action<int, string> hit)
    {
        var xs = new (string view, double? yoy)[]
        {
            ("累计", d.DeductYoyCum), ("单季度", d.DeductYoySq), ("TTM", d.DeductYoyTtm), ("年度", d.DeductYoyAnn)
        };
        foreach (var (view, yoy) in xs)
        {
            if (yoy is null) continue;
            if (yoy < high)
                hit(3, $"扣非净利润{view}同比 {yoy.Value:P1}（阈值 {high:P0}）→ 高度预警");
            else if (yoy < mod)
                hit(2, $"扣非净利润{view}同比 {yoy.Value:P1}（阈值 {mod:P0}）→ 中度预警");
        }
    }

    static List<string> DetectMajorRisks(QvRow r, QvStock? s, QvConfig cfg, QvReviewCard card)
    {
        var list = new List<string>();
        var flags = r.Flags ?? "";
        if (flags.Contains("red_flag") && !flags.Contains("red_flag_reviewed"))
            list.Add("未复核红旗（red_flag）");
        if (flags.Contains("cycle_top_risk"))
            list.Add("周期顶部风险（cycle_top_risk）");
        if (s?.NoticesMissing == true)
            card.DataIssues.Add("重大公告：未取到（标记缺失，未编造）");
        else if (s is not null)
        {
            var hard = HardAnnouncementKeys();
            var soft = SoftAnnouncementKeys();
            // 重新判定：Backfill 里用的是合并关键词；此处严格区分强制 C 与仅记录。
            var hardHits = s.Notices.Where(n => hard.Any(k => n.Title.Contains(k, StringComparison.Ordinal))).ToList();
            var softHits = s.Notices.Where(n => !hardHits.Contains(n) && soft.Any(k => n.Title.Contains(k, StringComparison.Ordinal))).ToList();
            foreach (var n in hardHits.Take(5))
                list.Add($"重大公告 {n.Date}：{n.Title}");
            foreach (var n in softHits.Take(3))
                card.Sources.Add($"关注类公告（不自动升为 C）：{n.Date} {n.Title}");
            if (s.Notices.Count > 0)
                card.Sources.Add($"东财公告 {s.Notices.Count} 条（近180天），其中强制重大 {hardHits.Count} 条、关注类 {softHits.Count} 条");
        }
        return list;
    }

    static bool DetectDataProblem(QvRow r, QvStock? s, EarningsDetection det)
    {
        // 数据质量档 D/E 只进入优先级，不单独定为 D 类；D 类仅表示关键数据算不出或明确不足。
        if ((r.Flags ?? "").Contains("data_insufficient")) return true;
        if (det.CoreMissing) return true;
        if (s is null || (s.Periods?.Count ?? 0) == 0) return true;
        return false;
    }

    static int ScorePriority(QvRow r, EarningsDetection det, bool major, bool dataProblem, bool valInvalid, QvConfig cfg)
    {
        var eMax = (int)cfg.D("review.priority.earnings_max", 30);
        var rMax = (int)cfg.D("review.priority.risk_event_max", 25);
        var vMax = (int)cfg.D("review.priority.valuation_max", 20);
        var dMax = (int)cfg.D("review.priority.data_quality_max", 15);
        var pMax = (int)cfg.D("review.priority.price_urgency_max", 10);

        int earn = det.Severity switch { "high" => eMax, "moderate" => 20, "mild" => 10, _ => 0 };
        int risk = major ? rMax : 0;

        int val = 0;
        var conf = r.ValuationConfidence ?? "";
        var flags = r.Flags ?? "";
        if (conf.Contains("很低") || flags.Contains("valuation_outlier")) val = vMax;
        else if (conf.Contains("低")) val = 15;
        else if (conf.Contains("中")) val = 8;
        if (valInvalid) val = Math.Max(val, 15);

        int dq = 0;
        var g = r.DataQualityGrade ?? "";
        if (g is "E" or "D" || det.CoreMissing) dq = dMax;
        else if (g == "C" || dataProblem) dq = 8;

        int urg = 0;
        if (r.Zone == "strike" && r.PSafe is > 0)
        {
            var deep = (r.Price ?? 0) <= r.PSafe.Value * (1 - cfg.D("review.deeper_mos_band", 0.10));
            urg = deep ? pMax : 5;
        }
        return Math.Min(100, earn + risk + val + dq + urg);
    }

    static string LevelOf(int score, QvConfig cfg)
    {
        var p0 = (int)cfg.D("review.priority.p0_min", 80);
        var p1 = (int)cfg.D("review.priority.p1_min", 60);
        var p2 = (int)cfg.D("review.priority.p2_min", 40);
        if (score >= p0) return "P0";
        if (score >= p1) return "P1";
        if (score >= p2) return "P2";
        return "P3";
    }

    static string CompareIndustry(QvRow r, QvStock? s, IReadOnlyList<QvStock> work, IReadOnlyList<QvRow> rows, EarningsDetection det, QvConfig cfg)
    {
        var ind = r.Industry ?? "";
        if (string.IsNullOrWhiteSpace(ind)) return "行业字段缺失，无法做同行业对比。";
        var peerRows = rows.Where(x => x.Code != r.Code && (x.Industry ?? "") == ind).ToList();
        var workMap = work.ToDictionary(x => x.Code, x => x);
        var peers = peerRows.Select(x => workMap.TryGetValue(x.Code, out var st) ? st : null)
            .Where(st => st is not null && (st!.Periods?.Count ?? 0) > 0).Cast<QvStock>().ToList();
        if (peers.Count < 2)
            return $"同行业（{ind}）在本轮工作集内可比样本不足（有行业 {peerRows.Count} 只、有财报序列 {peers.Count} 只），不编造对比结论。";

        var revYoys = new List<double>();
        var npYoys = new List<double>();
        foreach (var p in peers)
        {
            var tmp = new QvReviewCard();
            var ed = DetectEarnings(p, cfg, tmp);
            if (ed.RevYoyCum is not null) revYoys.Add(ed.RevYoyCum.Value);
            if (ed.NpYoyCum is not null) npYoys.Add(ed.NpYoyCum.Value);
        }
        var sb = new StringBuilder();
        sb.Append($"同行业 {ind} 工作集内样本 {peers.Count} 只。");
        if (revYoys.Count > 0)
            sb.Append($" 营收累计同比中位数 {Median(revYoys):P1}（本公司 {(det.RevYoyCum is null ? "缺失" : det.RevYoyCum.Value.ToString("P1"))}）。");
        else sb.Append(" 同行营收同比数据不足。");
        if (npYoys.Count > 0)
            sb.Append($" 归母累计同比中位数 {Median(npYoys):P1}（本公司 {(det.NpYoyCum is null ? "缺失" : det.NpYoyCum.Value.ToString("P1"))}）。");
        else sb.Append(" 同行归母同比数据不足。");
        sb.Append(" 此对比仅作参照，不单独改变分流。");
        return sb.ToString();
    }

    static bool ShouldRequeue(QvReviewRecord? active, EarningsDetection det, QvRow r, string fp, QvConfig cfg)
    {
        if (active is null) return false;
        if (CoreFingerprintEqual(active.Fingerprint, fp))
        {
            // 同一报告期、同一风险原因：不重复入队
        }
        else
        {
            return true;
        }
        // 价格进入更深安全边际：必须比记录结论时更低一档，普通已处于深区间或日常波动不触发。
        if (r.Zone == "strike" && r.PSafe is > 0
            && double.TryParse(active.PriceAtReview, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var oldPx)
            && double.TryParse(active.PSafeAtReview, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var oldSafe)
            && oldSafe > 0)
        {
            var band = cfg.D("review.deeper_mos_band", 0.10);
            var oldMos = (oldSafe - oldPx) / oldSafe;
            var newMos = (r.PSafe.Value - (r.Price ?? 0)) / r.PSafe.Value;
            if (newMos >= oldMos + band) return true;
        }
        return false;
    }

    public static string Fingerprint(string code, IEnumerable<string> triggers, string reportDate, string forecastKey)
    {
        var keys = triggers
            .Select(NormalizeKey)
            .Where(x => x.Length > 0)
            .Distinct()
            .OrderBy(x => x);
        // 用 + 连接，避免 CSV 逗号干扰；预告键仅作新证据，不并入主指纹。
        return code.PadLeft(6, '0') + "|" + reportDate + "|" + string.Join("+", keys);
    }

    static bool CoreFingerprintEqual(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (a == b) return true;
        // 兼容旧逗号指纹，只比较 代码|报告期|键集合
        static (string code, string date, string keys) Split(string s)
        {
            var p = s.Split('|');
            if (p.Length < 2) return (s, "", "");
            var keys = p.Length >= 3 ? p[^1].Replace(",", "+") : "";
            // 旧格式 code|date|forecast|keys 时取最后一段
            return (p[0], p[1], keys);
        }
        var x = Split(a); var y = Split(b);
        if (x.code != y.code || x.date != y.date) return false;
        var kx = x.keys.Split('+', StringSplitOptions.RemoveEmptyEntries).OrderBy(z => z);
        var ky = y.keys.Split('+', StringSplitOptions.RemoveEmptyEntries).OrderBy(z => z);
        return kx.SequenceEqual(ky);
    }

    static string NormalizeKey(string t)
    {
        // 去掉具体数字，保留原因类别，避免普通波动或四舍五入造成重复推送
        if (t.Contains("扣非") && t.Contains("高度")) return "deduct_high";
        if (t.Contains("扣非") && t.Contains("中度")) return "deduct_mod";
        if (t.Contains("归母")) return "np_mod";
        if (t.Contains("营收")) return "rev_mild";
        if (t.Contains("毛利率")) return "gm";
        if (t.Contains("ROE")) return "roe";
        if (t.Contains("现金流")) return "ocf";
        if (t.Contains("应收")) return "ar";
        if (t.Contains("存货")) return "inv";
        if (t.Contains("一次性")) return "one_off";
        if (t.Contains("四")) return "q_trend";
        if (t.Contains("红旗")) return "red_flag";
        if (t.Contains("周期顶部")) return "cycle_top";
        if (t.Contains("公告")) return "major_ann";
        if (t.Contains("估值失效")) return "val_invalid";
        if (t.Contains("缺失") || t.Contains("数据")) return "data_missing";
        return t.Length > 24 ? t[..24] : t;
    }

    static string Suggest(string cls, EarningsDetection det, List<string> major, bool dataProblem, bool valInvalid)
    {
        return cls switch
        {
            "A" => "基本面未检出恶化信号，若同时满足原有全部强提醒条件，可作为强提醒候选。不改动评分与估值。",
            "B" => "暂缓强提醒，进入人工复核队列。请判断恶化是季节性/周期性/一次性还是结构性，以及原估值是否仍可用。不得因短期下滑永久剔除。",
            "C" => "冻结强提醒，等待重大风险核实后再决定是否恢复。即使优先级不高也不得自动放行。",
            "D" => "数据或估值输入存在问题。已尝试补数；仍无法解决则进入人工队列，补齐来源后再跑复核。不要为了数量改评分或估值。",
            _ => "待人工查看。",
        };
    }

    static List<string> Questions(EarningsDetection det, List<string> major, QvReviewCard card, QvStock? s)
    {
        var q = new List<string>();
        if (det.HasAnySignal)
        {
            q.Add("近期盈利下滑是季节性、行业周期，还是份额/产品结构等持续性变化？请对照单季度与累计、扣非与归母。");
            if (det.OneOffGap is not null)
                q.Add("归母与扣非同比存在缺口，一次性损益项目是什么、是否已披露完毕？");
            if (det.Last4Direction == "down")
                q.Add("最近四个单季利润趋势向下，是否已在最新预告或管理层说明中给出拐点证据？");
        }
        if (card.ValuationInvalidityRisk)
            q.Add("在盈利已变化的前提下，现有估值模型与三档价格是否仍有效？若失效，应等待新财报后再估值，而不是在本模块改写价格。");
        if (major.Count > 0)
            q.Add("所列重大风险事件/红旗是否属实、进展如何、是否已足以解除或需要维持冻结？");
        if (card.DataIssues.Count > 0)
            q.Add("下列数据标记为缺失：" + string.Join("；", card.DataIssues) + "。请从年报/季报原件核对，不要用估计数填入评分。");
        if (s?.Type == "T5" || (card.Flags ?? "").Contains("cycle"))
            q.Add("标的可能具有周期属性：当前下滑是否处于可接受的周期位置？周期本身不构成自动否决。");
        if (q.Count == 0)
            q.Add("未检出强制人工问题。若仍要复核，请确认数据源报告期与公告是否有系统未覆盖的信息。");
        return q;
    }

    static string Render(QvReviewCard c, QvStock? s, DateTime asOf)
    {
        var d = c.Detection;
        var sb = new StringBuilder();
        sb.AppendLine($"# 人工复核卡片  {c.Name} ({c.Code})");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间（Asia/Shanghai）：{asOf:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 行业：{Nz(c.Industry)}　分型：{Nz(c.Type)}　池：{Nz(c.Pool)}　价格区：{Nz(c.Zone)}");
        sb.AppendLine($"- 现价：{F(c.Price)}　安全边际价：{F(c.PSafe)}　合理价：{F(c.PFair)}　乐观价：{F(c.POpt)}");
        sb.AppendLine($"- 基本面综合评分：{c.QualityScore:F1}（{Nz(c.QualityLabel)}）。本模块不修改该评分。");
        sb.AppendLine($"- 分流：{c.ReviewClass}　优先级：{c.Priority} / {c.PriorityLevel}　强提醒动作：{c.StrongAlertAction}");
        sb.AppendLine($"- 估值模型/说明：{Nz(c.ValuationModel)}");
        sb.AppendLine($"- 估值可信度：{Nz(c.ValuationConfidence)}　数据质量：{Nz(c.DataQualityGrade)}　数据可信分：{(c.DataConfidence is null ? "缺失" : c.DataConfidence.Value.ToString("F0"))}");
        sb.AppendLine($"- 估值失效风险标记：{(c.ValuationInvalidityRisk ? "是（原估值未重算）" : "否")}");
        if (c.DedupSuppressed)
            sb.AppendLine($"- 去重：有效期内已有人工结论（{c.ActiveVerdict?.Verdict} / {c.ActiveVerdict?.Id}），本次不重复推送。");
        sb.AppendLine();
        sb.AppendLine("## 最近一期财报（累计）");
        sb.AppendLine($"- 报告期：{Nz(d.LatestReportDate)}（{Nz(d.LatestPeriodType)}）");
        sb.AppendLine($"- 营收：{F(d.LatestRevenue)}　归母净利润：{F(d.LatestNp)}　扣非净利润：{F(d.LatestDeduct)}");
        sb.AppendLine($"- 毛利率：{PctOrMissing(d.LatestGm)}　ROE：{PctOrMissing(d.LatestRoe)}　OCF/净利润：{PctOrMissing(d.OcfToNp)}");
        sb.AppendLine();
        sb.AppendLine("## 同比变化（区分口径；缺失已标明）");
        sb.AppendLine("| 指标 | 累计同比 | 单季度同比 | TTM同比 | 年度同比 |");
        sb.AppendLine("|---|---|---|---|---|");
        sb.AppendLine($"| 营收 | {P(d.RevYoyCum)} | {P(d.RevYoySq)} | {P(d.RevYoyTtm)} | {P(d.RevYoyAnn)} |");
        sb.AppendLine($"| 归母净利润 | {P(d.NpYoyCum)} | {P(d.NpYoySq)} | {P(d.NpYoyTtm)} | {P(d.NpYoyAnn)} |");
        sb.AppendLine($"| 扣非净利润 | {P(d.DeductYoyCum)} | {P(d.DeductYoySq)} | {P(d.DeductYoyTtm)} | {P(d.DeductYoyAnn)} |");
        sb.AppendLine($"- 毛利率变动：累计 {Pp(d.GmChangePpCum)}；单季度 {Pp(d.GmChangePpSq)}");
        sb.AppendLine($"- ROE 变动：累计 {Pp(d.RoeChangePpCum)}");
        sb.AppendLine($"- 应收增速-营收增速：{P(d.ArMinusRev)}　存货增速-营收增速：{P(d.InvMinusRev)}");
        sb.AppendLine($"- 最近四个单季利润（扣非优先）：{(d.Last4Trend.Count == 0 ? "缺失" : string.Join(" → ", d.Last4Trend))}；方向：{Nz(d.Last4Direction)}");
        sb.AppendLine($"- 业绩摘要：{Nz(d.Summary)}");
        if (!string.IsNullOrWhiteSpace(d.CycleContext))
            sb.AppendLine($"- 分型语境：{d.CycleContext}（周期属性不自动升为 C 类）");
        sb.AppendLine();
        sb.AppendLine("## 触发复核的具体原因");
        if (c.Triggers.Count == 0) sb.AppendLine("- （无）");
        else foreach (var t in c.Triggers) sb.AppendLine("- " + t);
        sb.AppendLine();
        sb.AppendLine("## 同行业业绩对比");
        sb.AppendLine(Nz(c.IndustryCompare));
        sb.AppendLine();
        sb.AppendLine("## 原始数据来源与报告日期");
        if (c.Sources.Count == 0) sb.AppendLine("- 见上方报告期；若未列出则该源本轮未取到。");
        else foreach (var src in c.Sources) sb.AppendLine("- " + src);
        if (s?.ForecastsMissing == true) sb.AppendLine("- 业绩预告/快报：缺失");
        if (s?.NoticesMissing == true) sb.AppendLine("- 公告：缺失");
        if (c.DataIssues.Count > 0)
        {
            sb.AppendLine("- 明确标记的缺失项：");
            foreach (var m in c.DataIssues) sb.AppendLine("  - " + m);
        }
        sb.AppendLine();
        sb.AppendLine("## 系统建议的下一步");
        sb.AppendLine(c.SuggestedAction);
        sb.AppendLine();
        sb.AppendLine("## 人工需要判断的问题");
        foreach (var q in c.HumanQuestions) sb.AppendLine("- " + q);
        sb.AppendLine();
        sb.AppendLine("本卡片只基于已取到的公开数据。没有数据依据的结论不会写出。评分与三档估值保持扫描时原值。");
        return sb.ToString();
    }

    static string BuildSummary(EarningsDetection d)
    {
        var bits = new List<string>();
        bits.Add($"最近报告期 {Nz(d.LatestReportDate)}（{Nz(d.LatestPeriodType)}）");
        void one(string name, double? cum, double? sq, double? ttm, double? ann)
        {
            var parts = new List<string>();
            if (cum is not null) parts.Add("累计 " + cum.Value.ToString("P1"));
            if (sq is not null) parts.Add("单季 " + sq.Value.ToString("P1"));
            if (ttm is not null) parts.Add("TTM " + ttm.Value.ToString("P1"));
            if (ann is not null) parts.Add("年度 " + ann.Value.ToString("P1"));
            bits.Add(name + "同比：" + (parts.Count == 0 ? "缺失" : string.Join("，", parts)));
        }
        one("营收", d.RevYoyCum, d.RevYoySq, d.RevYoyTtm, d.RevYoyAnn);
        one("归母", d.NpYoyCum, d.NpYoySq, d.NpYoyTtm, d.NpYoyAnn);
        one("扣非", d.DeductYoyCum, d.DeductYoySq, d.DeductYoyTtm, d.DeductYoyAnn);
        bits.Add("恶化程度：" + d.Severity);
        return string.Join("；", bits) + "。";
    }

    // ───────── 单季度拆分 ─────────

    public static List<QvPeriod> DeriveSingles(IEnumerable<QvPeriod> periods)
    {
        var cum = periods.Where(p => p.IsCumulative && Looked(p.ReportDate)).ToList();
        var list = new List<QvPeriod>();
        var years = cum.Select(p => YearOf(p.ReportDate)).Where(y => y > 0).Distinct();
        foreach (var y in years)
        {
            QvPeriod? P(string typ, string mmdd) =>
                cum.FirstOrDefault(p => p.PeriodType == typ && p.ReportDate.StartsWith($"{y}-{mmdd[..2]}", StringComparison.Ordinal))
                ?? cum.FirstOrDefault(p => p.ReportDate == $"{y}-{mmdd}");

            var q1 = P("q1", "03-31");
            var h1 = P("h1", "06-30");
            var q3 = P("q3", "09-30");
            var ann = P("annual", "12-31");

            void add(string typ, string date, QvPeriod cur, QvPeriod? prev)
            {
                list.Add(new QvPeriod
                {
                    ReportDate = date,
                    PeriodType = typ,
                    IsCumulative = false,
                    Source = "由累计报告期差分（东财 MAINFINADATA）",
                    Revenue = Sub(cur.Revenue, prev?.Revenue),
                    Np = Sub(cur.Np, prev?.Np),
                    DeductNp = Sub(cur.DeductNp, prev?.DeductNp),
                    Ocf = Sub(cur.Ocf, prev?.Ocf),
                    Ar = cur.Ar,
                    Inventory = cur.Inventory,
                    GrossMargin = typ == "single_q1" ? cur.GrossMargin : null,
                    Roe = typ == "single_q1" ? cur.Roe : null,
                    OcfToNp = Ratio(Sub(cur.Ocf, prev?.Ocf), Sub(cur.Np, prev?.Np)),
                });
            }
            if (q1 is not null) add("single_q1", q1.ReportDate, q1, null);
            if (h1 is not null && q1 is not null) add("single_q2", h1.ReportDate, h1, q1);
            else if (h1 is not null && q1 is null)
            {
                // 无法拆 H1，不编造单季
            }
            if (q3 is not null && h1 is not null) add("single_q3", q3.ReportDate, q3, h1);
            if (ann is not null && q3 is not null) add("single_q4", ann.ReportDate, ann, q3);
        }
        return list;
    }

    static (double? cur, double? prev, double? yoy) TtmPair(List<QvPeriod> singles, string field)
    {
        double? Val(QvPeriod p) => field switch
        {
            "Revenue" => p.Revenue,
            "DeductNp" => p.DeductNp,
            _ => p.Np,
        };
        var ordered = singles.Where(p => Val(p) is not null).OrderBy(p => SortKey(p)).ToList();
        if (ordered.Count < 4) return (null, null, null);
        var last4 = ordered.TakeLast(4).ToList();
        var cur = last4.Sum(p => Val(p) ?? 0);
        if (ordered.Count < 8) return (cur, null, null);
        var prev4 = ordered.Skip(ordered.Count - 8).Take(4).ToList();
        var prev = prev4.Sum(p => Val(p) ?? 0);
        return (cur, prev, Yoy(cur, prev));
    }

    static List<string> MajorKeys(QvConfig cfg)
    {
        // Backfill 打标用较宽集合；分流是否强制 C 由 HardAnnouncementKeys 决定。
        return HardAnnouncementKeys().Concat(SoftAnnouncementKeys()).Distinct().ToList();
    }

    static List<string> HardAnnouncementKeys() =>
        ["立案", "处罚决定", "监管处罚", "退市", "暂停上市", "破产", "重整", "被实施", "失信", "违规立案"];

    static List<string> SoftAnnouncementKeys() =>
        ["问询函", "监管关注", "警示", "处罚"];

    public static List<(string Category, string Title, string Body)> BuildWouldPush(
        IReadOnlyList<QvRow> rows, QvConfig cfg, IReadOnlyList<string>? existingAlerts = null)
    {
        var list = new List<(string, string, string)>();
        var max = (int)cfg.D("review.daily_review_push_max", 10);
        var tasks = rows.Where(r => r.ReviewQueued && r.ReviewPriorityLevel is "P0" or "P1")
            .OrderByDescending(r => r.ReviewPriority)
            .ThenBy(r => r.Code)
            .Take(max)
            .ToList();
        foreach (var r in tasks)
        {
            var body = $"【人工复核任务 {r.ReviewPriorityLevel} {r.ReviewPriority}分】{r.Name}({r.Code}) 分流{r.ReviewClass}\n"
                       + $"现价 {r.Price:F2} 区={r.Zone} 评分 {r.QualityScore:F1} 强提醒={r.StrongAlertReviewAction}\n"
                       + $"{r.EarningsSummary}\n触发：{r.ReviewTriggers}\n建议：{r.ReviewSuggestedAction}";
            list.Add(("manual_review_task", $"人工复核 {r.ReviewPriorityLevel} {r.Name}", body));
        }

        // 强击球提醒（仅 A 类且未被 hold/freeze；具体是否发出仍由 QvNotify 原条件决定）
        foreach (var r in rows.Where(x => x.Zone == "strike" && x.ReviewClass == "A" && x.StrongAlertReviewAction == "auto_candidate"))
            list.Add(("strong_strike_alert", $"强击球候选 {r.Name}", $"{r.Name}({r.Code}) 复核分流 A，可作为强提醒候选（仍须满足原有强提醒条件）。"));

        // 风险事件
        foreach (var r in rows.Where(x => x.ReviewClass == "C"))
            list.Add(("risk_event_alert", $"风险事件 {r.Name}", $"{r.Name}({r.Code}) 分流 C，强提醒已冻结。{r.ReviewTriggers}"));

        return list;
    }

    // ───────── 工具 ─────────

    static bool IsRecentForecast(QvForecastItem f)
    {
        var cutoff = DateTime.Now.Date.AddYears(-2);
        return (DateTime.TryParse(f.Period, out var dt) && dt.Date >= cutoff);
    }

    static bool Looked(string? d) => !string.IsNullOrWhiteSpace(d) && d!.Length >= 10;
    static int YearOf(string d) => d.Length >= 4 && int.TryParse(d[..4], out var y) ? y : 0;
    static string SortKey(QvPeriod p) => p.ReportDate + "|" + p.PeriodType;
    static double? Yoy(double? cur, double? prev)
    {
        if (cur is null || prev is null || prev == 0) return null;
        return (cur.Value - prev.Value) / Math.Abs(prev.Value);
    }
    static double? Diff(double? a, double? b) => a is null || b is null ? null : a - b;
    static double? AbsDiff(double? a, double? b) => a is null || b is null ? null : Math.Abs(a.Value - b.Value);
    static double? Sub(double? a, double? b)
    {
        if (a is null) return null;
        if (b is null) return a;
        return a - b;
    }
    static double? Ratio(double? a, double? b)
    {
        if (a is null || b is null || b == 0) return null;
        return a / b;
    }
    static double? Primary(double? a, double? b, double? c) => a ?? b ?? c;
    static string TrendDir(List<double?> xs)
    {
        var v = xs.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (v.Count < 3) return "不足";
        var down = 0;
        var up = 0;
        for (int i = 1; i < v.Count; i++)
        {
            if (v[i] < v[i - 1]) down++;
            else if (v[i] > v[i - 1]) up++;
        }
        if (down >= v.Count - 1) return "down";
        if (up >= v.Count - 1) return "up";
        return "mixed";
    }
    static double Median(List<double> xs)
    {
        var a = xs.OrderBy(x => x).ToList();
        var n = a.Count;
        return n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
    }
    static string Nz(string? s) => string.IsNullOrWhiteSpace(s) ? "缺失" : s!;
    static string F(double? x) => x is null || (x == 0 && false) ? "缺失" : x.Value.ToString("N2", CultureInfo.InvariantCulture);
    static string P(double? x) => x is null ? "缺失" : x.Value.ToString("P1", CultureInfo.InvariantCulture);
    static string Pp(double? x) => x is null ? "缺失" : (x.Value * 100).ToString("F1", CultureInfo.InvariantCulture) + "pp";
    static string PctOrMissing(double? x)
    {
        if (x is null) return "缺失";
        // ROE/毛利率可能已是小数（0.25）或百分数（25）；与评分模块一致时为小数。
        return x.Value.ToString("P1", CultureInfo.InvariantCulture);
    }
}

public sealed class QvReviewCard
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string Type { get; set; } = "";
    public double Price { get; set; }
    public string Zone { get; set; } = "";
    public string Pool { get; set; } = "";
    public double QualityScore { get; set; }
    public string QualityLabel { get; set; } = "";
    public double PSafe { get; set; }
    public double PFair { get; set; }
    public double POpt { get; set; }
    public string ValuationModel { get; set; } = "";
    public string ValuationConfidence { get; set; } = "";
    public string DataQualityGrade { get; set; } = "";
    public double? DataConfidence { get; set; }
    public string Flags { get; set; } = "";
    public string ReviewClass { get; set; } = "";
    public int Priority { get; set; }
    public string PriorityLevel { get; set; } = "";
    public bool Queued { get; set; }
    public bool DedupSuppressed { get; set; }
    public string Fingerprint { get; set; } = "";
    public string StrongAlertAction { get; set; } = "";
    public bool ValuationInvalidityRisk { get; set; }
    public string SuggestedAction { get; set; } = "";
    public string IndustryCompare { get; set; } = "";
    public string RequeueReason { get; set; } = "";
    public string Markdown { get; set; } = "";
    public EarningsDetection Detection { get; set; } = new();
    public List<string> Triggers { get; set; } = new();
    public List<string> DataIssues { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public List<string> HumanQuestions { get; set; } = new();
    public QvReviewRecord? ActiveVerdict { get; set; }
}

public sealed class EarningsDetection
{
    public string Severity { get; set; } = "none";
    public bool HasAnySignal { get; set; }
    public bool CoreMissing { get; set; }
    public string Summary { get; set; } = "";
    public string LatestReportDate { get; set; } = "";
    public string LatestPeriodType { get; set; } = "";
    public string LatestSingleLabel { get; set; } = "";
    public string LatestForecastKey { get; set; } = "";
    public string CycleContext { get; set; } = "";
    public List<string> Triggers { get; set; } = new();
    public List<QvPeriod> Singles { get; set; } = new();
    public List<string> Last4Trend { get; set; } = new();
    public string Last4Direction { get; set; } = "";
    public double? LatestRevenue { get; set; }
    public double? LatestNp { get; set; }
    public double? LatestDeduct { get; set; }
    public double? LatestGm { get; set; }
    public double? LatestRoe { get; set; }
    public double? LatestOcfToNp { get; set; }
    public double? RevYoyCum { get; set; }
    public double? NpYoyCum { get; set; }
    public double? DeductYoyCum { get; set; }
    public double? RevYoySq { get; set; }
    public double? NpYoySq { get; set; }
    public double? DeductYoySq { get; set; }
    public double? RevYoyTtm { get; set; }
    public double? NpYoyTtm { get; set; }
    public double? DeductYoyTtm { get; set; }
    public double? RevYoyAnn { get; set; }
    public double? NpYoyAnn { get; set; }
    public double? DeductYoyAnn { get; set; }
    public double? GmChangePpCum { get; set; }
    public double? GmChangePpSq { get; set; }
    public double? RoeChangePpCum { get; set; }
    public double? OcfToNp { get; set; }
    public double? ArGrowthCum { get; set; }
    public double? InvGrowthCum { get; set; }
    public double? ArMinusRev { get; set; }
    public double? InvMinusRev { get; set; }
    public double? OneOffGap { get; set; }
}
