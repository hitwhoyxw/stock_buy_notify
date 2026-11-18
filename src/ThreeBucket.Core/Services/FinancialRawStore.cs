using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ThreeBucket.Core.Services;

// Shared across strategies. No prices, scores, strategy gates or API credentials are stored here.
public sealed class FinancialAccess : IDisposable
{
    private static readonly AsyncLocal<FinancialAccess?> Slot = new();
    public static FinancialAccess? Current => Slot.Value;
    private readonly FinancialAccess? _previous;
    public bool Offline { get; init; }
    public bool AllowFinancialNetwork { get; init; }
    public bool AllowCorporateActionsNetwork { get; init; }
    public bool ThrowOnNetwork { get; init; }
    public int CycleMonths { get; init; } = 6;
    public int OutboundCalls, CacheHits, CacheMisses;
    public readonly ConcurrentDictionary<string,string> Sources = new();
    public bool MissingOnly {get;set;}
    public readonly ConcurrentDictionary<string,int> Calls = new();
    public readonly ConcurrentDictionary<string,string> RefreshFailures = new();
    public FinancialAccess() { _previous=Slot.Value; Slot.Value=this; }
    public void BeforeNetwork(string resource, bool financial=false)
    {
        Interlocked.Increment(ref OutboundCalls); Calls.AddOrUpdate(resource,1,(_,v)=>v+1);
        if (Offline || ThrowOnNetwork || (financial && !AllowFinancialNetwork))
            throw new InvalidOperationException("NETWORK_FORBIDDEN: "+resource);
    }
    public void Hit(string path) { Interlocked.Increment(ref CacheHits); try { Sources[path]=File.GetLastWriteTime(path).ToString("O"); } catch {} }
    public void Missing(string path) { Interlocked.Increment(ref CacheMisses); Sources[path]="missing/pending"; }
    public void Dispose() { Slot.Value=_previous; }
    public static int ConfiguredCycle(string cacheDir)
    {
        var d=new DirectoryInfo(Path.GetFullPath(cacheDir));
        for(int i=0;i<6 && d!=null;i++,d=d.Parent)
        {
            var p=Path.Combine(d.FullName,"config","financial_cache.yaml");
            if(!File.Exists(p))continue;
            var node=QualityValue.QvYaml.ParseFile(p);ConfigGovernance.Validate(node,"financial_cache.yaml",p);
            return (int)ConfigGovernance.Number(node["cycle_months"]!);
        }
        return 6;
    }
    public static bool IsDue(DateTime checkedAt, DateTime now, int months) => now >= checkedAt.AddMonths(months==3?3:6);
}
public sealed class FinancialRawStore
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Locks = new();
    private static readonly JsonSerializerOptions Jo=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
    private readonly string _root;
    public FinancialRawStore(string cacheDir) { _root=Path.Combine(cacheDir,"financial_shared_v1"); }
    public sealed class PeriodIndex
    {
        public string Security {get;set;}=""; public string ReportPeriod {get;set;}=""; public string PublishedAt {get;set;}="unknown";
        public string PeriodKind {get;set;}="unknown"; public string Basis {get;set;}="provider_reported/cumulative_unless_provider_labels_otherwise";
    }
    public sealed class Envelope
    {
        public string Provider {get;set;}=""; public string Resource {get;set;}=""; public string Security {get;set;}=""; public string Scope {get;set;}="provider_defined_not_reconciled";
        public string Schema {get;set;}="raw-json-v1"; public string Units {get;set;}="provider_native; per-field definitions retained, no unit conversion";
        public string Definition {get;set;}="provider-defined ratios are NOT recalculated strategy ratios"; public string Source {get;set;}="";
        public DateTime FetchedAt {get;set;} public DateTime ValidatedAt {get;set;}
        public string Hash {get;set;}="";
        public List<PeriodIndex> Periods {get;set;}=new();
        public JsonElement Payload {get;set;}
        public FinancialContract? Contract {get;set;} // additive metadata, legacy envelopes remain readable
    }
    public static bool IsFinancialUrl(string url) => new[]{"RPT_LICO_FN_CPD","RPT_F10_FINANCE_MAINFINADATA","RPT_F10_FINANCE_GCASHFLOW","RPT_F10_FINANCE_GBALANCE","RPT_SHAREBONUS_DET"}.Any(url.Contains);
    // Canonical full projection for single-security F10: both QV and other strategies consume the SAME native response.
    public static string CanonicalUrl(string url)
    {
        if(!url.Contains("RPT_F10_FINANCE_")) return url;
        var uri=new Uri(url);var q=uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x.Length>1?Uri.UnescapeDataString(x[1]):"");
        q["columns"]="ALL";q["pageSize"]="100";q["pageNumber"] = q.GetValueOrDefault("pageNumber","1");
        var filter=q.GetValueOrDefault("filter","");var match=Regex.Match(filter,@"\d{6}");
        if(match.Success) {var c=match.Value; var ex=c.StartsWith("920")||c[0] is '4' or '8'?"BJ":c[0]=='6'?"SH":"SZ";q["filter"]=filter.Replace($"SECURITY_CODE=\"{c}\"",$"SECUCODE=\"{c}.{ex}\"");}
        q["sortColumns"]="REPORT_DATE";q["sortTypes"]="-1";q["source"]="HSF10";q["client"]="PC";
        return "https://datacenter.eastmoney.com/securities/api/data/v1/get?"+string.Join("&",q.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>x.Key+"="+Uri.EscapeDataString(x.Value)));
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public async Task<JsonDocument> GetAsync(string provider,string resource,string security,string source,Func<Task<JsonDocument>> fetch,CancellationToken ct=default)
    {
        security=FinancialContract.CanonicalSecurity(security);
        var key=Hash(provider+"|"+resource+"|"+security+"|provider_defined|raw-json-v1|"+source);
        var dir=Path.Combine(_root,provider,resource);var file=Path.Combine(dir,Regex.Replace(security,@"[^a-zA-Z0-9_.-]","_")+"_"+key+".json");
        Envelope? Read(){try{return JsonSerializer.Deserialize<Envelope>(File.ReadAllText(file),Jo);}catch{return null;}}
        var gate=Locks.GetOrAdd(file,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var old=Read();var a=FinancialAccess.Current;var corporate=resource.Contains("BONUS")||resource.Contains("adjustment-factors")||resource=="dividend";
            var riskEvent=resource is "pledge_stat" or "pledge_detail" or "repurchase" or "anns_d";
            bool Due(DateTime validated,int months)=>riskEvent?DateTime.Now>=validated.AddHours(1):corporate?DateTime.Now>=validated.AddDays(1):FinancialAccess.IsDue(validated,DateTime.Now,months);
            var allow=a?.Offline!=true&&(a==null||a.AllowFinancialNetwork||(corporate&&a.AllowCorporateActionsNetwork))&&!(a?.MissingOnly==true&&old!=null);
            var cycle=a?.CycleMonths??FinancialAccess.ConfiguredCycle(Path.GetDirectoryName(_root)!);
            if(old!=null && (!allow || !Due(old.ValidatedAt,cycle))) {a?.Hit(file);return JsonDocument.Parse(old.Payload.GetRawText());}
            if(!allow) {a?.Missing(file);return JsonDocument.Parse("{}");}
            Directory.CreateDirectory(dir);
            // Cross-process lock; re-read after lock acquisition. Locks are files, not credentials.
            FileStream? processLock=null;
            while(processLock==null){ct.ThrowIfCancellationRequested();try{processLock=new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){await Task.Delay(100,ct).ConfigureAwait(false);}}
            using(processLock)
            {
                old=Read();
                if(old!=null && !Due(old.ValidatedAt,cycle)){a?.Hit(file);return JsonDocument.Parse(old.Payload.GetRawText());}
                try
                {
                    using var doc=await fetch().ConfigureAwait(false);var payload=doc.RootElement.Clone();
                    // An error/null/empty response must NEVER destroy previously verified periods.
                    JsonElement rows=default;bool hasRows=payload.TryGetProperty("result",out var result)&&result.ValueKind==JsonValueKind.Object&&result.TryGetProperty("data",out rows)&&rows.ValueKind==JsonValueKind.Array;
                    if(!hasRows && payload.TryGetProperty("item",out var thsRows) && thsRows.ValueKind==JsonValueKind.Array){rows=thsRows;hasRows=true;}
                    if(!hasRows || (rows.GetArrayLength()==0&&!corporate)){if(old!=null){a?.RefreshFailures.TryAdd(file,"empty/error response; source asof unchanged");return JsonDocument.Parse(old.Payload.GetRawText());}throw new InvalidDataException("empty/error financial response retained as missing, previous inputs preserved");}
                    var e=new Envelope{Provider=provider,Resource=resource,Security=security,Source=source,FetchedAt=DateTime.Now,ValidatedAt=DateTime.Now,Payload=payload,Hash=Hash(payload.GetRawText())};
                    foreach(var row in rows.EnumerateArray())
                    {
                        string S(string k)=>row.TryGetProperty(k,out var v)?v.ToString():"";
                        var date=S("REPORT_DATE");if(date.Length==0)date=S("REPORTDATE");if(date.Length==0)date=S("end_date");if(System.Text.RegularExpressions.Regex.IsMatch(date,@"^\d{8}$"))date=date[..4]+"-"+date.Substring(4,2)+"-"+date.Substring(6,2);if(date.Length>10)date=date[..10];if(date.Length==0)date=S("EX_DIVIDEND_DATE");
                        var c=S("SECUCODE");if(c.Length==0)c=S("ts_code");if(c.Length==0)c=S("SECURITY_CODE");if(c.Length==6)c=ThsClient.ToThsCode(c);
                        var published=S("NOTICE_DATE");if(published.Length==0)published=S("UPDATE_DATE"); // UPDATE_DATE is not claimed as publication.
                        e.Periods.Add(new(){Security=c.Length>0?c:security,ReportPeriod=date,PublishedAt=S("NOTICE_DATE").Length>0?S("NOTICE_DATE"):S("PLAN_NOTICE_DATE").Length>0?S("PLAN_NOTICE_DATE"):S("ann_date").Length>0?S("ann_date"):"unknown",PeriodKind=resource.Contains("BONUS")||resource.Contains("adjustment")?"corporate_action":date.Contains("12-31")?"annual":date.Length>0?"cumulative/interim":"unknown"});
                    }
                    e.Contract=FinancialContract.Descriptor(provider);
                    var bytes=JsonSerializer.Serialize(e,Jo);var archive=Path.Combine(dir,key+"_revisions");Directory.CreateDirectory(archive);
                    if(old!=null)Atomic(Path.Combine(archive,old.Hash+".json"),JsonSerializer.Serialize(old,Jo));
                    Atomic(Path.Combine(archive,e.Hash+".json"),bytes);Atomic(file,bytes);a?.Hit(file);
                    return JsonDocument.Parse(payload.GetRawText());
                }
                catch(Exception ex) when(old!=null){a?.RefreshFailures.TryAdd(file,ex.Message);a?.Hit(file);return JsonDocument.Parse(old.Payload.GetRawText());}
            }
        }
        finally {gate.Release();}
    }
    public static void Atomic(string path,string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(temp,value,Encoding.UTF8);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public static string LegacyResource(string file) => Path.GetFileName(file).Split('_')[0] switch
    {"mainfin"=>"RPT_F10_FINANCE_MAINFINADATA","cashflow" or "cf"=>"RPT_F10_FINANCE_GCASHFLOW","zcfz" or "bs"=>"RPT_F10_FINANCE_GBALANCE","div"=>"RPT_SHAREBONUS_DET","yjbb"=>"RPT_LICO_FN_CPD",_=>""};
    public static DateTime NativeAsOf(string cacheDir,string legacyFile)
    {
        var resource=LegacyResource(legacyFile);if(resource.Length==0)return DateTime.MinValue;
        var segment=Path.GetFileNameWithoutExtension(legacyFile).Split('_').Last();
        var sec=segment.Length==6?ThsClient.ToThsCode(segment):"market_period-batch";
        var dir=Path.Combine(cacheDir,"financial_shared_v1","eastmoney",resource);
        if(!Directory.Exists(dir))return DateTime.MinValue;
        var latest=DateTime.MinValue;
        foreach(var p in Directory.EnumerateFiles(dir,sec+"_*.json"))
        {
            // Market-period batch caches must match the requested period, not merely the provider/resource.
            if(segment.Length==8)
            {try{var e=JsonSerializer.Deserialize<Envelope>(File.ReadAllText(p),Jo);if(e==null || !e.Source.Contains(segment[..4]+"-"+segment.Substring(4,2)+"-"+segment.Substring(6,2)))continue;}catch{continue;}}
            if(File.GetLastWriteTime(p)>latest)latest=File.GetLastWriteTime(p);
        }
        return latest;
    }
    public static string Resource(string url) {var m=Regex.Match(url,@"reportName=([^&]+)");return m.Success?m.Groups[1].Value:"unknown";}
    public static string Security(string url){var m=Regex.Match(Uri.UnescapeDataString(url),@"\d{6}\.(SH|SZ|BJ)");return m.Success?m.Value:"market/period-batch";}
}
