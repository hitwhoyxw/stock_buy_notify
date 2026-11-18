using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// v1.1 参数：优先加载 config/quality_value/*.yaml，保留 data/quality_value_v1.json 作为旧参数对比。
/// 全部阈值外置，业务代码只读本类。
/// </summary>
public sealed class QvConfig
{
    public JsonNode Root { get; }
    public string Path { get; }
    public string SourceKind { get; }
    public string Hash => ConfigGovernance.Hash(Root);
    public string ContractVersion => ConfigGovernance.SchemaVersion;
    private readonly List<string> _validationNotes=new();
    public IReadOnlyList<string> ValidationNotes => _validationNotes.Concat(new[]{"Legacy v1/v1.1 supported; missing optional keys retain historical accessor defaults; no injected reweighting.","Financial definitions: "+FinancialContract.Version}).ToArray();
    public void CommitRunSnapshot(string outDir, object sourceData)
    {
        var manifest=new{Schema=ContractVersion,Hash,SourceKind,Source=Path,Config=Root,SourceData=sourceData,FinancialContract=FinancialContract.Version};
        FinancialRawStore.Atomic(System.IO.Path.Combine(outDir,"config_run.json"),JsonSerializer.Serialize(manifest,Jo));
    }
    public const string FileName = "quality_value_v1.json";
    public const string FileNameV11 = "quality_value_v1.1.json";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions Jo = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private QvConfig(JsonNode root, string path, string kind) { Root = root; Path = path; SourceKind = kind; }

    /// <summary>默认：若存在 YAML 目录则合并加载，否则回退 quality_value_v1.json。</summary>
    public static QvConfig Load(string dataDir, string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path)) throw new FileNotFoundException("Explicit config path missing",path);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            if (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                return FromYamlDir(System.IO.Path.GetDirectoryName(path)!, dataDir);
            return FromJson(path, "json-explicit");
        }
        var yamlDir = FindYamlDir(dataDir);
        if (yamlDir is not null) return FromYamlDir(yamlDir, dataDir);
        var json = System.IO.Path.GetFullPath(System.IO.Path.Combine(dataDir, FileName));
        if (!File.Exists(json))
            throw new FileNotFoundException($"参数文件不存在: 未找到 config/quality_value/*.yaml 或 {json}");
        return FromJson(json, "json-v1-fallback");
    }

    public static QvConfig LoadLegacyV1(string dataDir)
    {
        var json = System.IO.Path.GetFullPath(System.IO.Path.Combine(dataDir, FileName));
        if (!File.Exists(json)) throw new FileNotFoundException("找不到旧参数 " + json);
        return FromJson(json, "json-v1");
    }

    public static string? FindYamlDir(string dataDir)
    {
        var cur = new DirectoryInfo(System.IO.Path.GetFullPath(dataDir));
        for (var i = 0; i < 6 && cur is not null; i++, cur = cur.Parent)
        {
            var p = System.IO.Path.Combine(cur.FullName, "config", "quality_value");
            if (File.Exists(System.IO.Path.Combine(p, "global.yaml"))) return p;
        }
        var sibling = System.IO.Path.GetFullPath(System.IO.Path.Combine(dataDir, "..", "config", "quality_value"));
        if (File.Exists(System.IO.Path.Combine(sibling, "global.yaml"))) return sibling;
        return null;
    }

    public static QvConfig FromYamlDir(string dir, string? dataDir = null)
    {
        foreach(var required in new[]{"global.yaml","T1.yaml","T2.yaml","T3.yaml","T4.yaml","T5.yaml","valuation.yaml","monitoring.yaml"})
            if(!File.Exists(System.IO.Path.Combine(dir,required)))throw new ConfigValidationException(System.IO.Path.Combine(dir,required),"$",1,"required v1.1 file missing; optional review/industry files alone may be absent");
        var knownFiles=new HashSet<string>(new[]{"global.yaml","T1.yaml","T2.yaml","T3.yaml","T4.yaml","T5.yaml","valuation.yaml","monitoring.yaml","review.yaml","industry_relative.yaml"},StringComparer.OrdinalIgnoreCase);
        foreach(var file in Directory.EnumerateFiles(dir,"*.yaml"))if(!knownFiles.Contains(System.IO.Path.GetFileName(file)))throw new ConfigValidationException(file,"$",1,"unknown quality configuration file");
        var notes=new List<string>();
        var root = new JsonObject();
        void MergeFile(string name, string? wrap)
        {
            var p = System.IO.Path.Combine(dir, name);
            if (!File.Exists(p)){notes.Add("Absent optional file: "+name+"; historical accessor defaults/feature-off semantics retained"); return;}
            var n = QvYaml.ParseFile(p);
            ConfigGovernance.Validate(n, name, p);
            notes.Add(name+": "+ConfigGovernance.DefaultsReport(n,name));
            if (wrap is null)
            {
                if (n is JsonObject o)
                {
                    foreach (var kv in o) root[kv.Key] = kv.Value?.DeepClone();
                }
            }
            else root[wrap] = n;
        }
        // global.yaml 的键进入 global.*；其中若干顶层键提升到根（与 v1 JSON 路径兼容）
        var gpath = System.IO.Path.Combine(dir, "global.yaml");
        if (File.Exists(gpath))
        {
            var g = QvYaml.ParseFile(gpath);
            ConfigGovernance.Validate(g, "global.yaml", gpath);
            root["global"] = g;
        }
        MergeFile("T1.yaml", "T1");
        MergeFile("T2.yaml", "T2");
        MergeFile("T3.yaml", "T3");
        MergeFile("T4.yaml", "T4");
        MergeFile("T5.yaml", "T5");
        // valuation.yaml：re_premium / div_yield_* 放到根，以便沿用 D("re_premium.T1.base")
        var vpath = System.IO.Path.Combine(dir, "valuation.yaml");
        if (File.Exists(vpath))
        {
            var v = QvYaml.ParseFile(vpath);
            ConfigGovernance.Validate(v, "valuation.yaml", vpath);
            root["valuation"] = v;
            if (v is JsonObject vo)
            {
                foreach (var k in new[] { "re_premium", "div_yield_premium", "div_yield_floor", "dispersion_confidence" })
                    if (vo[k] is JsonNode n) root[k] = n.DeepClone();
            }
        }
        MergeFile("monitoring.yaml", "monitoring");
        MergeFile("review.yaml", "review");
        MergeFile("industry_relative.yaml", "industry_relative");
        // 兼容：global.min_re 等
        var cfg = new QvConfig(root, dir, "yaml-v1.1");
        ConfigGovernance.CrossValidate(cfg);
        cfg._validationNotes.AddRange(notes);
        return cfg;
    }

    private static QvConfig FromJson(string path, string kind)
    {
        var fileName=System.IO.Path.GetFileName(path);
        var schema = fileName is FileName or FileNameV11 ? fileName : "qv-root.json";
        var node = ConfigGovernance.ReadJson(path, schema);
        var cfg=new QvConfig(node,path,kind);
        ConfigGovernance.CrossValidate(cfg);
        cfg._validationNotes.Add(ConfigGovernance.DefaultsReport(node,schema));
        return cfg;
    }

    public void WriteSnapshotJson(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Root.ToJsonString(Jo));
    }

    /// <summary>验收#12：保存当前参数集（带时间戳，不覆盖历史）。</summary>
    public string SaveNamedSet(string dataDir, string label)
    {
        var dir = System.IO.Path.Combine(dataDir, "quality_value", "param_sets");
        Directory.CreateDirectory(dir);
        var safe = string.Join("_", (label ?? "set").Split(System.IO.Path.GetInvalidFileNameChars()));
        var dest = System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safe}.json");
        WriteSnapshotJson(dest);
        return dest;
    }

    public static string Compare(QvConfig a, QvConfig b)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"A: {a.SourceKind}  {a.Path}");
        sb.AppendLine($"B: {b.SourceKind}  {b.Path}");
        var pa = Flatten(a.Root); var pb = Flatten(b.Root);
        var keys = pa.Keys.Union(pb.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var diffs = 0;
        foreach (var k in keys)
        {
            pa.TryGetValue(k, out var va); pb.TryGetValue(k, out var vb);
            va ??= "<缺失>"; vb ??= "<缺失>";
            if (!string.Equals(va, vb, StringComparison.Ordinal))
            {
                diffs++;
                sb.AppendLine($"{k}\t{va}\t{vb}");
            }
        }
        sb.Insert(0, $"差异条目 {diffs} / 合计键 {keys.Count}{Environment.NewLine}");
        return sb.ToString();
    }

    private static Dictionary<string, string> Flatten(JsonNode? n, string prefix = "")
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonNode? x, string p)
        {
            if (x is JsonObject o)
            {
                foreach (var kv in o) Walk(kv.Value, p.Length == 0 ? kv.Key : p + "." + kv.Key);
            }
            else if (x is JsonArray a)
            {
                for (var i = 0; i < a.Count; i++) Walk(a[i], p + $"[{i}]");
            }
            else d[p] = x is null ? "<null>" : x.ToString();
        }
        Walk(n, prefix);
        return d;
    }

    public JsonNode? Node(string path)
    {
        // 兼容：global.yaml 把 qualified_min 等放在 global 下；也允许误写在根上。
        JsonNode? Walk(JsonNode? start)
        {
            var n = start;
            foreach (var k in path.Split('.'))
            {
                if (n is not JsonObject o || !o.TryGetPropertyValue(k, out n)) return null;
            }
            return n;
        }
        return Walk(Root) ?? (path.StartsWith("global.") ? Walk(Root?["global"]) : null);
    }

    public static double AsDouble(JsonNode? n, double def = double.NaN)
    {
        if (n is null) return def;
        try { return n.GetValue<double>(); } catch { }
        try { return n.GetValue<long>(); } catch { }
        try { return n.GetValue<int>(); } catch { }
        try { return n.GetValue<decimal>() is decimal m ? (double)m : def; } catch { }
        return double.TryParse(n.ToString(), NumberStyles.Float, Inv, out var v) ? v : def;
    }

    public double D(string path, double def = double.NaN) => AsDouble(Node(path) ?? Alt(path), def);

    public int I(string path, int def = 0)
    {
        var v = D(path, def);
        return double.IsNaN(v) ? def : (int)v;
    }

    public bool B(string path, bool def = false)
    {
        var n = Node(path) ?? Alt(path);
        if (n is null) return def;
        try { return n.GetValue<bool>(); } catch { return def; }
    }

    public string S(string path, string def = "")
    {
        var n = Node(path) ?? Alt(path);
        if (n is null) return def;
        try { return n.GetValue<string>() ?? def; } catch { return n.ToString() ?? def; }
    }

    private JsonNode? Alt(string path)
    {
        // YAML 中部分键在 monitoring / valuation / global 下
        if (path.StartsWith("global.")) return Node(path["global.".Length..]);
        if (!path.Contains('.')) return Node("global." + path);
        return null;
    }

    public string[] Strs(string path)
    {
        var n = Node(path) ?? Alt(path);
        if (n is not JsonArray a) return [];
        return a.Select(x => x?.GetValue<string>() ?? x?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
    }

    public int[] Ints(string path)
    {
        var n = Node(path) ?? Alt(path);
        if (n is not JsonArray a) return [];
        return a.Select(x =>
        {
            if (x is null) return 0;
            try { return x.GetValue<int>(); } catch { return (int)(x.GetValue<double>()); }
        }).ToArray();
    }

    public IReadOnlyList<(double ProfitMin, double MarginMin)> ScaleMatrix()
    {
        // v1.1 取消硬性矩阵；保留读取以便对比旧参数。
        var a = Node("T1.scale_margin_matrix") as JsonArray;
        if (a is null) return [];
        var list = new List<(double, double)>();
        foreach (var x in a)
        {
            if (x is not JsonObject o) continue;
            double G(string k) => o[k] is JsonNode n ? n.GetValue<double>() : 0;
            list.Add((G("profit_min"), G("margin_min")));
        }
        return list.OrderByDescending(t => t.Item1).ToList();
    }

    public double Mos(double score)
    {
        var a = Node("global.mos") as JsonArray ?? Node("mos") as JsonArray;
        if (a is not null)
        {
            foreach (var x in a.OrderByDescending(n => AsDouble(n?["score_min"], 0)))
            {
                if (x is JsonObject o && score >= AsDouble(o["score_min"], 0))
                    return AsDouble(o["discount"], 0.30);
            }
        }
        // v1.1 默认：85→15%，80→20%，75→25%，70→30%；<70 不触发击球（由合格线保证）
        if (score >= 85) return 0.15;
        if (score >= 80) return 0.20;
        if (score >= 75) return 0.25;
        if (score >= 70) return 0.30;
        return 0.30; // <70 不触发击球区；仍用 30% 折扣展示参考价，避免 P_safe=0
    }

    public double Re(string type, string band, double rf)
    {
        var prem = D($"re_premium.{type}.{band}", band switch { "optimistic" => 0.055, "conservative" => 0.09, _ => 0.07 });
        var re = rf + prem;
        if (band == "base") re = Math.Max(re, D("global.min_re", 0.08));
        return re;
    }

    /// <summary>股息率法的目标股息收益率 Y_required，不得与股权要求回报率 Re 混用。</summary>
    public double TargetDivYield(string type, string band, double rf)
    {
        var prem = D($"div_yield_premium.{type}.{band}", 0.025);
        var floor = D($"div_yield_floor.{band}", band switch { "optimistic" => 0.03, "conservative" => 0.05, _ => 0.04 });
        return Math.Max(rf + prem, floor);
    }

    /// <summary>T5 65 分只是观察门槛；合格线一律取 qualified_min（默认 70）。</summary>
    public int QualifiedMin(string type)
        => I("global.qualified_min", 70);

    public int WatchMin(string type)
        => type == "T5" ? I("global.t5_watch_min", 65) : I("global.watch_min", 65);

    public int Concurrency => Math.Max(1, I("global.concurrency", 8));

    public string ValuationConfidence(double? dispersion)
    {
        if (dispersion is null || dispersion < 0) return "";
        var high = D("dispersion_confidence.high", 0.20);
        var mid = D("dispersion_confidence.mid", 0.40);
        var low = D("dispersion_confidence.low", 0.60);
        if (dispersion < high) return "高";
        if (dispersion < mid) return "中";
        if (dispersion < low) return "低";
        return "很低";
    }
}
