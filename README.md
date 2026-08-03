# 三桶资产交易策略系统

Agent 只出提醒、买卖仍由人决策的 A 股三桶（红利逆向 / 成长 / 热点周期）策略实现。

## 目录

```
.
├── trading-system/       # 策略文档 & 配置（01~08 号文件）
├── scripts/              # 静态量化脚本（T1~T8）
│   ├── lib/              # 通用库（8 模块）
│   ├── t1_daily_risk.py       # 每日风控/止损/集中度
│   ├── t2_weekly_dividend.py  # 周度红利择时评级
│   ├── t3_monthly_rebalance.py # 月度再平衡偏离检测
│   ├── t4_ingest.py           # 财报季文本判定 ingest
│   ├── t5_prepare.py          # 季度归因 LLM 输入准备
│   ├── t6_candidate_pool.py   # 候选池硬门槛筛选
│   ├── t7_backtest.py         # 参数回测
│   ├── t8_signal_log.py       # 台账维护 + Alpha衰减
│   └── test_channels.py       # 推送通道连通性测试
├── skills/               # LLM 动态判定 prompt 模板
│   ├── t4_c_text_scan.md      # C桶文本景气判定
│   ├── t5_attribution.md      # 季度归因复盘
│   └── t6_semantic_ranking.md # 候选池语义排序
├── .github/workflows/    # GitHub Actions 定时任务（5 个）
└── data/                 # 运行时输出（台账、报告、缓存、候选池）
```

策略设计文档从 `trading-system/01_策略系统总纲.md` 开始读起。
部署与客户端方案见 `trading-system/08_部署与客户端.md`。

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
python scripts/t4_ingest.py --prepare --codes 600028,601088  # Step 1: 准备输入
# ... 把 data/skill_input_T4C.md 喂给 LLM，产出写到 data/skill_output_T4C.md ...
python scripts/t4_ingest.py                                   # Step 2: 消费写入台账

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
