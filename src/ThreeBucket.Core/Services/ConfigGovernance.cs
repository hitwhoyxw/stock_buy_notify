using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ThreeBucket.Core.Services.QualityValue;
namespace ThreeBucket.Core.Services;

public sealed class ConfigValidationException(string file,string key,int line,string problem)
    : Exception($"{file}:{line}: {key}: {problem}");

/// <summary>Read-only strict common validation. No user settings, network, directories or snapshot writes.</summary>
public static class ConfigGovernance
{
    public static bool TryNumber(JsonNode? n,out double v)
    {
        v=double.NaN;
        return n is JsonValue j&&!j.TryGetValue<string>(out _)&&double.TryParse(n.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out v)&&double.IsFinite(v);
    }
    public static double Number(JsonNode n)=>TryNumber(n,out var v)?v:throw new InvalidDataException("finite numeric value required");
    public const string SchemaVersion="config-contract-v1";
    public static JsonNode Schema(string name)
    {
        var a=typeof(ConfigGovernance).Assembly;var n=a.GetManifestResourceNames().SingleOrDefault(x=>x.EndsWith(".ConfigSchemas."+name+".json",StringComparison.Ordinal));
        if(n==null)throw new InvalidDataException("Unregistered config schema: "+name);
        using var s=a.GetManifestResourceStream(n)!;return JsonNode.Parse(s)!;
    }
    public static int Line(string file,string path)
    {
        if(!File.Exists(file))return 1;var key=path.Split('.').Last().Split('[')[0];var lines=File.ReadAllLines(file);
        for(var i=0;i<lines.Length;i++)if(Regex.IsMatch(lines[i],"(?:^|[ {,\"\\t])"+Regex.Escape(key)+"[\" ]*\\s*:"))return i+1;
        return 1;
    }
    private static void Fail(string file,string path,string why)=>throw new ConfigValidationException(file,path,Line(file,path),why);
    public static JsonNode ReadJson(string file,string? schema=null)
    {
        var text=File.ReadAllText(file);try
        {
            var utf8=Encoding.UTF8.GetBytes(text);var scan=new Utf8JsonReader(utf8,new JsonReaderOptions{CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});
            var scopes=new Stack<(HashSet<string>? Seen,string Path)>();var pending="";
            while(scan.Read())
            {
                if(scan.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    var path=scopes.Count==0?"":scopes.Peek().Path+(pending.Length>0?"."+pending:"[]");
                    scopes.Push((scan.TokenType==JsonTokenType.StartObject?new HashSet<string>(StringComparer.Ordinal):null,path.TrimStart('.')));pending="";
                }
                else if(scan.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray){scopes.Pop();pending="";}
                else if(scan.TokenType==JsonTokenType.PropertyName)
                {
                    pending=scan.GetString()!;var frame=scopes.Peek();
                    if(!frame.Seen!.Add(pending)){var line=1;for(var i=0;i<scan.TokenStartIndex;i++)if(utf8[i]==(byte)'\n')line++;throw new ConfigValidationException(file,(frame.Path+"."+pending).TrimStart('.'),line,"duplicate key");}
                }
            }
            using var d=JsonDocument.Parse(text,new JsonDocumentOptions{CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});
            void Unique(JsonElement e,string path)
            {
                if(e.ValueKind==JsonValueKind.Object){var set=new HashSet<string>(StringComparer.Ordinal);foreach(var p in e.EnumerateObject()){var k=path.Length==0?p.Name:path+"."+p.Name;if(!set.Add(p.Name))Fail(file,k,"duplicate key");Unique(p.Value,k);}}
                else if(e.ValueKind==JsonValueKind.Array){var i=0;foreach(var x in e.EnumerateArray())Unique(x,path+"["+i+++"]");}
            }
            Unique(d.RootElement,"");var root=JsonNode.Parse(d.RootElement.GetRawText())!;
            if(schema!=null)Validate(root,schema,file);return root;
        }
        catch(JsonException ex){throw new ConfigValidationException(file,"$",(int)(ex.LineNumber??0)+1,ex.Message);}
    }
    public static void Validate(JsonNode? value,string schemaName,string file,bool validateShape=true)
    {
        var schema=Schema(schemaName);if(validateShape)Walk(value,schema["shape"]!,"",file);
        if(value is not JsonObject)Fail(file,"$","configuration root must be a mapping");
        var d=Flatten(value);
        double? N(string k)=>d.TryGetValue(k,out var x)&&double.TryParse(x,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:null;
        void Range(string k,double min,double max){if(N(k) is {} v&&(v<min||v>max))Fail(file,k,$"must be in [{min},{max}]");}
        void Ordered(string low,string high){if(N(low) is {} l&&N(high) is {} h&&l>=h)Fail(file,low,"must be less than "+high);}
        foreach(var kv in d)
        {
            var k=kv.Key;
            if(N(k) is {} n)
            {
                if(!double.IsFinite(n))Fail(file,k,"must be finite");
                if(k.Contains("weights.",StringComparison.Ordinal)&&n<0)Fail(file,k,"weight must be nonnegative");
                if(Regex.IsMatch(k,@"(?:^|\.)(?:concurrency|window_years|min_peers|supporting_dimensions|profit_positive_years|ocf_positive_years|daily_review_push_max|default_verdict_valid_days|div_years_min|profit_years_min|full_rescan_day_to|nov_light_day_to|alert_dedupe_trading_days)$")&&(n!=Math.Truncate(n)||n<1))Fail(file,k,"positive integer required");
            }
        }
        if(value!["weights"] is JsonObject weights){var sum=weights.Sum(x=>Number(x.Value!));if(Math.Abs(sum-100)>0.000001)Fail(file,"weights","sum must equal 100; no implicit renormalization");}
        if(schemaName=="global.yaml")
        {
            foreach(var k in (validateShape?new[]{"version","qualified_min","watch_min"}:Array.Empty<string>()))if(value[k]==null)Fail(file,k,"required key missing (v1.1)");
            Range("qualified_min",0,100);Range("watch_min",0,100);Range("concurrency",1,64);Ordered("watch_min","qualified_min");
            Range("min_re",0.000001,1);Range("approach_fair_ratio",0.000001,1);Range("full_rescan_day_to",1,31);Range("nov_light_day_to",1,30);
            if(value["full_rescan_months"] is JsonArray months&&months.Any(x=>x==null||Number(x)<1||Number(x)>12||Number(x)!=Math.Truncate(Number(x))))Fail(file,"full_rescan_months","months must be integers 1..12");
            if(value["mos"] is JsonArray mos){double prev=double.PositiveInfinity;foreach(var m in mos){double? sc=m?["score_min"] is {} sm?Number(sm):null;double? discount=m?["discount"] is {} dm?Number(dm):null;if(sc==null||discount==null||sc<0||sc>100||sc>=prev||discount<0||discount>=1)Fail(file,"mos","descending unique score_min 0..100 and discount [0,1) required");prev=sc.Value;}}
        }
        if(schemaName=="financial_cache.yaml" && N("cycle_months") is not (3 or 6))Fail(file,"cycle_months","only 3 (quarterly) or 6 (half-year) allowed");
        if(System.Text.RegularExpressions.Regex.IsMatch(schemaName,@"^T[1-5]\.yaml$"))
        {
            Range("roe_min",-1,1);Range("payout_min",0,1);Range("maint_capex_ratio",0,1);
            foreach(var k in new[]{"roe_sus_w5","roe_sus_w3","roe_sus_w1"})Range(k,0,1);
            if(N("roe_sus_w5") is {} w5&&N("roe_sus_w3") is {} w3&&N("roe_sus_w1") is {} w1&&Math.Abs(w5+w3+w1-1)>0.000001)Fail(file,"roe_sus_w5","ROE horizon weights must sum to 1");
        }
        if(schemaName=="review.yaml")
        {
            if(value["daily_review_push_min_priority"]?.ToString() is not ("P0" or "P1" or "P2" or "P3"))Fail(file,"daily_review_push_min_priority","enum P0/P1/P2/P3 required");
            Range("daily_review_push_max",1,100);foreach(var key in new[]{"priority.p0_min","priority.p1_min","priority.p2_min"})Range(key,0,100);Ordered("priority.p2_min","priority.p1_min");Ordered("priority.p1_min","priority.p0_min");
            var max=new[]{"earnings_max","risk_event_max","valuation_max","data_quality_max","price_urgency_max"};
            if(max.All(x=>N("priority."+x)!=null)&&Math.Abs(max.Sum(x=>N("priority."+x)!.Value)-100)>0.000001)Fail(file,"priority","five component maxima must sum to 100");
            Ordered("earnings.deduct_yoy_high","earnings.deduct_yoy_moderate");
        }
        if(schemaName=="industry_relative.yaml")
        {
            Range("window_years",3,10);Range("min_peers",10,10000);Range("supporting_dimensions",2,3);
            foreach(var k in new[]{"composite_min","dimension_min","core_min","other_min"})Range(k,0,100);
            if(N("profit_positive_years")>N("window_years")||N("ocf_positive_years")>N("window_years"))Fail(file,"window_years","required positive years exceed window");
            if(value["groups"] is JsonArray groups)
            {
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var group in groups){if(group is not JsonObject g||g["name"]?.ToString() is not {Length:>0} name||!names.Add(name))Fail(file,"groups","unique nonempty group name required");if(group?["industries"] is not JsonArray{Count:>0}||group?["types"] is not JsonArray{Count:>0})Fail(file,"groups","industries and types required for comparable groups");}
            }
            if(value["eligible_types"] is JsonArray types&&types.Any(x=>x?.ToString() is not ("T1" or "T4" or "T5")))Fail(file,"eligible_types","supported enum T1/T4/T5");
        }
        if(schemaName=="valuation.yaml")
        {
            foreach(var group in new[]{"re_premium","div_yield_premium"})foreach(var t in new[]{"T1","T2","T3","T4","T5"}){Ordered(group+"."+t+".optimistic",group+"."+t+".base");Ordered(group+"."+t+".base",group+"."+t+".conservative");}
            Ordered("dispersion_confidence.high","dispersion_confidence.mid");Ordered("dispersion_confidence.mid","dispersion_confidence.low");
            if(N("bank_valuation.model_weight") is {} a&&N("bank_valuation.peer_weight") is {} b&&Math.Abs(a+b-1)>0.000001)Fail(file,"bank_valuation","model_weight+peer_weight must equal 1");
            Ordered("bank_valuation.growth_rate_base_min","bank_valuation.growth_rate_max");
        }
        if(schemaName is "qv-root.json" or "quality_value_v1.json" or "quality_value_v1.1.json")
            foreach(var name in new[]{"global","T1","T2","T3","T4","T5","valuation","monitoring","review","industry_relative"})
                if(value[name] is JsonObject child)Validate(child,name+".yaml",file,false);
        foreach(var kv in d.Where(x=>x.Key.EndsWith(".min",StringComparison.Ordinal))){var other=kv.Key[..^4]+".max";if(N(kv.Key)>N(other))Fail(file,kv.Key,"must not exceed "+other);}
    }
    private static void Walk(JsonNode? node,JsonNode shape,string path,string file)
    {
        var type=shape["type"]!.ToString();
        if(type=="any")return;
        if(type=="oneOf")
        {
            foreach(var choice in (JsonArray)shape["choices"]!){try{Walk(node,choice!,path,file);return;}catch(ConfigValidationException){}}
            Fail(file,path,"value does not match registered scalar alternatives");return;
        }
        if(type=="evidence")
        {
            if(node is not JsonObject e){Fail(file,path,"evidence mapping required");return;}
            var allowed=new HashSet<string>(new[]{"report_date","source","total_net_profit","external_recurring_dividends_received","minority_dividends_paid","cash_interest_paid","restricted_cash","cash_available","debt_due","cash_dividends_to_parent_shareholders_paid"});
            foreach(var x in e){if(!Regex.IsMatch(x.Key,@"^\d{6}$"))Fail(file,path+"."+x.Key,"six-digit evidence security required");if(x.Value is not JsonArray arr){Fail(file,path+"."+x.Key,"annual evidence array required");continue;}foreach(var item in arr){if(item is not JsonObject o){Fail(file,path,"evidence object required");continue;}foreach(var f in o){if(!allowed.Contains(f.Key))Fail(file,path+"."+x.Key+"."+f.Key,"unknown evidence key");if(f.Key is "source" or "report_date"){if(f.Value is not JsonValue v||!v.TryGetValue<string>(out _))Fail(file,path+"."+f.Key,"string required");}else if(f.Value is not JsonValue v||!TryNumber(f.Value,out var n))Fail(file,path+"."+f.Key,"finite number required");}}}
            return;
        }
        if(type=="object")
        {
            if(node is not JsonObject obj){Fail(file,path,"mapping required");return;}var props=shape["properties"] as JsonObject??new();
            foreach(var kv in obj){var p=path.Length==0?kv.Key:path+"."+kv.Key;if(!props.TryGetPropertyValue(kv.Key,out var sub))Fail(file,p,"unknown key (typo or unsupported schema)");else Walk(kv.Value,sub!,p,file);}return;
        }
        if(type=="array")
        {
            if(node is not JsonArray a){Fail(file,path,"array required");return;}
            if(shape["tuple"] is JsonArray tuple){if(a.Count!=tuple.Count)Fail(file,path,"tuple length must be "+tuple.Count);for(var i=0;i<Math.Min(a.Count,tuple.Count);i++)Walk(a[i],tuple[i]!,path+"["+i+"]",file);}
            else for(var i=0;i<a.Count;i++)Walk(a[i],shape["items"]!,path+"["+i+"]",file);return;
        }
        if(node is not JsonValue value){Fail(file,path,type+" scalar required (missing is not zero)");return;}
        if(type=="number"){if(!TryNumber(node,out var n))Fail(file,path,"finite numeric scalar required, not quoted string/default");}
        else if(type=="boolean"){if(!value.TryGetValue<bool>(out _))Fail(file,path,"boolean true/false required");}
        else if(type=="string"&&!value.TryGetValue<string>(out _))Fail(file,path,"string required");
    }
    public static Dictionary<string,string> Flatten(JsonNode? node)
    {
        var d=new Dictionary<string,string>(StringComparer.Ordinal);
        void W(JsonNode? n,string p){if(n is JsonObject o){foreach(var kv in o.OrderBy(x=>x.Key,StringComparer.Ordinal))W(kv.Value,p.Length==0?kv.Key:p+"."+kv.Key);}else if(n is JsonArray a){for(var i=0;i<a.Count;i++)W(a[i],p+"["+i+"]");}else d[p]=n is JsonValue v&&v.TryGetValue<double>(out var num)?num.ToString("R",CultureInfo.InvariantCulture):n?.ToJsonString()??"null";}
        W(node,"");return d;
    }
    public static string Hash(JsonNode? node)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",Flatten(node).Select(x=>x.Key+"="+x.Value))))).ToLowerInvariant();
    public static string DefaultsReport(JsonNode node,string schemaName)
    {
        var expected=Flatten(Schema(schemaName)["baseline"]);var actual=Flatten(node);var missing=expected.Keys.Except(actual.Keys,StringComparer.Ordinal).ToList();
        return missing.Count==0?"no absent optional keys":"legacy optional keys absent (existing code defaults, no override): "+string.Join(", ",missing);
    }
    public static string Preview(QvConfig current,string? previousSnapshot)
    {
        var sb=new StringBuilder();sb.AppendLine($"配置校验通过 | {SchemaVersion} | SHA256 {current.Hash}");sb.AppendLine("读取/校验/预览不联网、不写盘、不推送。");
        var now=Flatten(current.Root);Dictionary<string,string>? before=null;
        if(previousSnapshot!=null&&File.Exists(previousSnapshot)){var old=ReadJson(previousSnapshot);before=Flatten(old["Config"]??old);}
        if(before==null)sb.AppendLine("没有上次成功运行的配置快照；不猜测受影响股票数量。");
        else{var keys=before.Keys.Union(now.Keys,StringComparer.Ordinal).Order(StringComparer.Ordinal);var changed=0;foreach(var k in keys){before.TryGetValue(k,out var a);now.TryGetValue(k,out var b);if(a==b)continue;changed++;sb.AppendLine($"{k}: {a??"<missing>"} → {b??"<missing>"} | {Impact(k)}");}sb.AppendLine($"变化 {changed} 项；仅规则潜在影响，不声称具体股票变化。");}
        sb.AppendLine("数据原始缓存不因规则变化失效。" );foreach(var x in current.ValidationNotes)sb.AppendLine(x);return sb.ToString();
    }
    private static string Impact(string k)=>k.StartsWith("review.")?"复核/提醒分流，不改变原始质量分或估值":k.StartsWith("industry_relative.")?"行业分位准入/待核实状态":k.StartsWith("valuation.")||k.Contains("premium")||k.Contains("g_div")||k.Contains("fcf_g")?"估值/区间":k.StartsWith("monitoring.")?"监控/提醒":"质量筛选/评分或运行参数；需本地同输入重算验证";

    public static void ValidateStrategiesCsv(string file)
    {
        if(!File.Exists(file))return; // documented in-memory built-ins, never seeded by a read
        using var reader=new StreamReader(file);using var csv=new CsvHelper.CsvReader(reader,CultureInfo.InvariantCulture);
        if(!csv.Read())Fail(file,"$","empty existing strategy CSV is malformed");csv.ReadHeader();var header=csv.HeaderRecord??[];
        var known=new HashSet<string>(new[]{"id","name","type","indicator","operator","threshold","condition","action","priority","enabled"},StringComparer.Ordinal);
        if(header.Distinct(StringComparer.Ordinal).Count()!=header.Length)Fail(file,"header","duplicate columns");
        foreach(var h in header)if(!known.Contains(h))Fail(file,h,"unknown strategy column");
        foreach(var h in new[]{"id","name","type","enabled"})if(!header.Contains(h))Fail(file,h,"required strategy column missing");
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while(csv.Read())
        {
            string G(string k)=>header.Contains(k)?csv.GetField(k)??"":"";
            var row=(int)csv.Parser.Row;
            void Bad(string k,string why)=>throw new ConfigValidationException(file,k,row,why);
            if(G("id").Length==0||!ids.Add(G("id")))Bad("id","missing/duplicate strategy id");
            if(G("name").Length==0)Bad("name","required strategy name");
            if(G("type") is not ("buy" or "sell" or "watch" or "both"))Bad("type","enum buy/sell/watch/both required");
            if(G("enabled") is not ("0" or "1"))Bad("enabled","enum 0/1 required");
            if(G("priority").Length>0&&G("priority") is not ("P0" or "P1" or "P2" or "P3"))Bad("priority","enum P0/P1/P2/P3 required");
            var strategy=new ThreeBucket.Core.Models.Strategy{Id=G("id"),Name=G("name"),Type=G("type"),Indicator=G("indicator"),Operator=G("operator"),Threshold=G("threshold"),Condition=G("condition")};
            var error=StrategyEngine.ValidateStrategy(strategy);if(error!=null)Bad("condition/indicator",error);
            if(strategy.Condition.Length==0)
            {
                if(!double.TryParse(strategy.Threshold,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n))Bad("threshold","finite numeric threshold required; cannot become missing/default");
                if(strategy.Operator is not ("<" or "<=" or ">" or ">=" or "==" or "=" or "cross_up" or "cross_down"))Bad("operator","unsupported comparison");
            }
        }
    }

    public static void CommitSuccessfulRun(string path,JsonNode configuration,object sources,string module)
    {
        FinancialRawStore.Atomic(path,JsonSerializer.Serialize(new{Schema=SchemaVersion,Module=module,Hash=Hash(configuration),Config=configuration,SourceData=sources,FinancialContract=FinancialContract.Version}));
    }

    public static void CrossValidate(QvConfig cfg)
    {
        var g=cfg.D("T1.fcf_g",0.03);var re=cfg.D("global.min_re",0.08);var t1=Directory.Exists(cfg.Path)?Path.Combine(cfg.Path,"T1.yaml"):cfg.Path;var t4=Directory.Exists(cfg.Path)?Path.Combine(cfg.Path,"T4.yaml"):cfg.Path;if(re<=g)Fail(t1,"fcf_g","static base minimum Re must exceed terminal g");
        if(cfg.D("T4.g_div",0.01)>=re||cfg.D("T4.g_pb",0.015)>=re)Fail(t4,"g_div","static growth must be below base min Re");
    }
}
