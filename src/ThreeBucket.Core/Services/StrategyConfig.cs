namespace ThreeBucket.Core.Services;

/// <summary>
/// 策略硬编码阈值（对齐 trading-system/02_strategy_config.yaml v1.0）。
/// 与 T1 同一约定：C# 端不解析 yaml，调整阈值需改此文件常量并重新编译。
/// </summary>
public static class StrategyConfig
{
    /// <summary>台账 yaml_version_at_trigger / 报告"yaml 版本"节标识（Python 端为 v{version}-{hash}）。</summary>
    public const string YamlTag = "c#-builtin-v1";

    /// <summary>allocation.states：档位 → 四桶目标权重（A 红利逆向 / B 成长 / C 热点周期 / D 弹药库）。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> AllocationStates =
        new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
        {
            ["S0"] = new Dictionary<string, double> { ["A"] = 0.20, ["B"] = 0.25, ["C"] = 0.10, ["D"] = 0.45 },
            ["S1"] = new Dictionary<string, double> { ["A"] = 0.30, ["B"] = 0.25, ["C"] = 0.10, ["D"] = 0.35 },
            ["S2"] = new Dictionary<string, double> { ["A"] = 0.40, ["B"] = 0.27, ["C"] = 0.08, ["D"] = 0.25 },
            ["S3"] = new Dictionary<string, double> { ["A"] = 0.55, ["B"] = 0.30, ["C"] = 0.00, ["D"] = 0.15 },
        };

    /// <summary>allocation.rebalance.deviation_trigger_pct（pct；偏离超过即触发再平衡建议）。</summary>
    public const double RebalanceDeviationTriggerPct = 5.0;

    /// <summary>allocation.rebalance.d_bucket_floor（小数权重；D 桶弹药库地板）。</summary>
    public const double RebalanceDBucketFloor = 0.15;

    /// <summary>bucket_A.market_signals.benchmark_index（中证红利）。</summary>
    public const string BucketABenchmark = "000922.SH";
}
