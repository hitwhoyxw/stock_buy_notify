namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>质量评分 0–100。缺失子项给中性分 50（不加不扣），并计入 data_coverage。缺失不得当作 0。</summary>
public static class QvScore
{
    public static void Run(QvStock s, QvRow row, QvData data, QvConfig cfg, IReadOnlyList<QvStock>? peers = null)
    {
        if (row.Pool == "excluded") return;
        if (row.Gates.Any(g => g.Name.Contains("上市年限") && g.Result == "soft") && row.Pool == "watch" && row.ScreenSummary.Contains("不足"))
            return;

        var y5 = s.Annuals.TakeLast(5).ToList();
        var y10 = s.Annuals.TakeLast(10).ToList();
        if (y5.Count < 2) { row.Pool = "watch"; row.DataGaps = Join(row.DataGaps, "年报不足以评分"); AddFlag(row, "data_insufficient"); return; }

        var subs = row.SubScores;
        var obtained = 0.0; var total = 0.0;
        void Track(double weight, bool has)
        {
            total += weight;
            if (has) obtained += weight;
        }

        switch (s.Type)
        {
            case "T1":
            {
                var roeAvg = QvMath.Mean(y5.Select(x => x.Roe));
                var roeMin = QvMath.Min(y5.Select(x => x.Roe));
                var nm = QvMath.Mean(y5.Select(x => x.NetMargin));
                var np = QvMath.Mean(y5.Select(x => x.Np));
                var roic = QvMath.Mean(y5.Select(x => x.Roic));
                Dim(subs, "盈利能力", cfg.D("T1.weights.profit", 25), Avg(
                    QvMath.Piece(roeAvg, C(cfg, "T1.score.roe_avg", 0, 0.08), C(cfg, "T1.score.roe_avg", 1, 0.18)),
                    QvMath.Piece(roeMin, C(cfg, "T1.score.roe_min_year", 0, 0.05), C(cfg, "T1.score.roe_min_year", 1, 0.12)),
                    QvMath.Piece(nm, C(cfg, "T1.score.net_margin", 0, 0.04), C(cfg, "T1.score.net_margin", 1, 0.15)),
                    QvMath.Piece(np, C(cfg, "T1.score.np_scale", 0, 2e8), C(cfg, "T1.score.np_scale", 1, 2e9)),
                    QvMath.Piece(roic, C(cfg, "T1.score.roic", 0, 0.06), C(cfg, "T1.score.roic", 1, 0.15))
                ), $"ROE {roeAvg:P1}，净利率 {nm:P1}，规模 {np / 1e8:F1} 亿，ROIC {roic:P1}（规模与净利率分评）");
                Track(cfg.D("T1.weights.profit", 25), roeAvg is not null && nm is not null);
                Dim(subs, "现金流质量", cfg.D("T1.weights.cash", 20), Avg(
                    QvMath.Piece(QvMath.Mean(y5.Select(x => x.OcfToNp)), C(cfg, "T1.score.ocf_np", 0, 0.7), C(cfg, "T1.score.ocf_np", 1, 1.1)),
                    QvMath.Piece(FcfNp(y5), C(cfg, "T1.score.fcf_np", 0, 0.5), C(cfg, "T1.score.fcf_np", 1, 1.0))
                ), $"OCF/NP {QvMath.Mean(y5.Select(x => x.OcfToNp)):F2}，维持性FCF/NP {FcfNp(y5):F2}");
                Track(cfg.D("T1.weights.cash", 20), y5.Any(x => x.Ocf is not null));
                var debt = QvMath.Mean(y5.Select(x => x.InterestDebtRatio));
                var cov = QvMath.Mean(y5.Select(x => x.InterestCoverage));
                var cap = Avg(
                    QvMath.Piece(debt, C(cfg, "T1.score.debt_ratio", 0, 0.25), C(cfg, "T1.score.debt_ratio", 1, 0.05), false),
                    QvMath.Piece(cov, C(cfg, "T1.score.interest_coverage", 0, 5), C(cfg, "T1.score.interest_coverage", 1, 20))
                );
                if (NetDebt(y5.LastOrDefault()) is < 0) cap = Math.Min(100, cap + 8);
                Dim(subs, "资本结构", cfg.D("T1.weights.capital", 15), cap, $"有息负债率 {debt:P1}，利息保障 {cov:F1}");
                Track(cfg.D("T1.weights.capital", 15), debt is not null || cov is not null);
                var py = QvMath.Mean(y5.Select(x => x.Payout));
                var dil = DilutionPenalty(y5);
                if (s.DividendStatus is "dividend_missing")
                {
                    Dim(subs, "股东回报", cfg.D("T1.weights.shareholder", 15), 50,
                        "分红数据缺失，不按零分红计分，本项中性 50 并降低数据置信度。原因：" + s.DividendMissingReason);
                    Track(cfg.D("T1.weights.shareholder", 15), false);
                }
                else
                {
                    Dim(subs, "股东回报", cfg.D("T1.weights.shareholder", 15), Math.Max(0, QvMath.Piece(py, C(cfg, "T1.score.payout", 0, 0.30), C(cfg, "T1.score.payout", 1, 0.60)) - dil),
                        $"分红率 {py:P0}" + (dil > 0 ? $"，股本稀释扣 {dil:F0}" : "") + (s.DividendStatus.Length > 0 ? "（" + s.DividendStatus + "）" : ""));
                    Track(cfg.D("T1.weights.shareholder", 15), py is not null);
                }
                var cagr = y5.Count >= 3 ? QvMath.Cagr(y5[0].Np, y5[^1].Np, y5.Count - 1) : null;
                Dim(subs, "成长与护城河", cfg.D("T1.weights.growth", 15), Avg(
                    QvMath.Piece(cagr, C(cfg, "T1.score.np_cagr", 0, 0), C(cfg, "T1.score.np_cagr", 1, 0.10)),
                    data.Tags.TryGetValue(s.Code, out var tg) && tg.MoatConfirmed ? 80 : 50
                ), $"净利润 CAGR {cagr:P1}" + (data.Tags.TryGetValue(s.Code, out var t2) && t2.MoatConfirmed ? "，护城河已确认" : "，护城河未确认（中性分）"));
                Track(cfg.D("T1.weights.growth", 15), cagr is not null);
                if (!(data.Tags.TryGetValue(s.Code, out var t3) && t3.MoatConfirmed)) AddFlag(row, "moat_unconfirmed");
                GovDim(subs, cfg.D("T1.weights.gov", 10), s, data, row);
                Track(cfg.D("T1.weights.gov", 10), true);
                break;
            }
            case "T2":
            case "T3":
            {
                var pfx = s.Type;
                var roe = QvMath.Mean(y5.Select(x => x.Roe));
                var nim = QvMath.Mean(y5.Select(x => x.Nim));
                var np = QvMath.Mean(y5.Select(x => x.Np));
                var nm = QvMath.Mean(y5.Select(x => x.NetMargin));
                var roeScore = PeerOrPiece(roe, peers, "T2", a => QvMath.Mean(a.TakeLast(5).Select(x => x.Roe)),
                    C(cfg, "T2.score.roe_avg", 0, 0.08), C(cfg, "T2.score.roe_avg", 1, 0.15));
                var nimScore = s.Type == "T2"
                    ? PeerOrPiece(nim, peers, "T2", a => QvMath.Mean(a.TakeLast(5).Select(x => x.Nim)), C(cfg, "T2.score.nim", 0, 0.012), C(cfg, "T2.score.nim", 1, 0.025))
                    : 50; // 金租不套用银行 NIM
                Dim(subs, "盈利能力", cfg.D($"{pfx}.weights.profit", 20), Avg(
                    roeScore,
                    nimScore,
                    QvMath.Piece(np, C(cfg, "T2.score.profit_scale", 0, 5e8), C(cfg, "T2.score.profit_scale", 1, 5e9)),
                    QvMath.Piece(nm, C(cfg, "T2.score.net_margin", 0, 0.20), C(cfg, "T2.score.net_margin", 1, 0.45))
                ), $"ROE {roe:P1}，净息差 {nim:P2}（{(s.Type == "T2" ? "同类分位+绝对" : "金租不套用银行 NIM")}），利润 {np / 1e8:F1} 亿");
                Track(cfg.D($"{pfx}.weights.profit", 20), roe is not null);
                Dim(subs, "资产质量", cfg.D($"{pfx}.weights.asset", 25), Avg(
                    QvMath.Piece(y5.LastOrDefault()?.Npl, C(cfg, "T2.score.npl", 0, 0.015), C(cfg, "T2.score.npl", 1, 0.008), false),
                    QvMath.Piece(y5.LastOrDefault()?.ProvisionCov, C(cfg, "T2.score.provision", 0, 1.5), C(cfg, "T2.score.provision", 1, 4.0))
                ), $"不良 {y5.LastOrDefault()?.Npl:P2}，拨备 {y5.LastOrDefault()?.ProvisionCov:P0}");
                Track(cfg.D($"{pfx}.weights.asset", 25), y5.LastOrDefault()?.Npl is not null && y5.LastOrDefault()?.ProvisionCov is not null);
                var buf = row.CapitalBuffer ?? (y5.LastOrDefault()?.Cet1 is double c && row.RegulatoryRequirement is double rq ? c - rq : (double?)null);
                Dim(subs, "资本安全", cfg.D($"{pfx}.weights.capital", 20),
                    s.Type == "T2" ? QvMath.Piece(buf, C(cfg, "T2.score.buffer", 0, 0.0), C(cfg, "T2.score.buffer", 1, 0.025))
                                   : QvMath.Piece(y5.LastOrDefault()?.Leverage, 12, 6, false),
                    s.Type == "T2" ? $"资本缓冲 {buf:P2}（CET1 {y5.LastOrDefault()?.Cet1:P2} − 监管 {row.RegulatoryRequirement:P2}）" : $"杠杆 {y5.LastOrDefault()?.Leverage:F1}");
                Track(cfg.D($"{pfx}.weights.capital", 20), s.Type == "T2" ? y5.LastOrDefault()?.Cet1 is not null : true);
                Dim(subs, "经营效率与稳定性", cfg.D($"{pfx}.weights.efficiency", 10),
                    QvMath.Piece(QvMath.Min(y5.Select(x => x.Roe)), 0.04, 0.12),
                    $"最低年 ROE {QvMath.Min(y5.Select(x => x.Roe)):P1}");
                Track(cfg.D($"{pfx}.weights.efficiency", 10), roe is not null);
                if (s.DividendStatus is "dividend_missing")
                {
                    Dim(subs, "股东回报", cfg.D($"{pfx}.weights.shareholder", 15), 50,
                        "分红数据缺失，不按零分红计分，本项中性 50 并降低数据置信度。原因：" + s.DividendMissingReason);
                    Track(cfg.D($"{pfx}.weights.shareholder", 15), false);
                }
                else
                {
                    Dim(subs, "股东回报", cfg.D($"{pfx}.weights.shareholder", 15),
                        QvMath.Piece(QvMath.Mean(y5.Select(x => x.Payout)), C(cfg, "T2.score.payout", 0, 0.20), C(cfg, "T2.score.payout", 1, 0.35)),
                        $"分红率 {QvMath.Mean(y5.Select(x => x.Payout)):P0}（{s.DividendStatus}）");
                    Track(cfg.D($"{pfx}.weights.shareholder", 15), y5.Any(x => x.Payout is not null));
                }
                Dim(subs, "成长能力", cfg.D($"{pfx}.weights.growth", 10),
                    QvMath.Piece(y5.Count >= 3 ? QvMath.Cagr(y5[0].Np, y5[^1].Np, y5.Count - 1) : null, 0, 0.12),
                    $"净利润 CAGR {QvMath.Cagr(y5.First().Np, y5.Last().Np, Math.Max(1, y5.Count - 1)):P1}");
                Track(cfg.D($"{pfx}.weights.growth", 10), y5.Count >= 3);
                break;
            }
            case "T4":
            {
                Dim(subs, "现金流质量", cfg.D("T4.weights.cash", 25), Avg(
                    QvMath.Piece(QvMath.Mean(y5.Select(x => x.OcfToNp)), 1.0, 2.0),
                    QvMath.Piece(CapexOcf(y5), 1.0, 0.40, false)
                ), $"OCF/NP {QvMath.Mean(y5.Select(x => x.OcfToNp)):F2}，通道 {row.T4Channel}");
                Track(cfg.D("T4.weights.cash", 25), y5.Any(x => x.Ocf is not null));
                Dim(subs, "偿债能力", cfg.D("T4.weights.debt", 20),
                    QvMath.Piece(QvMath.Mean(y5.Select(x => x.InterestCoverage)), 3, 10),
                    $"利息保障 {QvMath.Mean(y5.Select(x => x.InterestCoverage)):F1}");
                Track(cfg.D("T4.weights.debt", 20), y5.Any(x => x.InterestCoverage is not null));
                Dim(subs, "盈利稳定性", cfg.D("T4.weights.profit_stab", 15),
                    QvMath.Piece(QvMath.Mean(y5.Select(x => x.Roe)), 0.04, 0.12),
                    $"ROE {QvMath.Mean(y5.Select(x => x.Roe)):P1}（B 通道允许低于 8%）");
                Track(cfg.D("T4.weights.profit_stab", 15), y5.Any(x => x.Roe is not null));
                Dim(subs, "股东回报", cfg.D("T4.weights.shareholder", 25),
                    QvMath.Piece(QvMath.Mean(y5.Select(x => x.Payout)), 0.30, 0.70),
                    $"分红率 {QvMath.Mean(y5.Select(x => x.Payout)):P0}");
                Track(cfg.D("T4.weights.shareholder", 25), y5.Any(x => x.Payout is not null || x.Dps is not null));
                var moat = data.Tags.TryGetValue(s.Code, out var tm) && tm.MoatConfirmed ? 80.0 : 50.0;
                Dim(subs, "资源壁垒", cfg.D("T4.weights.moat", 10), moat, moat > 60 ? "人工已确认" : "缺失人工标签，中性分");
                Track(cfg.D("T4.weights.moat", 10), data.Tags.TryGetValue(s.Code, out var tm2) && tm2.MoatConfirmed);
                if (moat <= 50) AddFlag(row, "moat_unconfirmed");
                GovDim(subs, cfg.D("T4.weights.gov", 5), s, data, row);
                Track(cfg.D("T4.weights.gov", 5), true);
                break;
            }
            case "T5":
            {
                var roe10 = QvMath.Mean(y10.Select(x => x.Roe));
                Dim(subs, "正常化盈利", cfg.D("T5.weights.profit", 20),
                    QvMath.Piece(roe10, C(cfg, "T5.score.roe_10y", 0, 0.06), C(cfg, "T5.score.roe_10y", 1, 0.14)),
                    $"10 年 ROE {roe10:P1}");
                Track(cfg.D("T5.weights.profit", 20), roe10 is not null);
                Dim(subs, "资产负债表安全", cfg.D("T5.weights.balance", 25), Avg(
                    QvMath.Piece(QvMath.Mean(y5.Select(x => x.InterestCoverage)), C(cfg, "T5.score.interest_coverage", 0, 4), C(cfg, "T5.score.interest_coverage", 1, 12)),
                    QvMath.Piece(QvMath.Mean(y5.Select(x => x.InterestDebtRatio)), 0.40, 0.10, false)
                ), $"利息保障 {QvMath.Mean(y5.Select(x => x.InterestCoverage)):F1}，有息负债率 {QvMath.Mean(y5.Select(x => x.InterestDebtRatio)):P1}");
                Track(cfg.D("T5.weights.balance", 25), y5.Any(x => x.InterestCoverage is not null || x.InterestDebtRatio is not null));
                var ratio = (double)y10.Count(a => a.Np is > 0) / Math.Max(1, y10.Count);
                Dim(subs, "周期穿越能力", cfg.D("T5.weights.cycle", 20),
                    QvMath.Piece(ratio, C(cfg, "T5.score.profit_year_ratio", 0, 0.60), C(cfg, "T5.score.profit_year_ratio", 1, 1.00)),
                    $"10 年盈利年份占比 {ratio:P0}，cycle_ratio {row.CycleRatio:F2}");
                Track(cfg.D("T5.weights.cycle", 20), y10.Count >= 6);
                Dim(subs, "股东回报", cfg.D("T5.weights.shareholder", 15),
                    QvMath.Piece(QvMath.Mean(y5.Select(x => x.Payout)), C(cfg, "T5.score.payout", 0, 0.20), C(cfg, "T5.score.payout", 1, 0.50)),
                    $"分红率 {QvMath.Mean(y5.Select(x => x.Payout)):P0}");
                Track(cfg.D("T5.weights.shareholder", 15), y5.Any(x => x.Payout is not null || x.Dps is not null));
                var gms = y10.Where(a => a.GrossMargin is not null).Select(a => a.GrossMargin!.Value).ToList();
                double? gmRange = gms.Count >= 4 ? gms.Max() - gms.Min() : null;
                Dim(subs, "成本竞争力", cfg.D("T5.weights.cost", 10),
                    QvMath.Piece(gmRange, C(cfg, "T5.score.gm_stability", 0, 0.20), C(cfg, "T5.score.gm_stability", 1, 0.05), false),
                    gmRange is null ? "缺毛利率或产能/单位成本（中性分，待补录）" : $"10 年毛利率极差 {gmRange:P1}（产能利用率不可得）");
                Track(cfg.D("T5.weights.cost", 10), gmRange is not null);
                if (gmRange is null) row.ManualNeeded = Join(row.ManualNeeded, "产能利用率/单位成本");
                GovDim(subs, cfg.D("T5.weights.gov", 10), s, data, row);
                Track(cfg.D("T5.weights.gov", 10), true);
                break;
            }
        }

        var wsum = subs.Sum(x => x.Weight);
        var score = wsum <= 0 ? (double?)null : subs.Sum(x => x.Score * x.Weight) / wsum;
        row.QualityScore = score;
        row.QualityLabel = score switch
        {
            >= 85 => "核心优质公司",
            >= 80 => "优质公司",
            >= 70 => "合格公司",
            >= 65 => "观察公司",
            _ => "不进入优质池",
        };

        if (total > 0) row.DataCoverage = obtained / total;
        else row.DataCoverage = null;
        row.CoverageDetail = total <= 0 ? "" : $"已取得有效指标权重 {obtained:F0}/{total:F0} = {row.DataCoverage:P0}";

        ApplyPool(row, cfg, s.Type);
    }

    internal static void ApplyPool(QvRow row, QvConfig cfg, string type)
    {
        if (row.Pool == "excluded") return;
        var qmin = cfg.QualifiedMin(type);
        var wmin = cfg.WatchMin(type);
        var cov = row.DataCoverage;
        var covWatch = cfg.D("monitoring.data_coverage.watch", 0.70);
        var covGap = cfg.D("monitoring.data_coverage.display_gap", 0.80);
        var keyGap = row.FlagList.Contains("data_insufficient") || (row.DataGaps ?? "").Length > 0 && row.ScreenSummary.StartsWith("数据不足");

        if (cov is not null && cov < covWatch)
        {
            AddFlag(row, "data_insufficient");
            row.Pool = "watch";
            row.ScreenSummary = Join(row.ScreenSummary, $"数据完整度 {cov:P0} < 70%，不参与正式排名");
            return;
        }
        if (cov is not null && cov < covGap)
        {
            AddFlag(row, "data_insufficient");
            if (row.ScreenSummary.StartsWith("通过")) row.ScreenSummary += $"；数据完整度 {cov:P0}，进观察池";
            row.Pool = "watch";
            return;
        }
        if (keyGap && row.Pool != "excluded")
        {
            row.Pool = "watch";
            return;
        }
        // cycle_high 不再把合格公司打入观察池（只阻断强提醒）
        if (row.QualityScore is null) { row.Pool = "watch"; return; }
        // 行业相对通道不抬高/重标化原始分数；合格资格由独立、可追溯的通道决定。
        if (row.QualityAdmissionChannel == "industry_relative")
        {
            row.Pool = "qualified";
            row.QualityLabel = $"行业相对优质（原始评分 {row.QualityScore:F1}）";
            return;
        }
        if (row.QualityScore < wmin) { row.Pool = "excluded"; }
        else if (row.QualityScore < qmin) { row.Pool = "watch"; }
        else if (row.ScreenSummary.StartsWith("通过")) row.Pool = "qualified";
        else if (row.Pool is "candidate" or "") row.Pool = "watch";
    }

    private static void GovDim(List<QvSubScore> subs, double w, QvStock s, QvData data, QvRow row)
    {
        var sc = 70.0;
        if (row.FlagList.Contains("red_flag")) sc -= 25;
        if (data.PledgeCtrl.TryGetValue(s.Code, out var pl) && pl > 0.3) sc -= 10;
        Dim(subs, "治理合规", w, Math.Clamp(sc, 0, 100), row.FlagList.Contains("red_flag") ? "存在红旗标记" : "无重大红旗");
    }

    private static double PeerOrPiece(double? value, IReadOnlyList<QvStock>? peers, string type,
        Func<List<QvAnnual>, double?> pick, double fail, double full)
    {
        var abs = QvMath.Piece(value, fail, full);
        if (value is null || peers is null) return abs;
        var xs = peers.Where(p => p.Type == type).Select(p => pick(p.Annuals)).Where(x => x is not null).Select(x => x!.Value).ToList();
        if (xs.Count < 5) return abs;
        var below = xs.Count(x => x <= value.Value);
        var pct = below / (double)xs.Count; // 0-1 分位
        var peerScore = pct * 100;
        return 0.5 * abs + 0.5 * peerScore;
    }

    private static void Dim(List<QvSubScore> list, string name, double w, double score, string detail)
        => list.Add(new QvSubScore { Name = name, Weight = w, Score = Math.Clamp(score, 0, 100), Detail = detail });

    private static double Avg(params double[] xs) => xs.Length == 0 ? 50 : xs.Average();
    private static double C(QvConfig cfg, string path, int idx, double def = 0)
    {
        var n = cfg.Node(path);
        if (n is System.Text.Json.Nodes.JsonArray a && a.Count > idx)
            return QvConfig.AsDouble(a[idx], def);
        return def;
    }
    private static double? FcfNp(List<QvAnnual> y)
        => QvMath.Mean(y.Where(a => (a.FcfMaint ?? a.Fcf) is not null && a.Np is > 0).Select(a => (a.FcfMaint ?? a.Fcf) / a.Np));
    private static double? CapexOcf(List<QvAnnual> y)
    {
        var xs = y.Where(a => a.Ocf is > 0 && a.Capex is not null).Select(a => a.Capex!.Value / a.Ocf!.Value).ToList();
        return xs.Count == 0 ? null : xs.Average();
    }
    private static double? NetDebt(QvAnnual? a)
    {
        if (a?.InterestDebtRatio is null || a.TotalAssets is null || a.Cash is null) return null;
        return a.InterestDebtRatio.Value * a.TotalAssets.Value - a.Cash.Value;
    }
    private static double DilutionPenalty(List<QvAnnual> y)
    {
        if (y.Count < 3 || y[0].TotalShare is not > 0 || y[^1].TotalShare is not > 0) return 0;
        var cagr = QvMath.Cagr(y[0].TotalShare, y[^1].TotalShare, y.Count - 1);
        if (cagr is > 0.03) return Math.Min(30, (cagr.Value - 0.03) * 400);
        return 0;
    }
    private static void AddFlag(QvRow r, string f) { if (!r.FlagList.Contains(f)) r.FlagList.Add(f); }
    private static string Join(string? a, string b) => string.IsNullOrWhiteSpace(a) ? b : a + "；" + b;
}
