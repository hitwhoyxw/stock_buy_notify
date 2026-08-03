"""T4 财报季 ingest：消费 LLM 文本判定输出 → 写入 07 号信号台账。

完整流程：
1. t4_c_input.py（本文件另一半）从 akshare 抓财报/纪要摘要 → data/skill_input_T4C.md
2. 人工 / CI 用 LLM 跑 skills/t4_c_text_scan.md → data/skill_output_T4C.md（JSON 数组）
3. 本脚本读 JSON → 过滤 PASS → 写 07 号台账信号 + 生成报告 + 推送

用法：
    # ingest（读 LLM 产出写台账）
    python scripts/t4_ingest.py

    # input 准备（抓财报节选产出供 LLM 消费的输入）
    python scripts/t4_ingest.py --prepare --codes 600028,601088,601225

CLI 参数：
    --prepare          : 运行输入准备阶段
    --codes            : 逗号分隔的股票代码（prepare 模式下使用）
    --input-file       : 覆盖 LLM 输出路径（默认 data/skill_output_T4C.md）
    --dry-run          : 不写入台账，只打印
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib.paths import DATA_DIR, SKILLS_DIR, ensure_dirs
from lib.config import get_yaml_tag
from lib.signal_log import append_signal, init_if_missing
from lib.report import write_report
from lib.notifier import notify

SKILL_OUTPUT = DATA_DIR / "skill_output_T4C.md"
SKILL_INPUT = DATA_DIR / "skill_input_T4C.md"


# ============================================================
# Phase 1：输入准备（从 akshare 抓财报摘要 → skill_input_T4C.md）
# ============================================================

def prepare_input(codes: List[str]) -> Path:
    """为给定股票代码列表拉取财报关键段落，组装成 skill 需要的输入文件。"""
    import akshare as ak

    sections: List[str] = []
    for code in codes:
        pure = code.strip().split(".")[0]
        try:
            # 拉取最新公告关键段落（使用 stock_notice_report）
            # akshare 接口随版本变动，此处做 best-effort
            info = _get_stock_info(pure)
            name = info.get("name", pure)
            industry = info.get("industry", "未知")

            # 尝试获取最近的业绩摘要/管理层讨论
            text_block = _fetch_latest_report_excerpt(pure)
            if not text_block:
                text_block = f"（{name} 暂无可用财报摘要数据，请手动补充）"

            section = (
                f"=== {pure} · {name} · {industry} ===\n"
                f"数据来源: 自动抓取（akshare）\n"
                f"公开日期: {dt.date.today().isoformat()}\n"
                f"------\n"
                f"{text_block}\n"
                f"------\n"
            )
            sections.append(section)
            print(f"[T4-prepare] ✓ {pure} {name}")
        except Exception as e:
            print(f"[T4-prepare] ✗ {pure} 抓取失败：{e}", file=sys.stderr)
            sections.append(
                f"=== {pure} · ? · ? ===\n"
                f"数据来源: 抓取失败\n"
                f"公开日期: {dt.date.today().isoformat()}\n"
                f"------\n"
                f"（抓取失败：{e}，请手动补充）\n"
                f"------\n"
            )

    DATA_DIR.mkdir(parents=True, exist_ok=True)
    content = "\n\n".join(sections)
    SKILL_INPUT.write_text(content, encoding="utf-8")
    print(f"\n[T4-prepare] 输入文件已生成：{SKILL_INPUT}")
    print(f"[T4-prepare] 共 {len(codes)} 只，请将内容喂给 LLM（参考 skills/t4_c_text_scan.md）")
    return SKILL_INPUT


def _get_stock_info(code: str) -> Dict[str, str]:
    """获取个股基本信息（名称、行业）。"""
    try:
        import akshare as ak
        df = ak.stock_individual_info_em(symbol=code)
        info = {}
        if df is not None and not df.empty:
            for _, row in df.iterrows():
                key = str(row.iloc[0]) if len(row) > 0 else ""
                val = str(row.iloc[1]) if len(row) > 1 else ""
                if "名称" in key or "stock_name" in key.lower():
                    info["name"] = val
                elif "行业" in key or "industry" in key.lower():
                    info["industry"] = val
        return info
    except Exception:
        return {}


def _fetch_latest_report_excerpt(code: str) -> str:
    """尝试抓取最新财报/公告的管理层讨论节选。

    注意：akshare 的公告接口变动频繁；这里做 best-effort，
    如果接口不可用返回空字符串由用户手动补充。
    """
    try:
        import akshare as ak
        # 尝试拉取最新业绩预告
        df = ak.stock_yjyg_em(date=dt.date.today().strftime("%Y%m%d")[:6])
        if df is not None and not df.empty:
            row = df[df.iloc[:, 0].astype(str).str.contains(code)]
            if not row.empty:
                # 拼接预告内容
                cols = [c for c in row.columns if "摘要" in c or "内容" in c or "变动" in c]
                texts = [str(row.iloc[0][c]) for c in cols if str(row.iloc[0][c]) != "nan"]
                if texts:
                    return "\n".join(texts)
    except Exception:
        pass

    try:
        import akshare as ak
        # 尝试拉取最新研报摘要
        df = ak.stock_research_report_em(symbol=code)
        if df is not None and not df.empty:
            # 取最新 3 条研报标题和观点
            recent = df.head(3)
            lines = []
            for _, row in recent.iterrows():
                title = str(row.get("报告名称", row.get("title", "")))
                lines.append(title)
            return "\n".join(lines) if lines else ""
    except Exception:
        pass

    return ""


# ============================================================
# Phase 2：Ingest（读 LLM 产出 → 写入 07 号台账）
# ============================================================

def parse_llm_output(path: Path) -> List[Dict[str, Any]]:
    """从 skill_output_T4C.md 中提取 JSON 数组。

    文件可能包含 markdown 围栏，需要跳过非 JSON 部分。
    """
    if not path.exists():
        print(f"[T4-ingest] LLM 输出文件不存在：{path}", file=sys.stderr)
        return []

    text = path.read_text(encoding="utf-8").strip()

    # 尝试直接解析
    try:
        data = json.loads(text)
        if isinstance(data, list):
            return data
    except json.JSONDecodeError:
        pass

    # 尝试从 markdown 代码块中提取
    pattern = r"```(?:json)?\s*\n(.*?)\n```"
    matches = re.findall(pattern, text, re.DOTALL)
    for match in matches:
        try:
            data = json.loads(match)
            if isinstance(data, list):
                return data
        except json.JSONDecodeError:
            continue

    # 尝试找到 [ 开头到 ] 结尾的最大区间
    start = text.find("[")
    end = text.rfind("]")
    if start >= 0 and end > start:
        try:
            data = json.loads(text[start:end + 1])
            if isinstance(data, list):
                return data
        except json.JSONDecodeError:
            pass

    print("[T4-ingest] 无法从文件中解析 JSON 数组", file=sys.stderr)
    return []


def ingest(path: Path, dry_run: bool = False) -> int:
    """读取 LLM 产出 → 把 PASS 的条目写入信号台账。返回写入条数。"""
    ensure_dirs()
    init_if_missing()

    results = parse_llm_output(path)
    if not results:
        print("[T4-ingest] 没有可用结果", file=sys.stderr)
        return 0

    today = dt.date.today()
    yaml_tag = get_yaml_tag()
    passed: List[Dict[str, Any]] = []
    rejected: List[Dict[str, Any]] = []

    for item in results:
        verdict = str(item.get("verdict", "")).upper()
        if verdict == "PASS":
            passed.append(item)
        else:
            rejected.append(item)

    print(f"[T4-ingest] 解析 {len(results)} 条结果：PASS={len(passed)} REJECT={len(rejected)}")

    written = 0
    signals: List[str] = []
    for item in passed:
        record = {
            "触发日期": today.isoformat(),
            "yaml_version_at_trigger": yaml_tag,
            "触发任务": "T4",
            "桶": "C",
            "规则ID": "C-TEXT-SCAN",
            "标的代码": str(item.get("stock_code", "")),
            "标的名称": str(item.get("stock_name", "")),
            "申万一级行业": str(item.get("industry", "")),
            "分桶基准代码": "000905",  # 中证 500 作为 C 桶通用基准
            "触发时指标值": str(item.get("weighted_score", "")),
            "阈值": "6.0",
            "当时组合状态": "",
            "信号方向": "买入候选",
            "建议动作": "纳入 C 桶候选池观察",
            "是否实际执行": "",
            "备注": str(item.get("reason", ""))[:200],
        }

        if dry_run:
            print(f"  [dry-run] 会写入：{record['标的代码']} {record['标的名称']} "
                  f"score={record['触发时指标值']}")
        else:
            sid = append_signal(record)
            signals.append(sid)
            print(f"  ✓ {sid} | {record['标的代码']} {record['标的名称']} "
                  f"score={record['触发时指标值']}")
        written += 1

    # 生成报告
    sections = []
    if passed:
        lines = ["| 代码 | 名称 | 行业 | 加权分 | 关键理由 |",
                 "|------|------|------|--------|----------|"]
        for item in passed:
            lines.append(
                f"| {item.get('stock_code','')} "
                f"| {item.get('stock_name','')} "
                f"| {item.get('industry','')} "
                f"| {item.get('weighted_score','')} "
                f"| {str(item.get('reason',''))[:50]} |"
            )
        sections.append(("通过文本判定（纳入候选池）", "\n".join(lines)))

    if rejected:
        lines = ["| 代码 | 名称 | 加权分 | 淘汰理由 |",
                 "|------|------|--------|----------|"]
        for item in rejected:
            lines.append(
                f"| {item.get('stock_code','')} "
                f"| {item.get('stock_name','')} "
                f"| {item.get('weighted_score','')} "
                f"| {str(item.get('reason',''))[:60]} |"
            )
        sections.append(("未通过文本判定", "\n".join(lines)))

    alerts = []
    if passed:
        alerts.append(("P1", f"T4 文本扫描：{len(passed)} 只通过景气判定，纳入 C 桶候选池"))

    if not dry_run:
        report_path = write_report(
            task="T4",
            title=f"T4 财报季文本扫描 · {today}",
            sections=sections,
            alerts=alerts,
            date=today,
        )
        print(f"\n[T4-ingest] 报告：{report_path}")

        # 推送
        if passed:
            summary = "\n".join([
                f"**T4 文本扫描 · {today}**",
                f"通过：{len(passed)} 只 | 淘汰：{len(rejected)} 只",
                "",
                "通过标的：",
            ] + [f"- {p['stock_code']} {p['stock_name']}（{p.get('industry','')}，"
                 f"分={p.get('weighted_score','')}）" for p in passed[:10]])
            notify(summary, title="T4 C桶文本信号", level="P1")

    return written


# ============================================================
# CLI
# ============================================================

def main() -> int:
    parser = argparse.ArgumentParser(description="T4 财报季扫描：输入准备 / ingest")
    parser.add_argument("--prepare", action="store_true",
                        help="运行输入准备阶段（抓取财报摘要）")
    parser.add_argument("--codes", type=str, default="",
                        help="逗号分隔股票代码（prepare 模式）")
    parser.add_argument("--input-file", type=Path, default=None,
                        help="覆盖 LLM 输出文件路径")
    parser.add_argument("--dry-run", action="store_true",
                        help="只打印，不写入台账")
    args = parser.parse_args()

    if args.prepare:
        codes = [c.strip() for c in args.codes.split(",") if c.strip()]
        if not codes:
            print("[T4] --prepare 模式必须指定 --codes", file=sys.stderr)
            return 1
        prepare_input(codes)
        return 0

    # 默认：ingest 模式
    path = args.input_file or SKILL_OUTPUT
    n = ingest(path, dry_run=args.dry_run)
    print(f"\n[T4-ingest] 完成，写入 {n} 条信号")
    return 0


if __name__ == "__main__":
    sys.exit(main())
