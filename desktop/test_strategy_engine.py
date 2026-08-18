# -*- coding: utf-8 -*-
"""strategy_engine 单元测试：条件树求值 / 交叉判断 / 三值逻辑 / 旧格式兼容。

直接运行：cd desktop && python test_strategy_engine.py（无 pytest 依赖，
不依赖 PyQt / 网络，全部基于合成 K 线）。
"""
from __future__ import annotations

import pandas as pd

from strategy_engine import (
    IndicatorContext, StrategyConfigError, describe_condition,
    evaluate_condition, evaluate_strategy, primary_value,
    validate_condition,
)


def make_kline(closes, volumes=None):
    """由收盘序列合成日K（high/low 由 close 派生，升序）。"""
    n = len(closes)
    vols = volumes if volumes is not None else [10000.0] * n
    dates = pd.date_range("2026-01-01", periods=n).strftime("%Y-%m-%d")
    return pd.DataFrame({
        "date": dates,
        "open": [c * 0.995 for c in closes],
        "close": list(closes),
        "high": [c * 1.01 for c in closes],
        "low": [c * 0.99 for c in closes],
        "volume": vols,
    })


def leaf(ind, op, value, **params):
    node = {"indicator": ind, "operator": op, "value": value}
    if params:
        node["params"] = params
    return node


def ref(ind, **params):
    node = {"indicator": ind}
    if params:
        node["params"] = params
    return node


def and_(*children):
    return {"logic": "and", "children": list(children)}


def or_(*children):
    return {"logic": "or", "children": list(children)}


# ============================================================
# 用例1（需求指定）：均线多头排列 AND 量比>=2 能正确触发
# ============================================================

def test_bullish_alignment_with_volume():
    # 80 天匀速上升 → MA5>MA10>MA20>MA60 且现价站上 MA5；末日 3 倍量
    closes = [10.0 * (1.003 ** i) for i in range(80)]
    vols = [10000.0] * 79 + [30000.0]
    ctx = IndicatorContext({"price": closes[-1]}, make_kline(closes, vols))

    tree = and_(
        leaf("ma", ">", ref("ma", period=10), period=5),
        leaf("ma", ">", ref("ma", period=20), period=10),
        leaf("ma", ">", ref("ma", period=60), period=20),
        leaf("price", ">", ref("ma", period=5)),
        leaf("volume_ratio", ">=", 2, window=20),
    )
    assert evaluate_condition(tree, ctx) is True, "多头排列+放量应触发"

    # 量比阈值提高到 5 → and 短路为 False
    tree["children"][-1]["value"] = 5
    assert evaluate_condition(tree, ctx) is False, "量比不足应不触发"

    # 下跌序列 → 多头排列不成立
    down = [10.0 * (0.997 ** i) for i in range(80)]
    ctx2 = IndicatorContext({"price": down[-1]}, make_kline(down))
    tree["children"][-1]["value"] = 2
    assert evaluate_condition(tree, ctx2) is False, "空头走势不应触发多头排列"
    print("PASS 用例1: 均线多头排列 AND 量比>=2")


# ============================================================
# 用例2：MACD 金叉（cross_up 的 offset 语义与序列理论值对照）
# ============================================================

def test_macd_cross_up():
    # V 型：60 天阴跌 + 30 天强反弹
    closes = [20.0 * (0.99 ** i) for i in range(60)] + \
             [20.0 * (0.99 ** 59) * (1.012 ** (i + 1)) for i in range(30)]
    full = IndicatorContext({"price": closes[-1]}, make_kline(closes))
    m = full.macd_series()
    assert m is not None, "K线长度足够应能算出 MACD"

    dif, dea = m["dif"], m["dea"]
    crosses = [j for j in range(1, len(dif))
               if dif.iloc[j - 1] < dea.iloc[j - 1]
               and dif.iloc[j] >= dea.iloc[j]]
    assert crosses, "V型数据应至少存在一个金叉点（否则数据构造不合理）"

    j = crosses[0]
    tree = leaf("macd", "cross_up", ref("macd", field="dea"), field="dif")

    # 截断到金叉当日（最后一天 = j）→ True
    ctx_hit = IndicatorContext({"price": closes[j]},
                               make_kline(closes[:j + 1]))
    assert evaluate_condition(tree, ctx_hit) is True, "金叉当日应触发"

    # 截断到前一天 → False（金叉未发生）
    ctx_pre = IndicatorContext({"price": closes[j - 1]},
                               make_kline(closes[:j]))
    assert evaluate_condition(tree, ctx_pre) is False, "金叉前一日不应触发"

    # 死叉反向：金叉当日 cross_down 应为 False
    tree_dn = leaf("macd", "cross_down", ref("macd", field="dea"), field="dif")
    assert evaluate_condition(tree_dn, ctx_hit) is False

    # 组合：MACD 金叉 AND 量比>=1.5（末日放量）
    vols = [10000.0] * (len(closes) - 1) + [20000.0]
    ctx_combo = IndicatorContext({"price": closes[-1]},
                                 make_kline(closes, vols))
    combo = and_(tree,
                 leaf("volume_ratio", ">=", 1.5, window=20))
    # 最后一日未必是金叉日，按理论值对照（bool() 避免 numpy 标量 is 比较）
    last = len(closes) - 1
    expect = bool(dif.iloc[last - 1] < dea.iloc[last - 1]
                  and dif.iloc[last] >= dea.iloc[last])
    assert evaluate_condition(combo, ctx_combo) is expect, \
        "组合条件应与序列理论值一致"
    print(f"PASS 用例2: MACD金叉 cross_up（金叉点 index={j}，理论对照一致）")


# ============================================================
# 用例3：旧格式三列（indicator/operator/threshold）零迁移兼容
# ============================================================

def test_legacy_fallback():
    down = [10.0 * (0.995 ** i) for i in range(80)]
    ctx = IndicatorContext({"price": down[-1]}, make_kline(down))
    s1 = {"indicator": "price_vs_ma60", "operator": "<", "threshold": "0"}
    assert evaluate_strategy(s1, ctx) is True, "跌破MA60 应触发 S1"

    up = [10.0 * (1.003 ** i) for i in range(80)]
    ctx2 = IndicatorContext({"price": up[-1]}, make_kline(up))
    assert evaluate_strategy(s1, ctx2) is False

    # cost_basis_gain：无持仓成本 → None（策略跳过）
    s4 = {"indicator": "cost_basis_gain", "operator": ">=", "threshold": "40"}
    assert evaluate_strategy(s4, ctx2) is None

    # 阈值非法/缺失 → None（旧语义：跳过）
    bad = {"indicator": "price_vs_ma60", "operator": "<", "threshold": ""}
    assert evaluate_strategy(bad, ctx2) is None

    # 未知旧指标 → 配置错误（报错而非静默）
    try:
        evaluate_strategy({"indicator": "nope", "operator": "<",
                           "threshold": "1"}, ctx2)
        raise AssertionError("未知指标应抛 StrategyConfigError")
    except StrategyConfigError:
        pass
    print("PASS 用例3: 旧格式三列兼容")


# ============================================================
# 用例4：错误处理与三值逻辑（报错≠静默false；None=数据不足）
# ============================================================

def test_errors_and_kleene():
    short = [10.0] * 30   # 仅 30 根K线，MA60 数据不足
    ctx = IndicatorContext({"price": 10.0}, make_kline(short))

    # 数据不足 → None，而非 False
    assert evaluate_condition(leaf("ma", ">", 0, period=60), ctx) is None

    # and(可判True的叶子, None) → None（数据不足，策略跳过）
    both = and_(leaf("price", ">", 0), leaf("ma", ">", 0, period=60))
    assert evaluate_condition(both, ctx) is None

    # and(False, None) → False（短路：已能判否）
    refute = and_(leaf("price", "<", 0), leaf("ma", ">", 0, period=60))
    assert evaluate_condition(refute, ctx) is False

    # or(False, None) → None；or(True, None) → True
    assert evaluate_condition(or_(leaf("price", "<", 0),
                                  leaf("ma", ">", 0, period=60)), ctx) is None
    assert evaluate_condition(or_(leaf("price", ">", 0),
                                  leaf("ma", ">", 0, period=60)), ctx) is True

    # not(None) → None；not(True) → False
    assert evaluate_condition({"logic": "not", "children": [
        leaf("ma", ">", 0, period=60)]}, ctx) is None
    assert evaluate_condition({"logic": "not", "children": [
        leaf("price", ">", 0)]}, ctx) is False

    # 未知指标 / 参数 / 操作符 / 结构 → 一律报错
    for bad in [
        leaf("nope", ">", 0),
        leaf("ma", ">", 0, period=5, extra=1),
        {"indicator": "price", "operator": "~", "value": 0},
        {"indicator": "price", "operator": ">"},
        {"logic": "xor", "children": []},
        {"logic": "and", "children": []},
        {"logic": "not", "children": [leaf("price", ">", 0),
                                      leaf("price", ">", 0)]},
    ]:
        try:
            evaluate_condition(bad, ctx)
            raise AssertionError(f"应抛配置错误: {bad}")
        except StrategyConfigError:
            pass
    # validate_condition 同样拦下（保存前自检）
    try:
        validate_condition(leaf("nope", ">", 0))
        raise AssertionError("validate_condition 应拦下未知指标")
    except StrategyConfigError:
        pass
    print("PASS 用例4: 错误处理与三值逻辑")


# ============================================================
# 用例5：describe_condition 降级展示（含嵌套括号）+ primary_value
# ============================================================

def test_describe_and_primary_value():
    tree = and_(
        leaf("ma", ">", ref("ma", period=10), period=5),
        or_(leaf("volume_ratio", ">=", 2, window=20),
            leaf("day_change_pct", ">", 5)),
        {"logic": "not", "children": [leaf("bias", "<", -5, period=20)]},
    )
    text = describe_condition(tree)
    assert "MA5 > MA10" in text
    assert "且" in text and "或" in text
    assert "(" in text and ")" in text, "嵌套 or 应加括号"
    assert "非(乖离率MA20(%) < -5)" in text
    print(f"PASS 用例5: describe = {text}")

    closes = [10.0 * (1.003 ** i) for i in range(80)]
    ctx = IndicatorContext({"price": closes[-1]}, make_kline(closes))
    row = {"condition": '{"logic":"and","children":['
                        '{"indicator":"ma","params":{"period":5},'
                        '"operator":">","value":0}]}',
           "indicator": "", "operator": "", "threshold": ""}
    assert evaluate_strategy(row, ctx) is True
    assert primary_value(row, ctx) == ctx.get_ma(5), \
        "primary_value 应取第一个叶子的当日左值"
    print("PASS 用例5: primary_value")


if __name__ == "__main__":
    test_bullish_alignment_with_volume()
    test_macd_cross_up()
    test_legacy_fallback()
    test_errors_and_kleene()
    test_describe_and_primary_value()
    print("\nALL TESTS PASSED")
