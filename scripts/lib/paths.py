"""路径常量。所有模块共用，避免各处重算相对路径。"""
from __future__ import annotations

from pathlib import Path

# scripts/lib/paths.py → 项目根 = 上上级
LIB_DIR = Path(__file__).resolve().parent
SCRIPTS_DIR = LIB_DIR.parent
ROOT_DIR = SCRIPTS_DIR.parent

TRADING_SYSTEM_DIR = ROOT_DIR / "trading-system"
SKILLS_DIR = ROOT_DIR / "skills"
DATA_DIR = ROOT_DIR / "data"
CACHE_DIR = DATA_DIR / "cache"

CONFIG_YAML = TRADING_SYSTEM_DIR / "02_strategy_config.yaml"
SIGNAL_LOG_TEMPLATE = TRADING_SYSTEM_DIR / "07_信号台账模板.csv"
TRADE_LOG_TEMPLATE = TRADING_SYSTEM_DIR / "04_交易日志模板.csv"

LIVE_SIGNAL_LOG = DATA_DIR / "live_signal_log.csv"
LIVE_TRADE_LOG = DATA_DIR / "live_trade_log.csv"


def ensure_dirs() -> None:
    """确保运行时目录存在。脚本入口调用一次即可。"""
    for d in (DATA_DIR, CACHE_DIR):
        d.mkdir(parents=True, exist_ok=True)
