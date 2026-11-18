using System.Text.Json.Serialization;

namespace ThreeBucket.Core.Services.QualityValue;

public enum QvPool { Excluded, Watch, Qualified }
public enum QvZone { None, Strike, Approach, Fair, Optimistic }

public sealed class QvAnnual
{
    public int Year { get; set; }
    public string ReportDate { get; set; } = "";
    public double? Revenue, Np, DeductNp, Roe, Roic, NetMargin, GrossMargin;
    public double? DebtRatio, InterestDebtRatio, InterestCoverage, OcfToNp;
    public double? Bps, Eps, DeductEps, OcfPs, TotalShare, TotalAssets, Equity;
    public double? Ocf, Capex, Fcf, Depreciation, FcfMaint;
    public double? Cash, TradeFinAsset, Goodwill, Ar, ArDays, Inventory;
    public double? Npl, ProvisionCov, Cet1, Car, Tier1, Nim, GrossLoans, Deposits, Overdue, Leverage;
    public double? Dps, Payout, Buyback;
    public string Source { get; set; } = "";
}

public sealed class QvQuote
{
    public double? Price, PrevClose, PeTtm, Pb, Turnover60, AmountToday;
    public string AsOf { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class QvStock
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Board { get; set; } = "";
    public string IndustryRaw { get; set; } = "";
    public string Type { get; set; } = "";          // T1..T5 or ""
    public string TypeSource { get; set; } = "";    // mapping / override / keyword / unmapped / excluded
    public string ListDate { get; set; } = "";
    public List<QvAnnual> Annuals { get; set; } = new();
    public QvQuote Quote { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public Dictionary<string, string> Extra { get; set; } = new(); // 审计、质押、处罚原文等

    // v1.2 P0
    public string DividendStatus { get; set; } = "";
    public string DividendSource { get; set; } = "";
    public string DividendAsOf { get; set; } = "";
    public string DividendMissingReason { get; set; } = "";
    public double? PbCurrent, PbMedian3y, PbMedian5y;
    public string PbCurrentSource { get; set; } = "";
    public string PbHistorySource { get; set; } = "";
    public string PbHistoryAsOf { get; set; } = "";
    public string BankTypeKey { get; set; } = "";
    public string BankPeerGroup { get; set; } = "";
    public List<(int Year, double Pb)> PbHistory { get; set; } = new();

    // v1.5.2 复核（不参与评分/估值）
    public List<QvPeriod> Periods { get; set; } = new();
    public List<QvForecastItem> Forecasts { get; set; } = new();
    public List<QvNoticeItem> Notices { get; set; } = new();
    public bool ForecastsMissing, NoticesMissing;
}

public sealed class QvGateItem
{
    public string Name { get; set; } = "";
    public string Result { get; set; } = "";   // pass / fail / soft / missing
    public string Detail { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class QvSubScore
{
    public string Name { get; set; } = "";
    public double Weight { get; set; }
    public double Score { get; set; }          // 0-100
    public string Detail { get; set; } = "";
}

public sealed class QvValuationMethod
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";     // pricing / cross
    public double? Fair, Safe, Opt;
    public string Detail { get; set; } = "";
}

public sealed class QvRow
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string TypeSource { get; set; } = "";
    public string Industry { get; set; } = "";
    public string Pool { get; set; } = "";
    public string Zone { get; set; } = "";
    public string Flags { get; set; } = "";
    public List<string> FlagList { get; set; } = new();
    public double? QualityScore { get; set; }
    public List<QvSubScore> SubScores { get; set; } = new();
    public List<QvGateItem> Gates { get; set; } = new();
    public string ScreenSummary { get; set; } = "";
    public string GateSummary { get; set; } = "";
    public double? Price, PSafe, PFair, POpt;
    public double? PeSafe, PeFair, PeOpt, PbSafe, PbFair, PbOpt;
    public double? DySafe, DyFair, DyOpt;
    public double? PositionPct;                 // (P_opt - P) / (P_opt - P_safe) or similar
    public double? Dispersion;
    public double? Rf, ReBase;
    public double? CycleRatio, Mos;
    public List<QvValuationMethod> Methods { get; set; } = new();
    public string ValuationDetail { get; set; } = "";
    public string Chain { get; set; } = "";
    public string AsOf { get; set; } = "";
    public string DataGaps { get; set; } = "";
    public bool RedFlagUnreviewed { get; set; }
    public int HoldDays { get; set; }
    // v1.1
    public double? DataCoverage, CapitalBuffer, RegulatoryRequirement, Cet1Latest, YRequired;
    public string ValuationConfidence { get; set; } = "";
    public string T4Channel { get; set; } = "";
    public string CoverageDetail { get; set; } = "";
    public string ManualNeeded { get; set; } = "";
    public string QualityLabel { get; set; } = "";
    public string ReverseNote { get; set; } = "";
    // v1.5.4：公司质量、价格监控资格与行业相对通道分别记录；不把流动性当公司经营质量。
    public bool? PriceMonitoringEligible { get; set; }
    public double? MonitoringTurnover60 { get; set; }
    public string MonitoringLiquidityStatus { get; set; } = "";
    public string MonitoringReason { get; set; } = "";
    public string MonitoringAsOf { get; set; } = "";
    public string ValuationAdjustmentAsOf { get; set; } = "";
    public string QualityAdmissionChannel { get; set; } = "";
    public string AbsoluteAdmissionResult { get; set; } = "";
    public string IndustryRelativeGroup { get; set; } = "";
    public string IndustryRelativeDecision { get; set; } = "";
    public string IndustryRelativeDetail { get; set; } = "";
    public string IndustryRelativeBypassed { get; set; } = "";
    public double? IndustryRelativeComposite { get; set; }
    public List<QvIndustryMetric> IndustryRelativeMetrics { get; set; } = new();

    // v1.2 P0
    public double? PbCurrent, PbModel, PbPeer, PbFairFinal, PbWarning;
    public bool UsePbCap;
    public double? RoeSus, ReBank, GLong;
    public string BankType { get; set; } = "";
    public string ValuationStatus { get; set; } = "";
    public string DataQualityGrade { get; set; } = "";
    public double? DataConfidenceScore, SignalConfidence;
    public string DataConfidenceLabel { get; set; } = "";
    public string DataQualityDetail { get; set; } = "";
    public string DividendStatus { get; set; } = "";
    public bool CapitalRequirementUnknown;
    public string CapitalVerificationStatus { get; set; } = "";
    public string CapitalConfidence { get; set; } = "";
    public int PeerSampleN;
    public double? PeerWeightUsed;
    public string ReBreakdown { get; set; } = "";
    public string BankValuationBreakdown { get; set; } = "";
    public bool StrongAlertBlocked;
    public string StrongAlertBlockReason { get; set; } = "";

    // v1.5.2 复核（不修改评分/估值）
    public string ReviewClass { get; set; } = "";
    public int ReviewPriority { get; set; }
    public string ReviewPriorityLevel { get; set; } = "";
    public string ReviewTriggers { get; set; } = "";
    public bool ReviewQueued { get; set; }
    public bool ReviewDeduped { get; set; }
    public string ReviewFingerprint { get; set; } = "";
    public string ReviewCard { get; set; } = "";
    public string EarningsSeverity { get; set; } = "";
    public string EarningsSummary { get; set; } = "";
    public bool ValuationInvalidityRisk { get; set; }
    public string StrongAlertReviewAction { get; set; } = "";
    public string ReviewSuggestedAction { get; set; } = "";
}

public sealed class QvOutput
{
    public string RuleExecutedAt {get;set;}=""; public string EventInputAsOf {get;set;}="";
    public Dictionary<string,string> InputSources {get;set;}=new();
    public Dictionary<string,string> InputWarnings {get;set;}=new();
    public int NetworkCalls {get;set;} public int CacheHits {get;set;} public int CacheMisses {get;set;}

    public string Mode = "", AsOf = "", OutDir = "", ReportPath = "", LatestCsv = "", RfSource = "";
    public double? Rf;
    public int Universe, Classified, Qualified, Watch, Excluded, Unmapped;
    public int Strike, Approach, Fair, Optimistic, CoreQuality;
    public Dictionary<string, int> ByType = new();
    public Dictionary<string, int> ByPool = new();
    public Dictionary<string, int> ByZone = new();
    public List<QvRow> Rows = new();
    public List<string> Alerts = new();
    public List<string> Gaps = new();
    public List<QvIndustryPeer> IndustryRelativePeers = new();
}

public sealed class QvManualTag
{
    public string Code { get; set; } = "";
    public bool MoatConfirmed, ReviewedOk;
    public double? NplFormation, SpecialMention, ReLoanPct, PlatformLoanPct;
    public string FundingNote { get; set; } = "";
    public string MarketShare { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class QvSupplementBank
{
    public string Code { get; set; } = "";
    public string Period { get; set; } = "";
    public double? Npl, ProvisionCov, Cet1, Rwa, SpecialMention, ReLoanPct, PlatformLoanPct, NplFormation;
}

public static class QvMath
{
    public static double? Mean(IEnumerable<double?> xs)
    {
        var a = xs.Where(x => x is > 0 or < 0 or 0.0).Select(x => x!.Value).ToList();
        // allow zeros; reject NaN
        a = xs.Where(x => x.HasValue && !double.IsNaN(x.Value)).Select(x => x!.Value).ToList();
        return a.Count == 0 ? null : a.Average();
    }

    public static double? Min(IEnumerable<double?> xs)
    {
        var a = xs.Where(x => x.HasValue && !double.IsNaN(x.Value)).Select(x => x!.Value).ToList();
        return a.Count == 0 ? null : a.Min();
    }

    public static double? Max(IEnumerable<double?> xs)
    {
        var a = xs.Where(x => x.HasValue && !double.IsNaN(x.Value)).Select(x => x!.Value).ToList();
        return a.Count == 0 ? null : a.Max();
    }

    public static double? Cagr(double? first, double? last, int years)
    {
        if (first is not > 0 || last is not > 0 || years <= 0) return null;
        return Math.Pow(last.Value / first.Value, 1.0 / years) - 1;
    }

    /// <summary>分段线性：higherIsBetter 时 fail→0、full→100；否则相反。缺失返回中性 50。</summary>
    public static double Piece(double? x, double fail, double full, bool higherIsBetter = true)
    {
        if (x is null || double.IsNaN(x.Value)) return 50;
        var v = x.Value;
        if (higherIsBetter)
        {
            if (v <= fail) return 0;
            if (v >= full) return 100;
            return (v - fail) / (full - fail) * 100;
        }
        if (v >= fail) return 0;
        if (v <= full) return 100;
        return (fail - v) / (fail - full) * 100;
    }

    public static double? Median(IEnumerable<double?> xs)
    {
        var a = xs.Where(x => x.HasValue && !double.IsNaN(x.Value)).Select(x => x!.Value).OrderBy(x => x).ToList();
        if (a.Count == 0) return null;
        if (a.Count % 2 == 1) return a[a.Count / 2];
        return (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2.0;
    }

    public static double Percentile(IReadOnlyList<double> xs, double p)
    {
        if (xs.Count == 0) return double.NaN;
        var a = xs.OrderBy(x => x).ToList();
        var i = (a.Count - 1) * p;
        var lo = (int)Math.Floor(i); var hi = (int)Math.Ceiling(i);
        if (lo == hi) return a[lo];
        return a[lo] * (hi - i) + a[hi] * (i - lo);
    }
}


public sealed class QvBankCapitalReq
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string BankType { get; set; } = "";
    public string DSibLevel { get; set; } = "";
    public double? Cet1Min, ConservationBuffer, DSibAddon, OtherAddon, Pillar2;
    public double Requirement { get; set; } = 0.075; // indicative only when pending
    public string Components { get; set; } = "5% CET1 + 2.5% conservation (indicative, unverified)";
    public string Source { get; set; } = "default; not listed in bank_capital_req.csv";
    public string SourceDate { get; set; } = "";
    public string VerifiedAt { get; set; } = "";
    public string VerificationStatus { get; set; } = "pending";
    public string Notes { get; set; } = "";
    public bool NeedsReview { get; set; } = true;
    public bool IsPending => NeedsReview || string.IsNullOrWhiteSpace(VerificationStatus) || VerificationStatus.Equals("pending", StringComparison.OrdinalIgnoreCase);
}
