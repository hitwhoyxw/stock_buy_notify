using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.Demo;

/// <summary>
/// 提醒通知离线回归：LarkNotifier 消息组装 / webhook 校验 / 发送失败路径 /
/// DataStore.RecordAlerts 的 fresh（当日去重）语义。
/// 全部不发真实网络请求（无效 URL 与本机拒绝端口）。
/// </summary>
public static class NotifyRegression
{
    public static bool Run()
    {
        Console.WriteLine("=== 提醒通知离线回归（飞书消息组装 / webhook 校验 / 去重语义） ===\n");
        var ok = true;
        ok &= CaseMessageBuild();
        ok &= CaseWebhookValidation();
        ok &= CaseSendFailurePaths();
        ok &= CaseRecordAlertsFresh();
        Console.WriteLine(ok ? "\n通知回归：全部通过 ✅" : "\n通知回归：存在失败 ❌");
        return ok;
    }

    // ── 消息组装 ────────────────────────────────────────────────

    private static bool CaseMessageBuild()
    {
        var alerts = new List<AlertEntry>
        {
            new() { Code = "000001", Name = "平安银行", StrategyId = "S2", StrategyName = "深回撤清仓线",
                Action = "距半年高点回撤超20%，建议清仓止损", Priority = "P2" },
            new() { Code = "600519", Name = "贵州茅台", StrategyId = "S1", StrategyName = "跌破MA60减仓",
                Action = "现价跌破MA60均线，建议减仓1/3观察", Priority = "P0" },
            new() { Code = "300750", Name = "宁德时代", StrategyId = "S7", StrategyName = "MACD金叉放量",
                Action = "MACD金叉且量比≥1.5倍放量", Priority = "P1" },
        };
        var text = LarkNotifier.BuildAlertMessage(alerts);

        if (!text.Contains("【三桶监控】") || !text.Contains("触发 3 条策略提醒"))
        { Console.WriteLine("FAIL N1: 消息缺少标题/数量"); return false; }
        // P0 应排在最前（去重提示条目顺序）
        var i0 = text.IndexOf("600519", StringComparison.Ordinal);
        var i1 = text.IndexOf("300750", StringComparison.Ordinal);
        var i2 = text.IndexOf("000001", StringComparison.Ordinal);
        if (i0 < 0 || i1 < 0 || i2 < 0 || !(i0 < i1 && i1 < i2))
        { Console.WriteLine("FAIL N1: P0/P1/P2 排序错误"); return false; }
        if (!text.Contains("跌破MA60减仓") || !text.Contains("建议清仓止损"))
        { Console.WriteLine("FAIL N1: 策略名/建议文本缺失"); return false; }

        // 超过 20 条截断
        var many = Enumerable.Range(0, 25).Select(i => new AlertEntry
        {
            Code = $"60{i:D4}", StrategyId = $"S{i}", StrategyName = $"策略{i}", Priority = "P1",
        }).ToList();
        var bigText = LarkNotifier.BuildAlertMessage(many);
        if (!bigText.Contains("触发 25 条策略提醒") || !bigText.Contains("另有 5 条略"))
        { Console.WriteLine("FAIL N1: 超限截断提示缺失"); return false; }

        Console.WriteLine("PASS N1: 消息组装（标题/数量/P0 排前/截断）");
        return true;
    }

    // ── webhook 校验 ────────────────────────────────────────────

    private static bool CaseWebhookValidation()
    {
        var valid = new[]
        {
            "https://open.feishu.cn/open-apis/bot/v2/hook/abc123",
            "http://127.0.0.1:8080/hook",
        };
        var invalid = new[] { "", "   ", null, "ftp://x/y", "open.feishu.cn/hook" };
        foreach (var u in valid)
            if (!LarkNotifier.IsValidWebhook(u))
            { Console.WriteLine($"FAIL N2: 应为合法 {u}"); return false; }
        foreach (var u in invalid)
            if (LarkNotifier.IsValidWebhook(u))
            { Console.WriteLine($"FAIL N2: 应为非法 [{u}]"); return false; }
        Console.WriteLine("PASS N2: webhook URL 形态校验");
        return true;
    }

    // ── 发送失败路径（无真实网络）───────────────────────────────

    private static async Task<bool> CaseSendFailurePathsAsync()
    {
        // 无效 URL：不发请求直接失败
        var (ok1, msg1) = await LarkNotifier.SendAsync("not-a-url", "x");
        if (ok1 || !msg1.Contains("无效"))
        { Console.WriteLine($"FAIL N3: 无效URL应失败（{msg1}）"); return false; }

        // 本机拒绝端口：连接立即失败（含签名路径，验证 secret 分支不抛异常）
        var (ok2, msg2) = await LarkNotifier.SendAsync("https://127.0.0.1:1/hook", "x", "secret-x");
        if (ok2)
        { Console.WriteLine("FAIL N3: 拒绝端口不应成功"); return false; }

        Console.WriteLine($"PASS N3: 失败路径（无效URL + 连接拒绝，含签名分支）");
        return true;
    }

    private static bool CaseSendFailurePaths()
        => CaseSendFailurePathsAsync().GetAwaiter().GetResult();

    // ── RecordAlerts fresh 去重语义 ────────────────────────────

    private static bool CaseRecordAlertsFresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tb_notify_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new DataStore(dir);
            var a1 = new AlertEntry { Code = "600519", StrategyId = "S1", StrategyName = "跌破MA60减仓", Priority = "P0" };
            var a2 = new AlertEntry { Code = "000001", StrategyId = "S2", StrategyName = "深回撤清仓线", Priority = "P0" };

            var fresh1 = store.RecordAlerts(new List<AlertEntry> { a1, a2 });
            if (fresh1.Count != 2)
            { Console.WriteLine($"FAIL N4: 首次应返回 2 条 fresh（实际 {fresh1.Count}）"); return false; }

            // 同日重复：0 条 fresh（外部通知通道不应再推）
            var fresh2 = store.RecordAlerts(new List<AlertEntry> { a1, a2 });
            if (fresh2.Count != 0)
            { Console.WriteLine($"FAIL N4: 同日重复应返回 0 条 fresh（实际 {fresh2.Count}）"); return false; }

            // 换一条策略 → 1 条新 fresh
            var a3 = new AlertEntry { Code = "600519", StrategyId = "S11", StrategyName = "MACD顶背离", Priority = "P1" };
            var fresh3 = store.RecordAlerts(new List<AlertEntry> { a1, a3 });
            if (fresh3.Count != 1 || fresh3[0].StrategyId != "S11")
            { Console.WriteLine($"FAIL N4: 新策略应返回 1 条 fresh（实际 {fresh3.Count}）"); return false; }

            // 历史可回读（Priority 字段随 JSON 持久化）
            var history = store.LoadHistory();
            if (history.Count != 3 || history.Any(h => h.Priority != "P0" && h.Priority != "P1"))
            { Console.WriteLine("FAIL N4: 历史回读/Priority 持久化异常"); return false; }

            Console.WriteLine("PASS N4: RecordAlerts fresh 语义（首次全推/同日去重/新策略新推）");
            return true;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录清理失败忽略 */ }
        }
    }
}

/// <summary>toast 诊断：直接弹一条 Windows 系统通知（验证 toast 通道端到端可用，同 UI 监控触发同源）。</summary>
public static class ToastDiagnostic
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            Console.WriteLine("当前平台不支持 Windows toast（仅 Win10/11）");
            return;
        }
        Console.WriteLine("弹出测试 toast（应出现在屏幕右下角/通知中心）…");
        WindowsToastNotifier.Show("🎯 三桶监控 · toast 通道自检",
            "如果你看到这条系统通知，说明 Windows 通知通道工作正常。");
        Console.WriteLine("已发送（若未见弹窗：系统设置 → 通知 → 允许本应用 / 检查勿扰模式与专注助手）");
    }
}
