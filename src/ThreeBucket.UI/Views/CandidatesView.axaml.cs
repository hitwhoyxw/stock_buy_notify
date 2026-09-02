using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ThreeBucket.Core.Data;
using ThreeBucket.UI.Dialogs;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class CandidatesView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private List<Dictionary<string, string>> _full = new();

    private static readonly HashSet<string> InstCols = new() { "has_insurance", "has_social_security", "has_pension", "has_qfii" };
    private static readonly HashSet<string> Hidden = new() { "inst_detail", "pick_reason" };

    /// <summary>候选池英文列名 → 中文显示名（带方向箭头）的映射。
    /// 仅用于 UI 表头显示；CSV 文件、底层字典 key、数据绑定、搜索/过滤、LLM 输入仍用英文 key。
    /// 箭头约定：↑=越高越好，↓=越低越好，无箭头=标识/口径/中性列。</summary>
    private static readonly Dictionary<string, string> ColLabels = new()
    {
        ["code"] = "代码",
        ["name"] = "名称",
        ["industry"] = "行业",
        ["price"] = "最新价",
        // A桶·红利
        ["dividend_yield_ttm"] = "股息率TTM↑",
        ["dividend_percentile_5y"] = "股息率5年分位↑",
        ["roe_5y_avg"] = "ROE5年均↑",
        ["fcf_coverage"] = "自由现金流覆盖↑",
        ["pb"] = "市净率↓",
        ["pb_percentile"] = "市净率分位↑",
        ["dividend_years"] = "连续分红年数↑",
        ["loss_q_3y"] = "近3年亏损季↓",
        ["ocf_ps_annual"] = "每股经营现金流↑",
        ["quality_score"] = "质量分↑",
        // B桶·成长（2026-09-02 巴菲特式指标集）
        ["total_mv_yi"] = "总市值(亿)",
        ["profit_cagr_3y"] = "净利3年CAGR↑",
        ["revenue_cagr_3y"] = "营收3年CAGR↑",
        ["np_yoy_latest"] = "最新净利同比↑",
        ["roe_ann"] = "年化ROE↑",
        ["np_yoy_by_year"] = "逐年净利同比",
        ["rev_yoy_by_year"] = "逐年营收同比",
        ["gross_margin_by_year"] = "逐年毛利率",
        ["gm_trend"] = "毛利率趋势(末期-基期)↑",
        ["ocf_to_np"] = "OCF/净利↑",
        ["pe_ttm"] = "滚动PE↓",
        ["peg"] = "PEG↓",
        ["roic"] = "ROIC",
        ["debt_ratio"] = "资产负债率↓",
        ["interest_coverage"] = "利息保障倍数↑",
        ["bvps_cagr"] = "BVPS-CAGR↑",
        ["fcf_margin"] = "FCF利润率↑",
        ["capex_intensity"] = "Capex强度↓",
        ["owner_earnings"] = "所有者收益",
        ["gross_margin"] = "毛利率↑",
        ["gross_margin_yoy"] = "毛利率同比↑",
        ["rev_yoy_latest"] = "最新营收同比↑",
        ["ocf_yoy"] = "经营现金流同比↑",
        // 订单积压参考列
        ["drr"] = "合同负债/TTM营收↑",
        ["drgs"] = "合同负债-营收同比↑",
        ["ibr"] = "存货-营收同比(适中)",
        ["arr"] = "应收-营收同比↓",
        ["order_backlog_score"] = "订单积压分↑",
        ["filter_pass"] = "三道过滤",
        // C桶·热点
        ["text_score"] = "文本得分↑",
        ["categories_hit_count"] = "命中类别数↑",
        ["np_yoy"] = "净利同比↑",
        ["revenue_yoy"] = "营收同比↑",
        ["pe_dynamic"] = "动态PE↓",
        ["pe_method"] = "PE口径",
        ["price_index_1y_high"] = "行业价创1年新高",
        ["contract_liability_yoy"] = "合同负债同比↑",
        ["price_above_ma60"] = "价在MA60上",
        // 机构持仓（中性的标识列）
        ["has_insurance"] = "险资持仓",
        ["has_social_security"] = "社保持仓",
        ["has_pension"] = "养老金持仓",
        ["has_qfii"] = "QFII持仓",
        // 隐藏列（定义以便 ColLabels 完整覆盖，BuildColumns 会跳过）
        ["inst_detail"] = "机构明细",
        ["pick_reason"] = "入选理由",
        ["sort_value"] = "排序值↑",
    };

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public CandidatesView() : this(new AppState(), _ => { }) { }

    public CandidatesView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;

        BucketCombo.SelectionChanged += (_, _) => Load();
        RefreshBtn.Click += (_, _) => Load();
        InstFilter.IsCheckedChanged += (_, _) => Apply();
        SearchBox.TextChanged += (_, _) => Apply();
        Grid.SelectionChanged += (_, _) => ShowDetail();
        AddWatch.Click += (_, _) => AddToWatch();
    }

    public void OnShown() => Load();

    public void Load()
    {
        var bucket = "ABC"[BucketCombo.SelectedIndex].ToString();
        var (headers, rows) = _app.Store.LoadCandidates(bucket);
        _full = rows;
        BuildColumns(headers);
        Apply();
        Stats.Text = $"{bucket}桶: {rows.Count} 只  |  更新于 {_app.Store.FileMtime($"candidates_{bucket}.csv")}";
    }

    private void BuildColumns(List<string> headers)
    {
        Grid.Columns.Clear();
        foreach (var h in headers.Where(h => !Hidden.Contains(h)))
        {
            // 列名来自 CSV 表头，无法用强类型属性；索引器路径（[key]/['key']）在 Avalonia 版本间
            // 行为不一致会导致整列空白——改绑行对象本身，ConverterParameter 传列名取值。
            // 表头显示中文含义+方向箭头（↑越高越好/↓越低越好）；ConverterParameter 仍用英文 key 取值。
            var label = ColLabels.TryGetValue(h, out var lbl) ? lbl : h;
            var col = new DataGridTemplateColumn
            {
                Header = label,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                CellTemplate = new FuncDataTemplate<Dictionary<string, string>>((_, _) => new TextBlock
                {
                    [!TextBlock.TextProperty] = new Avalonia.Data.Binding
                    {
                        Converter = new DictValueConverter(),
                        ConverterParameter = h,
                    },
                }),
            };
            Grid.Columns.Add(col);
        }
    }

    private void Apply()
    {
        if (_full.Count == 0) { Grid.SetItemsSafe(null); return; }
        var q = _full;
        if (InstFilter.IsChecked == true)
            q = q.Where(r => InstCols.Any(c => r.GetValueOrDefault(c, "") == "是")).ToList();
        var text = SearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(text))
            q = q.Where(r => (r.GetValueOrDefault("code", "") + r.GetValueOrDefault("name", "")).Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        Grid.SetItemsSafe(new ObservableCollection<Dictionary<string, string>>(q));
    }

    private void ShowDetail()
    {
        if (Grid.SelectedItem is not Dictionary<string, string> row) { Detail.Text = ""; return; }
        var parts = new List<string>();
        if (row.TryGetValue("inst_detail", out var id) && !string.IsNullOrEmpty(id)) parts.Add($"🏛️ 机构持仓: {id}");
        if (row.TryGetValue("pick_reason", out var pr) && !string.IsNullOrEmpty(pr)) parts.Add($"📋 入选理由: {pr}");
        Detail.Text = string.Join("  |  ", parts);
    }

    private void AddToWatch()
    {
        if (Grid.SelectedItem is not Dictionary<string, string> row) { _status("请先选中一行"); return; }
        var code = row.GetValueOrDefault("code", "").Trim();
        var name = row.GetValueOrDefault("name", "").Trim();
        var (ok, msg) = _app.Store.AddWatch(code, name, "candidates");
        _status(msg);
        if (ok) Load();
    }
}
