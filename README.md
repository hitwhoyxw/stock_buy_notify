# 三桶资产交易策略系统

Agent 只出提醒、买卖仍由人决策的 A 股三桶（红利逆向 / 成长 / 热点周期）策略实现。

## T1~T8 任务总览

| 任务 | 脚本 | 频率 | 做什么 | 产出文件 |
|------|------|------|--------|----------|
| **T1 每日风控** | `t1_daily_risk.py` | 工作日盘后 | 检查持仓回撤/止损/集中度/MA60破位，触发告警写信号台账 | `data/report_日期_T1.md` |
| **T2 周度红利择时** | `t2_weekly_dividend.py` | 每周一 | 拉估值/宏观/情绪指标，判定红利桶档位 S0~S3，输出调仓建议 | `data/report_日期_T2.md` |
| **T3 月度再平衡** | `t3_monthly_rebalance.py` | 每月首日 | 检查四桶实际权重 vs 目标偏离，校验弪药桶≥15%，统计分红入D | `data/report_日期_T3.md` |
| **T4 财报季扫描** | `t4_ingest.py` | 4/8/10月+每周五 | 从业绩预告+互动易问答搜关键词→LLM判定景气→写入信号台账 | `skill_input_T4C.md` → `skill_output_T4C.md` → `live_signal_log.csv` |
| **T5 季度归因** | `t5_prepare.py` | 季末 | 组装季度交易日志+信号台账+基准走势→LLM做归因复盘 | `skill_input_T5.md` → `skill_output_T5.md` |
| **T6 候选池筛选** | `t6_candidate_pool.py` | 每周五/季末 | 三桶各自硬门槛过滤→排序→输出候选池CSV→LLM语义排序Top10 | `candidates_A/B/C.csv` + `skill_input_T6.md` → `skill_output_T6.md` |
| **T7 参数回测** | `t7_backtest.py` | 月末/季末 | 包装06号回测脚本，验证策略参数历史胜率 | `data/report_日期_T7.md` |
| **T8 信号台账维护** | `t8_signal_log.py` | 工作日17:00 | 回补历史信号60/120/250日收益，对比实盘vs回测胜率，触发失效预警 | `data/report_日期_T8.md` |

## 目录结构

```
.
├── trading-system/       # 策略文档 & 配置（01~08 号文件）
│   ├── 01_策略系统总纲.md      # 策略设计起点，从这里读起
│   ├── 02_strategy_config.yaml  # 核心配置（三桶参数/阈值/关键词）
│   ├── 03_Agent定期任务.md      # T1~T8 任务定义文档
│   ├── 04_交易日志模板.csv      # 手动记录买卖的台账
│   ├── 05_个股筛选与回测.md      # 选股+回测方法论
│   ├── 06_backtest_*.py         # 各桶回测脚本
│   ├── 07_信号台账模板.csv      # Agent 自动写入的信号记录
│   └── 08_部署与客户端.md       # 部署方案
├── scripts/              # 静态量化脚本（T1~T8）
│   ├── lib/              # 通用库
│   │   ├── config.py         # 读取 02_strategy_config.yaml
│   │   ├── data_fetch.py     # akshare/tushare 数据拉取（行情/财报/互动易）
│   │   ├── notifier.py       # 推送通知（企业微信/飞书/邮件）
│   │   ├── paths.py          # 路径常量
│   │   ├── report.py         # 报告生成
│   │   ├── signal_log.py     # 信号台账读写（07号CSV）
│   │   ├── trade_log.py      # 交易日志读写（04号CSV）
│   │   └── trading_day.py    # 交易日判定（北京时间）
│   ├── t1_daily_risk.py       # T1 每日风控/止损/集中度
│   ├── t2_weekly_dividend.py  # T2 周度红利择时评级
│   ├── t3_monthly_rebalance.py # T3 月度再平衡偏离检测
│   ├── t4_ingest.py           # T4 财报季文本判定 ingest
│   ├── t5_prepare.py          # T5 季度归因 LLM 输入准备
│   ├── t6_candidate_pool.py   # T6 候选池硬门槛筛选
│   ├── t7_backtest.py         # T7 参数回测
│   ├── t8_signal_log.py       # T8 台账维护 + Alpha衰减
│   └── test_channels.py       # 推送通道连通性测试
├── skills/               # LLM 动态判定 prompt 模板
│   ├── t4_c_text_scan.md      # C桶文本景气判定（给LLM的指令）
│   ├── t5_attribution.md      # 季度归因复盘（给LLM的指令）
│   └── t6_semantic_ranking.md # 候选池语义排序（给LLM的指令）
├── .github/workflows/    # GitHub Actions 定时任务（5 个）
└── data/                 # 运行时输出（台账、报告、缓存、候选池）
    ├── live_trade_log.csv        # 实盘交易日志（04号的运行副本）
    ├── live_signal_log.csv       # 实盘信号台账（07号的运行副本）
    ├── candidates_A.csv           # A桶候选池（红利逆向）
    ├── candidates_B.csv           # B桶候选池（成长）
    ├── candidates_C.csv           # C桶候选池（热点周期）
    ├── skill_input_T4C.md         # T4 输入：财报+互动易文本（喂给LLM）
    ├── skill_output_T4C.md        # T4 输出：LLM判定的PASS/REJECT结果
    ├── skill_input_T5.md          # T5 输入：季度交易+信号+基准（喂给LLM）
    ├── skill_input_T6.md          # T6 输入：三桶候选池CSV（喂给LLM）
    ├── skill_output_T6.md         # T6 输出：LLM语义排序Top10+景气分析
    ├── report_日期_T*.md          # 各任务的盘后报告
    └── cache/                     # 数据缓存（交易日历等）
```

## 各任务详细说明

### T1 · 每日盘后风控扫描（`t1_daily_risk.py`）

**频率**：工作日盘后 16:30
**做什么**：对当前持仓做5项静态检查：
1. C桶持仓回撤 & 是否跌破60日均线（触发 C-E1/C-E2 规则）
2. 全组合止损：B桶亏损>-25% 或 C桶亏损>-15% 时告警
3. C桶浮盈提示：盈利≥40% 提示减仓，≥80% 提示清仓
4. 集中度检查：单票占比 >8%/6%/4%（A/B/C桶）、单行业 >20% 时告警
5. 组合级回撤：-15% 警戒线、-20% 熔断线

**产出**：`data/report_日期_T1.md`（风控报告）+ 触发的P0/P1信号写入台账 + 推送通知

### T2 · 周度红利择时评级（`t2_weekly_dividend.py`）

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

### T3 · 月度再平衡检查（`t3_monthly_rebalance.py`）

**频率**：每月首个交易日 09:30
**做什么**：
1. 计算A/B/C/D四桶实际权重（从交易日志算）
2. 与目标权重（由T2档位决定）对比，偏离>5%输出调仓建议
3. 校验弪药桶（D桶）≥15%（弪药桶=现金/低风险资产储备，用于跌时抄底）
4. 检查D→C直转是否违规（弪药桶资金不能直接转入C桶热点，必须先回A/B桶）
5. 统计本月分红到账、C桶已兑现利润入D桶金额

**产出**：`data/report_日期_T3.md`（再平衡建议）

### T4 · 财报季文本景气扫描（`t4_ingest.py`）

**频率**：财报季（4/8/10月每周一 + 每周五）
**做什么**：分两步走：
1. **--prepare**：从全市场自动发现扫描池（关键词命中+高增长），拉取业绩预告+互动易问答文本，组装成输入文件
2. **ingest**：读取LLM判定输出，过滤PASS的票写入信号台账

**关键词三类**（C桶文本信号）：

| 类别 | 权重 | 含义 | 典型关键词 |
|------|------|------|-----------|
| demand（需求） | ×1.2 | 下游需求旺盛 | 需求旺盛、订单饱满、供不应求、在手订单充足 |
| price（价格） | ×1.5 | 产品涨价/量价齐升 | 涨价、提价、价格上涨、量价齐升、销售均价 |
| supply（供给） | ×1.3 | 供给偏紧/产能满 | 产能利用率、供给偏紧、供应偏紧 |

**反向词**（顶部信号）：行业竞争加剧、新增产能投放、积极扩产、控制库存、价格承压

**数据来源**：
- `stock_yjyg_em`（东财业绩预告）：全市场业绩预告，含变动原因文本
- `stock_yjbb_em`（东财业绩报表）：全市场财报，含营收/净利/毛利率
- `stock_irm_cninfo`（巨潮互动易）：投资者关系问答平台，公司回答中常含经营细节

**产出**：
- `data/skill_input_T4C.md`：输入文件（每只票的财报+互动易文本，喂给LLM）
- `data/skill_output_T4C.md`：LLM判定结果（JSON数组，每只票PASS/REJECT+理由）
- 写入 `data/live_signal_log.csv`（信号台账）

### T5 · 季度归因复盘（`t5_prepare.py`）

**频率**：季末
**做什么**：组装季度回顾数据喂给LLM做归因分析：
- 季度内交易记录（04号日志）
- 季度内信号台账（07号）
- 基准指数走势（沪深300 + 各桶代表ETF）

**产出**：`data/skill_input_T5.md` → 喂LLM → `data/skill_output_T5.md`

### T6 · 候选池筛选与排序（`t6_candidate_pool.py`）

**频率**：每周五 20:00 / 财报季
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

**产出**：`candidates_A/B/C.csv` + `skill_input_T6.md` → 喂LLM → `skill_output_T6.md`（Top10+REJECT+景气分析）

### T7 · 参数回测（`t7_backtest.py`）

**频率**：月末（C桶）/ 季末（A+B桶）
**做什么**：包装 `trading-system/06_backtest_*.py`，验证策略参数历史胜率
**产出**：`data/report_日期_T7.md`（回测摘要）

### T8 · 信号台账维护（`t8_signal_log.py`）

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

## 快速开始（本地）

```bash
pip install -r scripts/requirements.txt

# 每日风控扫描
python scripts/t1_daily_risk.py

# 周度红利状态判定
python scripts/t2_weekly_dividend.py

# 月度再平衡
python scripts/t3_monthly_rebalance.py

# 台账维护（追加新信号、回补历史收益、生成失效预警）
python scripts/t8_signal_log.py

# 参数回测（--bucket A|B|C|AB）
python scripts/t7_backtest.py --bucket A

# T4 财报季流程（分两步）
python scripts/t4_ingest.py --prepare          # Step 1: 自动发现扫描池+拉取文本
# ... 把 data/skill_input_T4C.md 喂给 LLM，产出写到 data/skill_output_T4C.md ...
python scripts/t4_ingest.py                   # Step 2: 读取LLM判定→写入台账

# T5 季度归因（准备 LLM 输入）
python scripts/t5_prepare.py --season 2026Q2

# T6 候选池筛选
python scripts/t6_candidate_pool.py
python scripts/t6_candidate_pool.py --bucket A --dry-run

# 推送通道测试
python scripts/test_channels.py
python scripts/test_channels.py --channel wecom

# 推送本地报告
python -m lib.notifier --latest
```

## LLM 半自动流程

T4/T5/T6 中涉及 LLM 的部分采用"脚本准备输入 → 人工/CI 调用 LLM → 脚本消费输出"的半自动闭环：

| 步骤 | 脚本命令 | LLM prompt | 产出文件 |
|------|----------|-----------|----------|
| T4 准备 | `t4_ingest.py --prepare` | — | `data/skill_input_T4C.md` |
| T4 判定 | _手动喂 LLM_ | `skills/t4_c_text_scan.md` | `data/skill_output_T4C.md` |
| T4 入库 | `t4_ingest.py` | — | 写入 `data/live_signal_log.csv` |
| T5 准备 | `t5_prepare.py` | — | `data/skill_input_T5.md` |
| T5 归因 | _手动喂 LLM_ | `skills/t5_attribution.md` | `data/skill_output_T5.md` |
| T6 筛选 | `t6_candidate_pool.py` | — | `data/skill_input_T6.md` + CSVs |
| T6 排序 | _手动喂 LLM_ | `skills/t6_semantic_ranking.md` | `data/skill_output_T6.md` |

## GitHub Actions 调度

| Workflow | 触发时间 | 任务 |
|----------|----------|------|
| `daily.yml` | 工作日 16:30/17:00 | T1 风控 + T8 台账维护 |
| `weekly.yml` | 周一 08:30 / 周五 20:00 | T2 红利判定 + T6 候选池 |
| `monthly.yml` | 每月 1号 09:30 / 28号 21:00 | T3 再平衡 + T7-C 回测 |
| `quarterly.yml` | 季末 28号 20:00/21:00 | T5 归因准备 + T7-AB 回测 |
| `earnings_season.yml` | 4/8/10月周一 + 每周五 | T4 财报季 + T6 候选池 |

## 环境变量

见 `.env.example`。生产运行由 GitHub Actions Secrets 注入。

| 变量 | 用途 | 必需 |
|---|---|---|
| `TUSHARE_TOKEN` | tushare 备用数据源 | 否（akshare 兜底） |
| `WECOM_BOT_KEY` | 企业微信群机器人 webhook key | P0/P1 推送需要 |
| `LARK_WEBHOOK` | 飞书机器人 webhook 完整 URL | 备用推送通道 |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USER` / `SMTP_PASS` / `SMTP_TO` | 邮箱推送 | 日报归档需要 |

配置步骤：仓库 → Settings → Secrets and variables → Actions → New repository secret

## 免责声明

本项目为个人量化研究工具，输出为**参考提醒**而非投资建议。所有交易由用户自行决策与执行。
