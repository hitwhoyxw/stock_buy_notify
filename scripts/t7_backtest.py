"""T7 · 参数回测入口（包装 06_backtest_*.py）

用法：
    python scripts/t7_backtest.py --bucket A     # 只跑红利桶
    python scripts/t7_backtest.py --bucket AB    # 跑 A + B 桶（季末）
    python scripts/t7_backtest.py --bucket C     # 只跑热点桶（月末）
    python scripts/t7_backtest.py --bucket all

行为：
- 定位 trading-system/06_backtest_*.py 并调用其 __main__
- 结果 CSV 保留在 trading-system/ 下（脚本自己写）
- 生成 data/report_YYYY-MM-DD_T7.md 摘要
- 报告仅静默存档，不推送（除非 --notify）
"""
from __future__ import annotations

import argparse
import datetime as dt
import runpy
import sys
from pathlib import Path
from typing import Dict, List

import pandas as pd

from lib import notifier, paths, report
from lib.paths import TRADING_SYSTEM_DIR

BACKTEST_SCRIPTS = {
    "A": TRADING_SYSTEM_DIR / "06_backtest_dividend.py",
    "BC": TRADING_SYSTEM_DIR / "06_backtest_growth_hot.py",
}
RESULT_CSVS = {
    "A": TRADING_SYSTEM_DIR / "06_backtest_dividend_result.csv",
    "BC": TRADING_SYSTEM_DIR / "06_backtest_growth_hot_result.csv",
}


def run_backtest(script: Path) -> None:
    if not script.exists():
        print(f"[T7] 未找到 {script}", file=sys.stderr)
        return
    print(f"[T7] 执行 {script.name} …")
    sys.path.insert(0, str(script.parent))
    try:
        runpy.run_path(str(script), run_name="__main__")
    finally:
        sys.path.pop(0)


def render_result(csv_path: Path) -> str:
    if not csv_path.exists():
        return f"_{csv_path.name} 未生成_\n"
    df = pd.read_csv(csv_path)
    lines = ["| " + " | ".join(df.columns) + " |",
             "| " + " | ".join(["---"] * len(df.columns)) + " |"]
    for _, r in df.iterrows():
        lines.append("| " + " | ".join(str(v) for v in r.tolist()) + " |")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bucket", choices=["A", "B", "C", "AB", "BC", "all"], default="all")
    parser.add_argument("--notify", action="store_true", help="强制推送结果")
    args = parser.parse_args()

    paths.ensure_dirs()

    target: List[str] = []
    if args.bucket in ("A", "AB", "all"):
        target.append("A")
    if args.bucket in ("B", "C", "AB", "BC", "all"):
        target.append("BC")

    for tag in target:
        run_backtest(BACKTEST_SCRIPTS[tag])

    today = dt.date.today()
    sections = []
    for tag in target:
        sections.append((f"{tag} 桶回测结果", render_result(RESULT_CSVS[tag])))

    sections.append(("使用说明",
                     "回测结果为**待评估值**，是否采纳需按 05 号文档 3.3 决策矩阵评估：\n"
                     "- 若新阈值优于当前 yaml，标记为 candidate，进入 A/B 两季度 或 C 两月影子并跑。\n"
                     "- 影子并跑期间**不改 yaml**，仅在 07 号台账用另一 signal_id 前缀（如 `SIG-shadow-`）记录。\n"))

    path = report.write_report(
        task="T7",
        title=f"T7 参数回测 · {today.isoformat()}",
        alerts=[],
        sections=sections,
    )
    print(f"[T7] 报告已写入 {path}")

    if args.notify:
        notifier.notify(path.read_text(encoding="utf-8"),
                        title=f"T7 回测结果 · {today}",
                        level="P2")
    return 0


if __name__ == "__main__":
    sys.exit(main())
