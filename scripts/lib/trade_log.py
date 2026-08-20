"""04 号交易日志的读写口。

用于：
- T1/T3 从日志计算当前持仓与四桶权重
- 客户端在用户"点击已执行"时追加一行
"""
from __future__ import annotations

import csv
import datetime as dt
from typing import Any, Dict, List, Optional

import pandas as pd

from lib.paths import LIVE_TRADE_LOG, TRADE_LOG_TEMPLATE

COLUMNS: List[str] = [
    "日期",
    "方向",
    "桶",
    "代码",
    "名称",
    "申万一级行业",
    "价格",
    "股数",
    "金额",
    "占总资产%",
    "触发规则ID",
    "触发时指标值",
    "阈值",
    "决策理由(一句话)",
    "当时组合状态",
    "当时四桶权重ABCD",
    "情绪自评(1-5)",
    "是否违反纪律",
    "事后30日涨跌%",
    "事后90日涨跌%",
    "复盘结论",
]


def init_if_missing() -> None:
    if LIVE_TRADE_LOG.exists():
        return
    LIVE_TRADE_LOG.parent.mkdir(parents=True, exist_ok=True)
    if TRADE_LOG_TEMPLATE.exists():
        with TRADE_LOG_TEMPLATE.open("r", encoding="utf-8") as src:
            header = src.readline()
        LIVE_TRADE_LOG.write_text(header, encoding="utf-8")
    else:
        LIVE_TRADE_LOG.write_text(",".join(COLUMNS) + "\n", encoding="utf-8")


def read_all() -> pd.DataFrame:
    init_if_missing()
    if LIVE_TRADE_LOG.stat().st_size == 0:
        return pd.DataFrame(columns=COLUMNS)
    df = pd.read_csv(LIVE_TRADE_LOG, dtype=str, keep_default_na=False)
    for c in COLUMNS:
        if c not in df.columns:
            df[c] = ""
    return df[COLUMNS]


def append_trade(record: Dict[str, Any]) -> None:
    init_if_missing()
    row = {c: str(record.get(c, "")) for c in COLUMNS}
    if not row["日期"]:
        row["日期"] = dt.date.today().isoformat()
    file_empty = LIVE_TRADE_LOG.stat().st_size == 0
    with LIVE_TRADE_LOG.open("a", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=COLUMNS)
        if file_empty:
            writer.writeheader()
        writer.writerow(row)


def current_positions() -> pd.DataFrame:
    """按代码汇总当前持仓（加权成本法，支持加仓/减仓/清仓）。

    返回列：代码, 名称, 桶, 申万一级行业, 净股数, 累计成本金额, 平均成本。

    成本口径（加权平均成本法，与 desktop/engine.py 保持一致）：
    - 买入：成本池 += 金额，股数 += 股数
    - 卖出：按当时加权均价结转成本（realized 不进成本池），
            成本池 -= 加权均价 × 卖出股数，股数 -= 卖出股数
    - 剩余持仓 平均成本 = 成本池 / 净股数
    """
    df = read_all()
    if df.empty:
        return pd.DataFrame(columns=["代码", "名称", "桶", "申万一级行业", "净股数", "累计成本金额", "平均成本"])

    df["股数"] = pd.to_numeric(df["股数"], errors="coerce").fillna(0)
    df["金额"] = pd.to_numeric(df["金额"], errors="coerce").fillna(0)

    # 按日期排序，保证买卖顺序正确
    d = df.copy()
    d["_dt"] = pd.to_datetime(d.get("日期", ""), errors="coerce")
    d = d.sort_values("_dt", kind="stable").reset_index(drop=True)

    BUY = ("买入", "buy", "BUY")
    st: Dict[str, dict] = {}
    for _, r in d.iterrows():
        code = str(r.get("代码", "")).strip()
        if not code:
            continue
        is_buy = str(r.get("方向", "")).strip() in BUY
        shares = float(r.get("股数", 0) or 0)
        amount = float(r.get("金额", 0) or 0)
        rec = st.get(code)
        if rec is None:
            rec = {"名称": "", "桶": "", "申万一级行业": "",
                   "shares": 0.0, "cost": 0.0}
            st[code] = rec
        nm = str(r.get("名称", "") or "").strip()
        bk = str(r.get("桶", "") or "").strip()
        ind = str(r.get("申万一级行业", "") or "").strip()
        if nm:
            rec["名称"] = nm
        if bk:
            rec["桶"] = bk
        if ind:
            rec["申万一级行业"] = ind

        if is_buy:
            rec["shares"] += shares
            rec["cost"] += amount
        else:
            avg = (rec["cost"] / rec["shares"]) if rec["shares"] > 0 else 0.0
            sold = min(shares, rec["shares"])
            rec["cost"] -= avg * sold
            rec["shares"] -= sold
            if rec["shares"] <= 1e-9:
                rec["shares"] = 0.0
                rec["cost"] = 0.0

    rows = []
    for code, rec in st.items():
        if rec["shares"] <= 0:
            continue
        avg = rec["cost"] / rec["shares"] if rec["shares"] > 0 else 0.0
        rows.append({
            "代码": code, "名称": rec["名称"], "桶": rec["桶"],
            "申万一级行业": rec["申万一级行业"],
            "净股数": rec["shares"], "累计成本金额": rec["cost"],
            "平均成本": avg,
        })
    return pd.DataFrame(rows)


def bucket_weights() -> Dict[str, float]:
    """按 04 号日志估算当前四桶资金占比（以累计成本金额近似市值）。
    返回 {'A': 0.x, 'B': 0.x, 'C': 0.x, 'D': 0.x}。D 桶来自日志中记录的现金/货基买入。
    """
    pos = current_positions()
    weights = {"A": 0.0, "B": 0.0, "C": 0.0, "D": 0.0}
    if pos.empty:
        return weights
    total = float(pos["累计成本金额"].sum())
    if total <= 0:
        return weights
    for _, row in pos.iterrows():
        b = str(row["桶"]).strip().upper()
        if b in weights:
            weights[b] += float(row["累计成本金额"]) / total
    return weights
