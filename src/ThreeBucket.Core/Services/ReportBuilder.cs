using System.Text;

namespace ThreeBucket.Core.Services;

/// <summary>风控告警（T1/T8 输出的通用结构，对齐 Python 端的 alert dict）。</summary>
public sealed record RiskAlert(
    string Level,      // P0/P1/P2/P3
    string RuleId,     // 如 C-E1 / B-STOP / CONC-STOCK / PORTFOLIO-CB-L1
    string Bucket,     // A/B/C/D/*（* 表示组合级）
    string Target,     // 标的（"代码 名称"）或组合/行业描述
    string Current,    // 当前值描述
    string Threshold,  // 阈值描述
    string Action,     // 建议动作
    string Source);    // 数据依据

/// <summary>C# 原生内置任务（桌面/移动端通用，不依赖 Python 运行时）。</summary>
public interface IBuiltinTask
{
    string Key { get; }    // T1 / T8
    string Name { get; }   // 每日风控 / 信号台账
    Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default);
}

/// <summary>内置任务运行结果。</summary>
public sealed record TaskRunResult(bool Ok, string ReportPath, int AlertCount, string Summary);

/// <summary>markdown 报告渲染与落盘（移植自 Python lib/report.py，输出格式保持一致）。</summary>
public static class ReportBuilder
{
    /// <summary>渲染告警列表（level/rule/target/current/threshold/action/source 全量展示）。</summary>
    public static string RenderAlertList(IEnumerable<RiskAlert> alerts)
    {
        var list = alerts.ToList();
        if (list.Count == 0) return "**本次无触发。系统正常。**\n";
        var sb = new StringBuilder();
        foreach (var a in list)
        {
            var icon = a.Level switch { "P0" => "🔴", "P1" => "🟠", "P2" => "🟡", "P3" => "⚪", _ => "•" };
            sb.AppendLine($"- {icon} **[{a.Level}] 规则 {a.RuleId}** · {a.Target}");
            sb.AppendLine($"    - 当前值：`{a.Current}` · 阈值：`{a.Threshold}`");
            sb.AppendLine($"    - 建议：{a.Action}");
            sb.AppendLine($"    - 依据：{a.Source}");
        }
        return sb.ToString();
    }

    /// <summary>一句话结论。</summary>
    public static string SummaryLine(IReadOnlyList<RiskAlert> alerts)
    {
        var p0 = alerts.Count(a => a.Level == "P0");
        var p1 = alerts.Count(a => a.Level == "P1");
        if (p0 > 0) return $"**⚠️ 结论：{p0} 条 P0 需立即处理，{p1} 条 P1 待 24h 内决策。**";
        if (p1 > 0) return $"**结论：{p1} 条 P1 待 24h 内决策，无 P0。**";
        if (alerts.Count > 0) return $"**结论：{alerts.Count} 条 P2/P3 提示，可择时处理。**";
        return "**结论：本次运行无信号触发，系统正常。**";
    }

    /// <summary>list[dict] 渲染成 Markdown 表格。</summary>
    public static string RenderKvTable(IEnumerable<Dictionary<string, string>> rows, IReadOnlyList<string> cols)
    {
        var list = rows.ToList();
        if (list.Count == 0) return "_（空）_\n";
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", cols) + " |");
        sb.AppendLine("| " + string.Join(" | ", cols.Select(_ => "---")) + " |");
        foreach (var r in list)
            sb.AppendLine("| " + string.Join(" | ", cols.Select(c => r.GetValueOrDefault(c, ""))) + " |");
        return sb.ToString();
    }

    /// <summary>
    /// 写报告 data/report_YYYY-MM-DD_{task}.md。sections = [(标题, 正文)]，alerts 非空时前置"触发项"。
    /// 返回报告绝对路径。
    /// </summary>
    public static string WriteReport(string dataDir, string task, string title,
        IReadOnlyList<(string Title, string Body)> sections, IReadOnlyList<RiskAlert>? alerts = null)
    {
        var today = TradingCalendar.NowCn();
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, $"report_{today:yyyy-MM-dd}_{task}.md");

        var parts = new List<string>
        {
            $"# {title}",
            $"_运行时间：{today:yyyy-MM-dd HH:mm:ss}（C# 内置任务）_",
            "",
        };
        if (alerts is not null)
        {
            parts.Add(SummaryLine(alerts));
            parts.Add("");
            parts.Add("## 触发项");
            parts.Add(RenderAlertList(alerts));
        }
        foreach (var (t, body) in sections)
        {
            parts.Add($"## {t}");
            parts.Add(body);
            parts.Add("");
        }
        File.WriteAllText(path, string.Join("\n", parts), new UTF8Encoding(false));
        return path;
    }
}
