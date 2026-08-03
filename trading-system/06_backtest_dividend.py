# -*- coding: utf-8 -*-
"""
三桶策略 · A桶(红利) 股息率分位买入信号 历史回测
=================================================
数据: 价格=sina日线(直连), 分红=akshare stock_dividend_cninfo(巨潮)
信号: 个股 TTM 股息率 处于 过去5年 滚动分位的 阈值% 以上 -> 买入
持有: 60 / 120 / 250 交易日后平仓, 统计胜率与收益
扫描阈值: 60 / 70 / 80 / 90 分位
注: 本环境 eastmoney 行情接口被沙箱拦截, 故价格走 sina; 脚本已预留切回 eastmoney/akshare 的接口。
"""
import urllib.request, json, ssl, time, datetime as dt
from collections import defaultdict

import akshare as ak

CTX = ssl.create_default_context(); CTX.check_hostname=False; CTX.verify_mode=ssl.CERT_NONE

def http_get(url, ref="https://finance.sina.com.cn"):
    req = urllib.request.Request(url, headers={"User-Agent":"Mozilla/5.0","Referer":ref})
    return urllib.request.urlopen(req, timeout=20, context=CTX).read().decode("utf-8","ignore")

def fetch_sina_kline(symbol):
    """symbol like sh601398 ; 返回 [(date, close), ...] 升序"""
    url = (f"https://money.finance.sina.com.cn/quotes_service/api/json_v2.php/CN_MarketData.getKLineData"
           f"?symbol={symbol}&scale=240&ma=no&datalen=2500")
    for _ in range(4):
        try:
            raw = http_get(url)
            if not raw.strip().startswith("["):
                time.sleep(1.5); continue
            data = json.loads(raw)
            out = [(r["day"], float(r["close"])) for r in data]
            return out
        except Exception as e:
            time.sleep(1.5)
    raise RuntimeError(f"sina fail {symbol}: {e}")

def fetch_dividends(code):
    """返回 [(ex_date_str, dps_per_share), ...]"""
    df = ak.stock_dividend_cninfo(symbol=code)
    res = []
    for _, row in df.iterrows():
        try:
            ex = str(row["除权日"])[:10]
            if ex in ("NaT", "None", "nan", "") or len(ex) < 10:
                continue
            to_dt(ex)                        # 校验日期合法
            ratio = row["派息比例"]          # 每10股派X元
            if ratio is None or (isinstance(ratio,float) and ratio!=ratio):
                continue
            dps = float(ratio)/10.0
            res.append((ex, dps))
        except Exception:
            continue
    return sorted(res)

def to_dt(s):
    return dt.datetime.strptime(s, "%Y-%m-%d").date()

def build_ttm_dps(divs, dates):
    """对每只交易日, 计算 往前365天窗口内的 每股分红合计(TTM)"""
    divs = [(to_dt(d), v) for d, v in divs]
    out = []
    for ds, _ in dates:
        d = to_dt(ds)
        s = sum(v for dd, v in divs if (d - dd).days <= 365 and (d - dd).days >= 0)
        out.append(s)
    return out

def rolling_percentile(series, window):
    """对序列每个点, 计算其在 [i-window+1, i] 窗口内的分位(0-100), 窗口不足返回None"""
    n = len(series); res = [None]*n
    for i in range(n):
        lo = max(0, i-window+1)
        if i-lo+1 < 250:   # 至少需要约1年数据才开始给信号
            continue
        w = series[lo:i+1]
        cur = series[i]
        res[i] = 100.0*sum(1 for x in w if x <= cur)/len(w)
    return res

def backtest(symbol, code, thresholds, horizons):
    kline = fetch_sina_kline(symbol)
    if len(kline) < 600:
        return None
    divs = fetch_dividends(code)
    dates = [d for d, _ in kline]
    closes = [c for _, c in kline]
    ttm = build_ttm_dps(divs, kline)
    yields = []
    for i, c in enumerate(closes):
        yields.append((ttm[i]/c) if c > 0 and ttm[i] > 0 else 0.0)
    pct = rolling_percentile(yields, 1210)   # ~5年
    n = len(closes)
    # 记录每次买入的 forward return
    records = defaultdict(lambda: defaultdict(list))
    holding = {th: False for th in thresholds}
    for i in range(n):
        if pct[i] is None:
            continue
        for th in thresholds:
            sig = pct[i] >= th
            if sig and not holding[th]:
                holding[th] = True
                for H in horizons:
                    j = i + H
                    if j < n:
                        records[th][H].append(closes[j]/closes[i]-1)
            if not sig:
                holding[th] = False
    return records

# ---------- 红利候选池 ----------
UNIVERSE = [
    ("sh601398","601398","工行"),("sh601939","601939","建行"),("sh601288","601288","农行"),
    ("sh601988","601988","中行"),("sh601328","601328","交行"),("sh600036","600036","招行"),
    ("sh601166","601166","兴业"),("sh600016","600016","民生"),("sh601998","601998","中信"),
    ("sh600000","600000","浦发"),("sh601088","601088","中国神华"),("sh601225","601225","陕西煤业"),
    ("sh600188","600188","兖矿"),("sh600050","600050","中国联通"),("sh600900","600900","长电"),
    ("sh600011","600011","华能"),("sh600795","600795","国电"),("sh600377","600377","宁沪"),
    ("sh600350","600350","山东高速"),("sh600028","600028","中石化"),("sh601857","601857","中石油"),
]

THRESHOLDS = [60,70,80,90]
HORIZONS = [60,120,250]

def main():
    agg = {th:{H:[] for H in HORIZONS} for th in THRESHOLDS}
    per_stock = {}
    for sym, code, name in UNIVERSE:
        try:
            rec = backtest(sym, code, THRESHOLDS, HORIZONS)
            if rec is None:
                print(f"  skip {name}({code}) 数据不足")
                continue
            per_stock[name] = rec
            for th in THRESHOLDS:
                for H in HORIZONS:
                    agg[th][H].extend(rec[th][H])
            print(f"  ok {name}({code})")
        except Exception as e:
            print(f"  ERR {name}({code}): {repr(e)[:80]}")
        time.sleep(0.4)
    # 汇总
    print("\n==================== 回测汇总 ====================")
    print("阈值  持有   样本数   胜率%   中位收益%   均值收益%")
    for th in THRESHOLDS:
        for H in HORIZONS:
            vals = agg[th][H]
            if not vals:
                print(f"{th:>3}  {H:>4}   0        -         -          -")
                continue
            win = 100.0*sum(1 for v in vals if v>0)/len(vals)
            med = sorted(vals)[len(vals)//2]*100
            mean = sum(vals)/len(vals)*100
            print(f"{th:>3}  {H:>4}  {len(vals):>5}  {win:>6.1f}  {med:>8.1f}  {mean:>8.1f}")
    # 保存 csv
    import csv
    with open("06_backtest_dividend_result.csv","w",newline="",encoding="utf-8") as f:
        w=csv.writer(f)
        w.writerow(["threshold","horizon","n","win_rate_pct","median_ret_pct","mean_ret_pct"])
        for th in THRESHOLDS:
            for H in HORIZONS:
                vals=agg[th][H]
                if vals:
                    win=100.0*sum(1 for v in vals if v>0)/len(vals)
                    med=sorted(vals)[len(vals)//2]*100
                    mean=sum(vals)/len(vals)*100
                    w.writerow([th,H,len(vals),round(win,2),round(med,2),round(mean,2)])
    print("\n结果已保存 -> 06_backtest_dividend_result.csv")

if __name__ == "__main__":
    main()
