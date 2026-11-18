using System.Globalization;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 内置定时调度引擎：工作日到点自动跑 C# 原生任务（对应设置页"内置定时器"）。
/// 用 System.Threading.Timer 每分钟检查一次——桌面常开可自动执行；移动端前台运行时同样生效
/// （系统杀后台进程属平台限制，重新打开时若当天到点未跑会自动补跑一次）。
/// 支持两个时段：收盘后主时段（SchedulerTime，如 16:30 跑 T1/T8）与盘中/尾盘时段
/// （IntradaySchedulerTime，如 14:30 跑 T9）；各任务以当日 report_yyyy-MM-dd_{任务}.md 去重。
/// 触发与结果通过 Status 事件上报，UI 层自行调度回自己的线程。
/// </summary>
public sealed class TaskSchedulerEngine : IDisposable
{
    private readonly Func<AppConfig> _config;
    private readonly IReadOnlyDictionary<string, IBuiltinTask> _tasks;
    private readonly string _dataDir;
    private readonly Timer _timer;
    private int _running;

    /// <summary>调度状态/结果上报（后台线程触发，订阅方需自行切换到 UI 线程）。</summary>
    public event Action<string>? Status;

    public TaskSchedulerEngine(Func<AppConfig> config, IReadOnlyDictionary<string, IBuiltinTask> tasks, string dataDir)
    {
        _config = config;
        _tasks = tasks;
        _dataDir = dataDir;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
    }

    private async Task TickAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            var cfg = _config();
            if (!cfg.SchedulerEnabled) return;

            var now = TradingCalendar.NowCn();
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;

            await RunSlotAsync(cfg.SchedulerTime, cfg.SchedulerTasksStr, now, new TimeSpan(16, 30, 0)).ConfigureAwait(false);
            await RunSlotAsync(cfg.IntradaySchedulerTime, cfg.IntradaySchedulerTasksStr, now, defaultTime: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status?.Invoke($"⚠ 内置定时器异常: {ex.Message}");
        }
        finally
        {
            _running = 0;
        }
    }

    /// <summary>
    /// 到点后按任务串逐个执行；已有当日报告文件的任务跳过（支持多时段、补跑）。
    /// defaultTime 非空时，时间串解析失败则用该默认；为空则解析失败直接跳过该时段。
    /// </summary>
    private async Task RunSlotAsync(string? timeStr, string? tasksStr, DateTime now, TimeSpan? defaultTime)
    {
        if (string.IsNullOrWhiteSpace(tasksStr)) return;

        if (!TimeSpan.TryParse(timeStr, CultureInfo.InvariantCulture, out var time))
        {
            if (defaultTime is null) return;
            time = defaultTime.Value;
        }
        if (now.TimeOfDay < time) return;

        foreach (var raw in tasksStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = raw.ToUpperInvariant();
            if (!_tasks.TryGetValue(key, out var task)) continue;

            var marker = Path.Combine(_dataDir, $"report_{now:yyyy-MM-dd}_{task.Key}.md");
            if (File.Exists(marker)) continue;

            Status?.Invoke($"⏱ 内置定时器触发 {task.Key} · {task.Name} …");
            try
            {
                var result = await task.RunAsync().ConfigureAwait(false);
                Status?.Invoke(result.Ok
                    ? $"✓ 定时 {task.Key} 完成：{result.Summary}"
                    : $"✗ 定时 {task.Key} 失败：{result.Summary}");
            }
            catch (Exception ex)
            {
                Status?.Invoke($"✗ 定时 {task.Key} 异常：{ex.Message}");
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}