using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace ThreeBucket.Core.Services;
/// <summary>Only explicit supplemental Tushare acquisition uses Python SDK, inside the authoritative C# lock/policy.</summary>
public static class TushareSharedAdapter
{
    private static readonly HashSet<string> Allowed=new(new[]{"fina_audit","pledge_stat","pledge_detail","repurchase","dividend","index_classify","index_member_all","fina_indicator","bank_indicator","anns_d"},StringComparer.Ordinal);
    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string n){var i=Array.IndexOf(args,n);return i>=0&&i+1<args.Length?args[i+1]:null;}
        var resource=Arg("--resource")??throw new ArgumentException("--resource required");if(!Allowed.Contains(resource))throw new ArgumentException("Unsupported Tushare resource; no direct fallback");
        var parameters=JsonNode.Parse(Arg("--params-json")??"{}") as JsonObject??throw new ArgumentException("params must be mapping");
        if(parameters.Any(p=>Regex.IsMatch(p.Key,"token|secret|password|key",RegexOptions.IgnoreCase)))throw new ArgumentException("Credentials must never be request params/cache keys");
        var root=Path.GetFullPath(Arg("--root")??Environment.CurrentDirectory);var cache=Path.Combine(Path.GetFullPath(Arg("--data")??Path.Combine(root,"data")),"cache");
        var canonical=new JsonObject();foreach(var p in parameters.OrderBy(x=>x.Key,StringComparer.Ordinal))canonical[p.Key]=p.Value?.DeepClone();
        var sec=parameters["ts_code"]?.ToString()??parameters["index_code"]?.ToString()??"market/period-batch";
        var source="tushare://"+resource+"?params="+Uri.EscapeDataString(canonical.ToJsonString());var refresh=args.Contains("--refresh");
        if(!Directory.Exists(cache)&&!refresh){Console.WriteLine(JsonSerializer.Serialize(new{Protocol="shared-financial-cli-v1",Provider="tushare",Resource=resource,Status="missing/pending",Rows=Array.Empty<object>(),NetworkCalls=0}));return 0;}
        using var access=new FinancialAccess{Offline=!refresh,ThrowOnNetwork=!refresh,AllowFinancialNetwork=refresh,CycleMonths=FinancialAccess.ConfiguredCycle(cache)};
        async Task<JsonDocument> Fetch()
        {
            access.BeforeNetwork("tushare:"+resource,resource is "fina_audit" or "fina_indicator" or "bank_indicator");
            var worker=Path.Combine(root,"scripts","tushare_supplement","shared_provider_worker.py");if(!File.Exists(worker))throw new FileNotFoundException("Supplement worker unavailable; no direct fallback",worker);
            var psi=new ProcessStartInfo(Arg("--python")??"python"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};psi.ArgumentList.Add(worker);psi.ArgumentList.Add(resource);psi.ArgumentList.Add(root);
            using var p=Process.Start(psi)??throw new InvalidOperationException("Supplement worker failed to start");
            await p.StandardInput.WriteAsync(canonical.ToJsonString());p.StandardInput.Close();
            var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try{await p.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){p.Kill(true);throw new TimeoutException("Supplement worker timed out");}
            var text=await stdout;await stderr; // never echo provider credential/error bodies
            if(p.ExitCode!=0)throw new InvalidDataException("Tushare supplemental provider failed; old raw preserved, credential values not logged");return JsonDocument.Parse(text);
        }
        using var raw=await new FinancialRawStore(cache).GetAsync("tushare",resource,sec,source,Fetch);
        var rows=FinancialContract.Rows(raw.RootElement).Select(x=>x.Clone()).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new{Protocol="shared-financial-cli-v1",Provider="tushare",Resource=resource,Contract=FinancialContract.Version,Status=rows.Length==0?"missing/pending":"available",Rows=rows,NetworkCalls=access.OutboundCalls,SourceData=access.Sources,Failures=access.RefreshFailures}));return 0;
    }
}
