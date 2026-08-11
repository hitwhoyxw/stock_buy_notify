"""T4 财报季 ingest：消费 LLM 文本判定输出 → 写入 07 号信号台账。

完整流程：
1. t4_c_input.py（本文件另一半）从 akshare 抓财报/纪要摘要 → data/skill_input_T4C.md
2. 人工 / CI 用 LLM 跑 skills/t4_c_text_scan.md → data/skill_output_T4C.md（JSON 数组）
3. 本脚本读 JSON → 过滤 PASS → 写 07 号台账信号 + 生成报告 + 推送

用法：
    # ingest（读 LLM 产出写台账）
    python scripts/t4_ingest.py

    # input 准备（自动发现扫描池：关键词命中 + 预告/报表高增长，无需人工填代码；
    # 报告头部附板块热度榜——同行业高增长家数，用于评估板块级景气）
    python scripts/t4_ingest.py --prepare

    # input 准备（手动指定股票，覆盖自动发现）
    python scripts/t4_ingest.py --prepare --codes 600028,601088,601225

CLI 参数：
    --prepare          : 运行输入准备阶段
    --codes            : 逗号分隔的股票代码（留空则自动发现扫描池）
    --top-n            : 自动发现扫描池上限（默认 300，仅作安全阀）
    --min-gain         : 预告/报表净利同比门槛（%，默认 50）
    --input-file       : 覆盖 LLM 输出路径（默认 data/skill_output_T4C.md）
    --dry-run          : 不写入台账，只打印
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib.paths import DATA_DIR, SKILLS_DIR, ensure_dirs
from lib.config import get_yaml_tag
from lib.signal_log import append_signal, init_if_missing
from lib.report import write_report
from lib.notifier import notify

SKILL_OUTPUT = DATA_DIR / "skill_output_T4C.md"
SKILL_INPUT = DATA_DIR / "skill_input_T4C.md"


# ============================================================
# Phase 1：输入准备（自动发现 + 关键词扫描 + 板块热度 → skill_input_T4C.md）
# ============================================================

def _load_text_signal_rules():
    """从 02_strategy_config.yaml 读 bucket_C.text_signal（唯一关键词来源）。

    返回 (categories, neg_keywords, penalty, min_score)：
      categories = {类别名: (权重, [关键词])}
    """
    from lib.config import get_config
    ts = get_config().get("bucket_C", {}).get("text_signal", {})
    cats: Dict[str, Any] = {}
    for cname, cdef in (ts.get("categories") or {}).items():
        cats[cname] = (float(cdef.get("weight", 1.0)),
                       list(cdef.get("keywords") or []))
    neg = ts.get("negative_keywords") or {}
    return (cats, list(neg.get("keywords") or []),
            float(neg.get("penalty", -3.0)), float(ts.get("min_weighted_score", 6.0)))


def scan_keyword_hits(yjyg_idx: "pd.DataFrame",
                      irm_texts: Dict[str, str] = None) -> Dict[str, Dict[str, Any]]:
    """对业绩预告文本 + 互动易问答文本做 bucket_C 关键词静态匹配。

    入参 yjyg_idx：以 code 为索引的预告快照（需含 reason/excerpt 列）。
    入参 irm_texts：{code: "Q: ... A: ..."} 互动易问答文本（可选）。
    返回 {code: {"hits": {类别: [关键词]}, "neg": [反向词], "score": 加权分, "sources": [文本来源]}}，
    仅含至少命中一个正向关键词的票。打分口径与 t4_c_text_scan skill 一致：
    sum(各类命中数×权重) + penalty×反向词数。
    """
    cats, neg_kws, penalty, _ = _load_text_signal_rules()
    out: Dict[str, Dict[str, Any]] = {}

    def _scan_text(text: str) -> tuple:
        """对单段文本做关键词匹配，返回 (hits, neg_hits, score)。"""
        hits: Dict[str, List[str]] = {}
        score = 0.0
        for cname, (w, kws) in cats.items():
            matched = [k for k in kws if k in text]
            if matched:
                hits[cname] = matched
                score += len(matched) * w
        neg_hits = [k for k in neg_kws if k in text]
        score += penalty * len(neg_hits)
        return hits, neg_hits, score

    # 源 1：业绩预告文本
    if yjyg_idx is not None and not yjyg_idx.empty and cats:
        for code, r in yjyg_idx.iterrows():
            text = (str(r.get("reason", "") or "") + "\n"
                    + str(r.get("excerpt", "") or ""))
            if len(text.strip()) <= 1:
                continue
            hits, neg_hits, score = _scan_text(text)
            if hits:
                out[code] = {"hits": hits, "neg": neg_hits,
                             "score": round(score, 1), "sources": ["预告"]}

    # 源 2：互动易问答文本
    if irm_texts:
        for code, text in irm_texts.items():
            if not text or len(text.strip()) <= 1:
                continue
            hits, neg_hits, score = _scan_text(text)
            if code in out:
                # 合并到已有结果（预告已命中）
                existing = out[code]
                for cname, matched in hits.items():
                    existing["hits"].setdefault(cname, [])
                    for k in matched:
                        if k not in existing["hits"][cname]:
                            existing["hits"][cname].append(k)
                existing["neg"] = list(set(existing["neg"] + neg_hits))
                # 重新计分
                new_score = 0.0
                for cname, (w, _) in cats.items():
                    new_score += len(existing["hits"].get(cname, [])) * w
                new_score += penalty * len(existing["neg"])
                existing["score"] = round(new_score, 1)
                if "互动易" not in existing["sources"]:
                    existing["sources"].append("互动易")
            elif hits:
                out[code] = {"hits": hits, "neg": neg_hits,
                             "score": round(score, 1), "sources": ["互动易"]}

    return out


def _clean_industry(v: Any) -> str:
    """东财行业带 Ⅱ/Ⅲ 后缀（申万二级/三级），聚合热度时去掉便于归并。"""
    s = str(v or "").strip()
    return s.rstrip("ⅡⅢ").strip() or "未分类"


def compute_sector_heat(yjbb_idx: "pd.DataFrame", min_gain: float = 50.0,
                        top: int = 10) -> "pd.DataFrame":
    """板块热度：全市场净利同比≥min_gain 的票按行业聚合。

    返回列：industry, count（高增长家数）, median_gain, top_codes（增速前3）。
    家数越多说明该板块业绩集体爆发，是 C 桶热点板块的核心证据。
    """
    if (yjbb_idx is None or yjbb_idx.empty
            or "np_yoy" not in yjbb_idx.columns or "industry" not in yjbb_idx.columns):
        return pd.DataFrame()
    hb = yjbb_idx.dropna(subset=["np_yoy"])
    hb = hb[hb["np_yoy"] >= min_gain]
    if hb.empty:
        return pd.DataFrame()
    ind = hb["industry"].map(_clean_industry)
    # 无行业归属的票（多为北交所/次新）不进热度榜，避免"未分类"霸榜
    keep = ind != "未分类"
    hb, ind = hb[keep], ind[keep]
    if hb.empty:
        return pd.DataFrame()
    heat = (pd.DataFrame({"industry": ind, "np_yoy": hb["np_yoy"]})
            .groupby("industry")["np_yoy"]
            .agg(count="size", median_gain="median")
            .reset_index()
            .sort_values(["count", "median_gain"], ascending=False))
    reps = {}
    for name in heat["industry"]:
        sub = hb[ind == name].sort_values("np_yoy", ascending=False)
        reps[name] = list(sub.index[:3])
    heat["top_codes"] = heat["industry"].map(reps)
    return heat.head(top).reset_index(drop=True)


def _build_pool_meta(pool: List[str], yjbb_idx: "pd.DataFrame",
                     yjyg_idx: "pd.DataFrame",
                     kw_map: Dict[str, Dict[str, Any]]) -> Dict[str, Dict[str, Any]]:
    """为池内每只票整理来源标签（关键词/预告增速/报表增速）、行业、名称。"""
    meta: Dict[str, Dict[str, Any]] = {}
    for code in pool:
        m: Dict[str, Any] = {"sources": [], "kw": kw_map.get(code),
                             "industry": "", "name": ""}
        if not yjyg_idx.empty and code in yjyg_idx.index:
            r = yjyg_idx.loc[code]
            m["name"] = str(r.get("name", "") or "")
            g = r.get("gain_pct")
            try:
                g = float(g)
                m["sources"].append(f"预告{g:+.0f}%")
            except (TypeError, ValueError):
                m["sources"].append(f"预告({r.get('preview_type', '?')})")
        if not yjbb_idx.empty and code in yjbb_idx.index:
            row = yjbb_idx.loc[code]
            g = row.get("np_yoy")
            try:
                m["sources"].append(f"报表净利{float(g):+.0f}%")
            except (TypeError, ValueError):
                pass
            m["industry"] = str(row.get("industry", "") or "")
        if m["kw"]:
            m["sources"].insert(0, "关键词")
        meta[code] = m
    return meta


def discover_scan_pool(top_n: int = 300, min_gain: float = 50.0):
    """自动发现 T4 扫描池（无需人工填写股票代码）。

    四个来源（全部来自全市场批量快照，约 3 次 HTTP 调用）：
      0. 赛道龙头源：yaml bucket_C.text_signal.sector_leaders 直接纳入；
      1. 关键词源：业绩预告"变动原因/变动"文本命中 bucket_C 文本信号关键词
         （需求/价格/供给），全部入池（不再截断）；
      2. 预告增速源：正面预告且利润变动幅度 ≥ min_gain%，按幅度降序；
      3. 报表增速源：净利同比 ≥ min_gain% 且营收正增长，补足剩余名额。
    返回 (pool, meta, heat)：
      pool  股票代码列表（≤top_n，top_n 默认 300 仅作安全阀）
      meta  {code: {sources, kw, industry, name}}
      heat  板块热度 DataFrame（全市场口径，供报告头部展示）
    """
    from lib.data_fetch import get_yjbb_snapshot, get_yjyg_snapshot

    yjyg = get_yjyg_snapshot()
    yjbb = get_yjbb_snapshot()
    yjyg_idx = yjyg.set_index("code") if not yjyg.empty else pd.DataFrame()
    yjbb_idx = yjbb.set_index("code") if not yjbb.empty else pd.DataFrame()

    heat = compute_sector_heat(yjbb_idx, min_gain)
    kw_map = scan_keyword_hits(yjyg_idx)

    pool: List[str] = []
    seen: set = set()

    def _add(code: str) -> None:
        if code not in seen:
            seen.add(code)
            pool.append(code)

    # 来源 0：赛道龙头强制纳入（不依赖增速排序，yaml 配置 sector_leaders）
    from lib.config import get_config
    leaders = (get_config().get("bucket_C", {})
               .get("text_signal", {}).get("sector_leaders") or [])
    for code in leaders:
        _add(str(code).strip().zfill(6))
    if leaders:
        print(f"[T4-discover] 赛道龙头源：yaml sector_leaders {len(leaders)} 只，入池 {len(pool)} 只")

    # 来源 1：关键词命中（按加权分、预告幅度降序）
    def _gain_of(code: str) -> float:
        if yjyg_idx.empty or code not in yjyg_idx.index:
            return 0.0
        g = yjyg_idx.loc[code].get("gain_pct")
        try:
            g = float(g)
            return g if g == g else 0.0  # NaN → 0
        except (TypeError, ValueError):
            return 0.0

    kw_items = sorted(kw_map.items(),
                      key=lambda kv: (kv[1]["score"], _gain_of(kv[0])),
                      reverse=True)
    for code, _ in kw_items:
        _add(code)
    print(f"[T4-discover] 关键词源：预告文本命中 bucket_C 关键词 {len(kw_map)} 只，全部入池")

    # 来源 2：预告增速（封顶 500% 防极端值挤占排序，不再截断数量）
    if not yjyg_idx.empty and "gain_pct" in yjyg_idx.columns:
        hit = yjyg_idx[yjyg_idx["gain_pct"].isna()
                       | (yjyg_idx["gain_pct"] >= min_gain)].copy()
        hit["_gain_capped"] = hit["gain_pct"].clip(upper=500.0)
        hit = hit.sort_values("_gain_capped", ascending=False, na_position="last")
        before = len(pool)
        for code in hit.index:
            if len(pool) >= top_n:
                break
            _add(code)
        print(f"[T4-discover] 预告增速源：变动幅度≥{min_gain:.0f}% 共 {len(hit)} 只，"
              f"入池 {len(pool) - before} 只（增速 clip 500%）")

    # 来源 3：报表增速补足（封顶 500% 防极端值，从已披露季报补充）
    if len(pool) < top_n and not yjbb_idx.empty and "np_yoy" in yjbb_idx.columns:
        hb = yjbb_idx.dropna(subset=["np_yoy"])
        rev_col = "rev_yoy" if "rev_yoy" in hb.columns else None
        hb = hb[(hb["np_yoy"] >= min_gain)
                & (hb[rev_col] > 0 if rev_col else True)].copy()
        hb["_np_capped"] = hb["np_yoy"].clip(upper=500.0)
        hb = hb.sort_values("_np_capped", ascending=False)
        before = len(pool)
        for code in hb.index:
            if len(pool) >= top_n:
                break
            _add(code)
        if len(pool) > before:
            print(f"[T4-discover] 报表增速源：补充 {len(pool) - before} 只"
                  f"（np_yoy≥{min_gain:.0f}% 且营收正增长，增速 clip 500%）")

    meta = _build_pool_meta(pool, yjbb_idx, yjyg_idx, kw_map)
    if pool:
        print(f"[T4-discover] 扫描池共 {len(pool)} 只：{','.join(pool)}")
    else:
        print("[T4-discover] [WARN] 所有数据源均为空，无法自动发现", file=sys.stderr)
    return pool, meta, heat


def prepare_input(codes: List[str]) -> Path:
    """为给定股票代码列表批量拉取财报关键数据，组装成 skill 需要的输入文件。

    防超时设计：批量快照（腾讯行情 + yjbb + yjyg）只 3-5 次全市场调用，
    互动易逐票拉取（每只约 2-3 秒，50 只约 2 分钟）。
    文件头部附板块热度榜（全市场净利同比≥50% 按行业聚合），
    每只票标注来源（关键词/预告/报表/互动易）与关键词命中详情。
    """
    from lib.data_fetch import (get_tencent_batch_quotes, get_yjbb_snapshot,
                                get_yjyg_snapshot, get_irm_texts)

    codes = [c.strip().split(".")[0].zfill(6) for c in codes if c.strip()]
    print(f"[T4-prepare] 批量拉取 {len(codes)} 只股票数据（行情+业绩报表+业绩预告）...")
    quotes = get_tencent_batch_quotes(codes)
    quotes = quotes.set_index("code") if not quotes.empty else pd.DataFrame()
    yjbb = get_yjbb_snapshot()
    yjbb_idx = yjbb.set_index("code") if not yjbb.empty else pd.DataFrame()
    yjyg = get_yjyg_snapshot()
    yjyg_idx = yjyg.set_index("code") if not yjyg.empty else pd.DataFrame()

    # 互动易问答文本（逐票拉取，约 2-3 秒/只）
    print(f"[T4-prepare] 拉取互动易问答文本（{len(codes)} 只，约 {len(codes)*3}s）...")
    irm_texts = get_irm_texts(codes, limit_per_stock=30)
    irm_hit_count = sum(1 for v in irm_texts.values() if v)
    print(f"[T4-prepare] 互动易：{irm_hit_count}/{len(codes)} 只有有效问答文本")

    period = str(yjbb["period"].iloc[0]) if (not yjbb.empty and "period" in yjbb.columns) else "未知"

    # 关键词命中（预告文本 + 互动易文本）+ 板块热度 + 池内来源标签
    kw_map = scan_keyword_hits(yjyg_idx, irm_texts)
    heat = compute_sector_heat(yjbb_idx, min_gain=50.0)
    meta = _build_pool_meta(codes, yjbb_idx, yjyg_idx, kw_map)

    header = _build_header(codes, meta, heat, kw_map, yjbb_idx, period)

    sections: List[str] = [header]
    for code in codes:
        m = meta.get(code, {})
        name = m.get("name", "")
        if not name and not quotes.empty and code in quotes.index:
            name = str(quotes.loc[code].get("name", "") or "")
        industry = m.get("industry", "")

        tags = " | 来源: " + "+".join(m.get("sources", [])) if m.get("sources") else ""
        kw_line = _format_kw_line(m.get("kw"))

        text_block = _build_report_text(code, yjbb_idx, yjyg_idx, irm_texts)
        if not text_block:
            text_block = f"（{name or code} 暂无可用财报数据，请手动补充）"

        sections.append(
            f"=== {code} · {name or '?'} · {industry or '未知'}{tags} ===\n"
            + (f"{kw_line}\n" if kw_line else "")
            + f"数据来源: 东财业绩报表/业绩预告 + 巨潮互动易（批量抓取）\n"
            f"报告期: {period}；生成日期: {dt.date.today().isoformat()}\n"
            f"------\n"
            f"{text_block}\n"
            f"------\n"
        )
        print(f"[T4-prepare] [OK] {code} {name}")

    DATA_DIR.mkdir(parents=True, exist_ok=True)
    content = "\n\n".join(sections)
    SKILL_INPUT.write_text(content, encoding="utf-8")
    print(f"\n[T4-prepare] 输入文件已生成：{SKILL_INPUT}")
    print(f"[T4-prepare] 共 {len(codes)} 只，请将内容喂给 LLM（参考 skills/t4_c_text_scan.md）")
    return SKILL_INPUT


def _format_kw_line(kw: Optional[Dict[str, Any]]) -> str:
    """把单票关键词命中渲染成一行：命中类别[词…]、加权分、反向词。"""
    if not kw:
        return ""
    parts = []
    for cname, kws in kw["hits"].items():
        parts.append(f"{cname}[{'、'.join(kws)}]")
    neg = "、".join(kw["neg"]) if kw["neg"] else "无"
    return (f"关键词命中: {' '.join(parts)}；加权分 {kw['score']}"
            f"（LLM 复核口径 ≥6.0 且三类齐全）；反向词: {neg}")


def _build_header(codes: List[str], meta: Dict[str, Dict[str, Any]],
                  heat: "pd.DataFrame", kw_map: Dict[str, Dict[str, Any]],
                  yjbb_idx: "pd.DataFrame", period: str) -> str:
    """报告头部：扫描池构成 + 板块热度榜。"""
    n_kw = sum(1 for c in codes if meta.get(c, {}).get("kw"))
    lines = [
        "# T4 财报季扫描输入（自动发现）",
        f"生成日期: {dt.date.today().isoformat()}；报告期: {period}；"
        f"扫描池 {len(codes)} 只（其中关键词命中 {n_kw} 只）",
        "",
        "关键词口径: 业绩预告『变动原因/变动』文本匹配 bucket_C.text_signal "
        "需求/价格/供给关键词；加权分=Σ(命中数×权重)−3×反向词数，最终判定以 LLM 复核为准。",
    ]

    if not heat.empty:
        total = 0
        n_ind = 0
        if not yjbb_idx.empty and "np_yoy" in yjbb_idx.columns:
            hb = yjbb_idx.dropna(subset=["np_yoy"])
            hb = hb[hb["np_yoy"] >= 50.0]
            total = len(hb)
            if "industry" in hb.columns:
                inds = hb["industry"].map(_clean_industry)
                n_ind = int(inds[inds != "未分类"].nunique())
        lines += [
            "",
            f"## 板块热度榜（全市场净利同比≥50% 共 {total} 只、"
            f"分布在 {n_ind} 个行业；按高增长家数排序，前 {len(heat)} 名）",
            "同一板块多家公司业绩集体爆发 = 板块级景气，是 C 桶热点判定的核心证据；"
            "单票不可买时可沿同板块寻找替代标的。",
            "",
        ]
        for i, r in heat.iterrows():
            reps = "/".join(r["top_codes"])
            lines.append(
                f"{i + 1}. {r['industry']} — 高增长 {int(r['count'])} 家 | "
                f"中位增速 {r['median_gain']:+.0f}% | 代表: {reps}"
            )
    return "\n".join(lines)


def _fmt_yi(v: Any) -> str:
    """金额（元）→ 亿元，保留 2 位；非数值返回空串。"""
    try:
        return f"{float(v) / 1e8:.2f}亿"
    except (ValueError, TypeError):
        return ""


def _build_report_text(code: str, yjbb: "pd.DataFrame", yjyg: "pd.DataFrame",
                      irm_texts: Dict[str, str] = None) -> str:
    """从批量快照中拼出单只票的财报要点文本（无网络调用）。

    文本源：业绩报表（结构化数字）+ 业绩预告（变动原因/描述）+ 互动易问答。
    """
    import math

    lines: List[str] = []
    if not yjbb.empty and code in yjbb.index:
        r = yjbb.loc[code]

        def _num(col):
            try:
                v = float(r.get(col))
                return v if not math.isnan(v) else None
            except (ValueError, TypeError):
                return None

        parts = []
        if (rev := _fmt_yi(_num("revenue"))):
            yoy = _num("rev_yoy")
            parts.append(f"营业收入 {rev}" + (f"（同比 {yoy:+.1f}%）" if yoy is not None else ""))
        if (np := _fmt_yi(_num("np"))):
            yoy = _num("np_yoy")
            parts.append(f"净利润 {np}" + (f"（同比 {yoy:+.1f}%）" if yoy is not None else ""))
        if (roe := _num("roe")) is not None:
            parts.append(f"ROE {roe:.2f}%")
        if (gm := _num("gross_margin")) is not None:
            parts.append(f"毛利率 {gm:.1f}%")
        if (ocf := _num("ocf_ps")) is not None:
            parts.append(f"每股经营现金流 {ocf:.2f}")
        if parts:
            lines.append("业绩报表: " + "；".join(parts))

    if not yjyg.empty and code in yjyg.index:
        r = yjyg.loc[code]
        excerpt = str(r.get("excerpt", "") or "")
        if excerpt:
            lines.append("业绩预告: " + excerpt)
        reason = str(r.get("reason", "") or "")
        if reason:
            lines.append("变动原因: " + reason)

    if irm_texts and code in irm_texts and irm_texts[code]:
        irm_text = irm_texts[code]
        # 截取前 2000 字防文件过大
        if len(irm_text) > 2000:
            irm_text = irm_text[:2000] + " ...(截断)"
        lines.append("互动易问答: " + irm_text)

    return "\n".join(lines)


# ============================================================
# Phase 2：Ingest（读 LLM 产出 → 写入 07 号台账）
# ============================================================

def parse_llm_output(path: Path) -> List[Dict[str, Any]]:
    """从 skill_output_T4C.md 中提取 JSON 数组。

    文件可能包含 markdown 围栏，需要跳过非 JSON 部分。
    """
    if not path.exists():
        print(f"[T4-ingest] LLM 输出文件不存在：{path}", file=sys.stderr)
        return []

    text = path.read_text(encoding="utf-8").strip()

    # 尝试直接解析
    try:
        data = json.loads(text)
        if isinstance(data, list):
            return data
    except json.JSONDecodeError:
        pass

    # 尝试从 markdown 代码块中提取
    pattern = r"```(?:json)?\s*\n(.*?)\n```"
    matches = re.findall(pattern, text, re.DOTALL)
    for match in matches:
        try:
            data = json.loads(match)
            if isinstance(data, list):
                return data
        except json.JSONDecodeError:
            continue

    # 尝试找到 [ 开头到 ] 结尾的最大区间
    start = text.find("[")
    end = text.rfind("]")
    if start >= 0 and end > start:
        try:
            data = json.loads(text[start:end + 1])
            if isinstance(data, list):
                return data
        except json.JSONDecodeError:
            pass

    print("[T4-ingest] 无法从文件中解析 JSON 数组", file=sys.stderr)
    return []


def ingest(path: Path, dry_run: bool = False) -> int:
    """读取 LLM 产出 → 把 PASS 的条目写入信号台账。返回写入条数。"""
    ensure_dirs()
    init_if_missing()

    results = parse_llm_output(path)
    if not results:
        print("[T4-ingest] 没有可用结果", file=sys.stderr)
        return 0

    today = dt.date.today()
    yaml_tag = get_yaml_tag()
    passed: List[Dict[str, Any]] = []
    rejected: List[Dict[str, Any]] = []

    for item in results:
        verdict = str(item.get("verdict", "")).upper()
        if verdict == "PASS":
            passed.append(item)
        else:
            rejected.append(item)

    print(f"[T4-ingest] 解析 {len(results)} 条结果：PASS={len(passed)} REJECT={len(rejected)}")

    written = 0
    signals: List[str] = []
    for item in passed:
        record = {
            "触发日期": today.isoformat(),
            "yaml_version_at_trigger": yaml_tag,
            "触发任务": "T4",
            "桶": "C",
            "规则ID": "C-TEXT-SCAN",
            "标的代码": str(item.get("stock_code", "")),
            "标的名称": str(item.get("stock_name", "")),
            "申万一级行业": str(item.get("industry", "")),
            "分桶基准代码": "000905",  # 中证 500 作为 C 桶通用基准
            "触发时指标值": str(item.get("weighted_score", "")),
            "阈值": "6.0",
            "当时组合状态": "",
            "信号方向": "买入候选",
            "建议动作": "纳入 C 桶候选池观察",
            "是否实际执行": "",
            "备注": str(item.get("reason", ""))[:200],
        }

        if dry_run:
            print(f"  [dry-run] 会写入：{record['标的代码']} {record['标的名称']} "
                  f"score={record['触发时指标值']}")
        else:
            sid = append_signal(record)
            signals.append(sid)
            print(f"  [OK] {sid} | {record['标的代码']} {record['标的名称']} "
                  f"score={record['触发时指标值']}")
        written += 1

    # 生成报告
    sections = []
    if passed:
        lines = ["| 代码 | 名称 | 行业 | 加权分 | 关键理由 |",
                 "|------|------|------|--------|----------|"]
        for item in passed:
            lines.append(
                f"| {item.get('stock_code','')} "
                f"| {item.get('stock_name','')} "
                f"| {item.get('industry','')} "
                f"| {item.get('weighted_score','')} "
                f"| {str(item.get('reason',''))[:50]} |"
            )
        sections.append(("通过文本判定（纳入候选池）", "\n".join(lines)))

    if rejected:
        lines = ["| 代码 | 名称 | 加权分 | 淘汰理由 |",
                 "|------|------|--------|----------|"]
        for item in rejected:
            lines.append(
                f"| {item.get('stock_code','')} "
                f"| {item.get('stock_name','')} "
                f"| {item.get('weighted_score','')} "
                f"| {str(item.get('reason',''))[:60]} |"
            )
        sections.append(("未通过文本判定", "\n".join(lines)))

    alerts = []
    if passed:
        alerts.append(("P1", f"T4 文本扫描：{len(passed)} 只通过景气判定，纳入 C 桶候选池"))

    if not dry_run:
        report_path = write_report(
            task="T4",
            title=f"T4 财报季文本扫描 · {today}",
            sections=sections,
            alerts=alerts,
            date=today,
        )
        print(f"\n[T4-ingest] 报告：{report_path}")

        # 推送
        if passed:
            summary = "\n".join([
                f"**T4 文本扫描 · {today}**",
                f"通过：{len(passed)} 只 | 淘汰：{len(rejected)} 只",
                "",
                "通过标的：",
            ] + [f"- {p['stock_code']} {p['stock_name']}（{p.get('industry','')}，"
                 f"分={p.get('weighted_score','')}）" for p in passed[:10]])
            notify(summary, title="T4 C桶文本信号", level="P1")

    # 汇总 LLM 建议的新关键词（suggested_keywords 字段）
    suggested: Dict[str, set] = {"demand": set(), "price": set(), "supply": set(), "negative": set()}
    for item in results:
        sk = item.get("suggested_keywords") or {}
        for cat, kws in sk.items():
            if cat in suggested and isinstance(kws, list):
                suggested[cat].update(kws)

    has_new = any(suggested.values())
    if has_new:
        print("\n[T4-ingest] LLM 建议补充的新关键词（人工审核后可更新到 02_strategy_config.yaml）：")
        for cat, kws in suggested.items():
            if kws:
                print(f"  {cat}: {', '.join(sorted(kws))}")
        # 在报告中也加一节
        kw_lines = []
        for cat, kws in suggested.items():
            if kws:
                kw_lines.append(f"- **{cat}**：{', '.join(sorted(kws))}")
        if kw_lines:
            sections.append(("LLM 建议补充关键词", "\n".join(kw_lines)))
            if not dry_run:
                report_path2 = write_report(
                    task="T4",
                    title=f"T4 财报季文本扫描 · {today}",
                    sections=sections,
                    alerts=alerts,
                    date=today,
                )

    return written


# ============================================================
# CLI
# ============================================================

def main() -> int:
    parser = argparse.ArgumentParser(description="T4 财报季扫描：输入准备 / ingest")
    parser.add_argument("--prepare", action="store_true",
                        help="运行输入准备阶段（抓取财报摘要）")
    parser.add_argument("--codes", type=str, default="",
                        help="逗号分隔股票代码（prepare 模式；留空则自动发现）")
    parser.add_argument("--top-n", type=int, default=300,
                        help="自动发现模式下扫描池上限（默认 300，仅作安全阀）")
    parser.add_argument("--min-gain", type=float, default=50.0,
                        help="预告/报表净利同比门槛（%%，默认 50）")
    parser.add_argument("--input-file", type=Path, default=None,
                        help="覆盖 LLM 输出文件路径")
    parser.add_argument("--dry-run", action="store_true",
                        help="只打印，不写入台账")
    args = parser.parse_args()

    if args.prepare:
        codes = [c.strip() for c in args.codes.split(",") if c.strip()]
        if not codes:
            print("[T4] 未指定 --codes，启用自动发现"
                  "（关键词+预告增速+报表增速）...")
            codes, _meta, _heat = discover_scan_pool(
                top_n=args.top_n, min_gain=args.min_gain)
            if not codes:
                print("[T4] 自动发现失败：预告/报表数据均为空，"
                      "请手动 --codes 指定或稍后重试", file=sys.stderr)
                return 1
        prepare_input(codes)
        return 0

    # 默认：ingest 模式
    path = args.input_file or SKILL_OUTPUT
    n = ingest(path, dry_run=args.dry_run)
    print(f"\n[T4-ingest] 完成，写入 {n} 条信号")
    return 0


if __name__ == "__main__":
    sys.exit(main())
