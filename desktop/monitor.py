"""监控引擎：自选池 × 策略 → 周期检查 → 提醒（弹窗信号 + 邮件）。

行情全部基于腾讯免费行情（实时价 + 日K），不依赖 akshare。
指标计算与策略求值由 strategy_engine 提供：
  · 简单策略（strategies.csv 的 indicator/operator/threshold 三列）
  · 复合策略（condition 列的条件树 JSON，支持 AND/OR/NOT 嵌套、
    形态类（如均线多头排列）、交叉类（MACD/均线金叉死叉））
  两类统一走 evaluate_strategy()，旧策略零迁移自动兼容。

提醒去重：同一 (日期, 策略ID, 代码) 当天只提醒一次。
监控节奏：盘中按用户设定间隔；盘外(工作日)至少5分钟；周末跳过。
策略与自选池每个 tick 重新读取 CSV —— 改动即时生效（热更新）。
"""
from __future__ import annotations

import datetime as dt
import json
import time
import traceback
from typing import Dict, List, Optional

import pandas as pd
from PyQt5.QtCore import QThread, pyqtSignal

from strategy_engine import (
    IndicatorContext, StrategyConfigError, describe_condition,
    evaluate_strategy, legacy_condition_text, primary_value,
)
from watchlist_store import WatchlistStore, STRATEGY_TYPES

# 旧版简单策略的指标标签（UI 展示用；条件树指标见 strategy_engine.INDICATOR_DEFS）
INDICATORS = {
    "price": "现价(元)",
    "day_change_pct": "当日涨跌幅(%)",
    "price_vs_ma20": "现价vs MA20(%)",
    "price_vs_ma60": "现价vs MA60(%)",
    "drawdown_from_high_180d": "距180日高点回撤(%)",
    "gain_from_low_180d": "距180日低点涨幅(%)",
    "volume_ratio_20d": "量比(vs 20日均量)",
    "pe_ttm": "市盈率TTM",
    "cost_basis_gain": "持仓浮盈(%)",
}

TYPE_EMOJI = {"buy": "🟢买入", "hold": "🔵持有", "sell": "🔴卖出"}

_UA = {"User-Agent": "Mozilla/5.0"}


# ============================================================
# 行情/K线抓取（同步版，供后台线程使用）
# ============================================================

def _to_tencent_symbol(code: str) -> str:
    pure = code.split(".")[0].zfill(6)
    if pure.startswith(("6", "5", "9")):
        return f"sh{pure}"
    if pure.startswith(("4", "8")):
        return f"bj{pure}"
    return f"sz{pure}"


def fetch_quotes(codes: List[str]) -> Dict[str, dict]:
    """批量拉实时行情，返回 {code: {price, change_pct, pe_ttm}}。"""
    import requests
    result: Dict[str, dict] = {}
    pure_codes = [c.split(".")[0].zfill(6) for c in codes]
    for i in range(0, len(pure_codes), 60):
        chunk = pure_codes[i:i + 60]
        q = ",".join(_to_tencent_symbol(c) for c in chunk)
        try:
            resp = requests.get("http://qt.gtimg.cn/q=" + q,
                                headers=_UA, timeout=10)
            resp.encoding = "gbk"
        except Exception:
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
            info = {"name": f[1] if len(f) > 1 else ""}
            try:
                p = float(f[3])
                if p > 0:
                    info["price"] = p
            except (ValueError, TypeError):
                continue
            try:
                info["change_pct"] = float(f[32])
            except (ValueError, TypeError):
                info["change_pct"] = None
            try:
                info["pe_ttm"] = float(f[39]) if f[39].strip() else None
            except (ValueError, TypeError):
                info["pe_ttm"] = None
            result[code] = info
    return result


def fetch_kline(code: str, days: int = 200) -> Optional[pd.DataFrame]:
    """拉日K（前复权），返回 date/open/close/high/low/volume 或 None。"""
    import requests
    end = dt.date.today().strftime("%Y-%m-%d")
    start = (dt.date.today() - dt.timedelta(days=days * 2)).strftime("%Y-%m-%d")
    sym = _to_tencent_symbol(code)
    url = (f"http://web.ifzq.gtimg.cn/appstock/app/fqkline/get?"
           f"param={sym},day,{start},{end},640,qfq")
    try:
        resp = requests.get(url, timeout=15)
        data = resp.json()
        kdata = data.get("data", {}).get(sym, {})
        day_list = kdata.get("day", kdata.get("qfqday", []))
        rows = []
        for d in day_list:
            if len(d) >= 6:
                rows.append({
                    "date": d[0], "open": float(d[1]), "close": float(d[2]),
                    "high": float(d[3]), "low": float(d[4]), "volume": float(d[5]),
                })
        return pd.DataFrame(rows) if rows else None
    except Exception:
        return None


# ============================================================
# 策略判断（简单三列 + 条件树 统一入口见 strategy_engine.evaluate_strategy）
# ============================================================

def strategy_condition_text(s: dict) -> str:
    """策略条件的中文可读文本（复合条件树降级展示，简单策略走旧样式）。"""
    raw = str(s.get("condition") or "").strip()
    if raw:
        try:
            return describe_condition(json.loads(raw))
        except json.JSONDecodeError:
            return raw[:80]
    return legacy_condition_text(s)


def fmt_value(v: Optional[float]) -> str:
    return "--" if v is None else f"{v:.2f}"


# ============================================================
# 邮件（独立实现，参考 scripts/lib/notifier.py）
# ============================================================

def send_alert_email(cfg: dict, subject: str, body: str) -> bool:
    if not cfg.get("monitor_email_enabled"):
        return False
    host = (cfg.get("smtp_host") or "").strip()
    user = (cfg.get("smtp_user") or "").strip()
    pwd = (cfg.get("smtp_pass") or "").strip()
    to = (cfg.get("smtp_to") or "").strip()
    if not (host and user and pwd and to):
        return False
    try:
        port = int(cfg.get("smtp_port", 465))
    except (ValueError, TypeError):
        port = 465

    from email.mime.text import MIMEText
    from email.utils import formataddr
    import smtplib
    msg = MIMEText(body, "plain", "utf-8")
    msg["From"] = formataddr(("三桶策略监控", user))
    msg["To"] = to
    msg["Subject"] = subject
    try:
        if port == 465:
            server = smtplib.SMTP_SSL(host, port, timeout=15)
        else:
            server = smtplib.SMTP(host, port, timeout=15)
            server.starttls()
        server.login(user, pwd)
        server.sendmail(user, [t.strip() for t in to.split(",") if t.strip()],
                        msg.as_string())
        server.quit()
        return True
    except Exception as e:
        print(f"[monitor] 邮件发送失败: {e}", flush=True)
        return False


# ============================================================
# 监控引擎（后台线程）
# ============================================================

class MonitorEngine(QThread):
    """周期检查自选池策略，触发时发信号（UI 弹窗）+ 邮件。"""

    alert_triggered = pyqtSignal(list)    # List[dict] 触发的提醒
    quotes_updated = pyqtSignal(dict)     # {code: {price, change_pct}}
    status_message = pyqtSignal(str)

    def __init__(self, dm, store: WatchlistStore, config: dict):
        super().__init__()
        self.dm = dm
        self.store = store
        self.config = config
        self._stop_flag = False
        self._active = False
        self._kline_cache: Dict[str, tuple] = {}  # code -> (date, df|None)
        self._last_check = ""
        self._cfg_errors: set = set()  # 已上报过的策略配置错误（去重）

    # ── 对外控制 ──

    def is_active(self) -> bool:
        return self._active

    def start_monitoring(self):
        self._active = True
        if not self.isRunning():
            self.start(QThread.LowPriority)

    def stop_monitoring(self):
        self._active = False
        self.status_message.emit("[监控] 已停止")

    def shutdown(self):
        self._active = False
        self._stop_flag = True
        if self.isRunning():
            self.wait(3000)

    def reload_config(self, cfg: dict):
        self.config = cfg

    # ── 线程主循环 ──

    def run(self):
        while not self._stop_flag:
            try:
                if self._active and dt.datetime.now().weekday() < 5:
                    self._tick()
            except Exception:
                self.status_message.emit(
                    f"[监控] 异常: {traceback.format_exc(limit=1)}")
            self._sleep(self._effective_wait())

    def _sleep(self, seconds: int):
        """可中断 sleep：停止/启停切换 1 秒内响应。"""
        state = self._active
        for _ in range(max(1, seconds)):
            if self._stop_flag or self._active != state:
                return
            time.sleep(1)

    def _effective_wait(self) -> int:
        """盘中按设定间隔；盘外(工作日)至少 5 分钟；周末 30 秒空转。"""
        now = dt.datetime.now()
        if now.weekday() >= 5:
            return 30
        try:
            base = int(self.config.get("monitor_interval", 60))
        except (ValueError, TypeError):
            base = 60
        base = max(10, base)
        t = now.hour * 60 + now.minute
        if 9 * 60 + 5 <= t <= 15 * 60 + 5:
            return base
        return max(base, 300)

    # ── 单次检查 ──

    def _get_kline(self, code: str) -> Optional[pd.DataFrame]:
        cached = self._kline_cache.get(code)
        today = dt.date.today().isoformat()
        if cached and cached[0] == today:
            return cached[1]
        df = fetch_kline(code)
        self._kline_cache[code] = (today, df)
        return df

    def _load_costs(self) -> Dict[str, float]:
        """从持仓聚合表取平均成本 {code: cost}。"""
        costs: Dict[str, float] = {}
        try:
            pos = self.dm.load_positions()
            for _, r in pos.iterrows():
                try:
                    c = float(r.get("平均成本", 0) or 0)
                    if c > 0:
                        costs[str(r["代码"]).zfill(6)] = c
                except (ValueError, TypeError, KeyError):
                    continue
        except Exception:
            pass
        return costs

    def _tick(self):
        wl = self.store.list_watchlist()
        if wl.empty:
            self.status_message.emit("[监控] 自选池为空，等待添加股票…")
            return

        srows = self.store.list_strategies()
        smap = {}
        if not srows.empty:
            for _, s in srows.iterrows():
                if str(s["enabled"]) == "1":
                    smap[str(s["id"])] = s.to_dict()

        codes = [str(c).zfill(6) for c in wl["code"]]
        quotes = fetch_quotes(codes)
        if quotes:
            self.quotes_updated.emit({
                c: q for c, q in quotes.items() if q.get("price")})
        costs = self._load_costs()
        seen = self.store.seen_keys()
        today = dt.date.today().isoformat()
        now_str = dt.datetime.now().strftime("%H:%M:%S")

        alerts: List[dict] = []
        hit_count = 0
        for _, row in wl.iterrows():
            code = str(row["code"]).zfill(6)
            sids = [s for s in str(row.get("strategies", "")).split(";") if s]
            quote = quotes.get(code)
            if not sids or not quote:
                continue
            kline = self._get_kline(code)
            ctx = IndicatorContext(quote, kline, costs.get(code))
            for sid in sids:
                s = smap.get(sid)
                if s is None:
                    continue
                try:
                    if evaluate_strategy(s, ctx) is not True:
                        continue
                except StrategyConfigError as e:
                    # 配置错误：状态栏上报一次（去重）并跳过，绝不静默当 false
                    msg = f"策略 {sid} 配置错误: {e}"
                    if msg not in self._cfg_errors:
                        self._cfg_errors.add(msg)
                        self.status_message.emit(f"[监控] {msg}")
                    continue
                hit_count += 1
                key = f"{today}|{sid}|{code}"
                if key in seen:
                    continue
                alerts.append({
                    "dedup_key": key,
                    "time": f"{today} {now_str}",
                    "code": code,
                    "name": str(row.get("name", "")) or quote.get("name", ""),
                    "strategy_id": sid,
                    "strategy_name": str(s.get("name", "")),
                    "type": str(s.get("type", "")),
                    "indicator": str(s.get("indicator", "")),
                    "indicator_label": strategy_condition_text(s),
                    "value": fmt_value(primary_value(s, ctx)),
                    "op": "",
                    "threshold": "",
                    "action": str(s.get("action", "")),
                    "priority": str(s.get("priority", "")),
                })

        if alerts:
            self.store.record_alerts(alerts)
            self.alert_triggered.emit(alerts)
            self._send_mail(alerts)

        self._last_check = now_str
        mail_tag = "📧" if self.config.get("monitor_email_enabled") else ""
        self.status_message.emit(
            f"[监控] {len(wl)} 只 | {now_str} 检查完毕 | "
            f"命中 {hit_count} 条 / 新提醒 {len(alerts)} 条 {mail_tag}")

    def _send_mail(self, alerts: List[dict]):
        lines = []
        for e in alerts:
            # 简单策略历史数据可能仍带 op/threshold，新提醒统一为条件描述 + 当前值
            detail = f"{e['indicator_label']}"
            if e.get("op"):
                detail += f" {e['op']} {e.get('threshold', '')}"
            if e.get("value") and e["value"] != "--":
                detail += f" = {e['value']}"
            lines.append(
                f"■ {e['name']}({e['code']})  [{e['priority']}] "
                f"{TYPE_EMOJI.get(e['type'], '')} {e['strategy_name']}\n"
                f"  触发条件: {detail}\n"
                f"  建议: {e['action']}\n"
                f"  时间: {e['time']}")
        subject = f"[三桶监控] {len(alerts)} 条策略提醒 {alerts[0]['time']}"
        body = "\n".join(lines)
        ok = send_alert_email(self.config, subject, body)
        if ok:
            self.status_message.emit(f"[监控] 提醒邮件已发送给 "
                                     f"{self.config.get('smtp_to', '')}")
