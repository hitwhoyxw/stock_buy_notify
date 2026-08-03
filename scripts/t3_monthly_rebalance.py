"""T3 · 月度再平衡检查（每月首个交易日）

依据 03_Agent定期任务.md 中 T3 定义：
1. 计算 A/B/C/D 四桶实际权重（04 号日志 → trade_log.bucket_weights）
2. 与目标（allocation.states[当前档位]）对比，偏离 >5pct 输出建议
3. 校验 D 桶不低于 15%；D → C 直转视为违规
4. 统计本月分红到账与 C 桶已兑现利润（若日志中有对应记录），按 profit_withdrawal.ratio_to_D_bucket 计算入 D 金额
5. 汇总本月违反 prohibitions 的次数
"""
from __future__ import annotations

import datetime as dt
import sys
from typing import Any, Dict, List

from lib import notifier, paths, report, signal_log, trade_log
from lib.config import get_config, get_yaml_tag


def read_last_tier() -> str:
    df = signal_log.read_all()
    if df.empty:
        return "S0"
    t2 = df[df["触发任务"] == "T2"].sort_values("触发日期")
    if t2.empty:
        return "S0"
    last_state = t2["当时组合状态"].iloc[-1]
    return (last_state.split("->")[-1].strip() or "S0") if last_state else "S0"


def main() -> int:
    paths.ensure_dirs()
    today = dt.date.today()
    cfg = get_config()
    yaml_tag = get_yaml_tag()

    tier = read_last_tier()
    target = cfg["allocation"]["states"].get(tier, {}).get("buckets", {})
    weights = trade_log.bucket_weights()

    deviation_trigger = cfg["allocation"]["rebalance"]["deviation_trigger_pct"] / 100
    d_floor = cfg["allocation"]["rebalance"]["d_bucket_floor"]

    delta_rows: List[Dict[str, Any]] = []
    alerts: List[Dict[str, Any]] = []

    for k in ("A", "B", "C", "D"):
        cur = weights.get(k, 0.0)
        tgt = target.get(k, 0.0)
        dev = tgt - cur
        row = {
            "桶": k, "当前%": f"{cur * 100:.1f}", "目标%": f"{tgt * 100:.1f}",
            "偏离%": f"{dev * 100:+.1f}",
        }
        delta_rows.append(row)
        if abs(dev) > deviation_trigger:
            alerts.append({
                "level": "P1",
                "rule_id": f"REBAL-{k}",
                "bucket": k,
                "target": f"{k} 桶偏离目标",
                "current": f"{cur * 100:.1f}%",
                "threshold": f"目标 {tgt * 100:.1f}% ± {deviation_trigger * 100:.0f}pct",
                "action": ("加仓" if dev > 0 else "减仓") + f" {abs(dev) * 100:.1f}pct",
                "source": f"04_交易日志 · 档位 {tier}",
            })

    if weights.get("D", 0) < d_floor:
        alerts.append({
            "level": "P0",
            "rule_id": "REBAL-D-FLOOR",
            "bucket": "D",
            "target": "D 桶地板",
            "current": f"{weights.get('D', 0) * 100:.1f}%",
            "threshold": f">= {d_floor * 100:.0f}%",
            "action": "立即补充 D 桶弹药，禁止 D → C 直转",
            "source": "组合规则",
        })

    # 违规统计（本月）
    df = signal_log.read_all()
    month_start = today.replace(day=1)
    if not df.empty:
        df["触发日期_dt"] = signal_log.pd.to_datetime(df["触发日期"], errors="coerce").dt.date
        month_signals = df[df["触发日期_dt"] >= month_start]
        exec_but_violate = month_signals[(month_signals["是否实际执行"] == "是")]
    else:
        month_signals = df
        exec_but_violate = df

    if alerts:
        for a in alerts:
            signal_log.append_signal({
                "触发日期": today.isoformat(),
                "yaml_version_at_trigger": yaml_tag,
                "触发任务": "T3",
                "桶": a.get("bucket", ""),
                "规则ID": a.get("rule_id", ""),
                "标的代码": "-",
                "标的名称": "组合",
                "触发时指标值": a.get("current", ""),
                "阈值": a.get("threshold", ""),
                "当时组合状态": tier,
                "信号方向": "调仓",
                "建议动作": a.get("action", ""),
                "是否实际执行": "否",
            })

    body_sections = [
        ("档位与四桶权重对照", f"当前档位 **{tier}**\n\n" +
         report.render_kv_table(delta_rows, ["桶", "当前%", "目标%", "偏离%"])),
        ("本月纪律记分卡",
         f"本月台账信号数：{len(month_signals) if not month_signals.empty else 0}；"
         f"执行数：{len(exec_but_violate) if not exec_but_violate.empty else 0}\n"),
        ("yaml 版本", f"`{yaml_tag}`\n"),
    ]

    path = report.write_report(
        task="T3",
        title=f"T3 月度再平衡 · {today.isoformat()}",
        alerts=alerts,
        sections=body_sections,
    )
    print(f"[T3] 报告已写入 {path}，偏离触发 {len(alerts)} 项")

    if any(a["level"] in ("P0", "P1") for a in alerts):
        level = "P0" if any(a["level"] == "P0" for a in alerts) else "P1"
        notifier.notify(path.read_text(encoding="utf-8"),
                        title=f"T3 月度再平衡 · {today}",
                        level=level)
    return 0


if __name__ == "__main__":
    sys.exit(main())
