using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ThreeBucket.Core.Services.BottomReversal;

namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// 数据采集：东财免费接口为主（全市场列表、日K 成交额、主要财务指标含银行专项、三大报表、分红、国债收益率、公告关键词），
/// 可选读取 data/quality_value/supplement/ 下由 scripts/tushare_supplement 写出的 CSV（审计/质押/回购/银行补充字段）。
/// 不读、不打印任何 token。
/// </summary>
public sealed partial class QvData
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HttpClient Http = Create();
    private static readonly JsonSerializerOptions Jo = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static HttpClient Create()
    {
        var h = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = 16 })
        { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ThreeBucket/1.0");
        h.DefaultRequestHeaders.Referrer = new Uri("https://data.eastmoney.com/");
        return h;
    }

    private readonly string _dataDir, _cacheDir, _outDir, _supDir;
    private readonly EastMoneyClient _em;
    private readonly Action<string> _log;
    public Dictionary<string, QvManualTag> Tags { get; private set; } = new();
    public HashSet<string> Holdings { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, (string Type, string Note)> Overrides { get; private set; } = new();
    public Dictionary<string, (string Type, string Note)> Mapping { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, QvSupplementBank> BankSup { get; private set; } = new();
    public Dictionary<string, List<(int Year, string Opinion)>> Audits { get; private set; } = new();
    public Dictionary<string, double> PledgeCtrl { get; private set; } = new(); // 控股股东质押比例 0-1
    public Dictionary<string, QvBankCapitalReq> BankCapital { get; private set; } = new(StringComparer.Ordinal);

    public Dictionary<string, Dictionary<int, double>> Buybacks { get; private set; } = new(); // code -> year -> 金额
    public double? RfPct { get; private set; }
    public string RfSource { get; private set; } = "";

    public QvData(string dataDir, EastMoneyClient em, Action<string> log)
    {
        _dataDir = dataDir;
        _cacheDir = Path.Combine(dataDir, "cache", "quality_value");
        _outDir = Path.Combine(dataDir, "quality_value");
        _supDir = Path.Combine(_outDir, "supplement");
        _em = em;
        _log = log;
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_outDir);
        Directory.CreateDirectory(_supDir);
    }

    public string OutDir => _outDir;

    public void L(string m) => _log(m);

    // ───────── 模板 / 手工表 ─────────

    public void LoadSidecars()
    {
        Overrides = ReadOverride(Path.Combine(_outDir, "override.csv"));
        Mapping = ReadMapping(Path.Combine(_outDir, "mapping.csv"));
        Tags = ReadTags(Path.Combine(_outDir, "manual_tags.csv"));
        Holdings = new HashSet<string>(ReadSimple(Path.Combine(_outDir, "holdings.csv"), "code").Select(c => c.PadLeft(6, '0')), StringComparer.Ordinal);
        LoadBankCapital();
        LoadSupplement();
        L($"[S29] 映射 {Mapping.Count} 条 · override {Overrides.Count} · 人工标签 {Tags.Count} · 持仓 {Holdings.Count} · 补充银行 {BankSup.Count} · 审计 {Audits.Count} · 质押 {PledgeCtrl.Count} · 回购 {Buybacks.Count} · 银行监管资本 {BankCapital.Count}");
    }

    private void LoadSupplement()
    {
        BankSup.Clear(); Audits.Clear(); PledgeCtrl.Clear(); Buybacks.Clear();
        var bank = Path.Combine(_supDir, "bank_metrics.csv");
        if (File.Exists(bank))
            foreach (var r in ReadCsv(bank))
            {
                var code = G(r, "code").PadLeft(6, '0');
                var rec = new QvSupplementBank
                {
                    Code = code, Period = G(r, "period"),
                    Npl = N(r, "npl"), ProvisionCov = N(r, "provision_cov"), Cet1 = N(r, "cet1"),
                    Rwa = N(r, "rwa"), SpecialMention = N(r, "special_mention"),
                    ReLoanPct = N(r, "re_loan_pct"), PlatformLoanPct = N(r, "platform_loan_pct"),
                    NplFormation = N(r, "npl_formation"),
                };
                BankSup[code + "|" + rec.Period] = rec;
                if (!BankSup.ContainsKey(code)) BankSup[code] = rec; // 最新一份（文件应倒序或我们取最后覆盖）
            }
        var audit = Path.Combine(_supDir, "fina_audit.csv");
        if (File.Exists(audit))
            foreach (var r in ReadCsv(audit))
            {
                var code = G(r, "code").PadLeft(6, '0');
                var year = int.TryParse((G(r, "end_date") ?? G(r, "period")).PadRight(4).Prefix(4), out var y) ? y : 0;
                if (year == 0) continue;
                if (!Audits.TryGetValue(code, out var list)) Audits[code] = list = new();
                list.Add((year, G(r, "opinion") + (G(r, "audit_result").Length > 0 ? " " + G(r, "audit_result") : "")));
            }
        var pledge = Path.Combine(_supDir, "pledge_stat.csv");
        if (File.Exists(pledge))
            foreach (var r in ReadCsv(pledge))
            {
                var code = G(r, "code").PadLeft(6, '0');
                // 优先控股股东质押比例，否则用整体质押比例
                var v = N(r, "pledge_ctrl") ?? N(r, "pledge_ratio") ?? N(r, "pledgeratio");
                if (v is not null) PledgeCtrl[code] = v.Value > 1 ? v.Value / 100 : v.Value;
            }
        var buy = Path.Combine(_supDir, "repurchase.csv");
        if (File.Exists(buy))
            foreach (var r in ReadCsv(buy))
            {
                var code = G(r, "code").PadLeft(6, '0');
                var dt = G(r, "ann_date").Length >= 4 ? G(r, "ann_date") : G(r, "end_date");
                if (dt.Length < 4) continue;
                if (!int.TryParse(dt[..4], out var year)) continue;
                var amt = N(r, "amount") ?? N(r, "vol") ?? N(r, "proc");
                if (amt is null) continue;
                if (!Buybacks.TryGetValue(code, out var m)) Buybacks[code] = m = new();
                m[year] = m.GetValueOrDefault(year) + amt.Value;
            }
    }

    // ───────── 无风险利率 ─────────

    public async Task<double> LoadRfAsync(CancellationToken ct)
    {
        if(LocalOnly || FinancialOnline)
        {
            var v=ReadLocal<double?>(Path.Combine(_cacheDir,"rf_input.json"))??Retained.Rf??Legacy?.Rf;
            RfPct=v??0.02;RfSource=v!=null?"既有RF输入（非本轮实时）/"+(Retained.RfSource.Length>0?Retained.RfSource:Legacy?.RfSource):"缺失：原模型2%假设，非观测数据";
            return RfPct.Value;
        }
        try
        {
            var y = await _em.GetCn10yYieldAsync(ct).ConfigureAwait(false);
            if (y is > 0)
            {
                RfPct = y > 1 ? y / 100 : y; // 接口通常返回 1.85 表示 1.85%
                if (y > 1) RfPct = y / 100;
                RfSource = "东方财富 RPTA_WEB_TREASURYYIELD";
                L($"[S29] 10 年期国债收益率 {RfPct.Value:P2}（{RfSource}）");
                SaveLocal(Path.Combine(_cacheDir,"rf_input.json"),RfPct.Value);
                return RfPct.Value;
            }
        }
        catch (Exception ex) { L($"[S29] 国债收益率获取失败：{ex.Message}，暂用 2.0%"); }
        RfPct = 0.02; RfSource = "缺数据，回退 2.0%";
        return RfPct.Value;
    }

    // ───────── 股票列表（沪深北） ─────────

    private static readonly string[] ClistUrls =
    [
        "https://push2.eastmoney.com/api/qt/clist/get",
        "https://82.push2.eastmoney.com/api/qt/clist/get",
        "https://80.push2.eastmoney.com/api/qt/clist/get",
        "https://88.push2.eastmoney.com/api/qt/clist/get",
        "https://91.push2.eastmoney.com/api/qt/clist/get",
        "https://push2delay.eastmoney.com/api/qt/clist/get",
        "http://80.push2.eastmoney.com/api/qt/clist/get",
    ];

    public async Task<List<QvStock>> LoadUniverseAsync(QvConfig cfg, CancellationToken ct)
    {
        var local=LocalUniverse();
        if(!LocalOnly && !FinancialOnline)
        {
            var live=await FetchUniverseCoreAsync(cfg,ct).ConfigureAwait(false);
            return live.Count>0?live:local; // daily ST/name/quote-universe monitoring stays independent of reports
        }
        if(local.Count>0 || !FinancialOnline)return local;
        return await FetchUniverseCoreAsync(cfg,ct).ConfigureAwait(false);
    }
    private async Task<List<QvStock>> FetchUniverseCoreAsync(QvConfig cfg, CancellationToken ct)
    {
        var cache = Path.Combine(_cacheDir, "universe.json");
        if (File.Exists(cache) && DateTime.Now - File.GetLastWriteTime(cache) < TimeSpan.FromDays(1))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<List<QvStock>>(File.ReadAllText(cache), Jo);
                if (cached is { Count: > 1000 })
                {
                    L($"[S29] 股票列表缓存 {cached.Count} 只");
                    return cached;
                }
            }
            catch { }
        }
        var list = new List<QvStock>();
        var fs = new Dictionary<string, string>
        {
            ["sh"] = "m:1+t:2,m:1+t:23",
            ["sz"] = "m:0+t:6,m:0+t:80",
        };
        if (cfg.B("global.include_bj", true)) fs["bj"] = "m:0+t:81+s:2048";
        foreach (var (board, f) in fs)
        {
            var gotBoard = await TryFetchClistBoardAsync(board, f, list, ct).ConfigureAwait(false);
            L($"[S29] {board} 列表 {gotBoard} 只");
        }
        var uniq = list.GroupBy(x => x.Code).Select(g => g.First()).ToList();
        if (uniq.Count < 1000)
        {
            L($"[S29] clist 仅 {uniq.Count} 只，改用业绩报表补全股票名单与行业");
            await ApplyYjbbMetaAsync(uniq, new[] { DateTime.Now.Year, DateTime.Now.Year - 1, DateTime.Now.Year - 2 }, ct).ConfigureAwait(false);
        }
        if (uniq.Count >= 1000)
        {
            try { File.WriteAllText(cache, JsonSerializer.Serialize(uniq, Jo)); } catch { }
        }
        else
            L($"[S29] 警告：股票名单仍偏少（{uniq.Count}），未写缓存");
        return uniq;
    }

    private async Task<int> TryFetchClistBoardAsync(string board, string fs, List<QvStock> list, CancellationToken ct)
    {
        var before = list.Count(x => x.Board == board);
        foreach (var baseUrl in ClistUrls)
        {
            var added = 0;
            var failed = false;
            for (var pn = 1; pn <= 80 && !failed; pn++)
            {
                var url = $"{baseUrl}?pn={pn}&pz=100&po=1&np=1&fltt=2&invt=2&fid=f12&fs={fs}&fields=f2,f6,f12,f14,f20,f21,f26,f100";
                JsonDocument? doc = null;
                for (var k = 0; k < 3 && doc is null; k++)
                {
                    try
                    {
                        using var r = await GuardAsync("qv-http",()=>Http.GetAsync(url, ct)).ConfigureAwait(false);
                        r.EnsureSuccessStatusCode();
                        var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        if (text.Length < 20 || text.TrimStart().StartsWith('<')) throw new InvalidDataException("非 JSON");
                        doc = JsonDocument.Parse(text);
                    }
                    catch { await Task.Delay(800 * (k + 1), ct).ConfigureAwait(false); }
                }
                if (doc is null) { failed = true; break; }
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                { failed = pn == 1; break; }
                var total = data.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                var got = 0;
                if (data.TryGetProperty("diff", out var diff))
                {
                    IEnumerable<JsonElement> rows = diff.ValueKind == JsonValueKind.Array
                        ? diff.EnumerateArray()
                        : diff.ValueKind == JsonValueKind.Object ? diff.EnumerateObject().Select(x => x.Value) : [];
                    foreach (var d in rows)
                    {
                        var code = Str(d, "f12").PadLeft(6, '0');
                        if (code.Length != 6) continue;
                        var ld = d.TryGetProperty("f26", out var f26)
                            ? (f26.ValueKind == JsonValueKind.Number ? f26.GetRawText() : f26.ToString())
                            : "";
                        if (ld is "0" or "-") ld = "";
                        var ind = Str(d, "f100"); if (ind is "-" or "0") ind = "";
                        list.Add(new QvStock
                        {
                            Code = code, Name = Str(d, "f14"), Board = board, IndustryRaw = ind, ListDate = NormalizeDate(ld),
                            Quote = new QvQuote { Price = Num(d, "f2"), AmountToday = Num(d, "f6") },
                        });
                        got++;
                        added++;
                    }
                }
                if (got == 0 || list.Count(x => x.Board == board) >= total) break;
                await Task.Delay(120, ct).ConfigureAwait(false);
            }
            if (added > 0) return list.Count(x => x.Board == board) - before;
            // added==0：换下一个 host
        }
        return list.Count(x => x.Board == board) - before;
    }

    /// <summary>用业绩报表补股票名称与行业（不依赖 clist）。已有名称/行业不被空值覆盖。</summary>
    public async Task ApplyYjbbMetaAsync(List<QvStock> stocks, IEnumerable<int> years, CancellationToken ct, bool addMissing = true)
    {
        var yj = await LoadYjbbAsync(years, ct).ConfigureAwait(false);
        var by = stocks.ToDictionary(s => s.Code, StringComparer.Ordinal);
        foreach (var (code, rows) in yj)
        {
            var last = rows.LastOrDefault(r => !string.IsNullOrWhiteSpace(r.Name) || !string.IsNullOrWhiteSpace(r.Industry)) ?? rows.Last();
            if (!by.TryGetValue(code, out var s))
            {
                if (!addMissing) continue;
                s = new QvStock { Code = code };
                stocks.Add(s);
                by[code] = s;
            }
            if (string.IsNullOrWhiteSpace(s.Name) || s.Name == s.Code) s.Name = last.Name;
            if (string.IsNullOrWhiteSpace(s.IndustryRaw)) s.IndustryRaw = last.Industry ?? "";
        }
    }

        // ───────── 批量年报（yjbb，已有磁盘缓存） ─────────

    public async Task<Dictionary<string, List<YjbbRow>>> LoadYjbbAsync(IEnumerable<int> years, CancellationToken ct)
    {
        var map = new Dictionary<string, List<YjbbRow>>(StringComparer.Ordinal);
        foreach (var y in years.Distinct().OrderBy(x => x))
        {
            var period = $"{y}1231";
            if(y>=TradingCalendar.NowCn().Year)continue; // future annual period is not a reason to download
            List<YjbbRow> rows;
            try { rows = await CachedAsync("yjbb_"+period+".json",()=>_em.GetYjbbAsync(period,m=>{},ct)).ConfigureAwait(false) ?? []; }
            catch { rows = []; }
            L($"[S29] 业绩报表 {period} {rows.Count} 行");
            foreach (var r in rows)
            {
                var code = r.Code.PadLeft(6, '0');
                if (!map.TryGetValue(code, out var l)) map[code] = l = new();
                l.Add(r);
            }
        }
        return map;
    }

    // ───────── 个股深挖：MAINFIN + 现金流 + 分红 + 可选资产负债表 ─────────

    public async Task FillFundamentalsAsync(IReadOnlyList<QvStock> stocks, int years, bool needBalance, CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(Math.Max(2, 8));
        var done = 0; var n = stocks.Count;
        await Task.WhenAll(stocks.Select(async s =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FillOneAsync(s, years, needBalance, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { s.Notes.Add("财务拉取失败: " + ex.Message); }
            finally
            {
                sem.Release();
                var i = Interlocked.Increment(ref done);
                if (i % 50 == 0 || i == n) L($"[S29] 财务 {i}/{n}");
            }
        })).ConfigureAwait(false);
    }

    private async Task FillOneAsync(QvStock s, int years, bool needBalance, CancellationToken ct)
    {
        s.Annuals.Clear();
        var mains = await GetCachedAsync($"mainfin_{s.Code}.json", TimeSpan.FromHours(36),
            () => FetchMainFinAsync(s.Code, ct)).ConfigureAwait(false) ?? [];
        var cfs = await GetCachedAsync($"cf_{s.Code}.json", TimeSpan.FromHours(36),
            () => FetchCashFlowAsync(s.Code, ct)).ConfigureAwait(false) ?? [];
        var bals = needBalance
            ? await GetCachedAsync($"bs_{s.Code}.json", TimeSpan.FromHours(36), () => FetchBalanceAsync(s.Code, ct)).ConfigureAwait(false) ?? []
            : [];
        var divs = await GetCachedAsync($"div_{s.Code}.json", TimeSpan.FromDays(7),
            () => FetchDividendsAsync(s.Code, ct)).ConfigureAwait(false) ?? [];

        // 只要年报
        var annualMain = mains.Where(x => x.ReportDate.EndsWith("-12-31") || x.ReportType is "年报" or "年度").ToList();
        if (annualMain.Count == 0) s.Notes.Add("缺少年度财报；季度不能冒充年报");
        foreach (var m in annualMain.OrderBy(x => x.ReportDate))
        {
            var year = YearOf(m.ReportDate);
            if (year == 0) continue;
            var cf = cfs.FirstOrDefault(x => YearOf(x.ReportDate) == year && (x.ReportDate.EndsWith("-12-31") || annualMain.Count < 3));
            var bs = bals.FirstOrDefault(x => YearOf(x.ReportDate) == year && x.ReportDate.EndsWith("-12-31"));
            var dps = divs.Where(d => d.Year == year).Sum(d => d.Dps);
            var a = new QvAnnual
            {
                Year = year, ReportDate = m.ReportDate, Source = "东财 MAINFINADATA/现金流/分红",
                Revenue = m.Revenue, Np = m.Np, DeductNp = m.DeductNp, Roe = m.Roe, Roic = m.Roic,
                NetMargin = m.NetMargin, GrossMargin = m.GrossMargin,
                DebtRatio = m.DebtRatio, InterestDebtRatio = m.InterestDebtRatio, InterestCoverage = m.InterestCoverage,
                OcfToNp = m.OcfToNp, Bps = m.Bps, Eps = m.Eps, DeductEps = m.DeductEps, OcfPs = m.OcfPs,
                TotalShare = m.TotalShare, TotalAssets = m.TotalAssets ?? bs?.TotalAssets, Equity = bs?.Equity ?? (m.Bps is > 0 && m.TotalShare is > 0 ? m.Bps * m.TotalShare : null),
                Ocf = cf?.Ocf ?? (m.OcfPs is > 0 && m.TotalShare is > 0 ? m.OcfPs * m.TotalShare : null),
                Capex = cf?.Capex,
                Npl = m.Npl, ProvisionCov = m.ProvisionCov, Cet1 = m.Cet1, Car = m.Car, Tier1 = m.Tier1,
                Nim = m.Nim, GrossLoans = m.GrossLoans, Deposits = m.Deposits, Overdue = m.Overdue,
                Leverage = (m.TotalAssets is > 0 && (bs?.Equity ?? 0) > 0) ? m.TotalAssets / bs!.Equity : (m.DebtRatio is > 0 ? 1 / (1 - m.DebtRatio / (m.DebtRatio > 1 ? 100 : 1)) : null),
                Cash = bs?.Cash, TradeFinAsset = bs?.TradeFin, Goodwill = bs?.Goodwill, Ar = bs?.Ar, Inventory = bs?.Inventory,
                Dps = divs.Any(d => d.Year == year) ? dps : null,
                Depreciation = cf?.Depreciation,
            };
            a.Fcf = a.Ocf is not null && a.Capex is not null ? a.Ocf - a.Capex : null;
            // 维持性 FCF：OCF − min(capex, 折旧)；无折旧时暂用账面 FCF，估值阶段再按配置比例拆分。
            if (a.Ocf is not null)
            {
                double? maint = a.Depreciation is > 0 && a.Capex is not null ? Math.Min(a.Capex.Value, a.Depreciation.Value)
                    : a.Depreciation is > 0 ? a.Depreciation
                    : null;
                a.FcfMaint = maint is not null ? a.Ocf - maint : a.Fcf;
            }

            if (a.Payout is null && a.Dps is > 0 && a.TotalShare is > 0 && a.Np is > 0)
                a.Payout = a.Dps * a.TotalShare / a.Np;
            // 单位修正：比率类字段 MAINFIN 里有的是百分数
            a.Roe = PctToUnit(a.Roe);
            a.Roic = PctToUnit(a.Roic);
            a.NetMargin = PctToUnit(a.NetMargin);
            a.GrossMargin = PctToUnit(a.GrossMargin);
            a.DebtRatio = PctToUnit(a.DebtRatio);
            a.InterestDebtRatio = PctToUnit(a.InterestDebtRatio);
            a.Npl = PctToUnit(a.Npl, typicalMax: 20, alreadyRatioBelow: 0.05);          // 0.68 表示 0.68%
            a.ProvisionCov = PctToUnit(a.ProvisionCov, typicalMax: 2000, alreadyRatioBelow: 20); // 426 表示 426%
            a.Cet1 = PctToUnit(a.Cet1, typicalMax: 40);
            a.Car = PctToUnit(a.Car, typicalMax: 40);
            a.Tier1 = PctToUnit(a.Tier1, typicalMax: 40);
            a.Nim = PctToUnit(a.Nim, typicalMax: 20);
            // Tushare 补充覆盖银行字段
            if (BankSup.TryGetValue(s.Code + "|" + year + "1231", out var sup) || BankSup.TryGetValue(s.Code, out sup))
            {
                if (sup.Npl is not null) { a.Npl = PctToUnit(sup.Npl, typicalMax: 20, alreadyRatioBelow: 0.05); s.Notes.Add("不良率来自 Tushare 补充"); }
                if (sup.ProvisionCov is not null) { a.ProvisionCov = PctToUnit(sup.ProvisionCov, typicalMax: 2000, alreadyRatioBelow: 20); s.Notes.Add("拨备覆盖率来自 Tushare 补充"); }
                if (sup.Cet1 is not null) { a.Cet1 = PctToUnit(sup.Cet1, typicalMax: 40); s.Notes.Add("CET1 来自 Tushare 补充"); }
            }
            if (Buybacks.TryGetValue(s.Code, out var bm) && bm.TryGetValue(year, out var ba) && a.Np is > 0)
                a.Buyback = ba;
            s.Annuals.Add(a);
        }
        s.Annuals = s.Annuals.GroupBy(x => x.Year).Select(g => g.Last()).OrderBy(x => x.Year).ToList();
        if (s.Annuals.Count > years) s.Annuals = s.Annuals.TakeLast(years).ToList();
        TagDividendStatus(s, divs);
        AttachPeriods(s, mains, cfs, bals);
    }


    /// <summary>
    /// 东财比率字段：小于 typicalMax 且大于 1 的视为百分数（如 ROE=19.7 → 0.197）。
    /// 不良率 0.68 也是百分数（&lt;1 但大于 0.05 的银行不良率按百分数）。
    /// alreadyRatioBelow：大于该值才当百分数（拨备覆盖率 4.0 可能已是倍数，426 是百分数）。
    /// </summary>


    public async Task EnsurePeriodsAsync(QvStock s, CancellationToken ct)
    {
        if (s.Periods.Count > 0) return;
        var mains = await GetCachedAsync($"mainfin_{s.Code}.json", TimeSpan.FromHours(36),
            () => FetchMainFinAsync(s.Code, ct)).ConfigureAwait(false) ?? [];
        var cfs = await GetCachedAsync($"cf_{s.Code}.json", TimeSpan.FromHours(36),
            () => FetchCashFlowAsync(s.Code, ct)).ConfigureAwait(false) ?? [];
        var bals = await GetCachedAsync($"bs_{s.Code}.json", TimeSpan.FromHours(36),
            () => FetchBalanceAsync(s.Code, ct)).ConfigureAwait(false) ?? [];
        AttachPeriods(s, mains, cfs, bals);
    }

    private void AttachPeriods(QvStock s, List<MainRow> mains, List<CfRow> cfs, List<BsRow> bals)
    {
        // 保存全部报告期供复核使用，不改写 Annuals（评分/估值仍只用年报）。
        var list = new List<QvPeriod>();
        foreach (var m in mains)
        {
            if (string.IsNullOrWhiteSpace(m.ReportDate)) continue;
            var cf = cfs.FirstOrDefault(x => x.ReportDate == m.ReportDate);
            var bs = bals.FirstOrDefault(x => x.ReportDate == m.ReportDate);
            var ocf = cf?.Ocf ?? (m.OcfPs is > 0 && m.TotalShare is > 0 ? m.OcfPs * m.TotalShare : null);
            list.Add(new QvPeriod
            {
                ReportDate = m.ReportDate,
                PeriodType = PeriodTypeOf(m.ReportDate, m.ReportType),
                IsCumulative = true,
                Source = "东财 MAINFINADATA/现金流/资产负债表",
                Revenue = m.Revenue,
                Np = m.Np,
                DeductNp = m.DeductNp,
                Roe = PctToUnit(m.Roe),
                GrossMargin = PctToUnit(m.GrossMargin),
                Ocf = ocf,
                OcfToNp = m.OcfToNp, // NCO_NETPROFIT is a ratio to consolidated total NP, never percent; contract-v1.
                Ar = bs?.Ar,
                Inventory = bs?.Inventory,
            });
        }
        s.Periods = list
            .GroupBy(x => x.ReportDate)
            .Select(g => g.Last())
            .OrderBy(x => x.ReportDate)
            .ToList();
    }

    private static string PeriodTypeOf(string date, string reportType)
    {
        var t = reportType ?? "";
        if (date.EndsWith("-12-31") || t is "年报" or "年度") return "annual";
        if (date.EndsWith("-03-31") || t.Contains("一季")) return "q1";
        if (date.EndsWith("-06-30") || t.Contains("中报") || t.Contains("半年")) return "h1";
        if (date.EndsWith("-09-30") || t.Contains("三季")) return "q3";
        return "other";
    }

    public Task<List<QvForecastItem>> FetchForecastsAsync(string code,CancellationToken ct) => EventCached("forecasts_"+code+".json",()=>FetchForecastsCoreAsync(code,ct));
    private async Task<List<QvForecastItem>> FetchForecastsCoreAsync(string code, CancellationToken ct)
    {
        var list = new List<QvForecastItem>();
        async Task Pull(string reportName, string kind)
        {
            var url = "https://datacenter-web.eastmoney.com/api/data/v1/get?sortColumns=NOTICE_DATE&sortTypes=-1&pageSize=20&pageNumber=1"
                    + $"&reportName={reportName}&columns=ALL&filter=(SECURITY_CODE=%22{code}%22)";
            try
            {
                var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
                foreach (var d in Arr(root, "result", "data"))
                {
                    var period = DateOnly(Str(d, "REPORT_DATE"));
                    if (string.IsNullOrWhiteSpace(period)) period = DateOnly(Str(d, "NOTICE_DATE"));
                    var typ = Str(d, "PREDICT_TYPE");
                    if (string.IsNullOrWhiteSpace(typ)) typ = Str(d, "PREDICT_FINANCE");
                    if (string.IsNullOrWhiteSpace(typ)) typ = kind;
                    var summary = Str(d, "PREDICT_CONTENT");
                    if (string.IsNullOrWhiteSpace(summary)) summary = Str(d, "CHANGE_REASON_EXPLAIN");
                    if (string.IsNullOrWhiteSpace(summary))
                        summary = string.Join(" ", new[] { Str(d, "ADD_AMP"), Str(d, "PREDICT_AMT"), Str(d, "YIELD") }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    if (string.IsNullOrWhiteSpace(period) && string.IsNullOrWhiteSpace(summary)) continue;
                    list.Add(new QvForecastItem
                    {
                        Period = period,
                        Type = typ,
                        Summary = string.IsNullOrWhiteSpace(summary) ? "（无摘要字段）" : summary,
                        Source = "东财 " + kind,
                    });
                }
            }
            catch { /* 标记缺失由调用方处理 */ }
        }
        await Pull("RPT_PUBLIC_OP_NEWPREDICT", "业绩预告").ConfigureAwait(false);
        await Pull("RPT_FCI_PERFORMANCEE", "业绩快报").ConfigureAwait(false);
        return list.Take(10).ToList();
    }

    private static void TagDividendStatus(QvStock s, List<DivRow> divs)
    {
        s.DividendSource = "EastMoney GetDividends + RPT_SHAREBONUS_DET";
        s.DividendAsOf = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var years = s.Annuals.TakeLast(3).Select(a => a.Year).ToList();
        if (divs.Count == 0)
        {
            s.DividendStatus = "dividend_missing";
            s.DividendMissingReason = "dividend API returned no records; must not default DPS to 0 or assume no dividend";
            return;
        }
        var known = years.Count(y => divs.Any(d => d.Year == y));
        if (years.Count > 0 && known == years.Count)
        {
            s.DividendStatus = "dividend_confirmed";
            s.DividendMissingReason = "";
        }
        else if (known > 0)
        {
            s.DividendStatus = "dividend_partial";
            s.DividendMissingReason = $"last 3 fiscal years: {years.Count - known} year(s) have no dividend record (not treated as zero)";
        }
        else
        {
            s.DividendStatus = "dividend_missing";
            s.DividendMissingReason = "history exists but last 3 years are uncovered; must not treat recent DPS as 0";
        }
    }

    private static double? PctToUnit(double? v, double typicalMax = 100, double alreadyRatioBelow = 1.5)
    {
        if (v is null || double.IsNaN(v.Value)) return null;
        var x = v.Value;
        if (Math.Abs(x) > typicalMax) return x / 100; // 极端百分数
        if (Math.Abs(x) > alreadyRatioBelow) return x / 100;
        return x;
    }

    // ───────── 流动性 60 日成交额 ─────────

    public async Task FillTurnoverAsync(IReadOnlyList<QvStock> stocks, int days, CancellationToken ct)
    {
        if(LocalOnly || FinancialOnline)return;
        using var sem = new SemaphoreSlim(8);
        var done = 0; var n = stocks.Count; var miss = 0;
        await Task.WhenAll(stocks.Select(async s =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var k = await GuardAsync("turnover",()=>BrMarketData.FetchKlineAsync(BrMarketData.Secid(s.Code), Math.Max(days + 5, 80), 0, 3, ct)).ConfigureAwait(false);
                k ??= await GuardAsync("turnover-fallback",()=>BrMarketData.FetchKlineAsync(BrMarketData.Secid(s.Code), Math.Max(days + 5, 80), 1, 2, ct)).ConfigureAwait(false);
                if (k?.S.Amount is { Length: > 0 } amt)
                {
                    var take = amt.TakeLast(days).Where(x => !double.IsNaN(x) && x > 0).ToList();
                    if (take.Count > 0)
                    {
                        s.Quote.Turnover60 = take.Average();
                        if (s.Quote.Price is null or 0) s.Quote.Price = k.S.Close[^1];
                        s.Quote.AsOf = k.S.Date[^1];
                        s.Quote.Source = "东财 push2his 日K 成交额";
                    }
                }
                if (s.Quote.Turnover60 is null) Interlocked.Increment(ref miss);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref miss);
                if (miss <= 5) L($"[S29] K线成交额失败 {s.Code}: {ex.Message}");
            }
            finally
            {
                sem.Release();
                var i = Interlocked.Increment(ref done);
                if (i % 100 == 0 || i == n) L($"[S29] 60日成交额 {i}/{n}（缺 {miss}）");
            }
        })).ConfigureAwait(false);
    }

    // ───────── 现价刷新 ─────────

    public async Task FillPricesAsync(IReadOnlyList<QvStock> stocks, QuoteService? quotes, CancellationToken ct)
    {
        if (LocalOnly || FinancialOnline || quotes is null) return;
        var codes = stocks.Select(s => s.Code).Distinct().ToList();
        var map = new Dictionary<string, QuoteInfo>();
        foreach (var chunk in codes.Chunk(80))
        {
            try
            {
                var part = await GuardAsync("quotes",()=>quotes.FetchAsync(chunk, ct)).ConfigureAwait(false);
                foreach (var kv in part) map[kv.Key] = kv.Value;
            }
            catch { }
        }
        var now = TradingCalendar.NowCn().ToString("yyyy-MM-dd HH:mm");
        foreach (var s in stocks)
            if (map.TryGetValue(s.Code, out var q) && q.Price > 0)
            {
                s.Quote.Price = (double)q.Price;
                s.Quote.PrevClose = (double)q.PrevClose;
                s.Quote.AsOf = now;
                s.Quote.Source = "实时行情";
            }
        L($"[S29] 实时行情 {map.Count}/{codes.Count}");
    }

    // ───────── 公告关键词（池内轻量检查） ─────────

    public Task<List<(string Date,string Title)>> FetchNoticesAsync(string code,int days,CancellationToken ct) => EventCached("notices_"+code+"_"+days+".json",()=>FetchNoticesCoreAsync(code,days,ct));
    private async Task<List<(string Date, string Title)>> FetchNoticesCoreAsync(string code, int days, CancellationToken ct)
    {
        var url = $"https://np-anotice-stock.eastmoney.com/api/security/ann?sr=-1&page_size=50&page_index=1&ann_type=A&client_source=web&stock_list={code}&f_node=0&s_node=0";
        try
        {
            using var r = await GuardAsync("qv-http",()=>Http.GetAsync(url, ct)).ConfigureAwait(false);
            r.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var list = new List<(string, string)>();
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;
            var since = TradingCalendar.NowCn().Date.AddDays(-days);
            foreach (var it in arr.EnumerateArray())
            {
                var title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var dt = it.TryGetProperty("notice_date", out var d) ? d.GetString() ?? "" : (it.TryGetProperty("display_time", out var e) ? e.GetString() ?? "" : "");
                if (DateTime.TryParse(dt, out var parsed) && parsed.Date < since) continue;
                if (title.Length > 0) list.Add((dt, title));
            }
            return list;
        }
        catch { return []; }
    }

    // ───────── HTTP 报表 ─────────

    private sealed class MainRow
    {
        public string ReportDate = "", ReportType = "";
        public double? Revenue, Np, DeductNp, Roe, Roic, NetMargin, GrossMargin, DebtRatio, InterestDebtRatio, InterestCoverage, OcfToNp, Bps, Eps, DeductEps, OcfPs, TotalShare, TotalAssets;
        public double? Npl, ProvisionCov, Cet1, Car, Tier1, Nim, GrossLoans, Deposits, Overdue;
    }
    private sealed class CfRow { public string ReportDate = ""; public double? Ocf, Capex, Depreciation; }
    private sealed class BsRow { public string ReportDate = ""; public double? TotalAssets, Equity, Cash, TradeFin, Goodwill, Ar, Inventory; }
    private sealed class DivRow { public int Year; public double Dps; public double Transfer; public string ExDate = ""; }

    private async Task<List<MainRow>> FetchMainFinAsync(string code, CancellationToken ct)
    {
        var url = "https://datacenter.eastmoney.com/securities/api/data/v1/get?reportName=RPT_F10_FINANCE_MAINFINADATA&columns=ALL"
                + $"&filter=(SECURITY_CODE=%22{code}%22)&pageNumber=1&pageSize=40&sortTypes=-1&sortColumns=REPORT_DATE&source=HSF10&client=PC";
        var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<MainRow>();
        foreach (var d in Arr(root, "result", "data"))
        {
            list.Add(new MainRow
            {
                ReportDate = DateOnly(Str(d, "REPORT_DATE")),
                ReportType = Str(d, "REPORT_TYPE"),
                Revenue = Num(d, "TOTALOPERATEREVE"), Np = Num(d, "PARENTNETPROFIT"), DeductNp = Num(d, "KCFJCXSYJLR"),
                Roe = Num(d, "ROEJQ"), Roic = Num(d, "ROIC"), NetMargin = Num(d, "XSJLL"), GrossMargin = Num(d, "XSMLL"),
                DebtRatio = Num(d, "ZCFZL"), InterestDebtRatio = Num(d, "INTEREST_DEBT_RATIO"),
                InterestCoverage = Num(d, "INTEREST_COVERAGE_RATIO") ?? Num(d, "INTSTCOVRATE"),
                OcfToNp = Num(d, "NCO_NETPROFIT"), Bps = Num(d, "BPS"), Eps = Num(d, "EPSJB"), DeductEps = Num(d, "EPSKCJB"),
                OcfPs = Num(d, "MGJYXJJE"), TotalShare = Num(d, "TOTAL_SHARE"), TotalAssets = Num(d, "TOTAL_ASSETS_PK") ?? Num(d, "SS_TA"),
                Npl = Num(d, "NONPERLOAN"), ProvisionCov = Num(d, "BLDKBBL"),
                Cet1 = Num(d, "HXYJBCZL"), Car = Num(d, "NEWCAPITALADER"), Tier1 = Num(d, "FIRST_ADEQUACY_RATIO"),
                Nim = Num(d, "NET_INTEREST_MARGIN"), GrossLoans = Num(d, "GROSSLOANS") ?? Num(d, "LOAN_ADVANCES"),
                Deposits = Num(d, "TOTALDEPOSITS"), Overdue = Num(d, "OVERDUE_LOANS"),
            });
        }
        return list;
    }

    private async Task<List<CfRow>> FetchCashFlowAsync(string code, CancellationToken ct)
    {
        // 复用已有客户端（缓存友好），并补一套 columns=ALL 兜底
        try
        {
            var rows = await _em.GetCashFlowAsync(code, ct: ct).ConfigureAwait(false);
            return rows.Select(r => new CfRow { ReportDate = r.ReportDate?.ToString("yyyy-MM-dd") ?? "", Ocf = r.Ocf, Capex = r.Capex, Depreciation = r.Depreciation }).ToList();
        }
        catch { return []; }
    }

    private async Task<List<BsRow>> FetchBalanceAsync(string code, CancellationToken ct)
    {
        var url = "https://datacenter.eastmoney.com/securities/api/data/v1/get?reportName=RPT_F10_FINANCE_GBALANCE"
                + "&columns=REPORT_DATE,TOTAL_ASSETS,TOTAL_PARENT_EQUITY,MONETARYFUNDS,TRADE_FINASSET,GOODWILL,ACCOUNTS_RECE,INVENTORY,PREFERRED_SHARES,PERPETUAL_BOND"
                + $"&filter=(SECURITY_CODE=%22{code}%22)&pageNumber=1&pageSize=40&sortTypes=-1&sortColumns=REPORT_DATE&source=HSF10&client=PC";
        var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<BsRow>();
        foreach (var d in Arr(root, "result", "data"))
        {
            var eq = Num(d, "TOTAL_PARENT_EQUITY");
            var pref = Num(d, "PREFERRED_SHARES") ?? 0; var perp = Num(d, "PERPETUAL_BOND") ?? 0;
            if (eq is not null) eq -= pref + perp;
            list.Add(new BsRow
            {
                ReportDate = DateOnly(Str(d, "REPORT_DATE")),
                TotalAssets = Num(d, "TOTAL_ASSETS"), Equity = eq,
                Cash = Num(d, "MONETARYFUNDS"), TradeFin = Num(d, "TRADE_FINASSET"),
                Goodwill = Num(d, "GOODWILL"), Ar = Num(d, "ACCOUNTS_RECE"), Inventory = Num(d, "INVENTORY"),
            });
        }
        return list;
    }

    private async Task<List<DivRow>> FetchDividendsAsync(string code, CancellationToken ct)
    {
        var map = new Dictionary<(int Year, string Ex), DivRow>();
        void Add(int year, double dps, double transfer, string ex)
        {
            var key = (year, ex ?? "");
            if (!map.TryGetValue(key, out var r))
            {
                r = new DivRow { Year = year, ExDate = ex ?? "" };
                map[key] = r;
            }
            if (dps > r.Dps) r.Dps = dps;
            if (transfer > r.Transfer) r.Transfer = transfer;
        }
        try
        {
            var rows = await _em.GetDividendsAsync(code, ct: ct).ConfigureAwait(false);
            foreach (var r in rows)
                Add(r.ExDate.Year, r.Dps, r.Transfer, r.ExDate.ToString("yyyy-MM-dd"));
        }
        catch { /* 主源失败则走东财分红明细兜底 */ }

        // 东财 RPT_SHAREBONUS_DET：补送转、除权日空但已实施的分红（缩小「分红缺口」）
        try
        {
            var filter = Uri.EscapeDataString($"(SECURITY_CODE=\"{code}\")");
            var url = "https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPT_SHAREBONUS_DET&columns=ALL"
                    + $"&filter={filter}&pageNumber=1&pageSize=200&sortColumns=PLAN_NOTICE_DATE,EX_DIVIDEND_DATE&sortTypes=-1,-1&source=WEB&client=WEB";
            var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
            foreach (var d in Arr(root, "result", "data"))
            {
                var bonus = Num(d, "PRETAX_BONUS_RMB"); // 每 10 股税前股利
                var transfer10 = Num(d, "TRANSFER_RATIO") ?? Num(d, "IT_RATIO") ?? Num(d, "BONUS_IT_RATIO") ?? 0;
                var bonusShare10 = Num(d, "BONUS_RATIO") ?? 0;
                var dps = (bonus ?? 0) / 10.0;
                var transfer = (transfer10 + bonusShare10) / 10.0;
                var ex = DateOnly(Str(d, "EX_DIVIDEND_DATE"));
                if (ex.Length < 8) ex = DateOnly(Str(d, "EQUITY_RECORD_DATE"));
                if (ex.Length < 8) ex = DateOnly(Str(d, "IMPLEMENT_DATE"));
                if (dps <= 0 && transfer <= 0) continue;
                var year = 0;
                if (DateTime.TryParse(ex, out var dt)) year = dt.Year;
                else if (DateTime.TryParse(DateOnly(Str(d, "REPORT_DATE")), out var rd)) year = rd.Year;
                if (year == 0) continue;
                Add(year, dps, transfer, ex);
            }
        }
        catch { }

        return map.Values.OrderBy(x => x.Year).ThenBy(x => x.ExDate).ToList();
    }

    public async Task<(double CashDps, double ShareFactor)> ExDivAdjustAsync(string code, DateTime from, DateTime to, CancellationToken ct)
    {
        var divs = await GetCachedAsync($"div_{code}.json", TimeSpan.FromDays(7), () => FetchDividendsAsync(code, ct)).ConfigureAwait(false) ?? [];
        double cash = 0, factor = 1;
        foreach (var d in divs)
        {
            if (!DateTime.TryParse(d.ExDate, out var ex)) continue;
            if (ex.Date <= from.Date || ex.Date > to.Date) continue;
            cash += d.Dps;
            if (d.Transfer > 0) factor *= 1 + d.Transfer; // 每 1 股送转 Transfer 股
        }
        return (cash, factor);
    }

    // ───────── 历史年收盘（估值分位） ─────────

    public async Task<List<(string Date, double Close)>> YearEndClosesAsync(string code, int years, CancellationToken ct)
    {
        var path=Path.Combine(_cacheDir,"yearend_"+code+".json");
        var old=ReadLocal<List<(string Date,double Close)>>(path);
        if(old!=null && (!FinancialOnline || !FinancialAccess.IsDue(File.GetLastWriteTime(path),DateTime.Now,Access?.CycleMonths??6)))return old;
        if(!FinancialOnline)return new();
        var fetched=await FetchYearEndCoreAsync(code,years,ct).ConfigureAwait(false);
        if(fetched.Count>0)SaveLocal(path,fetched);return fetched;
    }
    private async Task<List<(string Date,double Close)>> FetchYearEndCoreAsync(string code,int years,CancellationToken ct)
    {
        var k = await GuardAsync("yearend-kline",()=>BrMarketData.FetchKlineAsync(BrMarketData.Secid(code), years * 260 + 40, 0, 3, ct)).ConfigureAwait(false);
        if (k?.S.Date is not { Length: > 0 }) return [];
        var map = new Dictionary<int, (string, double)>();
        for (var i = 0; i < k.S.Date.Length; i++)
        {
            if (!DateTime.TryParse(k.S.Date[i], out var dt)) continue;
            map[dt.Year] = (k.S.Date[i], k.S.Close[i]);
        }
        return map.OrderBy(x => x.Key).Select(x => x.Value).ToList();
    }

    // ───────── JSON / CSV 工具 ─────────

    /// <summary>旧缓存用默认 JsonSerializer 写出字段类时得到 [{},{}]，一律视为无效并重新拉取。</summary>
    private static bool IsHollow(object? v) => v switch
    {
        IEnumerable<MainRow> ms => ms.Any() && ms.All(x => string.IsNullOrEmpty(x.ReportDate)),
        IEnumerable<CfRow> cs => cs.Any() && cs.All(x => string.IsNullOrEmpty(x.ReportDate)),
        IEnumerable<BsRow> bs => bs.Any() && bs.All(x => string.IsNullOrEmpty(x.ReportDate)),
        IEnumerable<DivRow> ds => ds.Any() && ds.All(x => x.Year == 0 && x.Dps == 0),
        _ => false,
    };

    private Task<T?> GetCachedAsync<T>(string name, TimeSpan ttl, Func<Task<T>> fetch) => CachedAsync(name,fetch);

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        if(FinancialRawStore.IsFinancialUrl(url))
        {
            var canonical=FinancialRawStore.CanonicalUrl(url);
            return await new FinancialRawStore(Path.Combine(_dataDir,"cache")).GetAsync("eastmoney",FinancialRawStore.Resource(canonical),FinancialRawStore.Security(canonical),canonical,async()=>
            {
                Access?.BeforeNetwork("qv-financial:"+FinancialRawStore.Resource(canonical),true);
                using var response=await Http.GetAsync(canonical,ct);response.EnsureSuccessStatusCode();return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            },ct).ConfigureAwait(false);
        }
        Exception? last = null;
        for (var k = 0; k < 3; k++)
        {
            try
            {
                using var r = await GuardAsync("qv-http",()=>Http.GetAsync(url, ct)).ConfigureAwait(false);
                r.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            catch (Exception ex) { last = ex; await Task.Delay(600 * (k + 1), ct).ConfigureAwait(false); }
        }
        throw last ?? new InvalidOperationException(url);
    }

    private async Task<T> GuardAsync<T>(string name,Func<Task<T>> fetch)
    { Access?.BeforeNetwork(name);return await fetch().ConfigureAwait(false); }

    private static IEnumerable<JsonElement> Arr(JsonDocument doc, params string[] path)
    {
        JsonElement n = doc.RootElement;
        foreach (var k in path)
        {
            if (n.ValueKind != JsonValueKind.Object || !n.TryGetProperty(k, out n)) return [];
        }
        return n.ValueKind == JsonValueKind.Array ? n.EnumerateArray() : [];
    }

    private static string Str(JsonElement e, string k)
        => e.TryGetProperty(k, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? ""
            : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : ""
            : "";
    private static double? Num(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, Inv, out var s)) return s;
        return null;
    }
    private static string DateOnly(string s) => s.Length >= 10 ? s[..10] : s;
    private static int YearOf(string s) => s.Length >= 4 && int.TryParse(s[..4], out var y) ? y : 0;

    public QvBankCapitalReq BankRequirement(string code)
    {
        code = (code ?? "").PadLeft(6, '0');
        if (BankCapital.TryGetValue(code, out var r)) return r;
        return new QvBankCapitalReq
        {
            Code = code,
            Requirement = 0.075,
            Cet1Min = 0.05,
            ConservationBuffer = 0.025,
            Components = "5% CET1 min + 2.5% conservation (statutory floor only, unverified total)",
            Source = "default statutory floor; see bank_capital_req_SOURCES.md",
            VerificationStatus = "pending",
            NeedsReview = true,
            Notes = "not listed; must not treat 7.5% as a verified full requirement or default to 9%",
        };
    }

    private static double? ParsePct(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v > 1.5 ? v / 100 : v;
        return null;
    }

    private void LoadBankCapital()
    {
        var path = Path.Combine(_outDir, "bank_capital_req.csv");
        BankCapital = new Dictionary<string, QvBankCapitalReq>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            L("[S29] bank_capital_req.csv missing; bank capital requirements stay pending with 7.5% indicative floor only");
            return;
        }
        foreach (var r in ReadCsv(path))
        {
            var code = (r.GetValueOrDefault("stock_code", "") != "" ? r.GetValueOrDefault("stock_code", "") : r.GetValueOrDefault("code", "")).Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;
            code = code.PadLeft(6, '0');
            var cet1Min = ParsePct(r.GetValueOrDefault("regulatory_cet1_min", "")) ?? 0.05;
            var cons = ParsePct(r.GetValueOrDefault("capital_conservation_buffer", "")) ?? 0.025;
            var dsibAdd = ParsePct(r.GetValueOrDefault("d_sib_addon", "")) ?? 0;
            var other = ParsePct(r.GetValueOrDefault("other_addon", "")) ?? 0;
            var p2 = ParsePct(r.GetValueOrDefault("pillar2_requirement", ""));
            // legacy column
            double? legacyReq = ParsePct(r.GetValueOrDefault("requirement", ""));
            var addon = Math.Max(dsibAdd, other);
            var req = cet1Min + cons + addon + (p2 ?? 0);
            if (legacyReq is > 0 && dsibAdd == 0 && other == 0 && !r.ContainsKey("regulatory_cet1_min"))
                req = legacyReq.Value;
            var status = r.GetValueOrDefault("verification_status", "");
            if (string.IsNullOrWhiteSpace(status))
            {
                var nr = r.GetValueOrDefault("needs_review", "true");
                status = (nr.Equals("false", StringComparison.OrdinalIgnoreCase) || nr == "0") ? "verified" : "pending";
            }
            var pending = !status.Equals("verified", StringComparison.OrdinalIgnoreCase);
            BankCapital[code] = new QvBankCapitalReq
            {
                Code = code,
                Name = r.GetValueOrDefault("bank_name", r.GetValueOrDefault("name", "")),
                BankType = r.GetValueOrDefault("bank_type", ""),
                DSibLevel = r.GetValueOrDefault("d_sib_level", r.GetValueOrDefault("dsib_group", "")),
                Cet1Min = cet1Min,
                ConservationBuffer = cons,
                DSibAddon = dsibAdd,
                OtherAddon = other,
                Pillar2 = p2,
                Requirement = req,
                Components = $"CET1 min {cet1Min:P2} + conservation {cons:P2} + addon {addon:P2}" + (p2 is > 0 ? $" + P2 {p2:P2}" : "") + (pending ? " (indicative, pending verification)" : ""),
                Source = r.GetValueOrDefault("source", ""),
                SourceDate = r.GetValueOrDefault("source_date", ""),
                VerifiedAt = r.GetValueOrDefault("verified_at", ""),
                VerificationStatus = status,
                Notes = r.GetValueOrDefault("notes", r.GetValueOrDefault("note", "")),
                NeedsReview = pending,
            };
        }
        L($"[S29] loaded {BankCapital.Count} bank capital-requirement rows (verification_status kept as in CSV; none treated as verified unless marked)");
    }

    private static string NormalizeDate(string ld)
    {
        if (ld.Length == 8 && ld.All(char.IsDigit)) return $"{ld[..4]}-{ld[4..6]}-{ld[6..]}";
        return ld;
    }

    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var res = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return res;
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length < 2) return res;
        var header = ObservationPool.ParseCsvLine(lines[0]).Select(h => h.Trim().TrimStart('\uFEFF')).ToList();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var f = ObservationPool.ParseCsvLine(line);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++) d[header[i]] = i < f.Count ? f[i].Trim() : "";
            res.Add(d);
        }
        return res;
    }

    private static string G(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
    private static double? N(Dictionary<string, string> d, string k)
        => double.TryParse(G(d, k).Trim().TrimEnd('%'), NumberStyles.Float, Inv, out var v) ? v : null;

    private static Dictionary<string, (string, string)> ReadOverride(string path)
    {
        var m = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var r in ReadCsv(path))
        {
            var code = G(r, "code").PadLeft(6, '0');
            var type = G(r, "type").Trim().ToUpperInvariant();
            if (code.Length == 6 && type.StartsWith('T')) m[code] = (type, G(r, "note"));
        }
        return m;
    }

    private static Dictionary<string, (string, string)> ReadMapping(string path)
    {
        var m = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ReadCsv(path))
        {
            var ind = G(r, "industry").Trim();
            var type = G(r, "type").Trim().ToUpperInvariant();
            if (ind.Length > 0 && type.Length > 0) m[ind] = (type, G(r, "note"));
        }
        return m;
    }

    private static Dictionary<string, QvManualTag> ReadTags(string path)
    {
        var m = new Dictionary<string, QvManualTag>(StringComparer.Ordinal);
        foreach (var r in ReadCsv(path))
        {
            var code = G(r, "code").PadLeft(6, '0');
            if (code.Length != 6) continue;
            bool Flag(string k) => G(r, k) is "1" or "true" or "Y" or "yes" or "是";
            m[code] = new QvManualTag
            {
                Code = code, MoatConfirmed = Flag("moat_confirmed"), ReviewedOk = Flag("reviewed_ok"),
                NplFormation = N(r, "npl_formation"), SpecialMention = N(r, "special_mention"),
                ReLoanPct = N(r, "re_loan_pct"), PlatformLoanPct = N(r, "platform_loan_pct"),
                FundingNote = G(r, "funding_note"), MarketShare = G(r, "market_share"), Note = G(r, "note"),
            };
        }
        return m;
    }

    private static List<string> ReadSimple(string path, string col)
        => ReadCsv(path).Select(r => G(r, col)).Where(s => s.Length > 0).ToList();

    public static void WriteCsv(string path, IEnumerable<IReadOnlyDictionary<string, string>> rows, string[] cols)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols));
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", cols.Select(c => Q(r.TryGetValue(c, out var v) ? v : ""))));
        }
        File.WriteAllText(path, "\uFEFF" + sb, new UTF8Encoding(false));
    }

    private static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

file static class QvStrExt
{
    public static string Prefix(this string s, int n) => s.Length <= n ? s : s[..n];
}
