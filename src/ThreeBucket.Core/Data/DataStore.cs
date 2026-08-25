using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using ThreeBucket.Core.Models;

namespace ThreeBucket.Core.Data;

/// <summary>
/// 数据访问层：复刻 Python 桌面端 DataManager / WatchlistStore 的能力。
/// 直接读写 data/ 下的 CSV / JSON，UI 无关，可独立测试。
/// </summary>
public class DataStore
{
    private static readonly string[] TradeColumns =
    {
        "日期","方向","桶","代码","名称","申万一级行业",
        "价格","股数","金额","占总资产%","触发规则ID",
        "触发时指标值","阈值","决策理由(一句话)","当时组合状态",
        "当时四桶权重ABCD","情绪自评(1-5)","是否违反纪律",
        "事后30日涨跌%","事后90日涨跌%","复盘结论",
    };

    private static readonly string[] WatchColumns = ["code", "name", "added_from", "added_at", "strategies", "note"];
    private static readonly string[] StrategyColumns = ["id", "name", "type", "indicator", "operator", "threshold", "condition", "action", "priority", "enabled"];

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string DataDir { get; }

    public DataStore(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
    }

    // ── 底层 CSV 读写 ───────────────────────────────────────────────

    private string PathOf(string file) => Path.Combine(DataDir, file);

    public (List<string> Headers, List<Dictionary<string, string>> Rows) ReadCsv(
        string file, IReadOnlyList<string>? requiredColumns = null)
    {
        var path = PathOf(file);
        if (!File.Exists(path))
            return (requiredColumns?.ToList() ?? new List<string>(), new List<Dictionary<string, string>>());

        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            Encoding = new UTF8Encoding(false),
        };
        using var reader = new StreamReader(path, new UTF8Encoding(false));
        using var csv = new CsvReader(reader, cfg);
        if (!csv.Read() || !csv.ReadHeader())
            return (requiredColumns?.ToList() ?? new List<string>(), new List<Dictionary<string, string>>());

        var headers = csv.HeaderRecord?.ToList() ?? new List<string>();
        if (requiredColumns is not null)
            foreach (var c in requiredColumns)
                if (!headers.Contains(c)) headers.Add(c);

        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var h in headers)
                row[h] = csv.GetField(h) ?? "";
            rows.Add(row);
        }
        return (headers, rows);
    }

    public void WriteCsv(string file, List<string> headers, List<Dictionary<string, string>> rows)
    {
        var path = PathOf(file);
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true, Encoding = new UTF8Encoding(false) };
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        using var csv = new CsvWriter(writer, cfg);
        foreach (var h in headers) csv.WriteField(h);
        csv.NextRecord();
        foreach (var row in rows)
        {
            foreach (var h in headers)
                csv.WriteField(row.TryGetValue(h, out var v) ? v : "");
            csv.NextRecord();
        }
    }

    // ── 交易流水 & 持仓 ─────────────────────────────────────────────

    public List<Trade> ReadTrades()
    {
        var (_, rows) = ReadCsv("live_trade_log.csv", TradeColumns);
        var list = new List<Trade>();
        foreach (var r in rows)
        {
            double Num(string k) => double.TryParse(r.GetValueOrDefault(k, ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
            list.Add(new Trade
            {
                Date = r.GetValueOrDefault("日期", ""),
                Direction = r.GetValueOrDefault("方向", ""),
                Bucket = r.GetValueOrDefault("桶", ""),
                Code = r.GetValueOrDefault("代码", ""),
                Name = r.GetValueOrDefault("名称", ""),
                Industry = r.GetValueOrDefault("申万一级行业", ""),
                Price = Num("价格"),
                Shares = Num("股数"),
                Amount = Num("金额"),
                RuleId = r.GetValueOrDefault("触发规则ID", ""),
                Reason = r.GetValueOrDefault("决策理由(一句话)", ""),
            });
        }
        return list;
    }

    private (List<string>, List<Dictionary<string, string>>) ReadTradeRows()
        => ReadCsv("live_trade_log.csv", TradeColumns);

    public void AppendTrade(Trade t)
    {
        var (headers, rows) = ReadTradeRows();
        rows.Add(t.ToRow());
        WriteCsv("live_trade_log.csv", headers, rows);
    }

    /// <summary>替换第 index 行交易（内联编辑/编辑对话框回写）。写文件失败返回 false。</summary>
    public bool UpdateTradeAt(int index, Trade t)
    {
        var (headers, rows) = ReadTradeRows();
        if (index < 0 || index >= rows.Count) return false;
        rows[index] = t.ToRow();
        try { WriteCsv("live_trade_log.csv", headers, rows); }
        catch { return false; }
        return true;
    }

    /// <summary>删除第 index 行交易。写文件失败返回 false。</summary>
    public bool DeleteTradeAt(int index)
    {
        var (headers, rows) = ReadTradeRows();
        if (index < 0 || index >= rows.Count) return false;
        rows.RemoveAt(index);
        try { WriteCsv("live_trade_log.csv", headers, rows); }
        catch { return false; }
        return true;
    }

    /// <summary>加权成本法聚合当前持仓（与 Python load_positions 等价）。</summary>
    public List<Position> LoadPositions()
    {
        var trades = ReadTrades();
        var ordered = trades
            .OrderBy(t => DateTime.TryParse(t.Date, out var d) ? d : DateTime.MinValue)
            .ToList();

        var st = new Dictionary<string, Position>(StringComparer.Ordinal);
        foreach (var t in ordered)
        {
            var code = t.Code.Trim();
            if (code.Length == 0) continue;
            var isBuy = t.Direction.Trim() is "买入" or "buy" or "BUY";
            var shares = t.Shares;
            var amount = t.Amount;

            if (!st.TryGetValue(code, out var rec))
            {
                rec = new Position { Code = code };
                st[code] = rec;
            }
            if (!string.IsNullOrWhiteSpace(t.Name)) rec.Name = t.Name;
            if (!string.IsNullOrWhiteSpace(t.Bucket)) rec.Bucket = t.Bucket;
            if (!string.IsNullOrWhiteSpace(t.Industry)) rec.Industry = t.Industry;

            if (isBuy)
            {
                rec.Shares += shares;
                rec.CostPool += amount;
            }
            else
            {
                var avg = rec.Shares > 0 ? rec.CostPool / rec.Shares : 0;
                var sold = Math.Min(shares, rec.Shares);
                rec.CostPool -= avg * sold;
                rec.Shares -= sold;
                if (rec.Shares <= 1e-9) { rec.Shares = 0; rec.CostPool = 0; }
            }
        }

        var result = new List<Position>();
        foreach (var kv in st)
        {
            var rec = kv.Value;
            if (rec.Shares <= 0) continue;
            rec.AvgCost = rec.Shares > 0 ? rec.CostPool / rec.Shares : 0;
            result.Add(rec);
        }
        return result;
    }

    public double SharesOf(string code)
    {
        var target = code.Split('.')[0].PadLeft(6, '0');
        return LoadPositions().Where(p => p.Code.Split('.')[0].PadLeft(6, '0') == target)
            .Sum(p => p.Shares);
    }

    public Dictionary<string, double> BucketWeights()
    {
        var w = new Dictionary<string, double> { ["A"] = 0, ["B"] = 0, ["C"] = 0, ["D"] = 0 };
        var pos = LoadPositions();
        var total = pos.Sum(p => p.CostPool);
        if (total <= 0) return w;
        foreach (var p in pos)
        {
            var b = p.Bucket.Trim().ToUpper();
            if (w.ContainsKey(b)) w[b] += p.CostPool / total;
        }
        return w;
    }

    public List<Dictionary<string, string>> LoadNav()
        => ReadCsv("portfolio_nav.csv").Rows;

    // ── 候选池（动态列） ────────────────────────────────────────────

    public (List<string> Headers, List<Dictionary<string, string>> Rows) LoadCandidates(string bucket)
        => ReadCsv($"candidates_{bucket}.csv");

    // ── 监控自选 ───────────────────────────────────────────────────

    public List<WatchItem> ListWatchlist()
    {
        var (_, rows) = ReadCsv("watchlist.csv", WatchColumns);
        return rows.Select(r => new WatchItem
        {
            Code = r.GetValueOrDefault("code", ""),
            Name = r.GetValueOrDefault("name", ""),
            AddedFrom = r.GetValueOrDefault("added_from", ""),
            AddedAt = r.GetValueOrDefault("added_at", ""),
            Strategies = r.GetValueOrDefault("strategies", ""),
            Note = r.GetValueOrDefault("note", ""),
        }).ToList();
    }

    public bool InWatchlist(string code)
    {
        var c = NormalizeCode(code);
        return ListWatchlist().Any(w => NormalizeCode(w.Code) == c);
    }

    public (bool ok, string msg) AddWatch(string code, string name = "", string from = "manual", string note = "")
    {
        code = code.Trim();
        // 归一化：兼容 sh600519 / SH600519 / 600519.SH / 600519.XSHG 等常见写法
        var lower = code.ToLowerInvariant();
        if (lower.StartsWith("sh") || lower.StartsWith("sz") || lower.StartsWith("bj"))
            code = code[2..];
        if (code.Contains('.'))
            code = code.Split('.')[0];
        code = code.Trim();
        if (code.Length == 5 && code.All(char.IsDigit)) code = code.PadLeft(6, '0');
        if (!(code.Length == 6 && code.All(char.IsDigit)))
            return (false, "代码必须是 6 位数字（支持 sh600519 / 600519.SH 等写法，5 位数字自动补零）");
        if (InWatchlist(code)) return (false, $"{code} 已在监控池中");

        var (headers, rows) = ReadCsv("watchlist.csv", WatchColumns);
        rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = code, ["name"] = name, ["added_from"] = from,
            ["added_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["strategies"] = "", ["note"] = note,
        });
        WriteCsv("watchlist.csv", headers, rows);
        return (true, $"已添加 {code} {name}".Trim());
    }

    public bool RemoveWatch(string code)
    {
        var c = NormalizeCode(code);
        var (headers, rows) = ReadCsv("watchlist.csv", WatchColumns);
        var filtered = rows.Where(r => NormalizeCode(r.GetValueOrDefault("code", "")) != c).ToList();
        if (filtered.Count == rows.Count) return false;
        WriteCsv("watchlist.csv", headers, filtered);
        return true;
    }

    public void SetStrategies(string code, List<string> ids)
    {
        var c = NormalizeCode(code);
        var (headers, rows) = ReadCsv("watchlist.csv", WatchColumns);
        foreach (var r in rows)
            if (NormalizeCode(r.GetValueOrDefault("code", "")) == c)
                r["strategies"] = string.Join(";", ids);
        WriteCsv("watchlist.csv", headers, rows);
    }

    public void SetNote(string code, string note)
    {
        var c = NormalizeCode(code);
        var (headers, rows) = ReadCsv("watchlist.csv", WatchColumns);
        foreach (var r in rows)
            if (NormalizeCode(r.GetValueOrDefault("code", "")) == c)
                r["note"] = note;
        WriteCsv("watchlist.csv", headers, rows);
    }

    public void SetName(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var c = NormalizeCode(code);
        var (headers, rows) = ReadCsv("watchlist.csv", WatchColumns);
        foreach (var r in rows)
            if (NormalizeCode(r.GetValueOrDefault("code", "")) == c && string.IsNullOrWhiteSpace(r.GetValueOrDefault("name", "")))
                r["name"] = name.Trim();
        WriteCsv("watchlist.csv", headers, rows);
    }

    /// <summary>
    /// 用行情名称补全交易流水中空白的名称列（只填空白不覆盖），返回填充行数。
    /// 对应 Python DataManager.fill_trade_names："录入时只填代码、刷新行情时自动补名"。
    /// names 的 key 为 6 位纯代码。
    /// </summary>
    public int FillTradeNames(Dictionary<string, string> names)
    {
        if (names.Count == 0) return 0;
        var (headers, rows) = ReadTradeRows();
        var filled = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var code = NormalizeCode(rows[i].GetValueOrDefault("代码", ""));
            if (!names.TryGetValue(code, out var nm) || string.IsNullOrWhiteSpace(nm)) continue;
            if (string.IsNullOrWhiteSpace(rows[i].GetValueOrDefault("名称", "")))
            {
                rows[i]["名称"] = nm.Trim();
                filled++;
            }
        }
        if (filled > 0) WriteCsv("live_trade_log.csv", headers, rows);
        return filled;
    }

    // ── 策略定义 ───────────────────────────────────────────────────

    public List<Strategy> ListStrategies()
    {
        var (_, rows) = ReadCsv("strategies.csv", StrategyColumns);
        if (rows.Count == 0) SeedDefaultStrategies();
        return ReadCsv("strategies.csv", StrategyColumns).Rows.Select(ToStrategy).ToList();
    }

    private void SeedDefaultStrategies()
    {
        var defaults = new List<Dictionary<string, string>>
        {
            Strat("S1","跌破MA60减仓","sell","price_vs_ma60","<","0","现价跌破MA60均线，建议减仓1/3观察","P0"),
            Strat("S2","深回撤清仓线","sell","drawdown_from_high_180d","<=","-20","距半年高点回撤超20%，建议清仓止损","P0"),
            Strat("S3","低点涨幅止盈","sell","gain_from_low_180d",">=","50","距半年低点涨幅超50%，建议分批止盈","P1"),
            Strat("S4","持仓浮盈减半","sell","cost_basis_gain",">=","40","持仓浮盈超40%，建议卖出半仓锁定利润","P1"),
            Strat("S5","放量异动关注","buy","volume_ratio_20d",">=","2","量比超2倍出现异动，关注买入机会","P2"),
            // 短期上升趋势三件套（条件树 JSON，Schema 见 StrategyEngine）
            Strat("S6","双均线金叉","buy","","","","MA5上穿MA10金叉且现价站上MA10，短期趋势转强，关注买入时机","P1",Cond(And(
                Leaf("ma",Cross("up"),Ref("ma",("period",10)),("period",5)),
                Leaf("price",">",Ref("ma",("period",10)))))),
            Strat("S7","MACD金叉放量","buy","","","","MACD金叉（DIF上穿DEA）且量比≥1.5倍放量，资金进场信号，关注买入时机","P1",Cond(And(
                Leaf("macd",Cross("up"),Ref("macd",("field","dea")),("field","dif")),
                Leaf("volume_ratio",">=",1.5,("window",20))))),
            Strat("S8","均线多头排列","buy","","","","MA5>MA10>MA20多头排列且量比≥1.5倍放量，短期上升趋势确立，持有或逢低介入","P1",Cond(And(
                Leaf("ma",">",Ref("ma",("period",10)),("period",5)),
                Leaf("ma",">",Ref("ma",("period",20)),("period",10)),
                Leaf("price",">",Ref("ma",("period",5))),
                Leaf("volume_ratio",">=",1.5,("window",20))))),
        };
        WriteCsv("strategies.csv", StrategyColumns.ToList(), defaults);
    }

    // ── 种子策略的条件树 JSON 组装（与 data/strategies.csv 的 S6–S8 保持一致）──

    private static string Cond(object node)
        => System.Text.Json.JsonSerializer.Serialize(
            node, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

    private static Dictionary<string, object> And(params Dictionary<string, object>[] children)
        => new() { ["logic"] = "and", ["children"] = children };

    private static Dictionary<string, object> Ref(string indicator,
        params (string Key, object Val)[] pars)
    {
        var node = new Dictionary<string, object> { ["indicator"] = indicator };
        if (pars.Length > 0)
            node["params"] = pars.ToDictionary(p => p.Key, p => p.Val);
        return node;
    }

    private static Dictionary<string, object> Leaf(string indicator, string op, object value,
        params (string Key, object Val)[] pars)
    {
        var node = Ref(indicator, pars);
        node["operator"] = op;
        node["value"] = value;
        return node;
    }

    private static string Cross(string dir) => $"cross_{dir}";

    private static Dictionary<string, string> Strat(string id, string name, string type, string ind, string op, string th, string act, string pri, string condition = "") => new()
    {
        ["id"]=id,["name"]=name,["type"]=type,["indicator"]=ind,["operator"]=op,
        ["threshold"]=th,["condition"]=condition,["action"]=act,["priority"]=pri,["enabled"]="1",
    };

    private static Strategy ToStrategy(Dictionary<string, string> r) => new()
    {
        Id = r.GetValueOrDefault("id", ""),
        Name = r.GetValueOrDefault("name", ""),
        Type = r.GetValueOrDefault("type", ""),
        Indicator = r.GetValueOrDefault("indicator", ""),
        Operator = r.GetValueOrDefault("operator", ""),
        Threshold = r.GetValueOrDefault("threshold", ""),
        Condition = r.GetValueOrDefault("condition", ""),
        Action = r.GetValueOrDefault("action", ""),
        Priority = r.GetValueOrDefault("priority", ""),
        Enabled = r.GetValueOrDefault("enabled", "1") == "1",
    };

    public string NextStrategyId()
    {
        var nums = ListStrategies().Select(s => { var ok = int.TryParse(s.Id.TrimStart('S', 's'), out var n); return ok ? n : 0; }).ToList();
        return $"S{(nums.Count == 0 ? 0 : nums.Max()) + 1}";
    }

    public void AddStrategy(Strategy s)
    {
        var (headers, rows) = ReadCsv("strategies.csv", StrategyColumns);
        if (string.IsNullOrWhiteSpace(s.Id)) s.Id = NextStrategyId();
        if (string.IsNullOrWhiteSpace(s.Enabled ? "1" : "0")) s.Enabled = true;
        rows.Add(ToRow(s));
        WriteCsv("strategies.csv", headers, rows);
    }

    public void UpdateStrategy(Strategy s)
    {
        var (headers, rows) = ReadCsv("strategies.csv", StrategyColumns);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].GetValueOrDefault("id", "") == s.Id)
            { rows[i] = ToRow(s); break; }
        }
        WriteCsv("strategies.csv", headers, rows);
    }

    public void DeleteStrategy(string id)
    {
        var (headers, rows) = ReadCsv("strategies.csv", StrategyColumns);
        var filtered = rows.Where(r => r.GetValueOrDefault("id", "") != id).ToList();
        WriteCsv("strategies.csv", headers, filtered);
        // 同步清理 watchlist 对该策略的引用
        var (wh, wr) = ReadCsv("watchlist.csv", WatchColumns);
        foreach (var r in wr)
        {
            var ids = r.GetValueOrDefault("strategies", "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x != id).ToList();
            r["strategies"] = string.Join(";", ids);
        }
        WriteCsv("watchlist.csv", wh, wr);
    }

    public void ToggleStrategy(string id)
    {
        var (headers, rows) = ReadCsv("strategies.csv", StrategyColumns);
        foreach (var r in rows)
            if (r.GetValueOrDefault("id", "") == id)
                r["enabled"] = r.GetValueOrDefault("enabled", "1") == "1" ? "0" : "1";
        WriteCsv("strategies.csv", headers, rows);
    }

    private static Dictionary<string, string> ToRow(Strategy s) => new()
    {
        ["id"]=s.Id,["name"]=s.Name,["type"]=s.Type,["indicator"]=s.Indicator,
        ["operator"]=s.Operator,["threshold"]=s.Threshold,["condition"]=s.Condition,
        ["action"]=s.Action,["priority"]=s.Priority,["enabled"]=s.Enabled?"1":"0",
    };

    // ── 提醒去重 & 历史 ─────────────────────────────────────────────

    private string AlertsPath => PathOf("monitor_alerts.json");

    public (Dictionary<string, string> Seen, List<AlertEntry> History) LoadAlerts()
    {
        if (!File.Exists(AlertsPath)) return (new(), new());
        try
        {
            var data = JsonSerializer.Deserialize<AlertStore>(File.ReadAllText(AlertsPath), JsonOpts);
            return (data?.Seen ?? new(), data?.History ?? new());
        }
        catch { return (new(), new()); }
    }

    public List<AlertEntry> LoadHistory() => LoadAlerts().History;

    public void RecordAlerts(List<AlertEntry> entries)
    {
        var (seen, history) = LoadAlerts();
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var fresh = new List<AlertEntry>();
        foreach (var e in entries)
        {
            var key = $"{today}|{e.StrategyId}|{e.Code}";
            if (seen.ContainsKey(key)) continue;
            seen[key] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            fresh.Add(e);
        }
        if (fresh.Count == 0) return;
        history.InsertRange(0, fresh);
        if (history.Count > 200) history.RemoveRange(200, history.Count - 200);
        File.WriteAllText(AlertsPath, JsonSerializer.Serialize(new AlertStore { Seen = seen, History = history }, JsonOpts));
    }

    public void ClearHistory()
        => File.WriteAllText(AlertsPath, JsonSerializer.Serialize(new AlertStore { Seen = new(), History = new() }, JsonOpts));

    // ── 配置 & skill 文件 ──────────────────────────────────────────

    public AppConfig LoadConfig()
    {
        var path = Path.Combine(Path.GetDirectoryName(DataDir.TrimEnd('\\', '/')) ?? "", "app_config.json");
        // 优先 data 同级（项目根）；也兼容桌面端放在 desktop/
        var candidates = new[]
        {
            Path.Combine(Path.GetDirectoryName(DataDir.TrimEnd('\\','/')) ?? "", "app_config.json"),
            Path.Combine(DataDir, "app_config.json"),
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(p), JsonOpts) ?? new(); }
                catch { }
            }
        }
        return new();
    }

    public void SaveConfig(AppConfig cfg)
    {
        var dir = Path.GetDirectoryName(DataDir.TrimEnd('\\', '/')) ?? "";
        File.WriteAllText(Path.Combine(dir, "app_config.json"), JsonSerializer.Serialize(cfg, JsonOpts));
    }

    public string LoadText(string file)
    {
        var path = PathOf(file);
        return File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false)) : "";
    }

    public bool SaveText(string file, string content)
    {
        try { File.WriteAllText(PathOf(file), content, new UTF8Encoding(false)); return true; }
        catch { return false; }
    }

    public string FileMtime(string file)
    {
        var path = PathOf(file);
        return File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss") : "";
    }

    public List<string> ListReports()
        => Directory.Exists(DataDir)
            ? Directory.GetFiles(DataDir, "report_*.md").OrderByDescending(File.GetLastWriteTime).ToList()
            : new();

    // ── 云同步快照（跨平台同步策略/流水/自选/提醒） ─────────────────

    /// <summary>同步种类与本地文件的对应（kind 与 Supabase three_bucket_sync 主键一致）。</summary>
    private static readonly (string Kind, string File, bool IsJson)[] SyncFiles =
    {
        ("strategies", "strategies.csv", false),
        ("trades", "live_trade_log.csv", false),
        ("watchlist", "watchlist.csv", false),
        ("alerts", "monitor_alerts.json", true),
    };

    /// <summary>
    /// 导出全部可同步数据：kind -> payload（{file, headers, rows} 或 {file, json}）。
    /// 列结构与 Python 端 CSV 完全一致，云端仅存行数据不存本地路径。
    /// </summary>
    public Dictionary<string, object> ExportSyncSnapshot()
    {
        var result = new Dictionary<string, object>();
        foreach (var (kind, file, isJson) in SyncFiles)
        {
            if (isJson)
            {
                var path = PathOf(file);
                if (!File.Exists(path)) continue;
                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
                    result[kind] = new Dictionary<string, object> { ["file"] = file, ["json"] = doc };
                }
                catch { /* 本地损坏则跳过，不阻断其它种类 */ }
            }
            else
            {
                var (headers, rows) = ReadCsv(file);
                result[kind] = new Dictionary<string, object> { ["file"] = file, ["headers"] = headers, ["rows"] = rows };
            }
        }
        return result;
    }

    /// <summary>
    /// 导入云端快照（覆盖本地对应文件）。覆盖前原文件自动备份到 data/sync_backup/<时间戳>/。
    /// 返回 (覆盖文件数, 每类结果说明)。
    /// </summary>
    public (int count, List<string> details) ImportSyncSnapshot(Dictionary<string, JsonElement> payloads)
    {
        var backupDir = Path.Combine(DataDir, "sync_backup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var count = 0;
        var details = new List<string>();
        foreach (var (kind, payload) in payloads)
        {
            var map = SyncFiles.FirstOrDefault(f => f.Kind == kind);
            if (map.File is null) { details.Add($"{kind}: 未知种类，跳过"); continue; }
            try
            {
                // JSON 类（monitor_alerts.json）
                if (map.IsJson && payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("json", out var jsonEl))
                {
                    BackupTo(map.File, backupDir);
                    File.WriteAllText(PathOf(map.File), JsonSerializer.Serialize(jsonEl, JsonOpts));
                    details.Add($"{kind} → {map.File}: 已覆盖");
                    count++;
                }
                // CSV 类
                else if (payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("headers", out var hEl) && hEl.ValueKind == JsonValueKind.Array
                    && payload.TryGetProperty("rows", out var rEl) && rEl.ValueKind == JsonValueKind.Array)
                {
                    var headers = hEl.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    var rows = new List<Dictionary<string, string>>();
                    foreach (var row in rEl.EnumerateArray())
                    {
                        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var prop in row.EnumerateObject())
                            dict[prop.Name] = prop.Value.GetString() ?? "";
                        rows.Add(dict);
                    }
                    BackupTo(map.File, backupDir);
                    WriteCsv(map.File, headers, rows);
                    details.Add($"{kind} → {map.File}: {rows.Count} 行");
                    count++;
                }
                else details.Add($"{kind}: 数据格式不符，跳过");
            }
            catch (Exception ex) { details.Add($"{kind}: 导入失败 {ex.Message}"); }
        }
        return (count, details);
    }

    private void BackupTo(string file, string backupDir)
    {
        var path = PathOf(file);
        if (!File.Exists(path)) return;
        Directory.CreateDirectory(backupDir);
        File.Copy(path, Path.Combine(backupDir, file), overwrite: true);
    }

    // ── 工具 ───────────────────────────────────────────────────────

    public static string NormalizeCode(string code)
    {
        var c = code.Split('.')[0].Trim();
        return c.PadLeft(6, '0');
    }
}

/// <summary>monitor_alerts.json 的存储结构。</summary>
public class AlertStore
{
    public Dictionary<string, string> Seen { get; set; } = new();
    public List<AlertEntry> History { get; set; } = new();
}
