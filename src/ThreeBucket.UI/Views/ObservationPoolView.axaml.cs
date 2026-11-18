using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using ThreeBucket.Core.Services;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

/// <summary>
/// 顶层「观察池」页（t42 起一页三表）：
/// ① T9 观察池：T9 每次扫描命中自动入池（data/observation_pool.json），观察 30 个自然日，到期自动归档（observation_pool_archive.csv）；
/// ② S27 底部反转买入信号：data/bottom_reversal/tracking/br_track_latest.csv（30 个交易日跟踪）中的 B2/B3 与 T+1 检查「可介入」的 B1；
/// ③ S28 底部缓升买入信号：data/bottom_slowrise/signals_&lt;日期&gt;.csv 中的 BUY（信号日起 30 个自然日，同 T9 观察期）。
/// 现价 / 当日涨幅 / 累计涨幅：用实时行情（QuoteService，含今日盘中价），只用于显示，不写任何数据文件；
/// 实时行情取不到时退回日K最新收盘并在「行情」列注明。
/// t42 修复：原「现价」= 日K最后一根收盘，而腾讯日K接口被 WAF 拦截（501）后回退新浪日K，新浪日K盘中不含当天 → 现价停在昨收，
/// 当天入选（14:30 盘中价）的票会被算成「入选价 vs 昨收」的假涨跌（如 300836 显示 −10.53%）。
/// </summary>
public partial class ObservationPoolView : UserControl, IRefreshable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int SrObserveDays = ObservationPool.ObserveDays; // S28 BUY 显示期：信号日起 30 个自然日（同 T9 观察池）

    private readonly AppState _app;
    private readonly Action<string> _status;
    private readonly ObservationPool _pool;
    private readonly DispatcherTimer _timer;
    private List<PoolRow> _all = new();
    private List<SigRow> _br = new();
    private List<SigRow> _sr = new();
    private Dictionary<string, QuoteInfo> _quotes = new();
    private DateTime? _quoteAt;
    private string _brInfo = "", _srInfo = "";
    private bool _loadingCombo;
    private bool _busy, _quoting;

    public sealed class PoolRow
    {
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Strategies { get; init; } = "";
        public List<string> StrategyIds { get; init; } = new();
        public string EntryDate { get; init; } = "";
        public double EntryPrice { get; init; }
        public double LastPrice { get; init; }
        /// <summary>当日涨幅% = 现价 / 昨收 − 1（不复权，实时行情）；取不到实时行情时为空。</summary>
        public double? DayPct { get; init; }
        /// <summary>累计涨幅% = 现价 / 入选价 − 1（不复权）。</summary>
        public double? CumPct { get; init; }
        /// <summary>原「涨跌%」：日K最新收盘 / 入选日收盘(前复权) − 1；K线最新日早于入选日时为空（尚无入选日之后的收盘）。</summary>
        public double? ReturnPct { get; init; }
        public double MaxGainPct { get; init; }
        public double MaxDrawdownPct { get; init; }
        public int HoldDays { get; init; }
        public int HitCount { get; init; }
        public string LastHitDate { get; init; } = "";
        public string ExpireDate { get; init; } = "";
        public int RemainingDays { get; init; }
        public string LastBarDate { get; init; } = "";
        public string QuoteSource { get; init; } = "";
        public string Status { get; init; } = "";
    }

    /// <summary>S27 / S28 买入信号行。</summary>
    public sealed class SigRow
    {
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string SignalDate { get; init; } = "";
        public string Signal { get; init; } = "";
        public string Grade { get; init; } = "";
        public double? EntryPrice { get; init; }
        public double? Stop { get; init; }
        public double? LastPrice { get; init; }
        public double? DayPct { get; init; }
        public double? CumPct { get; init; }
        public string Status { get; init; } = "";
        public string Days { get; init; } = "";
        public string QuoteSource { get; init; } = "";
        public string Note { get; init; } = "";
        // 原始数据（重算现价用）
        public double FallbackClose { get; init; }
        public string FallbackDate { get; init; } = "";
        public string BaseStatus { get; init; } = "";
    }

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public ObservationPoolView() : this(new AppState(), _ => { }) { }

    public ObservationPoolView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app;
        _status = status;
        _pool = new ObservationPool(app.DataDir, app.Klines);

        QuoteBtn.Click += async (_, _) => await FetchQuotesAsync(manual: true);
        RefreshBtn.Click += async (_, _) => await RefreshAsync();
        RemoveBtn.Click += async (_, _) => await RemoveSelectedAsync();
        ArchiveToggle.IsCheckedChanged += (_, _) => Load();
        StratCombo.SelectionChanged += (_, _) => { if (!_loadingCombo) ApplyFilter(); };
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        PendingB1Check.IsCheckedChanged += (_, _) => { LoadBr(); ApplyFilter(); _ = FetchQuotesAsync(manual: false); };
        Tabs.SelectionChanged += (_, _) => UpdateStats();

        // 交易时段每 60 秒自动刷新实时现价（页面可见时；只拉行情，不写文件）
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) =>
        {
            if (IsEffectivelyVisible && InTradingHours(TradingCalendar.NowCn())) await FetchQuotesAsync(manual: false);
        };
        _timer.Start();
    }

    public void OnShown()
    {
        Load();
        _ = FetchQuotesAsync(manual: false);
    }

    private bool ShowArchive => ArchiveToggle.IsChecked == true;

    private static bool InTradingHours(DateTime now)
    {
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var t = now.TimeOfDay;
        return t >= new TimeSpan(9, 15, 0) && t <= new TimeSpan(15, 5, 0);
    }

    // ───────────────────────── 加载 ─────────────────────────

    private void Load()
    {
        var today = TradingCalendar.NowCn();
        var src = ShowArchive ? _pool.LoadArchive() : _pool.Load();
        _all = src.Select(e => BuildPoolRow(e, today)).ToList();

        _loadingCombo = true;
        var prev = StratCombo.SelectedItem as string;
        var items = new List<string> { "全部策略" };
        items.AddRange(_all.SelectMany(r => r.StrategyIds).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.Length).ThenBy(s => s, StringComparer.Ordinal));
        StratCombo.ItemsSource = items;
        StratCombo.SelectedItem = prev is not null && items.Contains(prev) ? prev : "全部策略";
        _loadingCombo = false;
        RemoveBtn.IsEnabled = !ShowArchive;

        LoadBr();
        LoadSr();
        ApplyFilter();
    }

    private PoolRow BuildPoolRow(ObservationEntry e, DateTime today)
    {
        var (px, day, srcTxt) = ShowArchive ? (e.LastPrice, (double?)null, $"归档时收盘 {e.LastBarDate}") : Live(e.Code, e.LastPrice, e.LastBarDate);
        // K线最新日早于入选日（当天盘中入选、日K还没有今天）：没有入选日之后的收盘，涨跌% 不可算
        var noBarYet = string.CompareOrdinal(e.LastBarDate, e.EntryDate) < 0;
        return new PoolRow
        {
            Code = e.Code, Name = e.Name,
            Strategies = string.Join("/", e.StrategyIds),
            StrategyIds = e.StrategyIds,
            EntryDate = e.EntryDate, EntryPrice = e.EntryPrice, LastPrice = px,
            DayPct = day,
            CumPct = e.EntryPrice > 0 && px > 0 ? Math.Round((px / e.EntryPrice - 1) * 100, 2) : null,
            ReturnPct = noBarYet ? null : e.ReturnPct,
            MaxGainPct = e.MaxGainPct, MaxDrawdownPct = e.MaxDrawdownPct,
            HoldDays = e.HoldDays, HitCount = e.HitCount, LastHitDate = e.LastHitDate,
            ExpireDate = e.ExpireDate,
            RemainingDays = ShowArchive ? 0 : Math.Max(0, e.RemainingDays(today)),
            LastBarDate = e.LastBarDate,
            QuoteSource = srcTxt,
            Status = ShowArchive ? $"{e.ExitReason} {e.ExitDate} {e.Note}".Trim() : e.Note,
        };
    }

    /// <summary>现价/当日涨幅：优先实时行情；取不到时用日K收盘（当日涨幅留空）。</summary>
    private (double Price, double? DayPct, string Source) Live(string code, double fallback, string fallbackDate)
    {
        if (_quotes.TryGetValue(code.PadLeft(6, '0'), out var q) && q.Price > 0)
        {
            var px = (double)q.Price;
            double? day = q.PrevClose > 0 ? Math.Round(q.ChangePct, 2) : null;
            return (px, day, $"实时 {_quoteAt:MM-dd HH:mm}");
        }
        return (fallback, null, fallback > 0 ? $"日K收盘 {fallbackDate}" + (_quoteAt is null ? "" : "（实时取不到）") : "无行情");
    }

    /// <summary>S27：br_track_latest.csv（T10 收盘后更新）中仍在 30 个交易日跟踪期内的 B2/B3，及 T+1 检查可介入的 B1。</summary>
    private void LoadBr()
    {
        var path = Path.Combine(_app.DataDir, "bottom_reversal", "tracking", "br_track_latest.csv");
        var rows = ReadCsv(path);
        var withPending = PendingB1Check.IsChecked == true;
        var list = new List<SigRow>();
        string asOf = "";
        int pendingB1 = 0;
        foreach (var r in rows)
        {
            var type = G(r, "signal");
            var exec = G(r, "exec_status");
            var status = G(r, "status");
            if (asOf.Length == 0) asOf = G(r, "as_of");
            if (status is "已到期" or "数据缺失") continue;
            if (int.TryParse(G(r, "days"), out var dd) && dd >= BrTrackDays) continue;
            var pending = type == "B1" && exec.StartsWith("T+1未发生", StringComparison.Ordinal);
            if (pending) pendingB1++;
            var ok = type is "B2" or "B3"
                     || (type == "B1" && exec.StartsWith("可介入", StringComparison.Ordinal))
                     || (withPending && pending);
            if (!ok) continue;
            var grp = G(r, "group");
            var grade = string.Join(" · ", new[] { grp.Length > 0 ? grp + "组" : "", exec }.Where(x => x.Length > 0));
            var notes = new List<string>();
            var basis = G(r, "plan_basis"); if (basis.Length > 0) notes.Add("入选价=" + basis);
            var stage = G(r, "stage"); if (stage.Length > 0) notes.Add("阶段 " + stage);
            var tot = G(r, "total"); if (tot.Length > 0) notes.Add("趋势分 " + tot);
            if (G(r, "stop_hit") == "是") notes.Add($"收盘跟踪已触及止损 {G(r, "stop_date")}");
            var adj = D(G(r, "adj_factor"));
            if (adj is { } a && Math.Abs(a - 1) > 1e-6) notes.Add("信号后有除权：入选价/止损为信号日口径，现价为不复权实时价，累计涨幅含除权缺口");
            var n = G(r, "note"); if (n.Length > 0) notes.Add(n);
            list.Add(new SigRow
            {
                Code = G(r, "code").PadLeft(6, '0'), Name = G(r, "name"), SignalDate = G(r, "signal_date"), Signal = type,
                Grade = grade, EntryPrice = D(G(r, "plan_price")) ?? D(G(r, "entry")), Stop = D(G(r, "stop")),
                FallbackClose = D(G(r, "cur_close_raw")) ?? 0, FallbackDate = G(r, "as_of"),
                BaseStatus = status, Days = $"{(dd > 0 ? dd : 0)}/{BrTrackDays}",
                Note = string.Join("；", notes),
            });
        }
        _br = list.OrderByDescending(x => x.SignalDate, StringComparer.Ordinal).ThenBy(x => x.Signal, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToList();
        _brInfo = rows.Count == 0
            ? "S27：尚无跟踪数据（T10 收盘扫描后生成 br_track_latest.csv）"
            : $"S27：跟踪截至 {asOf}，B2/B3 + 可介入B1 共 {_br.Count(x => !(x.Signal == "B1" && x.Grade.Contains("T+1未发生")))} 条" + (withPending ? $"（另含待T+1确认B1 {_br.Count(x => x.Signal == "B1" && x.Grade.Contains("T+1未发生"))} 条）" : $"；待T+1确认的B1 {pendingB1} 条未显示（勾选「含待T+1确认的B1」可显示）");
    }

    private const int BrTrackDays = ThreeBucket.Core.Services.BottomReversal.BrTrack.TrackDays;

    /// <summary>S28：bottom_slowrise/signals_&lt;日期&gt;.csv 的 BUY（信号日起 30 个自然日内），按 代码+信号日 去重；最新一份信号表里的持仓状态一并显示。</summary>
    private void LoadSr()
    {
        var dir = Path.Combine(_app.DataDir, "bottom_slowrise");
        var today = TradingCalendar.NowCn().Date;
        var list = new List<SigRow>();
        var seen = new HashSet<string>();
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "signals_*.csv").Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^signals_\d{4}-\d{2}-\d{2}\.csv$"))
                .OrderByDescending(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();
        // 最新信号表：持仓/卖出状态（模拟组合）
        var posState = new Dictionary<string, (string Sig, string Note)>();
        if (files.Count > 0)
            foreach (var r in ReadCsv(files[0]))
                if (G(r, "signal") is "HOLD" or "SELL" or "REDUCE") posState[G(r, "code").PadLeft(6, '0')] = (G(r, "signal"), G(r, "position_note"));
        var latestDay = files.Count > 0 ? Path.GetFileNameWithoutExtension(files[0])[8..] : "";
        foreach (var f in files)
        {
            var fileDay = Path.GetFileNameWithoutExtension(f)[8..];
            if (!DateTime.TryParseExact(fileDay, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var fd)) continue;
            if (fd.AddDays(SrObserveDays) < today) continue;
            foreach (var r in ReadCsv(f))
            {
                if (G(r, "signal") != "BUY") continue;
                var code = G(r, "code").PadLeft(6, '0');
                var sd = G(r, "date"); if (sd.Length == 0) sd = fileDay;
                if (!seen.Add(code + "|" + sd)) continue;
                var sdd = DateTime.TryParseExact(sd, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var x) ? x : fd;
                var remain = (sdd.AddDays(SrObserveDays) - today).Days;
                if (remain < 0) continue;
                var pn = G(r, "position_note");
                var status = pn.StartsWith("大盘过滤未通过", StringComparison.Ordinal) ? "仅记录（大盘过滤未通过）" : "BUY（次日开盘）";
                if (posState.TryGetValue(code, out var ps)) status = $"模拟持仓 {ps.Sig}";
                var notes = new List<string>();
                var reason = G(r, "reason"); if (reason.Length > 0) notes.Add(reason);
                if (pn.Length > 0) notes.Add(pn);
                if (posState.TryGetValue(code, out var ps2) && ps2.Note.Length > 0) notes.Add(ps2.Note);
                var pe = G(r, "pool_enter_date"); if (pe.Length > 0) notes.Add("进池 " + pe);
                list.Add(new SigRow
                {
                    Code = code, Name = G(r, "name"), SignalDate = sd, Signal = "BUY",
                    Grade = G(r, "score"),
                    EntryPrice = D(G(r, "entry_price").Split('(')[0]) ?? D(G(r, "close")), Stop = D(G(r, "stop_price")),
                    FallbackClose = D(G(r, "close")) ?? 0, FallbackDate = sd,
                    BaseStatus = status, Days = remain.ToString(Inv),
                    Note = string.Join("；", notes),
                });
            }
        }
        _sr = list.OrderByDescending(x => x.SignalDate, StringComparer.Ordinal).ThenByDescending(x => D(x.Grade) ?? 0).ToList();
        _srInfo = files.Count == 0
            ? "S28：尚无每日信号表（T11 收盘扫描后生成 signals_<日期>.csv）"
            : $"S28：最新信号表 {latestDay}，近 {SrObserveDays} 天 BUY {_sr.Count} 条（大盘过滤未通过的 BUY 只记录、不开仓）";
    }

    /// <summary>用最新行情重算信号行的现价 / 当日涨幅 / 累计涨幅 / 状态。</summary>
    private SigRow WithQuote(SigRow s)
    {
        var (px, day, src) = Live(s.Code, s.FallbackClose, s.FallbackDate);
        var status = s.BaseStatus;
        if (px > 0 && s.Stop is > 0 && px <= s.Stop) status = "⚠ 现价≤止损 · " + status;
        return new SigRow
        {
            Code = s.Code, Name = s.Name, SignalDate = s.SignalDate, Signal = s.Signal, Grade = s.Grade,
            EntryPrice = s.EntryPrice, Stop = s.Stop,
            LastPrice = px > 0 ? px : null, DayPct = day,
            CumPct = px > 0 && s.EntryPrice is > 0 ? Math.Round((px / s.EntryPrice.Value - 1) * 100, 2) : null,
            Status = status, Days = s.Days, QuoteSource = src, Note = s.Note,
            FallbackClose = s.FallbackClose, FallbackDate = s.FallbackDate, BaseStatus = s.BaseStatus,
        };
    }

    // ───────────────────────── 过滤/显示 ─────────────────────────

    private void ApplyFilter()
    {
        var text = SearchBox.Text?.Trim() ?? "";
        bool M(string code, string name) => text.Length == 0 || (code + name).Contains(text, StringComparison.OrdinalIgnoreCase);

        var sid = StratCombo.SelectedItem as string;
        var q = _all.AsEnumerable();
        if (!string.IsNullOrEmpty(sid) && sid != "全部策略")
            q = q.Where(r => r.StrategyIds.Contains(sid, StringComparer.OrdinalIgnoreCase));
        _shownPool = q.Where(r => M(r.Code, r.Name)).OrderByDescending(r => r.CumPct ?? double.MinValue).ToList();
        Grid.SetItemsSafe(new ObservableCollection<PoolRow>(_shownPool));

        _shownBr = _br.Where(r => M(r.Code, r.Name)).Select(WithQuote).ToList();
        BrGrid.SetItemsSafe(new ObservableCollection<SigRow>(_shownBr));
        _shownSr = _sr.Where(r => M(r.Code, r.Name)).Select(WithQuote).ToList();
        SrGrid.SetItemsSafe(new ObservableCollection<SigRow>(_shownSr));

        BrTab.Header = $"S27 底部反转 买入信号（{_shownBr.Count}）";
        SrTab.Header = $"S28 底部缓升 买入信号（{_shownSr.Count}）";
        PoolTab.Header = $"T9 观察池（{_shownPool.Count}）";
        UpdateStats();
    }

    private List<PoolRow> _shownPool = new();
    private List<SigRow> _shownBr = new();
    private List<SigRow> _shownSr = new();

    private static string Avg(IEnumerable<double?> v)
    {
        var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return l.Count == 0 ? "-" : $"{l.Average():+0.00;-0.00;0.00}%（上涨 {l.Count(x => x > 0)}/{l.Count}）";
    }

    private void UpdateStats()
    {
        var quoteTxt = _quoteAt is null ? "实时行情未取到（显示日K收盘）" : $"实时行情 {_quoteAt:HH:mm:ss}";
        QuoteText.Text = quoteTxt;
        switch (Tabs.SelectedIndex)
        {
            case 1:
                Stats.Text = $"{_brInfo}  |  当日涨幅均值 {Avg(_shownBr.Select(r => r.DayPct))} · 累计涨幅均值 {Avg(_shownBr.Select(r => r.CumPct))}";
                Footnote.Text = "S27 来源 data/bottom_reversal/tracking/br_track_latest.csv（T10 收盘扫描后更新）：显示 30 个交易日跟踪期内的 B2/B3，以及 T+1 检查「可介入」的 B1（取消的 B1 不显示）。"
                                + "入选价 = 计划价（可介入时为 T+1 开盘模拟计划价，否则为信号日计划价）；累计涨幅 = 现价 / 入选价 − 1；当日涨幅 = 现价 / 昨收 − 1（实时、不复权）。⚠ = 现价已 ≤ 止损。模拟数据，不是真实成交。";
                break;
            case 2:
                Stats.Text = $"{_srInfo}  |  当日涨幅均值 {Avg(_shownSr.Select(r => r.DayPct))} · 累计涨幅均值 {Avg(_shownSr.Select(r => r.CumPct))}";
                Footnote.Text = $"S28 来源 data/bottom_slowrise/signals_<日期>.csv（T11 收盘扫描）：信号日起 {SrObserveDays} 个自然日内的 BUY，按 代码+信号日 去重。入选价 = 信号日参考收盘（实际按 T+1 开盘成交）；"
                                + "「仅记录」= 当天沪深300 大盘过滤未通过，模拟组合不开仓、信号只记录。累计涨幅 = 现价 / 入选价 − 1；当日涨幅 = 现价 / 昨收 − 1（实时、不复权）。剩余天数 = 信号日+30 天 − 今天。";
                break;
            default:
                var up = _shownPool.Count(r => r.CumPct > 0);
                var mtime = _app.Store.FileMtime(ShowArchive ? ObservationPool.ArchiveFile : ObservationPool.PoolFile);
                Stats.Text = $"{(ShowArchive ? "归档" : "在池")} {_shownPool.Count} 只 · 累计上涨 {up} · 累计涨幅均值 {Avg(_shownPool.Select(r => r.CumPct))} · 当日涨幅均值 {Avg(_shownPool.Select(r => r.DayPct))}"
                             + (string.IsNullOrEmpty(mtime) ? "  |  尚无数据（T9 扫描后自动入池）" : $"  |  观察池文件更新于 {mtime}");
                Footnote.Text = "现价 = 实时行情（盘中实时 / 收盘后为今日收盘；取不到时退回日K收盘，见「行情」列）；当日涨幅 = 现价 / 昨收 − 1；累计涨幅 = 现价 / 入选价 − 1（均不复权）。"
                                + "收盘涨跌% = 日K最新收盘 / 入选日收盘(前复权) − 1（「重算观察池」时更新，空 = 还没有入选日之后的日K）；最高% / 回撤% / 持有 同为日K口径。每次 T9 扫描后自动入池；点表头可排序。";
                break;
        }
    }

    // ───────────────────────── 行情 ─────────────────────────

    private async Task FetchQuotesAsync(bool manual)
    {
        if (_quoting) return;
        var codes = _all.Where(_ => !ShowArchive).Select(r => r.Code).Concat(_br.Select(r => r.Code)).Concat(_sr.Select(r => r.Code))
            .Select(c => c.PadLeft(6, '0')).Distinct().ToList();
        if (codes.Count == 0) { if (manual) _status("观察池：没有需要拉行情的股票"); return; }
        _quoting = true;
        QuoteBtn.IsEnabled = false;
        try
        {
            var dict = new Dictionary<string, QuoteInfo>();
            foreach (var chunk in codes.Chunk(60))
            {
                var part = await Task.Run(() => _app.Quotes.FetchAsync(chunk));
                foreach (var kv in part) dict[kv.Key] = kv.Value;
            }
            if (dict.Count > 0) { _quotes = dict; _quoteAt = TradingCalendar.NowCn(); }
            var today = TradingCalendar.NowCn();
            if (!ShowArchive)
            {
                var src = _pool.Load();
                _all = src.Select(e => BuildPoolRow(e, today)).ToList();
            }
            ApplyFilter();
            if (manual) _status($"观察池：实时行情 {dict.Count}/{codes.Count} 只" + (dict.Count < codes.Count ? "（其余显示日K收盘）" : ""));
        }
        catch (Exception ex) { if (manual) _status("观察池：实时行情获取失败：" + ex.Message); }
        finally { _quoting = false; QuoteBtn.IsEnabled = true; }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshBtn.IsEnabled = false;
        _status("观察池：按日K重算…");
        try
        {
            var r = await _pool.RefreshAsync();
            _status($"观察池重算完成：在池 {r.Active} · 更新 {r.Refreshed} · 停牌/未更新 {r.Stale} · 失败 {r.Failed} · 到期归档 {r.Archived}");
            Load();
            await FetchQuotesAsync(manual: false);
        }
        catch (Exception ex) { _status("观察池刷新失败：" + ex.Message); }
        finally { RefreshBtn.IsEnabled = true; _busy = false; }
    }

    private async Task RemoveSelectedAsync()
    {
        if (ShowArchive) return;
        if (Tabs.SelectedIndex != 0) { _status("「移除」只作用于 T9 观察池表"); return; }
        if (Grid.SelectedItem is not PoolRow row) { _status("请先选中一行"); return; }
        var ok = await _pool.RemoveAsync(row.Code);
        _status(ok ? $"已移出观察池并归档：{row.Code} {row.Name}" : $"{row.Code} 不在观察池");
        Load();
    }

    // ───────────────────────── CSV 工具 ─────────────────────────

    /// <summary>只读打开（允许其他进程同时写），表头去 BOM；文件不存在/读失败返回空。</summary>
    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var res = new List<Dictionary<string, string>>();
        try
        {
            if (!File.Exists(path)) return res;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var head = sr.ReadLine();
            if (head is null) return res;
            var cols = ObservationPool.ParseCsvLine(head).Select(h => h.Trim().TrimStart('\uFEFF')).ToList();
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var f = ObservationPool.ParseCsvLine(line);
                var d = new Dictionary<string, string>(cols.Count);
                for (var i = 0; i < cols.Count; i++) d[cols[i]] = i < f.Count ? f[i].Trim() : "";
                res.Add(d);
            }
        }
        catch { }
        return res;
    }

    private static string G(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
    private static double? D(string s) => double.TryParse(s.Trim().TrimEnd('%'), NumberStyles.Float, Inv, out var v) && !double.IsNaN(v) ? v : null;
}
