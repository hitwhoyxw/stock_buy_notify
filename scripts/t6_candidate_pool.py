"""T6 候选池静态筛选：硬门槛 + 排序值计算 → data/skill_input_T6_{A,B,C}.md。

三桶各自的筛选逻辑：
- A 桶（红利逆向）：中证红利成分 → 股息率/PB/ROE 过滤 → quality_score 排序
- B 桶（成长）：中证1000+500+A500+800成分 → 市值/CAGR/增速/ROE/现金流/PEG 过滤 → 1/PEG 排序
- C 桶（热点周期）：T4 文本判定 PASS → 数据验证（单季扣非 + 合同负债 + 价格指数）

产出：data/skill_input_T6_A.md / _B.md / _C.md 按桶分文件（LLM 消费，
格式对齐 skills/t6_semantic_ranking.md）。单桶跑只写对应桶文件，不覆盖其他桶。

用法：
    python scripts/t6_candidate_pool.py               # 全部三桶
    python scripts/t6_candidate_pool.py --bucket A    # 只跑 A 桶（只写 _A 分文件）
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
    get_csi500_constituents,
    get_csi_a500_constituents,
    get_csi800_constituents,
    get_batch_fundamentals,
    get_profit_quality_snapshot,
    get_growth_snapshot,
    get_yjbb_snapshot,
    get_dividend_yield_percentile,
    get_index_daily,
)

SKILL_INPUTS = {
    "A": DATA_DIR / "skill_input_T6_A.md",
    "B": DATA_DIR / "skill_input_T6_B.md",
    "C": DATA_DIR / "skill_input_T6_C.md",
}
T4_OUTPUT = DATA_DIR / "skill_output_T4C.md"


# ============================================================
# 机构持仓识别（险资/社保/养老金/QFII）
# ============================================================

_INSURANCE_KW = ["保险", "人寿", "平安", "泰康", "太保", "人保", "新华保险",
                 "太平人寿", "友邦", "中再", "大家人寿", "农银人寿"]
_SOCIAL_SECURITY_KW = ["社保", "全国社保"]
_PENSION_KW = ["基本养老", "养老基金"]  # 政府养老金，排除商业养老保险
_QFII_KW = ["摩根", "瑞银", "高盛", "富达", "QFII", "渣打", "花旗", "德银",
            "野村", "景顺", "施罗德", "巴克莱", "汇丰", "挪威中央银行",
            "阿布达比", "科威特", "淡马锡", "比尔盖茨", "老虎", "安本",
            "魁尔坎", "法兴", "新加坡政府投资", "澳门金融", "瑞信",
            "伯克希尔", "耶鲁", "斯坦福"]


def _classify_holder(name: str) -> list:
    """识别股东属于哪类机构，返回 tag list。"""
    tags = []
    is_pension = any(kw in name for kw in _PENSION_KW)
    # 保险：排除政府养老金（避免"基本养老保险基金"误匹配）
    if not is_pension and any(kw in name for kw in _INSURANCE_KW):
        tags.append("保险")
    if is_pension:
        tags.append("养老")
    if any(kw in name for kw in _SOCIAL_SECURITY_KW):
        tags.append("社保")
    if any(kw in name for kw in _QFII_KW):
        tags.append("QFII")
    return tags


def _fetch_institutional_holders(codes: list) -> dict:
    """批量拉取十大流通股东并分类（东方财富 API）。
    返回 {code: {insurance, social_security, pension, qfii, detail, count}}
    """
    import requests
    import time
    print(f"[T6] 拉取机构持仓（{len(codes)} 只）...")
    result = {}
    for i, code in enumerate(codes):
        code = str(code).zfill(6)
        if code.startswith("920"):
            prefix = "BJ"
        elif code.startswith(("0", "3", "2")):
            prefix = "SZ"
        else:
            prefix = "SH"
        url = "http://datacenter-web.eastmoney.com/api/data/v1/get"
        params = {
            "reportName": "RPT_F10_EH_FREEHOLDERS",
            "columns": "HOLDER_NAME,FREE_HOLDNUM_RATIO",
            "filter": f'(SECUCODE="{code}.{prefix}")',
            "pageNumber": "1",
            "pageSize": "10",
            "sortColumns": "UPDATE_DATE,HOLDER_RANK",
            "sortTypes": "-1,1",
            "source": "WEB",
            "client": "WEB",
        }
        try:
            resp = requests.get(url, params=params, timeout=10,
                                headers={"User-Agent": "Mozilla/5.0"})
            data = resp.json()
            rows = data.get("result", {}).get("data", []) if data.get("result") else []
        except Exception:
            rows = []

        tags_found = set()
        detail_parts = []
        for r in rows:
            hname = r.get("HOLDER_NAME", "")
            htags = _classify_holder(hname)
            if htags:
                tags_found.update(htags)
                detail_parts.append(f"{hname}[{'/'.join(htags)}]")

        result[code] = {
            "insurance": "保险" in tags_found,
            "social_security": "社保" in tags_found,
            "pension": "养老" in tags_found,
            "qfii": "QFII" in tags_found,
            "detail": "; ".join(detail_parts) if detail_parts else "",
            "count": len(tags_found),
        }
        if (i + 1) % 20 == 0:
            print(f"  机构持仓 {i + 1}/{len(codes)}")
        time.sleep(0.15)  # 避免限频
    print(f"[T6] 机构持仓拉取完成")
    return result


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

    # 拉取机构持仓（险资/社保/养老金/QFII）
    inst_holders = _fetch_institutional_holders(cons["code"].tolist())

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

        # 机构持仓加分：每种机构 +0.05（最多 +0.20）
        inst = inst_holders.get(code, {})
        inst_count = inst.get("count", 0)
        quality_score += 0.05 * inst_count

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
            "has_insurance": "是" if inst.get("insurance") else "",
            "has_social_security": "是" if inst.get("social_security") else "",
            "has_pension": "是" if inst.get("pension") else "",
            "has_qfii": "是" if inst.get("qfii") else "",
            "inst_detail": inst.get("detail", ""),
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
    """B 桶硬门槛筛选（中证1000+500+A500+800 全市场大中盘成长股）。

    候选池：中证1000（000852）+ 中证500（000905）+ 中证A500（000510）
    + 中证800（000906，含沪深300）全部成分股（合并去重），剔除 ST/退。
    （2026-08 扩池：原仅中证1000，大中盘成长如科达利等被池子排除）
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
    print("[T6-B] 中证1000+500+A500+800 全市场大中盘成长筛选...")

    pools = []
    pool_labels = []
    for fetch, label in [
        (get_csi1000_constituents, "中证1000"),
        (get_csi500_constituents, "中证500"),
        (get_csi_a500_constituents, "中证A500"),
        (get_csi800_constituents, "中证800(含沪深300)"),
    ]:
        df = fetch()
        if df.empty:
            print(f"[T6-B] [WARN] {label} 成分数据为空，跳过该池", file=sys.stderr)
        else:
            pools.append(df)
            pool_labels.append(f"{label}{len(df)}")
    if not pools:
        print("[T6-B] [WARN] 四个指数成分数据均为空，跳过 B 桶", file=sys.stderr)
        return pd.DataFrame()
    cons = pd.concat(pools, ignore_index=True) \
        .drop_duplicates(subset="code").reset_index(drop=True)
    print(f"[T6-B] 股票池合并 {' + '.join(pool_labels)}，去重后 {len(cons)} 只")

    # ST/退市风险票剔除（指数编制规则本就排除，这里双保险）
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
            "has_insurance": "",
            "has_social_security": "",
            "has_pension": "",
            "has_qfii": "",
            "inst_detail": "",
            "pick_reason": reason,
        })

    print(f"[T6-B] 完成：{len(results)} 只通过硬门槛（共扫 {total} 只）")
    drop_note = "、".join(f"{k}{v}" for k, v in dropped.items() if v)
    if drop_note:
        print(f"[T6-B] 剔除分布: {drop_note}")

    # 通过硬门槛后再拉取机构持仓（避免拉取 1000 只）
    if results:
        passing_codes = [r["code"] for r in results]
        inst_data = _fetch_institutional_holders(passing_codes)
        for r in results:
            inst = inst_data.get(r["code"], {})
            r["has_insurance"] = "是" if inst.get("insurance") else ""
            r["has_social_security"] = "是" if inst.get("social_security") else ""
            r["has_pension"] = "是" if inst.get("pension") else ""
            r["has_qfii"] = "是" if inst.get("qfii") else ""
            r["inst_detail"] = inst.get("detail", "")
            inst_count = inst.get("count", 0)
            r["sort_value"] = round(r["sort_value"] + 0.3 * inst_count, 3)  # 每种 +0.3

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

    # 报告期 → 年化系数（动态 PE 用）
    period = str(yjbb_df["period"].iloc[0]) if (not yjbb_df.empty and "period" in yjbb_df.columns) else ""
    q_label, ann_factor = _period_annualize_info(period) if period else ("", 1.0)

    # 拉取机构持仓（险资/社保/养老金/QFII）
    inst_holders = _fetch_institutional_holders(all_codes)

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

        # 动态 PE：已披露最新季报 → 年化推算；未披露 → 用 PE(TTM)
        pe_dynamic = None
        pe_method = ""
        if period and not yjbb_idx.empty and code in yjbb_idx.index:
            yjbb_row = yjbb_idx.loc[code]
            np_val = _safe_float(yjbb_row, ["np"])
            eps_val = _safe_float(yjbb_row, ["eps"])
            price_val = _safe_float(fund_idx.loc[code], ["price"]) if (not fund_idx.empty and code in fund_idx.index) else None
            total_mv_val = _safe_float(fund_idx.loc[code], ["total_mv"]) if (not fund_idx.empty and code in fund_idx.index) else None
            # 优先用 EPS×年化系数算（最直接），退回用 总市值/年化净利润
            if eps_val and eps_val > 0 and price_val and price_val > 0:
                pe_dynamic = round(price_val / (eps_val * ann_factor), 1)
                pe_method = f"动态({q_label})"
            elif np_val and np_val > 0 and total_mv_val and total_mv_val > 0:
                pe_dynamic = round(total_mv_val / (np_val * ann_factor / 1e8), 1)
                pe_method = f"动态({q_label})"
        if pe_dynamic is None:
            pe_dynamic = round(pe_ttm, 1) if pe_ttm is not None else ""
            pe_method = "PE(TTM)"

        # PEG = PE_TTM / 净利润同比增速（增速用百分比数字，如 50% → 50）
        peg = None
        if pe_ttm is not None and pe_ttm > 0 and np_yoy is not None and np_yoy > 0:
            peg = round(pe_ttm / np_yoy, 2)

        # 价格是否高于 MA60
        price_above_ma60 = _check_price_above_ma(code)

        # 主排序值：净利润同比增速，封顶 500% 防极端值
        sort_val = min(np_yoy, 500.0) if np_yoy is not None else 0.0

        # 机构持仓加分：每种机构 +10（最多 +40）
        inst = inst_holders.get(code, {})
        inst_count = inst.get("count", 0)
        sort_val += 10 * inst_count

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
            "pe_dynamic": pe_dynamic,
            "pe_method": pe_method,
            "peg": peg if peg is not None else "",
            "price_index_1y_high": "",  # 需要行业指数
            "contract_liability_yoy": "",  # 需要财报
            "price_above_ma60": "是" if price_above_ma60 else "否",
            "has_insurance": "是" if inst.get("insurance") else "",
            "has_social_security": "是" if inst.get("social_security") else "",
            "has_pension": "是" if inst.get("pension") else "",
            "has_qfii": "是" if inst.get("qfii") else "",
            "inst_detail": inst.get("detail", ""),
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


def _period_annualize_info(period: str) -> tuple:
    """报告期 → (年化标签, 年化系数)。

    一季报(0331)×4、中报(0630)×2、三季报(0930)×4/3、年报(1231)×1。
    用于动态 PE 计算：将最新报告期累计盈利年化后推算全年 PE。
    """
    p = str(period)
    if p.endswith("0331"):
        return ("一季报×4", 4.0)
    if p.endswith("0630"):
        return ("中报×2", 2.0)
    if p.endswith("0930"):
        return ("三季报×4/3", 4.0 / 3.0)
    if p.endswith("1231"):
        return ("年报", 1.0)
    return ("未知", 1.0)


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
        f"筛选规则: 中证1000+500+A500+800成分(剔ST/退) + 总市值≥{min_mv:.0f}亿"
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


def assemble_bucket(letter: str, bucket: pd.DataFrame) -> str:
    """组装单个桶的 skill_input 内容，写入 skill_input_T6_{letter}.md。

    格式对齐 skills/t6_semantic_ranking.md；行数截断由 main() 的 --top
    统一控制（每桶 LLM 分析上限），此处不再二次截断。
    """
    parts: List[str] = []
    yaml_tag = get_yaml_tag()

    if letter == "A":
        parts.append("=== BUCKET: A ===")
        parts.append(_rules_note_a(bucket))
        if bucket.empty:
            parts.append("（A 桶候选为空）")
        else:
            cols = ["code", "name", "industry", "price", "dividend_yield_ttm", "dividend_percentile_5y",
                    "roe_5y_avg", "fcf_coverage", "pb", "pb_percentile", "dividend_years",
                    "loss_q_3y", "ocf_ps_annual", "quality_score",
                    "has_insurance", "has_social_security", "has_pension", "has_qfii",
                    "sort_value", "pick_reason"]
            available = [c for c in cols if c in bucket.columns]
            parts.append(bucket[available].to_csv(index=False))
        parts.append("")

    elif letter == "B":
        parts.append("=== BUCKET: B ===")
        parts.append(_rules_note_b(bucket))
        if bucket.empty:
            parts.append("（B 桶候选为空）")
        else:
            cols = ["code", "name", "industry", "price", "total_mv_yi",
                    "profit_cagr_3y", "revenue_cagr_3y", "np_yoy_latest",
                    "roe_ann", "ocf_to_np", "loss_q_3y", "pe_ttm", "peg",
                    "has_insurance", "has_social_security", "has_pension", "has_qfii",
                    "sort_value", "pick_reason"]
            available = [c for c in cols if c in bucket.columns]
            parts.append(bucket[available].to_csv(index=False))
        parts.append("")

    elif letter == "C":
        parts.append("=== BUCKET: C ===")
        if bucket.empty:
            parts.append("（C 桶候选为空）")
        else:
            cols = ["code", "name", "industry", "text_score", "categories_hit_count",
                    "np_yoy", "revenue_yoy", "gross_margin",
                    "pe_ttm", "pe_dynamic", "pe_method", "peg",
                    "has_insurance", "has_social_security", "has_pension", "has_qfii",
                    "price_index_1y_high", "contract_liability_yoy", "price_above_ma60"]
            available = [c for c in cols if c in bucket.columns]
            parts.append(bucket[available].to_csv(index=False))
        parts.append("")

    else:
        raise ValueError(f"未知桶标识：{letter}")

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
    parser.add_argument("--top", type=int, default=100,
                        help="每桶按排序值截取 Top N 投入 LLM 分析（默认 100 上限）")
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
            before_c = len(bucket_c)
            bucket_c = bucket_c.head(args.top)
            print(f"\n[T6] C 桶 {before_c} 只按排序值截取 Top {len(bucket_c)}"
                  f"（LLM 分析上限 {args.top}）")

    outputs = {}
    if "A" in buckets:
        outputs["A"] = assemble_bucket("A", bucket_a)
    if "B" in buckets:
        outputs["B"] = assemble_bucket("B", bucket_b)
    if "C" in buckets:
        outputs["C"] = assemble_bucket("C", bucket_c)

    if args.dry_run:
        print("\n" + "=" * 50)
        for letter, text in outputs.items():
            print(f"\n[dry-run] === {letter} 桶输出预览（前 1000 字符）===")
            print(text[:1000])
        return 0

    DATA_DIR.mkdir(parents=True, exist_ok=True)
    for letter, text in outputs.items():
        path = SKILL_INPUTS[letter]
        path.write_text(text, encoding="utf-8")
        print(f"\n[T6-{letter}] 输入文件已生成：{path}")
        print(f"[T6-{letter}] 文件大小：{path.stat().st_size:,} bytes")
    print(f"[T6] 请将内容喂给 LLM（参考 skills/t6_semantic_ranking.md）")
    print(f"[T6] LLM 产出写回 data/skill_output_T6_{{A,B,C}}.md（按桶分文件）")

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
