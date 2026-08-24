using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 自动云同步（对应设置页"自动同步"开关）：
/// <list type="bullet">
/// <item>启动约 15 秒后自动拉取一次，此后每 10 分钟拉取——云端比本地新的种类覆盖本地（覆盖前自动备份）；</item>
/// <item>监控 4 个同步文件（策略/流水/自选/提醒），本地变化停止 30 秒后自动上传（任务跑完、手动编辑均覆盖）；</item>
/// <item>全程静默：进度与结果通过 Status 事件上报状态栏，失败不弹窗；手动上传/恢复仍保留在设置页。</item>
/// </list>
/// 拉取按"云端 UpdatedAt > 本地文件修改时间"逐类比对，避免云端旧数据覆盖本地新改动。
/// </summary>
public sealed class AutoSyncService : IDisposable
{
    private const int PullFirstDelaySec = 15;   // 启动后延迟首拉，不拖慢启动
    private const int PullPeriodMin = 10;       // 定时拉取周期（下行同步：其它设备的变更）
    private const int PushDebounceSec = 30;     // 本地变化防抖：停止修改 30 秒后才上传

    private readonly Func<AppConfig> _config;
    private readonly DataStore _store;
    private readonly string _dataDir;
    private readonly Timer _pullTimer;
    private readonly Timer _pushTimer;          // one-shot：防抖到期执行上传
    private readonly FileSystemWatcher? _watcher;
    private int _syncing;   // 拉取/上传互斥（0=空闲）
    private int _importing; // 拉取落盘期间置位：忽略 watcher 触发，防止"拉下来→又推回去"回环
    private DateTime _lastPush = DateTime.MinValue;

    /// <summary>同步状态上报（后台线程触发，订阅方需自行切换到 UI 线程）。</summary>
    public event Action<string>? Status;

    public AutoSyncService(Func<AppConfig> config, DataStore store, string dataDir)
    {
        _config = config;
        _store = store;
        _dataDir = dataDir;

        _pullTimer = new Timer(_ => _ = PullAsync(), null,
            TimeSpan.FromSeconds(PullFirstDelaySec), TimeSpan.FromMinutes(PullPeriodMin));
        _pushTimer = new Timer(_ => _ = PushAsync(), null, Timeout.Infinite, Timeout.Infinite);

        // 数据目录文件监控（Android/iOS 沙盒可能不支持，失败则退化为仅定时拉取）
        try
        {
            _watcher = new FileSystemWatcher(dataDir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
        }
        catch
        {
            _watcher = null; // 平台不支持 FSW：仅靠定时拉取兜底，不影响其余功能
        }
    }

    /// <summary>与 DataStore.SyncFiles 对应的本地文件名（watcher 过滤用）。</summary>
    private static readonly HashSet<string> SyncFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "strategies.csv", "live_trade_log.csv", "watchlist.csv", "monitor_alerts.json",
    };

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        if (!SyncFileNames.Contains(Path.GetFileName(e.Name ?? ""))) return;
        if (Interlocked.CompareExchange(ref _importing, 1, 1) == 1) return; // 拉取落盘触发的变化，忽略
        // 防抖：每次变化重置计时器，停止修改 PushDebounceSec 秒后才真正上传
        _pushTimer.Change(TimeSpan.FromSeconds(PushDebounceSec), Timeout.InfiniteTimeSpan);
    }

    private CloudSyncService? Svc()
    {
        var cfg = _config();
        if (!cfg.AutoSync) return null;
        var svc = new CloudSyncService(cfg.SupabaseUrl, cfg.SupabaseKey);
        return svc.IsConfigured ? svc : null;
    }

    /// <summary>拉取云端数据：云端较新的种类覆盖本地（ImportSyncSnapshot 自动备份原文件）。</summary>
    private async Task PullAsync()
    {
        if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0) return;
        try
        {
            var svc = Svc();
            if (svc is null) return;

            var (rows, error) = await svc.PullAsync();
            if (error.Length > 0) { Status?.Invoke($"☁ 自动同步跳过：{error}"); return; }

            // 逐类比对时间：云端 UpdatedAt(转本地) 比本地文件修改时间新（留 1 分钟容差）才覆盖
            var newer = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var r in rows)
            {
                var localPath = Path.Combine(_dataDir, LocalFileOf(r.Kind));
                var cloudLocal = r.UpdatedAt.ToLocalTime();
                if (!File.Exists(localPath) || cloudLocal > File.GetLastWriteTime(localPath).AddMinutes(1))
                    newer[r.Kind] = r.Payload;
            }
            if (newer.Count == 0) return; // 云端没有比本地新的数据：静默，不打扰

            Status?.Invoke($"☁ 自动同步：发现云端较新数据（{string.Join("/", newer.Keys)}），正在覆盖本地…");
            Interlocked.Exchange(ref _importing, 1);
            try
            {
                var (count, _) = _store.ImportSyncSnapshot(newer);
                Status?.Invoke($"☁ 自动同步完成：已从云端更新 {count} 类数据（原文件备份在 data/sync_backup/，切到各页可查看）");
            }
            finally
            {
                // 延迟清标志：文件系统事件异步传播，立即清零可能把导入写入误判为本地变化
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Interlocked.Exchange(ref _importing, 0));
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke($"⚠️ 自动同步异常: {ex.Message}");
        }
        finally
        {
            _syncing = 0;
        }
    }

    /// <summary>上传本地全部可同步数据到云端（本地数据变化防抖到期 / 任务跑完后触发）。</summary>
    private async Task PushAsync()
    {
        if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0)
        {   // 正在同步：稍后重试一次，避免这次变化被吞掉
            _pushTimer.Change(TimeSpan.FromSeconds(PushDebounceSec), Timeout.InfiniteTimeSpan);
            return;
        }
        try
        {
            var svc = Svc();
            if (svc is null) return;

            var snapshot = _store.ExportSyncSnapshot();
            if (snapshot.Count == 0) return;

            var (ok, msg) = await svc.PushAsync(snapshot, Environment.MachineName);
            if (ok)
            {
                _lastPush = DateTime.Now;
                Status?.Invoke($"☁ 自动同步：本地数据变化已上传云端（{DateTime.Now:HH:mm}）");
            }
            else
            {
                Status?.Invoke($"⚠️ 自动上传失败：{msg}");
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke($"⚠️ 自动上传异常: {ex.Message}");
        }
        finally
        {
            _syncing = 0;
        }
    }

    /// <summary>kind → 本地文件名（与 DataStore.SyncFiles 一致；未知 kind 返回空串）。</summary>
    private static string LocalFileOf(string kind) => kind switch
    {
        "strategies" => "strategies.csv",
        "trades" => "live_trade_log.csv",
        "watchlist" => "watchlist.csv",
        "alerts" => "monitor_alerts.json",
        _ => "",
    };

    public void Dispose()
    {
        _pullTimer.Dispose();
        _pushTimer.Dispose();
        if (_watcher is not null)
        {
            _watcher.Changed -= OnFileTouched;
            _watcher.Created -= OnFileTouched;
            _watcher.Dispose();
        }
    }
}
