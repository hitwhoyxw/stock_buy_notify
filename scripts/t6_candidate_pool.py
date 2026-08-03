"""T6 候选池静态筛选：硬门槛 + 排序值计算 → data/skill_input_T6.md。

三桶各自的筛选逻辑：
- A 桶（红利逆向）：中证红利成分 → 股息率/PB/ROE 过滤 → quality_score 排序
- B 桶（成长）：创业板指成分或全 A 高增长 → 增速/PEG/现金流过滤 → 成长因子排序
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
    get_batch_fundamentals,
    get_profit_quality_snapshot,
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
    """B 桶硬门槛筛选。

    条件：
    - 营收 CAGR 3年 ≥ 15%
    - 净利 CAGR 3年 ≥ 20%
    - PEG ≤ 1.5
    - 经营现金流/净利润 ≥ 0.5
    - 商誉/净资产 ≤ 30%

    排序值 = (revenue_cagr × profit_cagr) / PEG
    
    注意：由于 akshare 免费接口无法直接拉 CAGR/PEG 等，这里使用简化逻辑。
    完整版需要 tushare pro 接口或其他付费源。
    """
    print("[T6-B] B 桶成长筛选（简化版：基于创业板指成分 + 基本面）...")

    # 使用创业板指成分作为候选池（399006 是深证指数，用 index_stock_cons）
    cons = pd.DataFrame()
    try:
        import akshare as ak
        raw = ak.index_stock_cons(symbol="399006")
        if raw is not None and not raw.empty:
            cons = raw
    except Exception as e:
        print(f"[T6-B] ⚠️ 创业板指成分拉取失败：{e}", file=sys.stderr)

    if cons.empty:
        print("[T6-B] ⚠️ 创业板指成分为空", file=sys.stderr)
        return pd.DataFrame()

    col_code = next((c for c in cons.columns if "代码" in c and "指数" not in c), None)
    col_name = next((c for c in cons.columns if "名称" in c and "指数" not in c and "英文" not in c), None)
    if col_code is None:
        print(f"[T6-B] ⚠️ 成分列名解析失败：{list(cons.columns)}", file=sys.stderr)
        return pd.DataFrame()

    codes = [str(c).zfill(6) for c in cons[col_code].tolist()]
    name_map = {str(cons.iloc[i][col_code]).zfill(6): str(cons.iloc[i][col_name])
                for i in range(len(cons))} if col_name else {}
    total = len(codes)

    # 批量一次拉取基本面（防限频：绝不逐票请求）
    print(f"[T6-B] 批量拉取 {total} 只成分股基本面（单次调用）...")
    fund_df = get_batch_fundamentals(codes)
    if fund_df.empty:
        print("[T6-B] ⚠️ 基本面数据为空，跳过 B 桶", file=sys.stderr)
        return pd.DataFrame()

    results: List[Dict[str, Any]] = []
    for i, code in enumerate(codes):
        name = name_map.get(code, "")
        if code not in fund_df.index:
            continue
        fund_row = fund_df.loc[code]

        pe = _safe_float(fund_row, ["pe_ttm", "市盈率"])
        roe = _safe_float(fund_row, ["roe", "ROE"])  # 快照源通常无 ROE → None
        total_mv = _safe_float(fund_row, ["total_mv", "总市值"])
        price = _safe_float(fund_row, ["price", "现价"])
        pb = _safe_float(fund_row, ["pb", "市净率"])

        # 简化筛选：PE 明显过高才剔除，缺失时放行留给 LLM 判定
        if pe is not None and (pe <= 0 or pe > 80):
            continue
        if roe is not None and roe < 8:
            continue

        # 排序值：有 PE 用 ROE/PE（近似 PEG 逆），否则用低 PB 代理（PB 越低越靠前）
        if pe is not None:
            sort_val = (roe or 15) / max(pe, 1)
            rank_basis = f"排序=ROE/PE近似"
        else:
            sort_val = 1.0 / max(pb or 3.0, 0.1) * 5  # 归一到相近量级
            rank_basis = f"PE缺失按低PB排序"

        # 入选理由（门槛实际值 vs 阈值，缺数据时明示放行）
        reason_parts = []
        reason_parts.append(f"PE(TTM) {pe:.1f}≤80" if pe is not None else "PE缺失放行(或亏损)")
        reason_parts.append(f"ROE年化{roe:.1f}%≥8%" if roe is not None else "ROE缺失放行")
        reason_parts.append(rank_basis)

        results.append({
            "code": code,
            "name": name,
            "industry": "",
            "price": round(price, 2) if price else "",
            "revenue_cagr_3y": "",  # 需要财报数据
            "profit_cagr_3y": "",
            "gross_margin_change": "",
            "ocf_to_np": "",
            "roe_ttm": round(roe, 1) if roe else "",
            "peg": round(pe / max(roe or 15, 1), 2) if pe else "",
            "penetration_rate": "",
            "goodwill_ratio": "",
            "sort_value": round(sort_val, 4),
            "pick_reason": " | ".join(reason_parts),
        })

        if (i + 1) % 50 == 0:
            print(f"  已处理 {i + 1}/{total}，通过 {len(results)} 只")

    print(f"[T6-B] 完成：{len(results)} 只通过硬门槛")
    df = pd.DataFrame(results)
    if not df.empty:
        df = df.sort_values("sort_value", ascending=False).reset_index(drop=True)
    return df


# ============================================================
# C 桶 · 热点周期
# ============================================================

def screen_bucket_c() -> pd.DataFrame:
    """C 桶候选：从 T4 文本判定输出中读取 PASS 条目，补充数据验证列。

    数据验证维度：
    - 单季扣非增速 YoY
    - 合同负债 YoY
    - 价格指数是否高于 MA60
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

    results: List[Dict[str, Any]] = []
    for item in passed:
        code = str(item.get("stock_code", ""))
        name = str(item.get("stock_name", ""))
        industry = str(item.get("industry", ""))
        text_score = float(item.get("weighted_score", 0))
        cats = item.get("categories_hit", {})
        cats_count = sum(len(v) if isinstance(v, list) else 0 for v in cats.values())

        # 数据验证（简化：只拉取能拿到的）
        # 价格是否高于 MA60
        price_above_ma60 = _check_price_above_ma(code)

        results.append({
            "code": code,
            "name": name,
            "industry": industry,
            "text_score": round(text_score, 2),
            "categories_hit_count": cats_count,
            "price_index_1y_high": "",  # 需要行业指数
            "gross_margin_qoq": "",     # 需要财报
            "contract_liability_yoy": "",  # 需要财报
            "earnings_yoy_recurring": "",  # 需要财报
            "price_above_ma60": "是" if price_above_ma60 else "否",
            "sort_value": round(text_score + cats_count * 0.5, 3),
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
    """B 桶筛选规则与排序公式说明。"""
    lines = [
        "筛选规则: 创业板指成分 + PE(TTM)≤80（负PE剔除；PE缺失放行，见 pick_reason）",
        "排序公式: 有PE → ROE/PE（近似PEG逆）；PE缺失 → 按低PB代理排序",
    ]
    if not df.empty and "roe_ttm" in df.columns \
            and df["roe_ttm"].astype(str).str.strip().eq("").all():
        lines.append("注: 本次数据源无 ROE，ROE 按默认值 15 代入 → 排序实际按 1/PE 降序")
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
        cols_b = ["code", "name", "industry", "price", "revenue_cagr_3y", "profit_cagr_3y",
                  "gross_margin_change", "ocf_to_np", "roe_ttm", "peg", "penetration_rate", "goodwill_ratio",
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
                  "price_index_1y_high", "gross_margin_qoq", "contract_liability_yoy", "earnings_yoy_recurring"]
        available = [c for c in cols_c if c in bucket_c.columns]
        parts.append(bucket_c[available].head(30).to_csv(index=False))
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
    parser.add_argument("--top", type=int, default=30,
                        help="每桶输出 top N 候选（默认 30）")
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
            bucket_c = bucket_c.head(args.top)
            print(f"\n[T6] C 桶 Top {len(bucket_c)} 候选就绪")

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
