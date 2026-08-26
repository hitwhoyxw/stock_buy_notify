using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ThreeBucket.Core.Services;

/// <summary>腾讯批量行情基本面快照行（60 只/批，无 token、防限频主力源）。</summary>
public sealed record TencentFundamental(
    string Code, string Name,
    double? Price,     // 现价
    double? PeTtm,     // 市盈率 TTM
    double? Pb,        // 市净率
    double? TotalMvYi, // 总市值（亿元）
    double? DvTtm);    // 股息率 TTM %

/// <summary>
/// 腾讯财经批量基本面快照：qt.gtimg.cn/q=sh600519,sz000001（GBK，~ 波浪号分隔）。
/// 单次请求约 60 只，替代 akshare/tushare 的全市场基本面快照（T4/T6 使用）。
/// </summary>
public class TencentSnapshot
{
    private static readonly HttpClient Client = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex LineRegex =
        new(@"v_(?<sym>[a-zA-Z0-9]+)\s*=\s*""(?<data>[^""]*)""\s*;", RegexOptions.Compiled);

    static TencentSnapshot()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Client.DefaultRequestHeaders.UserAgent.TryParseAdd("Mozilla/5.0 (ThreeBucket/1.0)");
    }

    /// <summary>批量拉取（60 只/批，批间 300ms）。失败返回空字典。</summary>
    /// <para>每批 HTTP 失败重试 2 次（间隔 0.8s）：瞬时网络抖动会让整批 60 只
    /// 全部缺失 → 下游 B 桶把这些票计入"基本面缺失"静默剔除。重试能救回这部分。
    /// 解析错误不重试（结构变化需暴露，而非反复撞同一份坏数据）。</para>
    public async Task<Dictionary<string, TencentFundamental>> GetBatchAsync(
        IEnumerable<string> codes, Action<string>? log = null, CancellationToken ct = default)
    {
        var list = codes.Select(c => c.Split('.')[0].Trim().PadLeft(6, '0'))
            .Where(c => c.Length == 6).Distinct().ToList();
        var result = new Dictionary<string, TencentFundamental>(StringComparer.Ordinal);

        for (var i = 0; i < list.Count; i += 60)
        {
            var chunk = list.Skip(i).Take(60).ToList();
            var url = "https://qt.gtimg.cn/q=" + string.Join(",", chunk.Select(Symbol));
            var ok = false;
            // 重试 2 次（共 3 次尝试）：第 1 次正常抓，失败则间隔 0.8s 重抓
            for (var attempt = 0; attempt < 3 && !ok; attempt++)
            {
                try
                {
                    using var resp = await Client.GetAsync(url, ct);
                    resp.EnsureSuccessStatusCode();
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    Parse(Encoding.GetEncoding("GBK").GetString(bytes), result);
                    ok = true;
                }
                catch (Exception e)
                {
                    if (attempt < 2)
                        log?.Invoke($"[Tencent] 行情批次失败(第{attempt + 1}次): {e.Message}，重试…");
                    else
                        log?.Invoke($"[Tencent] 行情批次失败: {e.Message}（已重试 2 次仍失败，跳过该批）");
                    if (attempt < 2) await Task.Delay(800, ct);
                }
            }
            if (i + 60 < list.Count) await Task.Delay(300, ct);
        }
        log?.Invoke($"[Tencent] 批量行情 {result.Count}/{list.Count} 只");
        return result;
    }

    // 6/5/9 开头 sh，4/8 开头 bj，其余 sz（与 KlineService.StockSymbol 同规则）
    private static string Symbol(string code) => code[0] switch
    {
        '6' or '5' or '9' => "sh" + code,
        '4' or '8' => "bj" + code,
        _ => "sz" + code,
    };

    private static void Parse(string raw, Dictionary<string, TencentFundamental> result)
    {
        foreach (Match m in LineRegex.Matches(raw))
        {
            var f = m.Groups["data"].Value.Split('~');
            if (f.Length < 47) continue; // 字段不足（指数/停牌等）跳过
            var code = f[2].Trim();
            if (code.Length != 6) continue;

            // f[3]现价 f[39]PE(TTM) f[45]总市值(亿) f[46]PB f[64]股息率TTM(%)
            result[code] = new TencentFundamental(code, f[1].Trim(),
                Num(f, 3), Num(f, 39), Num(f, 46), Num(f, 45), f.Length > 64 ? Num(f, 64) : null);
        }
    }

    /// <summary>0/空串视为缺失（与 Python _num 口径一致）。</summary>
    private static double? Num(string[] f, int i)
        => i < f.Length && double.TryParse(f[i], NumberStyles.Any, Inv, out var v) && v != 0 ? v : null;
}
