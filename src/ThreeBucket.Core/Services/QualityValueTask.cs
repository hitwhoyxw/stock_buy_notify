using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services.QualityValue;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T12 · S29 优质股击球区：排雷 → 分型 → 准入 → 评分 → 三档估值 → 监控提醒。
/// 全量重扫每年 2 次（5 月初、9 月初）或手动；日常收盘后只更新价格、除权与轻量事件。
/// 参数 data/quality_value_v1.json；输出 data/quality_value/。不改观察池，不影响 S26/S27/S28。
/// </summary>
public sealed class QualityValueTask : IBuiltinTask
{
    public string Key => "T12";
    public string Name => "优质股击球区 S29";

    private readonly string _dataDir;
    private readonly EastMoneyClient _em;
    private readonly QuoteService _quotes;
    private readonly Func<AppConfig>? _cfg;
    private readonly DataStore? _store;

    public QualityValueTask(string dataDir, EastMoneyClient em, QuoteService quotes, Func<AppConfig>? cfg = null, DataStore? store = null)
    {
        _dataDir = dataDir; _em = em; _quotes = quotes; _cfg = cfg; _store = store;
    }

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) { log?.Invoke(m); Console.WriteLine(m); }
        try
        {
            var r = await QvScan.RunAsync(_dataDir, "auto", L, _em, _quotes, _cfg?.Invoke(), _store, null, ct).ConfigureAwait(false);
            var body = File.Exists(r.ReportPath) ? await File.ReadAllTextAsync(r.ReportPath, ct).ConfigureAwait(false) : "_报告缺失_";
            var today = TradingCalendar.NowCn().ToString("yyyy-MM-dd");
            var report = r.ReportPath;
            if (r.AsOf == today)
                report = ReportBuilder.WriteReport(_dataDir, Key, $"T12 优质股击球区 S29（{r.AsOf} {r.Mode}）", [("扫描结果", body)]);
            else
                L($"[T12] 扫描日 {r.AsOf} ≠ 今天 {today}，不写定时器去重标记");
            return new TaskRunResult(true, report, r.Alerts.Count,
                $"{r.Mode} · 合格 {r.Qualified} / 观察 {r.Watch} / 排除 {r.Excluded} · 击球 {r.Strike} · 关注 {r.Approach} · 提醒 {r.Alerts.Count}");
        }
        catch (Exception ex)
        {
            L($"[T12] 失败: {ex.Message}");
            return new TaskRunResult(false, "", 0, ex.Message);
        }
    }
}
