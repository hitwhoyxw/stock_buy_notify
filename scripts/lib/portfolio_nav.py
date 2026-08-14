"""组合净值序列管理。

维护 data/portfolio_nav.csv，记录每日盘后净值：
  date, A_mv, B_mv, C_mv, D_mv, total_mv, nav, peak_nav, drawdown_pct

nav 以首个交易日为 1.0000 基准归一化。
drawdown_pct = (nav - peak_nav) / peak_nav * 100，负值表示回撤。

T1 每日运行时调用 update_nav() 追加一行，
check_portfolio_drawdown() 读取净值序列判断是否触发 -15% / -20% 熔断。
"""
from __future__ import annotations

import datetime as dt
from typing import Any, Dict, List, Optional

import pandas as pd

from lib.paths import DATA_DIR

NAV_CSV = DATA_DIR / "portfolio_nav.csv"
NAV_COLUMNS = [
    "date", "A_mv", "B_mv", "C_mv", "D_mv",
    "total_mv", "nav", "peak_nav", "drawdown_pct",
]

# 熔断阈值（占总资产百分比回撤）
CIRCUIT_BREAKER_L1 = -15.0  # -15%：减仓至半仓
CIRCUIT_BREAKER_L2 = -20.0  # -20%：清仓至防守状态


def read_nav() -> pd.DataFrame:
    """读取净值序列。"""
    if not NAV_CSV.exists():
        return pd.DataFrame(columns=NAV_COLUMNS)
    try:
        df = pd.read_csv(NAV_CSV, dtype=str)
        for c in NAV_COLUMNS:
            if c not in df.columns:
                df[c] = ""
        return df[NAV_COLUMNS]
    except Exception:
        return pd.DataFrame(columns=NAV_COLUMNS)


def latest_nav() -> Optional[Dict[str, float]]:
    """返回最近一行净值数据。"""
    df = read_nav()
    if df.empty:
        return None
    row = df.iloc[-1]
    return {
        "date": row["date"],
        "total_mv": float(row["total_mv"] or 0),
        "nav": float(row["nav"] or 1.0),
        "peak_nav": float(row["peak_nav"] or 1.0),
        "drawdown_pct": float(row["drawdown_pct"] or 0),
    }


def update_nav(positions: pd.DataFrame, today: dt.date) -> Dict[str, Any]:
    """计算当日净值并追加到 CSV。

    positions: trade_log.current_positions() 返回的 DataFrame
    today: 当天日期

    返回 {"nav": float, "drawdown_pct": float, "peak_nav": float, "updated": bool}
    """
    from lib.data_fetch import get_tencent_batch_quotes

    # 计算各桶市值
    bucket_mv: Dict[str, float] = {"A": 0.0, "B": 0.0, "C": 0.0, "D": 0.0}

    if not positions.empty:
        codes = [str(r["代码"]).strip() for _, r in positions.iterrows() if str(r["代码"]).strip()]
        quotes = pd.DataFrame()
        if codes:
            try:
                quotes = get_tencent_batch_quotes(codes)
            except Exception:
                pass

        for _, row in positions.iterrows():
            code = str(row["代码"]).strip()
            bucket = str(row.get("桶", "")).strip().upper()
            shares = float(row.get("净股数", 0) or 0)
            if not code or shares <= 0 or bucket not in bucket_mv:
                continue
            price = 0.0
            if not quotes.empty and code in quotes["code"].values:
                q = quotes[quotes["code"] == code].iloc[0]
                price = float(q.get("price", 0) or 0)
            if price <= 0:
                # 回退用平均成本
                price = float(row.get("平均成本", 0) or 0)
            bucket_mv[bucket] += shares * price

    total_mv = sum(bucket_mv.values())

    # 读取历史净值计算 nav 归一化
    hist = read_nav()
    if hist.empty:
        nav = 1.0 if total_mv > 0 else 1.0
        peak_nav = nav
    else:
        base_mv = float(hist.iloc[0]["total_mv"] or 0)
        if base_mv > 0:
            nav = total_mv / base_mv
        else:
            nav = 1.0
        peak_nav = max(float(hist["peak_nav"].astype(float).max() or 1.0), nav)

    drawdown_pct = (nav - peak_nav) / peak_nav * 100 if peak_nav > 0 else 0.0

    # 追加行（如果当天已有记录则更新）
    new_row = {
        "date": today.isoformat(),
        "A_mv": f"{bucket_mv['A']:.2f}",
        "B_mv": f"{bucket_mv['B']:.2f}",
        "C_mv": f"{bucket_mv['C']:.2f}",
        "D_mv": f"{bucket_mv['D']:.2f}",
        "total_mv": f"{total_mv:.2f}",
        "nav": f"{nav:.4f}",
        "peak_nav": f"{peak_nav:.4f}",
        "drawdown_pct": f"{drawdown_pct:.2f}",
    }

    # 如果当天已有记录，替换；否则追加
    if not hist.empty and hist.iloc[-1]["date"] == today.isoformat():
        hist.iloc[-1] = new_row
        hist.to_csv(NAV_CSV, index=False, encoding="utf-8")
    else:
        import csv
        file_exists = NAV_CSV.exists() and NAV_CSV.stat().st_size > 0
        with NAV_CSV.open("a", encoding="utf-8", newline="") as f:
            writer = csv.DictWriter(f, fieldnames=NAV_COLUMNS)
            if not file_exists:
                writer.writeheader()
            writer.writerow(new_row)

    return {
        "nav": nav,
        "drawdown_pct": drawdown_pct,
        "peak_nav": peak_nav,
        "bucket_mv": bucket_mv,
        "total_mv": total_mv,
    }


def check_circuit_breaker(cfg: Dict[str, Any]) -> List[Dict[str, Any]]:
    """检查组合级熔断。读取净值序列，判断回撤是否触及阈值。

    返回 alert 列表。
    """
    alerts: List[Dict[str, Any]] = []
    latest = latest_nav()
    if latest is None:
        return alerts

    dd = latest["drawdown_pct"]
    today_str = latest["date"]

    if dd <= CIRCUIT_BREAKER_L2:
        alerts.append({
            "level": "P0",
            "rule_id": "PORTFOLIO-CB-L2",
            "bucket": "*",
            "target": "全组合",
            "current": f"净值回撤 {dd:.1f}%",
            "threshold": f"<= {CIRCUIT_BREAKER_L2}%",
            "action": "组合级二级熔断：清仓 B/C 桶，A 桶减至 10%，D 桶提升至 70%",
            "source": f"portfolio_nav · {today_str}",
        })
    elif dd <= CIRCUIT_BREAKER_L1:
        alerts.append({
            "level": "P1",
            "rule_id": "PORTFOLIO-CB-L1",
            "bucket": "*",
            "target": "全组合",
            "current": f"净值回撤 {dd:.1f}%",
            "threshold": f"<= {CIRCUIT_BREAKER_L1}%",
            "action": "组合级一级熔断：B/C 桶各减仓 50%，暂停新建仓",
            "source": f"portfolio_nav · {today_str}",
        })

    return alerts
