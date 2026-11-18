using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
namespace ThreeBucket.Core.Services;

/// <summary>Additive, versioned metadata. Never replaces provider payload or feeds changed values to a strategy.</summary>
public sealed record FinancialValue
{
    public string Provider {get;init;}=""; public string Security {get;init;}="";
    public string Resource {get;init;}=""; public string ReportPeriod {get;init;}="";
    public string ReportType {get;init;}="unknown"; public string PublishedAt {get;init;}="unknown";
    public string RevisedAt {get;init;}="unknown"; public string FetchedAt {get;init;}="unknown";
    public string RevisionSet {get;init;}="unknown"; public string Scope {get;init;}="unverified";
    public string Basis {get;init;}="unverified"; public string Currency {get;init;}="unverified";
    public string RawField {get;init;}=""; public string RawJson {get;init;}="null";
    public string Unit {get;init;}="unverified"; public double Scale {get;init;}=1;
    public string Denominator {get;init;}="not_applicable"; public string Sign {get;init;}="provider_signed";
    public string Source {get;init;}=""; public string Status {get;init;}="unverified";
    public double? Value {get;init;}
    public double? CanonicalValue => Value is {} v ? v*Scale : null;
}
public sealed record FinancialDerived(string Formula, IReadOnlyList<FinancialValue> Inputs, double? Value, string Status);
public sealed class FinancialContract
{
    public const string Version="financial-contract-v1";
    public string Schema {get;set;}=Version;
    public string Warning {get;set;}="Metadata only; original payload and legacy consumer formulas are unchanged. Unknown fields remain unverified; normalized values are separate.";
    public Dictionary<string,string> Fields {get;set;}=new();
    public List<FinancialValue> Values {get;set;}=new();
    public static FinancialContract Descriptor(string provider="eastmoney")=>new(){Fields=(provider=="eastmoney"?Known:new Dictionary<string,Def>()).ToDictionary(x=>x.Key,x=>$"unit={x.Value.Unit};scale={x.Value.Scale.ToString(CultureInfo.InvariantCulture)};scope={x.Value.Scope};denominator={x.Value.Denominator};sign={x.Value.Sign}"),Warning="Original values remain in Payload. Lazy projection by financial-contract-v1; raw fields not listed are unverified. No strategy input conversion."};
    private sealed record Def(string Unit,double Scale,string Scope,string Denominator="not_applicable",string Sign="provider_signed");
    private static readonly Dictionary<string,Def> Known=new(StringComparer.Ordinal)
    {
        ["NETCASH_OPERATE"]=new("CNY",1,"consolidated"),["TOTAL_OPERATE_INCOME"]=new("CNY",1,"consolidated"),
        ["TOTALOPERATEREVE"]=new("CNY",1,"consolidated"),
        ["PARENTNETPROFIT"]=new("CNY",1,"attributable_to_parent"),
        ["KCFJCXSYJLR"]=new("CNY",1,"attributable_to_parent_excluding_nonrecurring"),
        ["OPERATE_INCOME"]=new("CNY",1,"consolidated"),["NETPROFIT"]=new("CNY",1,"consolidated"),
        ["TOTAL_NETPROFIT"]=new("CNY",1,"consolidated"),["PARENT_NETPROFIT"]=new("CNY",1,"attributable_to_parent"),
        ["DEDUCT_PARENT_NETPROFIT"]=new("CNY",1,"attributable_to_parent_excluding_nonrecurring"),
        ["DEDUCT_PARENT_NETPROFIT_RATIO"]=new("percent",0.01,"attributable_to_parent_excluding_nonrecurring","prior_year_same_period"),
        ["WEIGHTAVG_ROE"]=new("percent",0.01,"attributable_to_parent","weighted_average_parent_equity"),
        ["BASIC_EPS"]=new("CNY_per_share",1,"attributable_to_parent"),
        ["ROEJQ"]=new("percent",0.01,"attributable_to_parent","weighted_average_parent_equity"),
        ["NCO_NETPROFIT"]=new("ratio",1,"consolidated","consolidated_total_net_profit"),
        ["XSMLL"]=new("percent",0.01,"consolidated","operating_revenue"),
        ["SJLTZ"]=new("percent",0.01,"attributable_to_parent","prior_year_same_period"),
        ["YSTZ"]=new("percent",0.01,"consolidated","prior_year_same_period"),
        ["BPS"]=new("CNY_per_share",1,"provider_defined"),["EPSJB"]=new("CNY_per_share",1,"attributable_to_parent"),
        ["MGJYXJJE"]=new("CNY_per_share",1,"consolidated","provider_share_count"),
        ["CONSTRUCT_LONG_ASSET"]=new("CNY",1,"consolidated","not_applicable","cash_outflow_reported_positive"),
        ["TOTAL_ASSETS"]=new("CNY",1,"consolidated"),["TOTAL_LIABILITIES"]=new("CNY",1,"consolidated"),
        ["TOTAL_PARENT_EQUITY"]=new("CNY",1,"attributable_to_parent"),["MONETARYFUNDS"]=new("CNY",1,"consolidated"),
        ["INVENTORY"]=new("CNY",1,"consolidated"),["ACCOUNTS_RECE"]=new("CNY",1,"consolidated")
    };
    public static string CanonicalSecurity(string code)
    {
        var m=System.Text.RegularExpressions.Regex.Match(code.ToUpperInvariant(),@"^(?:(SH|SZ|BJ))?(\d{6})(?:\.(SH|SZ|BJ))?$");
        if(!m.Success)return code;
        var c=m.Groups[2].Value;var ex=m.Groups[3].Success?m.Groups[3].Value:m.Groups[1].Success?m.Groups[1].Value:c.StartsWith("920")||c[0] is '4' or '8'?"BJ":c[0]=='6'?"SH":"SZ";
        return c+"."+ex;
    }
    public static IEnumerable<JsonElement> Rows(JsonElement payload)
    {
        if(payload.ValueKind==JsonValueKind.Array)return payload.EnumerateArray().ToArray();
        if(payload.ValueKind!=JsonValueKind.Object)return [];
        if(payload.TryGetProperty("result",out var r)&&r.ValueKind==JsonValueKind.Object&&r.TryGetProperty("data",out var d)&&d.ValueKind==JsonValueKind.Array)return d.EnumerateArray().ToArray();
        if(payload.TryGetProperty("item",out var a)&&a.ValueKind==JsonValueKind.Array)return a.EnumerateArray().ToArray();
        return [];
    }
    public static FinancialContract Describe(JsonElement payload,string provider,string resource,string security,string source,DateTime fetched,string revision)
    {
        var c=new FinancialContract();
        foreach(var row in Rows(payload))
        {
            if(row.ValueKind!=JsonValueKind.Object)continue;
            string S(params string[] keys){foreach(var key in keys)if(row.TryGetProperty(key,out var v)&&v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)&&v.ToString().Length>0)return v.ToString();return "unknown";}
            string DateValue(string value){if(value.Length==8&&DateTime.TryParseExact(value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))return date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);return value.Length>10?value[..10]:value;}
            var period=DateValue(S("REPORT_DATE","REPORTDATE","end_date"));
            var annual=period.EndsWith("12-31",StringComparison.Ordinal);var hasPeriod=DateTime.TryParseExact(period,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var reportDate);var balance=resource.Contains("BALANCE",StringComparison.OrdinalIgnoreCase);
            foreach(var prop in row.EnumerateObject())
            {
                Known.TryGetValue(prop.Name,out var def); if(provider!="eastmoney")def=null;
                var raw=prop.Value;double? n=null;
                if(raw.ValueKind==JsonValueKind.Number&&raw.TryGetDouble(out var num)&&double.IsFinite(num))n=num;
                else if(raw.ValueKind==JsonValueKind.String&&double.TryParse(raw.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var str)&&double.IsFinite(str))n=str;
                c.Values.Add(new(){Provider=provider,Security=CanonicalSecurity(S("SECUCODE","SECURITY_CODE","ts_code") is var sc&&sc!="unknown"?sc:security),Resource=resource,
                    ReportPeriod=period,ReportType=!hasPeriod?"unknown":annual?"annual":reportDate.Month==6?"halfyear":"quarterly",PublishedAt=DateValue(S("NOTICE_DATE","PLAN_NOTICE_DATE","ann_date")),RevisedAt=DateValue(S("UPDATE_DATE","update_date")),FetchedAt=fetched.ToString("O"),RevisionSet=revision,
                    Scope=def?.Scope??"provider_defined/unverified",Basis=def==null||!hasPeriod?"provider_defined/unverified":balance?"instant":def.Unit is "percent" or "ratio"?"provider_ratio_not_additive":annual?"annual_cumulative":"cumulative",
                    Currency=def?.Unit.StartsWith("CNY")==true?"CNY":"not_applicable_or_unverified",RawField=prop.Name,RawJson=raw.GetRawText(),Unit=def?.Unit??"provider_defined/unverified",Scale=def?.Scale??1,
                    Denominator=def?.Denominator??"unverified",Sign=def?.Sign??"provider_signed/unverified",Source=source,Status=def==null?"unverified":n==null?"missing":"mapped_provider_definition",Value=n});
            }
        }
        return c;
    }
    private static bool Compatible(FinancialValue a,FinancialValue b)=>a.Provider==b.Provider&&a.Resource==b.Resource&&a.Security==b.Security&&a.Scope==b.Scope&&a.Unit==b.Unit&&a.Scale==b.Scale&&a.Currency==b.Currency&&a.RawField==b.RawField&&a.RevisionSet!="unknown"&&a.RevisionSet==b.RevisionSet&&a.Status!="unverified"&&b.Status!="unverified";
    public static FinancialDerived SingleQuarter(FinancialValue current,FinancialValue? prior)
    {
        if(!DateTime.TryParse(current.ReportPeriod,out var end)||end.Month%3!=0||end.Day!=DateTime.DaysInMonth(end.Year,end.Month)||current.Basis is not ("annual_cumulative" or "cumulative")||current.Unit is not ("CNY" or "shares"))return new("singleQ",[current],null,"pending_nonadditive_or_basis");
        if(end.Month==3)return new("Q1=cumulative",[current],current.CanonicalValue,current.Value==null?"pending_missing":"derived");
        if(prior==null||!Compatible(current,prior)||!DateTime.TryParse(prior.ReportPeriod,out var prev)||prev.Year!=end.Year||prev!=new DateTime(end.Year,end.Month-3,DateTime.DaysInMonth(end.Year,end.Month-3))||prior.Basis!="cumulative"||current.Value==null||prior.Value==null)return new("singleQ=current_same_year_cumulative-prior_same_scope_revision_cumulative",prior==null?[current]:[current,prior],null,"pending_missing_scope_revision_or_period");
        return new("singleQ=current_same_year_cumulative-prior_same_scope_revision_cumulative",[current,prior],current.CanonicalValue-prior.CanonicalValue,"derived");
    }
    public static FinancialDerived Ttm(IReadOnlyList<FinancialValue> quarters)
    {
        var q=quarters.OrderBy(x=>x.ReportPeriod,StringComparer.Ordinal).ToList();
        if(q.Count!=4||q.Any(x=>x.Basis!="single_quarter"||x.Unit!="CNY"||x.Value==null)||q.Any(x=>!Compatible(q[0],x)))return new("TTM=sum(4 comparable singleQ)",q,null,"pending_incomplete_or_incompatible");
        for(var i=0;i<q.Count;i++){if(!DateTime.TryParse(q[i].ReportPeriod,out var d)||d.Month%3!=0||d.Day!=DateTime.DaysInMonth(d.Year,d.Month))return new("TTM",q,null,"pending_period");if(i>0){var prev=DateTime.Parse(q[i-1].ReportPeriod,CultureInfo.InvariantCulture);var next=prev.AddMonths(3);next=new(next.Year,next.Month,DateTime.DaysInMonth(next.Year,next.Month));if(d!=next)return new("TTM",q,null,"pending_gap");}}
        return new("TTM=sum(4 comparable singleQ)",q,q.Sum(x=>x.CanonicalValue!.Value),"derived");
    }

    public static FinancialContract ReadProjection(string path,string kind,string security)
    {
        if(kind is not ("qv-mainfin" or "qv-cashflow" or "em-cashflow" or "em-yjbb"))throw new ArgumentException("Explicit known legacy kind required; no inferred raw provider equivalence");
        using var doc=JsonDocument.Parse(File.ReadAllText(path));if(doc.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("legacy projection must be a typed row array");
        var map=new Dictionary<string,string>{{"Revenue","TOTALOPERATEREVE"},{"Np","PARENTNETPROFIT"},{"DeductNp","KCFJCXSYJLR"},{"Roe",kind=="em-yjbb"?"WEIGHTAVG_ROE":"ROEJQ"},{"OcfToNp","NCO_NETPROFIT"},{"Ocf","NETCASH_OPERATE"},{"Capex","CONSTRUCT_LONG_ASSET"},{"GrossMargin","XSMLL"},{"Eps","EPSJB"},{"Bps","BPS"},{"OcfPs","MGJYXJJE"}};
        var c=new FinancialContract{Warning="Retained typed projection, NOT original provider report. RawJson is original retained cache value; missing vendor raw/publication/fetch time is UNKNOWN. Field mapping established from existing source code; no inverse normalization. Existing legacy percentage heuristics are not redefined by this metadata."};
        var rev=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        foreach(var row in doc.RootElement.EnumerateArray())
        {
            var period=row.TryGetProperty("ReportDate",out var d)?d.ToString():"unknown";if(period.Length>10)period=period[..10];
            foreach(var p in row.EnumerateObject())
            {
                Def? def=null;string? field=null;if(map.TryGetValue(p.Name,out field))Known.TryGetValue(field,out def);
                double? n=p.Value.ValueKind==JsonValueKind.Number&&p.Value.TryGetDouble(out var number)&&double.IsFinite(number)?number:null;
                c.Values.Add(new(){Provider="legacy-eastmoney-csharp-projection",Resource=kind,Security=CanonicalSecurity(security),ReportPeriod=period,ReportType=period.EndsWith("12-31")?"annual":"interim",RevisionSet=rev,RawField=p.Name,RawJson=p.Value.GetRawText(),Value=n,
                    Unit=def?.Unit??"unverified",Scale=def?.Scale??1,Scope=def?.Scope??"unverified",Currency=def?.Unit.StartsWith("CNY")==true?"CNY":"not_applicable_or_unverified",Denominator=def?.Denominator??"unverified",Sign=def?.Sign??"unverified",Basis=def?.Unit is "ratio" or "percent"?"provider_ratio_not_additive":period.EndsWith("12-31")?"annual_cumulative":"cumulative",Status=def==null?"unverified":n==null?"missing":"legacy_projection_code_mapped/vendor_raw_not_retained",Source=path+"; source-field="+(field??"unknown")+"; mapper=QvData.FetchMainFin/EastMoneyClient; fetch/publication not retained"});
            }
        }
        return c;
    }

    public static FinancialContract ReadEnvelope(string path)
    {
        using var d=JsonDocument.Parse(File.ReadAllText(path));var e=d.RootElement;
        if(e.TryGetProperty("Contract",out var c)&&c.ValueKind==JsonValueKind.Object){var mapped=c.Deserialize<FinancialContract>();if(mapped?.Schema!=Version)throw new InvalidDataException("Unsupported financial contract version");if(mapped.Values.Count>0)return mapped;}
        string S(string k)=>e.TryGetProperty(k,out var v)?v.ToString():"unknown";
        var payload=e.TryGetProperty("Payload",out var p)?p:e;
        return Describe(payload,S("Provider"),S("Resource"),S("Security"),S("Source"),DateTime.TryParse(S("FetchedAt"),out var f)?f:DateTime.MinValue,S("Hash"));
    }
}
