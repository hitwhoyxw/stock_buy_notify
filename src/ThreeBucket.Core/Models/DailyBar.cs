namespace ThreeBucket.Core.Models;

/// <summary>日K一根（腾讯/新浪日K接口归一化后的结构，日期为自然日升序中的一项）。</summary>
public record DailyBar(DateTime Date, double Open, double Close, double High, double Low, double Volume);
