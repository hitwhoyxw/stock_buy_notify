namespace ThreeBucket.Core.Models;

/// <summary>交易所枚举。</summary>
public enum Exchange
{
    Unknown,
    Shanghai, // sh
    Shenzhen, // sz
    Beijing,  // bj
}

/// <summary>
/// 股票代码的标准化表示。
/// 新浪/腾讯接口使用带市场前缀的格式（如 <c>sh600519</c>、<c>sz000001</c>）。
/// 本结构负责解析前缀，并可在需要时补齐默认前缀。
/// </summary>
public readonly record struct StockCode
{
    public string Symbol { get; }   // 标准化后的代码，如 "sh600519"
    public Exchange Exchange { get; }
    public string Code { get; }     // 纯数字代码，如 "600519"

    public StockCode(string symbol)
    {
        var s = (symbol ?? string.Empty).Trim().ToLowerInvariant();
        if (s.StartsWith("sh"))
        {
            Exchange = Exchange.Shanghai;
            Code = s.Length > 2 ? s[2..] : s;
            Symbol = s;
        }
        else if (s.StartsWith("sz"))
        {
            Exchange = Exchange.Shenzhen;
            Code = s.Length > 2 ? s[2..] : s;
            Symbol = s;
        }
        else if (s.StartsWith("bj"))
        {
            Exchange = Exchange.Beijing;
            Code = s.Length > 2 ? s[2..] : s;
            Symbol = s;
        }
        else
        {
            // 无前缀：默认按上交所处理
            Exchange = Exchange.Shanghai;
            Code = s;
            Symbol = "sh" + s;
        }
    }
}
