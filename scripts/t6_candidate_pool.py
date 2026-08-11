"""T6 候选池静态筛选：硬门槛 + 排序值计算 → data/skill_input_T6.md。

三桶各自的筛选逻辑：
- A 桶（红利逆向）：中证红利成分 → 股息率/PB/ROE 过滤 → quality_score 排序
- B 桶（成长）：中证1000成分 → 市值/CAGR/增速/ROE/现金流/PEG 过滤 → 1/PEG 排序
- C 桶（热点周期）：T4 文本判定 PASS → 数据验证（单季扣非 + 合同负债 + 价格指数）

产出：data/skill_input_T6.md（LLM 消费用，格式对齐 skills/t6_semantic_ranking.md）

用法：
    python scripts/t6_candidate_pool.py               # 全部三桶
    python scripts/t6_candidate_pool.py --bucket A    # 只跑 A 桶
    python scripts/t6_candidate_pool.py --dry-run     # 只打印不写文件
"""
from __future__ import annotations

import argparse
import datetime as dt
import os
import sys
from io import StringIO
from pathlib import Path
from typing import Any, Dict, List, Optional

import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib.paths import DATA_DIR, ensure_dirs
from lib.config import get_config, get_yaml_tag
from lib.data_fetch import (
    get_csi_dividend_constituents,
    get_csi1000_constituents,
    get_batch_fundamentals,
    get_profit_quality_snapshot,
    get_growth_snapshot,
    get_yjbb_snapshot,
    get_dividend_yield_percentile,
    get_index_daily,
)

SKILL_INPUT = DATA_DIR / "skill_input_T6.md"
T4_OUTPUT = DATA_DIR / "skill_output_T4C.md"


# ============================================================
# A 桶 · 红利逆向
# ============================================================

def screen_bucket_a() -> pd.DataFrame:
    """A 桶硬门槛筛选。

    条件（来自 02_strategy_config.yaml bucket_A.stock_filters）：
    - 中证红利指数成分股
    - 股息率 TTM ≥ 3%
    - PB < 2.0
    - ROE 5 年均值 ≥ 10%
    - 连续分红 ≥ 3 年

    排序值 = 股息率 × quality_score
    quality_score = roe_5y_avg × 0.4 + fcf_coverage × 0.3 + dividend_years × 0.3 / 10
    """
    print("[T6-A] 拉取中证红利成分股...")
    cons = get_csi_dividend_constituents()
    if cons.empty:
        print("[T6-A] ⚠️ 成分股数据为空，跳过 A 桶", file=sys.stderr)
        return pd.DataFrame()

    # 加载配置阈值（容错：如果配置不存在用默认值）
    try:
        cfg = get_config().get("bucket_A", {}).get("stock_filters", {})
    except Exception:
        cfg = {}
    min_dy = cfg.get("dividend_yield_ttm_min_pct", 3.0)
    max_pb = cfg.get("pb_max", 2.0)
    min_roe = cfg.get("roe_5y_avg_min_pct", 10.0)
    min_div_years = cfg.get("dividend_continuous_years_min", 3)

    results: List[Dict[str, Any]] = []
    total = len(cons)

    # 批量一次拉取全部成分股基本面（防限频：绝不逐票请求）
    print(f"[T6-A] 批量拉取 {total} 只成分股基本面（单次调用）...")
    fund_df = get_batch_fundamentals(cons["code"].tolist())
    if fund_df.empty:
        print("[T6-A] ⚠️ 基本面数据为空，跳过 A 桶", file=sys.stderr)
        return pd.DataFrame()

    # 盈利质量快照（近3年单季亏损次数 + 年报经营现金流，批量一次）
    print("[T6-A] 拉取盈利质量快照（亏损季度/现金流，批量）...")
    pq_df = get_profit_quality_snapshot()
    pq_df = pq_df.set_index("code") if not pq_df.empty else pd.DataFrame()

    for i, row in cons.iterrows():
        code = str(row["code"])
        name = str(row.get("name", ""))

        if code not in fund_df.index:
            continue
        fund_row = fund_df.loc[code]

        # 提取指标（列名兼容 tushare/腾讯两种来源）
        dy_ttm = _safe_float(fund_row, ["dv_ttm", "dv_ratio", "股息率"])
        pb = _safe_float(fund_row, ["pb", "市净率"])
        pe = _safe_float(fund_row, ["pe_ttm", "市盈率"])
        roe = _safe_float(fund_row, ["roe", "ROE"])  # 快照源通常无 ROE → None
        price = _safe_float(fund_row, ["price", "现价"])

        # 硬门槛（数据缺失时放行：成分股本身已由指数做过股息筛选）
        if dy_ttm is not None and dy_ttm < min_dy:
            continue
        if pb is not None and pb > max_pb:
            continue
        if roe is not None and roe < min_roe:
            continue

        # 盈利质量门槛：近3年出现过单季亏损 → 剔除；年报经营现金流为负 → 剔除（借钱分红）
        loss_q = None
        ocf_ps = None
        if not pq_df.empty and code in pq_df.index:
            loss_q = _safe_float(pq_df.loc[code], ["loss_q_3y"])
            ocf_ps = _safe_float(pq_df.loc[code], ["ocf_ps_annual"])
        if loss_q is not None and loss_q > 0:
            continue
        if ocf_ps is not None and ocf_ps < 0:
            continue

        # quality_score 近似计算
        roe_score = min(roe or 10.0, 30.0) / 30.0  # 归一化到 [0,1]
        fcf_coverage = 1.0  # 简化：无 FCF 数据时默认 1
        div_years = 5  # 简化：成分股默认至少 3 年（实际需要接口）
        quality_score = roe_score * 0.4 + fcf_coverage * 0.3 + (div_years / 10) * 0.3

        # PB 分位（简化：使用当前 PB / 历史中位 PB 的逆）
        pb_percentile = min(100, max(0, (2.0 - (pb or 1.5)) / 2.0 * 100)) if pb else 50.0

        # 排序值：有股息率用 股息率×质量分，否则退化为 质量分
        sort_value = (dy_ttm * quality_score) if dy_ttm else quality_score

        # 入选理由（每道门槛的实际值 vs 阈值，缺数据时明示放行）
        reason_parts = []
        reason_parts.append(f"股息率{dy_ttm:.2f}%≥{min_dy}%" if dy_ttm is not None else "股息率缺失放行")
        reason_parts.append(f"PB {pb:.2f}≤{max_pb}" if pb is not None else "PB缺失放行")
        reason_parts.append(f"ROE年化{roe:.1f}%≥{min_roe}%" if roe is not None else "ROE缺失放行")
        reason_parts.append(f"近3年亏损季度{int(loss_q)}" if loss_q is not None else "亏损数据缺失放行")
        reason_parts.append(f"年报经营现金流/股{ocf_ps:.2f}≥0" if ocf_ps is not None else "现金流数据缺失放行")

        results.append({
            "code": code,
            "name": name,
            "industry": "",  # 后续可补充
            "price": round(price, 2) if price else "",
            "dividend_yield_ttm": round(dy_ttm, 2) if dy_ttm else "",
            "dividend_percentile_5y": "",  # 需要历史数据
            "roe_5y_avg": round(roe, 1) if roe else "",
            "fcf_coverage": round(fcf_coverage, 2),
            "pb": round(pb, 2) if pb else "",
            "pb_percentile": round(pb_percentile, 1),
            "dividend_years": div_years,
            "loss_q_3y": int(loss_q) if loss_q is not None else "",
            "ocf_ps_annual": round(ocf_ps, 2) if ocf_ps is not None else "",
            "quality_score": round(quality_score, 3),
            "sort_value": round(sort_value, 3),
            "pick_reason": " | ".join(reason_parts),
        })

        # 进度
        if (i + 1) % 20 == 0:
            print(f"  已处理 {i + 1}/{total}，通过 {len(results)} 只")

    print(f"[T6-A] 完成：{len(results)} 只通过硬门槛（共扫 {total} 只）")
    df = pd.DataFrame(results)
    if not df.empty:
        df = df.sort_values("sort_value", ascending=False).reset_index(drop=True)
    return df


# ============================================================
# B 桶 · 成长
# ============================================================

def screen_bucket_b() -> pd.DataFrame:
    """B 桶硬门槛筛选（中证1000 中盘成长股）。

    候选池：中证1000（000852）全部成分股，剔除 ST/退。
    硬门槛（阈值读 config bucket_B.batch_screen，默认值如下）：
    - 总市值 ≥ 50 亿（统一亿元口径）
    - 净利 3 年 CAGR ≥ 20%（2022→2025 年报首末期水平，基期/末期净利均须为正）
    - 营收 3 年 CAGR ≥ 15%
    - 最新报告期净利同比 ≥ 15%（成长未熄火）
    - ROE 年化 ≥ 8%
    - 近 3 年无单季亏损
    - 最新年报 经营现金流/净利润 ≥ 0.5 且每股经营现金流 > 0（成长有现金支撑）
    - 0 < PE(TTM) ≤ 60
    - PEG ≤ 1.2（PEG = PE ÷ min(净利CAGR, 100)，封顶防低基数虚高）
    核心数据缺失 → 剔除（不放行），缺哪一项在统计中打印。
    排序值 = min(净利CAGR, 100) / PE（即 1/PEG）降序。

    批量口径：成分/业绩/现金流全部来自全市场快照 merge，绝不逐票请求。
    非批量指标（商誉、应收、研发、渗透率、PE 历史分位）留 LLM 层验证。
    """
    print("[T6-B] 中证1000 中盘成长筛选...")

    cons = get_csi1000_constituents()
    if cons.empty:
        print("[T6-B] [WARN] 中证1000 成分数据为空，跳过 B 桶", file=sys.stderr)
        return pd.DataFrame()

    # ST/退市风险票剔除（中证1000 编制规则本就排除，这里双保险）
    before = len(cons)
    cons = cons[~cons["name"].astype(str).str.contains("ST|退", regex=True)]
    print(f"[T6-B] 成分 {before} 只，剔 ST/退 后 {len(cons)} 只")

    # 配置阈值（batch_screen 优先，缺省回退 quality_filters/默认值）
    try:
        bcfg = get_config().get("bucket_B", {})
    except Exception:
        bcfg = {}
    bs = bcfg.get("batch_screen", {})
    qf = bcfg.get("quality_filters", {})
    min_mv = bs.get("total_mv_min_yi", 50.0)
    min_np_cagr = bs.get("np_cagr_3y_min_pct",
                         qf.get("recurring_profit_cagr_3y_min_pct", 20.0))
    min_rev_cagr = bs.get("rev_cagr_3y_min_pct",
                          qf.get("revenue_cagr_3y_min_pct", 15.0))
    min_np_yoy = bs.get("latest_np_yoy_min_pct", 15.0)
    min_roe = bs.get("roe_annualized_min_pct", 8.0)
    min_ocf_ratio = bs.get("ocf_to_np_annual_min", 0.5)
    max_pe = bs.get("pe_ttm_max", 60.0)
    max_peg = bs.get("peg_max", 1.2)
    require_no_loss = bs.get("no_loss_quarters_3y", True)

    # 批量数据源（各自全市场一次，进程内缓存）
    print("[T6-B] 拉取 3 年成长快照（4 个年报期全市场业绩报表）...")
    growth_df = get_growth_snapshot()
    growth_idx = growth_df.set_index("code") if not growth_df.empty else pd.DataFrame()
    if growth_idx.empty:
        print("[T6-B] [WARN] 成长快照为空，跳过 B 桶", file=sys.stderr)
        return pd.DataFrame()

    print("[T6-B] 拉取最新报告期业绩快照（净利同比/行业）...")
    yjbb_df = get_yjbb_snapshot()
    yjbb_idx = yjbb_df.set_index("code") if not yjbb_df.empty else pd.DataFrame()

    print("[T6-B] 拉取盈利质量快照（亏损季度/现金流，批量）...")
    pq_df = get_profit_quality_snapshot()
    pq_idx = pq_df.set_index("code") if not pq_df.empty else pd.DataFrame()

    codes = cons["code"].astype(str).tolist()
    print(f"[T6-B] 批量拉取 {len(codes)} 只成分股基本面（PE/ROE/市值/现价）...")
    fund_df = get_batch_fundamentals(codes)
    if fund_df.empty:
        print("[T6-B] [WARN] 基本面数据为空，跳过 B 桶", file=sys.stderr)
        return pd.DataFrame()

    results: List[Dict[str, Any]] = []
    dropped = {"基本面缺失": 0, "市值不足": 0, "净利CAGR": 0, "营收CAGR": 0,
               "最新期增速": 0, "ROE": 0, "亏损季度": 0, "现金流": 0,
               "PE": 0, "PEG": 0}
    total = len(cons)

    for i, row in cons.iterrows():
        code = str(row["code"])
        name = str(row.get("name", ""))

        if code not in fund_df.index:
            dropped["基本面缺失"] += 1
            continue
        fund_row = fund_df.loc[code]
        pe = _safe_float(fund_row, ["pe_ttm", "市盈率"])
        roe = _safe_float(fund_row, ["roe", "ROE"])
        total_mv = _safe_float(fund_row, ["total_mv", "总市值"])  # 亿元
        price = _safe_float(fund_row, ["price", "现价"])

        # 市值门槛（缺市值视为不满足：中盘定位是硬约束）
        if total_mv is None or total_mv < min_mv:
            dropped["市值不足"] += 1
            continue

        # 成长门槛：CAGR 缺失（基期为负/缺披露）直接剔除，不放行
        g = growth_idx.loc[code] if code in growth_idx.index else None
        np_cagr = _safe_float(g, ["np_cagr_3y"]) if g is not None else None
        rev_cagr = _safe_float(g, ["rev_cagr_3y"]) if g is not None else None
        ocf_ratio = _safe_float(g, ["ocf_np_ratio"]) if g is not None else None
        ocf_ps_a = _safe_float(g, ["ocf_ps_annual"]) if g is not None else None
        if np_cagr is None or np_cagr < min_np_cagr:
            dropped["净利CAGR"] += 1
            continue
        if rev_cagr is None or rev_cagr < min_rev_cagr:
            dropped["营收CAGR"] += 1
            continue

        # 最新报告期净利同比（成长未熄火）；快照缺该票则剔除
        np_yoy = None
        industry = ""
        if not yjbb_idx.empty and code in yjbb_idx.index:
            np_yoy = _safe_float(yjbb_idx.loc[code], ["np_yoy"])
            industry = str(yjbb_idx.loc[code].get("industry", "") or "")
        if np_yoy is None or np_yoy < min_np_yoy:
            dropped["最新期增速"] += 1
            continue

        # ROE 年化（已由 _attach_roe 年化；缺失剔除）
        if roe is None or roe < min_roe:
            dropped["ROE"] += 1
            continue

        # 盈利质量：近 3 年单季亏损（no_loss_quarters_3y=false 时跳过此门槛）
        loss_q = None
        if not pq_idx.empty and code in pq_idx.index:
            loss_q = _safe_float(pq_idx.loc[code], ["loss_q_3y"])
        if require_no_loss and (loss_q is None or loss_q > 0):
            dropped["亏损季度"] += 1
            continue

        # 现金流：年报 OCF/NP 与每股 OCF
        if ocf_ratio is None or ocf_ratio < min_ocf_ratio \
                or ocf_ps_a is None or ocf_ps_a <= 0:
            dropped["现金流"] += 1
            continue

        # 估值：PE 区间 + PEG
        if pe is None or pe <= 0 or pe > max_pe:
            dropped["PE"] += 1
            continue
        cagr_capped = min(np_cagr, 100.0)
        peg = pe / cagr_capped
        if peg > max_peg:
            dropped["PEG"] += 1
            continue

        sort_val = cagr_capped / pe  # = 1/PEG
        reason = (
            f"总市值{total_mv:.0f}亿≥{min_mv:.0f} | "
            f"净利CAGR3年+{np_cagr:.0f}%≥{min_np_cagr:.0f}% | "
            f"营收CAGR3年+{rev_cagr:.0f}%≥{min_rev_cagr:.0f}% | "
            f"最新期净利同比+{np_yoy:.0f}%≥{min_np_yoy:.0f}% | "
            f"ROE年化{roe:.1f}%≥{min_roe:.0f}% | "
            f"近3年亏损季度0 | "
            f"年报OCF/NP {ocf_ratio:.2f}≥{min_ocf_ratio} | "
            f"PE(TTM){pe:.1f}≤{max_pe:.0f} | PEG {peg:.2f}≤{max_peg}"
        )

        results.append({
            "code": code,
            "name": name,
            "industry": industry,
            "price": round(price, 2) if price is not None else "",
            "total_mv_yi": round(total_mv, 0),
            "profit_cagr_3y": round(np_cagr, 1),
            "revenue_cagr_3y": round(rev_cagr, 1),
            "np_yoy_latest": round(np_yoy, 1),
            "roe_ann": round(roe, 1),
            "ocf_to_np": round(ocf_ratio, 2),
            "loss_q_3y": int(loss_q),
            "pe_ttm": round(pe, 1),
            "peg": round(peg, 2),
            "sort_value": round(sort_val, 3),
            "pick_reason": reason,
        })

    print(f"[T6-B] 完成：{len(results)} 只通过硬门槛（共扫 {total} 只）")
    drop_note = "、".join(f"{k}{v}" for k, v in dropped.items() if v)
    if drop_note:
        print(f"[T6-B] 剔除分布: {drop_note}")
    df = pd.DataFrame(results)
    if not df.empty:
        df = df.sort_values("sort_value", ascending=False).reset_index(drop=True)
    return df


# ============================================================
# C 桶 · 热点周期
# ============================================================

def screen_bucket_c() -> pd.DataFrame:
    """C 桶候选：从 T4 文本判定输出中读取 PASS 条目，补充财务指标并按增速排序。

    C 桶逻辑：文本信号只做初筛（≥1类命中即可），排序由净利润同比增速决定。
    数据验证维度：净利润同比增速 YoY（主排序值）、营收同比、毛利率、价格是否高于 MA60。
    """
    print("[T6-C] 从 T4 输出读取文本判定 PASS 标的...")

    if not T4_OUTPUT.exists():
        print("[T6-C] ⚠️ data/skill_output_T4C.md 不存在，跳过 C 桶", file=sys.stderr)
        print("[T6-C]   请先运行 T4 流程（scripts/t4_ingest.py --prepare + LLM）", file=sys.stderr)
        return pd.DataFrame()

    # 复用 t4_ingest 的解析逻辑
    from t4_ingest import parse_llm_output
    items = parse_llm_output(T4_OUTPUT)
    passed = [item for item in items if str(item.get("verdict", "")).upper() == "PASS"]

    if not passed:
        print("[T6-C] T4 输出中无 PASS 条目")
        return pd.DataFrame()

    # 拉取业绩报表快照，用于补充 np_yoy / revenue_yoy / gross_margin
    print("[T6-C] 拉取业绩报表快照（净利润同比/营收/毛利率）...")
    yjbb_df = get_yjbb_snapshot()
    yjbb_idx = yjbb_df.set_index("code") if not yjbb_df.empty else pd.DataFrame()

    # 拉取基本面（PE_TTM），用于计算 PEG
    # 用 get_batch_fundamentals 而非 get_fundamentals_snapshot，因为前者会降级到腾讯批量行情
    print("[T6-C] 拉取基本面快照（PE_TTM → PEG）...")
    all_codes = [str(item.get("stock_code", "")) for item in passed]
    fund_idx = get_batch_fundamentals(all_codes)
    if isinstance(fund_idx.index, pd.MultiIndex):
        fund_idx = fund_idx.reset_index().drop_duplicates("code").set_index("code")

    results: List[Dict[str, Any]] = []
    for item in passed:
        code = str(item.get("stock_code", ""))
        name = str(item.get("stock_name", ""))
        industry = str(item.get("industry", ""))
        text_score = float(item.get("weighted_score", 0))
        cats = item.get("categories_hit", {})
        cats_count = sum(len(v) if isinstance(v, list) else 0 for v in cats.values())

        # 从业绩报表快照补充财务指标
        np_yoy = None
        revenue_yoy = None
        gross_margin = None
        if not yjbb_idx.empty and code in yjbb_idx.index:
            row = yjbb_idx.loc[code]
            np_yoy = _safe_float(row, ["np_yoy"])
            revenue_yoy = _safe_float(row, ["rev_yoy"])
            gross_margin = _safe_float(row, ["gross_margin"])

        # 从基本面快照补充 PE_TTM
        pe_ttm = None
        if not fund_idx.empty and code in fund_idx.index:
            pe_ttm = _safe_float(fund_idx.loc[code], ["pe_ttm"])

        # PEG = PE_TTM / 净利润同比增速（增速用百分比数字，如 50% → 50）
        peg = None
        if pe_ttm is not None and pe_ttm > 0 and np_yoy is not None and np_yoy > 0:
            peg = round(pe_ttm / np_yoy, 2)

        # 价格是否高于 MA60
        price_above_ma60 = _check_price_above_ma(code)

        # 主排序值：净利润同比增速，封顶 500% 防极端值
        sort_val = min(np_yoy, 500.0) if np_yoy is not None else 0.0

        results.append({
            "code": code,
            "name": name,
            "industry": industry,
            "text_score": round(text_score, 2),
            "categories_hit_count": cats_count,
            "np_yoy": round(np_yoy, 1) if np_yoy is not None else "",
            "revenue_yoy": round(revenue_yoy, 1) if revenue_yoy is not None else "",
            "gross_margin": round(gross_margin, 1) if gross_margin is not None else "",
            "pe_ttm": round(pe_ttm, 1) if pe_ttm is not None else "",
            "peg": peg if peg is not None else "",
            "price_index_1y_high": "",  # 需要行业指数
            "contract_liability_yoy": "",  # 需要财报
            "price_above_ma60": "是" if price_above_ma60 else "否",
            "sort_value": round(sort_val, 3),
        })

    print(f"[T6-C] 完成：{len(results)} 只来自 T4 PASS")
    df = pd.DataFrame(results)
    if not df.empty:
        df = df.sort_values("sort_value", ascending=False).reset_index(drop=True)
    return df


def _check_price_above_ma(code: str, ma_window: int = 60) -> bool:
    """检查个股当前价格是否在 MA60 上方。"""
    try:
        from lib.data_fetch import get_stock_daily
        end = dt.date.today()
        start = end - dt.timedelta(days=ma_window * 3)
        df = get_stock_daily(code, start.strftime("%Y-%m-%d"), end.strftime("%Y-%m-%d"))
        if df.empty or len(df) < ma_window:
            return False
        df = df.sort_values("date")
        ma = df["close"].tail(ma_window).mean()
        current = float(df["close"].iloc[-1])
        return current > ma
    except Exception:
        return False


# ============================================================
# 工具函数
# ============================================================

def _safe_float(row: Any, candidates: List[str]) -> Optional[float]:
    """从 DataFrame 行中安全提取浮点值，兼容多种列名。"""
    for col in candidates:
        if col in row.index:
            try:
                val = float(row[col])
                if pd.notna(val):
                    return val
            except (ValueError, TypeError):
                continue
    return None


# ============================================================
# 组装输出
# ============================================================

def _rules_note_a(df: pd.DataFrame) -> str:
    """A 桶筛选规则与排序公式说明（阈值读配置，与筛选逻辑同源）。"""
    try:
        cfg = get_config().get("bucket_A", {}).get("stock_filters", {})
    except Exception:
        cfg = {}
    lines = [
        f"筛选规则: 中证红利成分 + 股息率TTM≥{cfg.get('dividend_yield_ttm_min_pct', 3.0)}% "
        f"+ PB≤{cfg.get('pb_max', 2.0)} + ROE≥{cfg.get('roe_5y_avg_min_pct', 10.0)}%"
        f"（ROE 为最新报告期年化近似，非5年均值）"
        f" + 近3年无单季亏损 + 最新年报每股经营现金流≥0（自由现金流近似，剔除借钱分红）"
        f"；数据缺失时放行，见 pick_reason",
        "排序公式: sort_value = 股息率TTM × quality_score（quality_score 含 ROE 权重）",
    ]
    if not df.empty and "roe_5y_avg" in df.columns \
            and df["roe_5y_avg"].astype(str).str.strip().eq("").all():
        lines.append("注: 本次数据源无 ROE，quality_score 为同一默认值 → 排序实际按股息率降序")
    return "\n".join(lines)


def _rules_note_b(df: pd.DataFrame) -> str:
    """B 桶筛选规则与排序公式说明（阈值读配置，与筛选逻辑同源）。"""
    try:
        bcfg = get_config().get("bucket_B", {})
    except Exception:
        bcfg = {}
    bs = bcfg.get("batch_screen", {})
    qf = bcfg.get("quality_filters", {})
    min_mv = bs.get("total_mv_min_yi", 50.0)
    min_np_cagr = bs.get("np_cagr_3y_min_pct",
                         qf.get("recurring_profit_cagr_3y_min_pct", 20.0))
    min_rev_cagr = bs.get("rev_cagr_3y_min_pct",
                          qf.get("revenue_cagr_3y_min_pct", 15.0))
    min_np_yoy = bs.get("latest_np_yoy_min_pct", 15.0)
    min_roe = bs.get("roe_annualized_min_pct", 8.0)
    min_ocf = bs.get("ocf_to_np_annual_min", 0.5)
    max_pe = bs.get("pe_ttm_max", 60.0)
    max_peg = bs.get("peg_max", 1.2)
    lines = [
        f"筛选规则: 中证1000成分(剔ST/退) + 总市值≥{min_mv:.0f}亿"
        f" + 净利3年CAGR≥{min_np_cagr:.0f}%(年报首末期,基期须为正)"
        f" + 营收3年CAGR≥{min_rev_cagr:.0f}%"
        f" + 最新报告期净利同比≥{min_np_yoy:.0f}%"
        f" + ROE年化≥{min_roe:.0f}%"
        f" + 近3年无单季亏损"
        f" + 最新年报OCF/NP≥{min_ocf}且每股OCF>0"
        f" + 0<PE(TTM)≤{max_pe:.0f} + PEG≤{max_peg}"
        f"（PEG=PE÷min(净利CAGR,100%)）；核心数据缺失即剔除，每只票见 pick_reason",
        "排序公式: sort_value = min(净利CAGR,100)/PE（即 1/PEG）降序",
        "批量层未覆盖、需 LLM/人工复核: 商誉/净资产、应收vs营收增速、研发占比、"
        "行业渗透率、PE 上市以来分位",
    ]
    return "\n".join(lines)


def assemble_output(bucket_a: pd.DataFrame, bucket_b: pd.DataFrame,
                    bucket_c: pd.DataFrame) -> str:
    """组装成 skills/t6_semantic_ranking.md 定义的输入格式。"""
    parts: List[str] = []
    yaml_tag = get_yaml_tag()

    # A 桶
    parts.append("=== BUCKET: A ===")
    parts.append(_rules_note_a(bucket_a))
    if bucket_a.empty:
        parts.append("（A 桶候选为空）")
    else:
        cols_a = ["code", "name", "industry", "price", "dividend_yield_ttm", "dividend_percentile_5y",
                  "roe_5y_avg", "fcf_coverage", "pb", "pb_percentile", "dividend_years",
                  "loss_q_3y", "ocf_ps_annual", "quality_score",
                  "sort_value", "pick_reason"]
        # 只取存在的列
        available = [c for c in cols_a if c in bucket_a.columns]
        parts.append(bucket_a[available].head(50).to_csv(index=False))
    parts.append("")

    # B 桶
    parts.append("=== BUCKET: B ===")
    parts.append(_rules_note_b(bucket_b))
    if bucket_b.empty:
        parts.append("（B 桶候选为空）")
    else:
        cols_b = ["code", "name", "industry", "price", "total_mv_yi",
                  "profit_cagr_3y", "revenue_cagr_3y", "np_yoy_latest",
                  "roe_ann", "ocf_to_np", "loss_q_3y", "pe_ttm", "peg",
                  "sort_value", "pick_reason"]
        available = [c for c in cols_b if c in bucket_b.columns]
        parts.append(bucket_b[available].head(50).to_csv(index=False))
    parts.append("")

    # C 桶
    parts.append("=== BUCKET: C ===")
    if bucket_c.empty:
        parts.append("（C 桶候选为空）")
    else:
        cols_c = ["code", "name", "industry", "text_score", "categories_hit_count",
                  "np_yoy", "revenue_yoy", "gross_margin",
                  "pe_ttm", "peg",
                  "price_index_1y_high", "contract_liability_yoy", "price_above_ma60"]
        available = [c for c in cols_c if c in bucket_c.columns]
        parts.append(bucket_c[available].to_csv(index=False))
    parts.append("")

    parts.append(f"=== YAML_TAG: {yaml_tag} ===")
    return "\n".join(parts)


# ============================================================
# CLI
# ============================================================

def main() -> int:
    parser = argparse.ArgumentParser(description="T6 候选池静态筛选")
    parser.add_argument("--bucket", type=str, default="ABC",
                        help="指定桶（A/B/C 组合，默认 ABC 全跑）")
    parser.add_argument("--dry-run", action="store_true",
                        help="只打印结果，不写输出文件")
    parser.add_argument("--top", type=int, default=200,
                        help="每桶输出 top N 候选（默认 200，不限制死）")
    args = parser.parse_args()

    ensure_dirs()
    buckets = args.bucket.upper()

    bucket_a = pd.DataFrame()
    bucket_b = pd.DataFrame()
    bucket_c = pd.DataFrame()

    if "A" in buckets:
        bucket_a = screen_bucket_a()
        if not bucket_a.empty:
            bucket_a = bucket_a.head(args.top)
            print(f"\n[T6] A 桶 Top {len(bucket_a)} 候选就绪")

    if "B" in buckets:
        bucket_b = screen_bucket_b()
        if not bucket_b.empty:
            bucket_b = bucket_b.head(args.top)
            print(f"\n[T6] B 桶 Top {len(bucket_b)} 候选就绪")

    if "C" in buckets:
        bucket_c = screen_bucket_c()
        if not bucket_c.empty:
            print(f"\n[T6] C 桶 {len(bucket_c)} 候选就绪（不截断）")

    output = assemble_output(bucket_a, bucket_b, bucket_c)

    if args.dry_run:
        print("\n" + "=" * 50)
        print("[dry-run] 输出预览（前 2000 字符）：")
        print(output[:2000])
        return 0

    DATA_DIR.mkdir(parents=True, exist_ok=True)
    SKILL_INPUT.write_text(output, encoding="utf-8")
    print(f"\n[T6] 输入文件已生成：{SKILL_INPUT}")
    print(f"[T6] 文件大小：{SKILL_INPUT.stat().st_size:,} bytes")
    print(f"[T6] 请将内容喂给 LLM（参考 skills/t6_semantic_ranking.md）")
    print(f"[T6] LLM 产出写回 data/skill_output_T6.md")

    # 同时输出 CSV 以便直接查看
    if not bucket_a.empty:
        csv_a = DATA_DIR / "candidates_A.csv"
        bucket_a.to_csv(csv_a, index=False, encoding="utf-8")
        print(f"  → {csv_a}")
    if not bucket_b.empty:
        csv_b = DATA_DIR / "candidates_B.csv"
        bucket_b.to_csv(csv_b, index=False, encoding="utf-8")
        print(f"  → {csv_b}")
    if not bucket_c.empty:
        csv_c = DATA_DIR / "candidates_C.csv"
        bucket_c.to_csv(csv_c, index=False, encoding="utf-8")
        print(f"  → {csv_c}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
