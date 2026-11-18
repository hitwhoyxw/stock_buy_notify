using ThreeBucket.Core.Services.BottomReversal;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T10 · S27 增强版底部反转 V1.2（100 分趋势评分 + 交易阶段/B1B2B3/风险收益比）全市场扫描，纯 C# 实现（ThreeBucket.Core/Services/BottomReversal），不依赖 Python。
/// 参数在 data/bottom_reversal_v1_1.json（无则回退 v1），每次运行重新读取；输出 data/bottom_reversal/。原有 S26/T9 不受影响。
/// </summary>
public sealed class BottomReversalTask : IBuiltinTask
{
    public string Key => "T10";
    public string Name => "增强版底部反转 S27";

    private readonly string _dataDir;

    public BottomReversalTask(string dataDir) => _dataDir = dataDir;

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) { log?.Invoke(m); Console.WriteLine(m); }
        try
        {
            var cfg = BrConfig.Load(_dataDir);
            var r = await BrScan.RunAsync(cfg, _dataDir, L, ct: ct).ConfigureAwait(false);
            var counts = string.Join("，", new[] { "强反转", "反转初期", "反弹观察", "暂不关注" }.Select(k => $"{k} {r.RatingCounts.GetValueOrDefault(k)}"));
            var body = File.Exists(r.ReportPath) ? await File.ReadAllTextAsync(r.ReportPath, ct).ConfigureAwait(false) : "_日报文件缺失_";
            // 盘中运行丢弃了今日未完成K线（扫描日≠今天）时不写 report_*_T10.md，避免内置定时器认为今天已跑过而跳过收盘后那次
            var today = TradingCalendar.NowCn().ToString("yyyy-MM-dd");
            var report = r.ReportPath;
            if (r.ScanDay == today)
                report = ReportBuilder.WriteReport(_dataDir, Key, $"T10 增强版底部反转 S27 V1.2（{r.ScanDay}）", [("扫描结果", body)]);
            else
                L($"[T10] 扫描日 {r.ScanDay} ≠ 今天 {today}（盘中运行），日报只写在 data/bottom_reversal/，不生成定时器去重标记");
            // t40：扫描完成后更新 B1/B2/B3 信号 30 日跟踪（失败不影响扫描结果）
            var track = "";
            try { var tr = await BrTrack.RunAsync(cfg, _dataDir, L, ct).ConfigureAwait(false); track = $" · {tr.Summary}"; }
            catch (Exception tex) { L($"[T10] 信号跟踪更新失败（不影响扫描）：{tex.Message}"); track = " · 信号跟踪更新失败"; }
            var g = r.Meta["group_counts"];
            var groups = g is null ? "" : $" · V1.2 A组 {g["A"]} / B组 {g["B"]} / C组 {g["C"]}";
            return new TaskRunResult(true, report, r.Scored, $"扫描日 {r.ScanDay} · 评分 {r.Scored} 只 · {counts}{groups}{track}");
        }
        catch (Exception ex)
        {
            L($"[T10] 失败: {ex.Message}");
            return new TaskRunResult(false, "", 0, ex.Message);
        }
    }
}
