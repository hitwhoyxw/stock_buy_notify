using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.Core.Services.QualityValue;

public static class QvScan
{
    public static readonly string[] CaseCodes = ["000333", "000651", "601838", "600919", "601009", "600926", "600901", "601018", "600377", "600900", "601919", "601872"];
    private static readonly JsonSerializerOptions Jo = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<QvOutput> RunAsync(string dataDir, string mode, Action<string> log,
        EastMoneyClient em, QuoteService? quotes, AppConfig? appCfg = null, DataStore? store = null,
        IReadOnlyList<string>? onlyCodes = null, CancellationToken ct = default,
        bool notify = true, string? onlyType = null, bool throwOnNetwork = false)
    {
        var cfg = QvConfig.Load(dataDir);
        var validatedCycle=FinancialAccess.ConfiguredCycle(Path.Combine(dataDir,"cache"));
        EnsureTemplates(dataDir, log);
        var today = TradingCalendar.NowCn();
        var asOf = today.ToString("yyyy-MM-dd");
        mode = (mode ?? "recalc").ToLowerInvariant();
        var automaticFull=false;
        if(mode=="auto"){mode=InferMode(cfg,today,Path.Combine(dataDir,"quality_value"));automaticFull=mode=="full";}
        if(mode is "full" or "scan" or "offline")mode="recalc";
        if(mode is "daily" or "nov" && LoadLatest(Path.Combine(dataDir,"quality_value")) is not {Rows.Count:>0})
        {log("[S29] 无历史名单，仅本地重算；不会隐式初始化联网全扫");mode="recalc";}
        var financial=mode=="refresh-financials";
        var offline=mode is not ("daily" or "nov" or "refresh-financials");
        using var access=new FinancialAccess{Offline=offline,AllowFinancialNetwork=financial,AllowCorporateActionsNetwork=mode is "daily" or "nov",ThrowOnNetwork=throwOnNetwork,CycleMonths=validatedCycle};
        if(offline||financial)notify=false;
        log($"[S29] input mode={mode}; offline={offline}; financial refresh={financial}; cycle={access.CycleMonths} months; notify={notify}; no force-all");
        var data = new QvData(dataDir, em, log);
        data.LoadSidecars();

        if (mode is "daily" or "nov")
        {
            var prev = LoadLatest(data.OutDir);
            if (prev is { Rows.Count: > 0 })
                return await IncrementalAsync(prev, data, cfg, em, quotes, appCfg, store, mode, today, log, ct, notify).ConfigureAwait(false);
            log($"[S29] 无历史扫描结果，{mode} 改为全量重扫");
            mode = "full";
        }

        var rf = await data.LoadRfAsync(ct).ConfigureAwait(false);
        var uni = await data.LoadUniverseAsync(cfg, ct).ConfigureAwait(false);
        if (onlyCodes is { Count: > 0 })
        {
            var set = onlyCodes.Select(c => c.PadLeft(6, '0')).ToHashSet();
            uni = uni.Where(s => set.Contains(s.Code)).ToList();
            foreach (var c in set.Where(c => uni.All(s => s.Code != c)))
                uni.Add(new QvStock { Code = c, Name = c });
        }
        log($"[S29] 股票池 {uni.Count} 只（全A 含北交所={cfg.B("global.include_bj", true)}）");

        // 用业绩报表补名称/行业（clist 行业为空或仅有代码占位时）
        try
        {
            await data.ApplyYjbbMetaAsync(uni, new[] { today.Year, today.Year - 1, today.Year - 2 }, ct, addMissing: false).ConfigureAwait(false);
            log($"[S29] 业绩报表补全后 {uni.Count} 只，其中有行业 {uni.Count(s => !string.IsNullOrWhiteSpace(s.IndustryRaw))}");
        }
        catch (Exception ex) { log("[S29] 业绩报表补全失败：" + ex.Message); }

        // 先用行业分型（财务尚未到位，化学制品升级稍后）
        foreach (var s in uni) QvClassify.Apply(s, data, cfg);

        var unmapped = uni.Where(s => s.Type is "" or null && s.TypeSource.StartsWith("unmapped")).ToList();
        WriteUnmapped(data.OutDir, unmapped);
        log($"[S29] 分型：T1-T5 {uni.Count(s => s.Type is "T1" or "T2" or "T3" or "T4" or "T5")} · 排除 {uni.Count(s => s.Type == "EXCL")} · 未分型 {unmapped.Count}");

        var work = uni.Where(s => s.Type is "T1" or "T2" or "T3" or "T4" or "T5" || CaseCodes.Contains(s.Code) || QvBankValuation.SampleCodes.Contains(s.Code)).ToList();
        if (!string.IsNullOrWhiteSpace(onlyType))
        {
            work = work.Where(s => s.Type == onlyType || QvBankValuation.SampleCodes.Contains(s.Code)).ToList();
            log($"[S29] onlyType={onlyType}，工作集 {work.Count} 只");
        }
        // v1.5.4：同业基准保留全体已分型公司（含原排除/ST）；ST仍由QvScreen安全门槛排除，不以合格池选择同行。

        log($"[S29] 拉取 60 日成交额 {work.Count} 只…");
        await data.FillTurnoverAsync(work, cfg.I("global.liquidity_days", 60), ct).ConfigureAwait(false);
        log($"[S29] 公司质量工作集 {work.Count} 只；低流动/缺成交额不预先删除，独立限制价格提醒");

        // 年报窗口
        var thisYear = today.Month >= 5 ? today.Year - 1 : today.Year - 2;
        log(financial?"[S29] 显式更新：只下载缺失/已到期财报资源（接口可能整批返回所有报告期）":"[S29] 本地复用原始财报（忽略TTL，不联网、不因规则改变失效）");
        await data.FillFundamentalsAsync(work, 10, needBalance: true, ct).ConfigureAwait(false);
        await data.FillPricesAsync(work, quotes, ct).ConfigureAwait(false);

        // 化学制品升级等需要财务的二次分型
        foreach (var s in work.Where(x => x.IndustryRaw.Contains("化学制品") && x.Type == "T5" && !data.Overrides.ContainsKey(x.Code)))
            QvClassify.Apply(s, data, cfg);

        var bankPeers = (QvBankValuation.PeerBook?)null;
        var banks = work.Where(s => s.Type == "T2").ToList();
        if (banks.Count > 0)
        {
            log($"[S29] 计算 {banks.Count} 家银行 3年/5年历史 PB（东财日K年末收盘 / 年报普通股 BPS）…");
            await QvBankValuation.FillHistoryAsync(banks, data, log, ct).ConfigureAwait(false);
            bankPeers = QvBankValuation.BuildPeers(banks, data, cfg);
            log("[S29] 同业组样本：" + string.Join("，", bankPeers.Groups.Select(g => QvBankValuation.GroupLabel(g.Key) + " " + g.Value.Count)));
        }


        var relativePeers = QvIndustryRelative.Build(work, cfg, today, onlyCodes is not { Count: > 0 } && string.IsNullOrWhiteSpace(onlyType));
        var rows = new List<QvRow>();
        foreach (var s in work)
        {
            var row = new QvRow
            {
                Code = s.Code, Name = s.Name, Type = s.Type, TypeSource = s.TypeSource, Industry = s.IndustryRaw, AsOf = asOf,
                Price = s.Quote.Price, ValuationAdjustmentAsOf=data.ValuationAnchorFor(s),
            };
            if (string.IsNullOrWhiteSpace(s.Type) || s.TypeSource.StartsWith("unmapped"))
            {
                row.Pool = "excluded";
                row.ScreenSummary = "未分型，请把行业写入 mapping.csv 或在 override.csv 指定类型";
                row.Chain = BuildChain(s, row);
                rows.Add(row);
                continue;
            }
            QvScreen.Run(s, row, data, cfg, today);
            QvScore.Run(s, row, data, cfg, work);
            QvIndustryRelative.Apply(s, row, relativePeers, data, cfg, work);
            TagReverse(s, row);
            List<(string, double)>? closes = null;
            if (row.Pool is "qualified" or "watch" || CaseCodes.Contains(s.Code))
            {
                try { closes = await data.YearEndClosesAsync(s.Code, 10, ct).ConfigureAwait(false); } catch { }
            }
            QvValuation.Run(s, row, data, cfg, rf, closes, bankPeers);
            row.Price ??= s.Quote.Price;
            QvDataQuality.Apply(s, row, data, cfg);
            row.Flags = string.Join(",", row.FlagList);
            row.Chain = BuildChain(s, row);
            rows.Add(row);
        }

        await QvReview.ApplyAsync(uni, rows, data, cfg, data.OutDir, log, ct).ConfigureAwait(false);
        foreach (var r in rows) { QvIndustryRelative.HoldForKnownRisk(r); r.Flags = string.Join(",", r.FlagList ?? new List<string>()); }

        var outp = Pack(rows, uni, unmapped.Count, mode, asOf, data.OutDir, rf, data.RfSource);
        outp.IndustryRelativePeers = relativePeers.Peers;
        data.RetainInputs(work);
        data.AttachInputAudit(outp);
        await SaveAsync(outp, cfg, log, ct).ConfigureAwait(false);
        var alerts = new List<string>();
        if (notify)
            alerts = await QvNotify.SendAsync(outp, data, cfg, appCfg, store, log, ct).ConfigureAwait(false);
        else
            log("[S29] 本轮不发送外部提醒（banks/trace/cases-notify=false）");
        outp.Alerts = alerts;
        if(automaticFull)File.WriteAllText(Path.Combine(data.OutDir,$"full_{today:yyyy-MM}.ok"),"local-rule-recalc/no-financial-network "+today.ToString("O"));
        log($"[S29] 完成 {mode}：合格 {outp.Qualified} · 观察 {outp.Watch} · 排除 {outp.Excluded} · 击球 {outp.Strike} · 关注 {outp.Approach} · 提醒 {alerts.Count}");
        return outp;
    }

    private static string InferMode(QvConfig cfg, DateTime today, string outDir)
    {
        var months = cfg.Ints("global.full_rescan_months");
        if (months.Length == 0) months = [5, 9];
        var to = cfg.I("global.full_rescan_day_to", 10);
        if (months.Contains(today.Month) && today.Day <= to)
        {
            var marker = Path.Combine(outDir, $"full_{today:yyyy-MM}.ok");
            if (!File.Exists(marker)) return "full";
        }
        if (today.Month == 11 && today.Day <= cfg.I("global.nov_light_day_to", 10)) return "nov";
        return "daily";
    }

    private static async Task<QvOutput> IncrementalAsync(QvOutput prev, QvData data, QvConfig cfg, EastMoneyClient em,
        QuoteService? quotes, AppConfig? appCfg, DataStore? store, string mode, DateTime today, Action<string> log, CancellationToken ct, bool notify)
    {
        var rf = await data.LoadRfAsync(ct).ConfigureAwait(false);
        // 用现价刷新；财务不重算。除权：三档价格扣减期间现金分红、按送转调整。
        var codes = prev.Rows.Where(r => r.Pool is "qualified" or "watch").Select(r => r.Code).Distinct().ToList();
        var uni = await data.LoadUniverseAsync(cfg, ct).ConfigureAwait(false);
        var by = uni.ToDictionary(s => s.Code, StringComparer.Ordinal);
        var fake = codes.Select(c => by.TryGetValue(c, out var s) ? s : new QvStock { Code = c }).ToList();
        // 日更仅刷新监控执行资格，不重算公司质量；阈值与原成交额限制相同。
        await data.FillTurnoverAsync(fake, cfg.I("global.liquidity_days", 60), ct).ConfigureAwait(false);
        await data.FillPricesAsync(fake, quotes, ct).ConfigureAwait(false);
        var qmap = fake.ToDictionary(s => s.Code, StringComparer.Ordinal);
        var asOf = today.ToString("yyyy-MM-dd");
        DateTime.TryParse(prev.AsOf, out var prevDay);
        foreach (var r in prev.Rows)
        {
            if (qmap.TryGetValue(r.Code, out var s) && s.Quote.Price is > 0)
            {
                r.Price = s.Quote.Price;
                r.Name = string.IsNullOrEmpty(s.Name) ? r.Name : s.Name;
            }
            if (prevDay != default && r.PFair is > 0)
            {
                try
                {
                    var anchor=DateTime.TryParse(r.ValuationAdjustmentAsOf,out var knownAnchor)?knownAnchor:prevDay;
                    var (cash, fac) = await data.ExDivAdjustAsync(r.Code, anchor, today, ct).ConfigureAwait(false);
                    r.ValuationAdjustmentAsOf=today.ToString("yyyy-MM-dd");
                    if (cash > 0 || Math.Abs(fac - 1) > 1e-9)
                    {
                        r.PSafe = (r.PSafe - cash) / fac;
                        r.PFair = (r.PFair - cash) / fac;
                        r.POpt = (r.POpt - cash) / fac;
                    }
                }
                catch { }
            }
            // 轻量事件：ST
            if (r.Name.Contains("ST", StringComparison.OrdinalIgnoreCase) || r.Name.Contains('退'))
            {
                r.Pool = "excluded";
                AddFlag(r, "downgrade_warning");
                r.ScreenSummary += "；事件：名称出现 ST/退";
            }
        }

        // 公告关键词（合格+观察）
        foreach (var r in prev.Rows.Where(x => x.Pool is "qualified" or "watch").Take(400).ToList())
        {
            try
            {
                var anns = await data.FetchNoticesAsync(r.Code, 5, ct).ConfigureAwait(false);
                var keys = cfg.Strs("screen.penalty_keywords");
                var hits = anns.Where(a => keys.Any(k => a.Title.Contains(k, StringComparison.Ordinal))).ToList();
                if (hits.Count > 0)
                {
                    AddFlag(r, "red_flag");
                    AddFlag(r, "downgrade_warning");
                    r.Gates.Add(new QvGateItem { Name = "处罚/立案公告", Result = "soft", Detail = string.Join("；", hits.Select(h => h.Title).Take(3)), Source = "东财公告标题" });
                    var fraud = cfg.Strs("screen.fraud_keywords");
                    if (hits.Any(h => fraud.Any(k => h.Title.Contains(k))) || hits.Count >= 2)
                    {
                        r.Pool = "watch"; r.RedFlagUnreviewed = true;
                    }
                }
            }
            catch { }
            ct.ThrowIfCancellationRequested();
        }

        if (mode == "nov")
        {
            log("[S29] 11 月轻量更新：刷新 T5 cycle_ratio 与 T2 不良/拨备/资本");
            var need = prev.Rows.Where(r => r.Type is "T2" or "T5" && r.Pool is "qualified" or "watch").Select(r => r.Code).ToList();
            var stocks = need.Select(c => qmap.TryGetValue(c, out var s) ? s : new QvStock { Code = c }).ToList();
            await data.FillFundamentalsAsync(stocks, 10, false, ct).ConfigureAwait(false);
            foreach (var r in prev.Rows.Where(x => need.Contains(x.Code)))
            {
                if (!qmap.TryGetValue(r.Code, out var s) || s.Annuals.Count == 0) continue;
                if (r.Type == "T5")
                {
                    var cr = QvScreen.CycleRatio(s);
                    r.CycleRatio = cr;
                    if (cr > cfg.D("T5.cycle_high", 1.5))
                    {
                        r.Pool = "watch"; AddFlag(r, "cycle_high"); AddFlag(r, "downgrade_warning");
                    }
                }
                if (r.Type == "T2")
                {
                    var last = s.Annuals.Last();
                    var req = data.BankRequirement(r.Code).Requirement;
                    var warn = (last.Npl is > 0 && last.Npl > cfg.D("T2.npl_max", 0.015))
                               || (last.Cet1 is > 0 && last.Cet1 - req < 0);
                    if (warn) { AddFlag(r, "downgrade_warning"); r.ScreenSummary += "；11月轻量：资产质量/资本缓冲预警（不因统一9%线降级）"; }
                }
            }
        }

        foreach (var r in prev.Rows)
        {
            // 重算 zone（财务价格已调整）
            QvValuationZoneOnly(r, cfg);
            if (qmap.TryGetValue(r.Code, out var monitoringStock)) QvMonitoring.Apply(monitoringStock.Quote, r, cfg);
            r.Flags = string.Join(",", r.FlagList);
            r.AsOf = asOf;
        }
        var reviewStocks = new List<QvStock>();
        foreach (var x in prev.Rows.Where(r => r.Zone == "strike"))
        {
            if (qmap.TryGetValue(x.Code, out var s))
            {
                if (string.IsNullOrWhiteSpace(s.Type)) s.Type = x.Type;
                if (string.IsNullOrWhiteSpace(s.IndustryRaw)) s.IndustryRaw = x.Industry;
                if (string.IsNullOrWhiteSpace(s.Name)) s.Name = x.Name;
                reviewStocks.Add(s);
            }
            else
                reviewStocks.Add(new QvStock { Code = x.Code, Name = x.Name, Type = x.Type, IndustryRaw = x.Industry });
        }
        foreach(var s in reviewStocks){s.Forecasts.Clear();s.Notices.Clear();}
        await QvReview.ApplyAsync(reviewStocks, prev.Rows, data, cfg, data.OutDir, log, ct).ConfigureAwait(false);
        foreach (var r in prev.Rows) { QvIndustryRelative.HoldForKnownRisk(r); r.Flags = string.Join(",", r.FlagList ?? new List<string>()); }
        var outp = Pack(prev.Rows, uni, prev.Unmapped, mode, asOf, data.OutDir, rf, data.RfSource);
        outp.IndustryRelativePeers = prev.IndustryRelativePeers;
        data.RetainInputs(fake);
        data.AttachInputAudit(outp);
        await SaveAsync(outp, cfg, log, ct).ConfigureAwait(false);
        outp.Alerts = notify ? await QvNotify.SendAsync(outp, data, cfg, appCfg, store, log, ct).ConfigureAwait(false) : new List<string>();
        if (!notify) log("[S29] notify=false：日更亦禁止外部提醒/通知去重写入");
        log($"[S29] {mode} 完成：合格 {outp.Qualified} 击球 {outp.Strike} 关注 {outp.Approach} 提醒 {outp.Alerts.Count}");
        return outp;
    }

    private static void QvValuationZoneOnly(QvRow r, QvConfig cfg)
    {
        if (r.Pool != "qualified" || r.Price is not > 0 || r.PSafe is not > 0 || r.PFair is not > 0 || r.POpt is not > 0)
        { r.Zone = ""; return; }
        var p = r.Price.Value;
        var edge = cfg.D("global.approach_fair_ratio", 0.9) * r.PFair.Value;
        r.Zone = p <= r.PSafe ? "strike" : p <= edge ? "approach" : p <= r.POpt ? "fair" : "optimistic";
        // v1.1：不因 cycle_high 改写 zone
    }

    private static QvOutput Pack(List<QvRow> rows, List<QvStock> uni, int unmapped, string mode, string asOf, string outDir, double rf, string rfSrc)
    {
        string Z(QvRow r) => r.Zone ?? "";
        var o = new QvOutput
        {
            Mode = mode, AsOf = asOf, OutDir = outDir, Rf = rf, RfSource = rfSrc,
            Universe = uni.Count, Unmapped = unmapped,
            Rows = rows.OrderBy(r => r.Pool == "qualified" ? 0 : r.Pool == "watch" ? 1 : 2)
                .ThenBy(r => Z(r) == "strike" ? 0 : Z(r) == "approach" ? 1 : 2)
                .ThenByDescending(r => r.QualityScore ?? 0).ToList(),
        };
        o.Qualified = rows.Count(r => r.Pool == "qualified");
        o.Watch = rows.Count(r => r.Pool == "watch");
        o.Excluded = rows.Count(r => r.Pool == "excluded");
        o.Classified = rows.Count;
        o.Strike = rows.Count(r => r.Zone == "strike");
        o.Approach = rows.Count(r => r.Zone == "approach");
        o.Fair = rows.Count(r => r.Zone == "fair");
        o.Optimistic = rows.Count(r => r.Zone == "optimistic");
        o.CoreQuality = rows.Count(r => r.Pool == "qualified" && r.QualityScore >= 85);
        foreach (var t in new[] { "T1", "T2", "T3", "T4", "T5" }) o.ByType[t] = rows.Count(r => r.Type == t);
        foreach (var p in new[] { "qualified", "watch", "excluded" }) o.ByPool[p] = rows.Count(r => r.Pool == p);
        foreach (var z in new[] { "strike", "approach", "fair", "optimistic" }) o.ByZone[z] = rows.Count(r => r.Zone == z);
        o.Gaps = rows.Select(r => r.DataGaps).Where(s => !string.IsNullOrWhiteSpace(s))
            .SelectMany(s => s.Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .GroupBy(s => s).OrderByDescending(g => g.Count()).Take(30)
            .Select(g => $"{g.Key} ×{g.Count()}").ToList();
        return o;
    }

    private static async Task SaveAsync(QvOutput o, QvConfig cfg, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(o.OutDir);
        var cols = new[] { "code","name","type","type_source","industry","pool","zone","flags","quality_score",
            "price","p_safe","p_fair","p_opt","pe_fair","pb_fair","dy_fair","dispersion","cycle_ratio","as_of","data_gaps","screen","valuation" };
        var rows = o.Rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["code"]=r.Code,["name"]=r.Name,["type"]=r.Type,["type_source"]=r.TypeSource,["industry"]=r.Industry,
            ["pool"]=r.Pool,["zone"]=r.Zone,["flags"]=r.Flags,["quality_score"]=F(r.QualityScore),
            ["price"]=F(r.Price),["p_safe"]=F(r.PSafe),["p_fair"]=F(r.PFair),["p_opt"]=F(r.POpt),
            ["pe_fair"]=F(r.PeFair),["pb_fair"]=F(r.PbFair),["dy_fair"]=F(r.DyFair,4),["dispersion"]=F(r.Dispersion,3),
            ["cycle_ratio"]=F(r.CycleRatio,3),["as_of"]=r.AsOf,["data_gaps"]=r.DataGaps,["screen"]=r.ScreenSummary,["valuation"]=r.ValuationDetail,
        });
        cols = new[] { "code","name","type","type_source","industry","pool","zone","flags","quality_score","quality_label",
            "data_coverage","data_grade","data_confidence","signal_confidence","val_confidence","t4_channel","capital_buffer","reg_requirement","capital_unknown","div_status","pb_current","pb_model","pb_peer","pb_fair_final","re_bank","g_long",
            "price","p_safe","p_fair","p_opt","pe_fair","pb_fair","dy_fair","dispersion","cycle_ratio","as_of","data_gaps","manual_needed","reverse_note","screen","valuation","quality_admission_channel","relative_group","relative_composite","relative_decision","relative_bypassed","price_monitoring_eligible","monitoring_turnover60","monitoring_reason" };
        rows = o.Rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["code"]=r.Code,["name"]=r.Name,["type"]=r.Type,["type_source"]=r.TypeSource,["industry"]=r.Industry,
            ["pool"]=r.Pool,["zone"]=r.Zone,["flags"]=r.Flags,["quality_score"]=F(r.QualityScore),["quality_label"]=r.QualityLabel??"",
            ["data_coverage"]=F(r.DataCoverage,3),["data_grade"]=r.DataQualityGrade??"",["data_confidence"]=F(r.DataConfidenceScore,1),["signal_confidence"]=F(r.SignalConfidence,1),["val_confidence"]=r.ValuationConfidence??"",["t4_channel"]=r.T4Channel??"",
            ["capital_buffer"]=F(r.CapitalBuffer,4),["reg_requirement"]=F(r.RegulatoryRequirement,4),["capital_unknown"]=r.CapitalRequirementUnknown?"true":"",["div_status"]=r.DividendStatus??"",["pb_current"]=F(r.PbCurrent,3),["pb_model"]=F(r.PbModel,3),["pb_peer"]=F(r.PbPeer,3),["pb_fair_final"]=F(r.PbFairFinal,3),["re_bank"]=F(r.ReBank,4),["g_long"]=F(r.GLong,4),
            ["price"]=F(r.Price),["p_safe"]=F(r.PSafe),["p_fair"]=F(r.PFair),["p_opt"]=F(r.POpt),
            ["pe_fair"]=F(r.PeFair),["pb_fair"]=F(r.PbFair),["dy_fair"]=F(r.DyFair,4),["dispersion"]=F(r.Dispersion,3),
            ["cycle_ratio"]=F(r.CycleRatio,3),["as_of"]=r.AsOf,["data_gaps"]=r.DataGaps,["manual_needed"]=r.ManualNeeded??"",
            ["reverse_note"]=r.ReverseNote??"",
            ["screen"]=r.ScreenSummary,["valuation"]=r.ValuationDetail,
            ["quality_admission_channel"]=r.QualityAdmissionChannel,["relative_group"]=r.IndustryRelativeGroup,["relative_composite"]=F(r.IndustryRelativeComposite,2),["relative_decision"]=r.IndustryRelativeDecision,["relative_bypassed"]=r.IndustryRelativeBypassed,
            ["price_monitoring_eligible"]=r.PriceMonitoringEligible?.ToString()??"",["monitoring_turnover60"]=F(r.MonitoringTurnover60),["monitoring_reason"]=r.MonitoringReason,
        });
        // 完整同业原始输入独立保存：可复算分位，无需在每只股票链路重复数百行。
        if (o.IndustryRelativePeers.Count > 0)
        {
            var peerPath = Path.Combine(o.OutDir, $"industry_relative_peers_{o.AsOf}.json");
            if (File.Exists(peerPath)) peerPath = Path.Combine(o.OutDir, $"industry_relative_peers_{o.AsOf}_{DateTime.Now:HHmmssfff}.json");
            await File.WriteAllTextAsync(peerPath, System.Text.Json.JsonSerializer.Serialize(o.IndustryRelativePeers, Jo), ct).ConfigureAwait(false);
        }
        o.LatestCsv = Path.Combine(o.OutDir, "pool_latest.csv");
        QvData.WriteCsv(o.LatestCsv, rows, cols);
        var poolDated = Path.Combine(o.OutDir, $"pool_{o.AsOf}.csv");
        if (File.Exists(poolDated))
            QvData.WriteCsv(Path.Combine(o.OutDir, $"pool_{o.AsOf}_{DateTime.Now:HHmmss}.csv"), rows, cols);
        else
            QvData.WriteCsv(poolDated, rows, cols);
        var json = Path.Combine(o.OutDir, "state.json");
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(o, Jo), ct).ConfigureAwait(false);
        var stateDated = Path.Combine(o.OutDir, $"state_{o.AsOf}.json");
        if (File.Exists(stateDated))
            await File.WriteAllTextAsync(Path.Combine(o.OutDir, $"state_{o.AsOf}_{DateTime.Now:HHmmss}.json"), JsonSerializer.Serialize(o, Jo), ct).ConfigureAwait(false);
        else
            await File.WriteAllTextAsync(stateDated, JsonSerializer.Serialize(o, Jo), ct).ConfigureAwait(false);
        var md = BuildReport(o);
        o.ReportPath = Path.Combine(o.OutDir, $"report_{o.AsOf}.md");
        if (File.Exists(o.ReportPath))
            o.ReportPath = Path.Combine(o.OutDir, $"report_{o.AsOf}_{DateTime.Now:HHmmss}.md");
        await File.WriteAllTextAsync(o.ReportPath, md, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(o.OutDir, "report_latest.md"), md, ct).ConfigureAwait(false);
        if (o.Mode == "full") File.WriteAllText(Path.Combine(o.OutDir, $"full_{o.AsOf[..7]}.ok"), o.AsOf);
        cfg.CommitRunSnapshot(o.OutDir,new{SourceData=o.InputSources,RuleExecutionAsOf=o.AsOf,Mode=o.Mode});
        log($"[S29] 已写入 {o.OutDir}");
    }

    public static string BuildReport(QvOutput o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# S29 优质股击球区 v1.2 P0 · {o.AsOf}（{o.Mode}）").AppendLine();
        sb.AppendLine($"> 无风险利率：10 年期国债 {o.Rf:P2}（{o.RfSource}）；银行 Re=Rf+ERP+R_bank（有类型下限，不再用统一 8% 下限）。参数见 config/quality_value/*.yaml。P0 不含周期正常化/多源分类与分红补全/逐家资本核验/回测。数据仅供研究，不构成投资建议。").AppendLine();
        sb.AppendLine($"- 股票池 {o.Universe} 只，进入流程 {o.Classified}，未分型 {o.Unmapped}");
        sb.AppendLine($"- 池：合格 **{o.Qualified}** / 观察 {o.Watch} / 排除 {o.Excluded} / 核心优质（≥85）**{o.CoreQuality}**");
        sb.AppendLine($"- 价格区（仅合格）：击球 **{o.Strike}** / 关注 {o.Approach} / 合理 {o.Fair} / 乐观 {o.Optimistic}");
        var qType = string.Join(" · ", new[] { "T1", "T2", "T3", "T4", "T5" }.Select(tp => tp + "合格 " + o.Rows.Count(r => r.Type == tp && r.Pool == "qualified")));
        sb.AppendLine($"- 分型（扫描集）：{string.Join(" · ", o.ByType.Select(kv => kv.Key + " " + kv.Value))}");
        sb.AppendLine($"- 合格分布：{qType}").AppendLine();
        void Table(string title, IEnumerable<QvRow> list, int take = 40)
        {
            var l = list.ToList(); if (l.Count == 0) return;
            sb.AppendLine($"## {title}（{l.Count}）").AppendLine();
            sb.AppendLine("| 代码 | 名称 | 类型 | 评分 | 完整度 | 现价 | 安全边际价 | 合理价 | 乐观价 | 标记 |");
            sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---|");
            foreach (var r in l.Take(take))
                sb.AppendLine($"| {r.Code} | {r.Name} | {r.Type} | {r.QualityScore:F1} | {r.DataCoverage:P0} | {r.Price:F2} | {r.PSafe:F2} | {r.PFair:F2} | {r.POpt:F2} | {r.Flags} |");
            if (l.Count > take) sb.AppendLine($"| … | 另 {l.Count - take} 只见 pool_latest.csv | | | | | | | |");
            sb.AppendLine();
        }
        Table("击球区", o.Rows.Where(r => r.Zone == "strike"));
        Table("关注区", o.Rows.Where(r => r.Zone == "approach"));
        Table("合格池（其他）", o.Rows.Where(r => r.Pool == "qualified" && r.Zone is not "strike" and not "approach"));
        sb.AppendLine("## 验证用例链路").AppendLine();
        foreach (var c in CaseCodes)
        {
            var r = o.Rows.FirstOrDefault(x => x.Code == c);
            if (r is null) { sb.AppendLine($"- {c}：本轮未覆盖"); continue; }
            sb.AppendLine($"### {r.Code} {r.Name}").AppendLine().AppendLine("```").AppendLine(r.Chain).AppendLine("```").AppendLine();
        }
        sb.AppendLine("## 反向测试（由本轮数据筛选，非硬编码代码）").AppendLine();
        sb.AppendLine(BuildReverseSection(o));
        if (o.Alerts.Count > 0)
        {
            sb.AppendLine("## 今日提醒").AppendLine();
            foreach (var a in o.Alerts) sb.AppendLine("- " + a);
            sb.AppendLine();
        }
        AppendV12Stats(sb, o);
        WriteTraceability(o);
        return sb.ToString();
    }

    public static string BuildChain(QvStock s, QvRow r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.Code} {r.Name}  {r.Type}（{r.TypeSource}）  行业 {r.Industry}");
        sb.AppendLine($"上市 {s.ListDate}  年报 {s.Annuals.Count} 份  现价 {r.Price:F2}（{s.Quote.Source} {s.Quote.AsOf}）");
        if (s.Annuals.Count > 0)
        {
            sb.AppendLine("年报摘要（近年）：");
            foreach (var a in s.Annuals.TakeLast(6))
                sb.AppendLine($"  {a.Year} 营收 {a.Revenue / 1e8:F1}亿  归母 {a.Np / 1e8:F1}亿  扣非 {a.DeductNp / 1e8:F1}亿  ROE {a.Roe:P1}  净利率 {a.NetMargin:P1}  有息负债率 {a.InterestDebtRatio:P1}  OCF/NP {a.OcfToNp:F2}  分红率 {a.Payout:P0}"
                              + (a.Npl is not null ? $"  不良 {a.Npl:P2}  拨备 {a.ProvisionCov:P0}  CET1 {a.Cet1:P2}" : ""));
        }
        sb.AppendLine("排雷/门槛：");
        foreach (var g in r.Gates) sb.AppendLine($"  [{g.Result}] {g.Name}：{g.Detail}" + (g.Source.Length > 0 ? $"  ← {g.Source}" : ""));
        sb.AppendLine("结论：" + r.ScreenSummary);
        if (r.SubScores.Count > 0)
        {
            sb.AppendLine($"评分 {r.QualityScore:F1}：");
            foreach (var sub in r.SubScores) sb.AppendLine($"  {sub.Name} 权重 {sub.Weight:F0} 得分 {sub.Score:F0}  {sub.Detail}");
        }
        if (r.Methods.Count > 0)
        {
            sb.AppendLine("估值方法：");
            foreach (var m in r.Methods) sb.AppendLine($"  [{m.Role}] {m.Name}  合理 {m.Fair:F2}  安全 {m.Safe:F2}  乐观 {m.Opt:F2}  {m.Detail}");
        }
        sb.AppendLine($"三档价格：安全边际价 {r.PSafe:F2} / 合理价 {r.PFair:F2} / 乐观价 {r.POpt:F2}  分歧度 {r.Dispersion:P0}  估值可信度 {r.ValuationConfidence}");
        if (r.PeFair is not null || r.PbFair is not null || r.DyFair is not null)
            sb.AppendLine($"三档倍数：PE {r.PeSafe:F1}/{r.PeFair:F1}/{r.PeOpt:F1}  PB {r.PbSafe:F2}/{r.PbFair:F2}/{r.PbOpt:F2}  股息率 {r.DySafe:P1}/{r.DyFair:P1}/{r.DyOpt:P1}");
        if (r.PFair is > 0 && r.Price is > 0)
            sb.AppendLine($"现价相对合理价：{r.Price / r.PFair:P1}（仓位指示 {r.PositionPct:P0}）");
        sb.AppendLine($"数据完整度 {r.DataCoverage:P0}  {r.CoverageDetail}");
        sb.AppendLine($"状态机：pool={r.Pool}  zone={r.Zone}  flags={r.Flags}（三层相互独立）  质量档 {r.QualityLabel}");
        if (r.Type == "T2")
            sb.AppendLine($"银行资本：CET1 {r.Cet1Latest:P2}  监管要求 {r.RegulatoryRequirement:P2}  缓冲 {r.CapitalBuffer:P2}");
        if (!string.IsNullOrEmpty(r.T4Channel)) sb.AppendLine("T4 通道：" + r.T4Channel);
        if (!string.IsNullOrEmpty(r.DataGaps)) sb.AppendLine("数据缺口：" + r.DataGaps);
        if (!string.IsNullOrEmpty(r.ManualNeeded)) sb.AppendLine("需人工补录/复核：" + r.ManualNeeded);
        if (!string.IsNullOrEmpty(r.ReverseNote)) sb.AppendLine("反向测试标记：" + r.ReverseNote);
        if (!string.IsNullOrEmpty(r.ValuationDetail)) sb.AppendLine(r.ValuationDetail);
        sb.AppendLine($"公司质量准入通道：{r.QualityAdmissionChannel}（质量与价格区间独立）");
        sb.AppendLine($"价格监控资格：{r.PriceMonitoringEligible}；{r.MonitoringReason}；成交额/行情日期 {r.MonitoringAsOf}");
        if (r.IndustryRelativeDetail.Length > 0) sb.AppendLine(r.IndustryRelativeDetail);
        return sb.ToString().TrimEnd();
    }

    public static string BuildReverseSection(QvOutput o)
    {
        var sb = new StringBuilder();
        void Block(string title, string tag, string empty, Func<QvRow, bool>? extra = null)
        {
            sb.AppendLine("### " + title);
            var list = o.Rows.Where(r => (r.ReverseNote ?? "").Contains(tag) && (extra == null || extra(r))).Take(3).ToList();
            if (list.Count == 0) { sb.AppendLine(empty).AppendLine(); return; }
            foreach (var r in list)
            {
                sb.AppendLine($"- {r.Code} {r.Name}  pool={r.Pool}  zone={r.Zone}  评分 {r.QualityScore:F1}  {r.ReverseNote}");
                sb.AppendLine().AppendLine("```").AppendLine(r.Chain).AppendLine("```").AppendLine();
            }
        }
        Block("1. 高 ROE 但有息负债持续快速增加（制造业）", "R1", "本轮扫描集中未找到同时满足「5 年 ROE 均值≥15% 且有息负债率上升≥10 个百分点」的 T1 样本。");
        sb.AppendLine("### 2. 货币资金很多但大部分受限");
        sb.AppendLine("东财主营指标与资产负债表未提供受限资金比例，本项无法用公开接口自动证实「大部分受限」，不编造该比例。存贷双高且待补录受限资金的 T1 仅作为「需人工核验受限资金」的代理候选：");
        var c2 = o.Rows.Where(r => (r.ReverseNote ?? "").Contains("R2") || ((r.Flags ?? "").Contains("red_flag") && (r.ManualNeeded ?? "").Contains("受限资金") && r.Type == "T1")).Take(3).ToList();
        if (c2.Count == 0) sb.AppendLine("本轮无代理候选。").AppendLine();
        else foreach (var r in c2)
            sb.AppendLine($"- [代理，非证实受限] {r.Code} {r.Name}  pool={r.Pool}").AppendLine().AppendLine("```").AppendLine(r.Chain).AppendLine("```").AppendLine();
        Block("3. 净利润增长但经营现金流长期为负", "R3", "本轮扫描集中未找到「净利润 CAGR>10% 且近 5 年至少 3 年经营现金流为负」的样本。");
        Block("4. 周期高峰利润很高但正常化利润较低", "R4", "本轮无 cycle_ratio≥1.8 的 T5 样本。");
        return sb.ToString();
    }

    private static void TagReverse(QvStock s, QvRow row)
    {
        var y5 = s.Annuals.TakeLast(5).ToList();
        if (y5.Count < 3) return;
        var notes = new List<string>();
        var roe = QvMath.Mean(y5.Select(x => x.Roe));
        var d0 = y5[0].InterestDebtRatio;
        var d1 = y5[^1].InterestDebtRatio;
        if (row.Type == "T1" && roe is >= 0.15 && d0 is not null && d1 is not null && d1.Value - d0.Value >= 0.10)
            notes.Add($"R1:5年ROE {roe:P1}，有息负债率 {d0:P1}→{d1:P1}");
        if (row.Type == "T1" && (row.Flags ?? "").Contains("red_flag") && (row.ManualNeeded ?? "").Contains("受限资金"))
            notes.Add("R2:存贷双高/高货币资金，受限资金待补录（代理）");
        var ocfNeg = y5.Count(a => a.Ocf is < 0);
        var cagr = QvMath.Cagr(y5[0].Np, y5[^1].Np, y5.Count - 1);
        if (cagr is > 0.10 && ocfNeg >= 3)
            notes.Add($"R3:净利润CAGR {cagr:P1}，近{y5.Count}年OCF为负 {ocfNeg} 年");
        if (row.Type == "T5" && row.CycleRatio is >= 1.8)
            notes.Add($"R4:cycle_ratio={row.CycleRatio:F2}（当前利润显著高于十年均值）");
        if (notes.Count > 0) row.ReverseNote = string.Join("；", notes);
    }

        public static QvOutput? LoadLatest(string outDir)
    {
        var p = Path.Combine(outDir, "state.json");
        if (!File.Exists(p)) return null;
        try
        {
            var o = JsonSerializer.Deserialize<QvOutput>(File.ReadAllText(p), Jo);
            if (o?.Rows is not null)
            {
                foreach (var r in o.Rows)
                {
                    if (r.FlagList.Count == 0 && !string.IsNullOrEmpty(r.Flags))
                        r.FlagList = r.Flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }
            }
            return o;
        }
        catch { return null; }
    }

    public static void EnsureTemplates(string dataDir, Action<string>? log = null)
    {
        var dir = Path.Combine(dataDir, "quality_value");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "supplement"));
        var cfgPath = Path.Combine(dataDir, QvConfig.FileName);
        // 配置 / 映射等：仅当缺失时从与本程序一起发布的模板或内嵌默认复制；这里由安装步骤放下文件。
        // 若配置缺失则抛出明确错误（调用方应先 Copy 模板）。
        if (QvConfig.FindYamlDir(dataDir) is null && !File.Exists(cfgPath))
            log?.Invoke($"[S29] 缺少 config/quality_value/*.yaml 且缺少 {cfgPath}");
    }

    private static void WriteUnmapped(string outDir, List<QvStock> list)
    {
        var groups = list.GroupBy(s => string.IsNullOrEmpty(s.IndustryRaw) ? "(空)" : s.IndustryRaw)
            .OrderByDescending(g => g.Count());
        var rows = groups.Select(g => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["industry"] = g.Key,
            ["sample_codes"] = string.Join(" ", g.Take(8).Select(x => x.Code)),
            ["sample_names"] = string.Join(" ", g.Take(8).Select(x => x.Name)),
            ["count"] = g.Count().ToString(),
        });
        QvData.WriteCsv(Path.Combine(outDir, "unmapped.csv"), rows, ["industry", "sample_codes", "sample_names", "count"]);
    }

    private static void AddFlag(QvRow r, string f) { if (!r.FlagList.Contains(f)) r.FlagList.Add(f); }
    private static string F(double? v, int d = 2) => v is null || double.IsNaN(v.Value) ? "" : Math.Round(v.Value, d).ToString("0." + new string('#', d), CultureInfo.InvariantCulture);

    private static void AppendV12Stats(StringBuilder sb, QvOutput o)
    {
        var rows = o.Rows;
        var uni = o.Universe;
        var classified = rows.Count(r => r.Type is "T1" or "T2" or "T3" or "T4" or "T5");
        var unclass = o.Unmapped;
        sb.AppendLine("## 8. 结果统计（v1.2 P0）").AppendLine();
        sb.AppendLine("### 8.1 股票池统计").AppendLine();
        sb.AppendLine($"- A股总数（本轮股票池）：{uni}");
        sb.AppendLine($"- 排雷/进入流程数量：{o.Classified}");
        sb.AppendLine($"- 已分类数量：{classified}");
        sb.AppendLine($"- 未分类数量：{unclass}");
        sb.AppendLine($"- 合格：{o.Qualified}；核心（评分≥85）：{o.CoreQuality}；观察：{o.Watch}；剔除：{o.Excluded}");
        var cov = uni > 0 ? classified / (double)uni : 0;
        if (cov < 0.95)
            sb.AppendLine($"> 分类覆盖率 {cov:P1} 低于 95%，数据覆盖不足（P1 多源分类未实施，此为基线）。").AppendLine();

        sb.AppendLine("### 8.2 行业分布与周期股占比（基线，P1 正常化未实施）").AppendLine();
        var q = rows.Where(r => r.Pool == "qualified").ToList();
        var ind = q.GroupBy(r => string.IsNullOrWhiteSpace(r.Industry) ? "(空)" : r.Industry)
            .OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine("| 行业 | 合格 | 合格占比 | 核心 | 击球区 |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var g in ind.Take(30))
        {
            var n = g.Count();
            sb.AppendLine($"| {g.Key} | {n} | {(q.Count==0?0:n/(double)q.Count):P1} | {g.Count(x => x.QualityScore>=85)} | {g.Count(x => x.Zone=="strike")} |");
        }
        var t5q = q.Count(r => r.Type == "T5");
        var t5c = rows.Count(r => r.Pool == "qualified" && r.QualityScore >= 85 && r.Type == "T5");
        var cycHi = rows.Count(r => r.FlagList.Contains("cycle_high"));
        var cycTop = rows.Count(r => r.FlagList.Contains("cycle_top_risk"));
        sb.AppendLine();
        sb.AppendLine($"- 周期股（以 T5 分型为代理，**非** P1 正常化后的周期识别）合格 {t5q}/{q.Count}（{(q.Count==0?0:t5q/(double)q.Count):P1}），目标上限 40%；核心池 T5 {t5c}/{o.CoreQuality}（{(o.CoreQuality==0?0:t5c/(double)Math.Max(1,o.CoreQuality)):P1}），目标上限 35%。");
        sb.AppendLine($"- 已有 `cycle_high` {cycHi} 只；`cycle_top_risk` {cycTop} 只（P1 未上线完整周期位置，后者现为 0 属基线）。");
        if (q.Count > 0 && t5q / (double)q.Count > 0.40)
            sb.AppendLine("- 合格池周期股占比仍高于 40%。按规范不在本轮用机械剔除凑比例，也不改个股基本面评分。待 P1 正常化盈利与周期位置识别后复测。");
        sb.AppendLine();

        sb.AppendLine("### 8.3 数据质量统计").AppendLine();
        var divOk = rows.Count(r => r.DividendStatus is "dividend_confirmed");
        var divPart = rows.Count(r => r.DividendStatus is "dividend_partial");
        var divMiss = rows.Count(r => r.FlagList.Contains("dividend_missing") || r.DividendStatus is "dividend_missing");
        var finOk = rows.Count(r => !string.IsNullOrWhiteSpace(r.Type) && !(r.DataGaps??"").Contains("财务"));
        var proxy = rows.Count(r => r.FlagList.Contains("proxy_data"));
        var uncert = rows.Count(r => r.FlagList.Contains("valuation_uncertain"));
        var outlier = rows.Count(r => r.FlagList.Contains("valuation_outlier"));
        var dqw = rows.Count(r => r.FlagList.Contains("data_quality_warning"));
        var gradeN = rows.Where(r => !string.IsNullOrWhiteSpace(r.DataQualityGrade)).GroupBy(r => r.DataQualityGrade).ToDictionary(g => g.Key, g => g.Count());
        sb.AppendLine($"- 分类完整率：{cov:P1}（已分类 {classified} / 池 {uni}）");
        sb.AppendLine($"- 分红数据：confirmed {divOk}，partial {divPart}，missing {divMiss}；完整率（confirmed/扫描行）{(rows.Count==0?0:divOk/(double)rows.Count):P1}");
        sb.AppendLine($"- 财务数据相对完整（扫描行、无“财务”缺口标记）：{finOk}/{rows.Count}");
        sb.AppendLine($"- 代理数据使用：{proxy}（{ (rows.Count==0?0:proxy/(double)rows.Count):P1}）");
        sb.AppendLine($"- 估值不确定 {uncert}，估值异常 {outlier}，数据质量预警 {dqw}");
        sb.AppendLine($"- 数据质量等级分布：" + string.Join("，", gradeN.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)));
        sb.AppendLine();

        var banks = rows.Where(r => r.Type == "T2").OrderBy(r => r.Code).ToList();
        sb.AppendLine($"### 8.4 银行估值专项（{banks.Count}）").AppendLine();
        sb.AppendLine("| 代码 | 名称 | 类型 | 当前PB | 模型PB | 同业PB | 最终合理PB | ROE | Re | g | CET1 | 监管要求 | 缓冲 | 状态 | 现价 | 合理价 | 池/区 | 标记 |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---|---|");
        foreach (var r in banks)
        {
            var reqTxt = r.CapitalRequirementUnknown ? (r.RegulatoryRequirement is null ? "未核验" : $"{r.RegulatoryRequirement:P2}（未核验）") : (r.RegulatoryRequirement is null ? "" : r.RegulatoryRequirement.Value.ToString("P2"));
            var bufTxt = r.CapitalBuffer is null ? "" : r.CapitalBuffer.Value.ToString("P2") + (r.CapitalRequirementUnknown ? "（参考）" : "");
            sb.AppendLine($"| {r.Code} | {r.Name} | {r.BankType} | {r.PbCurrent:F2} | {r.PbModel:F2} | {r.PbPeer:F2} | {r.PbFairFinal:F2} | {r.RoeSus:P1} | {r.ReBank:P1} | {r.GLong:P1} | {r.Cet1Latest:P2} | {reqTxt} | {bufTxt} | {r.ValuationStatus} | {r.Price:F2} | {r.PFair:F2} | {r.Pool}/{r.Zone} | {r.Flags} |");
        }
        sb.AppendLine();
        foreach (var c in QvBankValuation.SampleCodes)
        {
            var r = banks.FirstOrDefault(x => x.Code == c) ?? rows.FirstOrDefault(x => x.Code == c);
            if (r is null) continue;
            sb.AppendLine($"#### 样本分项 {r.Code} {r.Name}").AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(r.BankValuationBreakdown.Length > 0 ? r.BankValuationBreakdown : r.ValuationDetail);
            sb.AppendLine("Re 分项：" + r.ReBreakdown);
            sb.AppendLine($"P_safe/P_fair/P_opt = {r.PSafe:F2} / {r.PFair:F2} / {r.POpt:F2}，现价 {r.Price:F2}");
            sb.AppendLine("```").AppendLine();
        }

        var strike = rows.Where(r => r.Pool == "qualified" && r.Zone == "strike").ToList();
        var blocked = strike.Where(r => r.StrongAlertBlocked).ToList();
        var strong = strike.Where(r => !r.StrongAlertBlocked).ToList();
        sb.AppendLine("### 强提醒约束").AppendLine();
        sb.AppendLine($"- 合格且价格落入击球区：{strike.Count}；其中强提醒可通过 {strong.Count}，被前置条件阻断 {blocked.Count}（阻断 ≠ 取消合格或价格区）。");
        foreach (var g in blocked.GroupBy(r => r.StrongAlertBlockReason).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  - {g.Count()} 只：{g.Key}");
        if (strong.Count > 0)
            sb.AppendLine("- 可通过强提醒：" + string.Join("、", strong.Select(r => r.Code + r.Name)));
        sb.AppendLine();
    }

    public static void WriteTraceability(QvOutput o)
    {
        var cand = o.Rows.Where(r => !string.IsNullOrWhiteSpace(r.Type) && r.Type.StartsWith("T")).ToList();
        if (cand.Count == 0) return;
        var seed = 0;
        foreach (var ch in (o.AsOf ?? "").Where(char.IsDigit)) seed = seed * 10 + (ch - '0');
        if (seed == 0) seed = 20261113;
        var rng = new Random(seed);
        var n = Math.Min(30, cand.Count);
        var pick = cand.OrderBy(_ => rng.Next()).Take(n).OrderBy(r => r.Code).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# 数据可追溯抽查（至少 30 只）· {o.AsOf} · seed={seed}").AppendLine();
        sb.AppendLine("规则：任何一个估值结果都应能追溯到原始数据或明确的代理计算方法。本表抽自本轮扫描行。").AppendLine();
        sb.AppendLine("| 代码 | 名称 | 类型 | 数据等级 | 数据置信 | 信号置信 | 分红状态 | 来源/日期 | 缺失/代理 | 估值所用字段 |");
        sb.AppendLine("|---|---|---|---|---:|---:|---|---|---|---|");
        foreach (var r in pick)
        {
            var src = $"行情/财务见 ValuationDetail；评分 {r.QualityScore:F1}";
            var miss = (r.DataGaps ?? "") + (string.IsNullOrWhiteSpace(r.DataQualityDetail) ? "" : " | " + r.DataQualityDetail);
            var fields = r.Type == "T2"
                ? $"PB_model/PB_peer/PB_fair, ROE_sus, Re, g, BPS, CET1, 监管要求({r.CapitalVerificationStatus})"
                : "见 Methods + ValuationDetail";
            var date = r.AsOf;
            sb.AppendLine($"| {r.Code} | {r.Name} | {r.Type} | {r.DataQualityGrade} | {r.DataConfidenceScore:F0} | {r.SignalConfidence:F0} | {r.DividendStatus} | as_of {date} | {(miss.Length<=180?miss.Replace("|","/"):miss.Replace("|","/")[..180])} | {fields} |");
        }
        sb.AppendLine().AppendLine("## 分项（前 10 只）").AppendLine();
        foreach (var r in pick.Take(10))
        {
            sb.AppendLine($"### {r.Code} {r.Name}").AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(r.Chain ?? "");
            sb.AppendLine("数据质量：" + r.DataQualityDetail);
            sb.AppendLine("估值：" + r.ValuationDetail);
            if (!string.IsNullOrWhiteSpace(r.BankValuationBreakdown))
                sb.AppendLine("银行分项：" + r.BankValuationBreakdown);
            foreach (var m in r.Methods ?? [])
                sb.AppendLine($"方法 {m.Name} ({m.Role}) Fair={m.Fair:F2} {m.Detail}");
            sb.AppendLine("```").AppendLine();
        }
        var path = Path.Combine(o.OutDir, $"traceability_30_{o.AsOf}_{DateTime.Now:HHmmss}.md");
        File.WriteAllText(path, sb.ToString());
        File.WriteAllText(Path.Combine(o.OutDir, "traceability_30_latest.md"), sb.ToString());
        o.Gaps.Add("traceability_file=" + path);
    }

}

public static class QvCli
{
    public static async Task<int> RunAsync(string dataDir, string cmd, string? codes, string? outDir, CancellationToken ct = default)
    {
        void L(string m) { Console.WriteLine(m); }
        cmd = (cmd ?? "scan").ToLowerInvariant();
        if (cmd == "selftest")
        {
            Console.WriteLine(QvValuation.SelfTest());
            return 0;
        }
        if (QvReviewCli.Handles(cmd))
            return await QvReviewCli.RunAsync(dataDir, cmd, codes, outDir, ct).ConfigureAwait(false);
        if (cmd is "params" or "compare")
        {
            var cur = QvConfig.Load(dataDir);
            Console.WriteLine($"当前参数：{cur.SourceKind}  {cur.Path}");
            var snap = cur.SaveNamedSet(dataDir, "current_v1.1");
            Console.WriteLine("已保存参数快照 " + snap);
            var v1 = Path.Combine(dataDir, QvConfig.FileName);
            if (File.Exists(v1))
            {
                var legacy = QvConfig.LoadLegacyV1(dataDir);
                var cmp = QvConfig.Compare(legacy, cur);
                var cmpPath = Path.Combine(dataDir, "quality_value", "param_compare_v1_vs_v1.1.txt");
                File.WriteAllText(cmpPath, cmp);
                Console.WriteLine("对比已写入 " + cmpPath);
                foreach (var line in cmp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Take(40))
                    Console.WriteLine(line);
            }
            else Console.WriteLine("未找到旧参数 " + v1 + "，仅保存当前集");
            return 0;
        }
        var cache = Path.Combine(dataDir, "cache");
        var em = new EastMoneyClient(cache);
        var quotes = new QuoteService();
        if (cmd == "trace")
        {
            var latest = Path.Combine(dataDir, "quality_value", "state.json");
            if (!File.Exists(latest)) { Console.WriteLine("无 state.json，请先 --qv recalc offline"); return 2; }
            var prev = JsonSerializer.Deserialize<QvOutput>(File.ReadAllText(latest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true });
            if (prev is null) { Console.WriteLine("state.json 无法反序列化"); return 2; }
            QvScan.WriteTraceability(prev);
            Console.WriteLine("已按最近一次扫描写可追溯抽查，见 data/quality_value/traceability_30_latest.md");
            return 0;
        }
        var only = string.IsNullOrWhiteSpace(codes) ? null : codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mode = cmd is "scan" or "full" or "banks" or "offline" ? "recalc" : cmd;
        if (cmd == "cases") { mode = "full"; only ??= QvScan.CaseCodes; }
        AppConfig? cfg = null; DataStore? store = null;
        if(cmd is "daily" or "nov" or "auto")
            try { store = new DataStore(dataDir); cfg = store.LoadConfig(); } catch { /* 不打印配置 */ }
        var notify = cmd is "daily" or "nov" or "auto";
        var onlyType = cmd == "banks" ? "T2" : null;
        var r = await QvScan.RunAsync(dataDir, mode, L, em, quotes, cfg, store, only, ct, notify, onlyType).ConfigureAwait(false);
        Console.WriteLine($"池 合格={r.Qualified} 观察={r.Watch} 排除={r.Excluded} 未分型={r.Unmapped}");
        Console.WriteLine($"区 击球={r.Strike} 关注={r.Approach} 合理={r.Fair} 乐观={r.Optimistic}");
        Console.WriteLine("分型 " + string.Join(" ", r.ByType.Select(kv => kv.Key + "=" + kv.Value)));
        Console.WriteLine("今日击球：" + string.Join("、", r.Rows.Where(x => x.Zone == "strike").Select(x => x.Code + x.Name).DefaultIfEmpty("（无）")));
        Console.WriteLine("今日关注：" + string.Join("、", r.Rows.Where(x => x.Zone == "approach").Select(x => x.Code + x.Name).DefaultIfEmpty("（无）")));
        foreach (var c in QvScan.CaseCodes)
        {
            var row = r.Rows.FirstOrDefault(x => x.Code == c);
            Console.WriteLine("──── " + c + " ────");
            Console.WriteLine(row?.Chain ?? "未覆盖");
        }

        if (cmd == "banks")
        {
            Console.WriteLine("──── 银行样本分项 ────");
            foreach (var c in QvBankValuation.SampleCodes)
            {
                var row = r.Rows.FirstOrDefault(x => x.Code == c);
                Console.WriteLine("════ " + c + " " + (row?.Name ?? "") + " ════");
                Console.WriteLine(row?.BankValuationBreakdown ?? row?.ValuationDetail ?? "未覆盖");
                if (row is not null)
                    Console.WriteLine($"当前PB={row.PbCurrent:F2} 模型PB={row.PbModel:F2} 同业PB={row.PbPeer:F2} 最终PB={row.PbFairFinal:F2} ROE={row.RoeSus:P1} Re={row.ReBank:P1} g={row.GLong:P1} CET1={row.Cet1Latest:P2} 要求={row.RegulatoryRequirement:P2} 缓冲={row.CapitalBuffer:P2} 状态={row.ValuationStatus} 现价={row.Price:F2} 合理价={row.PFair:F2} {row.Pool}/{row.Zone}");
            }
        }

        if (r.Gaps.Count > 0) Console.WriteLine("缺口：" + string.Join("；", r.Gaps));
        Console.WriteLine("报告 " + r.ReportPath);
        return 0;
    }
}
