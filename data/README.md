# 数据目录

本目录存放运行时产物，不由 IDE 或人手编辑。

## 文件清单

| 文件 | 中文含义 | 生产者 | 消费者 | 说明 |
|---|---|---|---|---|
| `live_signal_log.csv` | 实盘信号台账 | T1/T2/T3/T4 | T5归因 / T8维护 / 客户端 | Agent自动写入的买卖信号记录（07号模板的运行副本） |
| `live_trade_log.csv` | 实盘交易日志 | 手动/客户端 | T1风控 / T3再平衡 / T8台账 | 手动记录的实际买卖记录（04号模板的运行副本） |
| `candidates_A.csv` | A桶候选池（红利逆向） | T6 | LLM排序 → `skill_output_T6.md` | 硬门槛过滤后的高分红股列表 |
| `candidates_B.csv` | B桶候选池（成长） | T6 | LLM排序 → `skill_output_T6.md` | 硬门槛过滤后的高成长股列表 |
| `candidates_C.csv` | C桶候选池（热点周期） | T6 | LLM排序 → `skill_output_T6.md` | T4判定PASS的景气股列表 |
| `skill_input_T4C.md` | T4输入：财报+互动易文本 | T4 --prepare | 喂给LLM做景气判定 | 每只票的业绩预告+互动易问答文本 |
| `skill_output_T4C.md` | T4输出：LLM判定结果 | 人工/CI喂LLM | T4 ingest → 写入台账 | JSON数组，每只票PASS/REJECT+理由 |
| `skill_input_T5.md` | T5输入：季度归因数据 | T5 prepare | 喂给LLM做归因 | 季度交易+信号+基准走势 |
| `skill_output_T5.md` | T5输出：LLM归因报告 | 人工/CI喂LLM | 季度复盘材料 | LLM写的季度归因分析 |
| `skill_input_T6.md` | T6输入：三桶候选池CSV | T6 | 喂给LLM做语义排序 | 三桶候选池的汇总CSV |
| `skill_output_T6.md` | T6输出：LLM排序+景气分析 | 人工/CI喂LLM | 研究材料 | Top10入选+REJECT名单+景气行业确认 |
| `report_日期_T1.md` | T1报告：每日风控 | T1脚本 | 推送通知/归档 | 持仓回撤/止损/集中度检查结果 |
| `report_日期_T2.md` | T2报告：周度红利评级 | T2脚本 | 推送通知/归档 | 红利桶档位S0~S3判定+调仓建议 |
| `report_日期_T3.md` | T3报告：月度再平衡 | T3脚本 | 推送通知/归档 | 四桶权重偏离+弪药桶检查 |
| `report_日期_T7.md` | T7报告：回测摘要 | T7脚本 | 归档 | 策略参数历史胜率验证 |
| `report_日期_T8.md` | T8报告：台账维护 | T8脚本 | 推送通知/归档 | 历史信号收益回补+失效预警 |
| `cache/` | 数据缓存 | data_fetch | 内部 | 交易日历等缓存，避免重复请求akshare |

## 初始化

首次运行前需要把 `../trading-system/07_信号台账模板.csv` 复制成 `live_signal_log.csv`，把 `../trading-system/04_交易日志模板.csv` 复制成 `live_trade_log.csv`。
脚本已在缺失时自动完成初始化。
