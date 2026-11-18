using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.BottomReversal;

/// <summary>报告：前20 CSV、单股 JSON/MD、日报。区分 已核实数据 / 模型计算 / 模型判断 / 未核实信息。</summary>
public static class BrReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly string[] TopCols = ["rank", "code", "name", "industry", "bar_date", "close", "total", "A", "B", "C", "D", "E", "stage", "rating",
        "breakout_date", "days_since_breakout", "platform", "support", "risk_reference", "chase_ok", "flags", "trade_stage", "signal", "op_status", "group", "rr"];
    public static readonly string[] AllCols = ["code", "name", "industry", "bar_date", "close", "total", "A", "B", "C", "D", "E", "stage", "rating", "breakout_date", "days_since_breakout", "flags"];
    private static readonly Dictionary<string, string> ModNames = new() { ["A"] = "底部结构", ["B"] = "放量突破", ["C"] = "突破承接", ["D"] = "均线趋势", ["E"] = "动能" };

    public static string Num(double? v) => v is null ? "" : BrMath.PyRepr(v.Value);

    public static Dictionary<string, string> TopRow(int rank, BrResult r)
    {
        var ms = r.ModuleScores;
        string ObsNum(string k) => r.Observation[k] is JsonValue jv && jv.TryGetValue<double>(out var x) && x != 0 ? Num(x) : "";
        return new Dictionary<string, string>
        {
            ["rank"] = rank.ToString(Inv), ["code"] = r.Code, ["name"] = r.Name, ["industry"] = r.Industry, ["bar_date"] = r.Date,
            ["close"] = Num(BrMath.R(r.Close, 3)), ["total"] = r.Total?.ToString(Inv) ?? "",
            ["A"] = ms["A"].ToString(Inv), ["B"] = ms["B"].ToString(Inv), ["C"] = ms["C"].ToString(Inv), ["D"] = ms["D"].ToString(Inv), ["E"] = ms["E"].ToString(Inv),
            ["stage"] = r.Stage, ["rating"] = r.Rating, ["breakout_date"] = r.BreakoutDate ?? "",
            ["days_since_breakout"] = r.DaysSinceBreakout?.ToString(Inv) ?? "",
            ["platform"] = r.BreakoutIndex is not null ? Num(r.Platform) : "",
            ["support"] = ObsNum("support"), ["risk_reference"] = ObsNum("risk_reference"),
            ["chase_ok"] = r.Observation["chase_ok"]?.GetValue<bool>() == true ? "是" : "否",
            ["flags"] = string.Join("；", r.Flags),
            ["trade_stage"] = r.Trade?.Stage ?? "", ["signal"] = r.Trade?.SignalType ?? "", ["op_status"] = r.Trade?.OpStatus ?? "", ["group"] = r.Trade?.Group ?? "",
            ["rr"] = r.Trade?.Levels.Rr is double rr ? BrMath.PyFmt(rr, 2) : "",
        };
    }

    public static string Esc(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static void WriteCsv(string path, IEnumerable<IReadOnlyDictionary<string, string>> rows, IReadOnlyList<string> cols)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", cols)).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(",", cols.Select(c => Esc(r.TryGetValue(c, out var v) ? v : "")))).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // utf-8-sig，Excel 直接打开不乱码
    }

    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return rows;
        var recs = ParseCsv(File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF'));
        if (recs.Count == 0) return rows;
        var head = recs[0];
        foreach (var rec in recs.Skip(1))
        {
            if (rec.Count == 1 && rec[0].Length == 0) continue;
            var d = new Dictionary<string, string>();
            for (var k = 0; k < head.Count; k++) d[head[k]] = k < rec.Count ? rec[k] : "";
            rows.Add(d);
        }
        return rows;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var res = new List<List<string>>(); var cur = new List<string>(); var f = new StringBuilder(); var q = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (q)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; } else q = false; }
                else f.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { cur.Add(f.ToString()); f.Clear(); }
            else if (ch == '\n') { cur.Add(f.ToString()); f.Clear(); res.Add(cur); cur = []; }
            else if (ch != '\r') f.Append(ch);
        }
        if (f.Length > 0 || cur.Count > 0) { cur.Add(f.ToString()); res.Add(cur); }
        return res;
    }

    public static string Pct(double? v) => v is null ? "-" : BrMath.PyFmtSigned(v.Value * 100, 2) + "%";

    private static double? Dn(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var x) ? x : null;
    private static string Sn(JsonNode? n, string def = "-")
    {
        if (n is null) return def;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            if (v.TryGetValue<int>(out var iv)) return iv.ToString(Inv);
            if (v.TryGetValue<long>(out var lv)) return lv.ToString(Inv);
            if (v.TryGetValue<double>(out var x)) return BrMath.PyRepr(x);
        }
        if (n is JsonArray a) return "[" + string.Join(", ", a.Select(x => Sn(x))) + "]";
        return n.ToJsonString();
    }
    private static string Low(JsonNode? n) => n is JsonObject o ? $"{Sn(o["low"])}({Sn(o["date"])})" : "-";

    public static string StockMd(BrResult r, JsonObject meta)
    {
        var m = r.Modules; var ms = r.ModuleScores;
        JsonNode a = m["A"]!, b = m["B"]!, c = m["C"]!, d = m["D"]!, e = m["E"]!; var o = r.Observation;
        var L = new List<string>
        {
            $"# {r.Code} {r.Name} · S27 增强版底部反转 V1.2 · {r.Date}", "",
            $"**总分 {r.Total} / 100 · {r.Rating} · 阶段：{r.Stage}**  " + (r.Flags.Count > 0 ? "（" + string.Join("；", r.Flags) + "）" : ""), "",
            "> 评分高 ≠ 买入时机好；本报告为辅助研究，不构成买卖指令。", "",
            "## 1. 已核实的客观数据（东方财富日K，前复权）",
            $"- 名称/代码/行业：{r.Name} / {r.Code} / {(r.Industry.Length > 0 ? r.Industry : "未获取")}（东财行业分类，非申万）",
            $"- 最新交易日：{r.Date}　收盘：{Sn(BrMath.R(r.Close, 3))}",
            $"- 近60日最高 {Sn(a["A1"]!["high60"])}（{Sn(a["A1"]!["high60_date"])}）/ 最低 {Sn(a["A1"]!["low60"])}　近60日收盘最大回撤 {Pct(Dn(a["A1"]!["max_drawdown60_close"]))}",
            $"- 数据来源：{Sn(meta["kline_source"], "")}；抓取时间 {Sn(meta["fetched_at"], "")}", "",
            "## 2. 模型计算结果", "| 模块 | 得分 | 满分 |", "|---|---:|---:|",
        };
        foreach (var (k, mx) in new[] { ("A", 20), ("B", 25), ("C", 20), ("D", 20), ("E", 15) })
            L.Add($"| {k} {ModNames[k]} | {ms[k]} | {mx} |");
        L.Add("");
        var a2note = Sn(a["A2"]!["note"], "");
        L.Add($"**底部结构**：前期跌幅 {Pct(Dn(a["A1"]!["drawdown60"]))}（A1={a["A1"]!["score"]}）；"
              + $"波段低点 L1={Low(a["A2"]!["L1"])}、L2={Low(a["A2"]!["L2"])}，低点抬高 {Pct(Dn(a["A2"]!["low_rise"]))}（A2={a["A2"]!["score"]}{(a2note.Length > 0 ? "，" + a2note : "")}）；"
              + $"整理区间 {Sn(a["A3"]!["window"])} 价格 {Sn(a["A3"]!["platform_low"])}~{Sn(a["A3"]!["platform_high"])}，振幅 {Pct(Dn(a["A3"]!["range_pct"]))}（A3={a["A3"]!["score"]}）");
        L.Add("");
        var bo = b["B2"]!["breakout_date"];
        L.Add($"**突破信号**：突破日 {(bo is null ? "无" : Sn(bo))}；突破前平台(前20日最高收盘) {Sn(b["B2"]!["platform_close_high20"], "None")}；"
              + $"当日涨幅 {Pct(Dn(b["B1"]!["change"]))}；成交量 {Sn(b["B1"]!["volume"])} 手；20日均量(不含当日) {Sn(b["B1"]!["avg_vol20_excl_today"])}；"
              + $"放量 {Sn(b["B1"]!["volume_ratio"], "None")} 倍；收盘位置 {Sn(b["B1"]!["close_position"])}；突破幅度 {Pct(Dn(b["B2"]!["breakout_ratio"]))}；上影线占比 {Sn(b["B3"]!["upper_shadow_ratio"], "None")}"
              + $"（B1={b["B1"]!["score"]} B2={b["B2"]!["score"]} B3={b["B3"]!["score"]}）");
        L.Add("");
        var c1s = c["C1"]!["status"] ?? c["C1"]!["note"];
        L.Add($"**突破承接**：突破后 {(r.DaysSinceBreakout?.ToString(Inv) ?? "-")} 个交易日；{Sn(c1s, "")}；"
              + $"跌回平台：{(b["B3"]!["fell_back_below_platform"]?.GetValue<bool>() == true ? "是" : "否")}；回调量能比 {Sn(c["C2"]!["volume_pullback_ratio"])}；"
              + $"回踩次数 {Sn(c["C3"]!["touch_runs"])}，有效跌破：{Sn(c["C3"]!["effective_break"])}（C1={c["C1"]!["score"]} C2={c["C2"]!["score"]} C3={c["C3"]!["score"]}）");
        L.Add("");
        L.Add($"**均线与动能**：MA5 {Sn(d["D2"]!["ma5"])} / MA10 {Sn(d["D2"]!["ma10"])} / MA20 {Sn(d["D2"]!["ma20"])} / MA60 {Sn(d["D1"]!["ma60"])}；MA20 5日斜率 {Pct(Dn(d["D3"]!["ma20_slope5"]))}；MA60 斜率 {Pct(Dn(d["D4"]!["ma60_slope"]))}；"
              + $"RSI14 {Sn(e["E1"]!["rsi14"])}；MACD DIF {Sn(e["E2"]!["dif"])} DEA {Sn(e["E2"]!["dea"])} 柱 {Sn(e["E2"]!["hist"])}；5日/20日均量 {Sn(e["E3"]!["v5_v20"])}（D={ms["D"]} E={ms["E"]}）");
        L.Add("");
        L.Add("## 3. 模型判断（交易观察）");
        L.Add($"- 当前阶段：{r.Stage}；评级：{r.Rating}");
        L.Add($"- 主要支撑：{Sn(o["support"])}；主要压力：{Sn(o["pressure"])}");
        L.Add($"- 观察区间：{Sn(o["observe_zone"])}；风险参考位：{Sn(o["risk_reference"])}（最新K线前复权价=当前实际价口径）");
        L.Add($"- 是否适合追涨：{(o["chase_ok"]?.GetValue<bool>() == true ? "可小心关注" : "否")}；更合理的观察方式：{Sn(o["how_to_watch"])}");
        L.Add($"- 反转失败判断：{Sn(o["failure_condition"])}");
        L.Add("");
        if (r.Trade is not null) L.Add(BrTradeReport.StockMd(r));
        L.Add("## 4. 尚未核实的信息");
        L.Add("- 财务风险（营收/归母/扣非/现金流/负债率/审计意见）与公告风险（减持/质押/预告/立案/诉讼/退市）：**V1 未接入，未核实**。");
        L.Add($"- 市场环境标签：{Sn(meta["market_label"], "未计算")}（独立标签，不计入个股分）");
        if (r.Insufficient.Count > 0) L.Add("- 数据不足项：" + string.Join("；", r.Insufficient));
        L.Add("");
        L.Add("_全部子项原始计算值见同名 .json 文件。_");
        return string.Join("\n", L);
    }

    public static void WriteDetail(string dir, BrResult r, JsonObject meta)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{r.Code}.json"), r.ToJson().ToJsonString(JsonOpts), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, $"{r.Code}.md"), StockMd(r, meta), new UTF8Encoding(false));
    }
}
