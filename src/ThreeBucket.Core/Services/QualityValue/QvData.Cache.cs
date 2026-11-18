using System.Text.Json;
using ThreeBucket.Core.Services;
namespace ThreeBucket.Core.Services.QualityValue;
public sealed partial class QvData
{
    private FinancialAccess? Access => FinancialAccess.Current;
    private bool LocalOnly => Access?.Offline ?? true;
    private bool FinancialOnline => Access?.AllowFinancialNetwork ?? false;
    private sealed class Inputs
    {
        public string ExecutedAt {get;set;}="";
        public double? Rf {get;set;}
        public string RfSource {get;set;}="";
        public List<QvStock> Stocks {get;set;}=new();
        public Dictionary<string,string> Sources {get;set;}=new();
    }
    private Inputs? _inputs;
    private Inputs Retained => _inputs ??= ReadLocal<Inputs>(Path.Combine(_outDir,"inputs_latest.json")) ?? new();
    private T? ReadLocal<T>(string path)
    {
        try {if(File.Exists(path)){var value=JsonSerializer.Deserialize<T>(File.ReadAllText(path),Jo);if(value!=null){Access?.Hit(path);return value;}}}catch{}
        Access?.Missing(path);return default;
    }
    private void SaveLocal<T>(string path,T value) => FinancialRawStore.Atomic(path,JsonSerializer.Serialize(value,Jo));
    private QvOutput? _legacy;
    private QvOutput? Legacy => _legacy ??= QvScan.LoadLatest(_outDir);
    public void RetainInputs(IReadOnlyList<QvStock> stocks)
    {
        var path=Path.Combine(_outDir,"inputs_latest.json");
        var all=Retained.Stocks.ToDictionary(s=>s.Code,StringComparer.Ordinal);
        foreach(var stock in stocks)
        {
            if(all.TryGetValue(stock.Code,out var previous))
            {
                if(stock.Annuals.Count==0)stock.Annuals=previous.Annuals;
                if(stock.Periods.Count==0)stock.Periods=previous.Periods;
                if(stock.PbHistory.Count==0){stock.PbHistory=previous.PbHistory;stock.PbMedian3y=previous.PbMedian3y;stock.PbMedian5y=previous.PbMedian5y;stock.PbHistorySource=previous.PbHistorySource;stock.PbHistoryAsOf=previous.PbHistoryAsOf;}
            }
            all[stock.Code]=stock;
        }
        var input=new Inputs{ExecutedAt=TradingCalendar.NowCn().ToString("O"),Rf=RfPct,RfSource=RfSource,Stocks=all.Values.ToList(),Sources=Access?.Sources.ToDictionary(x=>x.Key,x=>x.Value)??new()};
        SaveLocal(path,input);
        SaveLocal(Path.Combine(_outDir,"input_manifests","inputs_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".json"),input);
    }
    private List<QvStock> LocalUniverse()
    {
        var map=(ReadLocal<List<QvStock>>(Path.Combine(_cacheDir,"universe.json"))??new()).GroupBy(s=>s.Code).ToDictionary(g=>g.Key,g=>g.Last());
        foreach(var stock in Retained.Stocks)map[stock.Code]=stock;
        foreach(var row in Legacy?.Rows??new())
        {
            if(!map.TryGetValue(row.Code,out var stock))map[row.Code]=stock=new(){Code=row.Code,Name=row.Name,IndustryRaw=row.Industry};
            if(string.IsNullOrEmpty(stock.IndustryRaw))stock.IndustryRaw=row.Industry;
            if(string.IsNullOrEmpty(stock.Name)||stock.Name==stock.Code)stock.Name=row.Name;
            if(stock.Quote.Price is null || string.CompareOrdinal(row.AsOf,stock.Quote.AsOf)>=0){stock.Quote.Price=row.Price;stock.Quote.Source="retained state/"+(Legacy?.AsOf??"unknown");stock.Quote.AsOf=row.AsOf;}
            stock.Quote.Turnover60 ??= row.MonitoringTurnover60;
        }
        foreach(var path in Directory.EnumerateFiles(_cacheDir,"mainfin_*.json"))
        {
            var code=Path.GetFileNameWithoutExtension(path)[8..];
            if(code.Length==6&&!map.ContainsKey(code))map[code]=new(){Code=code,Name=code};
        }
        return map.Values.ToList();
    }
    private async Task<T?> CachedAsync<T>(string name,Func<Task<T>> fetch)
    {
        var path=Path.Combine(_cacheDir,name);var old=ReadLocal<T>(path);
        var stamp=path+".checked.json";var checkedAt=ReadLocal<DateTime?>(stamp)??(File.Exists(path)?File.GetLastWriteTime(path):DateTime.MinValue);
        var corporate=name.StartsWith("div_");
        var canUpdate=FinancialOnline||(corporate&&Access?.AllowCorporateActionsNetwork==true);
        var due=corporate?DateTime.Now>=checkedAt.AddDays(1):FinancialAccess.IsDue(checkedAt,DateTime.Now,Access?.CycleMonths??6);
        var native=FinancialRawStore.NativeAsOf(Path.Combine(_dataDir,"cache"),name);
        var reparseNative=native>DateTime.MinValue && (!File.Exists(path)||native>File.GetLastWriteTime(path));
        if(old!=null && !IsHollow(old) && !reparseNative && (!canUpdate || !due))return old;
        if(!canUpdate&&!reparseNative){Access?.Missing(path);return old!=null&&!IsHollow(old)?old:default;}
        try
        {
            var failuresBefore=Access?.RefreshFailures.Count??0;
            var value=await fetch().ConfigureAwait(false);
            if((Access?.RefreshFailures.Count??0)>failuresBefore)return old??value;
            if(value==null||IsHollow(value)||value is System.Collections.ICollection {Count:0})return old;
            if(!canUpdate)return value; // local raw-to-DTO adapter: never changes retained raw inputs
            // Preserve prior endpoint revisions; never delete old data or overwrite it with empty/error.
            if(File.Exists(path)) {var bytes=File.ReadAllText(path);var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bytes)));var archive=Path.Combine(_cacheDir,"retained",name,hash+".json");if(!File.Exists(archive))FinancialRawStore.Atomic(archive,bytes);}
            SaveLocal(path,value);SaveLocal(stamp,DateTime.Now);Access?.Hit(path);return value;
        }
        catch(Exception ex){L("[S29] 财报更新失败，保留旧输入："+name+" "+ex.Message);return old;}
    }
    private async Task<List<T>> EventCached<T>(string name,Func<Task<List<T>>> fetch)
    {
        var path=Path.Combine(_cacheDir,name);var old=ReadLocal<List<T>>(path);
        if(LocalOnly || FinancialOnline || (old!=null&&DateTime.Now-File.GetLastWriteTime(path)<TimeSpan.FromHours(1)))return old??new();
        try{var value=await fetch().ConfigureAwait(false);if(value.Count>0)SaveLocal(path,value);return value;}catch{return old??new();}
    }
    public string ValuationAnchorFor(QvStock stock)
    {
        var old=Legacy?.Rows.FirstOrDefault(r=>r.Code==stock.Code);
        return !string.IsNullOrEmpty(old?.ValuationAdjustmentAsOf)?old.ValuationAdjustmentAsOf:
            DateTime.TryParse(stock.Quote.AsOf,out var day)?day.ToString("yyyy-MM-dd"):Legacy?.AsOf??"";
    }
    public string EventInputAsOf => (LocalOnly||FinancialOnline)?"离线：仅既有事件输入，不代表当前安全；事件文件取数时间见InputSources":"本轮独立行情/事件更新；财报不联网";
    public void AttachInputAudit(QvOutput output)
    {
        output.RuleExecutedAt=TradingCalendar.NowCn().ToString("O");output.InputSources=Access?.Sources.ToDictionary(x=>x.Key,x=>x.Value)??new();
        output.InputWarnings=Access?.RefreshFailures.ToDictionary(x=>x.Key,x=>x.Value)??new();
        foreach(var warning in output.InputWarnings)L("[S29] 更新未核实，保留旧来源时间："+warning.Key+" "+warning.Value);
        output.EventInputAsOf=EventInputAsOf;output.NetworkCalls=Access?.OutboundCalls??0;output.CacheHits=Access?.CacheHits??0;output.CacheMisses=Access?.CacheMisses??0;
        foreach(var row in output.Rows)
        {
            row.Chain+="\n输入模式="+output.Mode+"；规则运行="+output.RuleExecutedAt+"；行情源日期="+row.MonitoringAsOf+"；"+EventInputAsOf;
            if(LocalOnly||FinancialOnline){row.StrongAlertBlocked=true;row.StrongAlertBlockReason+=";offline_events_not_current";row.ReviewCard+="\n离线事件状态："+EventInputAsOf;}
        }
        L($"[S29] 模式={output.Mode}；cache hit={output.CacheHits} miss={output.CacheMisses} outbound operations={output.NetworkCalls}；财报联网={FinancialOnline}；事件={EventInputAsOf}");
    }
}
