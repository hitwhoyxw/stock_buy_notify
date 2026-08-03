# 数据目录

本目录存放运行时产物，不由 IDE 或人手编辑。

| 文件 | 生产者 | 消费者 | 说明 |
|---|---|---|---|
| `live_signal_log.csv` | T1/T2/T3/T6/T8 | 客户端 / 归因 T5 | 07 号台账实盘副本 |
| `live_trade_log.csv` | 手动/客户端 | T1/T3/T8 | 04 号交易日志实盘副本 |
| `report_YYYY-MM-DD_TX.md` | TX 脚本 | Notifier / 客户端 | 单次运行报告 |
| `skill_input_TX.md` | TX 脚本 | 手动 LLM 或客户端 | 语义步骤输入 |
| `skill_output_TX.md` | 手动 LLM | TX 脚本回读 | 语义步骤输出 |
| `cache/` | data_fetch | 内部 | 行情/财报缓存 |

首次运行前需要把 `../trading-system/07_信号台账模板.csv` 复制成 `live_signal_log.csv`，把 `../trading-system/04_交易日志模板.csv` 复制成 `live_trade_log.csv`。
脚本已在缺失时自动完成初始化。
