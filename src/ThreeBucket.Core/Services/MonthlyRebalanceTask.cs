using System.Globalization;
using ThreeBucket.Core.Data;

namespace ThreeBucket.Core.Services;

/// <summary>
/// T3 · 月度再平衡检查（C# 版，移植自 scripts/t3_monthly_rebalance.py，桌面/移动端通用）。
///
/// 1. 读台账上次 T2 档位 → allocation.states 目标四桶权重
/// 2. 与交易日志实际权重（成本加权）对比，偏离 &gt; 5pct 输出 REBAL-{桶} 建议（P1）
/// 3. D 桶 &lt; 15% 触发 REBAL-D-FLOOR（P0，禁止 D → C 直转）
/// 4. 本月纪律记分卡：台账本月信号数 / 执行数
///
/// 触发时机为每月首个交易日（由调度器/人工保证，任务本身不重复校验日期）。
/// </summary>
public class MonthlyRebalanceTask : IBuiltinTask
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Key => "T3";
    public string Name => "每月再平衡";

    private readonly string _dataDir;
    private readonly DataStore _store;
    private readonly SignalLogStore _signals;

    public MonthlyRebalanceTask(string dataDir, DataStore store, SignalLogStore signals)
    {
        _dataDir = dataDir;
        _store = store;
        _signals = signals;
    }

    public Task<TaskRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string msg) => log?.Invoke($"[T3] {msg}");
        try
        {
            var today = TradingCalendar.NowCn();
            var tier = ReadLastTier();
            L($"当前档位 {tier}（台账 T2 最近记录）");

            var target = StrategyConfig.AllocationStates.GetValueOrDefault(tier)
                ?? StrategyConfig.AllocationStates["S0"];
            var weights = _store.BucketWeights();
            var trigger = StrategyConfig.RebalanceDeviationTriggerPct / 100;

            var deltaRows = new List<Dictionary<string, string>>();
            var alerts = new List<RiskAlert>();
            foreach (var k in new[] { "A", "B", "C", "D" })
            {
                var cur = weights.GetValueOrDefault(k, 0.0);
                var tgt = target.GetValueOrDefault(k, 0.0);
                var dev = tgt - cur;
                deltaRows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["桶"] = k,
                    ["当前%"] = (cur * 100).ToString("0.0", Inv),
                    ["目标%"] = (tgt * 100).ToString("0.0", Inv),
                    ["偏离%"] = (dev * 100).ToString("+0.0;-0.0", Inv),
                });
                if (Math.Abs(dev) > trigger)
                    alerts.Add(new RiskAlert("P1", $"REBAL-{k}", k, $"{k} 桶偏离目标",
                        $"{cur * 100:0.0}%",
                        $"目标 {tgt * 100:0.0}% ± {StrategyConfig.RebalanceDeviationTriggerPct:0}pct",
                        (dev > 0 ? "加仓" : "减仓") + $" {Math.Abs(dev) * 100:0.0}pct",
                        $"04_交易日志 · 档位 {tier}"));
            }

            if (weights.GetValueOrDefault("D", 0.0) < StrategyConfig.RebalanceDBucketFloor)
                alerts.Add(new RiskAlert("P0", "REBAL-D-FLOOR", "D", "D 桶地板",
                    $"{weights.GetValueOrDefault("D", 0.0) * 100:0.0}%",
                    $">= {StrategyConfig.RebalanceDBucketFloor * 100:0}%",
                    "立即补充 D 桶弹药，禁止 D → C 直转", "组合规则"));

            // 本月纪律记分卡（触发日期为 ISO 格式，字符串序即可比较）
            var monthStart = new DateTime(today.Year, today.Month, 1).ToString("yyyy-MM-dd");
            var all = _signals.ReadAll();
            var monthRows = all
                .Where(r => string.CompareOrdinal(r.GetValueOrDefault("触发日期", ""), monthStart) >= 0)
                .ToList();
            var executed = monthRows.Count(r => r.GetValueOrDefault("是否实际执行", "") == "是");

            foreach (var a in alerts)
                _signals.AppendSignal(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["触发日期"] = today.ToString("yyyy-MM-dd"),
                    ["yaml_version_at_trigger"] = StrategyConfig.YamlTag,
                    ["触发任务"] = "T3",
                    ["桶"] = a.Bucket,
                    ["规则ID"] = a.RuleId,
                    ["标的代码"] = "-",
                    ["标的名称"] = "组合",
                    ["触发时指标值"] = a.Current,
                    ["阈值"] = a.Threshold,
                    ["当时组合状态"] = tier,
                    ["信号方向"] = "调仓",
                    ["建议动作"] = a.Action,
                    ["是否实际执行"] = "否",
                });

            var sections = new List<(string, string)>
            {
                ("档位与四桶权重对照",
                    $"当前档位 **{tier}**\n\n"
                    + ReportBuilder.RenderKvTable(deltaRows, new[] { "桶", "当前%", "目标%", "偏离%" })),
                ("本月纪律记分卡",
                    $"本月台账信号数：{monthRows.Count}；执行数：{executed}\n"),
                ("yaml 版本", $"`{StrategyConfig.YamlTag}`（C# 内置常量，对应 02_strategy_config.yaml v1.0）\n"),
            };
            var path = ReportBuilder.WriteReport(_dataDir, "T3",
                $"T3 月度再平衡 · {today:yyyy-MM-dd}", sections, alerts);
            L($"报告已写入 {path}，偏离触发 {alerts.Count} 项");
            return Task.FromResult(new TaskRunResult(true, path, alerts.Count,
                $"偏离触发 {alerts.Count} 项，档位 {tier}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new TaskRunResult(false, "", 0, $"T3 失败: {ex.Message}"));
        }
    }

    /// <summary>台账上次 T2 判定档位（"当时组合状态"取末段；无记录 → S0）。</summary>
    private string ReadLastTier()
    {
        var t2 = _signals.ReadAll()
            .Where(r => r.GetValueOrDefault("触发任务") == "T2")
            .OrderBy(r => r.GetValueOrDefault("触发日期")).ToList();
        if (t2.Count == 0) return "S0";
        var state = t2[^1].GetValueOrDefault("当时组合状态", "");
        var last = state.Length > 0 ? state.Split("->")[^1].Trim() : "";
        return last.Length > 0 ? last : "S0";
    }
}
