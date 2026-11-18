namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// 一期财报（累计或已拆出的单季度）。仅供复核模块使用，不进入评分/估值。
/// </summary>
public sealed class QvPeriod
{
    public string ReportDate { get; set; } = "";
    public string PeriodType { get; set; } = ""; // annual / q1 / h1 / q3 / other / single_q1..q4 / ttm
    public bool IsCumulative { get; set; } = true;
    public string Source { get; set; } = "东财 MAINFINADATA";
    public double? Revenue { get; set; }
    public double? Np { get; set; }
    public double? DeductNp { get; set; }
    public double? Roe { get; set; }
    public double? GrossMargin { get; set; }
    public double? Ocf { get; set; }
    public double? OcfToNp { get; set; }
    public double? Ar { get; set; }
    public double? Inventory { get; set; }
}

public sealed class QvForecastItem
{
    public string Period { get; set; } = "";
    public string Type { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Source { get; set; } = "东财业绩预告/快报";
}

public sealed class QvNoticeItem
{
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Major { get; set; }
}
