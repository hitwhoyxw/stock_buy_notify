namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// v1.2 P0 数据质量等级 A–E、data_confidence_score、signal_confidence（独立于 quality_score），
/// 以及强提醒阻断原因。不因数据缺失自动把公司判为基本面不合格。
/// </summary>
public static class QvDataQuality
{
    public static void Apply(QvStock s, QvRow row, QvData data, QvConfig cfg)
    {
        var notes = new List<string>();
        var deduct = 0.0;
        var proxy = 0;
        var criticalMissing = 0;
        var grades = new List<char>();

        void Field(string name, char grade, int penalty, string why)
        {
            grades.Add(grade);
            deduct += penalty;
            notes.Add($"{name}:{grade} {why}");
            if (grade == 'C') proxy++;
            if (grade is 'D' or 'E') criticalMissing++;
        }

        // 财务主表
        if (s.Annuals.Count >= 5 && s.Annuals.All(a => a.Np is not null && a.Roe is not null))
            Field("财务年报", 'A', 0, $"东财 MAINFIN {s.Annuals.Count} 年，来源完整");
        else if (s.Annuals.Count >= 3)
            Field("财务年报", 'B', 4, $"{s.Annuals.Count} 年年报，部分字段可能有口径差");
        else if (s.Annuals.Count >= 1)
            Field("财务年报", 'D', 12, $"仅 {s.Annuals.Count} 年年报，关键历史不足");
        else
            Field("财务年报", 'E', 25, "无年报");

        // 行业分类
        if (string.IsNullOrWhiteSpace(s.Type) || s.TypeSource.StartsWith("unmapped"))
        {
            Field("行业分类", 'D', 10, "未完成分类，不得自动进入行业准入");
            AddFlag(row, "classification_pending");
        }
        else
            Field("行业分类", s.TypeSource is "override" or "mapping" ? 'A' : 'B', 0, s.TypeSource);

        // 分红
        var ds = s.DividendStatus ?? row.DividendStatus ?? "";
        row.DividendStatus = ds;
        if (ds == "dividend_confirmed")
            Field("分红", 'A', 0, "已获取分红明细");
        else if (ds == "dividend_partial")
            Field("分红", 'C', 6, "部分年份分红缺失，股息率法仅用已确认年份，不把缺失当 0");
        else if (ds == "dividend_estimated")
            Field("分红", 'C', 8, "估算分红，不得给出确定性股息率估值");
        else if (ds == "dividend_missing" || LooksDividendGap(row) && ds.Length == 0)
        {
            if (ds.Length == 0) { ds = "dividend_missing"; row.DividendStatus = ds; s.DividendStatus = ds; }
            Field("分红", 'D', 10, s.DividendMissingReason.Length > 0 ? s.DividendMissingReason : "分红数据缺失，不默认股息为 0");
            AddFlag(row, "dividend_missing");
        }

        // 银行专项
        if (s.Type == "T2")
        {
            var req = data.BankRequirement(s.Code);
            row.CapitalVerificationStatus = req.VerificationStatus;
            if (req.IsPending)
            {
                row.CapitalRequirementUnknown = true;
                AddFlag(row, "capital_requirement_unknown");
                Field("监管资本要求", 'C', 8, "verification_status=pending，不得据此硬排除或强提醒");
                row.CapitalConfidence = "低（未核验）";
            }
            else
            {
                Field("监管资本要求", 'A', 0, "已核验 " + req.VerifiedAt);
                row.CapitalConfidence = "高";
            }
            if (row.Cet1Latest is null)
                Field("CET1", 'D', 8, "核心一级资本充足率缺失");
            else
                Field("CET1", 'A', 0, $"CET1 {row.Cet1Latest:P2}");
            var last = s.Annuals.LastOrDefault();
            if (last?.Npl is null) Field("不良率", 'D', 6, "不良率缺失，不得推断资产质量良好");
            if (data.Tags.TryGetValue(s.Code, out var tag))
            {
                if (tag.SpecialMention is null) Field("关注类贷款", 'D', 4, "缺失，不得推断资产质量良好");
                else Field("关注类贷款", 'B', 0, "manual_tags");
                if (tag.ReLoanPct is null) Field("地产贷款占比", 'D', 4, "缺失，区域/行业集中风险不得给满分");
                if (tag.PlatformLoanPct is null) Field("平台贷款占比", 'D', 4, "缺失，相关风险标记未验证");
            }
            else
            {
                Field("关注类贷款", 'D', 4, "无人工补录");
                Field("地产贷款占比", 'D', 4, "无人工补录");
                Field("平台贷款占比", 'D', 4, "无人工补录");
            }
            if (row.FlagList.Contains("proxy_data"))
                Field("RWA/内生资本", 'C', 5, "RWA 缺失，用总资产增速代理");
            if (s.PbMedian3y is null && s.PbMedian5y is null)
                Field("历史PB", 'C', 4, s.PbHistorySource.Length > 0 ? s.PbHistorySource : "3年/5年 PB 不可得");
            else
                Field("历史PB", 'B', 0, s.PbHistorySource);
        }

        if (row.FlagList.Contains("proxy_data") && s.Type != "T2")
            Field("代理数据", 'C', 5, "存在代理口径");

        // 行情
        if (s.Quote.Price is > 0)
            Field("行情", 'A', 0, $"{s.Quote.Source} {s.Quote.AsOf}");
        else
            Field("行情", 'D', 10, "缺现价");

        var score = Math.Clamp(100 - deduct, 0, 100);
        row.DataConfidenceScore = score;
        var worst = grades.Count == 0 ? 'B' : grades.Max(); // E>D>C>B>A
        row.DataQualityGrade = worst.ToString();
        row.DataQualityDetail = string.Join("；", notes);
        row.DataConfidenceLabel = score >= 90 ? "高置信度" : score >= 75 ? "较高置信度" : score >= 60 ? "中等置信度" : "低置信度";

        if (worst is 'D' or 'E' || score < 60 || criticalMissing >= 3)
            AddFlag(row, "data_quality_warning");
        if (worst == 'E')
            AddFlag(row, "data_quality_e");

        // signal_confidence 独立于 quality_score
        var val = row.ValuationConfidence switch
        {
            "高" => 90.0,
            "中" => 75,
            "低" => 55,
            "很低" => 35,
            _ => row.PFair is > 0 ? 70 : 40,
        };
        if (row.FlagList.Contains("valuation_outlier")) val = Math.Min(val, 40);
        if (row.FlagList.Contains("valuation_uncertain")) val = Math.Min(val, 50);
        var sig = Math.Min(score, val);
        row.SignalConfidence = sig;

        // 强提醒阻断（不等于不合格）
        var blocks = new List<string>();
        if (row.Pool != "qualified") blocks.Add("基本面未进入合格池");
        if (row.FlagList.Contains("valuation_outlier")) blocks.Add("估值超过预警线且未完成复核（valuation_outlier）");
        if (row.FlagList.Contains("valuation_uncertain")) blocks.Add("估值不确定或模型分歧过大（valuation_uncertain）");
        if (row.RedFlagUnreviewed) blocks.Add("存在未复核红旗");
        if (row.FlagList.Contains("data_insufficient")) blocks.Add("数据不足");
        if (row.FlagList.Contains("cycle_high") && cfg.B("cycle_filter.block_strike_when_high", true))
            blocks.Add("周期高位，禁止强提醒（P1 完整识别未上线，沿用已有 cycle_high）");
        if (row.FlagList.Contains("cycle_top_risk")) blocks.Add("周期顶部风险");
        if (row.FlagList.Contains("classification_pending")) blocks.Add("行业分类未完成");
        if (row.CapitalRequirementUnknown && s.Type == "T2")
            blocks.Add("银行监管资本要求尚未核验（capital_requirement_unknown），资本缓冲不得作为买入判断");
        if (worst is 'D' or 'E') blocks.Add($"关键数据质量为 {worst} 级");
        if (score < 60) blocks.Add($"数据置信度 {score:F0} < 60");
        if (row.FlagList.Contains("dividend_missing") && DividendDependent(s, row, cfg))
            blocks.Add("重要分红数据缺失且估值或筛选依赖股息率");
        if (row.PriceMonitoringEligible == false) blocks.Add("监控资格：" + row.MonitoringReason);
        if (row.FlagList.Contains("industry_relative_unreviewed")) blocks.Add("行业相对通道尚待人工确认，不自动强提醒");
        row.StrongAlertBlockReason = string.Join("；", blocks);
        row.StrongAlertBlocked = blocks.Count > 0;
    }

    private static bool LooksDividendGap(QvRow row)
        => (row.DataGaps ?? "").Contains("分红");

    private static bool DividendDependent(QvStock s, QvRow row, QvConfig cfg)
    {
        if (s.Type == "T4") return true;
        if (row.Methods.Any(m => m.Name.Contains("股息") && m.Role == "pricing")) return true;
        return false;
    }

    private static void AddFlag(QvRow r, string f) { if (!r.FlagList.Contains(f)) r.FlagList.Add(f); }
}
