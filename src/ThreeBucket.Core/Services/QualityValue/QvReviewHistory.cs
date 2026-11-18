namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// 人工复核历史：可新增、修改、撤销、按有效期失效。不得永久屏蔽风险。
/// </summary>
public sealed class QvReviewRecord
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string ReviewTime { get; set; } = "";
    public string TriggerReason { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string Note { get; set; } = "";
    public string SubsequentSignalStatus { get; set; } = "";
    public string NextRereviewTrigger { get; set; } = "";
    public string ValidUntil { get; set; } = "";
    public string Revoked { get; set; } = "";
    public string RevokedAt { get; set; } = "";
    public string Source { get; set; } = "";
    public string PriceAtReview { get; set; } = "";
    public string PSafeAtReview { get; set; } = "";

    public bool IsRevoked => Revoked is "1" or "true" or "Y" or "是";

    public bool IsActive(DateTime asOf)
    {
        if (IsRevoked) return false;
        if (DateTime.TryParse(ValidUntil, out var until) && asOf.Date > until.Date) return false;
        return true;
    }
}

public static class QvReviewHistory
{
    public static readonly string[] Verdicts =
    [
        "temporary_issue", "structural_deterioration", "uncertain_wait",
        "data_error", "valuation_invalid", "confirmed_stable"
    ];

    public static readonly string[] Cols =
    [
        "id", "stock_code", "stock_name", "review_time", "trigger_reason", "fingerprint",
        "verdict", "note", "subsequent_signal_status", "next_rereview_trigger",
        "valid_until", "revoked", "revoked_at", "source", "price_at_review", "psafe_at_review"
    ];

    public static string PathOf(string outDir) => System.IO.Path.Combine(outDir, "manual_review_history.csv");

    public static List<QvReviewRecord> Load(string outDir)
    {
        var path = PathOf(outDir);
        var list = new List<QvReviewRecord>();
        if (!File.Exists(path)) return list;
        foreach (var r in QvData.ReadCsv(path))
        {
            list.Add(new QvReviewRecord
            {
                Id = G(r, "id"),
                Code = G(r, "stock_code").PadLeft(6, '0'),
                Name = G(r, "stock_name"),
                ReviewTime = G(r, "review_time"),
                TriggerReason = G(r, "trigger_reason"),
                Fingerprint = G(r, "fingerprint"),
                Verdict = G(r, "verdict"),
                Note = G(r, "note"),
                SubsequentSignalStatus = G(r, "subsequent_signal_status"),
                NextRereviewTrigger = G(r, "next_rereview_trigger"),
                ValidUntil = G(r, "valid_until"),
                Revoked = G(r, "revoked"),
                RevokedAt = G(r, "revoked_at"),
                Source = G(r, "source"),
                PriceAtReview = G(r, "price_at_review"),
                PSafeAtReview = G(r, "psafe_at_review"),
            });
        }
        return list;
    }

    public static void Save(string outDir, IReadOnlyList<QvReviewRecord> rows)
    {
        var recs = rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["id"] = r.Id,
            ["stock_code"] = r.Code,
            ["stock_name"] = r.Name,
            ["review_time"] = r.ReviewTime,
            ["trigger_reason"] = r.TriggerReason,
            ["fingerprint"] = r.Fingerprint,
            ["verdict"] = r.Verdict,
            ["note"] = r.Note,
            ["subsequent_signal_status"] = r.SubsequentSignalStatus,
            ["next_rereview_trigger"] = r.NextRereviewTrigger,
            ["valid_until"] = r.ValidUntil,
            ["revoked"] = r.Revoked,
            ["revoked_at"] = r.RevokedAt,
            ["source"] = r.Source,
            ["price_at_review"] = r.PriceAtReview,
            ["psafe_at_review"] = r.PSafeAtReview,
        });
        QvData.WriteCsv(PathOf(outDir), recs, Cols);
    }

    public static QvReviewRecord Add(string outDir, QvReviewRecord rec)
    {
        var list = Load(outDir);
        if (string.IsNullOrWhiteSpace(rec.Id))
            rec.Id = "R" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + rec.Code;
        if (string.IsNullOrWhiteSpace(rec.ReviewTime))
            rec.ReviewTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        list.Add(rec);
        Save(outDir, list);
        return rec;
    }

    public static bool Update(string outDir, string id, Action<QvReviewRecord> edit)
    {
        var list = Load(outDir);
        var rec = list.FirstOrDefault(x => x.Id == id);
        if (rec is null) return false;
        edit(rec);
        Save(outDir, list);
        return true;
    }

    public static bool Revoke(string outDir, string id, string? note = null)
    {
        return Update(outDir, id, rec =>
        {
            rec.Revoked = "true";
            rec.RevokedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (!string.IsNullOrWhiteSpace(note))
                rec.Note = string.IsNullOrWhiteSpace(rec.Note) ? note : rec.Note + " | 撤销：" + note;
        });
    }

    public static QvReviewRecord? LatestActive(IEnumerable<QvReviewRecord> all, string code, DateTime asOf)
        => all.Where(r => r.Code == code.PadLeft(6, '0') && r.IsActive(asOf))
              .OrderByDescending(r => r.ReviewTime)
              .FirstOrDefault();

    private static string G(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
}
