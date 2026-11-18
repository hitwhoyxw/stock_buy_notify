namespace ThreeBucket.Core.Services.SlowRise;

/// <summary>持仓（持久化：只存日期、股数、实际成交价、相对买入价的比例，不存调整后绝对价格）。</summary>
public sealed class SrPosition
{
    public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string Industry { get; set; } = "";
    public string SigDate { get; set; } = ""; public string EntryDate { get; set; } = "";
    public double Shares { get; set; } public double Shares0 { get; set; }
    public double EntryRawOpen { get; set; } public double EntryFill { get; set; }
    public double StopRatio { get; set; } public string StopKind { get; set; } = ""; public double StructRatio { get; set; } public double InitStopRatio { get; set; }
    public double PeakRatio { get; set; } = 1; public double MaeRatio { get; set; } = 1; public double MfeRatio { get; set; } = 1;
    public bool Reduced { get; set; } public bool R4Done { get; set; }
    public double CashIn { get; set; } public double CashOut { get; set; }
    public double Score { get; set; } public string Reason { get; set; } = ""; public string Regime { get; set; } = "";
    public double Dd { get; set; } public double Rebound { get; set; } public string D0Date { get; set; } = ""; public double D0VolRatio { get; set; }
    public string MaCrossDate { get; set; } = ""; public string MacdCrossDate { get; set; } = ""; public double Er { get; set; } public double R2 { get; set; } public double Sigma { get; set; } public int L3Count { get; set; }
    public List<string> Log { get; set; } = [];
    public string LastSignal { get; set; } = "HOLD"; public string LastNote { get; set; } = "";
}

public sealed class SrPending
{
    public string Code { get; set; } = ""; public string Kind { get; set; } = ""; public string SigDate { get; set; } = "";
    public double Fraction { get; set; } = 1; public string Primary { get; set; } = ""; public string Reasons { get; set; } = "";
    public double WeakMult { get; set; } = 1;
    public SrPosition? Snap { get; set; }   // BUY：信号快照
}

public sealed class SrTrade
{
    public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string Industry { get; set; } = "";
    public string SigDate { get; set; } = ""; public string EntryDate { get; set; } = ""; public string ExitDate { get; set; } = "";
    public double EntryFill { get; set; } public double Shares0 { get; set; } public double CashIn { get; set; } public double CashOut { get; set; }
    public double Ret { get; set; } public double Pnl { get; set; } public int HoldDays { get; set; }
    public string Primary { get; set; } = ""; public string Reasons { get; set; } = ""; public string Partials { get; set; } = "";
    public double Mae { get; set; } public double Mfe { get; set; } public double InitStopPct { get; set; } public string StopKind { get; set; } = "";
    public double Score { get; set; } public string Reason { get; set; } = ""; public string Regime { get; set; } = "";
    public double Dd { get; set; } public double Rebound { get; set; } public string D0Date { get; set; } = ""; public double D0VolRatio { get; set; }
    public string MaCrossDate { get; set; } = ""; public string MacdCrossDate { get; set; } = ""; public double Er { get; set; } public double R2 { get; set; } public double Sigma { get; set; } public int L3Count { get; set; }
    public bool OpenAtEnd { get; set; }
}

public sealed class SrBuyCand
{
    public string Code = ""; public SrEval Ev = null!; public SrStock S = null!;
    public string Outcome = "";   // 组合层结果（选中 / 未选原因）
}

/// <summary>组合（回测与实盘模拟共用）：T 日收盘出信号 → T+1 开盘成交；先卖后买；T+1；涨停开盘不买；跌停封死顺延；成本见 cost 段。</summary>
public sealed class SrBook
{
    public double Cash { get; set; }
    public List<SrPosition> Positions { get; set; } = [];
    public List<SrPending> Pending { get; set; } = [];
    public Dictionary<string, string> LastSell { get; set; } = new();
    public List<SrTrade> Closed { get; set; } = [];
    public double LastEquity { get; set; }
    public List<string> EquityDates { get; set; } = [];
    public List<double> Equity { get; set; } = [];

    public static SrBook New(SrParams p) => new() { Cash = p.Capital, LastEquity = p.Capital };

    private static double Fee(double notional, SrParams p, bool sell) => Math.Max(p.CommMin, notional * p.Comm) + notional * p.Transfer + (sell ? notional * p.Stamp : 0);

    /// <summary>一个交易日。stocks：代码 → 股票（含今日或更早数据）。cands：今日收盘的 BUY 信号。返回今日的持仓信号行（HOLD/REDUCE/SELL）。</summary>
    public void Day(string date, IReadOnlyDictionary<string, int> cal, Func<string, SrStock?> stock, List<SrBuyCand> cands, SrData.Index mkt, SrParams p, List<string>? log = null)
    {
        var calPos = cal[date];
        // ── A. 开盘：先卖 ──
        foreach (var o in Pending.Where(x => x.Kind == "SELL").ToList())
        {
            var pos = Positions.FirstOrDefault(x => x.Code == o.Code); var s = stock(o.Code);
            if (pos is null || s is null) { Pending.Remove(o); continue; }
            if (!s.Ix.TryGetValue(date, out var t)) continue;                       // 停牌：顺延
            var locked = t > 0 && s.RH[t] - s.RL[t] < 1e-6 && s.RC[t] <= Math.Round(s.RefPrev[t] * (1 - s.Lp), 2) + 0.001;   // 一字跌停
            if (locked) { pos.Log.Add($"{date} 跌停封死，卖出顺延"); continue; }
            Pending.Remove(o);
            var e = s.Ix[pos.EntryDate];
            var sh = o.Fraction >= 1 ? pos.Shares : Math.Floor(pos.Shares * o.Fraction / p.Lot) * p.Lot;
            if (sh <= 0) { pos.Log.Add($"{date} 减仓股数不足一手，未卖"); continue; }
            var px = pos.EntryRawOpen * s.O[t] / s.O[e] * (1 - p.Slip);
            var gross = sh * px; var net = gross - Fee(gross, p, true);
            Cash += net; pos.CashOut += net; pos.Shares -= sh;
            pos.Log.Add($"{date} 卖出{(pos.Shares > 0 ? "（减仓）" : "")} {sh:F0} 股 @≈{px:F2}（{o.Primary}）");
            if (pos.Shares <= 0) { Close(pos, s, date, cal, o, false); LastSell[pos.Code] = date; }
        }
        // ── A. 开盘：再买 ──
        foreach (var o in Pending.Where(x => x.Kind == "BUY").ToList())
        {
            Pending.Remove(o);
            var s = stock(o.Code); var snap = o.Snap!;
            if (s is null || !s.Ix.TryGetValue(date, out var t) || t == 0 || s.Date[t - 1] != o.SigDate) { log?.Add($"{date} {o.Code} 停牌/无数据，买入放弃"); continue; }
            if (s.RO[t] >= Math.Round(s.RefPrev[t] * (1 + s.Lp), 2) - 0.001) { log?.Add($"{date} {o.Code} 开盘涨停，买入放弃"); continue; }
            var sig = t - 1; var ea = s.O[t];
            var d0 = s.Ix.TryGetValue(snap.D0Date, out var di) ? s.L[di] : double.NaN;
            double low10 = double.PositiveInfinity; for (var k = Math.Max(0, sig - p.StructLowDays + 1); k <= sig; k++) low10 = Math.Min(low10, s.L[k]);
            double st; string kind; double structR = 0;
            switch (p.StopMode)
            {
                case "higher":   // 说明书 v1.0 原文：S1 硬止损 −8% 与 S2 结构止损 max(D0低点, 买入前10日最低) 并存，显示止损价取较高者
                    structR = Math.Max(double.IsNaN(d0) ? 0 : d0, low10) / ea;
                    st = ea * (1 - p.HardStop); kind = "S1";
                    break;
                case "atr":
                    st = ea - p.AtrMult * s.Atr[sig]; if (p.AtrCap) st = Math.Max(st, ea * (1 - p.MaxStop)); kind = "S1";
                    break;
                default:         // t34：D0 低点与 −8% 取较低者，但最宽不超过 −12%
                    var hard = ea * (1 - p.HardStop);
                    st = double.IsNaN(d0) ? hard : Math.Min(d0, hard); kind = st < hard ? "S2" : "S1";
                    var cap = ea * (1 - p.MaxStop); if (st < cap) { st = cap; kind = "S1"; }
                    break;
            }
            var riskPct = Math.Max(0.005, 1 - Math.Max(st, structR * ea) / ea);
            if (p.StopMode != "higher") riskPct = Math.Max(0.005, 1 - st / ea);
            var fill = s.RO[t] * (1 + p.Slip);
            double sh = p.Sizing == "equal"
                ? Math.Floor(LastEquity * p.EqualPct * o.WeakMult / fill / p.Lot) * p.Lot
                : Math.Floor(LastEquity * p.RiskPer * o.WeakMult / (fill * riskPct) / p.Lot) * p.Lot;
            sh = Math.Min(sh, Math.Floor(LastEquity * p.MaxPosPct / fill / p.Lot) * p.Lot);
            while (sh >= p.Lot && sh * fill + Fee(sh * fill, p, false) > Cash) sh -= p.Lot;
            if (sh < p.Lot) { log?.Add($"{date} {o.Code} 资金不足一手，买入放弃"); continue; }
            var cost = sh * fill + Fee(sh * fill, p, false);
            Cash -= cost;
            snap.EntryDate = date; snap.Shares = sh; snap.Shares0 = sh; snap.EntryRawOpen = s.RO[t]; snap.EntryFill = fill; snap.CashIn = cost;
            snap.StopRatio = st / ea; snap.InitStopRatio = p.StopMode == "higher" ? Math.Max(st / ea, structR) : st / ea; snap.StopKind = kind; snap.StructRatio = structR;
            snap.Log.Add($"{date} 开盘买入 {sh:F0} 股 @{fill:F2}，止损 {snap.InitStopRatio * s.RO[t]:F2}（{(p.StopMode == "atr" ? "ATR" : kind == "S2" ? "D0低点" : "−" + (1 - st / ea).ToString("P0"))}）");
            Positions.Add(snap);
        }
        // ── B. 收盘：盯市 + 持仓信号 ──
        double eq = Cash;
        foreach (var pos in Positions.ToList())
        {
            var s = stock(pos.Code); if (s is null) continue;
            var e = s.Ix[pos.EntryDate];
            var t = LastIndex(s, date); if (t < e) continue;
            eq += pos.Shares * pos.EntryRawOpen * s.C[t] / s.O[e];
            if (s.Date[t] != date) { pos.LastSignal = "HOLD"; pos.LastNote = "停牌"; continue; }
            pos.MaeRatio = Math.Min(pos.MaeRatio, s.L[t] / s.O[e]); pos.MfeRatio = Math.Max(pos.MfeRatio, s.H[t] / s.O[e]);
            if (Pending.Any(x => x.Code == pos.Code && x.Kind == "SELL" && x.Fraction >= 1)) { pos.LastSignal = "SELL"; continue; }
            Evaluate(pos, s, e, t, date, cal, p);
        }
        LastEquity = eq; EquityDates.Add(date); Equity.Add(eq);

        // ── C. 今日 BUY 信号 → 明日开盘买单 ──
        var weak = mkt.Weak(date, p);
        var mult = !weak || p.WeakMode == "off" ? 1.0 : p.WeakMode == "pause" ? 0.0 : 0.5;
        var held = Positions.Count + Pending.Count(x => x.Kind == "BUY");
        var newToday = 0;
        foreach (var c in cands.OrderByDescending(x => p.RankBy == "score" ? x.Ev.Score : (double.IsNaN(x.Ev.Er) ? double.MinValue : x.Ev.Er)).ThenByDescending(x => x.Ev.Score).ThenBy(x => x.Code, StringComparer.Ordinal))
        {
            if (Positions.Any(x => x.Code == c.Code) || Pending.Any(x => x.Code == c.Code)) { c.Outcome = "已持有"; continue; }
            if (LastSell.TryGetValue(c.Code, out var ls) && cal.TryGetValue(ls, out var lsp) && calPos - lsp < p.Cooldown) { c.Outcome = $"卖出后{p.Cooldown}日冷却"; continue; }
            if (mult <= 0) { c.Outcome = "大盘弱势暂停开仓"; continue; }
            if (held >= p.MaxPos) { c.Outcome = $"持仓已满{p.MaxPos}只"; continue; }
            if (newToday >= p.MaxNew) { c.Outcome = $"当日新开已满{p.MaxNew}只"; continue; }
            if (p.IndCap && c.S.Industry.Length > 0 && Positions.Count(x => x.Industry == c.S.Industry) + Pending.Count(x => x.Kind == "BUY" && x.Snap?.Industry == c.S.Industry) >= p.IndMax) { c.Outcome = $"同行业已满{p.IndMax}只"; continue; }
            var ev = c.Ev; var s = c.S;
            Pending.Add(new SrPending
            {
                Code = c.Code, Kind = "BUY", SigDate = date, WeakMult = mult,
                Snap = new SrPosition
                {
                    Code = c.Code, Name = s.Name, Industry = s.Industry, SigDate = date, Score = ev.Score, Reason = SrEngine.ReasonText(s, ev, p), Regime = mkt.Regime(date),
                    Dd = ev.Dd, Rebound = ev.Rebound, D0Date = ev.D0 >= 0 ? s.Date[ev.D0] : "", D0VolRatio = ev.D0VolRatio,
                    MaCrossDate = ev.MaCross >= 0 ? s.Date[ev.MaCross] : "", MacdCrossDate = ev.MacdCross >= 0 ? s.Date[ev.MacdCross] : "", Er = ev.Er, R2 = ev.R2, Sigma = ev.Sigma, L3Count = ev.L3Count,
                },
            });
            c.Outcome = weak && mult < 1 ? "选中（弱市半仓）" : "选中"; held++; newToday++;
        }
    }

    private static int LastIndex(SrStock s, string date)
    {
        if (s.Ix.TryGetValue(date, out var t)) return t;
        var lo = 0; var hi = s.Count - 1; var ans = -1;
        while (lo <= hi) { var mid = (lo + hi) / 2; if (string.CompareOrdinal(s.Date[mid], date) <= 0) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
        return ans;
    }

    /// <summary>收盘评估：卖出 S1~S8（全部记录，主因按 S1&gt;S2&gt;S6&gt;S4&gt;S3&gt;S5&gt;S7&gt;S8）、减仓 R1~R3（首次触发卖 50%）、R4（首次跌破 MA10：上移止损）。</summary>
    private void Evaluate(SrPosition pos, SrStock s, int e, int t, string date, IReadOnlyDictionary<string, int> cal, SrParams p)
    {
        var ea = s.O[e]; var c = s.C[t]; var g = c / ea - 1;
        var prevPeak = pos.PeakRatio; pos.PeakRatio = Math.Max(pos.PeakRatio, c / ea);
        var hit = new List<string>();
        var stop = pos.StopRatio * ea;
        if (p.StopMode == "higher")
        {
            if (p.SOn[0] && c <= stop) hit.Add("S1");
            if (p.SOn[1] && pos.StructRatio > 0 && c < pos.StructRatio * ea) hit.Add("S2");
        }
        else if (c <= stop)
        {
            var k = pos.StopKind == "S2" ? 1 : 0;
            if (p.SOn[k]) hit.Add(pos.StopKind == "S2" ? "S2" : "S1");
            else if (k == 1 && p.SOn[0]) hit.Add("S1");   // v2.0：止损价 = min(买入×0.92, D0最低)（含 D0 低点），S2 关闭时统一记为规则 1“止损”
        }
        if (p.SOn[2] && pos.PeakRatio - 1 >= p.TrailAct && c <= pos.PeakRatio * ea * (1 - p.TrailDd)) hit.Add("S3");
        if (p.SOn[3] && t - p.Ma20BreakDays + 1 >= e) { var ok = true; for (var k = t - p.Ma20BreakDays + 1; k <= t; k++) if (!(s.C[k] < s.Ma20[k])) ok = false; if (ok) hit.Add("S4"); }
        if (p.SOn[4] && s.MaS[t - 1] >= s.MaL[t - 1] && s.MaS[t] < s.MaL[t]) hit.Add("S5");
        if (p.SOn[5] && s.RPct[t] <= p.BigDropPct && s.V[t] >= p.BigDropVol * s.VmaPrev[t]) hit.Add("S6");
        var heldDays = cal[date] - cal[pos.EntryDate] + 1;
        if (p.SOn[6] && heldDays >= p.TimeDays && g < p.TimeMinGain && c / ea < pos.PeakRatio - 1e-12) hit.Add("S7");
        if (p.SOn[7] && pos.Reduced && c < s.Ma20[t]) hit.Add("S8");
        var ordered = SrParams.ExitPriority.Where(hit.Contains).ToList();
        if (ordered.Count > 0)
        {
            var names = ordered.Select(k => SrParams.ExitNames[Array.IndexOf(SrParams.ExitKeys, k)]).ToList();
            Pending.Add(new SrPending { Code = pos.Code, Kind = "SELL", SigDate = date, Fraction = 1, Primary = ordered[0], Reasons = string.Join("、", names) });
            pos.LastSignal = "SELL"; pos.LastNote = string.Join("、", names);
            return;
        }
        // 减仓预警
        var r = new List<string>();
        if (p.ROn[0] && c / s.Ma20[t] - 1 > p.R1Dev) r.Add("R1");
        var body = Math.Abs(s.C[t] - s.O[t]); var upper = s.H[t] - Math.Max(s.C[t], s.O[t]);
        if (p.ROn[1] && g >= p.R2Gain && s.V[t] >= p.R2Vol * s.VmaPrev[t] && (upper > p.R2Shadow * body || s.RPct[t] < p.R2PctMax)) r.Add("R2");
        if (p.ROn[2] && s.Dif[t - 1] >= s.Dea[t - 1] && s.Dif[t] < s.Dea[t] && s.Dif[t] / c > p.R3Dif) r.Add("R3");
        var r4 = p.ROn[3] && !pos.R4Done && c < s.Ma10[t];
        pos.LastSignal = "HOLD"; pos.LastNote = $"浮盈 {SrParams.P(g)}，距止损 {SrParams.P(stop / c - 1)}，较持仓最高收盘 {SrParams.P(c / (pos.PeakRatio * ea) - 1)}";
        if (r.Count > 0)
        {
            var names = string.Join("、", r.Select(k => SrParams.ReduceNames[Array.IndexOf(SrParams.ReduceKeys, k)]));
            pos.LastSignal = "REDUCE"; pos.LastNote = names + "；" + pos.LastNote;
            if (!pos.Reduced)
            {
                pos.Reduced = true;
                if (p.ReduceMode == "sell_half") Pending.Add(new SrPending { Code = pos.Code, Kind = "SELL", SigDate = date, Fraction = p.SellFrac, Primary = "R减仓", Reasons = names });
                pos.Log.Add($"{date} 减仓预警 {names}{(p.ReduceMode == "sell_half" ? "，次日开盘卖出 " + p.SellFrac.ToString("P0") : "（只记录）")}");
            }
        }
        if (r4)
        {
            pos.R4Done = true; if (p.S8IncludeR4) pos.Reduced = true;
            if (p.R4Mode == "raise_stop")
            {
                // R4 规则：新止损 = max(原止损, R4 当日最低价, 若当日收盘高于买入价则买入价)
                var ns = Math.Max(stop, Math.Max(s.L[t], c > ea ? ea : stop));
                if (ns > stop) { pos.StopRatio = ns / ea; if (p.StopMode != "higher") pos.StopKind = "S1"; pos.Log.Add($"{date} R4 首次跌破MA10，止损上移至 {ns / ea * pos.EntryRawOpen:F2}"); }
            }
            if (pos.LastSignal == "HOLD") { pos.LastSignal = "REDUCE"; pos.LastNote = "R4 首次跌破MA10（收紧止损）；" + pos.LastNote; }
        }
    }

    private void Close(SrPosition pos, SrStock s, string date, IReadOnlyDictionary<string, int> cal, SrPending o, bool openAtEnd)
    {
        Positions.Remove(pos);
        var partial = string.Join(" | ", pos.Log.Where(x => x.Contains("减仓")));
        Closed.Add(new SrTrade
        {
            Code = pos.Code, Name = pos.Name, Industry = pos.Industry, SigDate = pos.SigDate, EntryDate = pos.EntryDate, ExitDate = date,
            EntryFill = pos.EntryFill, Shares0 = pos.Shares0, CashIn = pos.CashIn, CashOut = pos.CashOut, Pnl = pos.CashOut - pos.CashIn, Ret = pos.CashOut / pos.CashIn - 1,
            HoldDays = cal[date] - cal[pos.EntryDate], Primary = o.Primary, Reasons = o.Reasons, Partials = partial,
            Mae = pos.MaeRatio - 1, Mfe = pos.MfeRatio - 1, InitStopPct = 1 - pos.InitStopRatio, StopKind = pos.StopKind,
            Score = pos.Score, Reason = pos.Reason, Regime = pos.Regime, Dd = pos.Dd, Rebound = pos.Rebound, D0Date = pos.D0Date, D0VolRatio = pos.D0VolRatio,
            MaCrossDate = pos.MaCrossDate, MacdCrossDate = pos.MacdCrossDate, Er = pos.Er, R2 = pos.R2, Sigma = pos.Sigma, L3Count = pos.L3Count, OpenAtEnd = openAtEnd,
        });
    }

    /// <summary>回测结束：未平仓按最后收盘价（扣卖出成本）计入统计，标记 OpenAtEnd。</summary>
    public void CloseAllAtEnd(string date, IReadOnlyDictionary<string, int> cal, Func<string, SrStock?> stock, SrParams p)
    {
        foreach (var pos in Positions.ToList())
        {
            var s = stock(pos.Code); if (s is null) continue;
            var e = s.Ix[pos.EntryDate]; var t = LastIndex(s, date);
            var gross = pos.Shares * pos.EntryRawOpen * s.C[t] / s.O[e] * (1 - p.Slip);
            pos.CashOut += gross - Fee(gross, p, true); pos.Shares = 0;
            Close(pos, s, date, cal, new SrPending { Primary = "期末持仓", Reasons = "回测结束按收盘价计" }, true);
        }
    }
}
