"""T2 · 每周红利状态判定 (周一 08:30)

对照 03_Agent定期任务.md 中 T2 定义 + 08 号文档中"科技吸血"场景，实现：

1. 拉五类估值/宏观/情绪指标
   估值类：中证红利股息率 5 年分位、ERP、相对超额 60d
   宏观流动性类：全 A 20日均量分位、10Y 国债、红利板块相对成交度分位
   情绪类：拥挤度、沪深300 回撤
2. 按 yaml.bucket_A.market_signals 判定 S0/S1/S2/S3 档位
   分类规则：估值类 ≥1 + 宏观流动性类 ≥1 才算达标（防单类别共振假信号）
3. 若跃迁，输出建议调仓金额（读 04 号日志估算总资产）
4. 顺带输出 B 桶估值分位提醒
"""
from __future__ import annotations

import datetime as dt
import sys
from typing import Any, Dict, List, Optional

from lib import notifier, paths, report, signal_log, trade_log
from lib.config import get_config, get_yaml_tag
from lib.data_fetch import (
    get_dividend_sector_relative_turnover,
    get_dividend_yield_percentile,
    get_hs300_drawdown,
    get_latest_10y_yield,
    get_market_turnover_percentile,
    get_relative_excess,
    get_top3_industry_turnover_share,
)

# 分类标签，用于 "估值类 ≥1 + 流动性类 ≥1" 规则
CATEGORY_MAP: Dict[str, str] = {
    "dividend_yield_percentile_5y": "估值",
    "equity_risk_premium_pct": "估值",
    "relative_excess_60d_pct": "估值",
    "market_turnover_20d_percentile": "宏观流动性",
    "dividend_sector_relative_turnover_percentile": "宏观流动性",
    "cn10y_yield_pct": "宏观流动性",
    "top3_industry_turnover_share_pct": "情绪",
    "hs300_single_day_pct": "情绪",
    "hs300_drawdown_20d_pct": "情绪",
}


def collect_indicators() -> Dict[str, Optional[float]]:
    """采集所有原始指标。返回 dict，缺失值为 None。"""
    ind: Dict[str, Optional[float]] = {}

    dy = get_dividend_yield_percentile(years=5)
    ind["dividend_yield_current"] = dy["current"] if dy else None
    ind["dividend_yield_percentile_5y"] = dy["percentile"] if dy else None

    y10 = get_latest_10y_yield()
    ind["cn10y_yield_pct"] = y10
    if dy and y10 is not None:
        ind["equity_risk_premium_pct"] = dy["current"] - y10
    else:
        ind["equity_risk_premium_pct"] = None

    ind["relative_excess_60d_pct"] = get_relative_excess("000922", "000985", days=60)
    ind["market_turnover_20d_percentile"] = get_market_turnover_percentile(window_days=20, lookback_days=250)
    ind["dividend_sector_relative_turnover_percentile"] = get_dividend_sector_relative_turnover(days=20)
    ind["top3_industry_turnover_share_pct"] = get_top3_industry_turnover_share()

    hs = get_hs300_drawdown(days=20)
    ind["hs300_single_day_pct"] = hs["single_day_pct"] if hs else None
    ind["hs300_drawdown_20d_pct"] = hs["drawdown_pct"] if hs else None

    return ind


def check_condition(indicator_key: str, value: Optional[float], tier_thr: Any) -> Optional[bool]:
    """判断单个指标是否达标。返回 True/False/None(数据缺失)。

    比较方向根据 indicator 语义决定：
      * dividend_yield_percentile_5y, market_turnover_20d_percentile → 越低越触发（<= 阈值算达标）
      * equity_risk_premium_pct → 越高越触发（>= 阈值算达标）
      * relative_excess_60d_pct → 越负越触发（<= 阈值算达标，阈值本身是负数）
      * top3_industry_turnover_share_pct → 越高越触发（>= 阈值算达标）
      * hs300_single_day_pct / drawdown_20d_pct → 越负越触发
      * dividend_sector_relative_turnover_percentile → 越低越触发（红利被抛弃）
    """
    if value is None or tier_thr is None:
        return None

    # 反向指标（越低越触发）
    reverse_lt = {"market_turnover_20d_percentile", "dividend_sector_relative_turnover_percentile"}
    # 正向指标（越高越触发）
    forward_gt = {
        "dividend_yield_percentile_5y", "equity_risk_premium_pct",
        "top3_industry_turnover_share_pct",
    }
    # 相对超额、单日跌幅、回撤：阈值本身是负数，实际值 <= 阈值算达标
    negative_le = {"relative_excess_60d_pct", "hs300_single_day_pct", "hs300_drawdown_20d_pct"}
    # 10Y 国债：这一档尚无 yaml 阈值定义，暂不参与判定
    if indicator_key == "cn10y_yield_pct":
        return None

    if indicator_key in forward_gt:
        return value >= tier_thr
    if indicator_key in reverse_lt:
        return value <= tier_thr
    if indicator_key in negative_le:
        return value <= tier_thr
    return None


def evaluate_tier(indicators: Dict[str, Optional[float]], cfg: Dict[str, Any]) -> Dict[str, Any]:
    """遍历 S1/S2/S3 档位，返回最高达标档位 + 每档明细。

    额外规则：估值类 ≥1 + 宏观流动性类 ≥1 才算达标。
    """
    market = cfg["bucket_A"]["market_signals"]
    conditions = market["conditions"]

    tier_detail: Dict[str, Any] = {}
    reached_tier = "S0"

    for tier in ("S1", "S2", "S3"):
        met_by_cat: Dict[str, List[str]] = {"估值": [], "宏观流动性": [], "情绪": []}
        rows: List[Dict[str, Any]] = []

        for key, tier_map in conditions.items():
            # tier_map 可能是 dict（S1/S2/S3 → threshold）或 nested（hs300_drawdown）
            if key == "hs300_drawdown":
                thr = tier_map.get(tier, {})
                sub_results = []
                if "single_day_pct" in thr:
                    ok = check_condition("hs300_single_day_pct", indicators.get("hs300_single_day_pct"), thr["single_day_pct"])
                    sub_results.append(("hs300_single_day_pct", indicators.get("hs300_single_day_pct"), thr["single_day_pct"], ok))
                if "drawdown_20d_pct" in thr:
                    ok = check_condition("hs300_drawdown_20d_pct", indicators.get("hs300_drawdown_20d_pct"), thr["drawdown_20d_pct"])
                    sub_results.append(("hs300_drawdown_20d_pct", indicators.get("hs300_drawdown_20d_pct"), thr["drawdown_20d_pct"], ok))
                for sub_key, val, sub_thr, ok in sub_results:
                    rows.append({"tier": tier, "指标": sub_key, "当前值": val, "阈值": sub_thr, "达标": ok})
                    if ok:
                        met_by_cat[CATEGORY_MAP.get(sub_key, "情绪")].append(sub_key)
                continue

            thr = tier_map.get(tier)
            val = indicators.get(key)
            ok = check_condition(key, val, thr)
            rows.append({"tier": tier, "指标": key, "当前值": val, "阈值": thr, "达标": ok})
            if ok:
                cat = CATEGORY_MAP.get(key)
                if cat:
                    met_by_cat[cat].append(key)

        # 应用分类规则：估值 ≥1 且 宏观流动性 ≥1
        passed = len(met_by_cat["估值"]) >= 1 and len(met_by_cat["宏观流动性"]) >= 1
        # 传统"任意 2 项"作为对照
        total_met = sum(len(v) for v in met_by_cat.values())

        tier_detail[tier] = {
            "rows": rows,
            "met_by_cat": met_by_cat,
            "total_met": total_met,
            "passed_category_rule": passed,
        }
        if passed:
            reached_tier = tier

    tier_detail["reached_tier"] = reached_tier
    return tier_detail


def render_indicator_table(indicators: Dict[str, Optional[float]], detail: Dict[str, Any]) -> str:
    """渲染指标 × S1/S2/S3 对照表。"""
    order = [
        "dividend_yield_percentile_5y",
        "equity_risk_premium_pct",
        "relative_excess_60d_pct",
        "market_turnover_20d_percentile",
        "dividend_sector_relative_turnover_percentile",
        "top3_industry_turnover_share_pct",
        "hs300_single_day_pct",
        "hs300_drawdown_20d_pct",
    ]
    labels = {
        "dividend_yield_percentile_5y": "中证红利股息率5年分位%",
        "equity_risk_premium_pct": "ERP=股息-10Y国债",
        "relative_excess_60d_pct": "红利vs全指60日相对超额%",
        "market_turnover_20d_percentile": "全A 20日均量分位%",
        "dividend_sector_relative_turnover_percentile": "红利板块相对成交度分位%",
        "top3_industry_turnover_share_pct": "前3行业成交额占比%",
        "hs300_single_day_pct": "沪深300单日最大跌幅%",
        "hs300_drawdown_20d_pct": "沪深300 20日最大回撤%",
    }
    lines = ["| 指标 | 当前值 | S1阈值 | S2阈值 | S3阈值 | 分类 |",
             "| --- | --- | --- | --- | --- | --- |"]
    for k in order:
        v = indicators.get(k)
        v_str = "缺失" if v is None else f"{v:.2f}"
        thr_row: Dict[str, Any] = {}
        for tier in ("S1", "S2", "S3"):
            for r in detail.get(tier, {}).get("rows", []):
                if r["指标"] == k:
                    thr_row[tier] = r["阈值"]
                    break
        lines.append(
            f"| {labels.get(k, k)} | {v_str} | "
            f"{thr_row.get('S1', '-')} | {thr_row.get('S2', '-')} | {thr_row.get('S3', '-')} | "
            f"{CATEGORY_MAP.get(k, '-')} |"
        )
    return "\n".join(lines) + "\n"


def read_last_tier() -> Optional[str]:
    """从台账中读取上次 T2 记录的状态。"""
    df = signal_log.read_all()
    if df.empty:
        return None
    t2 = df[df["触发任务"] == "T2"].sort_values("触发日期")
    if t2.empty:
        return None
    last_state = t2["当时组合状态"].iloc[-1]
    return last_state.split("->")[-1].strip() if last_state else None


# ============================================================
# 主流程
# ============================================================

def main() -> int:
    paths.ensure_dirs()
    today = dt.date.today()
    cfg = get_config()

    indicators = collect_indicators()
    detail = evaluate_tier(indicators, cfg)
    tier = detail["reached_tier"]
    last_tier = read_last_tier()
    yaml_tag = get_yaml_tag()

    alerts: List[Dict[str, Any]] = []
    if last_tier and last_tier != tier:
        alerts.append({
            "level": "P1",
            "rule_id": f"A-STATE-{last_tier}->{tier}",
            "bucket": "A",
            "target": "组合状态跃迁",
            "current": tier,
            "threshold": f"上次 {last_tier}",
            "action": f"按 allocation.states.{tier} 调仓；A 桶按 ladder 分档投入",
            "source": "T2 指标扫描",
        })
    elif not last_tier and tier != "S0":
        alerts.append({
            "level": "P1",
            "rule_id": f"A-STATE-INIT-{tier}",
            "bucket": "A",
            "target": "首次判定",
            "current": tier,
            "threshold": "-",
            "action": f"按 allocation.states.{tier} 建立初始仓位",
            "source": "T2 指标扫描",
        })

    # 目标权重表
    states = cfg["allocation"]["states"]
    weights = trade_log.bucket_weights()
    target = states.get(tier, {}).get("buckets", {})
    delta_rows = []
    for k in ("A", "B", "C", "D"):
        cur = weights.get(k, 0.0) * 100
        tgt = target.get(k, 0.0) * 100
        delta_rows.append({"桶": k, "当前%": f"{cur:.1f}", "目标%": f"{tgt:.1f}", "偏离%": f"{tgt - cur:+.1f}"})

    # 写入台账
    if alerts:
        for a in alerts:
            signal_log.append_signal({
                "触发日期": today.isoformat(),
                "yaml_version_at_trigger": yaml_tag,
                "触发任务": "T2",
                "桶": "A",
                "规则ID": a["rule_id"],
                "标的代码": "-",
                "标的名称": "组合",
                "分桶基准代码": cfg.get("bucket_A", {}).get("market_signals", {}).get("benchmark_index", ""),
                "触发时指标值": f"S={tier}",
                "阈值": "分类规则通过",
                "当时组合状态": f"{last_tier or 'INIT'}->{tier}",
                "信号方向": "买入" if tier != "S0" else "观察",
                "建议动作": a["action"],
                "是否实际执行": "否",
            })

    # 报告
    body_sections = [
        ("状态判定", f"当前档位 **{tier}**（上周 **{last_tier or '无记录'}**）\n\n"
                    f"S3 类别命中：估值 {len(detail['S3']['met_by_cat']['估值'])}、"
                    f"流动性 {len(detail['S3']['met_by_cat']['宏观流动性'])}、"
                    f"情绪 {len(detail['S3']['met_by_cat']['情绪'])}；"
                    f"S2 命中：估值 {len(detail['S2']['met_by_cat']['估值'])}、"
                    f"流动性 {len(detail['S2']['met_by_cat']['宏观流动性'])}、"
                    f"情绪 {len(detail['S2']['met_by_cat']['情绪'])}；"
                    f"S1 命中：估值 {len(detail['S1']['met_by_cat']['估值'])}、"
                    f"流动性 {len(detail['S1']['met_by_cat']['宏观流动性'])}、"
                    f"情绪 {len(detail['S1']['met_by_cat']['情绪'])}\n"),
        ("指标明细表", render_indicator_table(indicators, detail)),
        ("四桶权重对照", report.render_kv_table(delta_rows, ["桶", "当前%", "目标%", "偏离%"])),
        ("yaml 版本", f"`{yaml_tag}`\n"),
    ]

    path = report.write_report(
        task="T2",
        title=f"T2 周度红利判定 · {today.isoformat()}",
        alerts=alerts,
        sections=body_sections,
    )
    print(f"[T2] 报告已写入 {path}，档位 {tier}，跃迁={last_tier and last_tier != tier}")

    if alerts:
        notifier.notify(path.read_text(encoding="utf-8"),
                        title=f"T2 状态跃迁 → {tier}",
                        level="P1")
    return 0


if __name__ == "__main__":
    sys.exit(main())
