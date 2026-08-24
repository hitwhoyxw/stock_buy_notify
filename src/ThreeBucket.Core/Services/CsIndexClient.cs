using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDataReader;

namespace ThreeBucket.Core.Services;

/// <summary>中证指数成分股行。</summary>
public sealed record ConsRow(string Code, string Name);

/// <summary>中证指数估值指标行（近 20 个交易日，官网 indicator 文件口径）。</summary>
public sealed record IndicatorRow(DateTime Date, double Pe1, double Pe2, double Dp1, double Dp2);

/// <summary>
/// 中证指数官网客户端：成分股（cons.xls）/ 估值股息率（indicator.xls）。
/// 替代 akshare index_stock_cons_csindex / stock_zh_index_value_csindex（同一文件源）。
/// 官网只保留最近约 20 条 indicator 记录——与 Python 版行为一致，分位按可得样本计算。
/// </summary>
public class CsIndexClient
{
    private static readonly HttpClient Client = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _cacheDir;

    static CsIndexClient()
    {
        Client.DefaultRequestHeaders.UserAgent.TryParseAdd("Mozilla/5.0 (ThreeBucket/1.0)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://www.csindex.com.cn/");
        // xls（BIFF）内部使用 GBK 系编码，.NET Core 需显式注册
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public CsIndexClient(string? cacheDir = null)
    {
        _cacheDir = cacheDir ?? Path.Combine("data", "cache");
        try { Directory.CreateDirectory(_cacheDir); } catch { }
    }

    /// <summary>指数成分股（000922 中证红利 / 000852 中证1000 / 000905 中证500 / 000510 A500 / 000906 中证800）。失败返回空表。</summary>
    public async Task<List<ConsRow>> GetConstituentsAsync(string indexCode, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(_cacheDir, $"csi_cons_{indexCode}.json");
        if (File.Exists(cacheFile) && DateTime.Now - File.GetLastWriteTime(cacheFile) < TimeSpan.FromHours(24))
        {
            try
            {
                if (JsonSerializer.Deserialize<List<ConsRow>>(File.ReadAllText(cacheFile)) is { Count: > 0 } hit)
                    return hit;
            }
            catch { }
        }

        var bytes = await DownloadAsync(
            $"https://oss-ch.csindex.com.cn/static/html/csindex/public/uploads/file/autofile/cons/{indexCode}cons.xls", ct);
        var rows = ParseCons(bytes);

        try { File.WriteAllText(cacheFile, JsonSerializer.Serialize(rows)); } catch { }
        return rows;
    }

    /// <summary>指数估值指标（市盈率/股息率，近约 20 个交易日）。Dp1=总股本加权股息率%。</summary>
    public async Task<List<IndicatorRow>> GetIndicatorAsync(string indexCode, CancellationToken ct = default)
    {
        var bytes = await DownloadAsync(
            $"https://oss-ch.csindex.com.cn/static/html/csindex/public/uploads/file/autofile/indicator/{indexCode}indicator.xls", ct);
        return ParseIndicator(bytes);
    }

    /// <summary>当前股息率在近 N 年（样本受限时为全部可得样本）历史中的分位。返回 (current, percentile) 或 null。</summary>
    public async Task<(double Current, double Percentile)?> GetDividendYieldPercentileAsync(
        string indexCode = "000922", int minSamples = 10, CancellationToken ct = default)
    {
        var hist = await GetIndicatorAsync(indexCode, ct);
        if (hist.Count < minSamples) return null;

        var current = hist[^1].Dp1;
        if (current <= 0) return null;
        // Python 口径：≤ 当前值的样本占比（0-100）
        var pct = 100.0 * hist.Count(h => h.Dp1 <= current) / hist.Count;
        return (current, pct);
    }

    // ── xls 解析 ───────────────────────────────────────────────────
    // cons.xls 列：0日期 1指数代码 2指数名称 3指数英文名 4成分券代码 5成分券名称 6英文 7交易所 8交易所英文
    private static List<ConsRow> ParseCons(byte[] xls)
    {
        var rows = new List<ConsRow>();
        using var reader = ExcelReaderFactory.CreateReader(new MemoryStream(xls));
        var table = LoadTable(reader);

        for (var i = 1; i < table.Rows.Count; i++) // 第 0 行为表头
        {
            var code = CellStr(table.Rows[i][4]).Trim();
            var name = CellStr(table.Rows[i][5]).Trim();
            if (code.Length == 0 || name.Length == 0) continue;
            rows.Add(new ConsRow(code.PadLeft(6, '0'), name)); // 代码列为数字，需补前导零
        }
        return rows;
    }

    // indicator.xls 列：0日期(20260804) 1指数代码 2名称 3全称 4英文全称 5英文简称 6市盈率1 7市盈率2 8股息率1 9股息率2
    private static List<IndicatorRow> ParseIndicator(byte[] xls)
    {
        var rows = new List<IndicatorRow>();
        using var reader = ExcelReaderFactory.CreateReader(new MemoryStream(xls));
        var table = LoadTable(reader);

        for (var i = 1; i < table.Rows.Count; i++)
        {
            var dateRaw = CellStr(table.Rows[i][0]).Trim();
            if (!DateTime.TryParseExact(dateRaw, "yyyyMMdd", Inv,
                    DateTimeStyles.None, out var d)) continue;
            rows.Add(new IndicatorRow(d,
                CellNum(table.Rows[i][6]), CellNum(table.Rows[i][7]),
                CellNum(table.Rows[i][8]), CellNum(table.Rows[i][9])));
        }
        rows.Sort((a, b) => a.Date.CompareTo(b.Date));
        return rows;
    }

    // BIFF xls → DataTable（无表头模式；第 0 行是表头，由调用方跳过）
    private static System.Data.DataTable LoadTable(IExcelDataReader reader)
        => reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false },
        }).Tables[0];

    private static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var resp = await Client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private static string CellStr(object? cell) => cell?.ToString() ?? "";

    private static double CellNum(object? cell)
        => double.TryParse(CellStr(cell), NumberStyles.Any, Inv, out var v) ? v : 0;
}
