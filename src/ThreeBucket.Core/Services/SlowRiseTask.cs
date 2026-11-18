using ThreeBucket.Core.Services.SlowRise;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T11 · S28 底部反转缓升（第三个底部策略）每日扫描：L0~L4 → 观察池（持久化）→ BUY → 模拟持仓 → §12 信号表。
/// 纯 C#（Services/SlowRise）；参数 data/bottom_slowrise_v2.json（默认，精简版 v2.0 + t37；环境变量 SR_CONFIG 可指定其他文件，v2 缺失时回退 v1）；输出 data/bottom_slowrise/。不影响 T9(S26)/T10(S27) 与现有观察池。
/// </summary>
public sealed class SlowRiseTask : IBuiltinTask
{
    public string Key => "T11";
    public string Name => "底部反转缓升 S28";
    private readonly string _dataDir;
    public SlowRiseTask(string dataDir) => _dataDir = dataDir;

    public async Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) { log?.Invoke(m); Console.WriteLine(m); }
        try
        {
            var r = await SrScan.RunAsync(_dataDir, L, ct: ct).ConfigureAwait(false);
            var body = File.Exists(r.ReportPath) ? await File.ReadAllTextAsync(r.ReportPath, ct).ConfigureAwait(false) : "_日报文件缺失_";
            var today = TradingCalendar.NowCn().ToString("yyyy-MM-dd");
            var report = r.ReportPath;
            if (r.Day == today) report = ReportBuilder.WriteReport(_dataDir, Key, $"T11 底部反转缓升 S28（{r.Day}）", [("扫描结果", body)]);
            else L($"[T11] 扫描日 {r.Day} ≠ 今天 {today}（盘中/非交易日运行），日报只写在 data/bottom_slowrise/，不生成定时器去重标记");
            var c = r.Counts;
            return new TaskRunResult(true, report, r.Scanned, $"扫描日 {r.Day} · {r.Scanned} 只 · BUY {c["BUY"]} / SELL {c["SELL"]} / REDUCE {c["REDUCE"]} / HOLD {c["HOLD"]} / WATCH {c["WATCH"]}");
        }
        catch (Exception ex)
        {
            L($"[T11] 失败: {ex.Message}");
            return new TaskRunResult(false, "", 0, ex.Message);
        }
    }
}
