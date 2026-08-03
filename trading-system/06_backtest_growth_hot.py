# -*- coding: utf-8 -*-
"""
三桶策略 · B桶(成长) 与 C桶(热门) 业绩动量信号 历史回测
========================================================
数据: 业绩=akshare stock_yjbb_em(东财,可达); 价格=sina日线(直连)
B桶信号: 净利润同比增长>=25% 且 季度环比>=0(未减速) 且 ROE>=12%
C桶信号(热门/周期代理): 净利润同比增长>=50% 且 季度环比>0(加速)
买入: 财报披露日近似(报告期末+15日) 的收盘价
持有: 60 / 120 交易日后统计胜率与收益
说明: 这是"业绩动量"代理信号, 未含 C桶的关键词/行业景气判定(需另接文本与行业指数源)。
"""
import urllib.request, json, ssl, time, datetime as dt
import akshare as ak
from collections import defaultdict

CTX = ssl.create_default_context(); CTX.check_hostname=False; CTX.verify_mode=ssl.CERT_NONE
_kline_cache = {}

def http_get(url, ref="https://finance.sina.com.cn"):
    req = urllib.request.Request(url, headers={"User-Agent":"Mozilla/5.0","Referer":ref})
    return urllib.request.urlopen(req, timeout=20, context=CTX).read().decode("utf-8","ignore")

def fetch_sina_kline(symbol):
    if symbol in _kline_cache: return _kline_cache[symbol]
    url = (f"https://money.finance.sina.com.cn/quotes_service/api/json_v2.php/CN_MarketData.getKLineData"
           f"?symbol={symbol}&scale=240&ma=no&datalen=2500")
    out=None
    for _ in range(4):
        try:
            raw=http_get(url)
            if raw.strip().startswith("["):
                out=[(r["day"], float(r["close"])) for r in json.loads(raw)]
                break
        except Exception:
            pass
        time.sleep(1.2)
    _kline_cache[symbol]=out
    return out

def to_dt(s): return dt.datetime.strptime(s,"%Y-%m-%d").date()

def nearest_trading_day_after(kline, target):
    ds=[to_dt(d) for d,_ in kline]
    for i,d in enumerate(ds):
        if d >= target:
            return i
    return None

def forward_ret(kline, idx, H):
    j=idx+H
    if j>=len(kline): return None
    return kline[j][1]/kline[idx][1]-1

# 报告期(年末/季末) -> 近似披露日
DATES=["20230331","20230630","20230930","20231231",
       "20240331","20240630","20240930","20241231"]

def main(stock_cap=220):
    growth=defaultdict(list); hot=defaultdict(list)
    seen=set()
    for date in DATES:
        try:
            df=ak.stock_yjbb_em(date=date)
        except Exception as e:
            print("yjbb ERR",date,repr(e)[:60]); continue
        sig_g=[]; sig_h=[]
        for _,r in df.iterrows():
            try:
                yoy=r.get("净利润-同比增长"); qoq=r.get("净利润-季度环比增长"); roe=r.get("净资产收益率")
                if yoy is None or qoq is None: continue
                yoy=float(yoy); qoq=float(qoq)
                code=str(r["股票代码"])
                sym=("sh" if code.startswith(("60","68","90","11","13")) else "sz")+code
                if yoy>=25 and qoq>=0:
                    roev=float(roe) if roe is not None else 0
                    if roev>=12: sig_g.append(sym)
                if yoy>=50 and qoq>0:
                    sig_h.append(sym)
            except Exception:
                continue
        # 取样本, 控制总量
        def take(lst):
            new=[s for s in lst if s not in seen]
            return new
        ng,tk=take(sig_g),take(sig_h)
        # 拉取需要的kline(受 cap 限制)
        need=list(dict.fromkeys(ng+tk))
        for s in need:
            if len(seen)>=stock_cap: break
            fetch_sina_kline(s); seen.add(s)
        # 计算forward return
        tgt=to_dt(date[:4]+"-"+date[4:6]+"-"+date[6:])+dt.timedelta(days=15)
        for s in sig_g:
            kl=fetch_sina_kline(s)
            if not kl: continue
            idx=nearest_trading_day_after(kl,tgt)
            if idx is None: continue
            for H in (60,120):
                ret=forward_ret(kl,idx,H)
                if ret is not None: growth[H].append(ret)
        for s in sig_h:
            kl=fetch_sina_kline(s)
            if not kl: continue
            idx=nearest_trading_day_after(kl,tgt)
            if idx is None: continue
            for H in (60,120):
                ret=forward_ret(kl,idx,H)
                if ret is not None: hot[H].append(ret)
        print(f"  {date}: 成长候选{len(sig_g)} 热门候选{len(sig_h)} 已拉kline{len(seen)}")
        time.sleep(0.3)

    print("\n========== B桶(成长) 业绩动量回测 ==========")
    print("持有   样本   胜率%   中位%   均值%")
    for H in (60,120):
        v=growth[H]
        if not v: print(H,"无样本"); continue
        win=100*sum(1 for x in v if x>0)/len(v)
        med=sorted(v)[len(v)//2]*100; mean=sum(v)/len(v)*100
        print(f"{H:>4}  {len(v):>5}  {win:>6.1f}  {med:>6.1f}  {mean:>6.1f}")
    print("\n========== C桶(热门) 业绩动量回测(代理) ==========")
    print("持有   样本   胜率%   中位%   均值%")
    for H in (60,120):
        v=hot[H]
        if not v: print(H,"无样本"); continue
        win=100*sum(1 for x in v if x>0)/len(v)
        med=sorted(v)[len(v)//2]*100; mean=sum(v)/len(v)*100
        print(f"{H:>4}  {len(v):>5}  {win:>6.1f}  {med:>6.1f}  {mean:>6.1f}")

    import csv
    with open("06_backtest_growth_hot_result.csv","w",newline="",encoding="utf-8") as f:
        w=csv.writer(f); w.writerow(["bucket","horizon","n","win_rate_pct","median_ret_pct","mean_ret_pct"])
        for b,res in (("B成长",growth),("C热门",hot)):
            for H in (60,120):
                v=res[H]
                if v:
                    win=100*sum(1 for x in v if x>0)/len(v)
                    w.writerow([b,H,len(v),round(win,2),round(sorted(v)[len(v)//2]*100,2),round(sum(v)/len(v)*100,2)])
    print("\n已保存 06_backtest_growth_hot_result.csv")

if __name__=="__main__":
    main()
