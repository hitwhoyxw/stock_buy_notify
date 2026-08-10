"""A 股交易日历。

akshare 提供 tool_trade_date_hist_sina() 返回所有交易日 DataFrame，缓存后本地判断。
- is_trading_day(date): 判断某日是否交易日
- next_trading_day(date, n=1): 后推 n 个交易日
- prev_trading_day(date, n=1): 前推 n 个交易日
- trading_days_between(start, end): 区间内交易日列表

CLI 用法（供 GitHub Actions step 判断跳过）：
    python -m lib.trading_day
    # exit 0 是交易日，exit 1 非交易日；同时 stdout 打印 is_trading_day=true/false
"""
from __future__ import annotations

import datetime as dt
import functools
import json
import os
import sys
from pathlib import Path
from typing import Iterable, List, Union

from lib.paths import CACHE_DIR

DateLike = Union[str, dt.date, dt.datetime]
_CACHE_FILE = CACHE_DIR / "trade_calendar.json"
_CACHE_TTL_HOURS = 24

# A 股按北京时间计日。GitHub Actions runner 默认 UTC，直接用 date.today()
# 在凌晨触发的任务里会偏移一天；CI 与本地统一用 UTC+8 口径。
_CN_TZ = dt.timezone(dt.timedelta(hours=8))


def today_cn() -> dt.date:
    """返回北京时间（UTC+8）当前日期。供脚本与 CI 统一口径。"""
    return dt.datetime.now(_CN_TZ).date()


def now_cn() -> dt.datetime:
    """返回北京时间（UTC+8）当前 datetime（带时区）。报告运行时间用。"""
    return dt.datetime.now(_CN_TZ)


def _normalize(date: DateLike) -> dt.date:
    if isinstance(date, dt.datetime):
        return date.date()
    if isinstance(date, dt.date):
        return date
    if isinstance(date, str):
        for fmt in ("%Y-%m-%d", "%Y%m%d", "%Y/%m/%d"):
            try:
                return dt.datetime.strptime(date, fmt).date()
            except ValueError:
                continue
        raise ValueError(f"无法解析日期字符串: {date}")
    raise TypeError(f"unsupported date type: {type(date).__name__}")


def _cache_fresh() -> bool:
    if not _CACHE_FILE.exists():
        return False
    age_hours = (dt.datetime.now().timestamp() - _CACHE_FILE.stat().st_mtime) / 3600
    return age_hours < _CACHE_TTL_HOURS


def _load_from_akshare() -> List[str]:
    import akshare as ak

    df = ak.tool_trade_date_hist_sina()
    # akshare 版本差异：列名可能是 trade_date 或 0
    col = "trade_date" if "trade_date" in df.columns else df.columns[0]
    dates = [str(d)[:10] for d in df[col].tolist()]
    return sorted(set(dates))


@functools.lru_cache(maxsize=1)
def _all_trading_days() -> List[dt.date]:
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    if _cache_fresh():
        try:
            raw = json.loads(_CACHE_FILE.read_text(encoding="utf-8"))
            return [_normalize(d) for d in raw]
        except (json.JSONDecodeError, ValueError):
            pass  # 缓存损坏，重拉

    try:
        raw = _load_from_akshare()
    except Exception as e:
        # 网络失败：如有过期缓存，仍然回退使用
        if _CACHE_FILE.exists():
            print(f"[trading_day] 拉取失败({e})，回退到过期缓存", file=sys.stderr)
            raw = json.loads(_CACHE_FILE.read_text(encoding="utf-8"))
        else:
            raise RuntimeError(f"无交易日历且无本地缓存：{e}") from e

    _CACHE_FILE.write_text(json.dumps(raw, ensure_ascii=False), encoding="utf-8")
    return [_normalize(d) for d in raw]


def is_trading_day(date: DateLike | None = None) -> bool:
    if date is None:
        date = dt.date.today()
    d = _normalize(date)
    return d in set(_all_trading_days())


def next_trading_day(date: DateLike | None = None, n: int = 1) -> dt.date:
    if n <= 0:
        raise ValueError("n 必须为正整数")
    if date is None:
        date = dt.date.today()
    d = _normalize(date)
    days = _all_trading_days()
    # bisect_right：严格大于 d 的第一个位置
    import bisect

    idx = bisect.bisect_right(days, d)
    target = idx + (n - 1)
    if target >= len(days):
        raise IndexError(f"交易日历不含 {d} 之后第 {n} 个交易日")
    return days[target]


def prev_trading_day(date: DateLike | None = None, n: int = 1) -> dt.date:
    if n <= 0:
        raise ValueError("n 必须为正整数")
    if date is None:
        date = dt.date.today()
    d = _normalize(date)
    days = _all_trading_days()
    import bisect

    idx = bisect.bisect_left(days, d)  # 严格小于 d 的最后一个 = idx-1
    target = idx - n
    if target < 0:
        raise IndexError(f"交易日历不含 {d} 之前第 {n} 个交易日")
    return days[target]


def trading_days_between(start: DateLike, end: DateLike) -> List[dt.date]:
    s, e = _normalize(start), _normalize(end)
    if s > e:
        return []
    days = _all_trading_days()
    return [d for d in days if s <= d <= e]


def days_offset_to_date(base: DateLike, trading_days: int) -> dt.date:
    """从 base（含）起数 trading_days 个交易日后的日期。base 若非交易日按下一交易日算起。"""
    b = _normalize(base)
    if is_trading_day(b):
        return next_trading_day(b, trading_days - 1) if trading_days > 1 else b
    return next_trading_day(b, trading_days)


if __name__ == "__main__":
    today = today_cn() if len(sys.argv) < 2 else _normalize(sys.argv[1])
    try:
        result = is_trading_day(today)
    except Exception as e:
        # 交易日历拉取失败且无本地缓存（CI 上 data/cache 被 .gitignore 忽略，
        # 首跑依赖 akshare 访问新浪接口，海外 runner 易超时/限流）。
        # cron 已限定工作日（1-5），保守判为交易日：宁可多跑一次无信号扫描，
        # 不可让 is_trading_day 输出为空导致后续 T1/T8/commit 全部静默跳过。
        print(f"[trading_day] 判定失败({e})，兜底视为交易日", file=sys.stderr)
        result = True

    # GitHub Actions 输出：写 GITHUB_OUTPUT
    gh_output = os.environ.get("GITHUB_OUTPUT")
    if gh_output:
        with open(gh_output, "a", encoding="utf-8") as f:
            f.write(f"is_trading_day={'true' if result else 'false'}\n")

    print(f"{today} is_trading_day={result}")
    sys.exit(0 if result else 1)
