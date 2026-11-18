using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Data.Converters;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using ThreeBucket.Core.Services.QualityValue;
using ThreeBucket.UI.Services;
using ThreeBucket.Core.Services;

namespace ThreeBucket.UI.Views;

public partial class QualityValueView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;
    private bool _busy;
    private QvRow? _detailRow;
    private List<QvRow> _all = new();
    private List<string> _alerts = new();
    private string _asOf = "";
    private string _mode = "";

    private string OutDir => Path.Combine(_app.DataDir, "quality_value");

    public QualityValueView() : this(new AppState(), _ => { }) { }

    public QualityValueView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app;
        _status = status;
        foreach (var g in new[] { StrikeGrid, ApproachGrid, QualGrid, WatchGrid })
        {
            AddPoolColumns(g);
            WireRowNavigation(g);
        }
        AddUnmappedColumns(UnmappedGrid);
        AddReviewColumns(ReviewGrid);
        AddHistoryColumns(HistoryGrid);
        WireRowNavigation(ReviewGrid);
        BackBtn.Click += (_, _) => BackToList();
        HistoryGrid.SelectionChanged += (_, _) =>
        {
            if (HistoryGrid.SelectedItem is QvReviewRecord rec)
                HistoryDetailBox.Text = $"记录 {rec.Id}\n{rec.Code} {rec.Name}\n时间 {rec.ReviewTime}\n结论 {rec.Verdict}\n触发 {rec.TriggerReason}\n备注 {rec.Note}\n有效至 {rec.ValidUntil}\n撤销 {rec.Revoked} {rec.RevokedAt}\n下次触发 {rec.NextRereviewTrigger}\n指纹 {rec.Fingerprint}";
        };
        VerdictBox.DisplayMemberBinding = new Binding(".") { Converter = DisplayConverter };
        foreach (var v in QvReviewHistory.Verdicts)
            VerdictBox.Items.Add(v);
        if (VerdictBox.Items.Count > 0) VerdictBox.SelectedIndex = 0;
        FullBtn.Click += async (_, _) => {if(await PreviewConfigAsync(true))await RunAsync("recalc");};
        ConfigPreviewBtn.Click += async (_, _) => await PreviewConfigAsync(false);
        FinancialBtn.Click += async (_, _) => {if(await ConfirmFinancialRefreshAsync())await RunAsync("refresh-financials");};
        DailyBtn.Click += async (_, _) => await RunAsync("daily");
        NovBtn.Click += async (_, _) => await RunAsync("nov");
        RefreshBtn.Click += (_, _) => Load();
        OpenDirBtn.Click += (_, _) => OpenDir();
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        VerdictAddBtn.Click += (_, _) => AddVerdict();
        VerdictEditBtn.Click += (_, _) => EditVerdict();
        VerdictRevokeBtn.Click += (_, _) => RevokeVerdict();
        VerdictRefreshBtn.Click += (_, _) => LoadHistory();
    }


    private async Task<bool> PreviewConfigAsync(bool confirm)
    {
        string text;bool valid=true;
        try{text=ConfigGovernance.Preview(QvConfig.Load(_app.DataDir),Path.Combine(OutDir,"config_run.json"));}
        catch(Exception ex){text="配置校验失败；未开始计算，未修改输入。\n"+ex.Message;valid=false;}
        if(TopLevel.GetTopLevel(this) is not Window owner){_status(text);return false;}
        var panel=new StackPanel{Margin=new Avalonia.Thickness(12),Spacing=8};
        panel.Children.Add(new TextBox{Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=Avalonia.Media.TextWrapping.Wrap,MinHeight=360,MaxHeight=600});
        var buttons=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=8};
        var dialog=new Window{Title="配置校验 / 变更预览（只读）",Width=920,Height=650,Content=new ScrollViewer{Content=panel},WindowStartupLocation=WindowStartupLocation.CenterOwner};
        if(confirm&&valid){var run=new Button{Content="确认：仅本地重算"};run.Click+=(_,_)=>dialog.Close(true);buttons.Children.Add(run);}
        var close=new Button{Content=confirm?"取消":"关闭"};close.Click+=(_,_)=>dialog.Close(false);buttons.Children.Add(close);panel.Children.Add(buttons);
        return await dialog.ShowDialog<bool>(owner);
    }

    public void OnShown() { if (!DetailPage.IsVisible) Load(); }

    private void Load()
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            var o = QvScan.LoadLatest(OutDir);
            _all = o?.Rows ?? new List<QvRow>();
            _alerts = o?.Alerts ?? new List<string>();
            _asOf = o?.AsOf ?? "";
            _mode = o?.Mode ?? "";
            if (o is null)
            {
                SummaryText.Text = "尚未扫描：点「本地重算（不联网）」复用已有输入；缺财报请显式更新。参数见 config/quality_value/*.yaml。";
                OverviewText.Text = "";
                Stats.Text = "";
            }
            else
            {
                SummaryText.Text = $"{_asOf} · {_mode} · 合格 {o.Qualified} / 观察 {o.Watch} · 击球 {o.Strike} / 关注 {o.Approach}（点击行查看详情）";
                var core = o.CoreQuality;
                var byQ = string.Join("  ", new[] { "T1", "T2", "T3", "T4", "T5" }.Select(tp => tp + "合格 " + _all.Count(r => r.Type == tp && r.Pool == "qualified")));
                var holdOpt = _all.Count(r => r.Zone == "optimistic"); // 持仓过滤在提醒层；总览仍列出乐观区只数
                var down = _all.Count(r => (r.Flags ?? "").Contains("downgrade_warning"));
                OverviewText.Text = $"市场总览：核心优质 {core}  · {byQ}  · 当前击球 {_all.Count(r => r.Zone=="strike")}  · 关注 {_all.Count(r => r.Zone=="approach")}  · 乐观区 {holdOpt}  · 近期降级预警 {down}";
                Stats.Text = "分型 " + string.Join("  ", (o.ByType ?? new Dictionary<string, int>()).Select(kv => kv.Key + " " + kv.Value))
                             + ((o.Gaps?.Count ?? 0) > 0 ? "    缺口：" + string.Join("；", o.Gaps!.Take(8)) : "");
            }
            AlertList.ItemsSource = _alerts.Count > 0 ? _alerts : new List<string> { "（无提醒，或尚未跑过带提醒的扫描）" };
            CaseBox.Text = string.Join("\n\n────\n\n", QvScan.CaseCodes.Select(c =>
            {
                var r = _all.FirstOrDefault(x => x.Code == c);
                return r is null ? c + "：本轮未覆盖（请先全量重扫或 --qv cases）" : r.Chain;
            }));
            var unmappedPath = Path.Combine(OutDir, "unmapped.csv");
            if (File.Exists(unmappedPath))
            {
                var rows = QvData.ReadCsv(unmappedPath).Select(d => new UnmappedRow
                {
                    Industry = d.GetValueOrDefault("industry", ""),
                    SampleCodes = d.GetValueOrDefault("sample_codes", ""),
                    SampleNames = d.GetValueOrDefault("sample_names", ""),
                    Count = d.GetValueOrDefault("count", ""),
                }).ToList();
                UnmappedGrid.ItemsSource = rows;
            }
            ApplyFilter();
            LoadHistory();
        }
        catch (Exception ex)
        {
            _status("加载优质股结果失败：" + ex.Message);
        }
    }

    private void ApplyFilter()
    {
        var q = (SearchBox.Text ?? "").Trim();
        bool Match(QvRow r) => q.Length == 0
            || r.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (r.Industry ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
            || (r.Flags ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
        StrikeGrid.ItemsSource = _all.Where(r => r.Zone == "strike" && Match(r)).ToList();
        ApproachGrid.ItemsSource = _all.Where(r => r.Zone == "approach" && Match(r)).ToList();
        QualGrid.ItemsSource = _all.Where(r => r.Pool == "qualified" && Match(r)).ToList();
        WatchGrid.ItemsSource = _all.Where(r => r.Pool == "watch" && Match(r)).ToList();
        ReviewGrid.ItemsSource = _all.Where(r => r.Zone == "strike" && Match(r))
            .OrderByDescending(r => r.ReviewPriority)
            .ThenBy(r => r.Code)
            .ToList();
    }

    private async Task RunAsync(string mode)
    {
        if (_busy) return;
        _busy = true;
        FullBtn.IsEnabled = FinancialBtn.IsEnabled = DailyBtn.IsEnabled = NovBtn.IsEnabled = false;
        var label = mode switch { "recalc" => "本地重算（不联网）", "refresh-financials" => "显式更新到期财报", "daily" => "收盘更新", "nov" => "11月轻量", _ => mode };
        _status($"T12 优质股击球区「{label}」运行中…");
        try
        {
            var r = await Task.Run(async () =>
                await QvScan.RunAsync(_app.DataDir, mode,
                    msg => Dispatcher.UIThread.Post(() => _status(msg)),
                    _app.EastMoney, _app.Quotes, _app.Config, _app.Store).ConfigureAwait(false)
            ).ConfigureAwait(true);
            _status($"T12 完成：{r.Mode} · 合格 {r.Qualified} · 击球 {r.Strike} · 关注 {r.Approach} · 提醒 {r.Alerts.Count}");
        }
        catch (Exception ex) { _status("T12 失败：" + ex.Message); }
        finally
        {
            _busy = false;
            FullBtn.IsEnabled = FinancialBtn.IsEnabled = DailyBtn.IsEnabled = NovBtn.IsEnabled = true;
            Load();
        }
    }

    private async Task<bool> ConfirmFinancialRefreshAsync()
    {
        if(TopLevel.GetTopLevel(this) is not Window owner)return false;
        var window=new Window{Title="确认显式更新财报",Width=560,Height=230,WindowStartupLocation=WindowStartupLocation.CenterOwner,CanResize=false};
        var ok=new Button{Content="确认：只更新缺失/到期资源"};var cancel=new Button{Content="取消"};
        ok.Click+=(_,_)=>window.Close(true);cancel.Click+=(_,_)=>window.Close(false);
        window.Content=new StackPanel{Margin=new Avalonia.Thickness(18),Spacing=14,Children={new TextBlock{Text="此操作需要联网。统一财报缓存默认半年更新，可在config/financial_cache.yaml改为季度。未到期且已有的数据不会重拉；缺失公司可能较多，接口可能整批返回历史报告。仅修改规则请取消并使用本地重算。不会发送通知。",TextWrapping=Avalonia.Media.TextWrapping.Wrap},ok,cancel}};
        return await window.ShowDialog<bool>(owner);
    }

    private void OpenDir()
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutDir}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", OutDir) { UseShellExecute = true });
        }
        catch (Exception ex) { _status("打开目录失败：" + ex.Message); }
    }

    // Presentation-only navigation: keep the existing list tree alive, including tab/filter/selection/scroll.
    private void WireRowNavigation(DataGrid grid)
    {
        grid.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (e.Source is Avalonia.Visual visual)
            {
                var row = visual as DataGridRow ?? visual.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
                if (row?.DataContext is QvRow stock) OpenDetail(stock);
            }
        };
        grid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && grid.SelectedItem is QvRow stock)
            { OpenDetail(stock); e.Handled = true; }
        };
    }

    private void OpenDetail(QvRow r)
    {
        _detailRow = r;
        DetailTitle.Text = $"{r.Name}  {r.Code}";
        DetailSummary.Text = $"{r.Industry} · 公司质量：{DisplayToken(r.Pool)}（{AdmissionLabel(r.QualityAdmissionChannel)}） · 价格区间：{DisplayToken(r.Zone)} · 原始质量分 {r.QualityScore:F1} · 现价 {r.Price:F2} · 复核 {DisplayToken(r.ReviewClass)} / {DisplayToken(r.ReviewPriorityLevel)}";
        ValuationBox.Text = $"安全边际价 {r.PSafe:F2}　合理价 {r.PFair:F2}　乐观价 {r.POpt:F2}\n可信度：{Missing(r.ValuationConfidence)}\n{Missing(r.ValuationDetail)}\n\n" +
            string.Join("\n\n", (r.Methods ?? new List<QvValuationMethod>()).Select(m => $"{m.Name} / {m.Role}\n安全 {m.Safe:F2} / 合理 {m.Fair:F2} / 乐观 {m.Opt:F2}\n{m.Detail}")) +
            "\n\n" + Missing(r.BankValuationBreakdown);
        ReviewDetailBox.Text = $"业绩：{DisplayToken(r.EarningsSeverity)}\n{Missing(r.EarningsSummary)}\n风险：{Missing(r.Flags)}\n触发：{Missing(r.ReviewTriggers)}\n强提醒：{DisplayToken(r.StrongAlertReviewAction)}\n建议：{Missing(r.ReviewSuggestedAction)}\n\n{Missing(r.ReviewCard)}";
        DetailBox.Text = Missing(r.Chain);
        SourceBox.Text = $"财务契约：{FinancialContract.Version}。新共享输入保留原值；金额按字段标注，ROE为百分数、OCF/合并净利润为倍数；合并与归母口径分开；未知保持未核验，单季/TTM仅可比完整输入派生。旧快照不冒称已补齐元数据。\n\n价格监控资格：{r.PriceMonitoringEligible?.ToString() ?? "原快照待刷新"}；{r.MonitoringReason}\n\n行业相对准入：{r.IndustryRelativeDecision}\n{r.IndustryRelativeDetail}\n被替代的原门槛：{r.IndustryRelativeBypassed}\n\n数据缺口：{Missing(r.DataGaps)}\n人工补录：{Missing(r.ManualNeeded)}\n质量说明：{Missing(r.DataQualityDetail)}\n覆盖：{Missing(r.CoverageDetail)}\n排雷：{Missing(r.ScreenSummary)}\n\n完整原始字段（未生成新结论）：\n" +
            JsonSerializer.Serialize(r, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        VerdictNoteBox.Text = "";
        HistoryDetailBox.Text = "选中历史记录查看完整触发、备注与有效期。";
        LoadHistory();
        ListPage.IsVisible = false;
        DetailPage.IsVisible = true;
        DetailScroll.Offset = new Avalonia.Vector(0, 0);
        BackBtn.Focus();
    }

    private void BackToList()
    {
        DetailPage.IsVisible = false;
        ListPage.IsVisible = true;
        _detailRow = null;
        // Deliberately no Load()/ApplyFilter(): retain tab, query, sorting, width and scroll.
    }

    private static string AdmissionLabel(string? channel) => channel switch
    {
        "absolute" => "绝对质量标准",
        "absolute_unqualified" => "未通过绝对标准",
        "industry_relative" => "行业相对标准（原始分数保留）",
        "industry_relative_pending" => "行业相对待核实，非合格",
        _ => "原快照，通道未记录",
    };

    private static string Missing(string? value) => string.IsNullOrWhiteSpace(value) ? "未提供 / 缺失" : value;
    private static readonly FuncValueConverter<object?, string> DisplayConverter = new(v => DisplayToken(v?.ToString()));
    public static string DisplayToken(string? value) => value switch
    {
        "strike" => "击球区", "approach" => "关注区", "fair" => "合理区", "optimistic" => "乐观区",
        "qualified" => "合格", "watch" => "观察", "excluded" => "排除",
        "A" => "A 稳定", "B" => "B 边际恶化", "C" => "C 重大不确定", "D" => "D 数据问题",
        "high" => "高度预警", "moderate" => "中度预警", "mild" => "轻度预警", "none" => "未触发",
        "hold" => "暂缓", "freeze" => "冻结", "auto_candidate" => "候选（待原规则）",
        "P0" => "P0 紧急", "P1" => "P1 优先", "P2" => "P2 常规", "P3" => "P3 低优先",
        "temporary_issue" => "暂时性问题", "structural_deterioration" => "结构性恶化",
        "uncertain_wait" => "不确定，等待核实", "data_error" => "数据错误",
        "valuation_invalid" => "估值失效", "confirmed_stable" => "确认稳定",
        "" or null => "—", _ => value
    };

    private void LoadHistory()
    {
        try
        {
            HistoryGrid.ItemsSource = QvReviewHistory.Load(OutDir)
                .Where(x => _detailRow is not null && x.Code == _detailRow.Code)
                .OrderByDescending(x => x.ReviewTime)
                .ToList();
        }
        catch (Exception ex) { _status("加载复核历史失败：" + ex.Message); }
    }

    private QvRow? SelectedReviewRow() => _detailRow ?? ReviewGrid.SelectedItem as QvRow;
    private QvReviewRecord? SelectedHistory() => HistoryGrid.SelectedItem as QvReviewRecord;

    private void AddVerdict()
    {
        var r = SelectedReviewRow();
        if (r is null) { _status("请先在复核队列中选中一只股票"); return; }
        var verdict = (VerdictBox.SelectedItem as string) ?? "";
        if (string.IsNullOrWhiteSpace(verdict)) { _status("请选择结论"); return; }
        var days = 90;
        int.TryParse((VerdictDaysBox.Text ?? "").Trim(), out days);
        if (days <= 0) days = 90;
        var rec = QvReviewHistory.Add(OutDir, new QvReviewRecord
        {
            Code = r.Code,
            Name = r.Name,
            Verdict = verdict,
            Note = VerdictNoteBox.Text ?? "",
            TriggerReason = r.ReviewTriggers,
            Fingerprint = r.ReviewFingerprint,
            SubsequentSignalStatus = "pending",
            NextRereviewTrigger = "新财报、新预告/修正、新重大公告、风险指标明显恶化、估值模型实质变化、或价格进入更深安全边际",
            ValidUntil = DateTime.Now.Date.AddDays(days).ToString("yyyy-MM-dd"),
            Source = "ui",
            PriceAtReview = (r.Price ?? 0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            PSafeAtReview = (r.PSafe ?? 0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
        });
        _status($"已记录复核结论 {rec.Id} {rec.Code} {rec.Verdict}，有效至 {rec.ValidUntil}");
        LoadHistory();
    }

    private void EditVerdict()
    {
        var rec = SelectedHistory();
        if (rec is null) { _status("请先在历史中选中一条记录"); return; }
        var verdict = (VerdictBox.SelectedItem as string) ?? rec.Verdict;
        var ok = QvReviewHistory.Update(OutDir, rec.Id, x =>
        {
            x.Verdict = verdict;
            var note = VerdictNoteBox.Text ?? "";
            if (!string.IsNullOrWhiteSpace(note)) x.Note = note;
            x.Source = string.IsNullOrWhiteSpace(x.Source) ? "ui-edit" : x.Source + ",ui-edit";
        });
        _status(ok ? "已修改 " + rec.Id : "修改失败");
        LoadHistory();
    }

    private void RevokeVerdict()
    {
        var rec = SelectedHistory();
        if (rec is null) { _status("请先在历史中选中一条记录"); return; }
        var ok = QvReviewHistory.Revoke(OutDir, rec.Id, string.IsNullOrWhiteSpace(VerdictNoteBox.Text) ? "ui-revoke" : VerdictNoteBox.Text);
        _status(ok ? "已撤销 " + rec.Id + "（有效期结束前仍可再次记录）" : "撤销失败");
        LoadHistory();
    }

    private static void AddReviewColumns(DataGrid g)
    {
        void Col(string header, string path, double w, string? fmt = null)
        {
            g.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = fmt is null ? new Binding(path) { Converter = DisplayConverter } : new Binding(path) { StringFormat = fmt },
                Width = new DataGridLength(w),
            });
        }
        Col("代码", "Code", 82);
        Col("名称", "Name", 110);
        Col("行业", "Industry", 120);
        Col("分流", "ReviewClass", 110);
        Col("优先级", "ReviewPriorityLevel", 100);
        Col("复核分", "ReviewPriority", 80);
        Col("业绩", "EarningsSeverity", 100);
        Col("强提醒", "StrongAlertReviewAction", 110);
    }

    private static void AddHistoryColumns(DataGrid g)
    {
        void Col(string header, string path, double w)
        {
            g.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path) { Converter = DisplayConverter },
                Width = new DataGridLength(w),
            });
        }
        Col("ID", "Id", 150);
        Col("代码", "Code", 70);
        Col("名称", "Name", 90);
        Col("时间", "ReviewTime", 140);
        Col("结论", "Verdict", 140);
        Col("备注", "Note", 200);
        Col("触发原因", "TriggerReason", 220);
        Col("后续信号", "SubsequentSignalStatus", 90);
        Col("下次触发", "NextRereviewTrigger", 200);
        Col("有效至", "ValidUntil", 100);
        Col("撤销", "Revoked", 50);
        Col("撤销时间", "RevokedAt", 140);
        Col("来源", "Source", 80);
    }

    private static void AddPoolColumns(DataGrid g)
    {
        void Col(string header, string path, double w, string? fmt = null)
        {
            var c = new DataGridTextColumn
            {
                Header = header,
                Binding = fmt is null ? new Binding(path) { Converter = DisplayConverter } : new Binding(path) { StringFormat = fmt },
                Width = new DataGridLength(w),
            };
            g.Columns.Add(c);
        }
        Col("代码", "Code", 82);
        Col("名称", "Name", 110);
        Col("行业", "Industry", 120);
        Col("质量分", "QualityScore", 72, "F1");
        Col("现价", "Price", 82, "F2");
        Col("安全边际价", "PSafe", 105, "F2");
        Col("合理价", "PFair", 90, "F2");
        Col("价格区间", "Zone", 90);
        Col("复核", "ReviewClass", 110);
        Col("优先级", "ReviewPriorityLevel", 100);
    }

    private static void AddUnmappedColumns(DataGrid g)
    {
        g.Columns.Add(new DataGridTextColumn { Header = "行业", Binding = new Binding("Industry"), Width = new DataGridLength(160) });
        g.Columns.Add(new DataGridTextColumn { Header = "只数", Binding = new Binding("Count"), Width = new DataGridLength(60) });
        g.Columns.Add(new DataGridTextColumn { Header = "样本代码", Binding = new Binding("SampleCodes"), Width = new DataGridLength(280) });
        g.Columns.Add(new DataGridTextColumn { Header = "样本名称", Binding = new Binding("SampleNames"), Width = new DataGridLength(280) });
    }

    private sealed class UnmappedRow
    {
        public string Industry { get; set; } = "";
        public string Count { get; set; } = "";
        public string SampleCodes { get; set; } = "";
        public string SampleNames { get; set; } = "";
    }
}
