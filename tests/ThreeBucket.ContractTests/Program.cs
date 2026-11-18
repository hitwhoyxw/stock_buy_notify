using System.Reflection;
using System.Runtime.Loader;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThreeBucket.Core.Services;
using ThreeBucket.Core.Services.QualityValue;
using ThreeBucket.Core.Services.BottomReversal;
using ThreeBucket.Core.Services.SlowRise;

static class Program
{
    static int passed,failed;static readonly List<object> Results=new();
    static readonly JsonSerializerOptions Jo=new(){IncludeFields=true,PropertyNameCaseInsensitive=true,WriteIndented=true};
    static string root="",work="";
    static void Check(bool ok,string name){Results.Add(new{Name=name,Passed=ok});if(ok)passed++;else{failed++;Console.WriteLine("FAIL "+name);}}
    static void Reject(Action action,string name){try{action();Check(false,name);}catch(Exception ex){Check(ex is ConfigValidationException or InvalidDataException or ArgumentException,name);}}
    static string Hash(string f)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
    static string TextHash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static void CopyConfigs(string dir){Directory.CreateDirectory(Path.Combine(dir,"data"));foreach(var p in Directory.GetFiles(Path.Combine(root,"config","quality_value"),"*.yaml")){var d=Path.Combine(dir,"config","quality_value",Path.GetFileName(p));Directory.CreateDirectory(Path.GetDirectoryName(d)!);File.Copy(p,d,true);}}
    static JsonDocument Payload(double n=1)=>JsonDocument.Parse("{\"result\":{\"data\":[{\"SECUCODE\":\"001872.SZ\",\"REPORT_DATE\":\"2025-12-31\",\"NETCASH_OPERATE\":"+n.ToString(System.Globalization.CultureInfo.InvariantCulture)+"}]}}");
    static async Task<JsonDocument> FakeFetch(string cache)
    {
        File.AppendAllText(Path.Combine(cache,"fake_fetches.txt"),"fake-provider-only\n");await Task.Delay(150);return Payload();
    }
    static async Task<int> Worker(string cache)
    {
        using var access=new FinancialAccess{AllowFinancialNetwork=true};using var d=await new FinancialRawStore(cache).GetAsync("fake-provider","statement","001872.SZ","fake://same-resource",()=>FakeFetch(cache));return 0;
    }
    public static async Task<int> Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--store-worker")return await Worker(args[1]);
        if(args.Length<3){Console.Error.WriteLine("Usage: ContractTests ROOT WORK BASELINE155_DLL");return 2;}
        root=Path.GetFullPath(args[0]);work=Path.GetFullPath(args[1]);Directory.CreateDirectory(work);
        await ConfigTests();await ContractTests();await StoreTests();await CachedAndParityTests(args[2]);
        File.WriteAllText(Path.Combine(work,"results.json"),JsonSerializer.Serialize(new{Passed=passed,Failed=failed,Tests=Results},Jo));
        Console.WriteLine($"TOTAL passed={passed} failed={failed}; report={Path.Combine(work,"results.json")}");return failed==0?0:1;
    }
    static async Task ConfigTests()
    {
        var dir=Path.Combine(work,"config-tests");CopyConfigs(dir);var data=Path.Combine(dir,"data");var cfgDir=Path.Combine(dir,"config","quality_value");
        var files=Directory.GetFiles(cfgDir);var before=files.ToDictionary(x=>x,Hash);var cfg=QvConfig.Load(data);
        Check(files.All(x=>before[x]==Hash(x)),"Load preserves all config bytes");Check(!File.Exists(Path.Combine(data,QvConfig.FileNameV11)),"Load has no snapshot write");
        Check(cfg.Hash==QvConfig.Load(data).Hash,"config hash deterministic");
        var preview=ConfigGovernance.Preview(cfg,null);Check(preview.Contains("不联网"),"preview declares no network");Check(!Directory.GetFiles(data,"*",SearchOption.AllDirectories).Any(),"preview creates no data files");
        var current=JsonNode.Parse(cfg.Root.ToJsonString())!;var reverse=new JsonObject();foreach(var kv in ((JsonObject)current).Reverse())reverse[kv.Key]=kv.Value?.DeepClone();Check(ConfigGovernance.Hash(current)==ConfigGovernance.Hash(reverse),"hash independent of mapping order");
        var global=Path.Combine(cfgDir,"global.yaml");var original=File.ReadAllText(global);
        foreach(var pair in new[]{("qualified_min: 70","qualified_mim: 70"),("qualified_min: 70","qualified_min: not_a_number"),("qualified_min: 70","qualified_min: \"70\""),("concurrency: 8","concurrency: 0"),("concurrency: 8","concurrency: 1.5"),("watch_min: 65","watch_min: 90"),("qualified_min: 70\n",""),("min_re: 0.08","min_re: NaN"),("min_re: 0.08","min_re: Infinity"),("full_rescan_months: [5, 9]","full_rescan_months: [0, 13]"),("qualified_min: 70","qualified_min: true")})
        {File.WriteAllText(global,original.Replace(pair.Item1,pair.Item2));Reject(()=>QvConfig.Load(data),"reject global "+pair.Item2);}
        File.WriteAllText(global,original+"\nqualified_min: 60\n");try{QvConfig.Load(data);Check(false,"duplicate key line");}catch(ConfigValidationException ex){Check(ex.Message.Contains("duplicate")&&ex.Message.Contains("global.yaml")&&ex.Message.Contains("line "),"duplicate key file and line");}
        File.WriteAllText(global,original);File.WriteAllText(Path.Combine(cfgDir,"typo.yaml"),"x: 1");Reject(()=>QvConfig.Load(data),"unknown config filename");File.Delete(Path.Combine(cfgDir,"typo.yaml"));
        var t1=Path.Combine(cfgDir,"T1.yaml");var origT1=File.ReadAllText(t1);
        foreach(var tuple in new[]{("profit: 25","profit: 26"),("profit: 25","profit: -1"),("maint_capex_ratio: 0.60","maint_capex_ratio: 1.1"),("fcf_g: 0.03","fcf_g: 0.20")}){File.WriteAllText(t1,origT1.Replace(tuple.Item1,tuple.Item2));Reject(()=>QvConfig.Load(data),"reject T1 "+tuple.Item2);}
        File.WriteAllText(t1,origT1);var rv=Path.Combine(cfgDir,"review.yaml");var origRv=File.ReadAllText(rv);
        foreach(var tuple in new[]{("P1","P9"),("earnings_max: 30","earnings_max: 31"),("p1_min: 60","p1_min: 90")}){File.WriteAllText(rv,origRv.Replace(tuple.Item1,tuple.Item2));Reject(()=>QvConfig.Load(data),"reject review "+tuple.Item2);}
        File.WriteAllText(rv,origRv);
        var ir=Path.Combine(cfgDir,"industry_relative.yaml");var origIr=File.ReadAllText(ir);File.WriteAllText(ir,origIr.Replace("min_peers: 10","min_peers: 2"));Reject(()=>QvConfig.Load(data),"tiny peer sample rejected");File.WriteAllText(ir,origIr);
        File.Delete(rv);File.Delete(ir);var legacy=QvConfig.Load(data);Check(legacy.D("global.qualified_min")==70,"older YAML without optional review/relative remains compatible");File.WriteAllText(rv,origRv);File.WriteAllText(ir,origIr);
        foreach(var name in new[]{QvConfig.FileName,QvConfig.FileNameV11}){var f=Path.Combine(root,"data",name);if(File.Exists(f)){var loaded=QvConfig.Load(data,f);Check(loaded.Root!=null,"legacy JSON "+name);}}
        var snapshot=Path.Combine(dir,"before.json");File.WriteAllText(snapshot,JsonSerializer.Serialize(new{Config=cfg.Root}));var sh=Hash(snapshot);File.WriteAllText(global,original.Replace("qualified_min: 70","qualified_min: 71"));var changed=QvConfig.Load(data);var diff=ConfigGovernance.Preview(changed,snapshot);Check(diff.Contains("qualified_min")&&diff.Contains("70")&&diff.Contains("71"),"diff actual old/new values");Check(sh==Hash(snapshot),"diff previous snapshot read only");File.WriteAllText(global,original);
        var json=Path.Combine(dir,"duplicate.json");File.WriteAllText(json,"{\"a\":1,\"a\":2}");Reject(()=>ConfigGovernance.ReadJson(json),"JSON duplicate rejected");
        foreach(var text in new[]{"x: [1,2","x 3","x: true\nx: false","x: {a: 1, a: 2}"})Reject(()=>QvYaml.Parse(text),"YAML syntax "+text.Replace("\n","/"));
        var cycle=Path.Combine(dir,"cycle.yaml");foreach(var n in new[]{1,4,12}){File.WriteAllText(cycle,"cycle_months: "+n);Reject(()=>ConfigGovernance.Validate(QvYaml.ParseFile(cycle),"financial_cache.yaml",cycle),"cycle rejects "+n);}
        foreach(var name in new[]{"bottom_reversal_v1.json","bottom_reversal_v1_1.json","bottom_reversal_v1_2.json","bottom_slowrise_v1.json","bottom_slowrise_v2.json"}){var f=Path.Combine(root,"data",name);ConfigGovernance.ReadJson(f,name);Check(true,"existing valid strategy "+name);}
        var csvData=Path.Combine(work,"csv-config-tests");Directory.CreateDirectory(csvData);var store=new ThreeBucket.Core.Data.DataStore(csvData);var defaults=store.ListStrategies();Check(defaults.Count>=16&&!File.Exists(Path.Combine(csvData,"strategies.csv")),"strategy CSV missing uses pure in-memory defaults");
        store.AddStrategy(new ThreeBucket.Core.Models.Strategy{Id="S99",Name="TEST",Type="buy",Indicator="volume_ratio_20d",Operator=">",Threshold="1",Enabled=true});Check(store.ListStrategies().Count==defaults.Count+1,"first explicit strategy edit preserves defaults");
        var validCsv=File.ReadAllText(Path.Combine(csvData,"strategies.csv"));File.WriteAllText(Path.Combine(csvData,"strategies.csv"),validCsv.Replace("enabled","enabeld"));Reject(()=>store.ListStrategies(),"CSV typo rejects rather than seeding defaults");File.WriteAllText(Path.Combine(csvData,"strategies.csv"),validCsv);
        File.WriteAllText(json,"{\n\"a\":1,\n\"a\":2}");try{ConfigGovernance.ReadJson(json);Check(false,"exact JSON duplicate line");}catch(ConfigValidationException ex){Check(ex.Message.Contains(":3:"),"exact JSON duplicate line");}
        var path=Path.Combine(root,"trading-system","02_strategy_config.yaml");var th=Hash(path);var thresholds=PoolThresholds.Load(path);Check(th==Hash(path),"candidate YAML load pure and current syntax supported");
        using(var access=new FinancialAccess{Offline=true,ThrowOnNetwork=true}){File.WriteAllText(global,original.Replace("qualified_min: 70","qualified_mim: 70"));var beforeFiles=Directory.GetFiles(data,"*",SearchOption.AllDirectories);try{await QvScan.RunAsync(data,"recalc",_=>{},new EastMoneyClient(Path.Combine(dir,"constructor-only-cache")),null,notify:false,throwOnNetwork:true);Check(false,"invalid config no partial scan");}catch(ConfigValidationException){Check(beforeFiles.SequenceEqual(Directory.GetFiles(data,"*",SearchOption.AllDirectories)),"invalid config no partial scan writes");Check(access.OutboundCalls==0,"invalid config no network calls");}File.WriteAllText(global,original);}
    }
    static Task ContractTests()
    {
        using var payload=JsonDocument.Parse("{\"result\":{\"data\":[{\"SECUCODE\":\"001872.SZ\",\"REPORT_DATE\":\"2025-12-31\",\"ROEJQ\":6.36,\"NCO_NETPROFIT\":0.91785760196,\"NETCASH_OPERATE\":0,\"PARENTNETPROFIT\":null,\"UNKNOWN_FIELD\":5,\"UPDATE_DATE\":\"2026-05-01\"}]}}");
        var c=FinancialContract.Describe(payload.RootElement,"eastmoney","RPT_F10_FINANCE_MAINFINADATA","001872","fixture://synthetic-contract-only",DateTime.Now,"same-revision");
        using var ts=JsonDocument.Parse("[{\"ts_code\":\"001872.SZ\",\"end_date\":\"20251231\",\"ann_date\":\"20260331\",\"roe\":6.36}]");var tsContract=FinancialContract.Describe(ts.RootElement,"tushare","fina_indicator","001872","fixture://synthetic-tushare-contract",DateTime.Now,"fixture-revision");var tsRoe=tsContract.Values.Single(x=>x.RawField=="roe");
        Check(tsRoe.ReportPeriod=="2025-12-31"&&tsRoe.PublishedAt=="2026-03-31"&&tsRoe.Security=="001872.SZ","Tushare original dates/security canonicalized without API");Check(tsRoe.Status=="unverified"&&tsRoe.Basis=="provider_defined/unverified"&&tsRoe.Value==6.36,"unverified provider units/basis never silently normalized");Check(FinancialContract.Descriptor("tushare").Fields.Count==0,"EastMoney field definitions not assigned to Tushare");
        using var missingDate=JsonDocument.Parse("[{\"NETCASH_OPERATE\":1}]");Check(FinancialContract.Describe(missingDate.RootElement,"eastmoney","cashflow","001872","fixture://unknown-period",DateTime.Now,"v1").Values.Single().ReportType=="unknown","unknown report period not called interim");
        var roe=c.Values.Single(x=>x.RawField=="ROEJQ");Check(Math.Abs(roe.CanonicalValue!.Value-0.0636)<1e-12,"explicit percent scale, no magnitude heuristic");
        var nco=c.Values.Single(x=>x.RawField=="NCO_NETPROFIT");Check(nco.Scale==1&&nco.Denominator=="consolidated_total_net_profit","NCO ratio scale1 and consolidated denominator");Check(nco.PublishedAt=="unknown","revision date not publication date");
        Check(c.Values.Single(x=>x.RawField=="NETCASH_OPERATE").Value==0,"zero preserved");Check(c.Values.Single(x=>x.RawField=="PARENTNETPROFIT").Value==null,"missing never zero");Check(c.Values.Single(x=>x.RawField=="UNKNOWN_FIELD").Status=="unverified","unknown field not coerced");
        var q1=new FinancialValue{Provider="fake",Resource="income",Security="001872.SZ",ReportPeriod="2025-03-31",Scope="consolidated",Basis="cumulative",Currency="CNY",Unit="CNY",Scale=1,RawField="profit",RevisionSet="v1",Status="mapped_provider_definition",Value=10};
        var q2=q1 with{ReportPeriod="2025-06-30",Value=25};Check(FinancialContract.SingleQuarter(q1,null).Value==10,"Q1 equals cumulative");Check(FinancialContract.SingleQuarter(q2,q1).Value==15,"singleQ cumulative difference");
        foreach(var bad in new[]{q1 with{RevisionSet="v2"},q1 with{Scope="parent"},q1 with{Unit="CNY_thousand"},q1 with{Currency="USD"},q1 with{Provider="other"},q1 with{Resource="other"},q1 with{Value=null},q1 with{ReportPeriod="2024-03-31"}})Check(FinancialContract.SingleQuarter(q2,bad).Value==null,"reject incompatible singleQ "+JsonSerializer.Serialize(bad));
        var quarters=new[]{q1 with{Basis="single_quarter"},q1 with{Basis="single_quarter",ReportPeriod="2025-06-30"},q1 with{Basis="single_quarter",ReportPeriod="2025-09-30"},q1 with{Basis="single_quarter",ReportPeriod="2025-12-31"}};
        Check(FinancialContract.Ttm(quarters).Value==40,"TTM complete four quarters");Check(FinancialContract.Ttm(quarters.Take(3).ToArray()).Value==null,"TTM missing quarter rejected");Check(FinancialContract.Ttm(quarters.Select((x,i)=>i==3?x with{ReportPeriod="2026-03-31"}:x).ToArray()).Value==null,"TTM gap rejected");Check(FinancialContract.Ttm(quarters.Select(x=>x with{Basis="annual_cumulative"}).ToArray()).Value==null,"TTM no double annualization");
        foreach(var code in new[]{"001872","600018","600036","000915","603886"})
        {
            var f=Path.Combine(root,"data","cache","quality_value","mainfin_"+code+".json");var bytes=Hash(f);var contract=FinancialContract.ReadProjection(f,"qv-mainfin",code);
            using var d=JsonDocument.Parse(File.ReadAllText(f));var first=d.RootElement[0];
            foreach(var k in new[]{"Roe","OcfToNp","Np","Revenue"}){var v=contract.Values.First(x=>x.RawField==k);Check(v.RawJson==first.GetProperty(k).GetRawText(),"real retained original value "+code+" "+k);}
            Check(contract.Values.First(x=>x.RawField=="OcfToNp").Scale==1,"real NCO ratio "+code);Check(contract.Values.All(x=>x.FetchedAt=="unknown"&&x.PublishedAt=="unknown"),"legacy times unknown not fabricated "+code);Check(bytes==Hash(f),"contract projection read-only "+code);
        }
        return Task.CompletedTask;
    }
    static async Task StoreTests()
    {
        var cache=Path.Combine(work,"concurrent-store");Directory.CreateDirectory(cache);var dll=Assembly.GetExecutingAssembly().Location;
        var children=new List<Process>();for(var i=0;i<5;i++){var psi=new ProcessStartInfo("dotnet"){UseShellExecute=false};psi.ArgumentList.Add(dll);psi.ArgumentList.Add("--store-worker");psi.ArgumentList.Add(cache);children.Add(Process.Start(psi)!);}
        using(var access=new FinancialAccess{AllowFinancialNetwork=true}){var tasks=Enumerable.Range(0,5).Select(async _=>{using var d=await new FinancialRawStore(cache).GetAsync("fake-provider","statement","001872.SZ","fake://same-resource",()=>FakeFetch(cache));});await Task.WhenAll(tasks);}
        foreach(var p in children){await p.WaitForExitAsync();Check(p.ExitCode==0,"concurrent C# child succeeded");p.Dispose();}
        Check(File.ReadAllLines(Path.Combine(cache,"fake_fetches.txt")).Length==1,"cross-process and in-process same-resource one fake fetch");
        var envelope=Directory.GetFiles(Path.Combine(cache,"financial_shared_v1"),"001872.SZ_*.json",SearchOption.AllDirectories).Single();var node=JsonNode.Parse(File.ReadAllText(envelope))!;node["ValidatedAt"]=DateTime.Now.AddMonths(-7);File.WriteAllText(envelope,node.ToJsonString());var hash=Hash(envelope);
        using(var a=new FinancialAccess{Offline=true,ThrowOnNetwork=true}){using var d=await new FinancialRawStore(cache).GetAsync("fake-provider","statement","001872.SZ","fake://same-resource",()=>throw new Exception("must never fetch"));Check(a.OutboundCalls==0&&d.RootElement.TryGetProperty("result",out _),"expired offline reuses source zero requests");using var m=await new FinancialRawStore(cache).GetAsync("fake-provider","statement","600018.SH","fake://missing",()=>throw new Exception("must never fetch"));Check(m.RootElement.GetRawText()=="{}"&&a.OutboundCalls==0,"missing offline explicit pending zero requests");}
        Check(hash==Hash(envelope),"offline does not rewrite expired raw envelope");
        using(var a=new FinancialAccess{Offline=true,AllowFinancialNetwork=true,AllowCorporateActionsNetwork=true}){using var d=await new FinancialRawStore(cache).GetAsync("fake-provider","statement","001872.SZ","fake://same-resource",()=>throw new Exception("offline must dominate contradictory allow flags"));Check(a.OutboundCalls==0,"offline dominates contradictory refresh flags");}

        using(var a=new FinancialAccess{AllowFinancialNetwork=true,MissingOnly=true}){using var d=await new FinancialRawStore(cache).GetAsync("fake-provider","statement","001872.SZ","fake://same-resource",()=>throw new Exception("supplement cannot refresh existing"));Check(File.ReadAllLines(Path.Combine(cache,"fake_fetches.txt")).Length==1,"missing-only preserves existing expired resource");}
        using(var a=new FinancialAccess{AllowFinancialNetwork=true}){using var d=await new FinancialRawStore(cache).GetAsync("fake-provider","statement","001872.SZ","fake://same-resource",()=>Task.FromResult(JsonDocument.Parse("{}")));Check(a.RefreshFailures.Count==1&&hash==Hash(envelope),"failed refresh preserves exact old source atomically");}
        using(var a=new FinancialAccess{AllowFinancialNetwork=true}){using var d=await new FinancialRawStore(cache).GetAsync("fake-provider","statement","001872.SZ","fake://same-resource",()=>Task.FromResult(Payload(2)));}
        Check(FinancialContract.ReadEnvelope(envelope).Schema==FinancialContract.Version,"new compact envelope contract backward-compatible read");
        Check(Directory.GetFiles(Path.GetDirectoryName(envelope)!,"*.tmp").Length==0,"no partially written raw temp files");
        Check(!FinancialAccess.IsDue(new DateTime(2026,1,1),new DateTime(2026,6,30),6),"halfyear before due skipped");Check(FinancialAccess.IsDue(new DateTime(2026,1,1),new DateTime(2026,7,1),6),"halfyear due refresh eligible");Check(FinancialAccess.IsDue(new DateTime(2026,1,1),new DateTime(2026,4,1),3),"quarterly due refresh eligible");
    }
    static async Task CachedAndParityTests(string baselineDll)
    {
        var fixture=Path.Combine(work,"fixtures");CopyConfigs(fixture);var data=Path.Combine(fixture,"data");var cache=Path.Combine(data,"cache","quality_value");Directory.CreateDirectory(cache);
        var codes=new[]{"000915","600273","600507","600585","002027","002807","600971","600398","000932","000651","603886","605377","002043","001872","600018","600900","600036"};
        using var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"data","quality_value","state.json")));var rows=state.RootElement.GetProperty("Rows").EnumerateArray().Where(x=>codes.Contains(x.GetProperty("Code").GetString())).ToDictionary(x=>x.GetProperty("Code").GetString()!,x=>x.Clone());
        foreach(var code in codes)foreach(var prefix in new[]{"mainfin_","cf_","bs_","div_"}){var f=Path.Combine(root,"data","cache","quality_value",prefix+code+".json");if(File.Exists(f))File.Copy(f,Path.Combine(cache,Path.GetFileName(f)),true);}
        var cfg=QvConfig.Load(data);var log=new Action<string>(_=>{});var em=new EastMoneyClient(Path.Combine(data,"cache"));var qv=new QvData(data,em,log);qv.LoadSidecars();
        var stocks=codes.Where(rows.ContainsKey).Select(code=>new QvStock{Code=code,Name=rows[code].GetProperty("Name").GetString()??code,Type=rows[code].GetProperty("Type").GetString()??"",IndustryRaw=rows[code].GetProperty("Industry").GetString()??"",Quote=new QvQuote{Price=rows[code].TryGetProperty("Price",out var p)&&p.ValueKind==JsonValueKind.Number?p.GetDouble():null}}).ToList();
        using var access=new FinancialAccess{Offline=true,ThrowOnNetwork=true};await qv.FillFundamentalsAsync(stocks,10,true,CancellationToken.None);foreach(var s in stocks)await qv.EnsurePeriodsAsync(s,CancellationToken.None);Check(access.OutboundCalls==0,"all representative annual/quarter/peer inputs expired or missing offline zero requests");
        var peer=QvIndustryRelative.Build(stocks,cfg,new DateTime(2026,11,18),false);Check(access.OutboundCalls==0,"industry peers derive local inputs without separate fetch");
        var alc=new AssemblyLoadContext("baseline155",true);var asm=alc.LoadFromAssemblyPath(Path.GetFullPath(baselineDll));Type T(string n)=>asm.GetType("ThreeBucket.Core.Services.QualityValue."+n)!;
        var oldCfg=T("QvConfig").GetMethod("Load")!.Invoke(null,new object?[]{data,null})!;
        foreach(var name in new[]{"bottom_reversal_v1.json","bottom_reversal_v1_1.json","bottom_reversal_v1_2.json","bottom_slowrise_v1.json","bottom_slowrise_v2.json"})
        {
            var file=Path.Combine(root,"data",name);var currentBr=BrConfig.Load(data,file);var oldBrType=asm.GetType("ThreeBucket.Core.Services.BottomReversal.BrConfig")!;var oldBr=oldBrType.GetMethod("Load")!.Invoke(null,new object?[]{data,file})!;var oldRoot=(JsonNode)oldBrType.GetProperty("Root")!.GetValue(oldBr)!;
            Check(ConfigGovernance.Hash(currentBr.Root)==ConfigGovernance.Hash(oldRoot),"v155 S27/S28 exact config inputs "+name);
        }
        var candidatePath=Path.Combine(root,"trading-system","02_strategy_config.yaml");var currentTh=PoolThresholds.Load(candidatePath);var oldThType=asm.GetType("ThreeBucket.Core.Services.PoolThresholds")!;var oldTh=oldThType.GetMethod("Load")!.Invoke(null,new object?[]{candidatePath})!;
        foreach(var field in oldThType.GetFields(BindingFlags.Public|BindingFlags.Instance))
        {
            var newField=typeof(PoolThresholds).GetField(field.Name);if(newField==null)continue;var a=JsonSerializer.Serialize(field.GetValue(oldTh),field.FieldType,Jo);var b=JsonSerializer.Serialize(newField.GetValue(currentTh),newField.FieldType,Jo);Check(a==b,"v155 T6 same config field "+field.Name);
        }

        var oldEm=Activator.CreateInstance(asm.GetType("ThreeBucket.Core.Services.EastMoneyClient")!,new object?[]{Path.Combine(data,"cache"),null})!;
        var oldData=Activator.CreateInstance(T("QvData"),new object?[]{data,oldEm,log})!;T("QvData").GetMethod("LoadSidecars")!.Invoke(oldData,null);
        var diffs=new List<object>();
        foreach(var stock in stocks)
        {
            var newStock=JsonSerializer.Deserialize<QvStock>(JsonSerializer.Serialize(stock,Jo),Jo)!;
            var oldStock=JsonSerializer.Deserialize(JsonSerializer.Serialize(stock,Jo),T("QvStock"),Jo)!;
            var row=new QvRow{Code=stock.Code,Name=stock.Name,Type=stock.Type,Industry=stock.IndustryRaw,Price=stock.Quote.Price};
            var oldRow=JsonSerializer.Deserialize(JsonSerializer.Serialize(row,Jo),T("QvRow"),Jo)!;
            QvScreen.Run(newStock,row,qv,cfg,new DateTime(2026,11,18));T("QvScreen").GetMethod("Run")!.Invoke(null,new object?[]{oldStock,oldRow,oldData,oldCfg,new DateTime(2026,11,18)});
            QvScore.Run(newStock,row,qv,cfg,stocks);T("QvScore").GetMethod("Run")!.Invoke(null,new object?[]{oldStock,oldRow,oldData,oldCfg,null});
            QvValuation.Run(newStock,row,qv,cfg,0.02);T("QvValuation").GetMethod("Run")!.Invoke(null,new object?[]{oldStock,oldRow,oldData,oldCfg,0.02,null,null});
            var oldJson=JsonSerializer.SerializeToElement(oldRow,T("QvRow"),Jo);var newJson=JsonSerializer.SerializeToElement(row,Jo);
            foreach(var key in new[]{"Pool","QualityScore","PSafe","PFair","POpt","Zone"}){var a=oldJson.GetProperty(key).GetRawText();var b=newJson.GetProperty(key).GetRawText();Check(a==b,"v155 same-input "+stock.Code+" "+key);if(a!=b)diffs.Add(new{stock.Code,Field=key,Old=a,New=b});}
            var newCard=new QvReviewCard();var newDetection=QvReview.DetectEarnings(newStock,cfg,newCard);
            var oldCard=Activator.CreateInstance(T("QvReviewCard"))!;var oldDetection=T("QvReview").GetMethod("DetectEarnings")!.Invoke(null,new object?[]{oldStock,oldCfg,oldCard})!;
            Check(JsonSerializer.Serialize(newDetection,Jo)==JsonSerializer.Serialize(oldDetection,oldDetection.GetType(),Jo),"v155 same-input earnings review "+stock.Code);
            foreach(var period in newStock.Periods.Where(x=>x.OcfToNp.HasValue))
            {var rawFile=Path.Combine(cache,"mainfin_"+stock.Code+".json");using var raw=JsonDocument.Parse(File.ReadAllText(rawFile));var match=raw.RootElement.EnumerateArray().FirstOrDefault(x=>x.GetProperty("ReportDate").ToString()==period.ReportDate);if(match.ValueKind!=JsonValueKind.Undefined&&match.TryGetProperty("OcfToNp",out var n)&&n.ValueKind==JsonValueKind.Number)Check(period.OcfToNp==n.GetDouble(),"known NCO mapping exact, not divided by100 "+stock.Code+" "+period.ReportDate);}
        }
        File.WriteAllText(Path.Combine(work,"same_input_parity.json"),JsonSerializer.Serialize(new{SampleSize=stocks.Count,Differences=diffs,Note="Actual retained inputs; pure quality/valuation comparison, not an official scan. Missing bank peer data same on both sides. NCO quarter mapping tested separately."},Jo));
        Check(access.OutboundCalls==0,"parity and peer tests zero network");alc.Unload();
        var historyDir=Path.Combine(work,"manual-review-tests");var verdict=QvReviewHistory.Add(historyDir,new QvReviewRecord{Code="000915",Name="TEST_ONLY",TriggerReason="TEST",Verdict="temporary_issue",Note="TEST entry isolated",ValidUntil="2027-01-01",Fingerprint="TEST"});
        Check(QvReviewHistory.Load(historyDir).Count==1,"isolated verdict saved");Check(QvReviewHistory.Update(historyDir,verdict.Id,r=>r.Note="TEST edited"),"isolated verdict edited");Check(QvReviewHistory.Revoke(historyDir,verdict.Id,"TEST revoked"),"isolated verdict revoked");Check(QvReviewHistory.LatestActive(QvReviewHistory.Load(historyDir),"000915",new DateTime(2026,11,18))==null,"revoked verdict not active");
        var universe=Path.Combine(root,"data","cache","quality_value","universe.json");if(File.Exists(universe))File.Copy(universe,Path.Combine(cache,"universe.json"),true);
        foreach(var file in Directory.GetFiles(cache,"*.json"))File.SetLastWriteTime(file,new DateTime(2000,1,1));
        var recalc=await QvScan.RunAsync(data,"recalc",_=>{},em,null,onlyCodes:codes,notify:false,throwOnNetwork:true);
        Check(recalc.NetworkCalls==0,"actual offline recalc expired/missing all resources zero requests");Check(File.Exists(Path.Combine(data,"quality_value","config_run.json")),"successful isolated recalc commits explicit config/source manifest");
        var global=Path.Combine(fixture,"config","quality_value","global.yaml");File.WriteAllText(global,File.ReadAllText(global).Replace("qualified_min: 70","qualified_min: 71"));
        var changed=await QvScan.RunAsync(data,"recalc",_=>{},em,null,onlyCodes:codes,notify:false,throwOnNetwork:true);Check(changed.NetworkCalls==0,"rule-changed actual offline recalc zero requests");

    }
}
