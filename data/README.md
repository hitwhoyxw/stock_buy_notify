# 数据目录

本目录存放运行时产物，不由 IDE 或人手编辑。

## 文件清单

| 文件 | 中文含义 | 生产者 | 消费者 | 说明 |
|---|---|---|---|---|
| `live_signal_log.csv` | 实盘信号台账 | T1/T2/T3/T4 任务 | T5归因 / T8维护 / 客户端 | Agent自动写入的买卖信号记录（07号模板的运行副本） |
| `live_trade_log.csv` | 实盘交易日志 | 手动/客户端 | T1风控 / T3再平衡 / T8台账 | 手动记录的实际买卖记录（04号模板的运行副本） |
| `strategies.csv` | 策略配置运行副本 | 客户端策略页 | T1风控 / T8台账 / 云同步 | 02号 yaml 的结构化运行副本（首次运行自动种入默认策略） |
| `watchlist.csv` | 自选/监控池 | 客户端自选页 | 盘中监控 / 云同步 | 自选股及其关联策略、备注 |
| `monitor_alerts.json` | 监控提醒去重与历史 | 盘中监控 | 客户端 / 云同步 | 提醒去重表 + 提醒历史 |
| `portfolio_nav.csv` | 组合净值记录 | T1 等任务 | 客户端图表 | 组合净值曲线数据点 |
| `candidates_A.csv` | A桶候选池（红利逆向） | T6 | LLM排序 → `skill_output_T6_A.md` | 硬门槛过滤后的高分红股列表 |
| `candidates_B.csv` | B桶候选池（成长） | T6 | LLM排序 → `skill_output_T6_B.md` | 硬门槛过滤后的高成长股列表 |
| `candidates_C.csv` | C桶候选池（热点周期） | T6 | LLM排序 → `skill_output_T6_C.md` | T4判定PASS的景气股列表 |
| `skill_input_T4C.md` | T4输入：财报+互动易文本 | T4 prepare 阶段 | 喂给LLM做景气判定 | 每只票的业绩预告+互动易问答文本 |
| `skill_output_T4C.md` | T4输出：LLM判定结果 | UI LLM桥 / 外部Agent | T4 ingest 阶段 → 写入台账 | JSON数组，每只票PASS/REJECT+理由 |
| `skill_input_T5.md` | T5输入：季度归因数据 | T5 任务 | 喂给LLM做归因 | 季度交易+信号+基准走势 |
| `skill_output_T5.md` | T5输出：LLM归因报告 | UI LLM桥 / 外部Agent | 季度复盘材料 | LLM写的季度归因分析 |
| `skill_input_T6_A/_B/_C.md` | T6输入：按桶分文件的候选池CSV | T6 任务 | 喂给LLM做语义排序 | 单桶重跑只覆盖对应桶文件，互不污染 |
| `skill_output_T6_A/_B/_C.md` | T6输出：LLM三档全量分析 | UI LLM桥 / 外部Agent | 研究材料 | 推荐/中立/不推荐三档全量+行业分组分析 |
| `report_日期_T1.md` | T1报告：每日风控 | T1任务 | 推送通知/归档 | 持仓回撤/止损/集中度检查结果 |
| `report_日期_T2.md` | T2报告：周度红利评级 | T2任务 | 推送通知/归档 | 红利桶档位S0~S3判定+调仓建议 |
| `report_日期_T3.md` | T3报告：月度再平衡 | T3任务 | 推送通知/归档 | 四桶权重偏离+弹药桶检查 |
| `report_日期_T7.md` | T7报告：回测摘要 | T7任务 | 归档 | 策略参数历史胜率验证 |
| `report_日期_T8.md` | T8报告：台账维护 | T8任务 | 推送通知/归档 | 历史信号收益回补+失效预警 |
| `backtest_*.csv` | T7回测结果明细 | T7任务 | 归档/研究 | 各桶策略参数历史回测数据 |
| `cache/` | 数据缓存 | C# 数据源客户端 | 内部 | 行情/日K/财报/成分股缓存，跨运行复用，减少数据源限流 |
| `sync_backup/` | 云同步覆盖前备份 | 云同步 | 手动恢复 | 从云端拉数据覆盖本地前，原文件按时间戳备份于此 |
| `logs/` | 运行日志 | 各端程序 | 排查问题 | 客户端/任务运行日志 |

## 初始化

首次运行前需要把 `../trading-system/07_信号台账模板.csv` 复制成 `live_signal_log.csv`，把 `../trading-system/04_交易日志模板.csv` 复制成 `live_trade_log.csv`。
C# 任务与客户端在文件缺失时会自动完成初始化（策略配置首次运行自动种入默认值）。

注：客户端配置 `app_config.json` 优先读取 data 同级目录（即项目根），移动端沙盒场景兼容放本目录内。
