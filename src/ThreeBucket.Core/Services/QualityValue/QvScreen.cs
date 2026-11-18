namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>通用排雷 + 各类型准入硬门槛。缺失关键项不按 0 处理，进观察池。</summary>
public static class QvScreen
{
    public static void Run(QvStock s, QvRow row, QvData data, QvConfig cfg, DateTime today)
    {
        var gates = row.Gates;
        var ann = s.Annuals.OrderBy(x => x.Year).ToList();
        var last5 = LastN(ann, cfg.I("global.window_years", 5));
        var last10 = LastN(ann, cfg.I("global.t5_window_years", 10));

        // ── 排雷 ──
        var name = s.Name ?? "";
        if (name.Contains("ST", StringComparison.OrdinalIgnoreCase) || name.Contains('退') || name.Contains("退市"))
            Fail(gates, "ST/退市风险", "命中", "名称");

        var listYears = ListYears(s, today);
        var yearsOk = last5.Count >= cfg.I("global.observe_list_years", 5) && listYears >= 5;
        if (!yearsOk)
            Soft(gates, "上市年限/数据长度", listYears < 5 ? $"上市 {listYears:0.0} 年不足 5 年" : $"可用年报 {last5.Count} 份不足 5 份", "进观察，不打分");

        // 审计：有数据才判；缺失不剔除（非 12.1 关键项）
        if (data.Audits.TryGetValue(s.Code, out var audits))
        {
            var from = today.Year - cfg.I("screen.audit_years", 5);
            var bad = audits.Where(a => a.Year >= from && !IsStandardOpinion(a.Opinion)).ToList();
            if (bad.Count > 0) Fail(gates, "审计意见", string.Join("；", bad.Select(a => $"{a.Year}:{a.Opinion}")), "Tushare 补充 / fina_audit");
            else Pass(gates, "审计意见", "近五年未见非标", "Tushare 补充");
        }
        else Missing(gates, "审计意见", "无审计数据（未跑 Tushare 补充时不自动剔除）");

        // 质押
        if (data.PledgeCtrl.TryGetValue(s.Code, out var pl))
        {
            if (pl > cfg.D("screen.pledge_ctrl_max", 0.50)) Fail(gates, "实控人质押", $"控股股东质押 {pl:P1} > 50%", "Tushare 补充");
            else Pass(gates, "实控人质押", $"质押 {pl:P1}", "Tushare 补充");
        }
        else Missing(gates, "实控人质押", "无质押数据（未跑补充时不自动剔除）");

        // 持续亏损
        var win = s.Type == "T5" ? last10 : last5;
        var lossMax = s.Type == "T5" ? cfg.I("screen.t5_loss_years_max", 3) : cfg.I("screen.loss_years_max", 2);
        var losses = win.Count(a => a.Np is < 0);
        if (win.Count >= 3 && losses > lossMax) Fail(gates, "持续亏损", $"{win.Count} 年中亏损 {losses} 年（上限 {lossMax}）", "东财归母净利润");
        else if (win.Count >= 3) Pass(gates, "持续亏损", $"{win.Count} 年中亏损 {losses} 年", "东财");

        // 盈利质量 扣非/归母
        var q = last5.Where(a => a.Np is > 0 && a.DeductNp is not null).Select(a => a.DeductNp!.Value / a.Np!.Value).ToList();
        if (q.Count >= 3)
        {
            var avg = q.Average();
            if (avg < cfg.D("screen.quality_deduct_np_min", 0.7)) Fail(gates, "盈利质量", $"5 年平均扣非/归母 {avg:F2} < 0.7", "东财扣非净利润");
            else Pass(gates, "盈利质量", $"扣非/归母 {avg:F2}", "东财");
        }
        else Missing(gates, "盈利质量", "扣非净利润样本不足");

        // 商誉（T2/T3 不适用）
        if (s.Type is not "T2" and not "T3")
        {
            var last = last5.LastOrDefault();
            if (last?.Goodwill is not null && last.Equity is > 0)
            {
                var g = last.Goodwill.Value / last.Equity.Value;
                if (g > cfg.D("screen.goodwill_equity_max", 0.30)) Fail(gates, "商誉", $"商誉/净资产 {g:P1} > 30%", "东财资产负债表");
                else Pass(gates, "商誉", $"商誉/净资产 {g:P1}", "东财");
            }
            else Missing(gates, "商誉", "无商誉或净资产");
        }

        // 存贷双高：按行业豁免，不得直接认定为财务造假；触发后做更深检查。
        CashDebtCheck(s, row, last5, cfg, data);

        // 应收异常
        if (s.Type is "T1" or "T4" or "T5" && last5.Count >= 4)
        {
            var risingDays = 0; var faster = 0;
            for (var i = 1; i < last5.Count; i++)
            {
                var a0 = last5[i - 1]; var a1 = last5[i];
                if (a0.Revenue is > 0 && a1.Revenue is > 0 && a0.Ar is > 0 && a1.Ar is > 0)
                {
                    var gRev = a1.Revenue.Value / a0.Revenue.Value - 1;
                    var gAr = a1.Ar.Value / a0.Ar.Value - 1;
                    if (gAr > gRev + 0.02) faster++;
                }
                if (a0.ArDays is > 0 && a1.ArDays is > 0 && a1.ArDays > a0.ArDays) risingDays++;
            }
            if (faster >= 3 && risingDays >= 2)
            {
                Soft(gates, "应收异常", "应收账款增速连续高于营收且周转天数上升", "标红");
                AddFlag(row, "red_flag");
            }
        }

        // v1.5.4：行情/成交额只决定价格监控与执行资格，不进入经营排雷/质量评分。
        QvMonitoring.Apply(s.Quote, row, cfg);

        // 股本稀释（扣分项，不剔除）
        if (last5.Count >= 3 && last5.First().TotalShare is > 0 && last5.Last().TotalShare is > 0)
        {
            var cagr = QvMath.Cagr(last5.First().TotalShare, last5.Last().TotalShare, last5.Count - 1);
            if (cagr is > 0 && cagr > cfg.D("screen.share_dilution_cagr_max", 0.03))
                Soft(gates, "股本稀释", $"总股本年复合增 {cagr:P1} > 3%（送转未完全剔除则可能高估）", "扣分");
        }

        // ── 类型准入 ──
        if (s.Type is "T1" or "T2" or "T3" or "T4" or "T5")
            TypeGates(s, row, last5, last10, cfg, data);

        // 关键项缺失 → 观察
        var keyMissing = KeyMissing(s.Type, last5, gates);
        if (keyMissing.Count > 0)
        {
            Soft(gates, "关键数据缺失", string.Join("、", keyMissing) + "（进观察）", "v1.1 关键项");
            row.DataGaps = string.Join("；", keyMissing);
            AddFlag(row, "data_insufficient");
        }

        var hardFail = gates.Any(g => g.Result == "fail");
        var watchReason = gates.Where(g => g.Result is "soft" or "missing" && (g.Name.Contains("年限") || g.Name.Contains("关键") || g.Name.Contains("周期"))).ToList();
        if (hardFail) { row.Pool = "excluded"; row.ScreenSummary = "排雷/门槛未通过：" + JoinFails(gates); }
        else if (!yearsOk || keyMissing.Count > 0) { row.Pool = "watch"; row.ScreenSummary = row.DataGaps.Length > 0 ? "数据不足：" + row.DataGaps : "上市不足/年报不足，进观察"; }
        else { row.Pool = "candidate"; row.ScreenSummary = "通过排雷与硬门槛"; } // candidate 仅内部，评分后再定 pool
        row.GateSummary = string.Join("；", gates.Select(g => $"{g.Name}:{g.Result}"));
    }

    private static void TypeGates(QvStock s, QvRow row, List<QvAnnual> y5, List<QvAnnual> y10, QvConfig cfg, QvData data)
    {
        var gates = row.Gates;
        switch (s.Type)
        {
            case "T1":
            {
                // 取消阶梯净利率硬矩阵：规模与净利率分别进入评分，不在此淘汰。
                var np = QvMath.Mean(y5.Select(x => x.Np));
                var m = QvMath.Mean(y5.Select(x => x.NetMargin));
                Pass(gates, "规模与净利率", $"5 年平均归母 {np / 1e8:F1} 亿、净利率 {m:P1}（分别评分，非硬门槛）", "东财");

                var roe = QvMath.Mean(y5.Select(x => x.Roe));
                var roeMin = cfg.D("T1.roe_min", 0.08);
                var roeRev = cfg.D("T1.roe_review_below", 0.10);
                if (roe is null) Missing(gates, "ROE", "缺 ROE（关键项）");
                else if (roe < roeMin) Fail(gates, "ROE", $"5 年均值 {roe:P1} < {roeMin:P0}", "东财");
                else if (roe < roeRev)
                {
                    Soft(gates, "ROE 强化复核", $"5 年均值 {roe:P1} 低于 10%，进入强化复核（不直接排除）", "东财");
                    row.ManualNeeded = Join(row.ManualNeeded, "ROE<10% 强化复核");
                }
                else Pass(gates, "ROE", $"5 年均值 {roe:P1}", "东财");

                var debt = QvMath.Mean(y5.Select(x => x.InterestDebtRatio));
                var debtRev = cfg.D("T1.interest_debt_review", 0.25);
                if (debt is null) Missing(gates, "有息负债率", "缺有息负债率");
                else if (debt > debtRev)
                {
                    Soft(gates, "有息负债率", $"{debt:P1} > {debtRev:P0}，扣分并复核，不自动淘汰", "东财 INTEREST_DEBT_RATIO");
                    row.ManualNeeded = Join(row.ManualNeeded, "有息负债率偏高复核");
                }
                else Pass(gates, "有息负债率", $"{debt:P1}", "东财");

                var cov = QvMath.Mean(y5.Select(x => x.InterestCoverage));
                var covMin = cfg.D("T1.interest_coverage_min", 5);
                if (cov is null) Missing(gates, "利息保障倍数", "缺利息保障倍数");
                else if (cov < covMin)
                {
                    Soft(gates, "利息保障倍数", $"{cov:F1} < {covMin:F0}，触发风险复核", "东财");
                    row.ManualNeeded = Join(row.ManualNeeded, "利息保障倍数<5 复核");
                }
                else Pass(gates, "利息保障倍数", $"{cov:F1}", "东财");

                var ocfNp = QvMath.Mean(y5.Select(x => x.OcfToNp));
                var ocfMin = cfg.D("T1.ocf_to_np_min", 0.8);
                if (ocfNp is null) Missing(gates, "经营现金流/净利润", "缺经营现金流（关键项）");
                else if (ocfNp < ocfMin) Soft(gates, "经营现金流/净利润", $"5 年 {ocfNp:F2} < {ocfMin:F2}（原则上不低于，复核不直接淘汰）", "东财");
                else Pass(gates, "经营现金流/净利润", $"5 年 {ocfNp:F2}", "东财");

                var fcfPos = y5.Count(a => a.FcfMaint is > 0 || a.Fcf is > 0);
                if (y5.Count >= 4)
                {
                    // 单年 FCF 为负不直接淘汰；维持性 FCF 多年为负则复核。
                    if (fcfPos >= cfg.I("T1.fcf_positive_years_min", 3)) Pass(gates, "自由现金流", $"{fcfPos}/{y5.Count} 年维持性或账面 FCF 为正（单年转负不淘汰）", "东财现金流");
                    else Soft(gates, "自由现金流", $"{fcfPos}/{y5.Count} 年 FCF 为正，需复核分红是否由经营现金流支持", "东财现金流");
                }
                DivGate(gates, y5, cfg.I("T1.div_years_min", 4), cfg.D("T1.payout_min", 0.30), soft: true);
                break;
            }
            case "T2":
            case "T3":
            {
                var prefix = s.Type;
                // 净利润规模与净利率改为评分项，取消 20 亿 / 30% 硬门槛。
                var np = QvMath.Mean(y5.Select(x => x.Np));
                var nm = QvMath.Mean(y5.Select(x => x.NetMargin));
                Pass(gates, "净利率与利润规模", $"净利率 {nm:P1}、5 年平均净利润 {np / 1e8:F1} 亿（评分项，非硬门槛）", "东财");

                var roe = QvMath.Mean(y5.Select(x => x.Roe));
                var roeY = QvMath.Min(y5.Select(x => x.Roe));
                var roeMin = cfg.D($"{prefix}.roe_min", 0.08);
                if (roe is null) Missing(gates, "ROE", "缺 ROE（关键项）");
                else if (roe >= roeMin) Pass(gates, "ROE", $"5 年均值 {roe:P1}，最低年 {roeY:P1}", "东财");
                else
                {
                    Soft(gates, "ROE", $"5 年 {roe:P1} < {roeMin:P0}，进入复核，不直接排除", "东财");
                    row.ManualNeeded = Join(row.ManualNeeded, "银行/金租 ROE 低于原则线复核");
                }

                var npl = y5.LastOrDefault()?.Npl ?? QvMath.Mean(y5.Select(x => x.Npl));
                var nplMax = cfg.D($"{prefix}.npl_max", 0.015);
                var nplHard = cfg.D($"{prefix}.npl_hard_max", 0.030);
                if (npl is null) Missing(gates, "不良率", "缺不良率（关键项）");
                else if (npl > nplHard) Fail(gates, "不良率", $"{npl:P2} > 极端上限 {nplHard:P1}", "东财 NONPERLOAN");
                else if (npl > nplMax)
                {
                    Soft(gates, "不良率", $"{npl:P2} 高于原则线 {nplMax:P1}，复核不直接排除", "东财 NONPERLOAN");
                    row.ManualNeeded = Join(row.ManualNeeded, "不良率高于1.5%复核");
                }
                else
                {
                    var rise = y5.Count >= 3 && y5[^1].Npl is not null && y5[^3].Npl is not null ? y5[^1].Npl - y5[^3].Npl : 0;
                    if (s.Type == "T2" && rise > cfg.D("T2.npl_rise_max", 0.003))
                    {
                        Soft(gates, "不良率走势", $"近 3 年上升 {rise:P2} > 0.3 个百分点，预警", "东财");
                        AddFlag(row, "downgrade_warning");
                    }
                    else Pass(gates, "不良率", $"{npl:P2}", "东财 NONPERLOAN");
                }
                var pc = y5.LastOrDefault()?.ProvisionCov ?? QvMath.Mean(y5.Select(x => x.ProvisionCov));
                var pcMin = cfg.D($"{prefix}.provision_cov_min", 1.5);
                if (pc is null) Missing(gates, "拨备覆盖率", "缺拨备覆盖率（关键项）");
                else if (pc < pcMin)
                {
                    Soft(gates, "拨备覆盖率", $"{pc:P0} < 参考线 {pcMin:P0}（结合监管要求复核，不单独一票否决）", "东财 BLDKBBL");
                    row.ManualNeeded = Join(row.ManualNeeded, "拨备覆盖率低于参考线复核");
                }
                else Pass(gates, "拨备覆盖率", $"{pc:P0}", "东财 BLDKBBL");

                if (s.Type == "T2")
                {
                    var cet1 = y5.LastOrDefault()?.Cet1;
                    var req = data.BankRequirement(s.Code);
                    row.RegulatoryRequirement = req.Requirement;
                    row.Cet1Latest = cet1;
                    if (cet1 is null) Missing(gates, "核心一级资本充足率", "缺 CET1（关键项）");
                    else
                    {
                        var buf = cet1.Value - req.Requirement;
                        row.CapitalBuffer = buf;
                        if (req.NeedsReview) row.ManualNeeded = Join(row.ManualNeeded, "资本监管要求需复核（见 bank_capital_req.csv）");
                        var failB = cfg.D("T2.capital_buffer.fail_below", 0.0);
                        var hi = cfg.D("T2.capital_buffer.high_risk", 0.010);
                        var ded = cfg.D("T2.capital_buffer.deduct", 0.015);
                        var amp = cfg.D("T2.capital_buffer.ample", 0.025);
                        row.CapitalVerificationStatus = req.VerificationStatus;
                        var msg = $"CET1 {cet1:P2} − 监管要求 {req.Requirement:P2} = 缓冲 {buf:P2}（{req.Components}；核验状态 {req.VerificationStatus}）";
                        if (req.IsPending)
                        {
                            row.CapitalRequirementUnknown = true;
                            AddFlag(row, "capital_requirement_unknown");
                            Soft(gates, "核心一级资本充足率", msg + "，监管要求尚未核验，仅作参考缓冲，不得据此硬性排除或给出确定性资本安全结论", req.Source);
                            row.ManualNeeded = Join(row.ManualNeeded, "资本监管要求待核验（capital_requirement_unknown）");
                        }
                        else if (buf < failB) Fail(gates, "核心一级资本充足率", msg + "，缓冲<0，不通过硬性监管准入", req.Source);
                        else if (buf < hi) Soft(gates, "核心一级资本充足率", msg + "，高风险标记", req.Source);
                        else if (buf < ded) Soft(gates, "核心一级资本充足率", msg + "，扣分区间", req.Source);
                        else if (buf < amp) Pass(gates, "核心一级资本充足率", msg + "，正常", req.Source);
                        else Pass(gates, "核心一级资本充足率", msg + "，资本缓冲较充足", req.Source);
                    }
                    // g_sus 为评分指标，不再作为硬性准入。RWA 缺失时用总资产增速代理并标 proxy_data。
                    var gSus = roe is not null && QvMath.Mean(y5.Select(x => x.Payout)) is double py
                        ? roe * (1 - py) : (double?)null;
                    var assetCagr = y5.Count >= 4 ? QvMath.Cagr(y5[0].TotalAssets, y5[^1].TotalAssets, y5.Count - 1) : null;
                    if (gSus is not null && assetCagr is not null)
                    {
                        var need = assetCagr.Value * cfg.D("T2.g_sus_rwa_ratio", 0.9);
                        var msg = $"g_sus={gSus:P1} vs 总资产复合 {assetCagr:P1}×0.9（RWA 不可得，代理口径，不得当作精确 RWA 增速）";
                        if (gSus >= need) Pass(gates, "内生资本能力", msg, "proxy_data");
                        else Soft(gates, "内生资本能力", msg + "（评分项，非硬门槛）", "proxy_data");
                        AddFlag(row, "proxy_data");
                    }
                    else Missing(gates, "内生资本能力", "缺分红率或总资产，无法比较 g_sus 与增长");
                    // 关注类、迁徙率、地产/平台贷款等：数据源无则标入手工
                    if (data.Tags.TryGetValue(s.Code, out var tag))
                    {
                        if (tag.SpecialMention is not null) Pass(gates, "关注类贷款", $"人工补录 {tag.SpecialMention:P2}", "manual_tags");
                        else { Missing(gates, "关注类贷款", "数据源无，待 manual_tags 补录"); row.ManualNeeded = Join(row.ManualNeeded, "关注类贷款"); }
                        if (tag.ReLoanPct is not null) Pass(gates, "房地产贷款占比", $"人工补录 {tag.ReLoanPct:P1}", "manual_tags");
                        else { Missing(gates, "房地产贷款占比", "数据源无，待 manual_tags 补录"); row.ManualNeeded = Join(row.ManualNeeded, "房地产贷款占比"); }
                        if (tag.PlatformLoanPct is not null) Pass(gates, "平台相关贷款", $"人工补录 {tag.PlatformLoanPct:P1}", "manual_tags");
                        else { Missing(gates, "地方政府融资平台贷款", "数据源无，待 manual_tags 补录"); row.ManualNeeded = Join(row.ManualNeeded, "平台贷款"); }
                        if (tag.NplFormation is not null) Pass(gates, "不良生成率", $"人工补录 {tag.NplFormation:P2}", "manual_tags");
                        else { Missing(gates, "不良生成率/迁徙率", "数据源无，待 manual_tags 补录"); row.ManualNeeded = Join(row.ManualNeeded, "不良生成率/迁徙率"); }
                    }
                    else
                    {
                        Missing(gates, "银行额外风险指标", "关注类、迁徙率、地产/平台贷款不可得，待 manual_tags");
                        row.ManualNeeded = Join(row.ManualNeeded, "关注类/迁徙率/地产与平台贷款");
                    }
                }
                if (s.Type == "T3")
                {
                    var lev = y5.LastOrDefault()?.Leverage;
                    if (lev is null && y5.LastOrDefault()?.DebtRatio is double dr && dr < 1)
                        lev = 1 / Math.Max(1e-6, 1 - dr);
                    if (lev is null) Missing(gates, "杠杆倍数", "缺总资产/净资产");
                    else if (lev > cfg.D("T3.leverage_max", 10)) Fail(gates, "杠杆倍数", $"{lev:F1} > 10", "东财");
                    else Pass(gates, "杠杆倍数", $"{lev:F1}", "东财");
                    DivGate(gates, y5, 3, cfg.D("T3.payout_min", 0.30), soft: true);
                    Missing(gates, "融资来源集中度与期限错配", "原存贷双高规则不适用；融资来源/期限匹配需人工或补充数据");
                    row.ManualNeeded = Join(row.ManualNeeded, "金租融资来源与期限匹配");
                }
                break;
            }
            case "T4":
            {
                T4DualChannel(s, row, y5, cfg, data);
                break;
            }
            case "T5":
            {
                var roe = QvMath.Mean(y10.Select(x => x.Roe));
                var profitYears = y10.Count(a => a.Np is > 0);
                if (roe is null) Missing(gates, "10 年 ROE", "样本不足");
                else if (roe >= cfg.D("T5.roe_min", 0.07) && profitYears >= cfg.I("T5.profit_years_min", 7))
                    Pass(gates, "10 年盈利", $"ROE 均值 {roe:P1}，盈利年份 {profitYears}/{y10.Count}", "东财");
                else Fail(gates, "10 年盈利", $"ROE {roe:P1}，盈利年份 {profitYears}/{y10.Count}", "东财");
                DivGate(gates, y5, 3, cfg.D("T5.payout_min", 0.30), soft: true);
                var cycle = CycleRatio(s);
                row.CycleRatio = cycle;
                if (cycle is > 0 && cycle > cfg.D("T5.cycle_high", 1.5))
                {
                    Soft(gates, "周期位置", $"cycle_ratio={cycle:F2} > 1.5，标记 cycle_high：默认限制自动击球区强提醒，但不因此认定绝对不能买入，也不把 zone 降级", "TTM/10 年平均利润");
                    AddFlag(row, "cycle_high");
                    Missing(gates, "商品价格分位/产能利用率", "数据源无行业产品价格历史分位与产能利用率，待 proxy_data/manual_tags");
                    row.ManualNeeded = Join(row.ManualNeeded, "商品价格分位、产能利用率、单位成本");
                }
                else if (cycle is not null) Pass(gates, "周期位置", $"cycle_ratio={cycle:F2}", "东财");
                break;
            }
        }
    }

    private static void T4DualChannel(QvStock s, QvRow row, List<QvAnnual> y5, QvConfig cfg, QvData data)
    {
        var gates = row.Gates;
        var roe = QvMath.Mean(y5.Select(x => x.Roe));
        var ocfNp = QvMath.Mean(y5.Select(x => x.OcfToNp));
        var cov = QvMath.Mean(y5.Select(x => x.InterestCoverage));
        var ocfYears = y5.Count(a => a.Ocf is > 0);
        var divYears = y5.Count(a => a.Dps is > 0 || a.Payout is > 0);
        var divMissing = s.DividendStatus is "dividend_missing" or "dividend_partial" or "dividend_estimated";

        var dpsSum = y5.Where(a => a.Dps is > 0 && a.TotalShare is > 0).Sum(a => a.Dps!.Value * a.TotalShare!.Value);
        var ocfSum = y5.Where(a => a.Ocf is not null).Sum(a => a.Ocf!.Value);
        var maintFcfSum = y5.Sum(a => (a.FcfMaint ?? (a.Ocf is not null ? a.Ocf - (a.Capex ?? 0) * cfg.D("T4.maint_capex_ratio", 0.5) : 0)) ?? 0);
        var aReasons = new List<string>();
        var bReasons = new List<string>();

        if (roe is null) aReasons.Add("缺 ROE");
        else if (roe < cfg.D("T4.roe_min", 0.08)) aReasons.Add($"5 年 ROE {roe:P1} < 8%");
        if (ocfNp is null || ocfNp < cfg.D("T4.ocf_to_np_min", 1.0)) aReasons.Add($"经营现金流/净利润 {ocfNp:F2}");
        if (cov is not null && cov < cfg.D("T4.interest_coverage_min", 3)) aReasons.Add($"利息保障 {cov:F1}");
        if (divMissing) aReasons.Add("分红数据不完整，通道条件待复核（不把缺失当零分红）"); else if (divYears < cfg.I("T4.div_years_min", 3)) aReasons.Add($"分红年份 {divYears}");
        var aOk = aReasons.Count == 0 && roe is not null;

        // B：允许 ROE<8%，要求经营现金流长期稳定、维持性 FCF 或 OCF 能支持分红、利息保障符合行业特点。
        if (ocfYears < 4) bReasons.Add($"经营现金流为正仅 {ocfYears} 年");
        if (ocfNp is not null && ocfNp < cfg.D("T4.ocf_to_np_b", 0.90)) bReasons.Add($"OCF/NP {ocfNp:F2} < B 通道线");
        if (dpsSum > 0 && ocfSum > 0 && dpsSum > ocfSum * 0.95) bReasons.Add("累计分红接近或超过累计经营现金流，可持续性存疑");
        else if (dpsSum > 0 && maintFcfSum < dpsSum * 0.5 && ocfSum < dpsSum) bReasons.Add("维持性自由现金流难以覆盖分红");
        if (cov is not null && cov < cfg.D("T4.interest_coverage_b", 2.5)) bReasons.Add($"利息保障 {cov:F1} 偏低");
        if (divMissing) bReasons.Add("分红数据不完整，通道条件待复核（不把缺失当零分红）"); else if (divYears < cfg.I("T4.div_years_min", 3)) bReasons.Add($"分红年份 {divYears}");
        var bOk = bReasons.Count == 0 && ocfYears >= 4;

        if (aOk)
        {
            row.T4Channel = "A";
            Pass(gates, "T4 双通道", $"A 通道（盈利型）通过；5 年 ROE {roe:P1}，OCF/NP {ocfNp:F2}", "东财");
        }
        else if (bOk)
        {
            row.T4Channel = "B";
            Pass(gates, "T4 双通道", $"B 通道（现金流及分红型）通过；ROE {roe:P1}（允许低于 8%），经营现金流稳定且可支持分红", "东财");
        }
        else
        {
            Fail(gates, "T4 双通道", $"A 未通过：{string.Join("；", aReasons)}。B 未通过：{string.Join("；", bReasons)}", "东财");
        }

        var capexOcf = y5.TakeLast(3).Where(a => a.Ocf is > 0 && a.Capex is not null).Select(a => a.Capex!.Value / a.Ocf!.Value).ToList();
        if (capexOcf.Count > 0)
        {
            var v = capexOcf.Average();
            if (v <= cfg.D("T4.capex_ocf_max", 0.70)) Pass(gates, "资本开支/经营现金流", $"近3年 {v:P0}（含扩张性开支，不单独作为淘汰）", "东财");
            else Soft(gates, "资本开支/经营现金流", $"近3年 {v:P0} > 70%（扩张期可放宽；估值使用维持性 capex）", "东财");
        }
        if (data.Tags.TryGetValue(s.Code, out var tag) && tag.MoatConfirmed)
            Pass(gates, "资源/特许壁垒", "manual_tags 已确认护城河", "manual_tags");
        else
        {
            Soft(gates, "资源/特许壁垒", "未人工确认，标记 moat_unconfirmed（不因此排除）", "缺人工标签");
            AddFlag(row, "moat_unconfirmed");
        }
        var dps = y5.Where(a => a.Dps is > 0).Select(a => a.Dps!.Value).ToList();
        if (dps.Count >= 2 && dps[^1] < dps.Max() * (1 - cfg.D("T4.dps_drop_max", 0.20)))
            Soft(gates, "分红稳定性", "每股分红较最高年下降超过 20%，复核（不直接淘汰）", "东财分红");
    }

    private static void CashDebtCheck(QvStock s, QvRow row, List<QvAnnual> y5, QvConfig cfg, QvData data)
    {
        var gates = row.Gates;
        var lastA = y5.LastOrDefault();
        if (s.Type == "T2")
        {
            Pass(gates, "存贷双高", "银行不适用存贷双高规则", "行业豁免");
            return;
        }
        if (s.Type == "T3")
        {
            Soft(gates, "存贷双高", "类金融不适用原 20%/20% 规则；改为关注融资来源、资产期限错配与流动性（数据不足则人工复核）", "行业豁免");
            row.ManualNeeded = Join(row.ManualNeeded, "金租融资来源与期限错配");
            return;
        }
        if (lastA is null || lastA.TotalAssets is not > 0) return;

        if (s.Type is "T4" or "T5")
        {
            var cash = lastA.Cash;
            var debtR = lastA.InterestDebtRatio;
            if (cash is not null)
            {
                // 受限资金不可得，不能把货币资金全部视为可支配。
                Missing(gates, "受限资金", "数据源无受限资金比例，无法判断现金是否真实可支配，待 manual_tags / 年报附注");
                row.ManualNeeded = Join(row.ManualNeeded, "受限资金比例");
            }
            var ocf = lastA.Ocf;
            if (ocf is > 0 && lastA.InterestCoverage is double ic && ic < 3)
                Soft(gates, "短期偿债与现金覆盖", $"利息保障 {ic:F1} 偏低，复核债务期限与现金覆盖率", "东财");
            // 不使用 20%/20% 规则认定造假
            return;
        }

        // T1：货币资金/资产>20% 且有息负债/资产>20% 时复核，并做更深检查。
        if (lastA.Cash is null || lastA.InterestDebtRatio is null) return;
        var cashR = lastA.Cash.Value / lastA.TotalAssets.Value;
        var debtR1 = lastA.InterestDebtRatio.Value;
        var thCash = cfg.D("T1.cash_asset_high", 0.20);
        var thDebt = cfg.D("T1.interest_debt_high", 0.20);
        if (cashR > thCash && debtR1 > thDebt)
        {
            var bits = new List<string> { $"货币资金/资产 {cashR:P1}", $"有息负债率 {debtR1:P1}" };
            if (lastA.TradeFinAsset is > 0) bits.Add($"交易性金融资产 {lastA.TradeFinAsset / 1e8:F1} 亿");
            else bits.Add("交易性金融资产未列示或为 0");
            bits.Add("受限资金不可得（不能据此认定造假）");
            bits.Add("有息负债期限结构不可得");
            var avgCash = lastA.Cash;
            bits.Add("利息收入与平均现金余额是否匹配：数据源无利息收入明细，待复核");
            Soft(gates, "存贷双高", string.Join("；", bits) + "。触发复核，不得直接认定为财务造假。", "东财 + 缺项人工");
            AddFlag(row, "red_flag");
            row.RedFlagUnreviewed = !(data.Tags.TryGetValue(s.Code, out var t) && t.ReviewedOk);
            row.ManualNeeded = Join(row.ManualNeeded, "存贷双高：受限资金、利息收入匹配、债务期限");
        }
    }

    public static double? CycleRatio(QvStock s)
    {
        var y10 = LastN(s.Annuals, 10);
        var avg = QvMath.Mean(y10.Select(x => x.Np ?? x.DeductNp));
        var last = y10.LastOrDefault()?.Np ?? y10.LastOrDefault()?.DeductNp;
        if (avg is > 0 && last is not null) return last / avg;
        return null;
    }

    private static double? FcfNp(List<QvAnnual> y)
        => QvMath.Mean(y.Where(a => a.Fcf is not null && a.Np is > 0).Select(a => a.Fcf / a.Np));

    private static void DivGate(List<QvGateItem> g, List<QvAnnual> y, int minYears, double payoutMin, bool soft = false)
    {
        var years = y.Count(a => a.Dps is > 0 || a.Payout is > 0);
        var py = QvMath.Mean(y.Select(a => a.Payout));
        if (years == 0 && py is null) { Missing(g, "分红", "无分红数据"); return; }
        if (years >= minYears && (py is null || py >= payoutMin)) Pass(g, "分红", $"{years} 年有分红，平均分红率 {py:P0}", "东财分红");
        else if (py is double pyv && pyv < payoutMin)
        {
            var msg = $"平均分红率 {py:P0} < {payoutMin:P0}";
            if (soft) Soft(g, "分红", msg + "（复核，不直接淘汰）", "东财");
            else Fail(g, "分红", msg, "东财");
        }
        else
        {
            var msg = $"近 {y.Count} 年仅 {years} 年分红（需 ≥{minYears}）";
            if (soft) Soft(g, "分红", msg + "（复核，不直接淘汰）", "东财");
            else Fail(g, "分红", msg, "东财");
        }
    }

    private static string Join(string? a, string b)
        => string.IsNullOrWhiteSpace(a) ? b : a + "；" + b;

    private static List<string> KeyMissing(string type, List<QvAnnual> y5, List<QvGateItem> gates)
    {
        var miss = new List<string>();
        bool Has(Func<QvAnnual, double?> f) => y5.Count(a => f(a) is not null) >= 3;
        if (type == "T2")
        {
            if (!Has(a => a.Npl)) miss.Add("不良率");
            if (!Has(a => a.ProvisionCov)) miss.Add("拨备覆盖率");
            if (y5.LastOrDefault()?.Cet1 is null) miss.Add("核心一级资本充足率");
            if (!Has(a => a.Roe)) miss.Add("ROE");
        }
        else if (type is "T1" or "T4" or "T5")
        {
            if (!Has(a => a.Roe)) miss.Add("ROE");
            if (type == "T1" && !Has(a => a.InterestDebtRatio)) miss.Add("有息负债率");
            if (!Has(a => a.OcfToNp) && !Has(a => a.Ocf)) miss.Add("经营现金流");
            if (!Has(a => a.Dps) && !Has(a => a.Payout)) miss.Add("分红");
        }
        return miss;
    }

    private static bool IsStandardOpinion(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        // 标准无保留 / 无保留意见 为通过；带解释、保留、否定、无法表示 = 非标
        if (s.Contains("否定") || s.Contains("无法表示") || s.Contains("拒绝") || s.Contains("保留意见") || s.Contains("带强调") || s.Contains("带解释"))
            return false;
        return s.Contains("标准") || s.Contains("无保留") || s.Contains("清洁") || s.Contains("标准无保留");
    }

    private static List<QvAnnual> LastN(List<QvAnnual> a, int n) => a.TakeLast(n).ToList();
    private static double ListYears(QvStock s, DateTime today)
        => DateTime.TryParse(s.ListDate, out var d) ? (today - d).TotalDays / 365.25 : s.Annuals.Count;

    private static void Pass(List<QvGateItem> g, string n, string d, string src = "") => g.Add(new() { Name = n, Result = "pass", Detail = d, Source = src });
    private static void Fail(List<QvGateItem> g, string n, string d, string src = "") => g.Add(new() { Name = n, Result = "fail", Detail = d, Source = src });
    private static void Soft(List<QvGateItem> g, string n, string d, string src = "") => g.Add(new() { Name = n, Result = "soft", Detail = d, Source = src });
    private static void Missing(List<QvGateItem> g, string n, string d) => g.Add(new() { Name = n, Result = "missing", Detail = d, Source = "缺数据" });
    private static void RatioMax(List<QvGateItem> g, string n, double? v, double max, string src)
    {
        if (v is null) Missing(g, n, "缺数据");
        else if (v > max) Fail(g, n, $"{v:P1} > {max:P0}", src);
        else Pass(g, n, $"{v:P1}", src);
    }
    private static void RatioMin(List<QvGateItem> g, string n, double? v, double min, string src)
    {
        if (v is null) Missing(g, n, "缺数据");
        else if (v < min) Fail(g, n, $"{v:F2} < {min:F2}", src);
        else Pass(g, n, $"{v:F2}", src);
    }
    private static void AddFlag(QvRow r, string f) { if (!r.FlagList.Contains(f)) r.FlagList.Add(f); }
    private static string JoinFails(List<QvGateItem> g) => string.Join("；", g.Where(x => x.Result == "fail").Select(x => x.Name + " " + x.Detail));
}
