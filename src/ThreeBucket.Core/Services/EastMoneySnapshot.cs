using System.Globalization;
using System.Text.Json;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 东方财富全市场行情快照（82.push2.eastmoney.com/api/qt/clist/get）。
/// 腾讯批量行情（<see cref="TencentSnapshot"/>）覆盖率不足时的二级降级源——
/// 一次调用拉全市场 A 股（~5500 只），返回 PE/总市值/市净率/现价，能补腾讯缺失的字段。
/// 新浪 hq.sinajs.cn/list= 不含 PE/总市值，做不了这个降级，故选东财 clist。
/// </summary>
public class EastMoneySnapshot
{
    private static readonly HttpClient Client = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static EastMoneySnapshot()
    {
        Client.DefaultRequestHeaders.UserAgent.TryParseAdd("Mozilla/5.0 (ThreeBucket/1.0)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
    }

    /// <summary>东财全市场快照行（与 <see cref="TencentFundamental"/> 同口径）。</summary>
    public sealed record EmFundamental(
        string Code, double? Price, double? PeTtm, double? Pb, double? TotalMvYi);

    // 字段：f2=最新价 f9=市盈率(动态) f23=市净率 f20=总市值(元) f12=代码 f14=名称
    // fs=m:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23 为沪深 A 股全集（与 akshare stock_zh_a_spot_em 同）
    private const string ClistUrl =
        "https://82.push2.eastmoney.com/api/qt/clist/get" +
        "?pn={0}&pz=100&po=1&np=1&ut=bd1d9ddbbe4039d69c52e6a8e8f8f8f8" +
        "&fltt=2&invt=2&fid=f3&fs=m:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23" +
        "&fields=f2,f9,f12,f14,f20,f23";

    /// <summary>
    /// 拉取全市场 A 股快照，返回 code → EmFundamental 字典。失败返回空字典（不抛异常）。
    /// <para>分页 pz=100 拉全市场（~55 页），逐页累加。中途某页失败仅丢该页、不中断。</para>
    /// </summary>
    public async Task<Dictionary<string, EmFundamental>> GetMarketSnapshotAsync(
        Action<string>? log = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, EmFundamental>(StringComparer.Ordinal);
        var page = 1;
        try
        {
            while (true)
            {
                using var resp = await Client.GetAsync(string.Format(Inv, ClistUrl, page), ct);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) break;

                var total = data.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32() : 0;
                if (data.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in diff.EnumerateArray())
                    {
                        var code = Str(item, "f12").PadLeft(6, '0');
                        if (code.Length != 6) continue;
                        // 已有则不覆盖（避免后面页重复行；通常 clist 不重复，兜底）
                        if (result.ContainsKey(code)) continue;
                        result[code] = new EmFundamental(
                            code,
                            Num(item, "f2"),   // 最新价
                            Num(item, "f9"),   // 市盈率(动态)
                            Num(item, "f23"),  // 市净率
                            // 总市值 f20 单位=元 → 亿元（÷1e8，与腾讯 TotalMvYi 同口径）
                            Num(item, "f20") is { } mv ? mv / 1e8 : null);
                    }
                }
                // pz=100 → 总页数 = ceil(total/100)；total=0 或已拉完则停
                var pages = (int)Math.Ceiling(total / 100.0);
                if (pages <= 0 || page >= pages) break;
                page++;
                await Task.Delay(150, ct); // 分页间轻休眠防限频
            }
            log?.Invoke($"[EM] 全市场快照 {result.Count} 只（{page} 页）");
        }
        catch (Exception e)
        {
            log?.Invoke($"[EM] 全市场快照失败: {e.Message}（已取 {result.Count} 只）");
        }
        return result;
    }

    private static string Str(JsonElement item, string name)
        => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement item, string name)
        => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;
}
