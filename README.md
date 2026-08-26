# 三桶资产交易策略系统

Agent 只出提醒、买卖仍由人决策的 A 股三桶（红利逆向 / 成长 / 热点周期）策略系统。

> **实现已全面迁移至 C#（.NET 10）**：T1~T8 任务、策略引擎、数据源、云同步均为 C# 原生实现，
> 桌面端（Avalonia）/ 移动端（MAUI）/ CLI（CI·服务器）共用同一核心库 `ThreeBucket.Core`。
> 原 Python 脚本（`scripts/`）与 PyQt 桌面端（`desktop/legacy-pyqt/`）保留为应急回退，详见文末。

## 架构总览

```
┌──────────────────────────────────────────────────────────────┐
│                     src/ThreeBucket.Core                     │
│  T1~T8 内置任务 · 策略引擎 · 数据源客户端 · 云同步 · 推送通知  │
└──────────┬───────────────┬───────────────┬──────────────────┘
           │               │               │
   ThreeBucket.Cli    ThreeBucket.UI   ThreeBucket.Mobile
   （CI/服务器/批处理）  （Avalonia 桌面端）（MAUI Android/iOS）
           │               │               │
           └───────────────┴───────────────┘
                           │
            data/ 台账·报告·缓存  ⇄  Supabase 云同步
```

| 项目 | 说明 |
|------|------|
| **ThreeBucket.Core** | 核心库（无 UI 依赖）：`Services/` T1~T8 任务 + 策略引擎 + 交易日历 + 调度引擎 + 云同步；`DataSources/` 多数据源抽象（主源故障自动降级）；`Data/` 台账存储 |
| **ThreeBucket.Cli** | 无 UI 命令行：运行内置任务 + Supabase 拉取/推送（GitHub Actions 调度入口） |
| **ThreeBucket.UI** | Avalonia 桌面端（Windows / macOS / Linux）：仪表盘、持仓、候选池、分析、报告、策略、自选、LLM 桥接、设置 |
| **ThreeBucket.Mobile** | .NET MAUI 移动端（Android / iOS），与桌面端共用 Core（不入 slnx，需单独 workload 构建） |
| **ThreeBucket.Demo** | 回归与验证：策略引擎离线回归、数据源联网验证、T1~T8 实例化验证、T3 端到端实跑 |

**数据源**：同花顺（扶摇，`THS_API_KEY`）为行情快照/日K/成分股/分红主源，失败自动降级免费源（腾讯 / 新浪 / 东财 / 中证指数官网）；未配置时行为与免费源版一致。

## T1~T8 任务总览

| 任务 | C# 实现（`Core/Services/`） | 频率 | 做什么 | 产出文件 |
|------|------|------|--------|----------|
| **T1 每日风控** | `DailyRiskTask` | 工作日盘后 | 检查持仓回撤/止损/集中度/MA60破位，触发告警写信号台账 | `data/report_日期_T1.md` |
| **T2 周度红利择时** | `WeeklyDividendTask` | 每周一 | 拉估值/宏观/情绪指标，判定红利桶档位 S0~S3，输出调仓建议 | `data/report_日期_T2.md` |
| **T3 月度再平衡** | `MonthlyRebalanceTask` | 每月首日 | 检查四桶实际权重 vs 目标偏离，校验弹药桶≥15%，统计分红入D | `data/report_日期_T3.md` |
| **T4 财报季扫描** | `EarningsScanTask` | 4/8/10月每周一 | 先 ingest 上轮 LLM 判定入账，再从业绩预告+互动易组装下轮 LLM 输入 | `skill_input_T4C.md` → `skill_output_T4C.md` → `live_signal_log.csv` |
| **T5 季度归因** | `AttributionPrepTask` | 季末 | 组装季度交易日志+信号台账+基准走势→LLM做归因复盘 | `skill_input_T5.md` → `skill_output_T5.md` |
| **T6 候选池筛选** | `CandidatePoolTask` | 每周五/周一 | 三桶各自硬门槛过滤→排序→输出候选池CSV→LLM三档全量分析 | `candidates_A/B/C.csv` + `skill_input_T6_A/B/C.md` → `skill_output_T6_A/B/C.md` |
| **T7 参数回测** | `BacktestTask` | 每月28日 | 全桶（A/B/C）策略参数历史胜率验证，无需再按桶拆分 | `data/report_日期_T7.md` + `data/backtest_*.csv` |
| **T8 信号台账维护** | `SignalLogTask` | 工作日17:00 | 回补历史信号60/120/250日收益，对比实盘vs回测胜率，触发失效预警 | `data/report_日期_T8.md` |

所有任务实现统一接口 `IBuiltinTask`（`Key / Name / RunAsync`），交易日判断内建在任务里（`TradingCalendar`，日历不可用按周末兜底），非交易日自动跳过并记成功。

## 目录结构

```
.
├── src/                       # C# 解决方案（ThreeBucket.slnx，Mobile 除外）
│   ├── ThreeBucket.Core/      # 核心库
│   │   ├── Data/              # DataStore（台账/配置/云同步快照）、SignalLogStore（信号台账）
│   │   ├── DataSources/       # IMarketDataSource 抽象 + 腾讯/新浪/同花顺源 + 聚合降级
│   │   ├── Models/            # DailyBar / RealTimeQuote / StockCode 等领域模型
│   │   ├── Services/          # T1~T8 任务、ThsClient/EastMoneyClient/CsIndexClient、
│   │   │                      # StrategyEngine、TradingCalendar、TaskSchedulerEngine、
│   │   │                      # CloudSyncService/AutoSyncService、LlmClient、推送
│   │   └── build.ps1/sh       # 跨平台发布脚本（win/linux/osx，-SelfContained 内嵌运行时）
│   ├── ThreeBucket.Cli/       # CLI：--task / --sync pull|push / --list / --data
│   ├── ThreeBucket.UI/        # Avalonia 桌面端（Views: Dashboard/Portfolio/Candidates/
│   │                          #   Analysis/Reports/Strategy/Watchlist/LlmBridge/Settings）
│   ├── ThreeBucket.Mobile/    # MAUI 移动端（net10.0-android / net10.0-ios）
│   └── ThreeBucket.Demo/      # 回归验证（--engine 离线回归 / --toast 通知诊断）
├── desktop/                   # 各平台打包入口（build.bat 菜单：Windows/Linux/macOS/Android/iOS）
│   ├── windows|linux|macos/   # 对应平台 dist/ 发布产物
│   └── legacy-pyqt/           # 旧 PyQt 桌面端（已归档，仅参考）
├── trading-system/            # 策略文档 & 配置（01~08 号文件，02_strategy_config.yaml 为核心配置）
├── scripts/                   # Python 版 T1~T8 脚本（应急回退，定时调度已停用）
├── skills/                    # LLM 动态判定 prompt 模板（T4/T5/T6 用）
├── .github/workflows/         # cs-*.yml（C# 调度）+ 旧 Python workflow（手动应急）+ 打包
├── app_config.json            # C# 客户端运行配置（LLM/数据源/云同步/推送/调度）
└── data/                      # 运行时输出（台账、报告、缓存、候选池）
    ├── live_trade_log.csv     # 实盘交易日志（04号模板运行副本）
    ├── live_signal_log.csv    # 实盘信号台账（07号模板运行副本）
    ├── strategies.csv         # 策略配置运行副本
    ├── watchlist.csv          # 自选股
    ├── monitor_alerts.json    # 盘中监控告警
    ├── portfolio_nav.csv      # 组合净值曲线
    ├── candidates_A/B/C.csv   # 三桶候选池
    ├── skill_input/output_*.md # LLM 半自动流程输入/输出
    ├── report_日期_T*.md      # 各任务盘后报告
    ├── backtest_*.csv         # T7 回测结果
    └── cache/                 # 行情/财报缓存（parquet，跨运行复用）
```

## 各任务详细说明

### T1 · 每日盘后风控扫描（`DailyRiskTask`）

**频率**：工作日盘后 16:30
**做什么**：对当前持仓做5项静态检查：
1. C桶持仓回撤 & 是否跌破60日均线（触发 C-E1/C-E2 规则）
2. 全组合止损：B桶亏损>-25% 或 C桶亏损>-15% 时告警
3. C桶浮盈提示：盈利≥40% 提示减仓，≥80% 提示清仓
4. 集中度检查：单票占比 >8%/6%/4%（A/B/C桶）、单行业 >20% 时告警
5. 组合级回撤：-15% 警戒线、-20% 熔断线

**产出**：`data/report_日期_T1.md`（风控报告）+ 触发的P0/P1信号写入台账 + 推送通知

### T2 · 周度红利择时评级（`WeeklyDividendTask`）

**频率**：每周一 08:30
**做什么**：拉取5类指标判定红利桶（A桶）的市场状态档位 S0~S3：

| 指标类别 | 具体指标 | 含义 |
|---------|---------|------|
| 估值类 | 中证红利股息率5年分位 | 当前股息率在5年中排第几高，越高=越便宜=越该买 |
| 估值类 | ERP（股权风险溢价） | 股票相对债券的超额收益，越高=股票越便宜 |
| 估值类 | 红利相对超额60日 | 红利板块近60天相对大盘的超额收益 |
| 宏观流动性 | 全A 20日均量分位 | 全市场成交量在近1年中排第几，越低=越冷清 |
| 宏观流动性 | 10年期国债收益率 | 无风险利率，越高=债券越有吸引力=股票承压 |
| 宏观流动性 | 红利板块相对成交度分位 | 红利板块成交是否过热 |
| 情绪类 | 拥挤度 | 红利板块是否被过度追捧 |
| 情绪类 | 沪深300回撤 | 大盘从高点跌了多少 |

**档位含义**：

| 档位 | 含义 | 操作建议 |
|------|------|---------|
| S0 | 极度低估，红利黄金坑 | A桶满配，首档投入30%弹药 |
| S1 | 低估，红利有性价比 | A桶标准配置，首档投入30%弹药 |
| S2 | 估值中性 | A桶维持，不增不减 |
| S3 | 估值偏高，红利泡沫 | A桶减仓至目标下限 |

**产出**：`data/report_日期_T2.md`（档位判定+调仓建议）+ 信号写入台账

### T3 · 月度再平衡检查（`MonthlyRebalanceTask`）

**频率**：每月首个交易日 09:30
**做什么**：
1. 计算A/B/C/D四桶实际权重（从交易日志算）
2. 与目标权重（由T2档位决定）对比，偏离>5%输出调仓建议
3. 校验弹药桶（D桶）≥15%（弹药桶=现金/低风险资产储备，用于跌时抄底）
4. 检查D→C直转是否违规（弹药桶资金不能直接转入C桶热点，必须先回A/B桶）
5. 统计本月分红到账、C桶已兑现利润入D桶金额

**产出**：`data/report_日期_T3.md`（再平衡建议）

### T4 · 财报季文本景气扫描（`EarningsScanTask`）

**频率**：财报季（4/8/10月每周一 19:00）
**做什么**：单任务两步走（先入账上轮，再准备下轮）：
1. **ingest**：读取上轮 LLM 判定输出（`skill_output_T4C.md`），过滤PASS的票写入信号台账
2. **prepare**：从全市场自动发现扫描池（关键词命中+高增长），拉取业绩预告+互动易问答文本，组装成下轮输入文件

**关键词三类**（C桶文本信号）：

| 类别 | 权重 | 含义 | 典型关键词 |
|------|------|------|-----------|
| demand（需求） | ×1.2 | 下游需求旺盛 | 需求旺盛、订单饱满、供不应求、在手订单充足 |
| price（价格） | ×1.5 | 产品涨价/量价齐升 | 涨价、提价、价格上涨、量价齐升、销售均价 |
| supply（供给） | ×1.3 | 供给偏紧/产能满 | 产能利用率、供给偏紧、供应偏紧 |

**反向词**（顶部信号）：行业竞争加剧、新增产能投放、积极扩产、控制库存、价格承压

**数据来源**：东方财富业绩预告/业绩报表、巨潮互动易问答

**产出**：`data/skill_input_T4C.md`（喂给LLM）→ `data/skill_output_T4C.md`（LLM判定JSON）→ 写入 `data/live_signal_log.csv`

### T5 · 季度归因复盘（`AttributionPrepTask`）

**频率**：季末
**做什么**：组装季度回顾数据喂给LLM做归因分析：
- 季度内交易记录（04号日志）
- 季度内信号台账（07号）
- 基准指数走势（沪深300 + 各桶代表ETF）

**产出**：`data/skill_input_T5.md` → 喂LLM → `data/skill_output_T5.md`

### T6 · 候选池筛选与排序（`CandidatePoolTask`）

**频率**：每周五 20:00 / 周一 08:30
**做什么**：三桶各自硬门槛过滤 + 排序

**A桶（红利逆向）候选池** `candidates_A.csv` 列说明：

| 列名 | 中文含义 |
|------|---------|
| code | 股票代码 |
| name | 股票名称 |
| industry | 申万一级行业 |
| price | 最新价 |
| dividend_yield_ttm | 股息率（过去12个月分红/市值），≥5%才入池 |
| dividend_percentile_5y | 股息率5年分位（当前股息率在5年中排第几高） |
| roe_5y_avg | 净资产收益率（5年平均），≥8%才入池 |
| fcf_coverage | 自由现金流覆盖倍数（经营现金流/分红），防借钱分红 |
| pb | 市净率，≤2.0才入池 |
| pb_percentile | 市净率分位（当前PB在历史上排第几低） |
| dividend_years | 连续分红年数 |
| loss_q_3y | 近3年单季亏损次数，须=0 |
| ocf_ps_annual | 年报每股经营现金流，须>0 |
| quality_score | 质量系数（综合ROE稳定性+分红连续性+现金流覆盖） |
| sort_value | 排序值 = 股息率 × 质量系数，越高越优先 |
| pick_reason | 入选理由（各项指标是否达标的文字描述） |

**B桶（成长）候选池** `candidates_B.csv` 列说明：

| 列名 | 中文含义 |
|------|---------|
| code / name / industry | 同上 |
| price | 最新价 |
| total_mv_yi | 总市值（亿元），≥50亿才入池 |
| profit_cagr_3y | 净利润3年复合增长率，≥20%才入池 |
| revenue_cagr_3y | 营收3年复合增长率，≥15%才入池 |
| np_yoy_latest | 最新报告期净利润同比增速，≥15%才入池 |
| roe_ann | 年化净资产收益率，≥8%才入池 |
| ocf_to_np | 经营现金流/净利润，≥0.5才入池（防纸面利润） |
| loss_q_3y | 近3年单季亏损次数，须=0 |
| pe_ttm | 滚动市盈率，0<PE≤60才入池 |
| peg | PEG = PE ÷ 增速，≤1.2才入池（低PEG=便宜成长） |
| sort_value | 排序值 = min(增速,100)/PE = 1/PEG，越高越优先 |
| pick_reason | 入选理由 |

**C桶（热点周期）候选池** `candidates_C.csv` 列说明：

| 列名 | 中文含义 |
|------|---------|
| code / name / industry | 同上 |
| text_score | 文本得分（T4阶段关键词加权分：price×1.5 + demand×1.2 + supply×1.3） |
| categories_hit_count | 命中的关键词类别总数 |
| np_yoy | 净利润同比增速（财报验证，clip 500%防极端值） |
| revenue_yoy | 营收同比增速（验证量是否跟上） |
| gross_margin | 毛利率（验证定价权，<10%说明无定价权） |
| has_irm | 是否有互动易文本 |
| negative_hits | 反向词命中（顶部信号） |

**产出**：`candidates_A/B/C.csv` + `skill_input_T6_A/B/C.md` → 喂LLM → `skill_output_T6_A/B/C.md`（三档全量+REJECT+景气分析）

### T7 · 参数回测（`BacktestTask`）

**频率**：每月28日 20:00
**做什么**：验证策略参数历史胜率，一次运行输出 A/B/C 全桶结果
**产出**：`data/report_日期_T7.md`（回测摘要）+ `data/backtest_*.csv`

### T8 · 信号台账维护（`SignalLogTask`）

**频率**：工作日 17:00
**做什么**：
1. 校验信号台账格式（扫描格式错误的行）
2. 回补历史信号收益：对每条"已执行"的信号，过了60/120/250交易日后拉执行价和当期价，算收益
3. 实盘 vs 回测胜率对比：按桶×规则汇总最近30/90天，如果实盘胜率比回测低>10%→触发失效预警

**信号台账** `data/live_signal_log.csv` 列说明：

| 列名 | 中文含义 |
|------|---------|
| signal_id | 信号编号（SIG-日期-桶-序号） |
| 触发日期 | 信号触发日期 |
| yaml_version_at_trigger | 触发时的策略配置版本 |
| 触发任务 | T1/T2/T3/T4 哪个任务触发的 |
| 桶 | A/B/C/D 哪个桶 |
| 规则ID | 触发的规则编号（如 A-S1-01 = A桶S1档第1条规则） |
| 标的代码 / 标的名称 | 股票代码和名称 |
| 触发时指标值 | 触发时的具体指标数值（如"股息率分位72%"） |
| 阈值 | 规则要求的阈值（如">=70%"） |
| 信号方向 | 买入/卖出/观察 |
| 建议动作 | 具体建议（如"A桶弹药首档投入30%"） |
| 是否实际执行 | 是/否（由人决定是否执行） |
| 执行日期 / 执行价格 | 实际执行的日期和价格 |
| 回测预期胜率 | 该规则在历史回测中的胜率 |
| 回测预期中位收益_60d/120d/250d | 回测中买入后60/120/250天的中位收益 |
| 事后60/120/250日收益% | 实盘买入后60/120/250天的实际收益（T8回补） |
| 事后60/120/250日超额沪深300% | 相对沪深300的超额收益 |
| 事后60/120/250日超额分桶基准% | 相对分桶基准ETF的超额收益 |
| 信号最终评价 | 盈利/亏损/持平 |
| 备注 | 补充说明 |

## 快速开始（C#）

环境要求：.NET 10 SDK（`dotnet --version` 检查）。移动端另需 `dotnet workload install android` / `ios`。

```powershell
# 构建（Mobile 不在解决方案内，普通机器可直接 build）
dotnet build src/ThreeBucket.slnx

# 运行桌面端（Avalonia）
dotnet run --project src/ThreeBucket.UI

# CLI：列出全部内置任务
dotnet run --project src/ThreeBucket.Cli -- --list

# CLI：运行指定任务（逗号分隔），--data 指定数据目录（默认自动定位仓库根的 data/）
dotnet run --project src/ThreeBucket.Cli -c Release -- --task T1,T8
dotnet run --project src/ThreeBucket.Cli -c Release -- --task T3 --data D:\path\to\data

# CLI：组合 Supabase 云同步（先拉云端最新用户数据 → 跑任务 → 推产物回云端）
dotnet run --project src/ThreeBucket.Cli -c Release -- --sync pull --task T1,T8 --sync push

# 回归验证（离线策略引擎+通知回归，无网络依赖；--toast 单独诊断系统通知）
dotnet run --project src/ThreeBucket.Demo -- --engine
```

CLI 退出码：`0`=全部成功，`1`=任一任务失败，`2`=参数错误；`--sync` 失败不阻断（辅助操作）。

### 桌面端/移动端打包

```powershell
# 构建中心菜单（1=Windows 2=Linux 3=macOS 4=全部桌面 5=Android 6=iOS编译验证）
desktop\build.bat

# 或直接用跨平台脚本（-SelfContained 内嵌运行时，-All 全平台，-Run 构建后运行）
src\build.ps1 -Target win-x64 -SelfContained
```

移动端完整打包（Android APK + iOS ipa）走 GitHub Actions：手动触发 `Build Mobile Release` 或推送 `v*` tag，产物自动挂到 Release（iOS 需 macOS runner，macOS 上也可本地跑 `desktop/ios/build.sh`）。

### 桌面端内置调度

桌面端自带任务调度器（设置页可配，存 `app_config.json`：`SchedulerEnabled` / `SchedulerTime` / `SchedulerTasksStr`，默认 16:30 跑 T1 T8），无需依赖 CI 即可在本机定时执行；另有 `AutoSync` 自动云同步与盘后自动刷新。

## LLM 半自动流程

T4/T5/T6 中涉及 LLM 的部分采用"任务准备输入 → 调用 LLM → 任务消费输出"的闭环。桌面端「LLM 桥接」页可一键完成：选 Skill → 生成输入（运行任务）→ 调用 LLM（走 `app_config.json` 配置的 LLM 网关）→ 保存输出，T6 还支持三桶连续调用；也可以手动把输入粘贴给 Qoder 等外部 Agent。

| 步骤 | 命令/操作 | LLM prompt | 产出文件 |
|------|----------|-----------|----------|
| T4 准备+入账 | CLI `--task T4` 或 UI 生成输入 | — | `data/skill_input_T4C.md` |
| T4 判定 | UI 调用 LLM / 手动喂 LLM | `skills/t4_c_text_scan.md` | `data/skill_output_T4C.md` |
| T5 准备 | CLI `--task T5` | — | `data/skill_input_T5.md` |
| T5 归因 | UI 调用 LLM / 手动喂 LLM | `skills/t5_attribution.md` | `data/skill_output_T5.md` |
| T6 筛选 | CLI `--task T6` | — | `data/skill_input_T6_A/B/C.md` + CSVs |
| T6 排序 | UI 全桶调用 / 手动喂 LLM | `skills/t6_semantic_ranking.md` | `data/skill_output_T6_A/B/C.md` |

## GitHub Actions 调度

定时调度已全部由 C# CLI 接管（输出文件与 Python 版完全兼容）：

| Workflow | 触发时间 | 任务 |
|----------|----------|------|
| `cs-daily.yml` | 工作日 16:30 / 17:00 | T1 风控 + T8 台账维护 |
| `cs-weekly.yml` | 周一 08:30 / 周五 20:00 | T2 红利判定（周一）+ T6 候选池 |
| `cs-monthly.yml` | 每月1日 09:30 / 28日 20:00 | T3 再平衡 + T7 回测 |
| `cs-quarterly.yml` | 季末28日 21:00 | T5 归因准备 |
| `cs-earnings-season.yml` | 4/8/10月周一 19:00 | T4 财报季扫描 |
| `keepalive.yml` | 每日 18:00 | Supabase 免费项目保活（只读请求） |
| `build_release.yml` | 每日 19:30（有新提交时） | 桌面端 win-x64 自动构建发布（C#/Avalonia，self-contained，暂只验证 Windows，其他平台用本地脚本出包） |
| `build_mobile.yml` | 手动 / push tag `v*` | Android APK + iOS ipa 打包发布 |

所有 `cs-*` workflow 统一执行 `dotnet run --project src/ThreeBucket.Cli -c Release -- --sync pull --task ... --sync push`（拉云端最新用户数据 → 跑任务 → 推产物），并把台账/报告提交回仓库。

## 配置

### app_config.json（C# 客户端本地配置）

由桌面端设置页读写，CLI 也会读取（环境变量优先）：

| 配置项 | 用途 |
|---|---|
| `ThsApiKey` | 同花顺（扶摇）数据源 key（申请：https://fuyao.aicubes.cn） |
| `SupabaseUrl` / `SupabaseKey` | Supabase 云同步（strategies / trades / watchlist / alerts 四类数据） |
| `LlmApiUrl` / `LlmApiKey` / `LlmModel` | LLM 桥接调用的网关地址、key 与模型 |
| `LarkWebhook` / `NotifyLarkEnabled` | 飞书机器人推送 |
| `NotifySystemEnabled` | 系统通知（Windows Toast 等） |
| `SmtpHost/Port/User/Pass/To` / `MonitorEmailEnabled` | 邮箱推送 |
| `SchedulerEnabled` / `SchedulerTime` / `SchedulerTasksStr` | 桌面端内置调度器 |
| `AutoSync` / `AutoRefresh` / `RefreshInterval` / `MonitorInterval` | 自动云同步与行情自动刷新 |

### 环境变量 / CI Secrets

见 `.env.example`。CI（`cs-*` workflows）由 GitHub Actions Secrets 注入，环境变量优先于 `app_config.json`：

| 变量 | 用途 | 必需 |
|---|---|---|
| `SUPABASE_URL` / `SUPABASE_KEY` | 云同步（anon key，与设置页一致） | 云同步需要，未配置自动跳过 |
| `THS_API_KEY` | 同花顺数据源主源 | 否（未配置走免费源） |
| `TUSHARE_TOKEN` | tushare 备用数据源（Python 版用） | 否 |
| `WECOM_BOT_KEY` / `LARK_WEBHOOK` | 企业微信 / 飞书推送（Python 版脚本用；C# 版推送改配 `app_config.json` 的 `LarkWebhook`/`Smtp*`，CI 不注入） | 否 |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USER` / `SMTP_PASS` / `SMTP_TO` | 邮箱推送 | 否（C# 版在设置页/app_config.json 配置） |
| `CLIENT_API_TOKEN` | 客户端后端拉取台账用（只读，可随时 rotate） | 否 |

配置步骤：仓库 → Settings → Secrets and variables → Actions → New repository secret

## 遗留 Python 版（应急回退）

C# 版接管调度后，以下 Python 内容保留作应急回退，日常不再使用：

- `scripts/`：Python 版 T1~T8 脚本（`pip install -r scripts/requirements.txt` 后 `python scripts/t1_daily_risk.py` 等）
- `desktop/legacy-pyqt/`：旧 PyQt 桌面端（PyInstaller 打包）
- `.github/workflows/` 中不带 `cs-` 前缀的旧 workflow（`daily.yml` / `weekly.yml` / `monthly.yml` / `quarterly.yml` / `earnings_season.yml`）：定时已注释，仅保留 `workflow_dispatch` 手动触发。每日构建 `build_release.yml` 已改为 C# 版（原 PyInstaller/PyQt 流程已下线）

## 免责声明

本项目为个人量化研究工具，输出为**参考提醒**而非投资建议。所有交易由用户自行决策与执行。
