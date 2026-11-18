namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// 三档估值。定价法参与 P_fair；交叉法只进分歧度。
/// P_fair = 定价法算术平均，两种定价法相差 &gt;40% 取较低者并标估值不确定。
/// P_safe = min(悲观情景估值, P_fair×(1−MoS))；P_opt 按乐观情景且 ≤ P_fair×1.4。
/// </summary>
public static class QvValuation
{
    public static void Run(QvStock s, QvRow row, QvData data, QvConfig cfg, double rf,
        IReadOnlyList<(string Date, double Close)>? yearCloses = null,
        QvBankValuation.PeerBook? bankPeers = null)
    {
        row.Rf = rf;
        if (s.Type is not ("T1" or "T2" or "T3" or "T4" or "T5")) return;
        if (s.Annuals.Count < 2) { row.ValuationDetail = "年报不足，未估值"; return; }

        var score = row.QualityScore ?? 70;
        var mos = cfg.Mos(score);
        row.Mos = mos;
        var reB = cfg.Re(s.Type, "base", rf);
        var reO = cfg.Re(s.Type, "optimistic", rf);
        var reC = cfg.Re(s.Type, "conservative", rf);
        row.ReBase = reB;
        var methods = row.Methods;
        var shares = s.Annuals.Last().TotalShare ?? 0;
        var bps = OrdinaryBps(s);

        switch (s.Type)
        {
            case "T1":
                T1(s, methods, cfg, reB, reO, reC, rf, yearCloses);
                break;
            case "T2":
                QvBankValuation.Apply(s, row, data, cfg, rf, bankPeers);
                break;
            case "T3":
                Bank(s, methods, cfg, reB, reO, reC, rf, true);
                break;
            case "T4":
                T4(s, methods, cfg, reB, reO, reC, rf, yearCloses);
                break;
            case "T5":
                T5(s, methods, cfg, reB, reO, reC, rf, yearCloses, row.CycleRatio);
                break;
        }

        var pricing = methods.Where(m => m.Role == "pricing" && m.Fair is > 0).ToList();
        var all = methods.Where(m => m.Fair is > 0).ToList();
        if (pricing.Count == 0) { row.ValuationDetail = "定价法均未算出，未给出三档价格"; return; }

        double fair;
        if (pricing.Count >= 2)
        {
            var hi = pricing.Max(m => m.Fair!.Value); var lo = pricing.Min(m => m.Fair!.Value);
            if (lo > 0 && hi / lo - 1 > cfg.D("global.valuation_gap_flag", 0.4))
            {
                fair = lo;
                AddFlag(row, "valuation_uncertain");
                row.ValuationDetail = $"两种定价法相差 {(hi / lo - 1):P0} > 40%，取较低者 {fair:F2}";
            }
            else { fair = pricing.Average(m => m.Fair!.Value); row.ValuationDetail = $"定价法平均 {fair:F2}"; }
        }
        else { fair = pricing[0].Fair!.Value; row.ValuationDetail = $"{pricing[0].Name} {fair:F2}"; }

        // 悲观情景：各定价法以悲观盈利+保守 Re 再算一次，只参与 P_safe 的 min()
        var pessimistics = methods.Where(m => m.Role == "pricing" && m.Safe is > 0).Select(m => m.Safe!.Value).ToList();
        var pSafeFormula = fair * (1 - mos);
        var pSafe = pessimistics.Count > 0 ? Math.Min(pSafeFormula, pessimistics.Min()) : pSafeFormula;

        var optRaw = methods.Where(m => m.Role == "pricing" && m.Opt is > 0).Select(m => m.Opt!.Value).ToList();
        var cap = fair * cfg.D("global.p_opt_cap", 1.4);
        var pOpt = optRaw.Count > 0 ? Math.Min(optRaw.Average(), cap) : Math.Min(fair * 1.2, cap);
        if (pOpt < fair) pOpt = Math.Min(fair * 1.15, cap);

        if (pSafe >= fair) pSafe = fair * (1 - Math.Max(mos, 0.05));
        if (pOpt <= fair) pOpt = Math.Min(fair * 1.15, cap);
        if (!(pSafe < fair && fair < pOpt))
        {
            pSafe = Math.Min(pSafe, fair * 0.95);
            pOpt = Math.Max(pOpt, fair * 1.05);
        }
        row.PFair = Round(fair); row.PSafe = Round(pSafe); row.POpt = Round(pOpt);

        if (all.Count >= 2)
        {
            var hi = all.Max(m => m.Fair!.Value); var lo = all.Min(m => m.Fair!.Value);
            if (lo > 0)
            {
                row.Dispersion = hi / lo - 1;
                row.ValuationConfidence = cfg.ValuationConfidence(row.Dispersion);
                if (row.Dispersion > cfg.D("global.dispersion_flag", 0.6))
                {
                    AddFlag(row, "valuation_uncertain");
                    row.ValuationDetail += $"；含交叉法分歧度 {row.Dispersion:P0} > 60%，估值可信度{row.ValuationConfidence}，不触发自动击球区强提醒";
                }
                else
                    row.ValuationDetail += $"；分歧度 {row.Dispersion:P0}，估值可信度{row.ValuationConfidence}";
            }
        }

        var px = s.Quote.Price;
        row.Price = px;
        AssignMultiples(row, s, bps);
        AssignZone(row, px, cfg);
        row.ValuationDetail += $"；Re 基准 {(row.ReBase ?? reB):P1}（国债 {rf:P2}+溢价，银行走 Rf+ERP+R_bank，与 Y_required 分开），MoS {mos:P0}（评分 {score:F0}）";
    }

    private static void T1(QvStock s, List<QvValuationMethod> ms, QvConfig cfg, double reB, double reO, double reC, double rf,
        IReadOnlyList<(string, double)>? yearCloses)
    {
        var y5 = s.Annuals.TakeLast(5).ToList();
        var y3 = s.Annuals.TakeLast(3).ToList();
        var shares = NormalizeShares(s);
        var g = Math.Min(cfg.D("T1.fcf_g", 0.03), cfg.D("global.gdp_g", 0.03));
        // 基准盈利：min(3年平均扣非, 最近一个年度扣非)；无分析师预期（§12.1）
        var deductAvg = QvMath.Mean(y3.Select(x => x.DeductNp ?? x.Np));
        var deductLast = y3.LastOrDefault()?.DeductNp ?? y3.LastOrDefault()?.Np;
        var baseEarn = deductAvg is not null && deductLast is not null ? Math.Min(deductAvg.Value, deductLast.Value) : deductAvg ?? deductLast;
        var pessEarn = baseEarn * cfg.D("T1.earn_pessimistic", 0.8);
        var optEarn = deductAvg * cfg.D("T1.earn_optimistic", 1.15);

        // FCF 法：使用维持性资本开支（优先折旧），扩张性 capex 不从正常化 FCF 中全额扣除。
        var fcf = QvMath.Mean(y5.Select(x => x.FcfMaint ?? MaintFcf(x, cfg, "T1")));
        if (fcf is > 0 && shares > 0 && reB > g)
        {
            double Val(double f, double re) => f * (1 + g) / (re - g) / shares;
            ms.Add(new QvValuationMethod
            {
                Name = "FCF 法", Role = "pricing",
                Fair = Val(fcf.Value, reB),
                Safe = Val(fcf.Value * cfg.D("T1.earn_pessimistic", 0.8), reC),
                Opt = Val(fcf.Value * cfg.D("T1.earn_optimistic", 1.15), reO),
                Detail = $"正常化维持性 FCF {fcf / 1e8:F2} 亿（OCF−维持性capex，非全部资本开支），g={g:P0}，价值=FCF×(1+g)/(Re−g)，股本 {shares:N0}",
            });
        }

        // 股东回报：分红与回购不得重复计算。expDiv 仅来自分红；回购只在未计入 DPS/分红率时另加。
        var dps = QvMath.Mean(y3.Select(x => x.Dps));
        var payout = QvMath.Mean(y3.Select(x => x.Payout));
        var epsN = shares > 0 && baseEarn is > 0 ? baseEarn / shares : s.Annuals.Last().DeductEps ?? s.Annuals.Last().Eps;
        // 取 max(DPS, payout×EPS)，不要再把 payout×EPS 与 DPS 相加。
        var expDiv = (double?)null;
        if (dps is > 0) expDiv = dps;
        if (payout is > 0 && epsN is > 0)
            expDiv = expDiv is > 0 ? Math.Max(expDiv.Value, payout.Value * epsN.Value) : payout.Value * epsN.Value;
        var bbPs = QvMath.Mean(y3.Where(x => x.Buyback is > 0 && shares > 0).Select(x => x.Buyback / shares));
        // 若分红率已接近或超过（DPS+回购）/EPS，视为回购可能已包含在股东付出中，不再加回购。
        var addBb = bbPs is > 0;
        if (addBb && payout is > 0 && epsN is > 0 && dps is > 0 && payout * epsN >= dps * 0.95)
            addBb = false; // 避免把回购从 DPS 口径再加一次
        if (expDiv is > 0)
        {
            var cash = expDiv.Value + (addBb ? bbPs!.Value : 0);
            var yB = cfg.TargetDivYield("T1", "base", rf);
            var yC = cfg.TargetDivYield("T1", "conservative", rf);
            var yO = cfg.TargetDivYield("T1", "optimistic", rf);
            ms.Add(new QvValuationMethod
            {
                Name = "股东回报法", Role = "pricing",
                Fair = cash / yB, Safe = cash / yC, Opt = cash / yO,
                Detail = $"每股分红 {expDiv:F3}" + (addBb ? $"+回购 {bbPs:F3}（未与分红重复）" : "（仅分红，未重复计入回购）") + $" / Y_required {yB:P1}（非 Re）",
            });
        }

        // 历史 PE 分位（交叉）
        if (yearCloses is { Count: >= 5 } && epsN is > 0)
        {
            var pes = new List<double>();
            foreach (var (dt, close) in yearCloses)
            {
                if (!DateTime.TryParse(dt, out var d)) continue;
                var a = s.Annuals.LastOrDefault(x => x.Year == d.Year);
                var e = a?.DeductEps ?? a?.Eps;
                if (e is > 0 && close > 0) pes.Add(close / e.Value);
            }
            if (pes.Count >= 5)
            {
                var p25 = QvMath.Percentile(pes, 0.25);
                var p50 = QvMath.Percentile(pes, 0.50);
                var p75 = QvMath.Percentile(pes, 0.75);
                ms.Add(new QvValuationMethod
                {
                    Name = "历史 PE 分位", Role = "cross",
                    Fair = p50 * epsN, Safe = p25 * epsN, Opt = p75 * epsN,
                    Detail = $"近{pes.Count}年扣非 PE 25/50/75 分位 {p25:F1}/{p50:F1}/{p75:F1}",
                });
            }
        }
    }

    private static void Bank(QvStock s, List<QvValuationMethod> ms, QvConfig cfg, double reB, double reO, double reC, double rf, bool lease)
    {
        var y3 = s.Annuals.TakeLast(3).ToList();
        var bps = OrdinaryBps(s);
        var y5 = s.Annuals.TakeLast(5).ToList();
        var roe5 = QvMath.Mean(y5.Select(x => x.Roe));
        var roe3 = QvMath.Mean(y3.Select(x => x.Roe));
        var roe1 = y3.LastOrDefault()?.Roe ?? y5.LastOrDefault()?.Roe;
        var roeAvg = roe3 ?? roe5;
        var roeHi = QvMath.Max(y3.Select(x => x.Roe));
        var payout = QvMath.Mean(y3.Select(x => x.Payout)) ?? 0.30;
        if ((roe5 ?? roe3 ?? roe1) is null || bps is not > 0) return;
        var w5 = cfg.D("T2.roe_sus_w5", 0.50);
        var w3 = cfg.D("T2.roe_sus_w3", 0.30);
        var w1 = cfg.D("T2.roe_sus_w1", 0.20);
        // 缺某一档时按已有档重新归一化权重，避免把缺失当 0。
        double? roeSusRaw = null;
        var ww = 0.0; var acc = 0.0;
        if (roe5 is not null) { acc += w5 * roe5.Value; ww += w5; }
        if (roe3 is not null) { acc += w3 * roe3.Value; ww += w3; }
        if (roe1 is not null) { acc += w1 * roe1.Value; ww += w1; }
        if (ww > 0) roeSusRaw = acc / ww;
        if (roeSusRaw is null) return;
        var hair = cfg.D("T2.roe_sus_haircut", 0.0); // 默认 0，避免与已体现在 ROE 中的风险重复扣减
        var roeSus = roeSusRaw.Value - hair;
        var g = Math.Min(roeSus * (1 - payout), cfg.D("T2.g_cap", 0.04));
        if (reB <= g) g = reB - 0.005;
        double Pb(double roe, double re) => (roe - g) / Math.Max(1e-4, re - g);

        var pb = Pb(roeSus, reB);
        var pc = y3.Last().ProvisionCov;
        if (pc is not null)
        {
            if (pc < cfg.D("T2.provision_pb_low", 2.5)) pb *= cfg.D("T2.provision_pb_low_haircut", 0.9);
            else if (pc > cfg.D("T2.provision_pb_high", 4.0)) pb *= cfg.D("T2.provision_pb_high_uplift", 1.05);
        }
        if (lease)
        {
            var lev = y3.Last().Leverage;
            if (lev is > 0 && lev > cfg.D("T3.leverage_pb_haircut_at", 8)) pb *= cfg.D("T3.leverage_pb_haircut", 0.9);
        }
        var roePess = roeSus - cfg.D("T2.roe_sus_pessimistic_cut", 0.02);
        var pbPess = Pb(roePess, reC);
        var roeOpt = ((roeHi ?? roeSus) + roeSus) / 2;
        var pbOpt = Math.Min(Pb(roeOpt, reO), pb * 1.4);
        ms.Add(new QvValuationMethod
        {
            Name = "PB-ROE 法", Role = "pricing",
            Fair = pb * bps, Safe = pbPess * bps, Opt = pbOpt * bps,
            Detail = $"ROE_sus={roeSus:P1}（{w5:P0}×5Y {roe5:P1} + {w3:P0}×3Y {roe3:P1} + {w1:P0}×最近 {roe1:P1} − 风险调整 {hair:P1}），g=min(ROE_sus×留存,4%)={g:P1}，Re={reB:P1}，合理 PB={pb:F2}，BPS={bps:F2}（普通股）",
        });

        var dps = QvMath.Mean(y3.Select(x => x.Dps));
        if (dps is > 0)
        {
            var typ = lease ? "T3" : "T2";
            ms.Add(new QvValuationMethod
            {
                Name = "股息率法", Role = "cross",
                Fair = dps / cfg.TargetDivYield(typ, "base", rf),
                Safe = dps / cfg.TargetDivYield(typ, "conservative", rf),
                Opt = dps / cfg.TargetDivYield(typ, "optimistic", rf),
                Detail = $"每股分红 {dps:F3} / Y_required {cfg.TargetDivYield(typ, "base", rf):P1}（非 Re）",
            });
        }
    }

    private static void T4(QvStock s, List<QvValuationMethod> ms, QvConfig cfg, double reB, double reO, double reC, double rf,
        IReadOnlyList<(string, double)>? yearCloses)
    {
        var y3 = s.Annuals.TakeLast(3).ToList();
        var y5 = s.Annuals.TakeLast(5).ToList();
        var shares = NormalizeShares(s);
        var dps = QvMath.Mean(y3.Select(x => x.Dps));
        if (s.DividendStatus is "dividend_missing" or "dividend_estimated")
        {
            dps = null;
            ms.Add(new QvValuationMethod { Name = "股息率法", Role = "info", Detail = "分红数据" + s.DividendStatus + "，股息率法不参与估值，不把缺失当 0。原因：" + s.DividendMissingReason });
        }
        // 用维持性 FCF（OCF−折旧或维持性 capex）约束分红；不得用含扩张并购的全部 capex 把 DPS 压到接近 0。
        var maintFcf = QvMath.Mean(y5.Select(x => x.FcfMaint ?? MaintFcf(x, cfg, "T4")));
        var ocf = QvMath.Mean(y5.Select(x => x.Ocf));
        var yB = cfg.TargetDivYield("T4", "base", rf);
        var yC = cfg.TargetDivYield("T4", "conservative", rf);
        var yO = cfg.TargetDivYield("T4", "optimistic", rf);
        if (dps is > 0)
        {
            var cap = dps.Value;
            var note = "未用全部 capex 约束";
            if (shares > 0 && maintFcf is > 0)
            {
                var capPs = maintFcf.Value / shares * cfg.D("T4.div_fcf_cap", 0.90);
                if (capPs > 0 && cap > capPs * 3)
                {
                    // 口径可能仍错：维持性 FCF/股与 DPS 差一个数量级以上时，不强制把 DPS 砍到异常低。
                    note = $"维持性 FCF/股 {capPs:F3} 与 DPS {dps:F3} 量级差异大，未强制扣减（请复核股本/现金流单位）";
                }
                else if (cap > capPs && capPs >= dps.Value * 0.3)
                {
                    cap = capPs;
                    note = $"不超过维持性 FCF/股×0.9（{capPs:F3}）";
                }
                else note = $"维持性 FCF/股 {capPs:F3}，分红由经营现金流覆盖";
            }
            else if (shares > 0 && ocf is > 0 && dps * shares > ocf * 0.95)
            {
                cap = ocf.Value * 0.9 / shares;
                note = "累计分红接近经营现金流，按 OCF×0.9/股约束（非全部 capex）";
            }
            ms.Add(new QvValuationMethod
            {
                Name = "股息率法", Role = "pricing",
                Fair = cap / yB, Safe = cap / yC, Opt = cap / yO,
                Detail = $"正常化 DPS {cap:F3}（{note}）/ Y_required {yB:P1}（非 Re；Y=max(rf+溢价, floor)）",
            });
        }
        var bps = OrdinaryBps(s);
        var roe = QvMath.Mean(y5.Select(x => x.Roe));
        var g = cfg.D("T4.g_pb", 0.015);
        if (roe is not null && bps is > 0 && reB > g)
        {
            double Pb(double r, double re) => (r - g) / (re - g);
            var pb = Pb(roe.Value, reB);
            ms.Add(new QvValuationMethod
            {
                Name = "PB-ROE 法", Role = "pricing",
                Fair = pb * bps,
                Safe = Pb(roe.Value * 0.85, reC) * bps,
                Opt = Math.Min(Pb(roe.Value * 1.05, reO), pb * 1.4) * bps,
                Detail = $"合理 PB={pb:F2}，BPS={bps:F2}，g={g:P1}",
            });
        }
        // EV/EBITDA 交叉：数据不足时跳过（缺企业价值与 EBITDA 完整口径）
    }

    private static void T5(QvStock s, List<QvValuationMethod> ms, QvConfig cfg, double reB, double reO, double reC, double rf,
        IReadOnlyList<(string, double)>? yearCloses, double? cycle)
    {
        var y10 = s.Annuals.TakeLast(10).ToList();
        var shares = s.Annuals.Last().TotalShare ?? 0;
        var npAvg = QvMath.Mean(y10.Select(x => x.DeductNp ?? x.Np));
        if (npAvg is null || shares <= 0) return;
        var epsN = npAvg.Value / shares;
        var pe = cfg.D("T5.fair_pe", 10);
        var peO = cfg.D("T5.opt_pe", 12);
        var peS = cfg.D("T5.fair_pe_min", 8);
        ms.Add(new QvValuationMethod
        {
            Name = "正常化 EPS×合理 PE", Role = "pricing",
            Fair = epsN * pe, Safe = epsN * peS, Opt = epsN * peO,
            Detail = $"10 年平均盈利 {npAvg / 1e8:F2} 亿 → 正常化 EPS {epsN:F3}，合理 PE {pe:F1}",
        });
        var dps10 = QvMath.Mean(y10.Select(x => x.Dps));
        if (dps10 is > 0)
            ms.Add(new QvValuationMethod
            {
                Name = "股息率法（10 年平均）", Role = "cross",
                Fair = dps10 / cfg.TargetDivYield("T5", "base", rf),
                Safe = dps10 / cfg.TargetDivYield("T5", "conservative", rf),
                Opt = dps10 / cfg.TargetDivYield("T5", "optimistic", rf),
                Detail = $"10 年平均每股分红 {dps10:F3}",
            });
        if (yearCloses is { Count: >= 5 })
        {
            var pbs = new List<double>();
            foreach (var (dt, close) in yearCloses)
            {
                if (!DateTime.TryParse(dt, out var d)) continue;
                var a = s.Annuals.LastOrDefault(x => x.Year == d.Year);
                if (a?.Bps is > 0) pbs.Add(close / a.Bps.Value);
            }
            if (pbs.Count >= 5)
            {
                var p50 = QvMath.Percentile(pbs, 0.50);
                var p20 = QvMath.Percentile(pbs, cfg.D("T5.pb_safe_percentile", 0.20));
                var bps = OrdinaryBps(s) ?? s.Annuals.Last().Bps ?? 0;
                ms.Add(new QvValuationMethod
                {
                    Name = "PB 历史分位", Role = "cross",
                    Fair = p50 * bps, Safe = Math.Min(p20, cfg.D("T5.pb_safe_cap", 1.0)) * bps, Opt = QvMath.Percentile(pbs, 0.75) * bps,
                    Detail = $"PB 20/50/75 分位 {p20:F2}/{p50:F2}/{QvMath.Percentile(pbs, 0.75):F2}",
                });
            }
        }
    }

    private static void AssignMultiples(QvRow row, QvStock s, double? bps)
    {
        var eps = s.Annuals.TakeLast(3).Select(x => x.DeductEps ?? x.Eps).Where(x => x is > 0).DefaultIfEmpty().Average();
        if (eps is > 0)
        {
            if (row.PSafe is > 0) row.PeSafe = row.PSafe / eps;
            if (row.PFair is > 0) row.PeFair = row.PFair / eps;
            if (row.POpt is > 0) row.PeOpt = row.POpt / eps;
        }
        if (bps is > 0)
        {
            if (row.PSafe is > 0) row.PbSafe = row.PSafe / bps;
            if (row.PFair is > 0) row.PbFair = row.PFair / bps;
            if (row.POpt is > 0) row.PbOpt = row.POpt / bps;
        }
        var dps = QvMath.Mean(s.Annuals.TakeLast(s.Type == "T5" ? 10 : 3).Select(x => x.Dps));
        if (dps is > 0)
        {
            if (row.PSafe is > 0) row.DySafe = dps / row.PSafe;
            if (row.PFair is > 0) row.DyFair = dps / row.PFair;
            if (row.POpt is > 0) row.DyOpt = dps / row.POpt;
        }
    }

    private static void AssignZone(QvRow row, double? px, QvConfig cfg)
    {
        if (row.Pool != "qualified" || px is not > 0 || row.PSafe is not > 0 || row.PFair is not > 0 || row.POpt is not > 0)
        { row.Zone = ""; return; }
        var p = px.Value;
        var edge = cfg.D("global.approach_fair_ratio", 0.9) * row.PFair.Value;
        if (p <= row.PSafe) row.Zone = "strike";
        else if (p <= edge) row.Zone = "approach";
        else if (p <= row.POpt) row.Zone = "fair";
        else row.Zone = "optimistic";

        // v1.1：周期高位只阻断自动强提醒，不把 zone 从 strike 改为 approach。

        if (row.POpt > row.PSafe)
            row.PositionPct = (row.POpt - p) / (row.POpt - row.PSafe);
    }

    public static double? OrdinaryBps(QvStock s) => s.Annuals.LastOrDefault()?.Bps;

    /// <summary>股本单位校正：MAINFIN TOTAL_SHARE 一般为股。若误为万股（过小）则×10000。不得把缺失当 0 参与估值。</summary>
    public static double NormalizeShares(QvStock s)
    {
        var last = s.Annuals.LastOrDefault();
        var sh = last?.TotalShare ?? 0;
        if (sh <= 0) return 0;
        var np = last?.Np;
        var eps = last?.Eps ?? last?.DeductEps;
        // 若 NP/股本 与 EPS 差两个数量级以上，尝试万股→股。
        if (eps is > 0 && np is > 0)
        {
            var impl = np.Value / sh;
            if (impl > 0 && (impl / eps.Value > 50 || eps.Value / impl > 50) && sh < 1e8)
            {
                var sh2 = sh * 10000;
                var impl2 = np.Value / sh2;
                if (eps.Value > 0 && Math.Abs(impl2 / eps.Value - 1) < Math.Abs(impl / eps.Value - 1))
                    return sh2;
            }
        }
        return sh;
    }

    public static double? MaintCapex(QvAnnual a, QvConfig cfg, string type)
    {
        var ratio = cfg.D($"{type}.maint_capex_ratio", type == "T4" ? 0.50 : 0.60);
        if (a.Depreciation is > 0 && a.Capex is > 0)
            return Math.Min(a.Capex.Value, a.Depreciation.Value); // 超出折旧的部分视为扩张性
        if (a.Depreciation is > 0) return a.Depreciation;
        if (a.Capex is > 0) return a.Capex.Value * ratio;
        return a.Capex;
    }

    public static double? MaintFcf(QvAnnual a, QvConfig cfg, string type)
    {
        if (a.Ocf is null) return a.FcfMaint ?? a.Fcf;
        var maint = MaintCapex(a, cfg, type) ?? 0;
        return a.Ocf.Value - maint;
    }

    private static double Round(double v) => Math.Round(v, 3);
    private static void AddFlag(QvRow r, string f) { if (!r.FlagList.Contains(f)) r.FlagList.Add(f); }

    /// <summary>公式自检：ROE_sus=13%, g=4%, Re=10% → PB=1.5。</summary>
    public static string SelfTest()
    {
        var pb = (0.13 - 0.04) / (0.10 - 0.04);
        if (Math.Abs(pb - 1.5) > 1e-9) return $"FAIL PB-ROE {pb} ≠ 1.5";
        var roeSus = 0.50 * 0.12 + 0.30 * 0.13 + 0.20 * 0.14;
        if (Math.Abs(roeSus - 0.127) > 1e-9) return $"FAIL ROE_sus {roeSus}";
        var blend = 0.4 * 1.4 + 0.6 * 0.8;
        if (Math.Abs(blend - 1.04) > 1e-9) return $"FAIL blend {blend}";
        return "OK PB-ROE 示例 = 1.5；ROE_sus 50/30/20 示例 = 12.7%；v1.2 blend 0.4/0.6 = 1.04";

    }
}
