"""T5 季度归因数据准备：组装 LLM 输入 → data/skill_input_T5.md。

完整流程：
1. 本脚本读取 04号交易日志 + 07号信号台账最近 90 天 + 各桶基准指数走势
2. 按 skills/t5_attribution.md 定义的输入格式组装 → data/skill_input_T5.md
3. 用户 / CI 把该文件喂给 LLM → 产出写到 data/skill_output_T5.md

用法：
    python scripts/t5_prepare.py                   # 默认最近一个季度
    python scripts/t5_prepare.py --season 2026Q2   # 指定季度
    python scripts/t5_prepare.py --days 90         # 自定义天数
"""
from __future__ import annotations

import argparse
import datetime as dt
import os
import sys
from typing import Dict, List, Optional, Tuple

import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib.paths import DATA_DIR, ensure_dirs
from lib.config import get_yaml_tag
from lib.signal_log import read_all as read_signals
from lib.trade_log import read_all as read_trades
from lib.data_fetch import get_index_daily


SKILL_INPUT = DATA_DIR / "skill_input_T5.md"

# 桶基准映射
BUCKET_BENCHMARKS = {
    "A": ("000922", "中证红利"),
    "B": ("399006", "创业板指"),
    "C": ("000905", "中证500"),
}
MARKET_BENCHMARK = ("000300", "沪深300")


def _current_season() -> str:
    """推断当前季度标签。"""
    today = dt.date.today()
    q = (today.month - 1) // 3 + 1
    return f"{today.year}Q{q}"


def _season_date_range(season: str) -> Tuple[dt.date, dt.date]:
    """从季度标签推算起止日期。如 2026Q2 → (2026-04-01, 2026-06-30)。"""
    year = int(season[:4])
    q = int(season[-1])
    start_month = (q - 1) * 3 + 1
    start = dt.date(year, start_month, 1)
    if q == 4:
        end = dt.date(year, 12, 31)
    else:
        end = dt.date(year, start_month + 3, 1) - dt.timedelta(days=1)
    return start, end


def _fetch_benchmark(code: str, name: str, start: dt.date, end: dt.date) -> str:
    """拉取基准指数起止价格和区间收益率。"""
    df = get_index_daily(code, start.strftime("%Y-%m-%d"), end.strftime("%Y-%m-%d"))
    if df.empty or len(df) < 2:
        return f"{code}  {name}  start={start} end={end} return=N/A（数据不足）"

    df = df.sort_values("date")
    start_price = float(df["close"].iloc[0])
    end_price = float(df["close"].iloc[-1])
    ret = (end_price / start_price - 1) * 100
    return (
        f"{code}  {name}  "
        f"{df['date'].iloc[0]} start={start_price:.2f}  "
        f"{df['date'].iloc[-1]} end={end_price:.2f}  "
        f"return={ret:+.2f}%"
    )


def prepare(season: Optional[str] = None, days: Optional[int] = None) -> int:
    """组装 T5 归因输入文件。"""
    ensure_dirs()

    if not season:
        # 默认取上一个季度
        today = dt.date.today()
        q = (today.month - 1) // 3
        if q == 0:
            season = f"{today.year - 1}Q4"
        else:
            season = f"{today.year}Q{q}"

    if days:
        end = dt.date.today()
        start = end - dt.timedelta(days=days)
    else:
        start, end = _season_date_range(season)

    print(f"[T5-prepare] 季度={season}  日期范围={start} ~ {end}")

    # 1. 交易日志
    trades_df = read_trades()
    if not trades_df.empty and "日期" in trades_df.columns:
        trades_df["_date"] = pd.to_datetime(trades_df["日期"], errors="coerce").dt.date
        trades_df = trades_df[
            (trades_df["_date"] >= start) & (trades_df["_date"] <= end)
        ].drop(columns=["_date"])
    trade_csv = trades_df.to_csv(index=False) if not trades_df.empty else "（无交易记录）"
    print(f"  交易日志：{len(trades_df)} 条")

    # 2. 信号台账
    signals_df = read_signals()
    if not signals_df.empty and "触发日期" in signals_df.columns:
        signals_df["_date"] = pd.to_datetime(signals_df["触发日期"], errors="coerce").dt.date
        signals_df = signals_df[
            (signals_df["_date"] >= start) & (signals_df["_date"] <= end)
        ].drop(columns=["_date"])
    signal_csv = signals_df.to_csv(index=False) if not signals_df.empty else "（无信号记录）"
    print(f"  信号台账：{len(signals_df)} 条")

    # 3. 基准指数
    benchmark_lines: List[str] = []
    # 沪深300
    line = _fetch_benchmark(MARKET_BENCHMARK[0], MARKET_BENCHMARK[1], start, end)
    benchmark_lines.append(line)
    print(f"  基准 {MARKET_BENCHMARK[1]}：{line.split('return=')[-1] if 'return=' in line else 'N/A'}")

    for bucket, (code, name) in BUCKET_BENCHMARKS.items():
        line = _fetch_benchmark(code, name, start, end)
        benchmark_lines.append(f"[桶{bucket}基准] {line}")
        ret_str = line.split("return=")[-1] if "return=" in line else "N/A"
        print(f"  基准 桶{bucket} {name}：{ret_str}")

    # 4. 组装输出
    yaml_tag = get_yaml_tag()
    output = f"""=== SEASON: {season} ===

=== TRADE_LOG ===
{trade_csv}

=== SIGNAL_LOG ===
{signal_csv}

=== BENCHMARKS ===
{chr(10).join(benchmark_lines)}

=== CURRENT_YAML_HASH ===
{yaml_tag}
"""

    DATA_DIR.mkdir(parents=True, exist_ok=True)
    SKILL_INPUT.write_text(output, encoding="utf-8")
    print(f"\n[T5-prepare] 输入文件已生成：{SKILL_INPUT}")
    print(f"[T5-prepare] 文件大小：{SKILL_INPUT.stat().st_size:,} bytes")
    print(f"[T5-prepare] 请将内容喂给 LLM（参考 skills/t5_attribution.md）")
    print(f"[T5-prepare] LLM 产出写回 data/skill_output_T5.md")
    return 0


# ============================================================
# CLI
# ============================================================

def main() -> int:
    parser = argparse.ArgumentParser(description="T5 季度归因数据准备")
    parser.add_argument("--season", type=str, default=None,
                        help="季度标签如 2026Q2（默认取上一个自然季）")
    parser.add_argument("--days", type=int, default=None,
                        help="自定义天数（覆盖 --season 的起止计算）")
    args = parser.parse_args()
    return prepare(season=args.season, days=args.days)


if __name__ == "__main__":
    sys.exit(main())
