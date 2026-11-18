#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""S29 Tushare 补充拉取。Token 只从环境变量或本地文件读取，永不打印。"""
from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path

# Shared service proxy; no parallel Python cache or direct SDK access here.
import sys as _shared_sys
_shared_sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.shared_financial import SharedTushareProxy

CN = timezone(timedelta(hours=8))

# 文档结论（官方 tushare.pro 文档，2026-11 查阅）。积分以官网「权限」说明为准。
API_TABLE = [
    # field, api, points, provides, note
    ("银行不良率", "fina_indicator", "2000", "否（无 npl/不良 字段）", "无官方 bank_indicator；C# 用东财 NONPERLOAN"),
    ("拨备覆盖率", "fina_indicator", "2000", "否", "C# 用东财 BLDKBBL"),
    ("核心一级资本充足率 CET1", "fina_indicator", "2000", "否", "C# 用东财 HXYJBCZL"),
    ("RWA 风险加权资产", "（无公开接口）", "—", "否", "C# 用总资产 3 年 CAGR 代理，软门槛"),
    ("关注类贷款", "（无公开接口）", "—", "否", "manual_tags.csv 或中性分"),
    ("房地产/平台贷款占比", "（无公开接口）", "—", "否", "manual_tags.csv 或中性分"),
    ("不良生成率", "（无公开接口）", "—", "否", "manual_tags.csv 或中性分"),
    ("审计意见", "fina_audit", "2000", "是", "写出 fina_audit.csv"),
    ("质押比例", "pledge_stat / pledge_detail", "2000", "是", "写出 pledge_stat.csv"),
    ("分红", "dividend", "2000", "是（可选）", "东财已有分红，默认不作为前置"),
    ("回购", "repurchase", "2000", "是", "写出 repurchase.csv"),
    ("申万行业", "index_classify / index_member_all", "2000", "是（可选）", "C# 已用东财行业+mapping.csv"),
    ("处罚/立案", "无专用；anns_d 单独付费", "付费", "否（标准积分不够）", "C# 用东财公告标题关键词粗筛"),
]

PROBE_APIS = [
    "fina_audit",
    "pledge_stat",
    "pledge_detail",
    "repurchase",
    "dividend",
    "index_classify",
    "index_member_all",
    "fina_indicator",
    "bank_indicator",  # 探测：文档未列官方接口
    "anns_d",
]


def find_root(explicit: str | None) -> Path:
    if explicit:
        return Path(explicit).resolve()
    here = Path(__file__).resolve().parent
    for p in [here, *here.parents]:
        if (p / "data").is_dir() and (p / "scripts").is_dir():
            return p
    return Path.cwd()


def load_token(root: Path) -> tuple[str | None, str]:
    env = os.environ.get("TUSHARE_TOKEN", "").strip()
    if env:
        return env, "env:TUSHARE_TOKEN"
    candidates = [
        Path(__file__).resolve().parent / "token.txt",
        root / "data" / "quality_value" / "supplement" / "token.txt",
    ]
    for c in candidates:
        if not c.is_file():
            continue
        raw = c.read_text(encoding="utf-8")
        body_lines = [l.strip() for l in raw.splitlines() if l.strip() and not l.strip().startswith("#")]
        if not body_lines:
            continue
        token = body_lines[0]
        low = token.lower()
        if (not token) or "your_token" in low or "your_tushare_token" in low or low.startswith("paste"):
            continue
        return token, f"file:{c.name}"
    return None, "missing"


def print_table() -> None:
    cols = ("数据项", "Tushare 接口", "最低积分", "是否提供", "说明")
    rows = [tuple(r) for r in API_TABLE]
    widths = [max(len(c), max(len(r[i]) for r in rows)) for i, c in enumerate(cols)]
    def fmt(r):
        return " | ".join(s.ljust(widths[i]) for i, s in enumerate(r))
    print(fmt(cols))
    print("-+-".join("-" * w for w in widths))
    for r in rows:
        print(fmt(r))


def ts_code(code: str) -> str:
    c = code.strip().zfill(6)
    if c.startswith("9") or c.startswith("2"):  # 北交所/深市 B 少见；BJ 8/9/4
        pass
    if c.startswith(("6", "9")):
        return c + ".SH"
    if c.startswith(("0", "2", "3")):
        return c + ".SZ"
    if c.startswith(("4", "8")):
        return c + ".BJ"
    return c + ".SZ"


def main() -> int:
    ap = argparse.ArgumentParser(description="S29 Tushare 补充拉取（永不打印 token）")
    ap.add_argument("--offline", action="store_true", help="Only reuse C# shared raw cache; no provider requests")
    ap.add_argument("--root", default=None, help="项目根（含 data/ 与 scripts/）")
    ap.add_argument("--dry-run", action="store_true", help="只报告接口/积分与 token 是否存在，不写数据")
    ap.add_argument("--codes", default="", help="逗号分隔 6 位代码；空则尝试从 pool_latest.csv 或用例列表读取")
    ap.add_argument("--with-dividend", action="store_true", help="同时拉 dividend（东财已有，默认跳过）")
    ap.add_argument("--with-industry", action="store_true", help="同时拉申万行业（东财已有，默认跳过）")
    args = ap.parse_args()

    root = find_root(args.root)
    out = root / "data" / "quality_value" / "supplement"
    out.mkdir(parents=True, exist_ok=True)
    now = datetime.now(CN).strftime("%Y-%m-%d %H:%M")
    print(f"项目根: {root}")
    print(f"输出目录: {out}")
    print(f"时间（Asia/Shanghai）: {now}")
    print()
    print_table()
    print()

    # Credentials are resolved only inside explicit C#-locked supplemental worker.
    # Credentials are resolved only inside explicit C#-locked supplemental worker.
    # Credentials are resolved only inside explicit C#-locked supplemental worker.

    if args.dry_run:
        # 有 token 时的探测：只打一次 pro_api 初始化 + 各接口 1 行，确认权限，不写文件
        pass # SDK is loaded only in C#-locked provider worker
        pro = SharedTushareProxy(root, refresh=not args.offline)
        print("开始权限探测（每接口最多 1 次，失败只记原因，不打印 token）…")
        results = []
        for name in PROBE_APIS:
            try:
                fn = getattr(pro, name, None)
                if fn is None:
                    results.append((name, "无此方法", "tushare 包未暴露该接口"))
                    continue
                # 用极小参数探测
                if name == "fina_audit":
                    df = fn(ts_code="000001.SZ", start_date="20240101", end_date="20251231")
                elif name == "pledge_stat":
                    df = fn(ts_code="000001.SZ")
                elif name == "pledge_detail":
                    df = fn(ts_code="000001.SZ")
                elif name == "repurchase":
                    df = fn(start_date="20240101", end_date="20251231")
                elif name == "dividend":
                    df = fn(ts_code="000001.SZ", start_date="20240101", end_date="20251231")
                elif name == "index_classify":
                    df = fn(level="L1", src="SW2021")
                elif name == "index_member_all":
                    df = fn(l1_code="801780.SI")  # 申万银行示例
                elif name == "fina_indicator":
                    df = fn(ts_code="000001.SZ", start_date="20240101", end_date="20251231",
                            fields="ts_code,end_date,roe,npl,npl_ratio,non_perf_loan,provision_coverage,core_capital_adequacy")
                elif name == "bank_indicator":
                    df = fn(ts_code="000001.SZ")
                elif name == "anns_d":
                    df = fn(ts_code="000001.SZ", start_date="20260101", end_date="20261231")
                else:
                    df = fn()
                cols = list(getattr(df, "columns", []))
                n = 0 if df is None else len(df)
                results.append((name, f"OK rows={n}", "cols=" + ",".join(cols[:12])))
            except Exception as e:
                msg = str(e)
                results.append((name, "调用失败", msg[:200]))
        print(f"{'接口':<22} {'结果':<16} 说明")
        for name, st, note in results:
            print(f"{name:<22} {st:<16} {note}")
        (out / "dry_run_probe.json").write_text(
            json.dumps({"time": now, "source": "C# shared-service", "credentials": "not loaded by orchestrator",
                        "results": [{"api": a, "status": s, "note": n} for a, s, n in results]},
                       ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        print(f"探测摘要已写 {out / 'dry_run_probe.json'}（不含 token）")
        return 0

    # 正式拉取
    try:
        import pandas as pd  # type: ignore
        # SDK imported only in supplemental worker  # type: ignore
    except ImportError:
        print("未安装 tushare/pandas，请先: pip install -r requirements.txt")
        return 2

    default_codes = ["000333", "000651", "601838", "600919", "601009", "600926",
                     "600901", "601018", "600377", "600900", "601919", "601872"]
    codes: list[str] = []
    if args.codes.strip():
        codes = [c.strip().zfill(6) for c in args.codes.split(",") if c.strip()]
    else:
        pool = root / "data" / "quality_value" / "pool_latest.csv"
        if pool.is_file():
            try:
                import pandas as pd  # noqa
                dfp = pd.read_csv(pool, dtype=str)
                col = "code" if "code" in dfp.columns else dfp.columns[0]
                codes = [str(x).zfill(6) for x in dfp[col].dropna().unique().tolist()]
            except Exception:
                codes = []
        if not codes:
            codes = default_codes
    # 始终包含验证用例
    for c in default_codes:
        if c not in codes:
            codes.append(c)
    print(f"拟拉取 {len(codes)} 只（含验证用例）")

    pro = SharedTushareProxy(root, refresh=not args.offline)
    meta = {"time": now, "source": "C# shared-service", "n_codes": len(codes), "written": [], "errors": []}

    def safe_call(name, fn):
        try:
            df = fn()
            return df, None
        except Exception as e:
            msg = str(e)
            meta["errors"].append({"api": name, "error": msg[:300]})
            print(f"  {name} 失败：{msg[:200]}")
            return None, msg

    # 审计
    audit_rows = []
    for c in codes:
        df, _ = safe_call("fina_audit:"+c, lambda c=c: pro.fina_audit(ts_code=ts_code(c)))
        if df is None or len(df) == 0:
            continue
        for _, r in df.iterrows():
            audit_rows.append({
                "code": c,
                "end_date": str(r.get("end_date", "")),
                "ann_date": str(r.get("ann_date", "")),
                "opinion": str(r.get("audit_result") or r.get("audit_opinion") or r.get("result") or ""),
                "agency": str(r.get("audit_agency") or r.get("auditor") or ""),
            })
    if audit_rows:
        import pandas as pd
        pd.DataFrame(audit_rows).to_csv(out / "fina_audit.csv", index=False, encoding="utf-8-sig")
        meta["written"].append("fina_audit.csv")
        print(f"  写出 fina_audit.csv  {len(audit_rows)} 行")

    # 质押
    pledge_rows = []
    for c in codes:
        df, _ = safe_call("pledge_stat:"+c, lambda c=c: pro.pledge_stat(ts_code=ts_code(c)))
        if df is None or len(df) == 0:
            continue
        # 取最新一行
        r = df.sort_values(df.columns[0]).iloc[-1]
        pledge_rows.append({
            "code": c,
            "end_date": str(r.get("end_date", r.get("trade_date", ""))),
            "pledge_ratio": r.get("pledge_ratio", ""),
            "unpledge_ratio": r.get("unpledge_ratio", ""),
            "pledge_count": r.get("pledge_count", ""),
            "holder_pledge_ratio": r.get("holder_pledge_ratio", r.get("pledge_ratio", "")),
        })
    if pledge_rows:
        import pandas as pd
        pd.DataFrame(pledge_rows).to_csv(out / "pledge_stat.csv", index=False, encoding="utf-8-sig")
        meta["written"].append("pledge_stat.csv")
        print(f"  写出 pledge_stat.csv  {len(pledge_rows)} 行")

    # 回购
    rep_rows = []
    try:
        df, _ = safe_call("repurchase", lambda: pro.repurchase(start_date="20180101", end_date=datetime.now(CN).strftime("%Y%m%d")))
        if df is not None and len(df) > 0:
            want = set(ts_code(c) for c in codes)
            if "ts_code" in df.columns:
                df = df[df["ts_code"].isin(want)]
            for _, r in df.iterrows():
                code = str(r.get("ts_code", "")).split(".")[0]
                rep_rows.append({
                    "code": code,
                    "ann_date": str(r.get("ann_date", "")),
                    "end_date": str(r.get("end_date", "")),
                    "amount": r.get("amount", r.get("vol", "")),
                    "proc": str(r.get("proc", r.get("progress", ""))),
                })
    except Exception as e:
        meta["errors"].append({"api": "repurchase", "error": str(e)[:300]})
    if rep_rows:
        import pandas as pd
        pd.DataFrame(rep_rows).to_csv(out / "repurchase.csv", index=False, encoding="utf-8-sig")
        meta["written"].append("repurchase.csv")
        print(f"  写出 repurchase.csv  {len(rep_rows)} 行")

    if args.with_dividend:
        div_rows = []
        for c in codes:
            df, _ = safe_call("dividend:"+c, lambda c=c: pro.dividend(ts_code=ts_code(c)))
            if df is None:
                continue
            for _, r in df.iterrows():
                div_rows.append({"code": c, **{k: ("" if v is None else v) for k, v in r.to_dict().items()}})
        if div_rows:
            import pandas as pd
            pd.DataFrame(div_rows).to_csv(out / "dividend.csv", index=False, encoding="utf-8-sig")
            meta["written"].append("dividend.csv")

    if args.with_industry:
        df, _ = safe_call("index_classify", lambda: pro.index_classify(level="L1", src="SW2021"))
        if df is not None:
            df.to_csv(out / "sw_index_classify.csv", index=False, encoding="utf-8-sig")
            meta["written"].append("sw_index_classify.csv")

    # 银行字段探测结果写入说明，不编造数据
    (out / "fetch_meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"完成。写出 {len(meta['written'])} 个文件，错误 {len(meta['errors'])} 条。摘要 fetch_meta.json（不含 token）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
