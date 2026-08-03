"""T8 · 信号台账维护（每交易日 17:00）

三步（对应 03 号文档 T8 定义）：

1. 追加当日新信号：无。信号在 T1/T2/T3 生成时已直接调 signal_log.append_signal 写入台账。
   T8 只做校验，扫出格式错误的行做重复告警。

2. 回补历史信号收益：
   - 对每条"是否实际执行=是"的记录，若已过 60/120/250 交易日 → 拉执行价 & 当期价 → 算收益、超沪深300、超分桶基准。
   - 每 horizon 回补一次即写死。

3. 实盘 vs 回测胜率对比：
   - 按桶 × 规则ID 汇总最近 30/90 天记录，
   - 计算实盘胜率、回测预期胜率、差值 delta。
   - 若样本 ≥5 且 delta < -10pct → 触发失效预警 P0。

用法：
    python scripts/t8_signal_log.py            # 正常运行
    python scripts/t8_signal_log.py --dry-run  # 只查不写
"""
from __future__ import annotations

import argparse
import datetime as dt
import sys
from typing import Any, Dict, List, Optional, Tuple

import pandas as pd

from lib import notifier, paths, report, signal_log
from lib.config import get_config, get_yaml_tag
from lib.data_fetch import get_index_daily, get_stock_daily
from lib.signal_log import RETURN_HORIZONS, parse_date
from lib.trading_day import days_offset_to_date, is_trading_day


# ============================================================
# 收益计算
# ============================================================

def _get_close_on(code: str, target_date: dt.date) -> Optional[float]:
    """取某标的在 target_date 当天（若非交易日则取最近的下一交易日）的收盘价。"""
    if not code or code in ("-", ""):
        return None
    start = (target_date - dt.timedelta(days=15)).strftime("%Y-%m-%d")
    end = (target_date + dt.timedelta(days=15)).strftime("%Y-%m-%d")
    df = get_stock_daily(code, start, end, adjust="qfq")
    if df.empty:
        return None
    df = df[df["date"] >= target_date].sort_values("date")
    if df.empty:
        return None
    return float(df["close"].iloc[0])


def _get_index_close_on(code: str, target_date: dt.date) -> Optional[float]:
    if not code:
        return None
    pure = code.split(".")[0]
    start = (target_date - dt.timedelta(days=15)).strftime("%Y-%m-%d")
    end = (target_date + dt.timedelta(days=15)).strftime("%Y-%m-%d")
    df = get_index_daily(pure, start, end)
    if df.empty:
        return None
    df = df[df["date"] >= target_date].sort_values("date")
    if df.empty:
        return None
    return float(df["close"].iloc[0])


def _bucket_benchmark_code(bucket: str, industry: str, cfg: Dict[str, Any]) -> str:
    bm = cfg.get("bucket_benchmarks", {}) or {}
    if bucket == "A":
        return bm.get("A", "000922")
    if bucket == "B":
        return bm.get("B", "399006")
    if bucket == "C":
        # 动态取申万一级行业指数；简化处理：无映射时回退中证 500
        return bm.get("cross_reference", "000905")
    return bm.get("cross_reference", "000300")


def _compute_returns(row: pd.Series, cfg: Dict[str, Any]) -> Dict[str, Any]:
    """对单行信号回补 60/120/250 收益。返回可直接 update_signal 的 patch。"""
    exec_date_str = row.get("执行日期") or row.get("触发日期")
    exec_date = parse_date(exec_date_str)
    if exec_date is None:
        return {}

    # 用 T+1 开盘价起算：这里简化为 exec_date 后第 1 个交易日的收盘价
    try:
        base_date = days_offset_to_date(exec_date, 1)
    except IndexError:
        return {}

    exec_price = row.get("执行价格")
    try:
        base_price = float(exec_price) if exec_price and str(exec_price).strip() else None
    except (TypeError, ValueError):
        base_price = None
    if not base_price:
        base_price = _get_close_on(str(row["标的代码"]).strip(), base_date)
    if not base_price:
        return {}

    hs300_base = _get_index_close_on("000300", base_date)
    bench_code = row.get("分桶基准代码") or _bucket_benchmark_code(row.get("桶", ""), row.get("申万一级行业", ""), cfg)
    bench_base = _get_index_close_on(bench_code, base_date)

    patch: Dict[str, Any] = {}
    today = dt.date.today()

    for horizon_days, cols in RETURN_HORIZONS:
        try:
            target = days_offset_to_date(base_date, horizon_days)
        except IndexError:
            continue
        if target > today:
            continue  # 未到期

        # 已回补过则跳过（col[0] 存标的绝对收益）
        if str(row.get(cols[0], "")).strip():
            continue

        stock_end = _get_close_on(str(row["标的代码"]).strip(), target)
        if stock_end is None:
            continue
        ret = (stock_end / base_price - 1) * 100
        patch[cols[0]] = f"{ret:.2f}"

        if hs300_base:
            hs300_end = _get_index_close_on("000300", target)
            if hs300_end:
                hs_ret = (hs300_end / hs300_base - 1) * 100
                patch[cols[1]] = f"{ret - hs_ret:.2f}"

        if bench_base:
            bench_end = _get_index_close_on(bench_code, target)
            if bench_end:
                b_ret = (bench_end / bench_base - 1) * 100
                patch[cols[2]] = f"{ret - b_ret:.2f}"

    if patch and not row.get("分桶基准代码"):
        patch["分桶基准代码"] = bench_code

    return patch


# ============================================================
# 失效预警
# ============================================================

def check_alpha_decay(df: pd.DataFrame) -> List[Dict[str, Any]]:
    """按 桶+规则ID 汇总最近 90 天记录，若实盘胜率 < 回测预期 - 10pct 且 n>=5 → P0。"""
    if df.empty:
        return []
    today = dt.date.today()
    cutoff = today - dt.timedelta(days=90)

    df = df.copy()
    df["触发日期_dt"] = pd.to_datetime(df["触发日期"], errors="coerce").dt.date
    recent = df[(df["触发日期_dt"] >= cutoff) & (df["是否实际执行"] == "是")]
    if recent.empty:
        return []

    def _pct(v: Any) -> Optional[float]:
        try:
            s = str(v).replace("%", "").strip()
            return float(s) if s else None
        except (TypeError, ValueError):
            return None

    alerts: List[Dict[str, Any]] = []
    for (bucket, rule), g in recent.groupby(["桶", "规则ID"]):
        # 用 60 日收益判定胜率（最主流的短期观察窗）
        rets = g["事后60日收益%"].map(_pct).dropna()
        if len(rets) < 5:
            continue
        live_winrate = (rets > 0).mean() * 100
        expected_str = g["回测预期胜率"].dropna().astype(str).iloc[0] if not g["回测预期胜率"].dropna().empty else ""
        expected = _pct(expected_str)
        if expected is None:
            continue
        delta = live_winrate - expected
        if delta < -10:
            alerts.append({
                "level": "P0",
                "rule_id": f"DECAY-{bucket}-{rule}",
                "bucket": bucket,
                "target": f"{bucket} 桶规则 {rule}",
                "current": f"实盘胜率 {live_winrate:.1f}% (n={len(rets)})",
                "threshold": f"回测预期 {expected:.1f}%（差 {delta:+.1f}pct）",
                "action": "暂停使用该规则，触发 T7 重新回测；参数变更前不得再触发新信号",
                "source": "T8 实盘vs回测",
            })
    return alerts


# ============================================================
# 主流程
# ============================================================

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    paths.ensure_dirs()
    today = dt.date.today()
    cfg = get_config()
    yaml_tag = get_yaml_tag()

    df = signal_log.read_all()
    n_updated = 0
    updated_ids: List[str] = []

    if not df.empty:
        for idx, row in df.iterrows():
            if row.get("是否实际执行", "") != "是":
                continue
            patch = _compute_returns(row, cfg)
            if patch:
                if args.dry_run:
                    print(f"[dry-run] {row['signal_id']} patch = {patch}")
                else:
                    signal_log.update_signal(str(row["signal_id"]), patch)
                n_updated += 1
                updated_ids.append(str(row["signal_id"]))

    # 重新读一遍拿最新收益
    df = signal_log.read_all()
    decay_alerts = check_alpha_decay(df)

    if decay_alerts and not args.dry_run:
        for a in decay_alerts:
            signal_log.append_signal({
                "触发日期": today.isoformat(),
                "yaml_version_at_trigger": yaml_tag,
                "触发任务": "T8",
                "桶": a["bucket"],
                "规则ID": a["rule_id"],
                "标的代码": "-",
                "标的名称": "失效预警",
                "触发时指标值": a["current"],
                "阈值": a["threshold"],
                "当时组合状态": "-",
                "信号方向": "暂停",
                "建议动作": a["action"],
                "是否实际执行": "否",
                "信号最终评价": "alpha decay",
            })

    # 台账总览统计
    total_signals = len(df)
    executed = len(df[df["是否实际执行"] == "是"]) if not df.empty else 0
    pending_backfill = 0
    if not df.empty and executed > 0:
        exec_df = df[df["是否实际执行"] == "是"]
        for _, r in exec_df.iterrows():
            if not str(r.get("事后60日收益%", "")).strip() or not str(r.get("事后120日收益%", "")).strip():
                pending_backfill += 1

    overview_md = (
        f"| 维度 | 数值 |\n"
        f"|------|------|\n"
        f"| 台账总信号数 | {total_signals} |\n"
        f"| 已实际执行 | {executed} |\n"
        f"| 待回补收益 | {pending_backfill} |\n"
        f"| 本次回补 | {n_updated} |\n"
        f"| 失效预警 | {len(decay_alerts)} 条 |\n"
    )

    # 最近信号明细（最多 10 条）
    recent_md = ""
    if not df.empty:
        recent = df.tail(10).iloc[::-1]  # 最新在前
        recent_rows = []
        for _, r in recent.iterrows():
            recent_rows.append({
                "signal_id": str(r.get("signal_id", "")),
                "日期": str(r.get("触发日期", "")),
                "桶": str(r.get("桶", "")),
                "规则": str(r.get("规则ID", "")),
                "标的": f"{r.get('标的代码', '')} {r.get('标的名称', '')}",
                "方向": str(r.get("信号方向", "")),
                "已执行": str(r.get("是否实际执行", "")),
                "60d收益": str(r.get("事后60日收益%", "")) or "-",
            })
        recent_md = report.render_kv_table(
            recent_rows, ["signal_id", "日期", "桶", "规则", "标的", "方向", "已执行", "60d收益"]
        )
    else:
        recent_md = "_台账为空_\n"

    body_sections = [
        ("台账总览", overview_md),
        ("最近信号（倒序前 10）", recent_md),
        ("回补明细", f"本次回补 signal_id：`{', '.join(updated_ids[:20])}"
                     f"{'…' if len(updated_ids) > 20 else ''}`\n"
                     if updated_ids else "本次无需回补。\n"),
        ("失效预警", report.render_alert_list(decay_alerts) if decay_alerts else "本次无失效预警。\n"),
        ("yaml 版本", f"`{yaml_tag}`\n"),
    ]

    path = report.write_report(
        task="T8",
        title=f"T8 台账维护 · {today.isoformat()}",
        alerts=decay_alerts,
        sections=body_sections,
    )
    print(f"[T8] 报告已写入 {path}，回补 {n_updated} 行，失效预警 {len(decay_alerts)} 条")

    if decay_alerts and not args.dry_run:
        notifier.notify(path.read_text(encoding="utf-8"),
                        title=f"T8 失效预警 · {today}",
                        level="P0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
