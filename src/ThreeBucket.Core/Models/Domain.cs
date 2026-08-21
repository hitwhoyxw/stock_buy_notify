namespace ThreeBucket.Core.Models;

/// <summary>交易流水一条记录（对应 live_trade_log.csv 的列）。</summary>
public class Trade
{
    public string Date { get; set; } = "";
    public string Direction { get; set; } = "";      // 买入 / 卖出
    public string Bucket { get; set; } = "";          // A/B/C/D
    public string Code { get; set; } = "";            // 6 位代码（保前导零）
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";        // 申万一级行业
    public double Price { get; set; }
    public double Shares { get; set; }
    public double Amount { get; set; }
    public string RuleId { get; set; } = "";
    public string Reason { get; set; } = "";

    /// <summary>转回 CSV 行（仅保留核心 11 列，与 Python TRADE_COLUMNS 前段一致）。</summary>
    public Dictionary<string, string> ToRow() => new()
    {
        ["日期"] = Date,
        ["方向"] = Direction,
        ["桶"] = Bucket,
        ["代码"] = Code,
        ["名称"] = Name,
        ["申万一级行业"] = Industry,
        ["价格"] = Price.ToString("F3"),
        ["股数"] = Shares.ToString("F0"),
        ["金额"] = Amount.ToString("F2"),
        ["触发规则ID"] = RuleId,
        ["决策理由(一句话)"] = Reason,
    };
}

/// <summary>加权成本法聚合后的当前持仓。</summary>
public class Position
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Industry { get; set; } = "";
    public double Shares { get; set; }
    public double CostPool { get; set; }          // 累计成本金额（净投入）
    public double AvgCost { get; set; }           // 平均成本 = 成本池 / 净股数
    public double CurrentPrice { get; set; }      // 实时现价（未刷新为 0）
    public double MarketValue => Shares * (CurrentPrice > 0 ? CurrentPrice : AvgCost);
    public double Pnl => MarketValue - CostPool;
    public double PnlPct => CostPool > 0 ? Pnl / CostPool * 100 : 0;
}

/// <summary>监控自选一条（watchlist.csv）。</summary>
public class WatchItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string AddedFrom { get; set; } = "";
    public string AddedAt { get; set; } = "";
    public string Strategies { get; set; } = "";  // 分号分隔的策略 id
    public string Note { get; set; } = "";
}

/// <summary>策略定义（strategies.csv）。</summary>
public class Strategy
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";        // buy / hold / sell
    public string Indicator { get; set; } = "";
    public string Operator { get; set; } = "";    // < <= >= > == !=
    public string Threshold { get; set; } = "";
    public string Condition { get; set; } = "";   // 复合条件树 JSON（简单策略留空）
    public string Action { get; set; } = "";
    public string Priority { get; set; } = "";    // P0/P1/P2
    public bool Enabled { get; set; } = true;
}

/// <summary>提醒历史一条（monitor_alerts.json）。</summary>
public class AlertEntry
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string StrategyId { get; set; } = "";
    public string StrategyName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Time { get; set; } = "";
}

/// <summary>应用配置（对应 Python 的 app_config.json）。</summary>
public class AppConfig
{
    public string ProjectRoot { get; set; } = "";
    public string PythonExe { get; set; } = "";
    public string DataDir { get; set; } = "";
    public bool AutoRefresh { get; set; } = true;
    public int RefreshInterval { get; set; } = 60;
    public bool SchedulerEnabled { get; set; }
    public string SchedulerTime { get; set; } = "16:30";
    public string SchedulerTasksStr { get; set; } = "T1 T8";
    public int MonitorInterval { get; set; } = 60;
    public bool MonitorEmailEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public string SmtpUser { get; set; } = "";
    public string SmtpPass { get; set; } = "";
    public string SmtpTo { get; set; } = "";
    public string LlmApiUrl { get; set; } = "";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "gpt-4o";
    public string SupabaseUrl { get; set; } = "";   // 云同步：https://xxxx.supabase.co
    public string SupabaseKey { get; set; } = "";   // 云同步：anon public key
}
