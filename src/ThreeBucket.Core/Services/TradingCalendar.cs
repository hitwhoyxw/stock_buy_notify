using System.Globalization;
using System.Text.Json;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>
/// A 股交易日历（C# 版，akshare 替代）：以上证指数日K的日期序列为权威日历，
/// 缓存到 data/cache/trade_calendar.json —— 与 Python lib/trading_day.py 同格式，两端共享互读。
/// 拉不到且无缓存时按"周一~周五=交易日"兜底（宁可多跑一次无信号扫描，不可静默跳过）。
/// </summary>
public class TradingCalendar
{
    private static readonly TimeZoneInfo CnTz = ResolveCnTz();
    private readonly string _cachePath;
    private readonly KlineService _klines;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<string>? _days; // "yyyy-MM-dd" 升序
    private DateTime _loadedAt;

    public TradingCalendar(string dataDir, KlineService klines)
    {
        _cachePath = Path.Combine(dataDir, "cache", "trade_calendar.json");
        _klines = klines;
    }

    /// <summary>北京时间当前时间（UTC+8，与 Python today_cn 同口径，不受运行机器时区影响）。</summary>
    public static DateTime NowCn()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CnTz).LocalDateTime;

    private static TimeZoneInfo ResolveCnTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (Exception)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); // 老 Windows id
        }
    }

    /// <summary>加载日历（内存 24h 新鲜；缓存文件 24h 新鲜则直接用，否则拉上证指数日K刷新）。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (Fresh()) return;
        await _lock.WaitAsync();
        try
        {
            if (Fresh()) return;

            List<string>? days = null;
            if (File.Exists(_cachePath) && (DateTime.Now - File.GetLastWriteTime(_cachePath)).TotalHours < 24)
                days = TryReadCache();
            if (days is null)
            {
                var bars = await _klines.GetIndexDailyAsync("000001", 600);
                if (bars is { Count: > 0 })
                {
                    days = bars.Select(b => b.Date.ToString("yyyy-MM-dd")).ToList();
                    TryWriteCache(days);
                }
                else if (File.Exists(_cachePath))
                    days = TryReadCache(); // 网络失败：回退过期缓存
            }
            if (days is not null)
            {
                _days = days;
                _loadedAt = DateTime.Now;
            }
        }
        finally { _lock.Release(); }
    }

    private bool Fresh() => _days is not null && (DateTime.Now - _loadedAt).TotalHours < 24;

    private List<string>? TryReadCache()
    {
        try
        {
            var raw = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_cachePath));
            return raw is { Length: > 0 } ? raw.OrderBy(d => d, StringComparer.Ordinal).ToList() : null;
        }
        catch { return null; }
    }

    private void TryWriteCache(List<string> days)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(days));
        }
        catch { /* 缓存写失败不影响判断 */ }
    }

    /// <summary>是否交易日。日历不可用时按周末近似兜底（与 MarketTimes 同策略）。</summary>
    public async Task<bool> IsTradingDayAsync(DateTime date)
    {
        await EnsureLoadedAsync();
        var key = date.ToString("yyyy-MM-dd");
        if (_days is null || _days.Count == 0)
            return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        // 日期超出日历覆盖（日K收盘后才含当日 bar，盘中拉取的日历永远缺"今天"）：
        // 按周末近似兜底 —— 宁可多跑一次无信号扫描，不可静默跳过
        if (string.CompareOrdinal(_days[^1], key) < 0)
            return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        return _days.Contains(key);
    }

    /// <summary>date 之后（不含当日）第 n 个交易日。日历不可用返回 null。</summary>
    public async Task<DateTime?> NextTradingDayAsync(DateTime date, int n = 1)
    {
        if (n <= 0) return date;
        await EnsureLoadedAsync();
        if (_days is null) return null;
        var idx = UpperBound(_days, date.ToString("yyyy-MM-dd"));
        var target = idx + n - 1;
        return target < _days.Count && DateTime.TryParseExact(_days[target], "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>从 base 起数 n 个交易日后的日期（base 非交易日按下一交易日算起，Python days_offset_to_date 口径）。</summary>
    public async Task<DateTime?> DaysOffsetToDateAsync(DateTime baseDate, int tradingDays)
    {
        if (await IsTradingDayAsync(baseDate))
            return tradingDays > 1 ? await NextTradingDayAsync(baseDate, tradingDays - 1) : baseDate;
        return await NextTradingDayAsync(baseDate, tradingDays);
    }

    /// <summary>第一个严格大于 key 的下标（二分，_days 升序）。</summary>
    private static int UpperBound(List<string> sorted, string key)
    {
        var lo = 0;
        var hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (string.CompareOrdinal(sorted[mid], key) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
