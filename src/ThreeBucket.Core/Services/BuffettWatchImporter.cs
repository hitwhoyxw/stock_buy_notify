using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Services;

/// <summary>巴菲特观察条目一条（从 skill_output 的 BUFFETT_WATCH 块解析而来）。</summary>
public sealed class BuffettWatchItem
{
    public string Code { get; set; } = "";
    public string Stock { get; set; } = "";
    public string Indicator { get; set; } = "";
    public string Operator { get; set; } = "";
    public double Threshold { get; set; }
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ValidUntil { get; set; } = "";

    /// <summary>code 被 stock 名称反查纠正时的原错误代码（null=未纠正），弹窗展示用。</summary>
    public string? CorrectedFrom { get; set; }

    /// <summary>阈值/依据拼进一行，供弹窗列表与确认消息展示。</summary>
    public string Summary =>
        $"{IndicatorLabel} {Operator}{Threshold:0.###}" +
        (string.IsNullOrWhiteSpace(Reason) ? "" : $" ｜ {Reason}");
    public string IndicatorLabel => Indicator switch
    {
        "pe_ttm" => "PE(TTM)",
        "price" => "现价",
        "drawdown_from_high_180d" => "回撤",
        "gain_from_low_180d" => "低点涨幅",
        "day_change_pct" => "当日涨跌",
        _ => Indicator,
    };
}

/// <summary>
/// 「巴菲特观察」块解析 + 导入：把 LLM 在 skill_output 末尾输出的
/// <c>=== BUFFETT_WATCH v1 ===</c> 块导入监控体系（strategies.csv + watchlist.csv）。
/// 每条 = 一条独立策略（名字 [巴] 前缀）+ 自选绑定，触发即每日提醒（RecordAlerts 当日去重）。
/// </summary>
public static class BuffettWatchImporter
{
    /// <summary>引擎支持且适合表达"价格到位"的指标白名单（LegacyMap 别名，直接入 strategies.csv）。</summary>
    public static readonly string[] AllowedIndicators =
        ["pe_ttm", "price", "drawdown_from_high_180d", "gain_from_low_180d", "day_change_pct"];
    private static readonly string[] Ops = ["<", "<=", ">", ">="];

    /// <summary>
    /// 从 skill_output 文本解析观察块。宽松读取：只认 === 标记行之间的条目行，
    /// 字段缺失/指标不在白名单/操作符非法/符号错误的行跳过（返回 discarded 供 UI 提示）。
    /// <para><paramref name="codeByName"/>：候选池 name→code 反查表（来自 skill_input）。
    /// LLM 偶发抄错代码（股名/理由是对的、代码抄成别的票，实测 002050 三花智控被写成
    /// 002001 新和成）：stock 名在表中查得到且 code 不一致时，以名称为准纠正 code，
    /// 原错误代码记入 <see cref="BuffettWatchItem.CorrectedFrom"/> 供弹窗展示。
    /// 名称查不到的行不动（由调用方行情层终验）。</para>
    /// </summary>
    public static (List<BuffettWatchItem> Items, List<string> Discarded) Parse(
        string? text, IReadOnlyDictionary<string, string>? codeByName = null)
    {
        var items = new List<BuffettWatchItem>();
        var discarded = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return (items, discarded);

        var inBlock = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!inBlock)
            {
                if (line.StartsWith("=== BUFFETT_WATCH", StringComparison.Ordinal)) inBlock = true;
                continue;
            }
            if (line.StartsWith("=== END", StringComparison.Ordinal)) break;
            if (line.Length == 0) continue;

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var seg in line.Split('|'))
            {
                var i = seg.IndexOf('=');
                if (i <= 0) continue;
                fields[seg[..i].Trim()] = seg[(i + 1)..].Trim();
            }
            if (!fields.TryGetValue("code", out var code) || code.Length != 6 || !code.All(char.IsDigit))
            {
                discarded.Add(line);
                continue;
            }

            var indicator = fields.GetValueOrDefault("indicator", "");
            var op = fields.GetValueOrDefault("operator", "");
            if (!AllowedIndicators.Contains(indicator) || !Ops.Contains(op)
                || !double.TryParse(fields.GetValueOrDefault("threshold", ""), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var th))
            {
                discarded.Add(line);
                continue;
            }

            // 符号错误拦截：drawdown 值域为负，LLM 若给出正阈值+向下比较必然语义错误
            if (indicator == "drawdown_from_high_180d" && op is "<" or "<=" && th > 0)
            {
                discarded.Add(line);
                continue;
            }

            var item = new BuffettWatchItem
            {
                Code = code,
                Stock = fields.GetValueOrDefault("stock", ""),
                Indicator = indicator,
                Operator = op,
                Threshold = th,
                Name = NormalizeName(fields.GetValueOrDefault("name", ""), code, indicator, op, th),
                Reason = fields.GetValueOrDefault("reason", ""),
                ValidUntil = fields.GetValueOrDefault("valid_until", ""),
            };

            // code↔stock 交叉校验：候选池反查表里查得到该股名且代码对不上 → 名称为准纠正
            // （股名/理由/阈值都是 LLM 按真实候选行写的，只有 code 手滑抄错）
            if (item.Stock.Length > 0 && codeByName is not null
                && codeByName.TryGetValue(item.Stock, out var right) && right != code)
            {
                item.CorrectedFrom = item.Code;
                item.Code = right;
                item.Name = NormalizeName(
                    fields.GetValueOrDefault("name", "").Replace(code, right), right, indicator, op, th);
            }

            items.Add(item);
        }
        return (items, discarded);
    }

    /// <summary>LLM 漏写 [巴] 前缀时补上；name 缺失时按指标拼一个。</summary>
    private static string NormalizeName(string name, string code, string indicator, string op, double th)
    {
        var label = indicator switch
        {
            "pe_ttm" => $"PE{op}{th:0.##}",
            "price" => $"价{op}{th:0.##}",
            "drawdown_from_high_180d" => $"回撤{op}{th:0.##}%",
            "gain_from_low_180d" => $"低点涨幅{op}{th:0.##}%",
            _ => $"{indicator}{op}{th:0.##}",
        };
        var baseName = name.Length > 0 ? name : $"{code} {label}";
        return baseName.StartsWith("[巴]", StringComparison.Ordinal) ? baseName : "[巴] " + baseName.TrimStart();
    }

    /// <summary>
    /// 行情层终验：批量拉实时行情，code 不在行情源（无效代码）或行情官方名与条目 stock
    /// 不一致的条目剔除。严格比对（剥 ST/*ST 后要求官方名含股名）——LLM 的股名抄自
    /// skill_input 候选行，官方名应完全一致；模糊前缀匹配会把"东方通信↔东方财富"
    /// 这类近名误放行。兜底意义：候选池反查表救不了"池外股票名 + 编造代码"的组合。
    /// 行情拉取失败时保守放行（不因网络抖动把好条目全拒了），rejected 文案供弹窗提示。
    /// </summary>
    public static async Task<(List<BuffettWatchItem> Kept, List<string> Rejected)> VerifyByQuotesAsync(
        Func<IEnumerable<string>, Task<Dictionary<string, QuoteInfo>>> fetchQuotes,
        IReadOnlyList<BuffettWatchItem> items)
    {
        var kept = new List<BuffettWatchItem>();
        var rejected = new List<string>();
        if (items.Count == 0) return (kept, rejected);

        Dictionary<string, QuoteInfo> map;
        try { map = await fetchQuotes(items.Select(x => x.Code)); }
        catch { return (items.ToList(), rejected); } // 行情失败：保守放行，不在导入链上放大故障
        if (map.Count == 0) return (items.ToList(), rejected);

        foreach (var it in items)
        {
            if (!map.TryGetValue(it.Code, out var q) || q.Price <= 0)
            {
                rejected.Add($"{it.Code} {it.Stock}（行情源无此代码）");
                continue;
            }
            if (it.Stock.Length >= 2 && !OfficialNameMatches(q.Name, it.Stock))
            {
                rejected.Add($"{it.Code} 行情官方名「{q.Name}」≠「{it.Stock}」，疑 LLM 张冠李戴，已丢弃");
                continue;
            }
            kept.Add(it);
        }
        return (kept, rejected);
    }

    /// <summary>官方名剥 ST/*ST 后应包含股名（股名抄自输入行，官方名应严格一致或仅带后缀）。</summary>
    private static bool OfficialNameMatches(string official, string stock)
    {
        var core = official.Replace("*", "").Replace("ST", "").Replace("st", "").Trim();
        return core.Contains(stock);
    }

    /// <summary>
    /// 批量导入：每条 → 一条策略（[巴] 前缀、P1、关注买入、enabled=1）写入 strategies.csv，
    /// code 去重；再把全部策略 id 绑定到对应自选（无则 AddWatch）。reason+有效期写进自选备注。
    /// 已有同名策略的条目跳过（重复导入不产生副本）。返回每条的落库结果文案。
    /// </summary>
    public static List<string> Import(DataStore store, IEnumerable<BuffettWatchItem> items)
    {
        var results = new List<string>();
        foreach (var it in items)
        {
            // 幂等：同名策略已存在即跳过（LLM 重跑不产生重复策略）
            if (store.ListStrategies().Any(s => s.Name == it.Name))
            {
                results.Add($"○ {it.Code} {it.Stock} 已存在：{it.Name}");
                results.Add("  （如需更新阈值，先在策略管理删除旧策略再导入）");
                continue;
            }
            var s = new Strategy
            {
                Name = it.Name,
                Type = "buy",
                Indicator = it.Indicator,
                Operator = it.Operator,
                Threshold = it.Threshold.ToString("0.###", CultureInfo.InvariantCulture),
                Condition = "",
                Action = "关注买入",
                Priority = "P1",
                Enabled = true,
            };
            store.AddStrategy(s); // Id 为空时自动分配 S{n+1}

            // 自选：已在池中只补绑定；不在则新增（备注=reason+有效期，监控页可见）
            var existing = store.ListWatchlist().FirstOrDefault(w => DataStore.NormalizeCode(w.Code) == it.Code);
            var noteBits = new List<string>();
            if (!string.IsNullOrWhiteSpace(it.Reason)) noteBits.Add(it.Reason);
            if (!string.IsNullOrWhiteSpace(it.ValidUntil)) noteBits.Add($"有效期至 {it.ValidUntil}");
            var note = string.Join("；", noteBits);
            if (existing is null)
            {
                store.AddWatch(it.Code, it.Stock, from: "buffett", note: note);
            }
            else if (note.Length > 0)
            {
                var merged = string.IsNullOrWhiteSpace(existing.Note) ? note : $"{existing.Note}；{note}";
                store.SetNote(it.Code, merged);
            }
            var ids = existing?.Strategies.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList()
                      ?? new List<string>();
            if (!ids.Contains(s.Id!)) ids.Add(s.Id!);
            store.SetStrategies(it.Code, ids);
            results.Add($"✓ {it.Code} {it.Stock} → {s.Id} {it.Name}"
                        + (string.IsNullOrWhiteSpace(it.ValidUntil) ? "" : $"（有效期至 {it.ValidUntil}）"));
        }
        return results;
    }
}
