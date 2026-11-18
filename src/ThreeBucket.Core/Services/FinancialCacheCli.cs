using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ThreeBucket.Core.Services.QualityValue;
namespace ThreeBucket.Core.Services;
/// <summary>Authoritative C# adapter for legacy readers. Offline by default; never reads user app settings.</summary>
public static class FinancialCacheCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        try
        {
            var resource=Arg("--financial-cache")??"";
            if(resource=="contract")
            {
                var file=Arg("--file")??throw new ArgumentException("--file required");
                var kind=Arg("--legacy-kind");
                var c=kind==null?FinancialContract.ReadEnvelope(file):FinancialContract.ReadProjection(file,kind,Arg("--code")??"unknown");var code=Arg("--code");
                if(code!=null)c.Values=c.Values.Where(x=>x.Security==FinancialContract.CanonicalSecurity(code)).ToList();
                Console.WriteLine(JsonSerializer.Serialize(c));return 0;
            }
            if(resource=="tushare")return await TushareSharedAdapter.RunAsync(args);
            if(resource!="yjbb")throw new ArgumentException("Supported: --financial-cache yjbb --period YYYYMMDD --data DIR [--refresh|--supplement-missing]; --financial-cache contract --file ENVELOPE. Other resources must use native C# consumers; no direct fallback.");
            var period=Arg("--period")??throw new ArgumentException("--period required");
            if(!Regex.IsMatch(period,@"^\d{4}(0331|0630|0930|1231)$"))throw new ArgumentException("period must be a calendar report quarter YYYYMMDD");
            var data=Path.GetFullPath(Arg("--data")??Path.Combine(Environment.CurrentDirectory,"data"));var cache=Path.Combine(data,"cache");
            var refresh=args.Contains("--refresh")||args.Contains("--supplement-missing");
            if(!Directory.Exists(cache)&&!refresh){Console.WriteLine(JsonSerializer.Serialize(new{Protocol="shared-financial-cli-v1",Provider="eastmoney",Resource="RPT_LICO_FN_CPD",Period=period,Status="missing/pending",Rows=Array.Empty<object>(),NetworkCalls=0}));return 0;}
            using var access=new FinancialAccess{Offline=!refresh,ThrowOnNetwork=!refresh,AllowFinancialNetwork=refresh,MissingOnly=args.Contains("--supplement-missing"),CycleMonths=FinancialAccess.ConfiguredCycle(cache)};
            var rows=await new EastMoneyClient(cache).GetYjbbAsync(period);
            Console.WriteLine(JsonSerializer.Serialize(new{Protocol="shared-financial-cli-v1",Provider="eastmoney",Resource="RPT_LICO_FN_CPD",Contract=FinancialContract.Version,Period=period,
                Status=rows.Count==0?"missing/pending":"available",Mode=refresh?(access.MissingOnly?"supplement_missing":"refresh_due_only"):"offline",Rows=rows,
                SourceData=access.Sources.OrderBy(x=>x.Key).ToDictionary(x=>x.Key,x=>x.Value),NetworkCalls=access.OutboundCalls,Failures=access.RefreshFailures}));
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.Message);return 2;}
    }
}
public static class ConfigGovernanceCli
{
    public static Task<int> RunAsync(string[] args)
    {
        string? Arg(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        try
        {
            var op=Arg("--config")??"validate";var data=Path.GetFullPath(Arg("--data")??Path.Combine(Environment.CurrentDirectory,"data"));
            if(op=="export")
            {
                var file=Arg("--path")??throw new ArgumentException("--path required for config export");
                if(Arg("--family")!="candidate")throw new ArgumentException("supported export family: candidate");
                var node=QvYaml.ParseFile(file);ConfigGovernance.Validate(node,"02_strategy_config.yaml",file);
                Console.WriteLine(JsonSerializer.Serialize(new{Protocol="validated-config-v1",Family="candidate",Schema=ConfigGovernance.SchemaVersion,Hash=ConfigGovernance.Hash(node),Config=node,Defaults=ConfigGovernance.DefaultsReport(node,"02_strategy_config.yaml")}));
                return Task.FromResult(0);
            }
            var cfg=QvConfig.Load(data,Arg("--path"));
            if(op=="diff")Console.WriteLine(ConfigGovernance.Preview(cfg,Arg("--before")??Path.Combine(data,"quality_value","config_run.json")));
            else if(op=="validate")
            {
                Console.WriteLine(ConfigGovernance.Preview(cfg,null));
                ConfigGovernance.ValidateStrategiesCsv(Path.Combine(data,"strategies.csv"));
                var candidate=Path.Combine(Path.GetDirectoryName(data)!,"trading-system","02_strategy_config.yaml");
                if(File.Exists(candidate)){PoolThresholds.Load(candidate);Console.WriteLine("VALID "+candidate);}
                var parent=Path.GetDirectoryName(data)!;var cycle=Path.Combine(parent,"config","financial_cache.yaml");
                if(File.Exists(cycle))ConfigGovernance.Validate(QvYaml.ParseFile(cycle),"financial_cache.yaml",cycle);
                foreach(var name in new[]{"bottom_reversal_v1.json","bottom_reversal_v1_1.json","bottom_reversal_v1_2.json","bottom_slowrise_v1.json","bottom_slowrise_v2.json"})
                {var file=Path.Combine(data,name);if(File.Exists(file)){ConfigGovernance.ReadJson(file,name);Console.WriteLine("VALID "+file);}}
            }
            else throw new ArgumentException("--config validate|diff [--data DIR] [--path explicit-QV-config] [--before config_run.json]");
            return Task.FromResult(0);
        }
        catch(Exception ex){Console.Error.WriteLine(ex.Message);return Task.FromResult(2);}
    }
}
