using System.Globalization;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>CLI 入口：ThreeBucket.Cli --br scan|track|replay|backtest|backtest12|diag|selftest [--codes 600276,603379] [--days N] [--limit N] [--out 目录]</summary>
public static class BrCli
{
    public static async Task<int> RunAsync(string dataDir, string cmd, string? codesArg, int days, int limit, string? outDir)
    {
        void L(string m) => Console.WriteLine(m);
        var cfg = BrConfig.Load(dataDir);
        L($"[S27] 参数 {cfg.Path}（version {cfg.Version}）");
        var codes = (codesArg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.PadLeft(6, '0')).ToList();
        var three = new List<string> { "600276", "603379", "000682" };
        switch (cmd)
        {
            case "scan":
                var r = await BrScan.RunAsync(cfg, dataDir, L, limit, codes.Count > 0 ? codes : null, outDir);
                L($"RESULT scan_day={r.ScanDay} scored={r.Scored} dropped_incomplete_today_bar={r.DroppedIncompleteToday} " +
                  string.Join(" ", r.RatingCounts.Select(kv => $"{kv.Key}={kv.Value}")) + $" report={r.ReportPath}");
                return 0;
            case "track":   // t40：B1/B2/B3 信号 30 个交易日实盘跟踪（读 signal_history.csv，输出 bottom_reversal/tracking/）
                var tr = await BrTrack.RunAsync(cfg, dataDir, L);
                L($"RESULT track as_of={tr.AsOf} total={tr.Total} tracking={tr.Tracking} expired={tr.Expired} stop_hit={tr.StopHit} failed={tr.Failed} csv={tr.LatestPath} summary={tr.SummaryPath}");
                return tr.Total > 0 && tr.Failed == tr.Total ? 1 : 0;
            case "replay":
                await BrReplay.RunAsync(cfg, dataDir, codes.Count > 0 ? codes : three, days > 0 ? days : 500, L);           // V1 评分复盘
                await BrTradeReplay.RunAsync(cfg, dataDir, codes.Count > 0 ? codes : three, days > 0 ? days : 500, L);      // V1.1 交易阶段/信号复盘
                return 0;
            case "backtest":
                await BrBacktest.RunAsync(cfg, dataDir, codes.Count > 0 ? codes : three, days > 0 ? days : 750, L);         // V1 A/B 回测
                var v11 = Path.Combine(dataDir, BrConfig.V11FileName);   // V1.1 对照组固定用 V1.1 参数文件
                await BrTradeBacktest.RunAsync(File.Exists(v11) ? BrConfig.Load(dataDir, v11) : cfg, dataDir, codes.Count > 0 ? codes : three, days, L);   // V1.1 B1/B2/B3 回测
                return 0;
            case "backtest12":   // V1.2 实验：B3 止损实验组 / B1 过滤分析 / 四组对照（默认全市场抽样，--limit 改样本数，--codes 指定股票）
                await BrBacktest12.RunAsync(cfg, dataDir, codes, days, limit, outDir, L);
                return 0;
            case "diag":   // 逐日打印交易阶段与各阶段条件（排查用）
                foreach (var code in codes)
                {
                    var k = await BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), (days > 0 ? days : 60) + 200, 1, 4);
                    if (k is null) continue;
                    var s0 = BrScan.DropIncomplete(k.S, cfg); var s = s0.Take(s0.Count).Prepare(cfg);
                    for (var i = Math.Max(cfg.I("model.min_bars", 80) - 1, s.Count - (days > 0 ? days : 60)); i < s.Count; i++)
                    {
                        var t = BrTradeEngine.Evaluate(s, i, cfg, code);
                        L($"{code} {t.Date} {t.Stage} {t.SignalType} {t.OpStatus} bottom={t.Conditions["bottom"]?.ToJsonString()} trend={t.Conditions["trend"]?.ToJsonString()}");
                    }
                }
                return 0;
            case "selftest":
                return await BrSelfTest.RunAsync(cfg, codes.Count > 0 ? codes : ["600276"], L) ? 0 : 1;
            default:
                Console.Error.WriteLine($"--br 子命令无效: {cmd}（scan/track/replay/backtest/backtest12/diag/selftest）");
                return 2;
        }
    }
}
