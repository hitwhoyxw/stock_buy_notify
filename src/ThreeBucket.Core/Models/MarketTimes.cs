using System;

namespace ThreeBucket.Core.Models;

/// <summary>
/// A 股交易时段判断（行情自动刷新节流用）。
/// 移植自 Python 桌面端 engine.py 的同名函数，两端行为保持一致。
/// </summary>
public static class MarketTimes
{
    /// <summary>
    /// 是否处于行情会变化的时段（工作日 9:15-11:30 / 13:00-15:05）。
    /// 含集合竞价（9:15 起）与收盘定盘缓冲（15:05 前）；午休与收盘后行情不再变化。
    /// 节假日按工作日近似 —— 误差只是多拉一次，无害。
    /// </summary>
    public static bool IsMarketTime(DateTime? now = null)
    {
        var t0 = now ?? DateTime.Now;
        if (t0.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        var t = t0.Hour * 60 + t0.Minute;
        return (9 * 60 + 15 <= t && t < 11 * 60 + 30) || (13 * 60 <= t && t < 15 * 60 + 5);
    }

    /// <summary>
    /// 最近一次行情定盘时点（最近工作日的 15:00）。
    /// 盘外节流依据：上次拉取时间晚于该时点，数据不可能再变化。
    /// </summary>
    public static DateTime LastSettleTime(DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        if (t.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && t.Hour >= 15)
            return t.Date.AddHours(15);
        var d = t.Date.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            d = d.AddDays(-1);
        return d.AddHours(15);
    }

    /// <summary>
    /// 行情是否需要拉取：盘中恒 true；盘外仅在最近定盘后未拉过时 true。
    /// lastFetch 为 null 表示从未拉过。
    /// </summary>
    public static bool ShouldRefreshQuotes(DateTime? lastFetch, DateTime? now = null)
    {
        if (IsMarketTime(now))
            return true;
        if (lastFetch is { } lf && lf > LastSettleTime(now))
            return false; // 定盘后已拉过，数据不可能变化
        return true;
    }
}
