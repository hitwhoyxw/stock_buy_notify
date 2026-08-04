"""统一数据源封装。akshare 主，tushare 备。

策略脚本禁止直接 import akshare/tushare。所有行情/财务/指数调用集中到本模块，
方便未来切数据源或加缓存。

约定：
- 所有返回带日期的接口，日期一律 datetime.date；金额单位保持源数据不变。
- 所有函数在数据缺失时返回 None 或空 DataFrame，不抛异常（除了参数错误）。
- 缓存文件放 data/cache/，key 由函数名 + 参数生成，TTL 由调用方通过 cache_hours 控制。
"""
from __future__ import annotations

import datetime as dt
import functools
import hashlib
import json
import os
import sys
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

import pandas as pd

from lib.paths import CACHE_DIR


# ============================================================
# 缓存装饰器
# ============================================================

def _cache_path(name: str, key: str) -> Path:
    return CACHE_DIR / f"{name}__{hashlib.sha1(key.encode()).hexdigest()[:12]}.parquet"


def disk_cache(ttl_hours: float = 6.0):
    """本地 parquet 缓存装饰器。仅用于返回 DataFrame 的函数。"""

    def decorator(func: Callable[..., pd.DataFrame]):
        @functools.wraps(func)
        def wrapper(*args, **kwargs) -> pd.DataFrame:
            CACHE_DIR.mkdir(parents=True, exist_ok=True)
            key = json.dumps({"args": args, "kwargs": kwargs}, default=str, sort_keys=True)
            path = _cache_path(func.__name__, key)
            if path.exists():
                age_h = (dt.datetime.now().timestamp() - path.stat().st_mtime) / 3600
                if age_h < ttl_hours:
                    try:
                        return pd.read_parquet(path)
                    except Exception:
                        pass  # fallthrough to refetch
            try:
                df = func(*args, **kwargs)
            except Exception as e:
                if path.exists():
                    print(f"[data_fetch] {func.__name__} 失败({e})，回退过期缓存", file=sys.stderr)
                    return pd.read_parquet(path)
                raise
            if isinstance(df, pd.DataFrame) and not df.empty:
                try:
                    df.to_parquet(path)
                except Exception as e:
                    print(f"[data_fetch] 缓存写入失败：{e}", file=sys.stderr)
            return df

        return wrapper

    return decorator


# ============================================================
# 数据源初始化
# ============================================================

_tushare_pro = None


def _get_tushare():
    """惰性初始化 tushare pro。无 token 返回 None，调用方自行降级。"""
    global _tushare_pro
    if _tushare_pro is not None:
        return _tushare_pro
    token = os.environ.get("TUSHARE_TOKEN", "").strip()
    if not token:
        return None
    try:
        import tushare as ts

        ts.set_token(token)
        _tushare_pro = ts.pro_api()
        return _tushare_pro
    except Exception as e:
        print(f"[data_fetch] tushare 初始化失败：{e}", file=sys.stderr)
        return None


# ============================================================
# 进程内缓存（关键：disk_cache 的 parquet 在缺 pyarrow 时静默失败，
# 会导致同一快照被逐票重复拉取→触发限频。这里加一层内存缓存兜底。）
# ============================================================

_RUNTIME_CACHE: Dict[str, Any] = {}


def _memo(key: str, fetch_fn: Callable[[], Any]) -> Any:
    """进程内单次缓存：同一次运行里 fetch_fn 最多执行一次。"""
    if key in _RUNTIME_CACHE:
        return _RUNTIME_CACHE[key]
    val = fetch_fn()
    _RUNTIME_CACHE[key] = val
    return val


# ============================================================
# 腾讯轻量批量行情（防限频主力源）
# qt.gtimg.cn 单次请求可带 ~60 只代码，返回现价/PE(TTM)/PB/市值，
# 无需 token、不依赖东财爬虫，是本地与被限频环境下的兜底。
# ============================================================

def _to_tencent_symbol(code: str) -> str:
    pure = code.split(".")[0].zfill(6)
    if pure.startswith(("6", "5", "9")):
        return f"sh{pure}"
    if pure.startswith(("4", "8")):
        return f"bj{pure}"
    return f"sz{pure}"


def get_tencent_batch_quotes(codes: List[str], batch_size: int = 60) -> pd.DataFrame:
    """批量拉取实时行情（腾讯源）。返回列：code, name, price, pe_ttm, pb, total_mv。
    total_mv 单位：亿元。一次 HTTP 请求 ~60 只，频率极低。
    """
    import requests
    import time

    codes = [c.split(".")[0].zfill(6) for c in codes]
    codes = list(dict.fromkeys(codes))  # 去重保序
    if not codes:
        return pd.DataFrame()

    rows: Dict[str, Dict[str, Any]] = {}
    for i in range(0, len(codes), batch_size):
        chunk = codes[i:i + batch_size]
        q = ",".join(_to_tencent_symbol(c) for c in chunk)
        try:
            resp = requests.get(
                "http://qt.gtimg.cn/q=" + q,
                headers={"User-Agent": "Mozilla/5.0"},
                timeout=10,
            )
            resp.encoding = "gbk"
        except Exception as e:
            print(f"[data_fetch] 腾讯行情批次失败：{e}", file=sys.stderr)
            continue

        for line in resp.text.strip().split(";"):
            line = line.strip()
            if "=" not in line:
                continue
            _, val = line.split("=", 1)
            f = val.strip('"').split("~")
            if len(f) < 47:
                continue
            code = f[2]
            def _num(x):
                try:
                    v = float(x)
                    return v if v != 0 else None
                except (ValueError, TypeError):
                    return None
            rows[code] = {
                "code": code,
                "name": f[1],
                "price": _num(f[3]),
                "pe_ttm": _num(f[39]),
                "total_mv": _num(f[45]),  # 亿元
                "pb": _num(f[46]),
                "dv_ttm": _num(f[64]) if len(f) > 64 else None,  # 股息率 TTM(%)
            }
        time.sleep(0.3)  # 批次间轻休眠，防限频

    if not rows:
        return pd.DataFrame()
    return pd.DataFrame(list(rows.values()))


# ============================================================
# 指数行情
# ============================================================

@disk_cache(ttl_hours=6)
def get_index_daily(code: str, start: str, end: str) -> pd.DataFrame:
    """指数日线。返回列：date, open, close, high, low, volume, amount。
    code 支持格式：000922 / 000922.SH / sh000922，内部统一处理。
    数据源优先级：EastMoney → Sina → tushare。
    """
    import akshare as ak
    import time

    pure = code.split(".")[0]
    if pure.startswith(("sh", "sz")):
        symbol = pure
        pure = symbol[2:]
    elif pure.startswith("0") or pure.startswith("399"):
        symbol = f"sh{pure}" if pure.startswith("000") else f"sz{pure}"
    else:
        symbol = f"sh{pure}"

    start_d = start.replace("-", "")
    end_d = end.replace("-", "")
    df = pd.DataFrame()

    # 方案 1: EastMoney index_zh_a_hist（数据最新，含 amount）
    for attempt in range(2):
        try:
            raw = ak.index_zh_a_hist(symbol=pure, period="daily",
                                     start_date=start_d, end_date=end_d)
            if raw is not None and not raw.empty:
                df = raw.rename(columns={
                    "日期": "date", "开盘": "open", "收盘": "close",
                    "最高": "high", "最低": "low",
                    "成交量": "volume", "成交额": "amount",
                })
                break
        except Exception:
            if attempt == 0:
                time.sleep(1)

    # 方案 2: Sina stock_zh_index_daily（稳定但部分指数停更）
    if df is None or df.empty:
        try:
            raw = ak.stock_zh_index_daily(symbol=symbol)
            if raw is not None and not raw.empty:
                raw["date"] = pd.to_datetime(raw["date"]).dt.date
                sd = pd.to_datetime(start).date()
                ed = pd.to_datetime(end).date()
                raw = raw[(raw["date"] >= sd) & (raw["date"] <= ed)]
                if not raw.empty:
                    df = raw.copy()
        except Exception:
            pass

    # 方案 3: tushare index_daily
    if df is None or df.empty:
        pro = _get_tushare()
        if pro is not None:
            try:
                ts_code = f"{pure}.{'SH' if pure.startswith(('000', '9', '5')) else 'SZ'}"
                tdf = pro.index_daily(ts_code=ts_code, start_date=start_d, end_date=end_d)
                if tdf is not None and not tdf.empty:
                    tdf = tdf.rename(columns={"trade_date": "date", "vol": "volume"})
                    tdf["date"] = pd.to_datetime(tdf["date"], format="%Y%m%d").dt.date
                    df = tdf.sort_values("date").reset_index(drop=True)
            except Exception as e:
                print(f"[data_fetch] tushare index_daily({pure}) 失败：{e}", file=sys.stderr)

    if df is None or df.empty:
        return pd.DataFrame()
    if "date" in df.columns:
        df["date"] = pd.to_datetime(df["date"]).dt.date
    sd = pd.to_datetime(start).date()
    ed = pd.to_datetime(end).date()
    df = df[(df["date"] >= sd) & (df["date"] <= ed)]
    return df.reset_index(drop=True)


@disk_cache(ttl_hours=6)
def get_stock_daily(code: str, start: str, end: str, adjust: str = "qfq") -> pd.DataFrame:
    """A 股日线。code = 6 位数字。adjust: '' / 'qfq' / 'hfq'。"""
    import akshare as ak

    pure = code.split(".")[0]
    try:
        df = ak.stock_zh_a_hist(symbol=pure, period="daily",
                                start_date=start.replace("-", ""),
                                end_date=end.replace("-", ""),
                                adjust=adjust)
    except Exception as e:
        print(f"[data_fetch] akshare 拉 {code} 失败：{e}，尝试 tushare", file=sys.stderr)
        pro = _get_tushare()
        if pro is None:
            return pd.DataFrame()
        ts_code = f"{pure}.{'SH' if pure.startswith('6') else 'SZ'}"
        df = pro.daily(ts_code=ts_code, start_date=start.replace("-", ""), end_date=end.replace("-", ""))
        df = df.rename(columns={"trade_date": "date", "vol": "volume", "amount": "amount"})
        df["date"] = pd.to_datetime(df["date"]).dt.date
        return df.sort_values("date").reset_index(drop=True)

    if df is None or df.empty:
        return pd.DataFrame()
    df = df.rename(columns={"日期": "date", "开盘": "open", "收盘": "close", "最高": "high",
                            "最低": "low", "成交量": "volume", "成交额": "amount"})
    df["date"] = pd.to_datetime(df["date"]).dt.date
    return df.reset_index(drop=True)


# ============================================================
# 红利指数专项
# ============================================================

@disk_cache(ttl_hours=12)
def get_csi_dividend_yield_history(years: int = 6) -> pd.DataFrame:
    """中证红利指数近 N 年的股息率序列。返回列：date, dividend_yield。

    数据来源优先级：
    1. akshare stock_zh_index_value_csindex (H30269 / 000922)
    2. tushare index_dailybasic (000922.SH → dv_ratio 列)
    3. 若全部失败返回空 DataFrame
    """
    import akshare as ak

    cutoff = dt.date.today() - dt.timedelta(days=int(years * 366))
    df = pd.DataFrame()

    # akshare 方案 1
    for sym in ("H30269", "000922"):
        try:
            raw = ak.stock_zh_index_value_csindex(symbol=sym)
            if raw is not None and not raw.empty:
                col_date = next((c for c in raw.columns if "日期" in c or "date" in c.lower()), raw.columns[0])
                col_dy = next((c for c in raw.columns if "股息" in c or "dividend" in c.lower()), None)
                if col_dy:
                    df = pd.DataFrame({
                        "date": pd.to_datetime(raw[col_date]).dt.date,
                        "dividend_yield": pd.to_numeric(raw[col_dy], errors="coerce"),
                    }).dropna()
                    break
        except Exception:
            continue

    # tushare 方案（index_dailybasic 含 dv_ratio = 股息率）
    if df.empty:
        pro = _get_tushare()
        if pro is not None:
            try:
                start_d = cutoff.strftime("%Y%m%d")
                end_d = dt.date.today().strftime("%Y%m%d")
                tdf = pro.index_dailybasic(
                    ts_code="000922.SH",
                    start_date=start_d,
                    end_date=end_d,
                    fields="trade_date,ts_code,turnover_rate,pe,total_mv,float_mv,dv_ratio",
                )
                if tdf is not None and not tdf.empty and "dv_ratio" in tdf.columns:
                    tdf = tdf.dropna(subset=["dv_ratio"])
                    df = pd.DataFrame({
                        "date": pd.to_datetime(tdf["trade_date"], format="%Y%m%d").dt.date,
                        "dividend_yield": pd.to_numeric(tdf["dv_ratio"], errors="coerce"),
                    }).dropna()
            except Exception as e:
                print(f"[data_fetch] tushare index_dailybasic(000922.SH) 失败：{e}", file=sys.stderr)

    if df.empty:
        print("[data_fetch] 中证红利股息率拉取全部失败", file=sys.stderr)
        return pd.DataFrame()

    df = df[df["date"] >= cutoff].sort_values("date").reset_index(drop=True)
    return df


def get_dividend_yield_percentile(years: int = 5) -> Optional[Dict[str, float]]:
    """当前中证红利股息率在近 N 年历史中的分位。返回 {current, percentile}。
    注意：akshare stock_zh_index_value_csindex 目前仅返回近 20 条，
    因此降低最低样本数要求；若 tushare 可用则能拿到完整历史。
    """
    df = get_csi_dividend_yield_history(years=years + 1)
    if df.empty or len(df) < 10:
        return None
    cutoff = dt.date.today() - dt.timedelta(days=int(years * 365.25))
    win = df[df["date"] >= cutoff].sort_values("date")
    if len(win) < 10:
        return None
    current = float(win["dividend_yield"].iloc[-1])
    pct = float((win["dividend_yield"] <= current).mean() * 100)
    return {"current": current, "percentile": pct, "n_samples": len(win)}


# ============================================================
# 债券收益率
# ============================================================

@disk_cache(ttl_hours=12)
def get_bond_yield_10y(days: int = 30) -> pd.DataFrame:
    """10 年期国债到期收益率。返回 date, yield_pct。"""
    import akshare as ak

    try:
        df = ak.bond_zh_us_rate()
    except Exception as e:
        print(f"[data_fetch] 10Y国债收益率拉取失败：{e}", file=sys.stderr)
        return pd.DataFrame()
    if df is None or df.empty:
        return pd.DataFrame()

    col_date = next((c for c in df.columns if "日期" in c), None)
    col_y = next((c for c in df.columns if "中国国债收益率10年" in c or "10年" in c and "美" not in c), None)
    if col_date is None or col_y is None:
        return pd.DataFrame()

    out = pd.DataFrame({
        "date": pd.to_datetime(df[col_date]).dt.date,
        "yield_pct": pd.to_numeric(df[col_y], errors="coerce"),
    }).dropna().sort_values("date")
    return out.tail(days).reset_index(drop=True)


def get_latest_10y_yield() -> Optional[float]:
    df = get_bond_yield_10y(days=10)
    if df.empty:
        return None
    return float(df["yield_pct"].iloc[-1])


# ============================================================
# 全 A 成交额 & 板块成交额
# ============================================================

@disk_cache(ttl_hours=6)
def get_market_turnover(days: int = 30) -> pd.DataFrame:
    """全 A 每日成交额（上证综指+深证成指之和近似）。返回 date, amount（单位：元）。
    优先用 amount 列，若缺失则用 volume 列代替（仅影响绝对值，不影响分位计算）。
    """
    end = dt.date.today()
    start = end - dt.timedelta(days=days * 2)
    start_s = start.strftime("%Y-%m-%d")
    end_s = end.strftime("%Y-%m-%d")

    sh = get_index_daily("000001", start_s, end_s)
    sz = get_index_daily("399001", start_s, end_s)

    if sh.empty or sz.empty:
        print("[data_fetch] 市场成交额拉取失败：sh/sz 指数数据为空", file=sys.stderr)
        return pd.DataFrame()

    def _find_col(df: pd.DataFrame, candidates: List[str]) -> Optional[str]:
        for c in candidates:
            if c in df.columns:
                return c
        return None

    sh_col = _find_col(sh, ["amount", "成交额", "volume", "成交量"])
    sz_col = _find_col(sz, ["amount", "成交额", "volume", "成交量"])
    if sh_col is None or sz_col is None:
        print("[data_fetch] 市场成交额缺少 amount/volume 列", file=sys.stderr)
        return pd.DataFrame()

    merged = pd.merge(
        sh[["date", sh_col]].rename(columns={sh_col: "sh_amt"}),
        sz[["date", sz_col]].rename(columns={sz_col: "sz_amt"}),
        on="date", how="inner",
    )
    merged["sh_amt"] = pd.to_numeric(merged["sh_amt"], errors="coerce")
    merged["sz_amt"] = pd.to_numeric(merged["sz_amt"], errors="coerce")
    merged["amount"] = merged["sh_amt"] + merged["sz_amt"]
    merged = merged.dropna(subset=["amount"])
    return merged[["date", "amount"]].sort_values("date").tail(days).reset_index(drop=True)


def get_market_turnover_percentile(window_days: int = 20, lookback_days: int = 250) -> Optional[float]:
    """当前 window_days 平均成交额，在过去 lookback_days 日窗口分位。"""
    df = get_market_turnover(days=lookback_days + window_days)
    if df.empty or len(df) < window_days + 30:
        return None
    df["rolling"] = df["amount"].rolling(window_days).mean()
    df = df.dropna()
    if df.empty:
        return None
    current = float(df["rolling"].iloc[-1])
    hist = df["rolling"].tail(lookback_days)
    pct = float((hist <= current).mean() * 100)
    return pct


# ============================================================
# 沪深 300 回撤
# ============================================================

def get_hs300_drawdown(days: int = 20) -> Optional[Dict[str, float]]:
    """沪深 300 近 N 日单日最大跌幅 + 期间最大回撤。返回百分数（负值）。"""
    end = dt.date.today()
    start = end - dt.timedelta(days=days * 3)
    df = get_index_daily("000300", start.strftime("%Y-%m-%d"), end.strftime("%Y-%m-%d"))
    if df.empty or len(df) < 5:
        return None
    df = df.sort_values("date").tail(days)
    ret = df["close"].pct_change() * 100
    single_day = float(ret.min()) if not ret.empty else None
    peak = df["close"].cummax()
    dd = ((df["close"] - peak) / peak * 100).min()
    return {"single_day_pct": single_day, "drawdown_pct": float(dd)}


# ============================================================
# 相对超额（红利 vs Wind 全 A）
# ============================================================

def get_relative_excess(bucket_code: str = "000922", benchmark_code: str = "000985",
                       days: int = 60) -> Optional[float]:
    """bucket_code 相对 benchmark_code 近 days 交易日的相对超额收益（百分数）。
    默认 000985 = 中证全指（Wind 全 A 的近似替代）。
    """
    end = dt.date.today()
    start = end - dt.timedelta(days=days * 2)
    fmt = "%Y-%m-%d"
    a = get_index_daily(bucket_code, start.strftime(fmt), end.strftime(fmt))
    b = get_index_daily(benchmark_code, start.strftime(fmt), end.strftime(fmt))
    if a.empty or b.empty:
        return None
    a = a.sort_values("date").tail(days).reset_index(drop=True)
    b = b.sort_values("date").tail(days).reset_index(drop=True)
    if len(a) < 5 or len(b) < 5:
        return None
    a_ret = a["close"].iloc[-1] / a["close"].iloc[0] - 1
    b_ret = b["close"].iloc[-1] / b["close"].iloc[0] - 1
    return float((a_ret - b_ret) * 100)


# ============================================================
# 申万一级行业成交额集中度
# ============================================================

@disk_cache(ttl_hours=6)
def get_sw_l1_turnover_share() -> pd.DataFrame:
    """当日申万一级行业成交额及占比。返回 industry, amount, share_pct。"""
    import akshare as ak

    try:
        df = ak.sw_index_first_info()
    except Exception as e:
        print(f"[data_fetch] 申万一级行业信息拉取失败：{e}", file=sys.stderr)
        return pd.DataFrame()

    if df is None or df.empty:
        return pd.DataFrame()

    # 列名兼容：可能是 行业代码/行业名称/成交额 或英文
    col_name = next((c for c in df.columns if "行业名称" in c or "name" in c.lower()), None)
    col_amt = next((c for c in df.columns if "成交额" in c or "amount" in c.lower()), None)
    if col_name is None or col_amt is None:
        return pd.DataFrame()

    out = pd.DataFrame({
        "industry": df[col_name],
        "amount": pd.to_numeric(df[col_amt], errors="coerce"),
    }).dropna()
    total = out["amount"].sum()
    if total <= 0:
        return pd.DataFrame()
    out["share_pct"] = out["amount"] / total * 100
    return out.sort_values("share_pct", ascending=False).reset_index(drop=True)


def get_top3_industry_turnover_share() -> Optional[float]:
    df = get_sw_l1_turnover_share()
    if df.empty:
        return None
    return float(df["share_pct"].head(3).sum())


def get_dividend_sector_relative_turnover(days: int = 20) -> Optional[float]:
    """红利板块（银行 + 煤炭 + 电力）相对全市场的成交度分位。
    输出为 0~100 分位数，越低说明红利被抛弃越严重。
    """
    import akshare as ak

    dividend_industries = ["银行", "煤炭", "公用事业", "石油石化", "交通运输"]
    try:
        end = dt.date.today()
        start = end - dt.timedelta(days=days * 5)
        # 每个行业指数拉日线，加总成交额
        codes = {
            "银行": "801780",
            "煤炭": "801950",
            "公用事业": "801160",
            "石油石化": "801960",
            "交通运输": "801170",
        }
        div_amt = None
        for name, code in codes.items():
            try:
                d = ak.index_hist_sw(symbol=code, period="day")
                d = d.rename(columns={"日期": "date", "成交额": "amount"})
                d["date"] = pd.to_datetime(d["date"]).dt.date
                d = d[["date", "amount"]].rename(columns={"amount": name})
                div_amt = d if div_amt is None else pd.merge(div_amt, d, on="date", how="outer")
            except Exception:
                continue
        if div_amt is None or div_amt.empty:
            return None
        div_amt["div_total"] = div_amt.drop(columns=["date"]).sum(axis=1)
        div_amt = div_amt.sort_values("date")

        # 全市场
        market = get_market_turnover(days=days * 6)
        if market.empty:
            return None
        merged = pd.merge(div_amt[["date", "div_total"]], market, on="date", how="inner")
        merged["ratio"] = merged["div_total"] / merged["amount"]
        merged["ratio_ma"] = merged["ratio"].rolling(days).mean()
        merged = merged.dropna()
        if merged.empty:
            return None
        current = merged["ratio_ma"].iloc[-1]
        pct = float((merged["ratio_ma"] <= current).mean() * 100)
        return pct
    except Exception as e:
        print(f"[data_fetch] 红利板块相对成交度计算失败：{e}", file=sys.stderr)
        return None


# ============================================================
# 中证红利成分股
# ============================================================

@disk_cache(ttl_hours=24)
def get_csi_dividend_constituents() -> pd.DataFrame:
    """中证红利指数成分股。返回 code, name, weight。"""
    import akshare as ak

    df = pd.DataFrame()
    weight_col = None

    # 优先用带权重的接口
    try:
        raw = ak.index_stock_cons_weight_csindex(symbol="000922")
        if raw is not None and not raw.empty:
            df = raw
            weight_col = next((c for c in raw.columns if "权重" in c or "weight" in c.lower()), None)
    except Exception:
        pass

    if df.empty:
        try:
            raw = ak.index_stock_cons_csindex(symbol="000922")
            if raw is not None and not raw.empty:
                df = raw
        except Exception as e:
            print(f"[data_fetch] 中证红利成分股拉取失败：{e}", file=sys.stderr)
            return pd.DataFrame()

    if df.empty:
        return pd.DataFrame()

    # 注意：列名同时含"指数代码"和"成分券代码"，必须精确匹配成分券
    col_code = next((c for c in df.columns if "成分券代码" in c), None)
    col_name = next((c for c in df.columns if "成分券名称" in c), None)
    if col_code is None:
        candidates = [c for c in df.columns if "代码" in c and "指数" not in c]
        col_code = candidates[0] if candidates else None
    if col_name is None:
        candidates = [c for c in df.columns if "名称" in c and "指数" not in c and "英文" not in c]
        col_name = candidates[0] if candidates else None
    if col_code is None:
        print(f"[data_fetch] 成分股列名解析失败：{list(df.columns)}", file=sys.stderr)
        return pd.DataFrame()

    out = pd.DataFrame({
        "code": df[col_code].astype(str).str.strip().str.zfill(6),
        "name": df[col_name].astype(str) if col_name else "",
        "weight": pd.to_numeric(df[weight_col], errors="coerce") if weight_col else None,
    })
    out = out.drop_duplicates(subset=["code"]).reset_index(drop=True)
    return out


# ============================================================
# 单票基本面（股息率、PB、PE、市值）
# 防限频设计（参考 daily_stock_analysis）：
#   - 全市场快照一次拉取（tushare daily_basic），进程内缓存，绝不逐票重复请求
#   - 无 token / 失败时降级腾讯批量行情（单次 HTTP ~60 只）
# ============================================================

def get_fundamentals_snapshot() -> pd.DataFrame:
    """全市场基本面快照（进程内只拉一次）。列：code, pe_ttm, pb, dv_ttm, dv_ratio, total_mv。"""
    return _memo("fundamentals_snapshot", _fetch_fundamentals_snapshot)


@disk_cache(ttl_hours=12)
def _fetch_fundamentals_snapshot() -> pd.DataFrame:
    """实际拉取全市场快照。tushare daily_basic → akshare spot_em 兜底。"""
    import akshare as ak
    from lib.trading_day import is_trading_day, prev_trading_day

    today = dt.date.today()
    try:
        td = today if is_trading_day(today) else prev_trading_day(today, 1)
    except Exception:
        td = today
    td_str = td.strftime("%Y%m%d")

    # 方案 1: tushare daily_basic（一次返回全市场，含股息率）
    pro = _get_tushare()
    if pro is not None:
        try:
            raw = pro.daily_basic(trade_date=td_str)
            if raw is None or raw.empty:
                td2 = prev_trading_day(td, 1)
                raw = pro.daily_basic(trade_date=td2.strftime("%Y%m%d"))
            if raw is not None and not raw.empty:
                out = pd.DataFrame({
                    "code": raw["ts_code"].str.split(".").str[0],
                    "pe_ttm": pd.to_numeric(raw.get("pe_ttm"), errors="coerce"),
                    "pb": pd.to_numeric(raw.get("pb"), errors="coerce"),
                    "dv_ttm": pd.to_numeric(raw.get("dv_ttm"), errors="coerce"),
                    "dv_ratio": pd.to_numeric(raw.get("dv_ratio"), errors="coerce"),
                    "total_mv": pd.to_numeric(raw.get("total_mv"), errors="coerce"),
                })
                return out.dropna(subset=["code"]).reset_index(drop=True)
        except Exception as e:
            print(f"[data_fetch] tushare daily_basic 失败：{e}", file=sys.stderr)

    # 方案 2: akshare stock_zh_a_spot_em（含 PE/PB/总市值，无股息率）
    try:
        raw = ak.stock_zh_a_spot_em()
        if raw is not None and not raw.empty:
            def _col(cands):
                for c in cands:
                    if c in raw.columns:
                        return c
                return None
            c_code = _col(["代码", "code"])
            c_pe = _col(["市盈率-动态", "市盈率(动态)", "pe"])
            c_pb = _col(["市净率", "pb"])
            c_mv = _col(["总市值", "total_mv"])
            if c_code:
                out = pd.DataFrame({
                    "code": raw[c_code].astype(str).str.zfill(6),
                    "pe_ttm": pd.to_numeric(raw[c_pe], errors="coerce") if c_pe else None,
                    "pb": pd.to_numeric(raw[c_pb], errors="coerce") if c_pb else None,
                    "total_mv": pd.to_numeric(raw[c_mv], errors="coerce") if c_mv else None,
                })
                return out.reset_index(drop=True)
    except Exception as e:
        print(f"[data_fetch] akshare spot_em 失败：{e}", file=sys.stderr)

    return pd.DataFrame()


# ============================================================
# 全市场 ROE 快照（财报指标，行情源没有）
# ROE 按季度发布 → 24h 缓存 + 进程内缓存，每个报告期只拉一次。
# 源：tushare fina_indicator(period=...) 批量 → akshare stock_yjbb_em 全市场业绩报表。
# ============================================================

def _report_period_candidates(today: dt.date) -> List[str]:
    """按披露截止日推断最新已完整披露的报告期，返回尝试顺序。
    年报/一季报 4-30 披露完，中报 8-31，三季报 10-31。"""
    y, m = today.year, today.month
    if m >= 11:
        first = f"{y}0930"
    elif m >= 9:
        first = f"{y}0630"
    elif m >= 5:
        first = f"{y}0331"
    else:
        first = f"{y - 1}1231"
    pool = [f"{y}0930", f"{y}0630", f"{y}0331", f"{y - 1}1231", f"{y - 1}0930"]
    try:
        return pool[pool.index(first):]
    except ValueError:
        return pool


def get_roe_snapshot() -> pd.DataFrame:
    """全市场 ROE 快照（进程内只拉一次）。列：code, roe（%，最新报告期）。"""
    return _memo("roe_snapshot", _fetch_roe_snapshot)


@disk_cache(ttl_hours=24)
def _fetch_roe_snapshot() -> pd.DataFrame:
    """实际拉取。tushare fina_indicator 批量 → akshare stock_yjbb_em 兜底。"""
    import akshare as ak

    for period in _report_period_candidates(dt.date.today()):
        # 源 1: tushare fina_indicator（period 参数一次返回全市场）
        pro = _get_tushare()
        if pro is not None:
            try:
                raw = pro.fina_indicator(period=period, fields="ts_code,roe")
                if raw is not None and not raw.empty:
                    out = pd.DataFrame({
                        "code": raw["ts_code"].str.split(".").str[0],
                        "roe": pd.to_numeric(raw["roe"], errors="coerce"),
                        "period": period,
                    }).dropna(subset=["roe"]).drop_duplicates("code")
                    if not out.empty:
                        return out.reset_index(drop=True)
            except Exception as e:
                print(f"[data_fetch] tushare fina_indicator({period}) 失败：{e}", file=sys.stderr)

        # 源 2: akshare stock_yjbb_em（东财业绩报表，一次调用全市场）
        try:
            raw = ak.stock_yjbb_em(date=period)
            if raw is not None and not raw.empty:
                c_code = next((c for c in raw.columns if "股票代码" in c), None)
                c_roe = next((c for c in raw.columns if "净资产收益率" in c), None)
                if c_code and c_roe:
                    out = pd.DataFrame({
                        "code": raw[c_code].astype(str).str.zfill(6),
                        "roe": pd.to_numeric(raw[c_roe], errors="coerce"),
                        "period": period,
                    }).dropna(subset=["roe"]).drop_duplicates("code")
                    # 披露期可能只有部分公司出数据，太稀疏就换更早的报告期
                    if len(out) >= 500:
                        return out.reset_index(drop=True)
        except Exception as e:
            print(f"[data_fetch] stock_yjbb_em({period}) 失败：{e}", file=sys.stderr)

    return pd.DataFrame()


# ============================================================
# 盈利质量快照：近3年单季亏损次数 + 最新年报每股经营现金流
# （识别"借钱分红"：股息率高但经营现金流为负 / 利润不稳定的票）
# 历史财报数据不会变 → 72h 缓存；每期一次全市场调用，绝不逐票。
# ============================================================

def _loss_check_periods(today: dt.date) -> List[str]:
    """最新已完整披露报告期起往前数 12 个季度，再加 1 个推导基期，旧→新排列。"""
    y, m = today.year, today.month
    if m >= 11:
        yy, qq = y, 3
    elif m >= 9:
        yy, qq = y, 2
    elif m >= 5:
        yy, qq = y, 1
    else:
        yy, qq = y - 1, 4
    quarters = [(yy, qq)]
    for _ in range(12):
        qq -= 1
        if qq == 0:
            qq, yy = 4, yy - 1
        quarters.append((yy, qq))
    quarters.reverse()
    suffix = {1: "0331", 2: "0630", 3: "0930", 4: "1231"}
    return [f"{a}{suffix[b]}" for a, b in quarters]


def get_profit_quality_snapshot() -> pd.DataFrame:
    """每股盈利质量快照（进程内只拉一次）。
    列：code, loss_q_3y（近12个季度单季亏损次数）,
        ocf_ps_annual（最新年报每股经营现金流，自由现金流近似）。"""
    return _memo("profit_quality", _fetch_profit_quality_snapshot)


@disk_cache(ttl_hours=72)
def _fetch_profit_quality_snapshot() -> pd.DataFrame:
    """akshare stock_yjbb_em 按期批量拉取（每次调用全市场）。

    单季净利 = 本期累计 − 上期累计（一季报即单季）。缺基期的季度跳过不判。
    """
    import akshare as ak
    import time

    today = dt.date.today()
    periods = _loss_check_periods(today)
    range_periods = periods[1:]  # 前 12 个季度（判亏损用）
    annual_period = f"{today.year - 1}1231" if today.month >= 5 else f"{today.year - 2}1231"
    prev_of = {periods[i]: periods[i - 1] for i in range(1, len(periods))}

    cum_np: Dict[str, Dict[str, float]] = {}  # code -> period -> 累计净利
    ocf_map: Dict[str, float] = {}
    for p in periods:
        try:
            raw = ak.stock_yjbb_em(date=p)
        except Exception as e:
            print(f"[data_fetch] stock_yjbb_em({p}) 失败：{e}", file=sys.stderr)
            time.sleep(1.0)
            continue
        if raw is None or raw.empty:
            continue
        c_code = next((c for c in raw.columns if "股票代码" in c), None)
        c_np = next((c for c in raw.columns
                     if "净利润" in c and "同比" not in c and "环比" not in c), None)
        c_ocf = next((c for c in raw.columns if "经营现金流" in c), None)
        if c_code is None or c_np is None:
            continue
        codes = raw[c_code].astype(str).str.zfill(6)
        nps = pd.to_numeric(raw[c_np], errors="coerce")
        for code, npv in zip(codes, nps):
            if pd.notna(npv):
                cum_np.setdefault(code, {})[p] = float(npv)
        if p == annual_period and c_ocf is not None:
            for code, ov in zip(codes, pd.to_numeric(raw[c_ocf], errors="coerce")):
                if pd.notna(ov):
                    ocf_map[code] = float(ov)
        time.sleep(0.8)  # 期与期之间休眠，防限频

    rows = []
    for code, pmap in cum_np.items():
        loss = 0
        for p in range_periods:
            if p not in pmap:
                continue
            if p.endswith("0331"):
                single = pmap[p]  # 一季报累计即单季
            else:
                prev = prev_of.get(p)
                if prev is None or prev not in pmap:
                    continue  # 缺基期无法推导单季，跳过
                single = pmap[p] - pmap[prev]
            if single < 0:
                loss += 1
        rows.append({"code": code, "loss_q_3y": loss,
                     "ocf_ps_annual": ocf_map.get(code)})
    return pd.DataFrame(rows)


# ============================================================
# T4 输入准备用批量快照（业绩报表 + 业绩预告）
# 无论多少只票：yjbb 一次全市场、yjyg 最多两次全市场，绝不逐票。
# ============================================================

def get_yjbb_snapshot() -> pd.DataFrame:
    """最新已完整披露报告期的全市场业绩报表（进程内只拉一次）。
    列：code, industry, revenue, rev_yoy, np, np_yoy, roe, ocf_ps,
        gross_margin, period。"""
    return _memo("yjbb_snapshot", _fetch_yjbb_snapshot)


@disk_cache(ttl_hours=24)
def _fetch_yjbb_snapshot() -> pd.DataFrame:
    import akshare as ak

    y, m = dt.date.today().year, dt.date.today().month
    if m >= 11:
        period = f"{y}0930"
    elif m >= 9:
        period = f"{y}0630"
    elif m >= 5:
        period = f"{y}0331"
    else:
        period = f"{y - 1}1231"
    try:
        raw = ak.stock_yjbb_em(date=period)
    except Exception as e:
        print(f"[data_fetch] stock_yjbb_em({period}) 失败：{e}", file=sys.stderr)
        return pd.DataFrame()
    if raw is None or raw.empty:
        return pd.DataFrame()

    def _col(*cands: str) -> Optional[str]:
        for cand in cands:
            hit = next((c for c in raw.columns if cand in c), None)
            if hit:
                return hit
        return None

    c_code = _col("股票代码")
    if not c_code:
        return pd.DataFrame()
    out = pd.DataFrame({"code": raw[c_code].astype(str).str.zfill(6)})
    for key, cands in [
        ("industry", ("所处行业", "行业")),
        ("revenue", ("营业总收入-营业总收入", "营业收入-营业收入",
                     "营业总收入", "营业收入")),
        ("rev_yoy", ("营业总收入-同比增长", "营业收入-同比增长")),
        ("np", ("净利润-净利润",)),
        ("np_yoy", ("净利润-同比增长",)),
        ("roe", ("净资产收益率",)),
        ("ocf_ps", ("经营现金流",)),
        ("gross_margin", ("销售毛利率",)),
    ]:
        c = _col(*cands)
        if c:
            out[key] = raw[c] if key == "industry" \
                else pd.to_numeric(raw[c], errors="coerce")
    out["period"] = period
    return out.drop_duplicates("code").reset_index(drop=True)


def get_yjyg_snapshot() -> pd.DataFrame:
    """最新业绩预告（进程内只拉一次）。
    列：code, name, preview_type, gain_pct, excerpt（业绩变动）,
        reason（业绩变动原因，供关键词检索）, period。"""
    return _memo("yjyg_snapshot", _fetch_yjyg_snapshot)


@disk_cache(ttl_hours=24)
def _fetch_yjyg_snapshot() -> pd.DataFrame:
    """业绩预告聚合：每股一行。仅保留正面预告（预增/略增/扭亏/续盈/减亏）。
    gain_pct 取利润类指标变动幅度的最大值；excerpt 取对应业绩变动原文。"""
    import akshare as ak

    y, m = dt.date.today().year, dt.date.today().month
    if m >= 11:
        cands = [f"{y}0930", f"{y}0630"]
    elif m >= 9:
        cands = [f"{y}0630", f"{y}0331"]
    elif m >= 5:
        cands = [f"{y}0331", f"{y - 1}1231"]
    else:
        cands = [f"{y - 1}1231", f"{y - 1}0930"]

    positive_types = {"预增", "略增", "扭亏", "续盈", "减亏"}
    for period in cands:
        try:
            raw = ak.stock_yjyg_em(date=period)
        except Exception as e:
            print(f"[data_fetch] stock_yjyg_em({period}) 失败：{e}", file=sys.stderr)
            continue
        if raw is None or raw.empty:
            continue
        c_code = next((c for c in raw.columns if "股票代码" in c), None)
        c_type = next((c for c in raw.columns if "预告类型" in c), None)
        c_gain = next((c for c in raw.columns if "变动幅度" in c), None)
        c_text = next((c for c in raw.columns if "业绩变动" in c
                       and "幅度" not in c and "原因" not in c), None)
        c_reason = next((c for c in raw.columns if "变动原因" in c), None)
        if not c_code or not c_type:
            continue

        raw = raw[raw[c_type].astype(str).str.strip().isin(positive_types)].copy()
        if raw.empty:
            continue
        raw["_gain"] = pd.to_numeric(raw.get(c_gain), errors="coerce") if c_gain else None
        raw["_is_profit"] = raw["预测指标"].astype(str).str.contains("净利润") \
            if "预测指标" in raw.columns else True

        rows = []
        for code, grp in raw.groupby(raw[c_code].astype(str).str.zfill(6)):
            profit_rows = grp[grp["_is_profit"]]
            best_src = profit_rows if not profit_rows.empty else grp
            if c_gain and best_src["_gain"].notna().any():
                best = best_src.loc[best_src["_gain"].idxmax()]
                gain = float(best["_gain"])
            else:
                best = best_src.iloc[0]
                gain = None
            text = str(best[c_text]).strip() if c_text and pd.notna(best[c_text]) else ""
            reason = ""
            if c_reason:
                # 优先取 best 行的原因；为空则取该票任意非空原因
                cand = best.get(c_reason)
                if pd.notna(cand) and str(cand).strip():
                    reason = str(cand).strip()
                else:
                    nn = best_src[c_reason].dropna()
                    nn = nn[nn.astype(str).str.strip() != ""]
                    if not nn.empty:
                        reason = str(nn.iloc[0]).strip()
            rows.append({
                "code": code,
                "name": str(best.get("股票简称", "") or ""),
                "preview_type": str(best[c_type]).strip(),
                "gain_pct": gain,
                "excerpt": text,
                "reason": reason,
                "period": period,
            })
        if rows:
            return pd.DataFrame(rows).reset_index(drop=True)
    return pd.DataFrame()


def get_batch_fundamentals(codes: List[str]) -> pd.DataFrame:
    """批量获取一组股票的基本面，返回以 code 为索引的 DataFrame。

    数据源优先级：
      1. tushare 全市场快照（含 dv_ttm 股息率）
      2. 腾讯批量行情（pe/pb/市值/现价，无股息率，但绝不逐票、频率极低）
    ROE（财报指标，行情源无）单独由 get_roe_snapshot 批量补充。
    列：pe_ttm, pb, dv_ttm, dv_ratio, total_mv, price, name, roe（视来源而定）。
    """
    codes = [c.split(".")[0].zfill(6) for c in codes]
    codes = list(dict.fromkeys(codes))
    if not codes:
        return pd.DataFrame()
    wanted = set(codes)

    snap = get_fundamentals_snapshot()
    if not snap.empty:
        sub = snap[snap["code"].isin(wanted)]
        # 覆盖率够就用快照（可能缺个别停牌票，交给腾讯补）
        if len(sub) >= max(1, int(len(codes) * 0.6)):
            return _attach_roe(sub).set_index("code")

    # 降级：腾讯批量行情
    tq = get_tencent_batch_quotes(codes)
    if not tq.empty:
        return _attach_roe(tq).set_index("code")
    return pd.DataFrame()


def _annualize_roe(roe_df: pd.DataFrame) -> pd.DataFrame:
    """单季 ROE → 年化近似（策略阈值是年度口径）。Q1×4、中报×2、三季报×4/3、年报×1。"""
    df = roe_df.copy()
    if "period" in df.columns:
        def _factor(p: str) -> float:
            p = str(p)
            if p.endswith("0331"):
                return 4.0
            if p.endswith("0630"):
                return 2.0
            if p.endswith("0930"):
                return 4.0 / 3.0
            return 1.0
        df["roe"] = df["roe"] * df["period"].map(_factor)
    return df[["code", "roe"]]


def _attach_roe(df: pd.DataFrame) -> pd.DataFrame:
    """给基本面表补上 ROE 列（财报快照，批量一次，年化近似；失败时保持原样）。"""
    if df.empty or "roe" in df.columns:
        return df
    roe_df = get_roe_snapshot()
    if roe_df.empty:
        return df
    roe_ann = _annualize_roe(roe_df)
    merged = df.merge(roe_ann, on="code", how="left")
    # 快照路径回填缓存，两桶共用（快照可能为空表：无列，须判断）
    snap = _RUNTIME_CACHE.get("fundamentals_snapshot")
    if not (snap is None or snap.empty) and "code" in snap.columns and "roe" not in snap.columns:
        _RUNTIME_CACHE["fundamentals_snapshot"] = snap.merge(roe_ann, on="code", how="left")
    return merged


def get_stock_fundamentals(code: str) -> pd.DataFrame:
    """单票关键指标（兼容旧调用）。内部走批量快照，不逐票请求。"""
    pure = code.split(".")[0].zfill(6)
    df = get_batch_fundamentals([pure])
    if df.empty or pure not in df.index:
        return pd.DataFrame()
    row = df.loc[[pure]].reset_index()
    return row


# ============================================================
# 单点入口测试
# ============================================================

if __name__ == "__main__":
    print("=== 交易日历 ===")
    from lib.trading_day import is_trading_day

    print(f"today is_trading_day: {is_trading_day()}")

    print("\n=== 中证红利股息率分位 ===")
    r = get_dividend_yield_percentile()
    print(r)

    print("\n=== 10Y 国债 ===")
    print(get_latest_10y_yield())

    print("\n=== 全 A 20日均量分位 ===")
    print(get_market_turnover_percentile())

    print("\n=== 沪深300 20日回撤 ===")
    print(get_hs300_drawdown())
