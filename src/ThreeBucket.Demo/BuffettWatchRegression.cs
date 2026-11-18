using ThreeBucket.Core.Data;
using ThreeBucket.Core.Services;

namespace ThreeBucket.Demo;

/// <summary>
/// 巴菲特观察离线回归：BUFFETT_WATCH 块解析（白名单/符号拦截/字段缺失跳过）、
/// [巴] 名字归一、导入幂等（同名策略不重复）、自选新增与策略绑定落库。
/// 全部走临时目录 DataStore，不碰真实 data/。
/// </summary>
public static class BuffettWatchRegression
{
    public static bool Run()
    {
        Console.WriteLine("=== 巴菲特观察离线回归（块解析 / 符号拦截 / 导入幂等） ===\n");
        var ok = true;
        ok &= CaseParse();
        ok &= CaseImport();
        Console.WriteLine(ok ? "\n巴菲特观察回归：全部通过 ✅" : "\n巴菲特观察回归：存在失败 ❌");
        return ok;
    }

    private const string Sample = """
        # B 桶 · 成长候选池 · 2026-09-03

        ## Top 10 推荐
        （正文省略）

        ## 备注
        - 排序依据：1/PEG

        === BUFFETT_WATCH v1 ===
        code=688008 | stock=澜起科技 | indicator=pe_ttm | operator=< | threshold=55 | name=[巴] 澜起科技 PE≤55 | reason=内存接口双寡头当前PE75.5高于历史中枢 | valid_until=2027-03-31
        code=002001 | stock=三花智控 | indicator=pe_ttm | operator=< | threshold=30 | name=[巴] 三花智控 PE≤30 | reason=热管理龙头PEG2.33偏贵，PE38.3回落至30以下触发 | valid_until=2027-03-31
        code=600660 | stock=福耀玻璃 | indicator=drawdown_from_high_180d | operator=< | threshold=-20 | name=[巴] 福耀玻璃 回撤超20% | reason=全球汽玻垄断周期下行 | valid_until=2027-03-31
        code=600660 | stock=福耀玻璃 | indicator=drawdown_from_high_180d | operator=< | threshold=20 | name=[巴] 符号错误行 | reason=回撤写正值应被丢弃
        code=0000001 | stock=长度错误 | indicator=pe_ttm | operator=< | threshold=30 | name=[巴] 长度错误 | reason=代码非6位
        code=600036 | indicator=dividend_yield | operator=> | threshold=5 | name=[巴] 不在白名单 | reason=指标不支持
        === END BUFFETT_WATCH ===
        尾部杂讯行不应被读取
        """;

    private static bool CaseParse()
    {
        // 反查表：模拟 skill_input 里的真实映射（002001=新和成 / 002050=三花智控）
        var byName = new Dictionary<string, string>
        {
            ["三花智控"] = "002050",
            ["新和成"] = "002001",
            ["澜起科技"] = "688008",
            ["福耀玻璃"] = "600660",
        };
        var (items, discarded) = BuffettWatchImporter.Parse(Sample, byName);
        var pass = items.Count == 3
            && items[1].CorrectedFrom == "002001" && items[1].Code == "002050"
            && items[1].Stock == "三花智控" && items[1].Threshold == 30
            && items[1].Name == "[巴] 三花智控 PE≤30" // name 里的错误代码同步替换
            && items[0].Code == "688008" && items[2].Code == "600660" && items[2].Threshold == -20
            && discarded.Count == 3; // 符号错误 + 代码长度 + 白名单外
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} W1: 块解析（{items.Count} 条有效 / {discarded.Count} 行丢弃；抄错代码按股名纠正 002001→002050）");
        return pass;
    }

    private static bool CaseImport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "buffett_watch_reg_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new DataStore(dir);
            var byName = new Dictionary<string, string> { ["三花智控"] = "002050", ["澜起科技"] = "688008", ["福耀玻璃"] = "600660" };
            var (items, _) = BuffettWatchImporter.Parse(Sample, byName);
            // 只导两条（澜起 + 三花），三花验证纠正后的 002050 落库

            var r1 = BuffettWatchImporter.Import(store, items);
            var r2 = BuffettWatchImporter.Import(store, items); // 重复导入：幂等
            var strats = store.ListStrategies();
            var ba = strats.Where(s => s.Name.StartsWith("[巴]", StringComparison.Ordinal)).ToList();
            var watch = store.ListWatchlist();

            var pass = ba.Count == 3
                && ba.All(s => s.Type == "buy" && s.Priority == "P1" && s.Enabled && s.Action == "关注买入")
                && watch.Count == 3
                && watch.Any(w => w.Code == "002050" && w.Name == "三花智控") // 纠正后的代码落库
                && watch.Any(w => w.Code == "688008")
                && watch.All(w => w.Strategies.Split(';', StringSplitOptions.RemoveEmptyEntries).Length == 1)
                && watch.First(w => w.Code == "600660").Note.Contains("全球汽玻垄断")
                && r1.Count(x => x.StartsWith('✓')) == 3
                && r2.Count(x => x.StartsWith('○')) == 3; // 第二轮全部命中"已存在"
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} W2: 导入落库（策略 {ba.Count} 条 / 自选 {watch.Count} 只，三花纠正为 002050）+ 幂等");
            return pass;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
        }
    }
}
