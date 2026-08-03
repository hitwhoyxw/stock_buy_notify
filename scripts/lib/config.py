"""策略配置加载。02_strategy_config.yaml 是全系统唯一的阈值来源。

用法：
    from lib.config import get_config
    cfg = get_config()
    a_min = cfg["bucket_A"]["stock_filters"]["dividend_yield_ttm_min_pct"]
"""
from __future__ import annotations

import functools
import hashlib
from typing import Any, Dict

import yaml

from lib.paths import CONFIG_YAML


@functools.lru_cache(maxsize=1)
def get_config() -> Dict[str, Any]:
    """加载策略 yaml。全进程缓存一次，脚本运行期不刷新。"""
    if not CONFIG_YAML.exists():
        raise FileNotFoundError(
            f"策略配置文件缺失：{CONFIG_YAML}。请检查 trading-system 目录是否完整。"
        )
    with CONFIG_YAML.open("r", encoding="utf-8") as f:
        cfg = yaml.safe_load(f)
    if not isinstance(cfg, dict):
        raise ValueError(f"{CONFIG_YAML} 顶层必须是 mapping，实际为 {type(cfg).__name__}")
    return cfg


def get_version() -> str:
    """yaml 内部声明的 meta.version，用于台账 yaml_version_at_trigger 字段。"""
    return str(get_config().get("meta", {}).get("version", "unknown"))


def get_config_hash() -> str:
    """yaml 文件内容 SHA-256 前 8 位。用作参数变更的追溯锚点。"""
    return hashlib.sha256(CONFIG_YAML.read_bytes()).hexdigest()[:8]


def get_yaml_tag() -> str:
    """写入台账的 yaml_version_at_trigger 组合值：v{version}-{hash}。"""
    return f"v{get_version()}-{get_config_hash()}"
