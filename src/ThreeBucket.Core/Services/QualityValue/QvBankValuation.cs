namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// v1.2 P0 银行估值：Re = Rf + ERP + R_bank（按类型下限与个券风险调整），
/// 同业 PB（样本不足时用分组中位数/分位数，不强行低样本回归），
/// 双模型加权（默认 0.4/0.6，同业样本弱则降权），预警线（不封顶除非已校准），三情景。
/// </summary>
public static class QvBankValuation
{
    public static readonly string[] SampleCodes = ["601838", "600919", "600926", "601009", "600036", "601328"];

    public static readonly HashSet<string> StateOwnedCodes = new(StringComparer.Ordinal)
    {
        "601398", "601288", "601939", "601988", "601328", "601658"
    };

    public static readonly HashSet<string> JointStockCodes = new(StringComparer.Ordinal)
    {
        "000001", "600000", "600015", "600016", "600036", "601166", "601818", "601916", "601998"
    };

    public sealed class PeerMember
    {
        public string Code = "", Name = "", Group = "";
        public double? PbCurrent, Pb3y, Pb5y, Roe, Roa, Npl, Provision, Cet1, DivYield;
        public double? AnchorPb;
    }

    public sealed class PeerBook
    {
        public Dictionary<string, List<PeerMember>> Groups { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PeerMember> ByCode { get; } = new(StringComparer.Ordinal);
        public string BuiltAt { get; set; } = "";
        public string Source { get; set; } = "";
    }

    public static string ClassifyGroup(string code, string name, string? csvType)
    {
        var t = (csvType ?? "").Trim().ToLowerInvariant();
        if (t is "state_owned" or "国有大型银行" or "soe") return "state_owned";
        if (t is "joint_stock" or "股份制银行" or "joint") return "joint_stock";
        if (t is "city_commercial" or "城商行" or "city") return "city_commercial";
        if (t is "rural_commercial" or "农商行" or "rural") return "rural_commercial";
        code = (code ?? "").PadLeft(6, '0');
        if (StateOwnedCodes.Contains(code)) return "state_owned";
        if (JointStockCodes.Contains(code)) return "joint_stock";
        name ??= "";
        if (name.Contains("农商") || name.Contains("农行") && code != "601288") return "rural_commercial";
        if (name.EndsWith("农") || name.Contains("渝农") || name.Contains("沪农") || name.Contains("青农") || name.Contains("苏农"))
            return "rural_commercial";
        return "city_commercial";
    }

    public static string GroupLabel(string g) => g switch
    {
        "state_owned" => "国有大型银行",
        "joint_stock" => "股份制银行",
        "city_commercial" => "城商行",
        "rural_commercial" => "农商行",
        _ => g,
    };

    public static async Task FillHistoryAsync(IReadOnlyList<QvStock> banks, QvData data, Action<string> log, CancellationToken ct)
    {
        var nOk = 0; var nFail = 0;
        foreach (var s in banks)
        {
            try
            {
                var closes = await data.YearEndClosesAsync(s.Code, 8, ct).ConfigureAwait(false) ?? [];
                if(closes.Count>0 || s.PbHistory.Count==0)ApplyHistory(s, closes);
                else log($"[S29] {s.Code} 历史PB复用已有输入快照，未下载历史行情");
                if (s.PbMedian3y is not null || s.PbMedian5y is not null) nOk++;
                else nFail++;
            }
            catch (Exception ex)
            {
                nFail++;
                s.PbHistorySource = "东财日K年末收盘获取失败：" + ex.Message;
            }
        }
        log($"[S29] 银行历史 PB：成功 {nOk}，不足或失败 {nFail}（来源：东财日K年末收盘 / 年报普通股 BPS）");
    }

    public static void ApplyHistory(QvStock s, IReadOnlyList<(string Date, double Close)> closes)
    {
        var byYear = new Dictionary<int, double>();
        foreach (var (date, close) in closes)
        {
            if (close <= 0) continue;
            if (!DateTime.TryParse(date, out var dt)) continue;
            byYear[dt.Year] = close;
        }
        var pbs = new List<(int Year, double Pb, string Date)>();
        foreach (var a in s.Annuals)
        {
            if (a.Bps is not > 0) continue;
            if (!byYear.TryGetValue(a.Year, out var px)) continue;
            var pb = px / a.Bps.Value;
            if (pb > 0 && pb < 20) pbs.Add((a.Year, pb, a.ReportDate));
        }
        s.PbHistory = pbs.Select(x => (x.Year, x.Pb)).ToList();
        if (pbs.Count > 0)
        {
            var last3 = pbs.TakeLast(3).Select(x => (double?)x.Pb);
            var last5 = pbs.TakeLast(5).Select(x => (double?)x.Pb);
            s.PbMedian3y = QvMath.Median(last3);
            s.PbMedian5y = QvMath.Median(last5);
            var last = pbs[^1];
            s.PbHistoryAsOf = last.Date;
            s.PbHistorySource = $"东财日K年末收盘 / 年报普通股BPS（报告期 {string.Join("、", pbs.TakeLast(5).Select(x => x.Year))}）；最近一年末 PB={last.Pb:F2}（{last.Year}）";
        }
        else
        {
            s.PbHistorySource = closes.Count == 0
                ? "东财日K年末收盘不可得，3年/5年 PB 中位数未计算"
                : "有年末收盘但缺少对应年份普通股 BPS，3年/5年 PB 中位数未计算";
        }

        var bps = s.Annuals.LastOrDefault()?.Bps;
        var price = s.Quote.Price;
        if (s.Quote.Pb is > 0 and < 20) s.PbCurrent = s.Quote.Pb;
        else if (price is > 0 && bps is > 0) s.PbCurrent = price / bps;
        s.PbCurrentSource = s.Quote.Pb is > 0
            ? $"东财行情 PB={s.Quote.Pb:F2}（{s.Quote.AsOf}，{s.Quote.Source}）"
            : price is > 0 && bps is > 0
                ? $"现价 {price:F2} / 普通股BPS {bps:F2}（{s.Quote.AsOf}）"
                : "当前 PB 不可得";
    }

    public static PeerBook BuildPeers(IReadOnlyList<QvStock> banks, QvData data, QvConfig cfg)
    {
        var book = new PeerBook
        {
            BuiltAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Source = "同业组成员来自本次扫描 T2；当前 PB=行情或现价/BPS；历史 PB=东财年末收盘/年报 BPS",
        };
        foreach (var s in banks)
        {
            var req = data.BankRequirement(s.Code);
            var g = ClassifyGroup(s.Code, s.Name, req.BankType);
            s.BankTypeKey = g;
            s.BankPeerGroup = GroupLabel(g);
            var last = s.Annuals.LastOrDefault();
            var y3 = s.Annuals.TakeLast(3).ToList();
            var y5 = s.Annuals.TakeLast(5).ToList();
            var roe = QvMath.Mean(y5.Select(x => x.Roe)) ?? QvMath.Mean(y3.Select(x => x.Roe));
            double? roa = null;
            if (last?.Np is not null && last.TotalAssets is > 0) roa = last.Np / last.TotalAssets;
            double? dy = null;
            if (s.DividendStatus is "dividend_confirmed" or "dividend_partial")
            {
                var dps = QvMath.Mean(y3.Select(x => x.Dps));
                if (dps is > 0 && s.Quote.Price is > 0) dy = dps / s.Quote.Price;
            }
            var anchorParts = new List<double>();
            if (s.PbCurrent is > 0) anchorParts.Add(s.PbCurrent.Value);
            if (s.PbMedian3y is > 0) anchorParts.Add(s.PbMedian3y.Value);
            if (s.PbMedian5y is > 0) anchorParts.Add(s.PbMedian5y.Value);
            var m = new PeerMember
            {
                Code = s.Code, Name = s.Name, Group = g,
                PbCurrent = s.PbCurrent, Pb3y = s.PbMedian3y, Pb5y = s.PbMedian5y,
                Roe = roe, Roa = roa,
                Npl = last?.Npl ?? QvMath.Mean(y3.Select(x => x.Npl)),
                Provision = last?.ProvisionCov ?? QvMath.Mean(y3.Select(x => x.ProvisionCov)),
                Cet1 = last?.Cet1,
                DivYield = dy,
                AnchorPb = anchorParts.Count == 0 ? null : anchorParts.Average(),
            };
            book.ByCode[s.Code] = m;
            if (!book.Groups.TryGetValue(g, out var list)) { list = new(); book.Groups[g] = list; }
            list.Add(m);
        }
        return book;
    }

    public static void Apply(QvStock s, QvRow row, QvData data, QvConfig cfg, double rf, PeerBook? peers)
    {
        var y3 = s.Annuals.TakeLast(3).ToList();
        var y5 = s.Annuals.TakeLast(5).ToList();
        var bps = QvValuation.OrdinaryBps(s);
        var roe5 = QvMath.Mean(y5.Select(x => x.Roe));
        var roe3 = QvMath.Mean(y3.Select(x => x.Roe));
        var roe1 = y3.LastOrDefault()?.Roe ?? y5.LastOrDefault()?.Roe;
        if ((roe5 ?? roe3 ?? roe1) is null || bps is not > 0) return;

        var w5 = cfg.D("T2.roe_sus_w5", 0.50);
        var w3 = cfg.D("T2.roe_sus_w3", 0.30);
        var w1 = cfg.D("T2.roe_sus_w1", 0.20);
        var ww = 0.0; var acc = 0.0;
        if (roe5 is not null) { acc += w5 * roe5.Value; ww += w5; }
        if (roe3 is not null) { acc += w3 * roe3.Value; ww += w3; }
        if (roe1 is not null) { acc += w1 * roe1.Value; ww += w1; }
        if (ww <= 0) return;
        var hair = cfg.D("T2.roe_sus_haircut", 0.0);
        var roeSus = acc / ww - hair;
        row.RoeSus = roeSus;

        var payout = QvMath.Mean(y3.Select(x => x.Payout));
        var payoutUsed = payout ?? cfg.D("bank_valuation.payout_assumption_if_missing", 0.30);
        var payoutNote = payout is not null ? $"近3年分红率 {payout:P0}" : "分红率缺失，g 用假设留存 30%（非把股息记为 0）";

        var req = data.BankRequirement(s.Code);
        var group = ClassifyGroup(s.Code, s.Name, req.BankType);
        s.BankTypeKey = group;
        s.BankPeerGroup = GroupLabel(group);
        row.BankType = GroupLabel(group);

        var last = y5.LastOrDefault() ?? y3.LastOrDefault();
        var npl = last?.Npl ?? QvMath.Mean(y3.Select(x => x.Npl));
        var pc = last?.ProvisionCov ?? QvMath.Mean(y3.Select(x => x.ProvisionCov));
        var cet1 = last?.Cet1 ?? row.Cet1Latest;
        var roeVol = Stdev(y5.Select(x => x.Roe));
        var qualityCityOrRural = IsQuality(npl, pc, cet1, roeSus);

        var (re, reNote, rMin, rMax) = CostOfEquity(cfg, rf, group, qualityCityOrRural, npl, pc, cet1, roeVol, row.RedFlagUnreviewed, req);
        row.ReBank = re;
        row.ReBase = re;
        row.ReBreakdown = reNote;

        var gBaseMax = cfg.D("bank_valuation.growth_rate_base_max", 0.03);
        var gBaseMin = cfg.D("bank_valuation.growth_rate_base_min", 0.02);
        var gCap = cfg.D("bank_valuation.growth_rate_max", 0.04);
        var gPess = cfg.D("bank_valuation.growth_rate_pess", 0.02);
        var gRaw = roeSus * (1 - payoutUsed);
        var g = Math.Clamp(gRaw, gBaseMin, gBaseMax);
        var (okG, gAdj, gFlag) = EnsureReGtG(re, g);
        g = gAdj;
        row.GLong = g;

        var pbModel = PbRoe(roeSus, re, g);
        // 拨备对模型 PB 的微调（已有规则，保留并写入分项）
        var pbModelAdj = pbModel;
        var provNote = "";
        if (pc is not null)
        {
            if (pc < cfg.D("T2.provision_pb_low", 2.5)) { pbModelAdj *= cfg.D("T2.provision_pb_low_haircut", 0.9); provNote = "拨备偏低，模型PB×0.9"; }
            else if (pc > cfg.D("T2.provision_pb_high", 4.0)) { pbModelAdj *= cfg.D("T2.provision_pb_high_uplift", 1.05); provNote = "拨备较高，模型PB×1.05"; }
        }

        var (pbPeer, peerN, peerW, peerNote) = PeerPb(s, row, peers, cfg, group, roeSus, npl, pc, cet1);
        var modelW0 = cfg.D("bank_valuation.model_weight", 0.40);
        var peerW0 = cfg.D("bank_valuation.peer_weight", 0.60);
        double wPeer, wModel;
        if (pbPeer is not > 0)
        {
            wPeer = 0; wModel = 1;
            AddFlag(row, "valuation_uncertain");
        }
        else
        {
            wPeer = peerW;
            wModel = 1 - wPeer;
        }
        row.PeerSampleN = peerN;
        row.PeerWeightUsed = wPeer;

        var pbBlend = wModel * pbModelAdj + wPeer * (pbPeer ?? 0);
        var warn = WarningLine(cfg, group, qualityCityOrRural);
        row.PbWarning = warn;
        row.PbCurrent = s.PbCurrent;
        row.PbModel = pbModelAdj;
        row.PbPeer = pbPeer;
        row.PbFairFinal = pbBlend;
        row.UsePbCap = cfg.B("bank_valuation.use_pb_cap", false);
        var cap = cfg.D($"bank_valuation.pb_cap.{group}", 0);
        if (row.UsePbCap && cap > 0) row.PbFairFinal = Math.Min(pbBlend, cap);
        else row.PbFairFinal = pbBlend; // 未校准不封顶，展示未封顶估值

        var outlier = cfg.B("bank_valuation.valuation_outlier_check", true) && pbBlend > warn;
        if (outlier)
        {
            AddFlag(row, "valuation_outlier");
            // 检查驱动因素，写入分项，不直接改最终 PB
        }
        if (pbPeer is > 0 && pbModelAdj > 0)
        {
            var lo = Math.Min(pbModelAdj, pbPeer.Value);
            var hi = Math.Max(pbModelAdj, pbPeer.Value);
            if (lo > 0 && hi / lo - 1 > cfg.D("bank_valuation.model_peer_gap_flag", 0.40))
                AddFlag(row, "valuation_uncertain");
        }

        // 三情景（均要求 Re>g）
        var roePessCut = cfg.D("bank_valuation.roe_pess_cut", 0.025);
        var roeOptAdd = cfg.D("bank_valuation.roe_opt_add", 0.01);
        var roeCap = cfg.D("bank_valuation.roe_sus_cap", 0.20);
        var roePess = roeSus - roePessCut;
        var roeOpt = Math.Min(roeSus + roeOptAdd, roeCap);
        var rePess = re + cfg.D("bank_valuation.re_pess_add", 0.01);
        var gOpt = Math.Min(Math.Max(gRaw, g), gCap);
        var (okP, gPessU, fP) = EnsureReGtG(rePess, gPess);
        var (okO, gOptU, fO) = EnsureReGtG(re, gOpt);
        var pbPessM = PbRoe(roePess, rePess, gPessU);
        var pbOptM = PbRoe(roeOpt, re, gOptU);
        var pbPess = wModel * pbPessM + wPeer * (pbPeer is > 0 ? pbPeer.Value * 0.90 : pbPessM);
        var pbOpt = wModel * pbOptM + wPeer * (pbPeer is > 0 ? pbPeer.Value * 1.10 : pbOptM);
        if (pbOpt < pbBlend) pbOpt = pbBlend * 1.10;

        var sens = Sensitivity(roeSus, re, g, cfg.D("bank_valuation.sensitivity_step", 0.005));
        var denom = re - g;
        var uncertain = !okG || !okP || !okO || fP || fO || gFlag
            || denom < cfg.D("bank_valuation.denom_min", 0.03)
            || sens > cfg.D("bank_valuation.sensitivity_dispersion", 0.40)
            || wPeer < peerW0 * 0.5 && pbPeer is not > 0;
        if (uncertain) AddFlag(row, "valuation_uncertain");

        row.ValuationStatus = outlier ? "异常" : (req.IsPending ? "待复核" : (uncertain ? "待复核" : "正常"));

        var detail = new System.Text.StringBuilder();
        detail.Append($"ROE_sus={roeSus:P1}（{w5:P0}×5Y {roe5:P1} + {w3:P0}×3Y {roe3:P1} + {w1:P0}×最近 {roe1:P1}）");
        detail.Append($"；{payoutNote}");
        detail.Append($"；g_base=clamp(ROE×留存, {gBaseMin:P0}–{gBaseMax:P0})={g:P1}（未调整前 {gRaw:P1}）");
        detail.Append($"；{reNote}");
        detail.Append($"；PB_model=(ROE-g)/(Re-g)={pbModel:F2}");
        if (provNote.Length > 0) detail.Append("，" + provNote + $" → {pbModelAdj:F2}");
        detail.Append($"；{peerNote}");
        detail.Append($"；权重 模型 {wModel:P0} / 同业 {wPeer:P0} → PB_fair(未封顶)={pbBlend:F2}");
        detail.Append($"；预警线 {warn:F2}（{(outlier ? "已超过，标记 valuation_outlier，未封顶" : "未超过")}）");
        if (row.UsePbCap && cap > 0) detail.Append($"；已启用校准上限 {cap:F2}");
        else detail.Append("；上限未校准，不封顶，展示未封顶估值与预警线");
        detail.Append($"；情景 悲观 ROE {roePess:P1} Re {rePess:P1} g {gPessU:P1} PB={pbPess:F2}；乐观 ROE {roeOpt:P1} Re {re:P1} g {gOptU:P1} PB={pbOpt:F2}");
        detail.Append($"；参数敏感性（Re/g ±0.5pp）离散 {sens:P0}，Re-g={denom:P1}");
        detail.Append($"；当前PB={s.PbCurrent:F2}（{s.PbCurrentSource}）");
        detail.Append($"；3年PB中位数={s.PbMedian3y:F2}，5年PB中位数={s.PbMedian5y:F2}（{s.PbHistorySource}）");
        if (gFlag || fP || fO) detail.Append("；存在 Re≤g 已下调 g 并标记不确定");
        row.BankValuationBreakdown = detail.ToString();

        row.Methods.Add(new QvValuationMethod
        {
            Name = "银行双模型", Role = "pricing",
            Fair = pbBlend * bps, Safe = pbPess * bps, Opt = pbOpt * bps,
            Detail = detail.ToString(),
        });
        row.Methods.Add(new QvValuationMethod
        {
            Name = "PB-ROE 法", Role = "cross",
            Fair = pbModelAdj * bps,
            Detail = $"ROE_sus={roeSus:P1}，g={g:P1}，Re={re:P1}，PB={pbModelAdj:F2}，BPS={bps:F2}",
        });
        if (pbPeer is > 0)
        {
            row.Methods.Add(new QvValuationMethod
            {
                Name = "同业 PB 法", Role = "cross",
                Fair = pbPeer * bps,
                Detail = peerNote,
            });
        }

        // 股息率法：分红缺失时不参与
        if (s.DividendStatus is "dividend_missing" or "dividend_estimated" or null or "")
        {
            if (s.DividendStatus is "dividend_missing" or "" or null)
            {
                AddFlag(row, "dividend_missing");
                row.DividendStatus = "dividend_missing";
            }
        }
        else
        {
            var dps = QvMath.Mean(y3.Select(x => x.Dps));
            if (dps is > 0)
            {
                row.Methods.Add(new QvValuationMethod
                {
                    Name = "股息率法", Role = "cross",
                    Fair = dps / cfg.TargetDivYield("T2", "base", rf),
                    Safe = dps / cfg.TargetDivYield("T2", "conservative", rf),
                    Opt = dps / cfg.TargetDivYield("T2", "optimistic", rf),
                    Detail = $"每股分红 {dps:F3} / Y_required {cfg.TargetDivYield("T2", "base", rf):P1}（状态 {s.DividendStatus}，非把缺失当 0）",
                });
            }
            else if (s.DividendStatus == "dividend_confirmed")
            {
                // 已确认近三年无现金分红：不把缺失当 0 去估值，只是没有可参与的股息率法
                row.Methods.Add(new QvValuationMethod
                {
                    Name = "股息率法", Role = "info",
                    Detail = "分红数据已确认但近三年现金分红为 0，股息率法不参与估值",
                });
            }
        }
        row.DividendStatus ??= s.DividendStatus;
    }

    private static (double Re, string Note, double Min, double Max) CostOfEquity(
        QvConfig cfg, double rf, string group, bool quality, double? npl, double? pc, double? cet1,
        double? roeVol, bool redFlag, QvBankCapitalReq req)
    {
        var (min, max) = TypeRange(cfg, group, quality);
        var erp = cfg.D("bank_valuation.erp", 0.065);
        var parts = new List<string>();
        var rBank = 0.0;
        // 类型中枢相对 Rf+ERP 的差额作为类型项，再叠加个券风险
        var mid = (min + max) / 2.0;
        var rType = Math.Max(0, mid - (rf + erp));
        rBank += rType;
        parts.Add($"类型中枢项 {rType:P2}（{GroupLabel(group)}{(quality ? "·优质参考" : "")} 区间 {min:P1}–{max:P1}）");

        void Add(string name, double v)
        {
            rBank += v;
            parts.Add($"{name} {v:+0.00%;-0.00%;0}");
        }

        // 资产质量（数据缺失不加分、也不假装良好）
        if (npl is null) parts.Add("不良率缺失，未下调 R_bank");
        else if (npl > cfg.D("bank_valuation.rbank.npl_high", 0.02)) Add("不良率偏高", cfg.D("bank_valuation.rbank.npl_high_add", 0.008));
        else if (npl > cfg.D("bank_valuation.rbank.npl_mid", 0.015)) Add("不良率偏中高", cfg.D("bank_valuation.rbank.npl_mid_add", 0.004));
        else if (npl < cfg.D("bank_valuation.rbank.npl_low", 0.008)) Add("不良率较低", cfg.D("bank_valuation.rbank.npl_low_add", -0.002));

        if (pc is null) parts.Add("拨备缺失，未下调 R_bank");
        else if (pc < cfg.D("bank_valuation.rbank.prov_low", 1.5)) Add("拨备偏低", cfg.D("bank_valuation.rbank.prov_low_add", 0.004));
        else if (pc < cfg.D("bank_valuation.rbank.prov_mid", 2.0)) Add("拨备中低", cfg.D("bank_valuation.rbank.prov_mid_add", 0.002));
        else if (pc > cfg.D("bank_valuation.rbank.prov_high", 3.0)) Add("拨备较厚", cfg.D("bank_valuation.rbank.prov_high_add", -0.001));

        if (cet1 is null) parts.Add("CET1 缺失，未下调 R_bank");
        else if (cet1 < cfg.D("bank_valuation.rbank.cet1_low", 0.09)) Add("CET1 偏低", cfg.D("bank_valuation.rbank.cet1_low_add", 0.004));
        else if (cet1 < cfg.D("bank_valuation.rbank.cet1_mid", 0.10)) Add("CET1 中低", cfg.D("bank_valuation.rbank.cet1_mid_add", 0.002));
        else if (cet1 > cfg.D("bank_valuation.rbank.cet1_high", 0.13)) Add("CET1 较充足", cfg.D("bank_valuation.rbank.cet1_high_add", -0.001));

        if (req.IsPending)
        {
            Add("监管资本要求未核验不确定性", cfg.D("bank_valuation.rbank.capital_unknown_add", 0.002));
        }

        if (roeVol is not null)
        {
            if (roeVol > cfg.D("bank_valuation.rbank.vol_high", 0.05)) Add("盈利波动高", cfg.D("bank_valuation.rbank.vol_high_add", 0.006));
            else if (roeVol > cfg.D("bank_valuation.rbank.vol_mid", 0.03)) Add("盈利波动中等", cfg.D("bank_valuation.rbank.vol_mid_add", 0.003));
        }
        if (redFlag) Add("未复核红旗", cfg.D("bank_valuation.rbank.red_flag_add", 0.005));

        parts.Add("区域集中/负债稳定性/公司治理：无单独可靠数据，未给减项");

        var raw = rf + erp + rBank;
        var highRisk = (npl is > 0.02) || (pc is not null && pc < 1.5) || (cet1 is not null && cet1 < 0.085) || redFlag;
        var hiMin = cfg.D("bank_valuation.cost_of_equity.high_risk.min", 0.13);
        var hiMax = cfg.D("bank_valuation.cost_of_equity.high_risk.max", 0.16);
        var re = raw;
        if (re < min) re = min;
        if (highRisk)
        {
            re = Math.Max(re, hiMin);
            re = Math.Min(re, hiMax);
        }
        else
        {
            re = Math.Min(re, max);
        }
        var note = $"Re=Rf {rf:P2}+ERP {erp:P1}+R_bank {rBank:P2} → 未钳位 {raw:P2}，下限 {min:P1}，最终 {re:P2}"
                   + (highRisk ? "（高风险区间）" : "")
                   + "；" + string.Join("，", parts);
        return (re, note, min, max);
    }

    private static (double Min, double Max) TypeRange(QvConfig cfg, string group, bool quality)
    {
        // 优质城商行用 11–13%；一般城商行、农商行 12–14.5%
        string key = group switch
        {
            "state_owned" => "state_owned",
            "joint_stock" => "joint_stock",
            "city_commercial" => quality ? "city_commercial" : "city_commercial_general",
            "rural_commercial" => "rural_commercial",
            _ => "city_commercial_general",
        };
        var min = cfg.D($"bank_valuation.cost_of_equity.{key}.min", group switch
        {
            "state_owned" => 0.10,
            "joint_stock" => 0.105,
            "city_commercial" when quality => 0.11,
            "city_commercial" => 0.12,
            _ => 0.12,
        });
        var max = cfg.D($"bank_valuation.cost_of_equity.{key}.max", group switch
        {
            "state_owned" => 0.115,
            "joint_stock" => 0.12,
            "city_commercial" when quality => 0.13,
            "city_commercial" => 0.145,
            _ => 0.145,
        });
        return (min, max);
    }

    private static bool IsQuality(double? npl, double? pc, double? cet1, double roeSus)
    {
        // 缺关键资产质量数据时不得自称优质（不得因为缺失就给更低的 Re 下限）
        if (npl is null || pc is null || cet1 is null) return false;
        return npl <= 0.012 && pc >= 2.5 && cet1 >= 0.09 && roeSus >= 0.12;
    }

    private static (double? Pb, int N, double Weight, string Note) PeerPb(
        QvStock s, QvRow row, PeerBook? book, QvConfig cfg, string group, double roe, double? npl, double? pc, double? cet1)
    {
        if (book is null || !book.Groups.TryGetValue(group, out var all))
            return (null, 0, 0, "同业样本不可得，同业权重视为 0，估值不确定");
        // 排除自身后，有锚定价的同业
        var peers = all.Where(p => p.Code != s.Code && p.AnchorPb is > 0).ToList();
        var n = peers.Count;
        var full = cfg.I("bank_valuation.peer_full_sample", 8);
        var w0 = cfg.D("bank_valuation.peer_weight", 0.60);
        if (n < cfg.I("bank_valuation.peer_min_sample", 3))
        {
            var med = QvMath.Median(all.Where(p => p.AnchorPb is > 0).Select(p => p.AnchorPb));
            var wLow = med is > 0 ? w0 * Math.Min(1, n / (double)full) : 0;
            return (med, n, wLow, $"同业组 {GroupLabel(group)} 有效样本 {n}（不足 3，用组中位数 {med:F2}，降权 {wLow:P0}，未做回归）");
        }

        // 不强行低样本回归：一律分位数 / 中位数
        var roes = peers.Where(p => p.Roe is not null).Select(p => p.Roe!.Value).OrderBy(x => x).ToList();
        var pbs = peers.Where(p => p.AnchorPb is not null).Select(p => p.AnchorPb!.Value).OrderBy(x => x).ToList();
        double? pbQ = null;
        if (roes.Count >= 3 && pbs.Count >= 3)
        {
            var pctl = PercentileRank(roes, roe);
            pbQ = QvMath.Percentile(pbs, pctl);
        }
        var medPb = QvMath.Median(peers.Select(p => p.AnchorPb));
        var pb = pbQ ?? medPb;
        // 资产质量相对中位数的轻度调整（有数据才调）
        var nplMed = QvMath.Median(peers.Select(p => p.Npl));
        var adj = 1.0;
        if (npl is not null && nplMed is not null)
        {
            if (npl > nplMed * 1.5) adj *= 0.95;
            else if (npl < nplMed * 0.7) adj *= 1.03;
        }
        if (pb is not null) pb *= adj;
        var w = n >= full ? w0 : w0 * (n / (double)full);
        var note = $"同业组 {GroupLabel(group)} 有效样本 {n}（未做回归，用组中位数 {medPb:F2}"
                   + (pbQ is not null ? $"、ROE 分位 {PercentileRank(roes, roe):P0} 对应 PB {pbQ:F2}" : "")
                   + $"，质量微调 {adj:F2} → PB_peer={pb:F2}；当前PB中位数 {QvMath.Median(peers.Select(p => p.PbCurrent)):F2}，3Y {QvMath.Median(peers.Select(p => p.Pb3y)):F2}，5Y {QvMath.Median(peers.Select(p => p.Pb5y)):F2}）"
                   + $"，权重 {w:P0}";
        if (n < full) note += "；样本未满，已降低同业权重并提高不确定性";
        if (n < full) AddFlag(row, "valuation_uncertain");
        return (pb, n, w, note);
    }

    private static double WarningLine(QvConfig cfg, string group, bool quality) => group switch
    {
        "state_owned" => cfg.D("bank_valuation.outlier.state_owned", 1.5),
        "joint_stock" => cfg.D("bank_valuation.outlier.joint_stock", 1.6),
        "city_commercial" => quality
            ? cfg.D("bank_valuation.outlier.city_commercial_quality", 1.8)
            : cfg.D("bank_valuation.outlier.city_commercial", 1.8),
        "rural_commercial" => cfg.D("bank_valuation.outlier.rural_commercial", 1.6),
        _ => 1.6,
    };

    private static double PbRoe(double roe, double re, double g)
    {
        if (re <= g) return double.NaN;
        return (roe - g) / (re - g);
    }

    private static (bool Ok, double G, bool Flag) EnsureReGtG(double re, double g)
    {
        if (re > g + 1e-6) return (true, g, false);
        return (false, Math.Max(0, re - 0.005), true);
    }

    private static double Sensitivity(double roe, double re, double g, double step)
    {
        var basePb = PbRoe(roe, re, g);
        if (!(basePb > 0) || double.IsNaN(basePb)) return 1;
        var xs = new List<double>();
        foreach (var dre in new[] { -step, 0, step })
        foreach (var dg in new[] { -step, 0, step })
        {
            var re2 = re + dre; var g2 = g + dg;
            if (re2 <= g2) continue;
            var p = PbRoe(roe, re2, g2);
            if (p > 0 && !double.IsNaN(p)) xs.Add(p);
        }
        if (xs.Count < 3) return 1;
        var lo = xs.Min(); var hi = xs.Max();
        return lo > 0 ? hi / lo - 1 : 1;
    }

    private static double PercentileRank(List<double> sorted, double x)
    {
        if (sorted.Count == 0) return 0.5;
        var i = sorted.FindIndex(v => v >= x);
        if (i < 0) return 1;
        if (i == 0) return 0;
        return i / (double)(sorted.Count - 1);
    }

    private static double? Stdev(IEnumerable<double?> xs)
    {
        var a = xs.Where(x => x.HasValue && !double.IsNaN(x.Value)).Select(x => x!.Value).ToList();
        if (a.Count < 3) return null;
        var m = a.Average();
        var v = a.Average(x => (x - m) * (x - m));
        return Math.Sqrt(v);
    }

    private static void AddFlag(QvRow r, string f) { if (!r.FlagList.Contains(f)) r.FlagList.Add(f); }
}
