"""Markdown 报告生成。所有 TX 脚本都输出到 data/report_YYYY-MM-DD_TX.md。"""
from __future__ import annotations

import datetime as dt
from pathlib import Path
from typing import Any, Dict, List, Optional

from lib.paths import DATA_DIR
from lib.trading_day import today_cn, now_cn


def report_path(task: str, date: Optional[dt.date] = None) -> Path:
    date = date or today_cn()
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    return DATA_DIR / f"report_{date.isoformat()}_{task}.md"


def render_kv_table(rows: List[Dict[str, Any]], cols: List[str]) -> str:
    """把 list[dict] 渲染成 Markdown 表格。"""
    if not rows:
        return "_（空）_\n"
    header = "| " + " | ".join(cols) + " |"
    sep = "| " + " | ".join(["---"] * len(cols)) + " |"
    body = []
    for r in rows:
        body.append("| " + " | ".join(str(r.get(c, "")) for c in cols) + " |")
    return "\n".join([header, sep, *body]) + "\n"


def render_alert_list(alerts: List[Dict[str, Any]]) -> str:
    """渲染告警列表。alerts 每项要求含: level, rule_id, target, current, threshold, action, source。"""
    if not alerts:
        return "**本次无触发。系统正常。**\n"
    lines = []
    for a in alerts:
        icon = {"P0": "🔴", "P1": "🟠", "P2": "🟡", "P3": "⚪"}.get(a.get("level", ""), "•")
        lines.append(
            f"- {icon} **[{a.get('level', '')}] 规则 {a.get('rule_id', '')}** · {a.get('target', '')}\n"
            f"    - 当前值：`{a.get('current', 'N/A')}` · 阈值：`{a.get('threshold', 'N/A')}`\n"
            f"    - 建议：{a.get('action', '')}\n"
            f"    - 依据：{a.get('source', '')}"
        )
    return "\n".join(lines) + "\n"


def summary_line(alerts: List[Dict[str, Any]]) -> str:
    """一句话结论。"""
    p0 = sum(1 for a in alerts if a.get("level") == "P0")
    p1 = sum(1 for a in alerts if a.get("level") == "P1")
    if p0 > 0:
        return f"**⚠️ 结论：{p0} 条 P0 需立即处理，{p1} 条 P1 待 24h 内决策。**"
    if p1 > 0:
        return f"**结论：{p1} 条 P1 待 24h 内决策，无 P0。**"
    if alerts:
        return f"**结论：{len(alerts)} 条 P2/P3 提示，可择时处理。**"
    return "**结论：本次运行无信号触发，系统正常。**"


def write_report(task: str, title: str, sections: List[tuple[str, str]],
                 alerts: Optional[List[Dict[str, Any]]] = None,
                 date: Optional[dt.date] = None) -> Path:
    """写报告文件。sections = [(section_title, section_body_md), ...]。返回文件路径。"""
    date = date or today_cn()
    path = report_path(task, date)
    parts: List[str] = [
        f"# {title}",
        f"_运行时间：{now_cn().strftime('%Y-%m-%d %H:%M:%S')}_",
        "",
    ]
    if alerts is not None:
        parts.append(summary_line(alerts))
        parts.append("")
        parts.append("## 触发项")
        parts.append(render_alert_list(alerts))
    for title_, body in sections:
        parts.append(f"## {title_}")
        parts.append(body)
        parts.append("")
    path.write_text("\n".join(parts), encoding="utf-8")
    return path


def latest_report(prefix: str = "report_") -> Optional[Path]:
    files = sorted(DATA_DIR.glob(f"{prefix}*.md"))
    return files[-1] if files else None
