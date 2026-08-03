"""07 号信号台账 CSV 的唯一读写口。

台账 schema 见 trading-system/07_信号台账模板.csv。
所有列名保持中文原样，避免与文档漂移。

关键操作：
- init_if_missing()：从模板复制出 data/live_signal_log.csv
- append_signal(dict)：追加一行，自动生成 signal_id
- update_signal(signal_id, patch)：更新（如回补收益）
- read_all() -> DataFrame
- read_pending_returns()：找出还需要回补收益的记录
"""
from __future__ import annotations

import csv
import datetime as dt
import shutil
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional

import pandas as pd

from lib.paths import LIVE_SIGNAL_LOG, SIGNAL_LOG_TEMPLATE

# 台账 32 列（严格顺序，写入时按此顺序对齐）
COLUMNS: List[str] = [
    "signal_id",
    "触发日期",
    "yaml_version_at_trigger",
    "触发任务",
    "桶",
    "规则ID",
    "标的代码",
    "标的名称",
    "申万一级行业",
    "分桶基准代码",
    "触发时指标值",
    "阈值",
    "当时组合状态",
    "信号方向",
    "建议动作",
    "是否实际执行",
    "执行日期",
    "执行价格",
    "回测预期胜率",
    "回测预期中位收益_60d",
    "回测预期中位收益_120d",
    "回测预期中位收益_250d",
    "事后60日收益%",
    "事后120日收益%",
    "事后250日收益%",
    "事后60日超额沪深300%",
    "事后120日超额沪深300%",
    "事后250日超额沪深300%",
    "事后60日超额分桶基准%",
    "事后120日超额分桶基准%",
    "事后250日超额分桶基准%",
    "信号最终评价",
    "备注",
]

# 需要回补的收益列（(交易日数, [列名列表])）
RETURN_HORIZONS: List[tuple[int, List[str]]] = [
    (60, ["事后60日收益%", "事后60日超额沪深300%", "事后60日超额分桶基准%"]),
    (120, ["事后120日收益%", "事后120日超额沪深300%", "事后120日超额分桶基准%"]),
    (250, ["事后250日收益%", "事后250日超额沪深300%", "事后250日超额分桶基准%"]),
]


def init_if_missing() -> None:
    """首次运行：如 data/live_signal_log.csv 缺失，用模板初始化。"""
    if LIVE_SIGNAL_LOG.exists():
        return
    LIVE_SIGNAL_LOG.parent.mkdir(parents=True, exist_ok=True)
    if SIGNAL_LOG_TEMPLATE.exists():
        # 只复制表头，不带示例数据
        with SIGNAL_LOG_TEMPLATE.open("r", encoding="utf-8") as src:
            header = src.readline()
        LIVE_SIGNAL_LOG.write_text(header, encoding="utf-8")
    else:
        LIVE_SIGNAL_LOG.write_text(",".join(COLUMNS) + "\n", encoding="utf-8")


def read_all() -> pd.DataFrame:
    init_if_missing()
    if LIVE_SIGNAL_LOG.stat().st_size == 0:
        return pd.DataFrame(columns=COLUMNS)
    df = pd.read_csv(LIVE_SIGNAL_LOG, dtype=str, keep_default_na=False)
    # 补齐可能缺失的列
    for c in COLUMNS:
        if c not in df.columns:
            df[c] = ""
    return df[COLUMNS]


def write_all(df: pd.DataFrame) -> None:
    """原子写回。所有列按 COLUMNS 顺序对齐。"""
    LIVE_SIGNAL_LOG.parent.mkdir(parents=True, exist_ok=True)
    out = df.copy()
    for c in COLUMNS:
        if c not in out.columns:
            out[c] = ""
    out = out[COLUMNS]
    tmp = LIVE_SIGNAL_LOG.with_suffix(".csv.tmp")
    out.to_csv(tmp, index=False, encoding="utf-8")
    tmp.replace(LIVE_SIGNAL_LOG)


def next_signal_id(bucket: str, trigger_date: dt.date, task: str) -> str:
    """生成新 signal_id：SIG-YYYYMMDD-{bucket}-{seq}。seq 按当日当桶计数。"""
    df = read_all()
    prefix = f"SIG-{trigger_date.strftime('%Y%m%d')}-{bucket}-"
    existing = df[df["signal_id"].str.startswith(prefix, na=False)]["signal_id"].tolist()
    max_seq = 0
    for sid in existing:
        try:
            seq = int(sid.split("-")[-1])
            max_seq = max(max_seq, seq)
        except (ValueError, IndexError):
            continue
    return f"{prefix}{max_seq + 1:02d}"


def append_signal(record: Dict[str, Any]) -> str:
    """追加一条信号。record 中缺失的字段自动填 ""，signal_id 若未给则自动生成。
    返回最终 signal_id。
    """
    init_if_missing()
    trigger_date = record.get("触发日期") or dt.date.today().isoformat()
    if isinstance(trigger_date, dt.date):
        trigger_date = trigger_date.isoformat()
    bucket = str(record.get("桶", "X")).strip() or "X"
    task = str(record.get("触发任务", "T?"))

    if not record.get("signal_id"):
        record["signal_id"] = next_signal_id(bucket, dt.datetime.strptime(trigger_date, "%Y-%m-%d").date(), task)

    row = {c: str(record.get(c, "")) for c in COLUMNS}
    row["触发日期"] = trigger_date

    # 直接追加一行，避免读全表 → 加速
    file_empty = LIVE_SIGNAL_LOG.stat().st_size == 0
    with LIVE_SIGNAL_LOG.open("a", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=COLUMNS)
        if file_empty:
            writer.writeheader()
        writer.writerow(row)
    return row["signal_id"]


def update_signal(signal_id: str, patch: Dict[str, Any]) -> bool:
    """按 signal_id 更新字段。返回是否命中。"""
    df = read_all()
    mask = df["signal_id"] == signal_id
    if not mask.any():
        return False
    for k, v in patch.items():
        if k not in COLUMNS:
            continue
        df.loc[mask, k] = "" if v is None else str(v)
    write_all(df)
    return True


def read_pending_returns() -> pd.DataFrame:
    """找出还需要回补收益的记录。规则：
    - 是否实际执行 == "是"
    - 60/120/250 日至少一列为空
    - 距触发日期分别满 60/120/250 交易日
    """
    df = read_all()
    if df.empty:
        return df
    df = df[df["是否实际执行"] == "是"].copy()
    return df


def parse_date(s: str) -> Optional[dt.date]:
    if not s:
        return None
    try:
        return dt.datetime.strptime(s, "%Y-%m-%d").date()
    except ValueError:
        return None
