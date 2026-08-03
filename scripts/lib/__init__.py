"""三桶策略系统 · 通用库

约定：
- lib.paths 定义所有目录常量，其它模块只 import 常量不重算路径。
- lib.config 是唯一的 yaml 入口，全局单例。
- lib.data_fetch 是唯一的数据源入口，脚本禁止直接 import akshare/tushare。
- lib.signal_log / trade_log 是 07/04 号 CSV 的唯一读写口。
- lib.notifier 是唯一的对外推送入口。
- lib.report 只负责把结构化字典渲染成 Markdown。
"""
from lib import paths  # noqa: F401
