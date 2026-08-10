"""T1 · 每日盘后风控扫描

对照 03_Agent定期任务.md 中 T1 定义，实现以下静态检查（不含 LLM 语义步骤）：

1. C 桶持仓每日回撤 & MA60 位置（触发 C-E1 / C-E2）
2. 全组合止损：B < -25% / C < -15%
3. C 桶浮盈提示：>=40% / >=80%
4. 集中度：单票 > 8%/6%/4%；单申万一级 > 20%
5. 组合级回撤：-15% / -20% 熔断线

输出：
- data/report_YYYY-MM-DD_T1.md
- 触发的 P0/P1 追加进信号台账
- 若含 P0/P1 → 推送通知（level=P0 就 P0 通道，else P1）
"""
from __future__ import annotations

import datetime as dt
import sys
from typing import Any, Dict, List, Optional

from lib import notifier, paths, report, signal_log, trade_log
from lib.config import get_config, get_yaml_tag
from lib.data_fetch import get_index_daily, get_stock_daily
from lib.trading_day import is_trading_day, today_cn


def _pct(a: float, b: float) -> float:
    return (a - b) / b * 100 if b else 0.0


def _price_and_ma60(code: str) -> Optional[Dict[str, float]]:
    end = today_cn()
    start = end - dt.timedelta(days=180)
    df = get_stock_daily(code, start.strftime("%Y-%m-%d"), end.strftime("%Y-%m-%d"), adjust="qfq")
    if df.empty or len(df) < 30:
        return None
    close = df["close"].astype(float)
    latest = float(close.iloc[-1])
    ma60 = float(close.tail(60).mean())
    peak = float(close.max())
    drawdown = _pct(latest, peak)
    return {"latest": latest, "ma60": ma60, "peak": peak, "drawdown_pct": drawdown}


def check_c_bucket_drawdown(positions, cfg: Dict[str, Any]) -> List[Dict[str, Any]]:
    alerts: List[Dict[str, Any]] = []
    c_rules = cfg["bucket_C"]["exit_rules"]
    dd_thr = next((r for r in c_rules if r["id"] == "C-E2"), None)
    ma_thr = next((r for r in c_rules if r["id"] == "C-E1"), None)

    for _, row in positions.iterrows():
        if str(row["桶"]).strip().upper() != "C":
            continue
        code = str(row["代码"]).strip()
        if not code:
            continue
        m = _price_and_ma60(code)
        if m is None:
            continue
        if ma_thr and m["latest"] < m["ma60"]:
            alerts.append({
                "level": "P0",
                "rule_id": "C-E1",
                "bucket": "C",
                "target": f"{code} {row.get('名称', '')}",
                "current": f"现价 {m['latest']:.2f} < MA60 {m['ma60']:.2f}",
                "threshold": "price_index_cross_below_ma60",
                "action": "同日减仓 50%",
                "source": f"akshare · {today_cn()}",
            })
        if dd_thr and m["drawdown_pct"] < -15:
            alerts.append({
                "level": "P0",
                "rule_id": "C-E2",
                "bucket": "C",
                "target": f"{code} {row.get('名称', '')}",
                "current": f"距高点 {m['drawdown_pct']:.1f}%",
                "threshold": "drawdown_from_high_pct > 15",
                "action": "清仓",
                "source": f"akshare · {today_cn()}",
            })
    return alerts


def check_stop_loss(positions, cfg: Dict[str, Any]) -> List[Dict[str, Any]]:
    alerts: List[Dict[str, Any]] = []
    stop_thr = {
        "B": cfg["bucket_B"]["stop_loss"]["hard_stop_pct"],
        "C": cfg["bucket_C"]["stop_loss"]["hard_stop_pct"],
    }
    for _, row in positions.iterrows():
        bucket = str(row["桶"]).strip().upper()
        if bucket not in stop_thr:
            continue
        avg_cost = row.get("平均成本")
        if not avg_cost or not float(avg_cost):
            continue
        code = str(row["代码"]).strip()
        if not code:
            continue
        m = _price_and_ma60(code)
        if m is None:
            continue
        ret = _pct(m["latest"], float(avg_cost))
        thr = stop_thr[bucket]
        if ret <= thr:
            alerts.append({
                "level": "P0",
                "rule_id": f"{bucket}-STOP",
                "bucket": bucket,
                "target": f"{code} {row.get('名称', '')}",
                "current": f"浮亏 {ret:.1f}%",
                "threshold": f"< {thr}%",
                "action": "立即止损清仓",
                "source": f"akshare · {today_cn()}",
            })
        elif bucket == "C" and ret >= 40:
            level = "P0" if ret >= 80 else "P1"
            rule = "C-E5" if ret >= 80 else "C-E4"
            act = "再减半仓" if ret >= 80 else "减半仓并挂 10% 尾随止盈"
            alerts.append({
                "level": level,
                "rule_id": rule,
                "bucket": "C",
                "target": f"{code} {row.get('名称', '')}",
                "current": f"浮盈 {ret:.1f}%",
                "threshold": f">= {'80' if ret >= 80 else '40'}%",
                "action": act,
                "source": f"akshare · {today_cn()}",
            })
    return alerts


def check_concentration(positions, cfg: Dict[str, Any]) -> List[Dict[str, Any]]:
    alerts: List[Dict[str, Any]] = []
    if positions.empty:
        return alerts
    limits = cfg["allocation"]["concentration_limits"]
    per_bucket = limits["bucket_single_stock_max_pct"]
    industry_max = limits["single_sw_l1_industry_max_pct"]

    total = float(positions["累计成本金额"].sum())
    if total <= 0:
        return alerts

    for _, row in positions.iterrows():
        share = float(row["累计成本金额"]) / total * 100
        bucket = str(row["桶"]).strip().upper()
        cap = per_bucket.get(bucket, limits["single_stock_max_pct"])
        if share > cap:
            alerts.append({
                "level": "P2",
                "rule_id": "CONC-STOCK",
                "bucket": bucket,
                "target": f"{row['代码']} {row.get('名称', '')}",
                "current": f"单票占比 {share:.1f}%",
                "threshold": f"<= {cap}%",
                "action": f"减仓至 {cap}% 以下",
                "source": "04_交易日志",
            })

    industry_agg = positions.groupby("申万一级行业")["累计成本金额"].sum() / total * 100
    for ind, share in industry_agg.items():
        if not ind or share <= industry_max:
            continue
        alerts.append({
            "level": "P2",
            "rule_id": "CONC-INDUSTRY",
            "bucket": "*",
            "target": f"{ind}",
            "current": f"行业占比 {share:.1f}%",
            "threshold": f"<= {industry_max}%",
            "action": f"减配至 {industry_max}% 以下",
            "source": "04_交易日志 + 申万一级",
        })
    return alerts


def check_portfolio_drawdown(cfg: Dict[str, Any]) -> List[Dict[str, Any]]:
    """组合级回撤检查。当前 04 号日志暂无净值序列，本函数只输出提示文案，
    等 T5 归因或客户端接入后补真实净值曲线。
    """
    return []


# ============================================================
# 主流程
# ============================================================

def _fetch_market_overview() -> str:
    """拉取市场概况数据，生成 markdown 表格。"""
    from lib.data_fetch import (
        get_hs300_drawdown,
        get_market_turnover_percentile,
        get_latest_10y_yield,
        get_dividend_yield_percentile,
        get_relative_excess,
    )

    rows: List[str] = []
    rows.append("| 指标 | 当前值 | 备注 |")
    rows.append("|------|--------|------|")

    # 沪深300 回撤
    dd = get_hs300_drawdown(days=20)
    if dd:
        rows.append(f"| 沪深300 20日最大回撤 | {dd['drawdown_pct']:.2f}% | 单日最大跌幅 {dd['single_day_pct']:.2f}% |")
    else:
        rows.append("| 沪深300 20日最大回撤 | N/A | 数据获取失败 |")

    # 全 A 成交额分位
    turnover_pct = get_market_turnover_percentile(window_days=20, lookback_days=250)
    if turnover_pct is not None:
        heat = "偏热" if turnover_pct > 80 else ("偏冷" if turnover_pct < 20 else "中性")
        rows.append(f"| 全A 20日均量（250日分位） | {turnover_pct:.1f}% | {heat} |")
    else:
        rows.append("| 全A 20日均量分位 | N/A | 数据获取失败 |")

    # 10 年国债收益率
    y10 = get_latest_10y_yield()
    if y10 is not None:
        rows.append(f"| 10年期国债收益率 | {y10:.3f}% | {'股债性价比偏低' if y10 > 3.5 else '股债性价比尚可'} |")
    else:
        rows.append("| 10年期国债收益率 | N/A | 数据获取失败 |")

    # 中证红利股息率分位
    div = get_dividend_yield_percentile(years=5)
    if div:
        # 股息率分位低 = 股价偏贵；分位高 = 股价便宜（红利策略估值视角）
        position = "偏高(贵)" if div["percentile"] < 30 else ("偏低(便宜)" if div["percentile"] > 70 else "中位")
        rows.append(f"| 中证红利股息率 | {div['current']:.2f}% (5年{div['percentile']:.0f}%分位) | {position} |")
    else:
        rows.append("| 中证红利股息率分位 | N/A | 数据获取失败 |")

    # 红利 vs 全A 相对超额
    excess = get_relative_excess(bucket_code="000922", benchmark_code="000985", days=60)
    if excess is not None:
        rows.append(f"| 红利60日相对超额(vs全A) | {excess:+.2f}% | {'红利占优' if excess > 0 else '红利落后'} |")
    else:
        rows.append("| 红利60日相对超额 | N/A | 数据获取失败 |")

    return "\n".join(rows) + "\n"


def main() -> int:
    paths.ensure_dirs()
    today = today_cn()

    try:
        trading_day = is_trading_day(today)
    except Exception as e:
        print(f"[T1] 交易日历判定失败({e})，兜底视为交易日继续", file=sys.stderr)
        trading_day = True
    if not trading_day:
        print(f"[T1] {today} 非交易日，跳过")
        return 0

    cfg = get_config()
    positions = trade_log.current_positions()

    alerts: List[Dict[str, Any]] = []
    alerts.extend(check_c_bucket_drawdown(positions, cfg))
    alerts.extend(check_stop_loss(positions, cfg))
    alerts.extend(check_concentration(positions, cfg))
    alerts.extend(check_portfolio_drawdown(cfg))

    # 写台账
    yaml_tag = get_yaml_tag()
    for a in alerts:
        if a.get("level") in ("P0", "P1"):
            signal_log.append_signal({
                "触发日期": today.isoformat(),
                "yaml_version_at_trigger": yaml_tag,
                "触发任务": "T1",
                "桶": a.get("bucket", ""),
                "规则ID": a.get("rule_id", ""),
                "标的代码": a.get("target", "").split(" ")[0],
                "标的名称": " ".join(a.get("target", "").split(" ")[1:]),
                "触发时指标值": a.get("current", ""),
                "阈值": a.get("threshold", ""),
                "信号方向": "卖出" if "止损" in a.get("action", "") or "清仓" in a.get("action", "") or "减仓" in a.get("action", "") else "观察",
                "建议动作": a.get("action", ""),
                "是否实际执行": "否",
            })

    # 市场概况
    print("[T1] 拉取市场概况数据...")
    market_overview = _fetch_market_overview()

    # 持仓快照
    snapshot_rows = []
    for _, row in positions.iterrows():
        snapshot_rows.append({
            "代码": row["代码"],
            "名称": row.get("名称", ""),
            "桶": row.get("桶", ""),
            "净股数": f"{float(row['净股数']):.0f}",
            "平均成本": f"{float(row['平均成本'] or 0):.2f}",
        })

    weights = trade_log.bucket_weights()
    weights_md = " · ".join(f"{k}={v * 100:.1f}%" for k, v in weights.items())

    # 组装报告
    sections = [
        ("市场概况", market_overview),
        ("四桶权重", f"`{weights_md}`\n\n"
                    f"持仓标的数：**{len(positions)}**\n"),
        ("持仓明细", report.render_kv_table(snapshot_rows, ["代码", "名称", "桶", "净股数", "平均成本"])
                    if snapshot_rows else "_当前空仓_\n"),
        ("风控检查结论",
         f"- C 桶回撤/MA60：{'无持仓' if positions.empty or not any(str(r.get('桶','')).upper()=='C' for _,r in positions.iterrows()) else '已检查'}\n"
         f"- 止损线（B<-25%, C<-15%）：{'无持仓' if positions.empty else '已检查'}\n"
         f"- 集中度（单票/行业）：{'无持仓' if positions.empty else '已检查'}\n"
         f"- 组合级回撤：{'待净值接入' if True else '已检查'}\n"),
        ("yaml 版本", f"`{yaml_tag}`\n"),
    ]

    path = report.write_report(
        task="T1",
        title=f"T1 每日风控扫描 · {today.isoformat()}",
        alerts=alerts,
        sections=sections,
    )
    print(f"[T1] 报告已写入 {path}，触发 {len(alerts)} 项")

    # 推送：有 P0 走 P0 通道，有 P1 走 P1，其余仅归档
    has_p0 = any(a.get("level") == "P0" for a in alerts)
    has_p1 = any(a.get("level") == "P1" for a in alerts)
    if has_p0 or has_p1:
        level = "P0" if has_p0 else "P1"
        notifier.notify(path.read_text(encoding="utf-8"),
                        title=f"T1 每日风控 · {today}",
                        level=level)
    return 0


if __name__ == "__main__":
    sys.exit(main())
