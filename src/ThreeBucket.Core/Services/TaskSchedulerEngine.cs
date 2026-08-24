using System.Globalization;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 内置定时调度引擎：工作日到点自动跑 C# 原生任务（对应设置页"内置定时器"）。
/// 用 System.Threading.Timer 每分钟检查一次——桌面常开可自动执行；移动端前台运行时同样生效
/// （系统杀后台进程属平台限制，重新打开时若当天到点未跑会自动补跑一次）。
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

            if (!TimeSpan.TryParse(cfg.SchedulerTime, CultureInfo.InvariantCulture, out var time))
                time = new TimeSpan(16, 30, 0);
            if (now.TimeOfDay < time) return;

            // 当日已跑过则跳过（以 T1 报告存在性为准，应用重启不会重复执行）
            var t1Report = Path.Combine(_dataDir, $"report_{now:yyyy-MM-dd}_T1.md");
            if (File.Exists(t1Report)) return;

            foreach (var raw in cfg.SchedulerTasksStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!_tasks.TryGetValue(raw.ToUpperInvariant(), out var task)) continue;
                Status?.Invoke($"⏰ 内置定时器触发 {task.Key} · {task.Name} …");
                var result = await task.RunAsync();
                Status?.Invoke(result.Ok
                    ? $"✅ 定时 {task.Key} 完成：{result.Summary}"
                    : $"❌ 定时 {task.Key} 失败：{result.Summary}");
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke($"⚠️ 内置定时器异常: {ex.Message}");
        }
        finally
        {
            _running = 0;
        }
    }

    public void Dispose() => _timer.Dispose();
}
