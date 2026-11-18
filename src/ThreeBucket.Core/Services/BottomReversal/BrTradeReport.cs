using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>V1.1 每日报告字段（需求文档第八节）、A/B/C 分组、历史信号。</summary>
public static class BrTradeReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static readonly string[] TradeCols = ["group", "group_rank", "code", "name", "industry", "bar_date", "total", "rating", "stage", "signal", "signal_date",
        "first_breakout_date", "platform", "close", "ma20_dev", "entry", "entry_basis", "stop", "stop_basis", "resistance", "resistance_basis",
        "reward_pct", "risk_pct", "rr", "rr_label", "risk_tags", "op_status", "op_note", "recent_signal", "recent_exec", "position", "v1_stage", "b1_grade", "b1_grade_note"];
    public static readonly string[] SignalHistCols = ["date", "code", "name", "total", "stage", "signal", "platform", "close", "entry", "stop", "resistance", "rr", "rr_label", "risk_tags", "op_status", "group"];
    public static readonly Dictionary<string, string> GroupNames = new() { ["A"] = "A组：趋势强且介入位置合理", ["B"] = "B组：趋势已出现，等待确认或回踩", ["C"] = "C组：趋势评分较高但已涨幅过大，暂不追" };

    private static string N(double? v, int nd = 3) => v is double x && !double.IsNaN(x) ? BrMath.PyFmt(x, nd) : "";
    private static string Pc(double? v) => v is double x && !double.IsNaN(x) ? BrMath.PyFmtSigned(x * 100, 2) + "%" : "";

    public static Dictionary<string, string> TradeRow(BrResult r, int grank)
    {
        var t = r.Trade!; var lv = t.Levels;
        return new Dictionary<string, string>
        {
            ["group"] = t.Group, ["group_rank"] = grank > 0 ? grank.ToString(Inv) : "", ["code"] = r.Code, ["name"] = r.Name, ["industry"] = r.Industry, ["bar_date"] = r.Date,
            ["total"] = r.Total?.ToString(Inv) ?? "", ["rating"] = r.Rating, ["stage"] = t.Stage, ["signal"] = t.SignalType, ["signal_date"] = t.Signal?.Date ?? "",
            ["first_breakout_date"] = t.FirstBreakoutDate ?? "", ["platform"] = N(t.PlatformLast ?? t.Platform), ["close"] = N(r.Close), ["ma20_dev"] = Pc(t.Ma20Dev),
            ["entry"] = N(lv.Entry), ["entry_basis"] = lv.EntryBasis, ["stop"] = N(lv.Stop), ["stop_basis"] = lv.StopBasis, ["resistance"] = N(lv.Resistance), ["resistance_basis"] = lv.ResistanceBasis,
            ["reward_pct"] = Pc(lv.RewardPct), ["risk_pct"] = Pc(lv.RiskPct), ["rr"] = N(lv.Rr, 2), ["rr_label"] = lv.RrLabel, ["risk_tags"] = string.Join("；", t.Tags),
            ["op_status"] = t.OpStatus, ["op_note"] = t.OpNote,
            ["recent_signal"] = t.Recent is null ? "" : $"{t.Recent.Type}@{t.Recent.Date}",
            ["recent_exec"] = t.Recent is null ? "" : t.RecentExecStatus + (t.RecentEntry is double en ? $"（模拟计划价 {N(en)}，止损 {N(t.RecentLevels?.Stop)}，压力 {N(t.RecentLevels?.Resistance)}，R/R {N(t.RecentLevels?.Rr, 2)}）" : ""),
            ["position"] = t.Position["eligibility"]?.ToString() + "；单笔风险 " + t.Position["risk_per_trade"], ["v1_stage"] = r.Stage, ["b1_grade"] = t.B1Grade.Length > 0 ? "B1-" + t.B1Grade : "", ["b1_grade_note"] = t.B1GradeNote,
        };
    }

    /// <summary>分组排序：A 按 R/R 降序；B、C 按趋势评分降序（不只按评分排序）。</summary>
    public static Dictionary<string, List<BrResult>> Groups(List<BrResult> list)
    {
        var g = new Dictionary<string, List<BrResult>>();
        foreach (var k in new[] { "A", "B", "C" })
        {
            var sub = list.Where(r => r.Trade?.Group == k);
            g[k] = (k == "A" ? sub.OrderByDescending(r => r.Trade!.Levels.Rr ?? 0).ThenByDescending(r => r.Total)
                             : sub.OrderByDescending(r => r.Total).ThenByDescending(r => r.Trade!.Levels.Rr ?? 0)).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
        }
        return g;
    }

    public static void WriteOutputs(string outDir, string scanDay, List<BrResult> list, BrConfig cfg, JsonObject meta)
    {
        var groups = Groups(list);
        var rank = new Dictionary<string, int>();
        foreach (var kv in groups) for (var k = 0; k < kv.Value.Count; k++) rank[kv.Value[k].Code] = k + 1;
        var ordered = groups["A"].Concat(groups["B"]).Concat(groups["C"]).Concat(list.Where(r => r.Trade is not null && r.Trade.Group == "").OrderByDescending(r => r.Total)).ToList();
        var rows = ordered.Select(r => (IReadOnlyDictionary<string, string>)TradeRow(r, rank.GetValueOrDefault(r.Code))).ToList();
        var histDir = Path.Combine(outDir, "history"); Directory.CreateDirectory(histDir);
        BrReport.WriteCsv(Path.Combine(outDir, $"br_trade_{scanDay}.csv"), rows, TradeCols);
        BrReport.WriteCsv(Path.Combine(outDir, "br_trade_latest.csv"), rows, TradeCols);
        BrReport.WriteCsv(Path.Combine(histDir, $"trade_{scanDay}.csv"), rows, TradeCols);
        // 历史信号：只追加有 B1/B2/B3 的股票；同一扫描日重跑时覆盖当日
        var sp = Path.Combine(outDir, "signal_history.csv");
        var keep = BrReport.ReadCsv(sp).Where(x => x.GetValueOrDefault("date") != scanDay).Cast<IReadOnlyDictionary<string, string>>().ToList();
        foreach (var r in list.Where(r => r.Trade?.Signal is not null).OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            var t = r.Trade!; var lv = t.Levels;
            keep.Add(new Dictionary<string, string>
            {
                ["date"] = scanDay, ["code"] = r.Code, ["name"] = r.Name, ["total"] = r.Total?.ToString(Inv) ?? "", ["stage"] = t.Stage, ["signal"] = t.SignalType,
                ["platform"] = N(t.Signal!.Platform), ["close"] = N(r.Close), ["entry"] = N(lv.Entry), ["stop"] = N(lv.Stop), ["resistance"] = N(lv.Resistance), ["rr"] = N(lv.Rr, 2),
                ["rr_label"] = lv.RrLabel, ["risk_tags"] = string.Join("；", t.Tags), ["op_status"] = t.OpStatus, ["group"] = t.Group,
            });
        }
        BrReport.WriteCsv(sp, keep, SignalHistCols);
        meta["group_counts"] = new JsonObject(groups.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.Count)));
        meta["trade_stage_counts"] = Count(list.Select(r => r.Trade?.Stage ?? "未计算"));
        meta["signal_counts"] = Count(list.Select(r => r.Trade?.SignalType ?? "未计算"));
        meta["op_status_counts"] = Count(list.Select(r => r.Trade?.OpStatus ?? "未计算"));
    }

    private static JsonObject Count(IEnumerable<string> xs) =>
        new(xs.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count())));

    public static string GroupsMd(List<BrResult> list, BrConfig cfg)
    {
        var groups = Groups(list); var topN = cfg.I("trade.groups.report_top_n", 20);
        var L = new List<string> { "## V1.2 交易时机排行榜（A/B/C 分组，不只按趋势评分排序）", "",
            "> 趋势评分与交易时机分开：评分高不代表买入位置好，买入信号也不代表一定盈利。计划介入价均为**模拟/参考价**：当日信号的 T+1 价格未知，按“T+1 开盘、高开>3%取消、低开跌回平台取消、R/R<1.5不追”规则执行。", "" };
        L.Add("- 分组数量：" + string.Join("，", new[] { "A", "B", "C" }.Select(k => $"{k}组 {groups[k].Count}")));
        L.Add("- 交易阶段分布：" + string.Join("，", list.GroupBy(r => r.Trade?.Stage ?? "未计算").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        L.Add("- 当日信号：" + string.Join("，", list.Where(r => r.Trade?.Signal is not null).GroupBy(r => r.Trade!.SignalType).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        L.Add("");
        foreach (var k in new[] { "A", "B", "C" })
        {
            L.Add($"### {GroupNames[k]}（{groups[k].Count} 只，显示前 {Math.Min(topN, groups[k].Count)}）");
            if (groups[k].Count == 0) { L.Add("无"); L.Add(""); continue; }
            L.Add("| # | 代码 | 名称 | 趋势分 | 阶段 | 信号 | 信号日 | 平台 | 现价 | MA20乖离 | 计划介入价(模拟) | 止损 | 上方压力 | 潜在收益 | 潜在风险 | R/R | 风险标签 | 操作状态 |");
            L.Add("|---:|---|---|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|");
            var j = 0;
            foreach (var r in groups[k].Take(topN))
            {
                var x = TradeRow(r, ++j);
                L.Add($"| {j} | {x["code"]} | {x["name"]} | {x["total"]} | {x["stage"]} | {x["signal"]}{(x["recent_signal"].Length > 0 ? "（近期 " + x["recent_signal"] + "）" : "")} | {x["signal_date"]} | {x["platform"]} | {x["close"]} | {x["ma20_dev"]} | {x["entry"]} | {x["stop"]} | {x["resistance"]} | {x["reward_pct"]} | {x["risk_pct"]} | {x["rr"]} | {x["risk_tags"]} | {x["op_status"]} |");
            }
            L.Add("");
        }
        L.Add("_完整字段（介入价/止损/压力位口径、操作说明、近期信号 T+1 检查、仓位参考）见 br_trade_<扫描日>.csv；历史信号累计见 signal_history.csv。_");
        return string.Join("\n", L);
    }

    public static string StockMd(BrResult r)
    {
        var t = r.Trade; if (t is null) return "";
        var lv = t.Levels;
        var L = new List<string>
        {
            "## 3b. 交易时机（V1.2，独立于趋势评分）",
            $"- 当前阶段：**{t.Stage}**；买入信号：**{t.SignalType}**{(t.Signal is not null ? $"（{t.Signal.Date}，{t.Signal.Note}）" : "")}；操作状态：**{t.OpStatus}**（{t.OpNote}）；分组：{(t.Group.Length > 0 ? t.Group : "-")}",
            $"- B1 分级（V1.2）：{(t.B1Grade.Length > 0 ? "B1-" + t.B1Grade + "（" + t.B1GradeNote + "）" : "-")}",
            $"- 首次突破日 {t.FirstBreakoutDate ?? "-"}；最近突破日 {t.LastBreakoutDate ?? "-"}；突破平台 {N(t.PlatformLast ?? t.Platform)}；突破后 {t.DaysSinceBreakout?.ToString(Inv) ?? "-"} 日" + (t.FailDate is not null ? $"；反转失败 {t.FailDate}：{t.FailReason}" : ""),
            $"- 计划介入价：{N(lv.Entry)}（{lv.EntryBasis}）",
            $"- 止损价：{N(lv.Stop)}（{lv.StopBasis}；ATR14 {N(lv.Atr)}）；潜在风险 {Pc(lv.RiskPct)}；纯结构止损 {N(lv.StructStop)}",
            $"- 上方压力位：{N(lv.Resistance)}（{lv.ResistanceBasis}）；潜在收益 {Pc(lv.RewardPct)}",
            $"- 风险收益比：{N(lv.Rr, 2)} → {lv.RrLabel}",
            $"- 风险标签：{(t.Tags.Count > 0 ? string.Join("；", t.Tags) : "无")}；MA20 乖离 {Pc(t.Ma20Dev)}；5日涨幅 {Pc(t.R5)}；RSI14 {N(t.Rsi, 1)}",
        };
        if (t.Recent is not null) L.Add($"- 近期信号 {t.Recent.Type}@{t.Recent.Date}：T+1 检查 {t.RecentExecStatus}；{t.RecentExecNote}" + (t.RecentEntry is double en ? $"；模拟计划价 {N(en)}（非真实成交）" : ""));
        L.Add($"- 仓位参考：{t.Position["eligibility"]}；单笔风险 {t.Position["risk_per_trade"]}；{t.Position["formula"]}；{t.Position["shares"]?.ToString() ?? ""}");
        L.Add("- 压力位不是必达目标价；止损价不保证按该价成交（跳空/跌停时实际亏损可能更大）。");
        L.Add("");
        return string.Join("\n", L);
    }
}
