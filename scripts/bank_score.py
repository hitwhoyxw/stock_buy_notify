# -*- coding: utf-8 -*-
"""银行股合理PB模型打分。

合理 PB = (ROE - g) / (r - g)
  ROE = 净资产收益率
  g   = 可持续增长率（净利润增长率近似）
  r   = 要求回报率（10%，约等于 10Y国债 + 4% 风险溢价）
"""
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")

import akshare as ak

banks = {
    "600036": "招商银行", "601838": "成都银行", "600919": "江苏银行",
    "601009": "南京银行", "601166": "兴业银行", "601998": "中信银行",
    "601963": "重庆银行", "601077": "渝农商行", "601288": "农业银行",
    "601939": "建设银行", "601658": "邮储银行", "601328": "交通银行",
    "601818": "光大银行", "600015": "华夏银行", "601997": "贵阳银行",
}

# candidates_A.csv 已有数据
existing = {
    "600036": {"dy": 5.18, "roe": 13.5, "pb": 0.89},
    "601838": {"dy": 5.07, "roe": 14.0, "pb": 0.87},
    "600919": {"dy": 4.77, "roe": 16.4, "pb": 0.80},
    "601009": {"dy": 4.70, "roe": 14.3, "pb": 0.75},
    "601166": {"dy": 5.85, "roe": 11.6, "pb": 0.47},
    "601998": {"dy": 5.06, "roe": 11.1, "pb": 0.57},
    "601963": {"dy": 4.01, "roe": 12.6, "pb": 0.66},
    "601077": {"dy": 4.92, "roe": 11.9, "pb": 0.53},
    "601288": {"dy": 3.87, "roe": 10.6, "pb": 0.81},
    "601939": {"dy": 3.82, "roe": 9.8,  "pb": 0.76},
    "601658": {"dy": 4.41, "roe": 9.7,  "pb": 0.58},
    "601328": {"dy": 4.71, "roe": 9.1,  "pb": 0.53},
    "601818": {"dy": 5.78, "roe": 8.0,  "pb": 0.36},
    "600015": {"dy": 6.23, "roe": 6.3,  "pb": 0.34},
    "601997": {"dy": 5.35, "roe": 9.1,  "pb": 0.32},
}

r = 0.10  # 要求回报率

print("code    name      ROE%   g%    PB   fairPB  ratio  dy%   score")
print("-" * 72)

results = []
for code, name in banks.items():
    # 拉取净利润增长率
    g = 0.03  # 默认
    try:
        df = ak.stock_financial_analysis_indicator(symbol=code, start_year="2024")
        if df is not None and not df.empty:
            latest = df.iloc[-1]
            np_g = latest.get("净利润增长率(%)")
            nav_g = latest.get("净资产增长率(%)")
            import pandas as pd
            raw = np_g if pd.notna(np_g) else nav_g
            if pd.notna(raw):
                g = float(raw) / 100.0
                # Gordon模型要求 g < r，长期增长率不可能超要求回报率
                # 截断到 [0, r*0.8] = [0, 0.08]
                if g < 0:
                    g = 0.0
                if g > r * 0.8:
                    g = r * 0.8  # 8%
    except Exception as e:
        pass

    e = existing[code]
    roe = e["roe"] / 100.0
    pb = e["pb"]
    dy = e["dy"]

    # 合理 PB = (ROE - g) / (r - g)
    denom = r - g
    if abs(denom) > 0.001:
        fair_pb = (roe - g) / denom
    else:
        fair_pb = 0.0
    fair_pb = max(fair_pb, 0.0)  # 不出现负值

    ratio = fair_pb / pb if pb > 0 else 0.0

    # 打分（100分制）
    # 1. 估值差 ratio = fairPB / actualPB（35分）
    if ratio >= 2.0:
        s_val = 35
    elif ratio >= 1.5:
        s_val = 30
    elif ratio >= 1.2:
        s_val = 24
    elif ratio >= 1.0:
        s_val = 18
    elif ratio >= 0.8:
        s_val = 10
    else:
        s_val = 5

    # 2. ROE 盈利能力（30分）
    roe_pct = e["roe"]
    if roe_pct >= 13:
        s_roe = 30
    elif roe_pct >= 11:
        s_roe = 25
    elif roe_pct >= 9:
        s_roe = 18
    elif roe_pct >= 7:
        s_roe = 10
    else:
        s_roe = 5

    # 3. 股息率（20分）
    if dy >= 5.0:
        s_dy = 20
    elif dy >= 4.0:
        s_dy = 16
    elif dy >= 3.5:
        s_dy = 12
    else:
        s_dy = 6

    # 4. 成长性 g（15分）
    g_pct = g * 100
    if g_pct >= 10:
        s_g = 15
    elif g_pct >= 5:
        s_g = 12
    elif g_pct >= 2:
        s_g = 8
    elif g_pct >= 0:
        s_g = 5
    else:
        s_g = 2

    score = s_val + s_roe + s_dy + s_g
    results.append((code, name, roe_pct, g_pct, pb, fair_pb, ratio, dy, score))
    print(f"{code:<8}{name:<10}{roe_pct:<7.1f}{g_pct:<6.1f}{pb:<6.2f}{fair_pb:<8.2f}{ratio:<7.2f}{dy:<6.2f}{score}")
    time.sleep(0.3)

print()
print("=== 按打分降序排列 ===")
results.sort(key=lambda x: -x[8])
for rank, (code, name, roe_pct, g_pct, pb, fair_pb, ratio, dy, score) in enumerate(results, 1):
    print(f"{rank:>2}. {code} {name:<8}  ROE={roe_pct:.1f}%  g={g_pct:.1f}%  PB={pb:.2f}  合理PB={fair_pb:.2f}  估值差={ratio:.2f}  股息={dy:.2f}%  得分={score}")
