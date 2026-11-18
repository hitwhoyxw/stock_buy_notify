using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.QualityValue;

public sealed class QvIndustryMetric
{
    public string Dimension { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Direction { get; set; } = "higher";
    public string Window { get; set; } = "";
    public double? Value { get; set; }
    public double? Percentile { get; set; }
    public int SampleSize { get; set; }
    public string Definition { get; set; } = "";
}
public sealed class QvIndustryPeer
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public int StartYear { get; set; }
    public int EndYear { get; set; }
    public double? Roe { get; set; }
    public double? OcfMatch { get; set; }
    public double? Payout { get; set; }
    public string Reports { get; set; } = "";
    public string Source { get; set; } = "";
}
/// <summary>独立行业相对准入，不使用行情，不改写原财务指标/质量评分/估值。
/// 三个独立维度：资本回报、现金实现、现金分配；相关指标不凑数。
/// 无足够同行或现金安全证据时只能待核实；非经营安全硬失败永不绕过。</summary>
public static class QvIndustryRelative
{
    public sealed class PeerBook
    {
        public List<QvIndustryPeer> Peers { get; } = new();
        public bool CompleteUniverse { get; init; } = true;
        public int StartYear { get; init; }
        public int EndYear { get; init; }
    }
    private static readonly string[] OperatingNames = ["ROE", "T4 双通道", "10 年盈利"];
    private static readonly string[] RiskFlags = ["red_flag", "cycle_top_risk", "classification_pending"];
    public static PeerBook Build(IReadOnlyList<QvStock> universe, QvConfig cfg, DateTime date, bool completeUniverse = true)
    {
        var end = date.Month >= 5 ? date.Year - 1 : date.Year - 2;
        var years = Math.Max(3, cfg.I("industry_relative.window_years", 5));
        var book = new PeerBook { StartYear = end - years + 1, EndYear = end, CompleteUniverse=completeUniverse };
        // 不按池、得分、流动性、现价、ST过滤同行；无财报者保留在全集，单项无值不进入该项分母。
        foreach (var s in universe.GroupBy(x => x.Code).Select(x => x.First()))
        {
            var group = Group(s, cfg); if (group.Length == 0) continue;
            var a = Window(s, book);
            double? Mean(Func<QvAnnual, double?> pick, bool needsPositiveProfit = false)
            {
                if (a.Count != years || (needsPositiveProfit && a.Any(x => x.Np is not > 0))) return null;
                var values = a.Select(pick).ToList();
                return values.Any(v => v is null || !double.IsFinite(v.Value)) ? null : values.Average(v => v!.Value);
            }
            book.Peers.Add(new QvIndustryPeer { Code=s.Code, Name=s.Name, Group=group, StartYear=book.StartYear, EndYear=end,
                Roe=Mean(x=>x.Roe), OcfMatch=Mean(x=>x.OcfToNp,true), Payout=Mean(x=>x.Payout,true),
                Reports=string.Join(",",a.Select(x=>x.ReportDate)), Source=string.Join(";",a.Select(x=>x.Source).Distinct()) });
        }
        return book;
    }
    private static List<QvAnnual> Window(QvStock s, PeerBook b) => s.Annuals
        .Where(a=>a.Year>=b.StartYear && a.Year<=b.EndYear && a.ReportDate.StartsWith(a.Year.ToString()) && a.ReportDate.EndsWith("-12-31"))
        .GroupBy(a=>a.Year).Select(g=>g.Last()).OrderBy(a=>a.Year).ToList();
    public static string Group(QvStock s, QvConfig cfg)
    {
        var ind=(s.IndustryRaw??"").Trim(); if(ind.Length==0) return "";
        if(cfg.Node("industry_relative.groups") is JsonArray groups)
            foreach(var node in groups.OfType<JsonObject>())
            {
                string[] Arr(string key) => node[key] is JsonArray a ? a.Select(x=>x?.GetValue<string>()??"").Where(x=>x.Length>0).ToArray() : [];
                var inds=Arr("industries");var types=Arr("types");var names=Arr("name_contains_any");var rejects=Arr("exclude_name_contains");
                if(!inds.Contains(ind) || (types.Length>0&&!types.Contains(s.Type))) continue;
                if(names.Length>0&&!names.Any(x=>s.Name.Contains(x,StringComparison.Ordinal))) continue;
                if(rejects.Any(x=>s.Name.Contains(x,StringComparison.Ordinal))) continue;
                return node["name"]?.GetValue<string>()??"";
            }
        if(cfg.Strs("industry_relative.ambiguous_industries").Contains(ind)) return "";
        return cfg.B("industry_relative.fallback_exact_industry",true) ? ind+" / "+s.Type : "";
    }
    // 并列用中位秩；所有人相同=50，唯一最高=100；绝不把所有并列都记100。
    public static double Percentile(IEnumerable<double> values,double value,bool higher=true)
    {
        var xs=values.Where(double.IsFinite).ToList();if(xs.Count<2)return 50;
        var less=xs.Count(v=>v<value);var equal=xs.Count(v=>v==value);
        var p=100.0*(less+(equal-1)/2.0)/(xs.Count-1);
        return higher?p:100-p;
    }
    public static bool PassesThresholds(IReadOnlyList<double> percentiles,QvConfig cfg)
    {
        if(percentiles.Count!=3||percentiles.Any(x=>!double.IsFinite(x)))return false;
        var support=percentiles.Count(x=>x>=cfg.D("industry_relative.dimension_min",80));
        var corroborates=support>=cfg.I("industry_relative.supporting_dimensions",2)
            || (percentiles[0]>=cfg.D("industry_relative.core_min",90)&&percentiles.Skip(1).All(x=>x>=cfg.D("industry_relative.other_min",60)));
        return percentiles.Average()>=cfg.D("industry_relative.composite_min",80)&&corroborates;
    }
    public static void Apply(QvStock s,QvRow row,PeerBook book,QvData data,QvConfig cfg,IReadOnlyList<QvStock> universe)
    {
        row.QualityAdmissionChannel=row.Pool=="qualified"?"absolute":"absolute_unqualified";
        row.AbsoluteAdmissionResult=$"pool={row.Pool}; 原始评分={(row.QualityScore?.ToString("F2")??"未评分")}; {row.ScreenSummary}";
        row.IndustryRelativeGroup=Group(s,cfg);
        row.IndustryRelativeDecision="not_applicable";
        if(!cfg.B("industry_relative.enabled",true)) { row.IndustryRelativeDecision="disabled";return; }
        if(!cfg.Strs("industry_relative.eligible_types").Contains(s.Type)||row.IndustryRelativeGroup.Length==0)return;
        var current=book.Peers.FirstOrDefault(x=>x.Code==s.Code&&x.Group==row.IndustryRelativeGroup);
        if(current is null)return;
        var group=book.Peers.Where(x=>x.Group==current.Group).ToList();var min=Math.Max(3,cfg.I("industry_relative.min_peers",10));
        row.IndustryRelativeMetrics.Clear();
        void Metric(string dim,string name,double? value,Func<QvIndustryPeer,double?> pick,string definition)
        {
            var xs=group.Select(pick).Where(x=>x.HasValue).Select(x=>x!.Value).ToList();
            row.IndustryRelativeMetrics.Add(new QvIndustryMetric{Dimension=dim,Metric=name,Window=$"{book.StartYear}–{book.EndYear}年度",Value=value,SampleSize=xs.Count,
                Percentile=value.HasValue&&xs.Count>=min?Percentile(xs,value.Value):null,Definition=definition});
        }
        Metric("资本回报","五年平均ROE",current.Roe,x=>x.Roe,"年度加权ROE的算术均值；非股价指标");
        Metric("现金实现","五年平均OCF/合并净利润",current.OcfMatch,x=>x.OcfMatch,"东财NCO_NETPROFIT年度同口径；归母每年>0才有可解释的现金利润匹配");
        Metric("股东分配","五年平均现金分红/归母利润",current.Payout,x=>x.Payout,"实际现金DPS×同期股本/归母利润的年度均值；非股息率");
        var complete=row.IndustryRelativeMetrics.All(x=>x.Percentile.HasValue);
        if(complete)row.IndustryRelativeComposite=row.IndustryRelativeMetrics.Average(x=>x.Percentile!.Value);
        var metrics=string.Join("\n",row.IndustryRelativeMetrics.Select(x=>$"{x.Dimension}：{x.Metric}={x.Value?.ToString("F6")??"缺失"}；分位={x.Percentile?.ToString("F2")??"不足样本/缺失"}；有效N={x.SampleSize}；方向={x.Direction}；{x.Definition}"));
        row.IndustryRelativeDetail=$"可比组：{current.Group}；输入全集{group.Count}（不按合格池筛）；窗口{book.StartYear}–{book.EndYear}，同一组仅相同完整年度窗口，缺失不作零；至少{min}个有效同行/项。\n{metrics}\n三维等权综合分位：{row.IndustryRelativeComposite?.ToString("F2")??"不可计算"}；并列中位秩，非原始质量分。同行原始输入见state.IndustryRelativePeers/industry_relative_peers日期文件。";
        if(row.Pool=="qualified") { row.IndustryRelativeDecision="absolute_qualified";return; }
        var allow=cfg.Strs("industry_relative.operating_gate_allowlist").Intersect(OperatingNames).ToHashSet();
        var failures=row.Gates.Where(g=>g.Result=="fail").ToList();
        var hard=failures.Where(g=>!allow.Contains(g.Name)).Select(g=>g.Name+":"+g.Detail).ToList();
        var annual=Window(s,book);
        if(row.RedFlagUnreviewed||row.FlagList.Any(RiskFlags.Contains))hard.Add("未核实重大/财务红旗，不使用相对准入");
        if(s.Annuals.OrderBy(a=>a.Year).LastOrDefault()?.Equity is <=0)hard.Add("净资产非正的财务安全风险");
        if(annual.Count>0&&annual.Count(a=>a.Np is <0)>cfg.I("screen.loss_years_max",2))hard.Add("持续亏损底线");
        if(s.Type=="T5"&&failures.Any(g=>g.Name=="10 年盈利") && s.Annuals.TakeLast(10).Count(a=>a.Np is >0)<cfg.I("T5.profit_years_min",7))hard.Add("T5长期盈利年份不足，不是仅ROE偏低");
        if(annual.Count==book.EndYear-book.StartYear+1)
        {
            if(annual.All(a=>a.Np.HasValue)&&annual.Count(a=>a.Np is >0)<cfg.I("industry_relative.profit_positive_years",4))hard.Add("多年度真实盈利不足");
            if(annual.All(a=>a.Ocf.HasValue)&&annual.Count(a=>a.Ocf is >0)<cfg.I("industry_relative.ocf_positive_years",4))hard.Add("多年度经营现金流底线未满足");
            if(annual.All(a=>a.InterestCoverage.HasValue)&&QvMath.Mean(annual.Select(a=>a.InterestCoverage))<cfg.D("industry_relative.interest_coverage_min",2.5))hard.Add("利息保障安全底线未满足");
            if(annual.All(a=>a.Np.HasValue&&a.Dps.HasValue&&a.TotalShare is >0)&&annual.Sum(a=>a.Dps!.Value*a.TotalShare!.Value)>annual.Sum(a=>a.Np!.Value)*cfg.D("industry_relative.payout_ratio_max",1))hard.Add("累计分红超过累计归母利润，不作相对豁免");
        }
        if(hard.Count>0){Finish("blocked_safety",string.Join("；",hard));return;}
        if(!book.CompleteUniverse){Finish("pending_partial_universe","本轮为代码/类型子集，禁止以选择后的同行样本作相对准入");Pending();return;}
        if(!complete){Finish("pending_sample","同行不足/同窗口指标缺失，不认定行业相对优质");Pending();return;}
        // 港口控参股收益与投资股息口径已证实需桥接；未核验时展示原始分位，不把低OCF分位当经济质量差。
        if(cfg.Strs("industry_relative.cash_semantics_review_groups").Contains(current.Group)
            && failures.Any(g=>g.Name=="T4 双通道") && current.OcfMatch<cfg.D("T4.ocf_to_np_b",0.9))
        {
            var bridgeSafety=new List<string>();var bridgeMissing=new List<string>();
            CheckCashEvidence(s,annual,book,cfg,bridgeSafety,bridgeMissing);
            if(bridgeSafety.Count>0){Finish("blocked_safety",string.Join("；",bridgeSafety));return;}
            if(bridgeMissing.Count>0){Finish("pending_cash_definition","原始OCF分位未含已核验实际外部股息；缺五年合并/归母/少数股东现金桥接，仅进待核实，不认定优质合格，也不据此断言公司差；"+string.Join("；",bridgeMissing));Pending();return;}
        }
        if(!PassesThresholds(row.IndustryRelativeMetrics.Select(x=>x.Percentile!.Value).ToList(),cfg)){Finish("below_percentile","综合分位或独立维度支持不足；不降低绝对门槛、不保证目标股纳入");return;}
        var safety=new List<string>();var missing=new List<string>();
        var needYears=book.EndYear-book.StartYear+1;
        if(annual.Count!=needYears||annual.Any(a=>a.Np is null||a.Ocf is null||a.Capex is null||a.TotalShare is not >0||a.Dps is null))missing.Add("五年利润/OCF/总capex/现金DPS/股本不完整");
        else
        {
            if(annual.Count(a=>a.Np is >0)<cfg.I("industry_relative.profit_positive_years",4))safety.Add("盈利正值年份不足");
            if(annual.Count(a=>a.Ocf is >0)<cfg.I("industry_relative.ocf_positive_years",4))safety.Add("现金经营不稳定");
            var cov=QvMath.Mean(annual.Select(a=>a.InterestCoverage));
            if(cov is null||annual.Any(a=>a.InterestCoverage is null))missing.Add("五年利息保障缺失");else if(cov<cfg.D("industry_relative.interest_coverage_min",2.5))safety.Add("真实利息保障不足");
            if(annual.Sum(a=>a.Np??0)<=0 || annual.Sum(a=>(a.Dps??0)*(a.TotalShare??0))>annual.Sum(a=>a.Np??0)*cfg.D("industry_relative.payout_ratio_max",1))safety.Add("累计现金分红超过累计归母利润");
        }
        { // 审计及现金安全为相对通道强制证据，不能由配置开关绕过。

            var audits=data.Audits.GetValueOrDefault(s.Code)??[];
            if(Enumerable.Range(book.StartYear,needYears).Any(y=>!audits.Any(a=>a.Year==y)))missing.Add("五年审计核验不完整");
            if(audits.Any(a=>a.Year>=book.StartYear&&a.Year<=book.EndYear&&(!a.Opinion.Contains("无保留")||a.Opinion.Contains("非标准")||a.Opinion.Contains("强调事项")||a.Opinion.Contains("持续经营"))))safety.Add("非标准/持续经营审计风险");
        }
        CheckCashEvidence(s,annual,book,cfg,safety,missing);
        if(safety.Count>0){Finish("blocked_safety",string.Join("；",safety));return;}
        if(missing.Count>0){Finish("pending_evidence",string.Join("；",missing));Pending();return;}
        row.IndustryRelativeBypassed=failures.Count>0?string.Join("；",failures.Select(g=>$"{g.Name}:{g.Detail}")):"原始绝对评分准入线（原分数保留）";
        row.QualityAdmissionChannel="industry_relative";
        row.Pool="candidate";row.ScreenSummary="通过行业相对准入（非绝对评分抬分）；"+row.IndustryRelativeBypassed;
        if(row.QualityScore is null){row.SubScores.Clear();QvScore.Run(s,row,data,cfg,universe);}else QvScore.ApplyPool(row,cfg,s.Type);
        if(row.Pool!="qualified"){row.QualityAdmissionChannel="industry_relative_pending";Finish("pending_data_coverage","原始数据质量/覆盖底线仍未满足");return;}
        if(!row.FlagList.Contains("industry_relative_admission"))row.FlagList.Add("industry_relative_admission");
        if(cfg.B("industry_relative.block_strong_until_review",true)&&!(data.Tags.TryGetValue(s.Code,out var tag)&&tag.ReviewedOk))row.FlagList.Add("industry_relative_unreviewed");
        Finish("admitted","独立通道准入，原始评分未抬高；原经营门槛仍保留原始失败记录，明确列作被替代条件。"+row.IndustryRelativeBypassed);
        void Finish(string decision,string why){row.IndustryRelativeDecision=decision;row.IndustryRelativeDetail+="\n相对通道结论："+decision+"；"+why;}
        void Pending(){row.QualityAdmissionChannel="industry_relative_pending";if(row.Pool=="excluded")row.Pool="watch";if(!row.FlagList.Contains("industry_relative_pending"))row.FlagList.Add("industry_relative_pending");row.ManualNeeded+=(row.ManualNeeded.Length>0?"；":"")+"行业相对通道证据/同业样本待核实（非合格）";}
    }
    public static void HoldForKnownRisk(QvRow row)
    {
        // 复核触发时点可能与价格区间有关，故复核结果绝不能回写公司质量池/评分。
        // 准入前已知严重风险已在Apply的安全门槛否决；此处新发现事件只冻结提醒并留痕。
        if(row.QualityAdmissionChannel!="industry_relative"||row.ReviewClass!="C")return;
        if(!row.FlagList.Contains("industry_relative_risk_unverified"))row.FlagList.Add("industry_relative_risk_unverified");
        row.StrongAlertBlocked=true;row.StrongAlertReviewAction="freeze";
        const string reason="相对通道重大风险待核实（不因价格相关复核改质量分/池）";
        if(!row.StrongAlertBlockReason.Contains(reason))row.StrongAlertBlockReason+=(row.StrongAlertBlockReason.Length>0?"；":"")+reason;
        if(!row.IndustryRelativeDetail.Contains(reason))row.IndustryRelativeDetail+="\n"+reason;
    }
    private static void CheckCashEvidence(QvStock s,List<QvAnnual> annual,PeerBook book,QvConfig cfg,List<string> safety,List<string> missing)
    {
        // 证券代码只是证据的主键，不是准入名单；每个年度必须有数值/来源，且仍须全部分位/安全条件。
        var node=cfg.Node("industry_relative.evidence."+s.Code) as JsonArray;
        if(node is null){missing.Add("无可追溯五年实际外部股息/少数股东/利息/受限现金/债务桥接（不编造）");return;}
        var records=node.OfType<JsonObject>().ToList();double available=0,dividends=0;var lastMatched=false;
        foreach(var a in annual)
        {
            var e=records.FirstOrDefault(x=>x["report_date"]?.GetValue<string>()==a.ReportDate);
            double? N(string k)=>e?[k] is JsonValue v&&v.TryGetValue<double>(out var value)&&double.IsFinite(value)?value:null;
            if(e is null||!(e["source"]?.GetValue<string>()??"").StartsWith("https://")||new[]{"external_recurring_dividends_received","minority_dividends_paid","cash_interest_paid","restricted_cash","cash_available","debt_due","total_net_profit","cash_dividends_to_parent_shareholders_paid"}.Any(k=>N(k) is null||N(k)<0))
            {missing.Add(a.Year+"现金桥接数值/官方来源缺失");continue;}
            var total=N("total_net_profit")!.Value;
            if(total<=0||a.Ocf is null||a.OcfToNp is null||a.OcfToNp==0||Math.Abs((a.Ocf.Value/a.OcfToNp.Value)/total-1)>0.01){missing.Add(a.Year+"合并净利润分母未与原始OCF口径勾稽");continue;}
            if(a.Capex is null||a.Dps is null||a.TotalShare is null){missing.Add(a.Year+"现金/股本主表缺失");continue;}
            var flow=(a.Ocf??0)+N("external_recurring_dividends_received")!.Value-a.Capex.Value-N("minority_dividends_paid")!.Value-N("cash_interest_paid")!.Value;
            var own=N("cash_dividends_to_parent_shareholders_paid")!.Value;available+=flow;dividends+=own;
            if(a.Year==book.EndYear){lastMatched=true;if(N("cash_available")!.Value+flow-own<N("debt_due")!.Value)safety.Add("期末可用现金+保守年度现金结余未覆盖到期债务，不能相对豁免偿债约束");}
        }
        if(!lastMatched)missing.Add("末年度现金/到期债务证据未核验");
        if(missing.Count==0&&available<dividends)safety.Add("已核验实际现金扣全部capex/利息/少数分红后不足以覆盖归母股东分红");
    }
}
