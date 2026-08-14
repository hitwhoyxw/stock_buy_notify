"""监控自选与策略定义的数据存储层。

三个文件（都在 data/ 目录下）：
  watchlist.csv       监控股票池（候选池右键添加 / 手动录入）
  strategies.csv      策略定义（指标 + 条件 + 阈值 + 建议动作）
  monitor_alerts.json 提醒去重 seen + 触发历史 history

编码统一 utf-8-sig，code 全程按 str 处理（保前导零）。
"""
from __future__ import annotations

import datetime as dt
import json
import os
from typing import List, Optional

import pandas as pd

WATCHLIST_COLUMNS = [
    "code", "name", "added_from", "added_at", "strategies", "note",
]

STRATEGY_COLUMNS = [
    "id", "name", "type", "indicator", "operator", "threshold",
    "action", "priority", "enabled",
]

STRATEGY_TYPES = {"buy": "买入建议", "hold": "持有观察", "sell": "卖出建议"}

# 首次初始化写入的默认策略（enabled 默认启用，但监控池为空不会误触发）
DEFAULT_STRATEGIES = [
    {"id": "S1", "name": "跌破MA60减仓", "type": "sell",
     "indicator": "price_vs_ma60", "operator": "<", "threshold": "0",
     "action": "现价跌破MA60均线，建议减仓1/3观察", "priority": "P0", "enabled": "1"},
    {"id": "S2", "name": "深回撤清仓线", "type": "sell",
     "indicator": "drawdown_from_high_180d", "operator": "<=", "threshold": "-20",
     "action": "距半年高点回撤超20%，建议清仓止损", "priority": "P0", "enabled": "1"},
    {"id": "S3", "name": "低点涨幅止盈", "type": "sell",
     "indicator": "gain_from_low_180d", "operator": ">=", "threshold": "50",
     "action": "距半年低点涨幅超50%，建议分批止盈", "priority": "P1", "enabled": "1"},
    {"id": "S4", "name": "持仓浮盈减半", "type": "sell",
     "indicator": "cost_basis_gain", "operator": ">=", "threshold": "40",
     "action": "持仓浮盈超40%，建议卖出半仓锁定利润", "priority": "P1", "enabled": "1"},
    {"id": "S5", "name": "放量异动关注", "type": "buy",
     "indicator": "volume_ratio_20d", "operator": ">=", "threshold": "2",
     "action": "量比超2倍出现异动，关注买入机会", "priority": "P2", "enabled": "1"},
]

_HISTORY_LIMIT = 200


class WatchlistStore:
    """watchlist + strategies + 提醒历史 的统一读写。"""

    def __init__(self, data_dir: str):
        self.data_dir = data_dir
        self._watchlist_path = os.path.join(data_dir, "watchlist.csv")
        self._strategy_path = os.path.join(data_dir, "strategies.csv")
        self._alerts_path = os.path.join(data_dir, "monitor_alerts.json")
        self._ensure_defaults()

    def set_data_dir(self, data_dir: str):
        """设置页切换数据目录后热更新路径（对象引用不变）。"""
        self.data_dir = data_dir
        self._watchlist_path = os.path.join(data_dir, "watchlist.csv")
        self._strategy_path = os.path.join(data_dir, "strategies.csv")
        self._alerts_path = os.path.join(data_dir, "monitor_alerts.json")
        self._ensure_defaults()

    # ── 内部：CSV 读写 ──

    def _read_csv(self, path: str, columns: List[str]) -> pd.DataFrame:
        if not os.path.exists(path):
            return pd.DataFrame(columns=columns)
        try:
            df = pd.read_csv(path, dtype=str, keep_default_na=False)
            for c in columns:
                if c not in df.columns:
                    df[c] = ""
            return df[columns]
        except Exception:
            return pd.DataFrame(columns=columns)

    def _write_csv(self, path: str, df: pd.DataFrame) -> bool:
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            df.to_csv(path, index=False, encoding="utf-8-sig")
            return True
        except Exception:
            return False

    def _ensure_defaults(self):
        """首次运行时写入默认策略表。"""
        if not os.path.exists(self._strategy_path):
            df = pd.DataFrame(DEFAULT_STRATEGIES, columns=STRATEGY_COLUMNS)
            self._write_csv(self._strategy_path, df)

    # ============================================================
    # 监控自选池
    # ============================================================

    def list_watchlist(self) -> pd.DataFrame:
        return self._read_csv(self._watchlist_path, WATCHLIST_COLUMNS)

    def in_watchlist(self, code: str) -> bool:
        code = code.split(".")[0].zfill(6)
        df = self.list_watchlist()
        return not df.empty and not df[df["code"].str.zfill(6) == code].empty

    def add(self, code: str, name: str = "", added_from: str = "manual",
            note: str = "") -> (bool, str):
        code = code.split(".")[0].strip()
        # 仅 5 位数字自动补前导零（如 00001 -> 000001）；其它长度必须是完整 6 位
        if len(code) == 5 and code.isdigit():
            code = code.zfill(6)
        if not (code.isdigit() and len(code) == 6):
            return False, "代码必须是 6 位数字（5 位数字会自动补零）"
        if self.in_watchlist(code):
            return False, f"{code} 已在监控池中"
        df = self.list_watchlist()
        row = {
            "code": code, "name": name, "added_from": added_from,
            "added_at": dt.datetime.now().strftime("%Y-%m-%d %H:%M"),
            "strategies": "", "note": note,
        }
        df = pd.concat([df, pd.DataFrame([row])], ignore_index=True)
        if self._write_csv(self._watchlist_path, df):
            return True, f"已添加 {code} {name}".strip()
        return False, "写入 watchlist.csv 失败"

    def remove(self, code: str) -> bool:
        code = code.split(".")[0].zfill(6)
        df = self.list_watchlist()
        if df.empty:
            return False
        mask = df["code"].str.zfill(6) != code
        if mask.all():
            return False
        return self._write_csv(self._watchlist_path, df[mask].reset_index(drop=True))

    def set_strategies(self, code: str, strategy_ids: List[str]):
        code = code.split(".")[0].zfill(6)
        df = self.list_watchlist()
        if df.empty:
            return False
        idx = df.index[df["code"].str.zfill(6) == code]
        if len(idx) == 0:
            return False
        df.loc[idx[0], "strategies"] = ";".join(strategy_ids)
        return self._write_csv(self._watchlist_path, df)

    def set_note(self, code: str, note: str) -> bool:
        code = code.split(".")[0].zfill(6)
        df = self.list_watchlist()
        if df.empty:
            return False
        idx = df.index[df["code"].str.zfill(6) == code]
        if len(idx) == 0:
            return False
        df.loc[idx[0], "note"] = note
        return self._write_csv(self._watchlist_path, df)

    # ============================================================
    # 策略定义
    # ============================================================

    def list_strategies(self) -> pd.DataFrame:
        return self._read_csv(self._strategy_path, STRATEGY_COLUMNS)

    def next_strategy_id(self) -> str:
        df = self.list_strategies()
        if df.empty:
            return "S1"
        nums = []
        for v in df["id"]:
            try:
                nums.append(int(str(v).lstrip("Ss")))
            except ValueError:
                pass
        return f"S{max(nums, default=0) + 1}"

    def add_strategy(self, record: dict) -> Optional[str]:
        df = self.list_strategies()
        sid = str(record.get("id") or self.next_strategy_id())
        row = {c: str(record.get(c, "")) for c in STRATEGY_COLUMNS}
        row["id"] = sid
        if not row["enabled"]:
            row["enabled"] = "1"  # 新策略默认启用
        df = pd.concat([df, pd.DataFrame([row])], ignore_index=True)
        return sid if self._write_csv(self._strategy_path, df) else None

    def update_strategy(self, sid: str, record: dict) -> bool:
        df = self.list_strategies()
        if df.empty:
            return False
        idx = df.index[df["id"].astype(str) == str(sid)]
        if len(idx) == 0:
            return False
        for c in STRATEGY_COLUMNS:
            if c in record and c != "id":
                df.loc[idx[0], c] = str(record.get(c, ""))
        return self._write_csv(self._strategy_path, df)

    def delete_strategy(self, sid: str) -> bool:
        df = self.list_strategies()
        if df.empty:
            return False
        mask = df["id"].astype(str) != str(sid)
        if mask.all():
            return False
        ok = self._write_csv(self._strategy_path, df[mask].reset_index(drop=True))
        if ok:
            # 同步清理 watchlist 中对该策略的引用
            wl = self.list_watchlist()
            if not wl.empty:
                def _strip(s: str) -> str:
                    ids = [x for x in str(s).split(";") if x and x != str(sid)]
                    return ";".join(ids)
                wl["strategies"] = wl["strategies"].map(_strip)
                self._write_csv(self._watchlist_path, wl)
        return ok

    def toggle_strategy(self, sid: str) -> Optional[bool]:
        """切换启用状态，返回切换后的状态。"""
        df = self.list_strategies()
        if df.empty:
            return None
        idx = df.index[df["id"].astype(str) == str(sid)]
        if len(idx) == 0:
            return None
        new_val = "0" if str(df.loc[idx[0], "enabled"]) == "1" else "1"
        df.loc[idx[0], "enabled"] = new_val
        return new_val == "1" if self._write_csv(self._strategy_path, df) else None

    # ============================================================
    # 提醒去重与历史
    # ============================================================

    def _load_alerts(self) -> dict:
        if not os.path.exists(self._alerts_path):
            return {"seen": {}, "history": []}
        try:
            with open(self._alerts_path, encoding="utf-8") as f:
                data = json.load(f)
            if not isinstance(data, dict):
                data = {}
            data.setdefault("seen", {})
            data.setdefault("history", [])
            return data
        except Exception:
            return {"seen": {}, "history": []}

    def _save_alerts(self, data: dict):
        try:
            os.makedirs(os.path.dirname(self._alerts_path), exist_ok=True)
            with open(self._alerts_path, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=1)
        except Exception:
            pass

    def seen_keys(self) -> set:
        """当前已提醒过的 dedup key 集合（tick 内批量查询用）。"""
        return set(self._load_alerts()["seen"].keys())

    def is_alert_seen(self, key: str) -> bool:
        return key in self._load_alerts()["seen"]

    def record_alerts(self, entries: List[dict]):
        """写入触发历史并标记 seen（已 seen 的自动跳过，幂等）。"""
        if not entries:
            return
        data = self._load_alerts()
        today = dt.date.today().isoformat()
        fresh = []
        # seen 按 "日期|策略ID|代码"，每天最多提醒一次
        for e in entries:
            key = e.get("dedup_key") or f"{today}|{e.get('strategy_id')}|{e.get('code')}"
            if key in data["seen"]:
                continue
            data["seen"][key] = dt.datetime.now().isoformat(timespec="seconds")
            e.pop("dedup_key", None)
            fresh.append(e)
        if not fresh:
            return
        # history 增量 + 截断
        data["history"] = (fresh + data["history"])[:_HISTORY_LIMIT]
        self._save_alerts(data)

    def load_history(self) -> List[dict]:
        return self._load_alerts()["history"]

    def clear_history(self):
        self._save_alerts({"seen": {}, "history": []})
