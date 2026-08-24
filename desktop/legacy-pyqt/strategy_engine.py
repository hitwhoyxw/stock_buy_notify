"""策略条件引擎：条件树 Schema + 带偏移(offset)的指标计算 + 递归求值。

不依赖 PyQt，仅依赖 pandas —— 可独立 import / 单元测试。

## 一、策略定义 Schema（JSON）

一个策略 = 元信息 + 一棵条件树（condition tree）。存储时元信息放
strategies.csv 各列，条件树整体序列化为 JSON 存 `condition` 列：

    {
      "id": "S6", "name": "均线多头排列确认", "type": "buy",
      "condition": { ...条件树... },
      "action": "多头排列成立，可关注买入机会", "priority": "P1", "enabled": "1"
    }

条件树两种节点：
  组合节点 {"logic": "and"|"or"|"not", "children": [子节点, ...]}
      and/or 至少 1 个子节点；not 必须恰好 1 个。
  叶子节点 {"indicator": "ma", "params": {"period": 5},
           "operator": ">"|"<"|">="|"<="|"=="|"cross_up"|"cross_down",
           "value": 12.3 或 {"indicator": "ma", "params": {"period": 10}}}
      value 既可以是常量，也可以是另一个指标（表达 MA5 > MA10 这类
      指标间比较）；交叉类操作符（cross_up/cross_down）的 value 同样
      两可（MACD.DIF 上穿 MACD.DEA / MACD.HIST 上穿 0）。

## 二、三值逻辑

指标数据不足（K 线不够长、无持仓成本等）叶子返回 None：
  and：任一 False → False；全 True → True；否则 None（数据不足，策略跳过）
  or ：任一 True → True；全 False → False；否则 None
  not：None → None
配置错误（指标名不存在 / 参数不合法 / JSON 解析失败 / 结构错误）抛
StrategyConfigError —— 由调用方捕获并上报，绝不静默当作 false。

## 三、offset 语义

所有指标函数都支持 offset：0 = 最新一根 K 线（今天），1 = 昨天……
金叉/死叉本质就是 f(offset=1) 与 f(offset=0) 的大小关系发生翻转：
  cross_up   = 昨天 A < B  且 今天 A >= B
  cross_down = 昨天 A > B  且 今天 A <= B
"""
from __future__ import annotations

import json
from typing import Callable, Dict, List, Optional

import pandas as pd

__all__ = [
    "StrategyConfigError", "IndicatorContext", "INDICATOR_DEFS",
    "MACD_FIELDS", "evaluate_condition", "evaluate_strategy",
    "validate_condition", "describe_condition", "primary_value",
    "legacy_condition_text",
]


class StrategyConfigError(ValueError):
    """策略条件树配置错误（指标名/参数/结构/JSON 非法）。"""


# ============================================================
# 指标上下文：quote(实时) + kline(日K, 升序) + cost(持仓成本)
# ============================================================

class IndicatorContext:
    """基于实时行情 + 日K + 持仓成本的带 offset 指标计算器。

    所有 get_* 与序列计算均带缓存（同一 ctx 内 MA/MACD 只算一次），
    供一只股票的多个策略复用。
    """

    def __init__(self, quote: Optional[dict], kline: Optional[pd.DataFrame],
                 cost: Optional[float] = None):
        self.quote = quote or {}
        self.kline = kline if (kline is not None and not kline.empty) else None
        self.cost = cost
        self._cache: Dict[tuple, Optional[pd.Series]] = {}

    # ── 序列（公有：测试/绘图可直接用）──

    def _series(self, key: str, builder: Callable[[], Optional[pd.Series]]
                ) -> Optional[pd.Series]:
        if key not in self._cache:
            self._cache[key] = builder()
        return self._cache[key]

    def close_series(self) -> Optional[pd.Series]:
        return self._series("close", lambda: (
            self.kline["close"].reset_index(drop=True)
            if self.kline is not None else None))

    def volume_series(self) -> Optional[pd.Series]:
        return self._series("volume", lambda: (
            self.kline["volume"].reset_index(drop=True)
            if self.kline is not None else None))

    def high_series(self) -> Optional[pd.Series]:
        return self._series("high", lambda: (
            self.kline["high"].reset_index(drop=True)
            if self.kline is not None else None))

    def low_series(self) -> Optional[pd.Series]:
        return self._series("low", lambda: (
            self.kline["low"].reset_index(drop=True)
            if self.kline is not None else None))

    def ma_series(self, period: int) -> Optional[pd.Series]:
        period = int(period)
        return self._series(("ma", period), lambda: (
            self.close_series().rolling(period).mean()
            if self.close_series() is not None and
            len(self.close_series()) >= period else None))

    def macd_series(self) -> Optional[Dict[str, pd.Series]]:
        """国内口径：DIF=EMA12-EMA26，DEA=EMA9(DIF)，HIST=2*(DIF-DEA)。"""
        def _build():
            c = self.close_series()
            if c is None or len(c) < 30:
                return None
            ema12 = c.ewm(span=12, adjust=False).mean()
            ema26 = c.ewm(span=26, adjust=False).mean()
            dif = ema12 - ema26
            dea = dif.ewm(span=9, adjust=False).mean()
            return {"dif": dif, "dea": dea, "hist": (dif - dea) * 2}
        if "macd" not in self._cache:
            self._cache["macd"] = _build()
        return self._cache["macd"]

    # ── 单值访问（offset: 0=今天, 1=昨天, …）──

    @staticmethod
    def _at(series: Optional[pd.Series], offset: int) -> Optional[float]:
        if series is None:
            return None
        i = len(series) - 1 - int(offset)
        if i < 0:
            return None
        v = series.iloc[i]
        return None if pd.isna(v) else float(v)

    def get_price(self, offset: int = 0) -> Optional[float]:
        if offset == 0:
            p = self.quote.get("price")
            if p is not None:
                return float(p)
        return self._at(self.close_series(), offset)

    def get_ma(self, period: int, offset: int = 0) -> Optional[float]:
        return self._at(self.ma_series(period), offset)

    def get_bias(self, period: int, offset: int = 0) -> Optional[float]:
        """乖离率：现价相对 MA(period) 的偏离 (%)。"""
        ma, price = self.get_ma(period, offset), self.get_price(offset)
        if ma is None or price is None or ma <= 0:
            return None
        return (price / ma - 1) * 100

    def get_ma_spread(self, fast: int = 5, slow: int = 60,
                      offset: int = 0) -> Optional[float]:
        """均线发散强度：(MA_fast - MA_slow) / MA_slow (%)。"""
        ma_f, ma_s = self.get_ma(fast, offset), self.get_ma(slow, offset)
        if ma_f is None or ma_s is None or ma_s <= 0:
            return None
        return (ma_f / ma_s - 1) * 100

    def get_macd(self, offset: int = 0) -> Optional[Dict[str, Optional[float]]]:
        m = self.macd_series()
        if m is None:
            return None
        return {k: self._at(v, offset) for k, v in m.items()}

    def get_volume_ratio(self, window: int = 20,
                         offset: int = 0) -> Optional[float]:
        """量比 = 当日量 / 前 window 日均量（分母不含当日）。"""
        v = self.volume_series()
        if v is None:
            return None
        window = int(window)
        end = len(v) - int(offset)          # 当日位于 end-1
        if end < window + 1:
            return None
        cur = v.iloc[end - 1]
        base = v.iloc[end - 1 - window:end - 1].mean()
        if pd.isna(base) or base <= 0:
            return None
        return float(cur) / float(base)

    def get_pct_change(self, offset: int = 0) -> Optional[float]:
        """日涨跌幅(%)（K 线口径：close / 前收 - 1）。"""
        c = self.close_series()
        if c is None or len(c) < int(offset) + 2:
            return None
        i = len(c) - 1 - int(offset)
        prev = c.iloc[i - 1]
        if pd.isna(prev) or prev <= 0:
            return None
        return (c.iloc[i] / prev - 1) * 100

    def get_drawdown_from_high(self, window: int = 180,
                               offset: int = 0) -> Optional[float]:
        hi = self.high_series()
        if hi is None:
            return None
        end = len(hi) - int(offset)
        start = max(0, end - int(window))
        if end - start <= 0:
            return None
        peak = hi.iloc[start:end].max()
        price = self.get_price(offset)
        if peak is None or peak <= 0 or price is None:
            return None
        return (price / peak - 1) * 100

    def get_gain_from_low(self, window: int = 180,
                          offset: int = 0) -> Optional[float]:
        lo = self.low_series()
        if lo is None:
            return None
        end = len(lo) - int(offset)
        start = max(0, end - int(window))
        trough = lo.iloc[start:end].min()
        price = self.get_price(offset)
        if trough is None or trough <= 0 or price is None:
            return None
        return (price / trough - 1) * 100

    def get_cost_basis_gain(self) -> Optional[float]:
        if not self.cost or self.cost <= 0:
            return None
        price = self.get_price(0)
        if price is None:
            return None
        return (price / self.cost - 1) * 100


# ============================================================
# 指标注册表：key -> (中文标签, 参数spec, calc(ctx, params, offset))
# 参数spec: {参数名: (标签, 默认, 最小, 最大)}，值一律按整数处理
# ============================================================

def _c_price(ctx, p, off):
    return ctx.get_price(off)


def _c_day_change(ctx, p, off):
    if off == 0:
        return ctx.quote.get("change_pct")
    return ctx.get_pct_change(off)


def _c_pe_ttm(ctx, p, off):
    return ctx.quote.get("pe_ttm") if off == 0 else None


def _c_cost_gain(ctx, p, off):
    return ctx.get_cost_basis_gain() if off == 0 else None


def _c_ma(ctx, p, off):
    return ctx.get_ma(int(p.get("period", 5)), off)


def _c_bias(ctx, p, off):
    return ctx.get_bias(int(p.get("period", 20)), off)


def _c_ma_spread(ctx, p, off):
    return ctx.get_ma_spread(int(p.get("fast", 5)), int(p.get("slow", 60)), off)


def _c_macd(ctx, p, off):
    m = ctx.get_macd(off)
    if m is None:
        return None
    return m.get(str(p.get("field", "dif")).lower())


def _c_volume_ratio(ctx, p, off):
    return ctx.get_volume_ratio(int(p.get("window", 20)), off)


def _c_pct_change(ctx, p, off):
    return ctx.get_pct_change(off)


def _c_drawdown(ctx, p, off):
    return ctx.get_drawdown_from_high(int(p.get("window", 180)), off)


def _c_gain(ctx, p, off):
    return ctx.get_gain_from_low(int(p.get("window", 180)), off)


def _c_volume(ctx, p, off):
    return ctx._at(ctx.volume_series(), off)


INDICATOR_DEFS = {
    # key: (中文标签, 参数spec, 取值函数)
    "price": ("现价(元)", {}, _c_price),
    "day_change_pct": ("当日涨跌幅(%)", {}, _c_day_change),
    "pct_change": ("日涨跌幅(%)·K线", {}, _c_pct_change),
    "pe_ttm": ("市盈率TTM", {}, _c_pe_ttm),
    "cost_basis_gain": ("持仓浮盈(%)", {}, _c_cost_gain),
    "ma": ("均线MA", {"period": ("周期", 5, 2, 500)}, _c_ma),
    "bias": ("乖离率(现价vs MA,%)", {"period": ("周期", 20, 2, 500)}, _c_bias),
    "ma_spread": ("均线发散(MA快/MA慢,%)",
                  {"fast": ("快线", 5, 2, 250), "slow": ("慢线", 60, 3, 500)},
                  _c_ma_spread),
    "macd": ("MACD", {"field": ("字段dif/dea/hist", 0, 0, 2)}, _c_macd),
    "volume_ratio": ("量比(vs N日均量)", {"window": ("均量窗口", 20, 2, 120)},
                     _c_volume_ratio),
    "volume": ("成交量", {}, _c_volume),
    "drawdown_from_high": ("距N日高点回撤(%)",
                           {"window": ("窗口", 180, 20, 500)}, _c_drawdown),
    "gain_from_low": ("距N日低点涨幅(%)",
                      {"window": ("窗口", 180, 20, 500)}, _c_gain),
}

# macd.field 是三选一字符串（dif/dea/hist）
MACD_FIELDS = ("dif", "dea", "hist")

# 旧 strategies.csv 扁平三列(indicator/operator/threshold) → 新指标 key 映射
LEGACY_MAP = {
    "price": ("price", {}),
    "day_change_pct": ("day_change_pct", {}),
    "pe_ttm": ("pe_ttm", {}),
    "cost_basis_gain": ("cost_basis_gain", {}),
    "price_vs_ma20": ("bias", {"period": 20}),
    "price_vs_ma60": ("bias", {"period": 60}),
    "drawdown_from_high_180d": ("drawdown_from_high", {"window": 180}),
    "gain_from_low_180d": ("gain_from_low", {"window": 180}),
    "volume_ratio_20d": ("volume_ratio", {"window": 20}),
}

LOGIC_OPS = {"and", "or", "not"}
COMPARE_OPS = {"<", "<=", ">", ">=", "=="}
CROSS_OPS = {"cross_up", "cross_down"}
ALL_OPS = COMPARE_OPS | CROSS_OPS

_MAX_DEPTH = 12      # 条件树最大嵌套深度（防手写 JSON 深层套娃）
_MISSING = object()  # 区分"value 未提供"与"value=None"


# ============================================================
# 求值
# ============================================================

def _eval_indicator(ref: dict, ctx: IndicatorContext,
                    offset: int, where: str) -> Optional[float]:
    """按注册表取指标值。指标名/参数非法立即抛 StrategyConfigError。"""
    if not isinstance(ref, dict):
        raise StrategyConfigError(f"{where}: 指标引用必须是对象 {ref!r}")
    key = ref.get("indicator")
    if not key or key not in INDICATOR_DEFS:
        raise StrategyConfigError(f"{where}: 未知指标 {key!r}")
    params = ref.get("params", {})
    if params is None:
        params = {}
    if not isinstance(params, dict):
        raise StrategyConfigError(f"{where}: 指标 {key} 的 params 必须是对象")
    spec = INDICATOR_DEFS[key][1]
    for k in params:
        if k not in spec:
            raise StrategyConfigError(f"{where}: 指标 {key} 不支持参数 {k!r}")
    norm = {}
    for k, v in params.items():
        try:
            if key == "macd" and k == "field":
                norm[k] = str(v).lower()
                if norm[k] not in MACD_FIELDS:
                    raise StrategyConfigError(
                        f"{where}: macd.field 必须是 dif/dea/hist")
            else:
                norm[k] = int(v)
        except (ValueError, TypeError):
            raise StrategyConfigError(f"{where}: 参数 {k}={v!r} 不是合法数值")
    return INDICATOR_DEFS[key][2](ctx, norm, offset)


def _resolve_value(value, ctx: IndicatorContext, offset: int,
                   where: str) -> Optional[float]:
    """value 字段：数字常量 或 另一个指标引用对象。"""
    if isinstance(value, dict):
        return _eval_indicator(value, ctx, offset, where)
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        try:
            return float(value)
        except ValueError:
            raise StrategyConfigError(f"{where}: value 不是数字 {value!r}")
    raise StrategyConfigError(f"{where}: value 类型非法 {value!r}")


def evaluate_condition(node, ctx: IndicatorContext, _depth: int = 0
                       ) -> Optional[bool]:
    """递归求值条件树。True/False/None(None=数据不足，策略跳过)。"""
    if _depth > _MAX_DEPTH:
        raise StrategyConfigError(f"条件树嵌套超过 {_MAX_DEPTH} 层")
    if not isinstance(node, dict):
        raise StrategyConfigError(f"条件节点必须是对象: {node!r}")

    if "logic" in node:
        logic = str(node["logic"]).lower()
        children = node.get("children")
        if logic not in LOGIC_OPS:
            raise StrategyConfigError(f"未知逻辑运算 {node.get('logic')!r}")
        if not isinstance(children, list) or not children:
            raise StrategyConfigError(f"{logic} 节点缺少 children")
        if logic == "not" and len(children) != 1:
            raise StrategyConfigError("not 节点必须恰好 1 个子条件")
        vals = [evaluate_condition(c, ctx, _depth + 1) for c in children]
        if logic == "and":
            if any(v is False for v in vals):
                return False
            return True if all(v is True for v in vals) else None
        if logic == "or":
            if any(v is True for v in vals):
                return True
            return False if all(v is False for v in vals) else None
        v = vals[0]  # not
        return None if v is None else (not v)

    # 叶子节点：单一比较
    key = node.get("indicator")
    where = f"叶子[{key}]"
    op = node.get("operator")
    if op not in ALL_OPS:
        raise StrategyConfigError(f"{where}: 非法操作符 {op!r}")
    left = _eval_indicator(node, ctx, 0, where)
    if op in CROSS_OPS:
        left_prev = _eval_indicator(node, ctx, 1, where)
        if "value" not in node:
            raise StrategyConfigError(f"{where}: 交叉判断缺少 value")
        right = _resolve_value(node["value"], ctx, 0, where)
        right_prev = _resolve_value(node["value"], ctx, 1, where)
        if any(v is None for v in (left, left_prev, right, right_prev)):
            return None  # 今天/昨天任一值缺失 → 无法判定
        if op == "cross_up":
            return left_prev < right_prev and left >= right
        return left_prev > right_prev and left <= right  # cross_down

    if "value" not in node:
        raise StrategyConfigError(f"{where}: 缺少 value")
    right = _resolve_value(node["value"], ctx, 0, where)
    if left is None or right is None:
        return None
    return {"<": left < right, "<=": left <= right, ">": left > right,
            ">=": left >= right, "==": left == right}[op]


def validate_condition(node, _depth: int = 0) -> None:
    """仅校验条件树结构（不取行情数据）：非法抛 StrategyConfigError。

    供 UI 保存策略前自检，把"指标名不存在/参数缺失/结构错误"拦在写入
    CSV 之前，而不是留到监控 tick 时才报。
    """
    if _depth > _MAX_DEPTH:
        raise StrategyConfigError(f"条件树嵌套超过 {_MAX_DEPTH} 层")
    if not isinstance(node, dict):
        raise StrategyConfigError(f"条件节点必须是对象: {node!r}")
    if "logic" in node:
        logic = str(node["logic"]).lower()
        children = node.get("children")
        if logic not in LOGIC_OPS:
            raise StrategyConfigError(f"未知逻辑运算 {node.get('logic')!r}")
        if not isinstance(children, list) or not children:
            raise StrategyConfigError(f"{logic} 节点缺少 children")
        if logic == "not" and len(children) != 1:
            raise StrategyConfigError("not 节点必须恰好 1 个子条件")
        for c in children:
            validate_condition(c, _depth + 1)
        return
    key = node.get("indicator")
    if not key or key not in INDICATOR_DEFS:
        raise StrategyConfigError(f"未知指标 {key!r}")
    params = node.get("params") or {}
    if not isinstance(params, dict):
        raise StrategyConfigError(f"指标 {key} 的 params 必须是对象")
    spec = INDICATOR_DEFS[key][1]
    for k in params:
        if k not in spec:
            raise StrategyConfigError(f"指标 {key} 不支持参数 {k!r}")
    if node.get("operator") not in ALL_OPS:
        raise StrategyConfigError(
            f"叶子[{key}]: 非法操作符 {node.get('operator')!r}")
    if "value" not in node:
        raise StrategyConfigError(f"叶子[{key}]: 缺少 value")
    v = node["value"]
    if isinstance(v, dict):
        vkey = v.get("indicator")
        if not vkey or vkey not in INDICATOR_DEFS:
            raise StrategyConfigError(f"叶子[{key}].value: 未知指标 {vkey!r}")
        vparams = v.get("params") or {}
        if not isinstance(vparams, dict):
            raise StrategyConfigError(
                f"叶子[{key}].value: params 必须是对象")
        vspec = INDICATOR_DEFS[vkey][1]
        for k in vparams:
            if k not in vspec:
                raise StrategyConfigError(
                    f"叶子[{key}].value: 指标 {vkey} 不支持参数 {k!r}")
    elif isinstance(v, bool) or not isinstance(v, (int, float, str)):
        raise StrategyConfigError(f"叶子[{key}].value 类型非法 {v!r}")
    elif isinstance(v, str):
        try:
            float(v)
        except ValueError:
            raise StrategyConfigError(f"叶子[{key}].value 不是数字 {v!r}")


def evaluate_strategy(row: dict, ctx: IndicatorContext) -> Optional[bool]:
    """策略行求值入口：优先 condition 列(JSON 条件树)，否则回退旧三列。

    旧三列 (indicator/operator/threshold) 会先映射成一棵等价叶子树再
    求值，保证存量 S1~S5 策略零迁移可用。
    """
    raw = str(row.get("condition") or "").strip()
    if raw:
        try:
            node = json.loads(raw)
        except json.JSONDecodeError as e:
            raise StrategyConfigError(f"condition JSON 解析失败: {e}")
        return evaluate_condition(node, ctx)

    legacy_key = str(row.get("indicator") or "").strip()
    if not legacy_key:
        raise StrategyConfigError("策略既无 condition 也无 indicator")
    if legacy_key not in LEGACY_MAP:
        raise StrategyConfigError(f"未知指标 {legacy_key!r}")
    key, params = LEGACY_MAP[legacy_key]
    try:
        threshold = float(str(row.get("threshold", "")))
    except (ValueError, TypeError):
        return None  # 阈值缺失/非法：数据不足语义，跳过（与旧行为一致）
    node = {"indicator": key, "params": dict(params),
            "operator": str(row.get("operator", "")), "value": threshold}
    return evaluate_condition(node, ctx)


def _iter_leaves(node) -> List[dict]:
    """前序遍历收集全部叶子（primary_value / describe 内部用）。"""
    if not isinstance(node, dict):
        return []
    if "logic" in node:
        out: List[dict] = []
        for c in node.get("children") or []:
            out.extend(_iter_leaves(c))
        return out
    return [node]


def primary_value(row: dict, ctx: IndicatorContext) -> Optional[float]:
    """提醒展示用"代表值"：条件树第一个叶子今天(offset=0)的左侧指标值。"""
    raw = str(row.get("condition") or "").strip()
    try:
        if raw:
            leaves = _iter_leaves(json.loads(raw))
            return _eval_indicator(leaves[0], ctx, 0, "primary") if leaves \
                else None
        legacy_key = str(row.get("indicator") or "").strip()
        if legacy_key not in LEGACY_MAP:
            return None
        key, params = LEGACY_MAP[legacy_key]
        return _eval_indicator({"indicator": key, "params": params},
                               ctx, 0, "primary")
    except (StrategyConfigError, json.JSONDecodeError, IndexError):
        return None


# ============================================================
# 降级展示：条件树 → 人类可读文本
# ============================================================

_OP_CN = {"cross_up": "上穿", "cross_down": "下穿"}


def _ind_display(key: str, params: dict) -> str:
    p = params or {}
    if key == "ma":
        return f"MA{p.get('period', 5)}"
    if key == "bias":
        return f"乖离率MA{p.get('period', 20)}(%)"
    if key == "ma_spread":
        return f"均线发散MA{p.get('fast', 5)}/MA{p.get('slow', 60)}(%)"
    if key == "macd":
        return f"MACD.{str(p.get('field', 'dif')).upper()}"
    if key == "volume_ratio":
        return f"量比({p.get('window', 20)}日均量)"
    if key == "drawdown_from_high":
        return f"{p.get('window', 180)}日高点回撤(%)"
    if key == "gain_from_low":
        return f"{p.get('window', 180)}日低点涨幅(%)"
    return INDICATOR_DEFS.get(key, (key,))[0]


def _value_display(value) -> str:
    if isinstance(value, dict):
        return _ind_display(str(value.get("indicator", "")),
                            value.get("params") or {})
    if isinstance(value, (int, float)):
        return f"{value:g}"
    return str(value)


def describe_condition(node, parent: str = "") -> str:
    """条件树 → 中文可读字符串（策略表格"条件"列 / 提醒展示用）。

    嵌套时按需加括号：and 里的 or 子树、or 里的 and 子树会带括号。
    """
    if not isinstance(node, dict):
        return str(node)
    if "logic" in node:
        logic = str(node["logic"]).lower()
        subs = [describe_condition(c, logic)
                for c in (node.get("children") or [])]
        if not subs:
            return "(空)"
        if logic == "not":
            return f"非({subs[0]})"
        joiner = " 且 " if logic == "and" else " 或 "
        text = joiner.join(subs)
        need_paren = (parent == "and" and logic == "or") or \
                     (parent == "or" and logic == "and")
        return f"({text})" if need_paren else text
    op = str(node.get("operator", ""))
    op_txt = _OP_CN.get(op, op)
    left = _ind_display(str(node.get("indicator", "")), node.get("params"))
    return f"{left} {op_txt} {_value_display(node.get('value'))}"


def legacy_condition_text(row: dict) -> str:
    """旧三列策略的条件文本（表格"条件"列兼容显示）。"""
    key = str(row.get("indicator") or "").strip()
    if key in LEGACY_MAP:
        label = _ind_display(*LEGACY_MAP[key])
    else:
        label = key
    return f"{label} {row.get('operator', '')} {row.get('threshold', '')}"
