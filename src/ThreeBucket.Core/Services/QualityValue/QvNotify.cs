using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// 提醒：进入 strike 且无 valuation_uncertain、无未复核 red_flag → 强提醒；
/// 进入 approach → 关注提醒；optimistic 只对 holdings.csv 里的股票提醒减仓。
/// 同一股票同一状态 5 个交易日内不重复。走应用已有 DataStore.RecordAlerts + 飞书/系统通知通道。
/// 不打印 webhook 或密钥。
/// </summary>
public static class QvNotify
{
    public static async Task<List<string>> SendAsync(QvOutput o, QvData data, QvConfig cfg,
        AppConfig? appCfg, DataStore? store, Action<string> log, CancellationToken ct)
    {
        var messages = new List<string>();
        var entries = new List<AlertEntry>();
        var dedupeDays = cfg.I("global.alert_dedupe_trading_days", 5);
        var seen = LoadDedupe(data.OutDir);

        void Consider(QvRow r, string kind, string action, string pri)
        {
            // 保留原成交额执行限制：不发低流动/缺行情的价格交易提醒；复核/风险独立队列不受影响。
            if (r.PriceMonitoringEligible == false && kind is "strike" or "approach" or "fair" or "optimistic") return;
            if (kind == "strike")
            {
                if (r.Pool != "qualified") return;
                if (r.StrongAlertReviewAction is "hold" or "freeze") return;
                if (r.ReviewClass is "B" or "C" or "D") return;
                if (r.StrongAlertBlocked) return;
                if (r.FlagList.Contains("industry_relative_unreviewed")) return;
                if (r.FlagList.Contains("valuation_uncertain")) return;
                if (r.FlagList.Contains("valuation_outlier")) return;
                if (r.RedFlagUnreviewed) return;
                if (r.FlagList.Contains("data_insufficient")) return;
                if (r.FlagList.Contains("classification_pending")) return;
                if (r.FlagList.Contains("capital_requirement_unknown")) return;
                if (r.FlagList.Contains("data_quality_warning")) return;
                if (r.FlagList.Contains("data_quality_e")) return;
                if (r.DataQualityGrade is "D" or "E") return;
                if (r.DataConfidenceScore is < 60) return;
                if (r.FlagList.Contains("cycle_top_risk")) return;
                if (cfg.B("monitoring.cycle_high_blocks_strong_alert", true) && r.FlagList.Contains("cycle_high")) return;
                if (r.FlagList.Contains("dividend_missing") && (r.Type == "T4" || (r.Methods?.Any(m => m.Name.Contains("股息") && m.Role == "pricing") ?? false))) return;
            }
            if (kind == "optimistic" && !data.Holdings.Contains(r.Code)) return;
            var key = r.Code + "|" + kind;
            if (Recently(seen, key, o.AsOf, dedupeDays)) return;
            seen[key] = o.AsOf;
            var msg = $"{r.Code} {r.Name} · {Label(kind)}  现价 {r.Price:F2} / 安全 {r.PSafe:F2} / 合理 {r.PFair:F2} / 乐观 {r.POpt:F2}  评分 {r.QualityScore:F0}  {r.Flags}";
            messages.Add(msg);
            entries.Add(new AlertEntry
            {
                Code = r.Code, Name = r.Name, StrategyId = "S29", StrategyName = "优质股击球区",
                Action = action + " " + msg, Priority = pri, Time = TradingCalendar.NowCn().ToString("yyyy-MM-dd HH:mm"),
            });
        }

        foreach (var r in o.Rows.Where(x => x.Pool == "qualified" && x.Zone == "strike"))
            Consider(r, "strike", "强提醒：进入击球区，可考虑分批建仓（仅建议，不自动交易）。", "P0");
        foreach (var r in o.Rows.Where(x => x.Pool == "qualified" && x.Zone == "approach"))
            Consider(r, "approach", "关注：接近击球区。", "P1");
        foreach (var r in o.Rows.Where(x => x.Pool == "qualified" && x.Zone == "optimistic"))
            Consider(r, "optimistic", "持仓提醒：价格已过乐观价，考虑减仓。", "P1");
        foreach (var r in o.Rows.Where(x => x.FlagList.Contains("downgrade_warning")))
            Consider(r, "downgrade", "降级预警：需人工复核基本面。", "P0");

        var reviewMsgs = QvReview.BuildWouldPush(o.Rows, cfg);
        var dry = cfg.B("review.feishu_dry_run", true);
        var reviewEntries = new List<AlertEntry>();
        foreach (var w in reviewMsgs.Where(x => x.Category is "manual_review_task" or "risk_event_alert"))
        {
            messages.Add($"[{w.Category}] {w.Title} :: {w.Body.Replace("\n", " / ")}");
            reviewEntries.Add(new AlertEntry
            {
                Code = "", Name = w.Title, StrategyId = "S29", StrategyName = "优质股击球区-复核",
                Action = w.Body, Priority = w.Category == "risk_event_alert" ? "P0" : "P1",
                Time = TradingCalendar.NowCn().ToString("yyyy-MM-dd HH:mm"),
            });
        }
        if (dry)
            log($"[S29] 复核/风险类别飞书 dry-run（review.feishu_dry_run=true），本应独立推送 {reviewEntries.Count} 条，未发送。");

        SaveDedupe(data.OutDir, seen);
        if (entries.Count == 0 && (dry || reviewEntries.Count == 0)) return messages;

        List<AlertEntry> fresh = entries;
        if (store is not null)
        {
            try { fresh = store.RecordAlerts(entries); }
            catch (Exception ex) { log("[S29] 记录提醒失败：" + ex.Message); }
        }
        if (fresh.Count == 0 || appCfg is null) return messages;

        try
        {
            if (appCfg.NotifyLarkEnabled && LarkNotifier.IsValidWebhook(appCfg.LarkWebhook))
            {
                var text = LarkNotifier.BuildAlertMessage(fresh);
                var (ok, _) = await LarkNotifier.SendAsync(appCfg.LarkWebhook, text, appCfg.LarkSecret).ConfigureAwait(false);
                log(ok ? $"[S29] 已通过飞书推送 {fresh.Count} 条提醒" : "[S29] 飞书推送失败（细节已省略）");
            }
            if (appCfg.NotifySystemEnabled)
            {
                var digest = string.Join("；", fresh.Take(3).Select(a => a.Code + a.Name));
                WindowsToastNotifier.Show($"💎 优质股击球区 · {fresh.Count} 条提醒", digest);
            }
        }
        catch (Exception ex) { log("[S29] 外发提醒异常：" + ex.Message); }

        if (!dry && reviewEntries.Count > 0 && appCfg is not null
            && appCfg.NotifyLarkEnabled && LarkNotifier.IsValidWebhook(appCfg.LarkWebhook))
        {
            try
            {
                var text = "【人工复核/风险事件（独立推送）】\n" + string.Join("\n---\n", reviewEntries.Select(e => e.Action));
                var (ok, _) = await LarkNotifier.SendAsync(appCfg.LarkWebhook, text, appCfg.LarkSecret).ConfigureAwait(false);
                log(ok ? $"[S29] 已独立推送复核/风险 {reviewEntries.Count} 条" : "[S29] 复核飞书推送失败（细节已省略）");
            }
            catch (Exception ex) { log("[S29] 复核飞书异常：" + ex.Message); }
        }
        return messages;
    }

    private static string Label(string k) => k switch
    {
        "strike" => "击球区",
        "approach" => "关注区",
        "optimistic" => "乐观区",
        "downgrade" => "降级预警",
        "manual_review_task" => "人工复核任务",
        "risk_event_alert" => "风险事件提醒",
        _ => k,
    };

    private static bool Recently(Dictionary<string, string> seen, string key, string asOf, int days)
    {
        if (!seen.TryGetValue(key, out var last) || !DateTime.TryParse(last, out var d) || !DateTime.TryParse(asOf, out var now))
            return false;
        // 简化：用自然日 7 天近似 5 个交易日（避免再拉日历）；严格交易日可在后续用指数日历替换
        return (now.Date - d.Date).TotalDays < Math.Max(days, 5) + 2;
    }

    private static Dictionary<string, string> LoadDedupe(string dir)
    {
        var p = Path.Combine(dir, "alert_dedupe.csv");
        var d = new Dictionary<string, string>();
        if (!File.Exists(p)) return d;
        foreach (var r in QvData.ReadCsv(p))
            if (r.TryGetValue("key", out var k) && r.TryGetValue("date", out var dt)) d[k] = dt;
        return d;
    }

    private static void SaveDedupe(string dir, Dictionary<string, string> seen)
    {
        var rows = seen.Select(kv => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["key"] = kv.Key, ["date"] = kv.Value });
        QvData.WriteCsv(Path.Combine(dir, "alert_dedupe.csv"), rows, ["key", "date"]);
    }
}
