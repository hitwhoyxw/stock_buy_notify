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
# 指数行情
# ============================================================

@disk_cache(ttl_hours=6)
def get_index_daily(code: str, start: str, end: str) -> pd.DataFrame:
    """指数日线。返回列：date, open, close, high, low, volume, amount。
    code 支持格式：000922 / 000922.SH / sh000922，内部统一处理。
    """
    import akshare as ak

    # akshare 用 sh000922 / sz399006 前缀
    pure = code.split(".")[0]
    if pure.startswith(("sh", "sz")):
        symbol = pure
    elif pure.startswith("0") or pure.startswith("399"):
        # 000xxx 属沪深两市：000922/000300 归属上交所
        symbol = f"sh{pure}" if pure.startswith("000") else f"sz{pure}"
    else:
        symbol = f"sh{pure}"

    try:
        df = ak.stock_zh_index_daily(symbol=symbol)
    except Exception:
        df = ak.index_zh_a_hist(symbol=pure, period="daily", start_date=start.replace("-", ""), end_date=end.replace("-", ""))
        df = df.rename(columns={"日期": "date", "开盘": "open", "收盘": "close", "最高": "high", "最低": "low", "成交量": "volume", "成交额": "amount"})

    if df is None or df.empty:
        return pd.DataFrame()
    df["date"] = pd.to_datetime(df["date"]).dt.date
    df = df[(df["date"] >= pd.to_datetime(start).date()) & (df["date"] <= pd.to_datetime(end).date())]
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

    数据来源：akshare stock_zh_index_value_csindex（中证指数官方接口）。
    """
    import akshare as ak

    try:
        df = ak.stock_zh_index_value_csindex(symbol="H30269")  # 中证红利全收益
    except Exception:
        try:
            df = ak.stock_zh_index_value_csindex(symbol="000922")
        except Exception as e:
            print(f"[data_fetch] 中证红利股息率拉取失败：{e}", file=sys.stderr)
            return pd.DataFrame()

    if df is None or df.empty:
        return pd.DataFrame()
    # 列名可能是 日期/股息率1 或 date/dividend_yield
    col_date = next((c for c in df.columns if "日期" in c or "date" in c.lower()), df.columns[0])
    col_dy = next((c for c in df.columns if "股息" in c or "dividend" in c.lower()), None)
    if col_dy is None:
        return pd.DataFrame()
    out = pd.DataFrame({
        "date": pd.to_datetime(df[col_date]).dt.date,
        "dividend_yield": pd.to_numeric(df[col_dy], errors="coerce"),
    }).dropna()
    cutoff = dt.date.today() - dt.timedelta(days=int(years * 366))
    return out[out["date"] >= cutoff].reset_index(drop=True)


def get_dividend_yield_percentile(years: int = 5) -> Optional[Dict[str, float]]:
    """当前中证红利股息率在近 N 年历史中的分位。返回 {current, percentile}。"""
    df = get_csi_dividend_yield_history(years=years + 1)
    if df.empty or len(df) < 60:
        return None
    cutoff = dt.date.today() - dt.timedelta(days=int(years * 365.25))
    win = df[df["date"] >= cutoff].sort_values("date")
    if len(win) < 60:
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
    """全 A 每日成交额。返回 date, amount（单位：元）。"""
    import akshare as ak

    try:
        df = ak.stock_zh_a_spot_em()  # 无历史，这个只能拿当日 → 需要走指数
    except Exception:
        pass

    try:
        # 用 Wind 全 A 指数（8841388.WI 无 akshare 接口），改用上证综指 + 深证成指之和近似
        end = dt.date.today().strftime("%Y%m%d")
        start = (dt.date.today() - dt.timedelta(days=days * 2)).strftime("%Y%m%d")
        sh = ak.stock_zh_index_daily(symbol="sh000001")
        sz = ak.stock_zh_index_daily(symbol="sz399001")
    except Exception as e:
        print(f"[data_fetch] 市场成交额拉取失败：{e}", file=sys.stderr)
        return pd.DataFrame()

    for d in (sh, sz):
        d["date"] = pd.to_datetime(d["date"]).dt.date

    amount_col = "amount" if "amount" in sh.columns else "成交额"
    if amount_col not in sh.columns:
        return pd.DataFrame()

    merged = pd.merge(
        sh[["date", amount_col]].rename(columns={amount_col: "sh_amt"}),
        sz[["date", amount_col]].rename(columns={amount_col: "sz_amt"}),
        on="date", how="inner",
    )
    merged["amount"] = merged["sh_amt"] + merged["sz_amt"]
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

    try:
        df = ak.index_stock_cons_csindex(symbol="000922")
    except Exception as e:
        print(f"[data_fetch] 中证红利成分股拉取失败：{e}", file=sys.stderr)
        return pd.DataFrame()
    if df is None or df.empty:
        return pd.DataFrame()

    col_code = next((c for c in df.columns if "代码" in c or "code" in c.lower()), df.columns[0])
    col_name = next((c for c in df.columns if "名称" in c or "name" in c.lower()), df.columns[1])
    col_w = next((c for c in df.columns if "权重" in c or "weight" in c.lower()), None)
    out = pd.DataFrame({
        "code": df[col_code].astype(str).str.zfill(6),
        "name": df[col_name],
        "weight": pd.to_numeric(df[col_w], errors="coerce") if col_w else None,
    })
    return out


# ============================================================
# 单票基本面（股息率、PB、ROE）
# ============================================================

@disk_cache(ttl_hours=12)
def get_stock_fundamentals(code: str) -> pd.DataFrame:
    """单票关键指标：股息率 TTM、PB、PE-TTM、ROE、总市值。返回单行 DataFrame。"""
    import akshare as ak

    pure = code.split(".")[0]
    try:
        df = ak.stock_a_indicator_lg(symbol=pure)
    except Exception as e:
        print(f"[data_fetch] {code} 指标拉取失败：{e}", file=sys.stderr)
        return pd.DataFrame()
    if df is None or df.empty:
        return pd.DataFrame()
    df["date"] = pd.to_datetime(df.iloc[:, 0]).dt.date
    return df.sort_values("date").tail(1).reset_index(drop=True)


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
