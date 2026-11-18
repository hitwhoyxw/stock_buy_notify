namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>§12.2 行业分型：mapping.csv 精确匹配 → 混合行业关键词拆分 → override.csv 覆盖。未命中进 unmapped。</summary>
public static class QvClassify
{
    public static void Apply(QvStock s, QvData data, QvConfig cfg)
    {
        if (data.Overrides.TryGetValue(s.Code, out var ov))
        {
            s.Type = ov.Item1;
            s.TypeSource = "override：" + ov.Item2;
            return;
        }
        var ind = (s.IndustryRaw ?? "").Trim();
        if (ind.Length == 0) { s.Type = ""; s.TypeSource = "unmapped：行业为空"; return; }

        if (data.Mapping.TryGetValue(ind, out var mp))
        {
            var typ = mp.Item1;
            if (typ is "EXCL") { s.Type = "EXCL"; s.TypeSource = "排除：" + mp.Item2; return; }
            if (typ.StartsWith("MIX"))
            {
                var (t, why) = SplitMix(typ, s.Name, ind, s.Annuals, cfg);
                s.Type = t;
                s.TypeSource = why;
                return;
            }
            // 化学制品：默认 T5，满足毛利率稳定 + ROE 后升 T1
            if (ind.Contains("化学制品") && typ == "T5")
            {
                if (TryUpgradeFineChem(s, cfg, out var why))
                {
                    s.Type = "T1"; s.TypeSource = "化学制品自动升 T1：" + why;
                    return;
                }
            }
            s.Type = typ;
            s.TypeSource = "mapping：" + ind;
            return;
        }
        s.Type = "";
        s.TypeSource = "unmapped：" + ind;
    }

    private static (string Type, string Why) SplitMix(string mix, string name, string ind, List<QvAnnual> annuals, QvConfig cfg)
    {
        bool Has(params string[] ks) => ks.Any(k => name.Contains(k, StringComparison.Ordinal));
        switch (mix)
        {
            case "MIX_PORT":
                if (Has("海控", "轮船", "航运", "海运", "海能", "海发", "海特")) return ("T5", "航运港口·名称偏船舶运输 → T5");
                if (Has("港", "码头", "港口")) return ("T4", "航运港口·名称偏码头港口 → T4");
                return ("", "unmapped：航运港口未能从名称判断主营（请在 override.csv 指定）");
            case "MIX_POWER":
                if (Has("水电", "核电", "绿电", "光伏电站", "风电运营", "抽水蓄能")) return ("T4", "电力·名称偏水电/核电/绿电运营 → T4");
                return ("T5", "电力默认 T5（火电/周期波动；水电核电请 override）");
            case "MIX_ROAD":
                if (Has("高速", "公路", "高铁", "铁路")) return ("T4", "铁路公路·名称含高速/铁路 → T4");
                return ("", "unmapped：铁路公路未能从名称判断（请在 override.csv 指定）");
            case "MIX_AIR":
                if (Has("机场", "空港")) return ("T4", "航空机场·机场 → T4");
                return ("T5", "航空机场·非机场按航空运输 → T5");
            default:
                return ("", "unmapped：" + mix);
        }
    }

    private static bool TryUpgradeFineChem(QvStock s, QvConfig cfg, out string why)
    {
        why = "";
        var ys = s.Annuals.TakeLast(5).ToList();
        if (ys.Count < 3) return false;
        var gms = ys.Select(x => x.GrossMargin).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var roeMin = QvMath.Min(ys.Select(x => x.Roe));
        var rangeMax = cfg.D("T5.fine_chem_upgrade_gm_range_max", 0.06);
        var roeFloor = cfg.D("T5.fine_chem_upgrade_roe_min", 0.08);
        if (gms.Count < 3 || roeMin is null) return false;
        var span = gms.Max() - gms.Min();
        if (span <= rangeMax && roeMin >= roeFloor)
        {
            why = $"近{gms.Count}年毛利率极差 {span:P1} ≤ {rangeMax:P0} 且 ROE 最低年 {roeMin:P1} ≥ {roeFloor:P0}";
            return true;
        }
        return false;
    }
}
