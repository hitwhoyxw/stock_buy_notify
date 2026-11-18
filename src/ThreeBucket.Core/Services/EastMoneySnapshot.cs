using System.Globalization;
using System.Text.Json;

namespace ThreeBucket.Core.Services;

/// <summary>
/// 东方财富全市场行情快照（82.push2.eastmoney.com/api/qt/clist/get）。
/// 腾讯批量行情（<see cref="TencentSnapshot"/>）覆盖率不足时的二级降级源——
/// 一次调用拉全市场 A 股（~5500 只），返回 PE/总市值/市净率/现价，能补腾讯缺失的字段。
/// 新浪 hq.sinajs.cn/list= 不含 PE/总市值，做不了这个降级，故选东财 clist。
/// <para>传入 cacheDir 时，全市场列表落盘缓存（默认 TTL 7 天；股票池不必日更）。</para>
/// </summary>
public class EastMoneySnapshot
{
    private static readonly HttpClient Client = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>全市场代码列表缓存有效期（默认一周）。</summary>
    public TimeSpan ListCacheTtl { get; set; } = TimeSpan.FromDays(7);

    private readonly string? _cacheDir;

    static EastMoneySnapshot()
    {
        Client.DefaultRequestHeaders.UserAgent.TryParseAdd("Mozilla/5.0 (ThreeBucket/1.0)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
    }

    public EastMoneySnapshot(string? cacheDir = null)
    {
        _cacheDir = string.IsNullOrWhiteSpace(cacheDir) ? null : cacheDir;
        if (_cacheDir is not null)
        {
            try { Directory.CreateDirectory(_cacheDir); } catch { /* 由调用方保证可写 */ }
        }
    }

    /// <summary>东财全市场快照行（与 <see cref="TencentFundamental"/> 同口径）。</summary>
    public sealed record EmFundamental(
        string Code, string Name, double? Price, double? PeTtm, double? Pb, double? TotalMvYi);

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
    /// <para>若构造时给了 cacheDir，命中未过期缓存则直接返回（默认 7 天）。</para>
    /// </summary>
    public async Task<Dictionary<string, EmFundamental>> GetMarketSnapshotAsync(
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (TryReadListCache(out var cached) && cached!.Count > 0)
        {
            log?.Invoke($"[EM] 全市场快照缓存命中 {cached.Count} 只（TTL {ListCacheTtl.TotalDays:0} 天）");
            return cached;
        }

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
                        if (result.ContainsKey(code)) continue;
                        result[code] = new EmFundamental(
                            code,
                            Str(item, "f14"),  // 名称（剔 ST）
                            Num(item, "f2"),   // 最新价
                            Num(item, "f9"),   // 市盈率(动态)
                            Num(item, "f23"),  // 市净率
                            Num(item, "f20") is { } mv ? mv / 1e8 : null);
                    }
                }
                var pages = (int)Math.Ceiling(total / 100.0);
                if (pages <= 0 || page >= pages) break;
                page++;
                await Task.Delay(150, ct);
            }
            log?.Invoke($"[EM] 全市场快照 {result.Count} 只（{page} 页）");
            TryWriteListCache(result, log);
        }
        catch (Exception e)
        {
            log?.Invoke($"[EM] 全市场快照失败: {e.Message}（已取 {result.Count} 只）");
        }
        return result;
    }

    private string? CachePath =>
        _cacheDir is null ? null : Path.Combine(_cacheDir, "em_clist_a_share.json");

    private bool TryReadListCache(out Dictionary<string, EmFundamental>? map)
    {
        map = null;
        var path = CachePath;
        if (path is null || !File.Exists(path)) return false;
        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > ListCacheTtl) return false;
            var rows = JsonSerializer.Deserialize<List<CacheRow>>(File.ReadAllText(path), JsonOpts);
            if (rows is null || rows.Count == 0) return false;
            map = new Dictionary<string, EmFundamental>(rows.Count, StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Code)) continue;
                var code = r.Code.PadLeft(6, '0');
                map[code] = new EmFundamental(code, r.Name ?? "", r.Price, r.PeTtm, r.Pb, r.TotalMvYi);
            }
            return map.Count > 0;
        }
        catch { return false; }
    }

    private void TryWriteListCache(Dictionary<string, EmFundamental> map, Action<string>? log)
    {
        var path = CachePath;
        if (path is null || map.Count == 0) return;
        try
        {
            var rows = map.Values
                .Select(v => new CacheRow(v.Code, v.Name, v.Price, v.PeTtm, v.Pb, v.TotalMvYi))
                .ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(rows));
            log?.Invoke($"[EM] 全市场快照已缓存 → {Path.GetFileName(path)}（{rows.Count} 只，TTL {ListCacheTtl.TotalDays:0} 天）");
        }
        catch (Exception e)
        {
            log?.Invoke($"[EM] 全市场快照缓存写入失败: {e.Message}");
        }
    }

    private sealed record CacheRow(
        string Code, string? Name, double? Price, double? PeTtm, double? Pb, double? TotalMvYi);

    private static string Str(JsonElement item, string name)
        => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement item, string name)
        => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;
}