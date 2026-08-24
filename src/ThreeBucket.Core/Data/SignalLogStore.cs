using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.Core.Data;

/// <summary>
/// 信号台账（data/live_signal_log.csv）唯一读写口，移植自 Python lib/signal_log.py。
/// 32 列与 trading-system/07_信号台账模板.csv 严格一致，避免与 Python 端漂移。
/// </summary>
public class SignalLogStore
{
    public static readonly string[] Columns =
    {
        "signal_id",
        "触发日期",
        "yaml_version_at_trigger",
        "触发任务",
        "桶",
        "规则ID",
        "标的代码",
        "标的名称",
        "申万一级行业",
        "分桶基准代码",
        "触发时指标值",
        "阈值",
        "当时组合状态",
        "信号方向",
        "建议动作",
        "是否实际执行",
        "执行日期",
        "执行价格",
        "回测预期胜率",
        "回测预期中位收益_60d",
        "回测预期中位收益_120d",
        "回测预期中位收益_250d",
        "事后60日收益%",
        "事后120日收益%",
        "事后250日收益%",
        "事后60日超额沪深300%",
        "事后120日超额沪深300%",
        "事后250日超额沪深300%",
        "事后60日超额分桶基准%",
        "事后120日超额分桶基准%",
        "事后250日超额分桶基准%",
        "信号最终评价",
        "备注",
    };

    /// <summary>需要回补的收益列：(交易日数, [标的收益, 超额沪深300, 超额分桶基准])。</summary>
    public static readonly (int Days, string Ret, string ExHs300, string ExBucket)[] ReturnHorizons =
    {
        (60,  "事后60日收益%",  "事后60日超额沪深300%",  "事后60日超额分桶基准%"),
        (120, "事后120日收益%", "事后120日超额沪深300%", "事后120日超额分桶基准%"),
        (250, "事后250日收益%", "事后250日超额沪深300%", "事后250日超额分桶基准%"),
    };

    private readonly string _path;

    public SignalLogStore(string dataDir) => _path = Path.Combine(dataDir, "live_signal_log.csv");

    private static CsvConfiguration Cfg() => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        BadDataFound = null,
        MissingFieldFound = null,
        Encoding = new UTF8Encoding(false),
    };

    /// <summary>读全表（文件缺失/损坏时返回空表）。</summary>
    public List<Dictionary<string, string>> ReadAll()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            using var reader = new StreamReader(_path, new UTF8Encoding(false));
            using var csv = new CsvReader(reader, Cfg());
            if (!csv.Read() || !csv.ReadHeader()) return new();
            var rows = new List<Dictionary<string, string>>();
            while (csv.Read())
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var h in Columns)
                    row[h] = csv.GetField(h) ?? "";
                rows.Add(row);
            }
            return rows;
        }
        catch { return new(); }
    }

    /// <summary>追加一条信号（signal_id 未给则按 SIG-YYYYMMDD-桶-序号 生成），返回 signal_id。</summary>
    public string AppendSignal(Dictionary<string, string> record)
    {
        var triggerDate = record.GetValueOrDefault("触发日期", "") is { Length: > 0 } d ? d : TradingCalendar.NowCn().ToString("yyyy-MM-dd");
        var bucket = record.GetValueOrDefault("桶", "X").Trim();
        if (bucket.Length == 0) bucket = "X";

        var id = record.GetValueOrDefault("signal_id", "");
        if (id.Length == 0) id = NextSignalId(bucket, triggerDate);
        record["signal_id"] = id;
        record["触发日期"] = triggerDate;

        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in Columns)
            row[c] = record.GetValueOrDefault(c, "");

        var fileEmpty = !File.Exists(_path) || new FileInfo(_path).Length == 0;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var writer = new StreamWriter(_path, append: true, new UTF8Encoding(false));
        using var csv = new CsvWriter(writer, Cfg());
        if (fileEmpty)
        {
            foreach (var c in Columns) csv.WriteField(c);
            csv.NextRecord();
        }
        foreach (var c in Columns) csv.WriteField(row[c]);
        csv.NextRecord();
        return id;
    }

    /// <summary>按 signal_id 更新字段（仅接受 Columns 内的键）。返回是否命中。</summary>
    public bool UpdateSignal(string signalId, Dictionary<string, string> patch)
    {
        var rows = ReadAll();
        var hit = false;
        foreach (var r in rows)
        {
            if (r.GetValueOrDefault("signal_id", "") != signalId) continue;
            hit = true;
            foreach (var (k, v) in patch)
                if (Columns.Contains(k))
                    r[k] = v ?? "";
        }
        if (!hit) return false;
        WriteAll(rows);
        return true;
    }

    /// <summary>全量写回（所有列按 Columns 顺序对齐）。</summary>
    private void WriteAll(List<Dictionary<string, string>> rows)
    {
        using var writer = new StreamWriter(_path, false, new UTF8Encoding(false));
        using var csv = new CsvWriter(writer, Cfg());
        foreach (var c in Columns) csv.WriteField(c);
        csv.NextRecord();
        foreach (var row in rows)
        {
            foreach (var c in Columns) csv.WriteField(row.GetValueOrDefault(c, ""));
            csv.NextRecord();
        }
    }

    /// <summary>生成新 signal_id：SIG-YYYYMMDD-{bucket}-{seq}，seq 按当日当桶计数。</summary>
    private string NextSignalId(string bucket, string triggerDate)
    {
        var prefix = $"SIG-{triggerDate.Replace("-", "")}-{bucket}-";
        var maxSeq = 0;
        foreach (var sid in ReadAll().Select(r => r.GetValueOrDefault("signal_id", "")))
        {
            if (!sid.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(sid.Split('-')[^1], out var seq) && seq > maxSeq) maxSeq = seq;
        }
        return $"{prefix}{maxSeq + 1:00}";
    }
}
