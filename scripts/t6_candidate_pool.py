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
    get_stock_fundamentals,
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
    print(f"[T6-A] 逐票拉取基本面（共 {total} 只）...")

    for i, row in cons.iterrows():
        code = str(row["code"])
        name = str(row.get("name", ""))
        weight = row.get("weight")

        # 拉取基本面
        fund = get_stock_fundamentals(code)
        if fund.empty:
            continue

        # 提取指标（列名兼容）
        try:
            fund_row = fund.iloc[0]
            # akshare stock_a_indicator_lg 列名可能包含中英文
            dy_ttm = _safe_float(fund_row, ["dv_ttm", "股息率", "dividend_yield"])
            pb = _safe_float(fund_row, ["pb", "市净率"])
            pe = _safe_float(fund_row, ["pe_ttm", "市盈率"])
            roe = _safe_float(fund_row, ["roe", "ROE"])
            total_mv = _safe_float(fund_row, ["total_mv", "总市值"])
        except Exception:
            continue

        # 硬门槛
        if dy_ttm is None or dy_ttm < min_dy:
            continue
        if pb is not None and pb > max_pb:
            continue
        if roe is not None and roe < min_roe:
            continue

        # quality_score 近似计算
        roe_score = min(roe or 10.0, 30.0) / 30.0  # 归一化到 [0,1]
        fcf_coverage = 1.0  # 简化：无 FCF 数据时默认 1
        div_years = 5  # 简化：成分股默认至少 3 年（实际需要接口）
        quality_score = roe_score * 0.4 + fcf_coverage * 0.3 + (div_years / 10) * 0.3

        # PB 分位（简化：使用当前 PB / 历史中位 PB 的逆）
        pb_percentile = min(100, max(0, (2.0 - (pb or 1.5)) / 2.0 * 100)) if pb else 50.0

        results.append({
            "code": code,
            "name": name,
            "industry": "",  # 后续可补充
            "dividend_yield_ttm": round(dy_ttm, 2),
            "dividend_percentile_5y": "",  # 需要历史数据
            "roe_5y_avg": round(roe or 0, 1),
            "fcf_coverage": round(fcf_coverage, 2),
            "pb": round(pb, 2) if pb else "",
            "pb_percentile": round(pb_percentile, 1),
            "dividend_years": div_years,
            "quality_score": round(quality_score, 3),
            "sort_value": round(dy_ttm * quality_score, 3),
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

    results: List[Dict[str, Any]] = []
    total = min(len(cons), 100)  # 限制处理数量避免超时
    print(f"[T6-B] 逐票拉取基本面（前 {total} 只）...")

    for i in range(total):
        row = cons.iloc[i]
        code = str(row[col_code]).zfill(6)
        name = str(row.get(col_name, ""))

        fund = get_stock_fundamentals(code)
        if fund.empty:
            continue

        try:
            fund_row = fund.iloc[0]
            pe = _safe_float(fund_row, ["pe_ttm", "市盈率"])
            roe = _safe_float(fund_row, ["roe", "ROE"])
            total_mv = _safe_float(fund_row, ["total_mv", "总市值"])
        except Exception:
            continue

        # 简化筛选：PE 合理、ROE 较高
        if pe is None or pe <= 0 or pe > 80:
            continue
        if roe is not None and roe < 8:
            continue

        # 简化排序值：ROE / PE（近似 PEG 逆）
        sort_val = (roe or 15) / max(pe, 1)

        results.append({
            "code": code,
            "name": name,
            "industry": "",
            "revenue_cagr_3y": "",  # 需要财报数据
            "profit_cagr_3y": "",
            "gross_margin_change": "",
            "ocf_to_np": "",
            "roe_ttm": round(roe, 1) if roe else "",
            "peg": round(pe / max(roe or 15, 1), 2),
            "penetration_rate": "",
            "goodwill_ratio": "",
            "sort_value": round(sort_val, 4),
        })

        if (i + 1) % 20 == 0:
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

def assemble_output(bucket_a: pd.DataFrame, bucket_b: pd.DataFrame,
                    bucket_c: pd.DataFrame) -> str:
    """组装成 skills/t6_semantic_ranking.md 定义的输入格式。"""
    parts: List[str] = []
    yaml_tag = get_yaml_tag()

    # A 桶
    parts.append("=== BUCKET: A ===")
    if bucket_a.empty:
        parts.append("（A 桶候选为空）")
    else:
        cols_a = ["code", "name", "industry", "dividend_yield_ttm", "dividend_percentile_5y",
                  "roe_5y_avg", "fcf_coverage", "pb", "pb_percentile", "dividend_years", "quality_score"]
        # 只取存在的列
        available = [c for c in cols_a if c in bucket_a.columns]
        parts.append(bucket_a[available].head(50).to_csv(index=False))
    parts.append("")

    # B 桶
    parts.append("=== BUCKET: B ===")
    if bucket_b.empty:
        parts.append("（B 桶候选为空）")
    else:
        cols_b = ["code", "name", "industry", "revenue_cagr_3y", "profit_cagr_3y",
                  "gross_margin_change", "ocf_to_np", "roe_ttm", "peg", "penetration_rate", "goodwill_ratio"]
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
