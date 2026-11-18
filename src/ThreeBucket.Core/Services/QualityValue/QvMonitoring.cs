namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>价格监控/交易执行条件，禁止改变 Pool、QualityScore 或财务门槛。</summary>
public static class QvMonitoring
{
    public static void Apply(QvQuote quote, QvRow row, QvConfig cfg)
    {
        var names = new[] { "monitoring_ineligible", "liquidity_low", "liquidity_missing", "price_missing" };
        row.FlagList.RemoveAll(names.Contains);
        row.MonitoringTurnover60 = quote.Turnover60 is double t && double.IsFinite(t) ? t : null;
        row.MonitoringAsOf = quote.AsOf;
        var reasons = new List<string>();
        var min = cfg.D("global.liquidity_avg_amount_min", 5e7);
        if (row.MonitoringTurnover60 is not double value || value < 0)
        {
            row.MonitoringLiquidityStatus = "missing"; row.FlagList.Add("liquidity_missing"); reasons.Add("缺60日成交额，监控执行资格待核验");
        }
        else if (value < min)
        {
            row.MonitoringLiquidityStatus = "low"; row.FlagList.Add("liquidity_low"); reasons.Add($"60日日均成交额 {value / 1e8:F2}亿 < {min / 1e8:F2}亿");
        }
        else row.MonitoringLiquidityStatus = "eligible";
        if (row.Price is not double price || !double.IsFinite(price) || price <= 0)
        {
            row.FlagList.Add("price_missing"); reasons.Add("缺有效现价（行情问题，不代表公司差）"); row.Zone = "";
        }
        row.PriceMonitoringEligible = reasons.Count == 0;
        row.MonitoringReason = reasons.Count == 0 ? "价格监控条件满足；仍须估值/风险/复核允许才提醒" : string.Join("、", reasons);
        if (reasons.Count > 0) row.FlagList.Add("monitoring_ineligible");
        // daily 不重算财务；只增删本模块自己的阻断原因，保留原有风险/估值/人工复核阻断。
        var blocks = (row.StrongAlertBlockReason ?? "").Split('；', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith("监控资格：", StringComparison.Ordinal)).ToList();
        if (reasons.Count > 0) blocks.Add("监控资格：" + row.MonitoringReason);
        row.StrongAlertBlockReason = string.Join("；", blocks);
        row.StrongAlertBlocked = blocks.Count > 0;
    }
}
