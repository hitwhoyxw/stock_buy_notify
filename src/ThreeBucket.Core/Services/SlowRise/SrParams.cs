using System.Globalization;
using System.Text.Json.Nodes;
using ThreeBucket.Core.Services.BottomReversal;

namespace ThreeBucket.Core.Services.SlowRise;

/// <summary>
/// 底部反转缓升（第三个底部策略，T11）参数：data/bottom_slowrise_v1.json，全部阈值与每个子条件开关都在配置里（不硬编码）。
/// 复用 BrConfig（JSON 读取 / With() 克隆替换节点，用于消融与变体）。每次运行重新读取。
/// </summary>
public sealed class SrParams
{
    public const string FileName = "bottom_slowrise_v2.json";      // v2.0 精简版 + t37 修订（默认）
    public const string FileNameV1 = "bottom_slowrise_v1.json";    // v1（t34/t36）保留作对照
    public BrConfig Cfg { get; }
    public string Version => Cfg.S("version", "1.0");

    // 条件编号与名称（报告 / fail_reason 用）
    public static readonly string[] L0Keys = ["st", "list_days", "amount", "market_cap", "suspend", "one_word", "price"];
    public static readonly string[] L0Names = ["0.1 ST/退市", "0.2 上市天数", "0.3 20日均成交额", "0.4 总市值", "0.5 20日停牌", "0.6 20日一字板", "0.7 股价"];
    public static readonly string[] L1Keys = ["dd", "high_age", "days_since_low", "rebound", "low_lift", "range_pos"];
    public static readonly string[] L1Names = ["1.1 调整幅度DD", "1.2 高点距今", "1.3 距低点天数", "1.4 反弹幅度", "1.5 低点抬高", "1.6 250日区间位置"];
    public static readonly string[] L2Keys = ["vol", "pct", "close_pos", "d0_age", "hold_low", "vol_after"];
    public static readonly string[] L2Names = ["2.1 放量倍数", "2.2 启动日涨幅", "2.3 收盘位置", "2.4 D0距今", "2.5 不破D0低点", "2.6 启动后缩量"];
    public static readonly string[] L3Keys = ["big_move", "sigma", "max_drop", "er", "r2", "gain", "up_candle", "above_ma20", "atr"];
    public static readonly string[] L3Names = ["3.1 大波动天数", "3.2 波动率σ", "3.3 最大单日跌幅", "3.4 效率比ER", "3.5 回归R²", "3.6 区间涨幅", "3.7 阳线占比", "3.8 站上MA20占比", "3.9 ATR/C"];
    public static readonly string[] L4Keys = ["ma_cross", "above_ma_long", "ma_long_slope", "macd_cross", "macd_bottom", "macd_hist", "resonance"];
    public static readonly string[] L4Names = ["4.1 均线金叉", "4.2 站上长均线", "4.3 长均线不下行", "4.4 MACD金叉", "4.5 底部金叉", "4.6 红柱放大", "4.7 双金叉共振"];
    public static readonly string[] L4OKeys = ["enable_a", "enable_b", "enable_c"];
    public static readonly string[] L4ONames = ["4a MACD底部金叉", "4b 均线金叉确认", "4c 状态未失效"];
    /// <summary>当前 L4 模式下的条件名（ordered = t37 三段式 a/b/c；legacy = 说明书 v1 的 4.1~4.7）</summary>
    public string[] L4N => L4Mode == "legacy" ? L4Names : L4ONames;
    public static readonly string[] BuyNames = ["B4 当日涨幅<7%", "B4 MA20乖离<12%", "B5 VMA5≥0.8×VMA20", "B6 收盘未涨停", "B8 评分≥阈值"];
    public static readonly string[] ExitKeys = ["S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8"];
    public static readonly string[] ExitNames = ["S1 硬止损", "S2 结构止损", "S3 移动止盈", "S4 趋势破坏", "S5 均线死叉", "S6 放量大阴", "S7 时间止损", "S8 减仓后恶化"];
    public static readonly string[] ExitPriority = ["S1", "S2", "S6", "S4", "S3", "S5", "S7", "S8"];
    public static readonly string[] ReduceKeys = ["R1", "R2", "R3", "R4"];
    public static readonly string[] ReduceNames = ["R1 乖离过大", "R2 高位放量滞涨", "R3 MACD高位死叉", "R4 首次跌破MA10"];

    // L0
    public bool[] L0On = new bool[7];
    public int MinListDays, MaxSusp20, MaxOneWord20; public double MinAmt20, MinCap, MinPrice; public bool BacktestUseCap;
    public string[] ExcludeBoards = [];
    // L1
    public bool[] L1On = new bool[6];
    public double DdMin, RebMin, RebMax, LowLift, RangePosMax; public int HighWin, LowWin, HighAgeMin, DslMin, DslMax; public string LowLiftMode = "";
    // L2
    public bool[] L2On = new bool[6];
    public double VolMult, PctMin, PctMax, ClosePosMin, DryRatio, PullTouch; public int VmaWin, D0MaxAge, DryWin, DryVmaWin;
    // L3
    public bool[] L3On = new bool[9];
    public string L3Mode = ""; public int L3Win, L3MinWin, BigMoveMax, WatchMin, BuyMin; public double BigMove, SigmaMax, MaxDrop, ErMin, R2Min, GainMin, GainMax, UpRatio, AboveRatio, AtrMax;
    public int[] BuyMandatory = [];
    // L4
    public bool[] L4On = new bool[7];
    public int MaS, MaL, CrossLb, MaLSlopeDays, MacdF, MacdSl, MacdSig, CrossGap; public double DifRatioMax;
    public string L4Mode = "ordered"; public bool[] L4OOn = new bool[3]; public int MacdFromD0Off, MaCrossMaxAge; public bool RequireOrder;
    // WATCH / BUY
    public int WatchValid, ValidFromD0; public bool AllowSameDay, RequireNoDouble; public string RankBy = "er";
    public bool[] BuyOn = new bool[5];
    public double BuyMaxPct, BuyMaxDev, Vma5Min, ScoreMin;
    public bool MktOn; public string MktLogic = ""; public int MktMa, MktRetDays; public double MktRet5Min;
    // 退出
    public bool[] SOn = new bool[8];
    public string StopMode = ""; public double HardStop, MaxStop, AtrMult, TrailAct, TrailDd, BigDropPct, BigDropVol, TimeMinGain; public int StructLowDays, Ma20BreakDays, TimeDays; public bool AtrCap;
    public bool[] ROn = new bool[4];
    public double R1Dev, R2Gain, R2Vol, R2Shadow, R2PctMax, R3Dif, SellFrac; public string ReduceMode = "", R4Mode = ""; public bool S8IncludeR4;
    // 仓位 / 成本
    public string Sizing = ""; public double Capital, RiskPer, MaxPosPct, EqualPct; public int MaxPos, MaxNew, IndMax, Cooldown, WeakMa, WeakSlope, Lot; public bool IndCap; public string WeakMode = "";
    public double Comm, CommMin, Stamp, Slip, Transfer;
    // 评分（绝对阈值线性插值：[x0, x1, y0, y1]，x 在 x0→x1 之间线性得 y0→y1，区间外取端点）
    public double[] ScDd = [], ScDdLow = [], ScVol = [], ScClosePos = [], ScEr = [], ScR2 = [], ScSigma = [], ScStand = [], ScMacdBelow = [], ScMacdAbove = [], ScHist = [], ScReb = [], ScDev = [];
    public double ScDry, ScPull, ScMa20Up, ScMa60Flat, ScMacdNear, ScMacdNearLo, ScHistPts; public double Ma60FlatTol; public double[] ScMacd = []; public string ScoreScheme = "t37";

    private SrParams(BrConfig cfg)
    {
        Cfg = cfg;
        bool On(string sec, string k) => cfg.B($"{sec}.on.{k}", true);
        for (var j = 0; j < 7; j++) L0On[j] = On("universe", L0Keys[j]);
        MinListDays = cfg.I("universe.min_list_days", 250); MinAmt20 = cfg.D("universe.min_avg_amount_20", 5e7); MinCap = cfg.D("universe.min_market_cap", 3e9);
        MaxSusp20 = cfg.I("universe.max_suspend_days_20", 0); MaxOneWord20 = cfg.I("universe.max_one_word_limit_days_20", 2); MinPrice = cfg.D("universe.min_price", 2);
        BacktestUseCap = cfg.B("backtest.use_market_cap", false);
        ExcludeBoards = cfg.Node("universe.exclude_boards") is JsonArray eb ? eb.Select(x => x!.GetValue<string>()).ToArray() : [];

        for (var j = 0; j < 6; j++) L1On[j] = On("l1", L1Keys[j]);
        DdMin = cfg.D("l1.dd_min", 0.40); HighWin = cfg.I("l1.high_window", 250); LowWin = cfg.I("l1.low_window", 60); HighAgeMin = cfg.I("l1.high_to_now_min_days", 60);
        var dsl = cfg.Doubles("l1.days_since_low"); DslMin = dsl.Count > 0 ? (int)dsl[0] : 8; DslMax = dsl.Count > 1 ? (int)dsl[1] : 40;
        var reb = cfg.Doubles("l1.rebound_range"); RebMin = reb.Count > 0 ? reb[0] : 0.05; RebMax = reb.Count > 1 ? reb[1] : 0.30;
        LowLift = cfg.D("l1.low_lift", 1.02); LowLiftMode = cfg.S("l1.low_lift_mode", "all_after_low"); RangePosMax = cfg.D("l1.range_pos_max", 0.40);

        for (var j = 0; j < 6; j++) L2On[j] = On("l2", L2Keys[j]);
        VolMult = cfg.D("l2.vol_mult", 2.0); VmaWin = cfg.I("l2.vma_window", 20);
        var pr = cfg.Doubles("l2.pct_range"); PctMin = pr.Count > 0 ? pr[0] : 0.03; PctMax = pr.Count > 1 ? pr[1] : 0.095;
        ClosePosMin = cfg.D("l2.close_pos_min", 0.6); D0MaxAge = cfg.I("l2.d0_max_age", 20);
        DryWin = cfg.I("l2.bonus.dryup_window", 10); DryVmaWin = cfg.I("l2.bonus.dryup_vma", 60); DryRatio = cfg.D("l2.bonus.dryup_ratio", 0.6); PullTouch = cfg.D("l2.bonus.pullback_touch", 0.01);

        for (var j = 0; j < 9; j++) L3On[j] = On("l3", L3Keys[j]);
        L3Mode = cfg.S("l3.window_mode", "since_low"); L3Win = cfg.I("l3.window", 20); L3MinWin = cfg.I("l3.min_window", 5);
        BigMove = cfg.D("l3.big_move_pct", 0.06); BigMoveMax = cfg.I("l3.big_move_max_days", 1); SigmaMax = cfg.D("l3.sigma_max", 0.025); MaxDrop = cfg.D("l3.max_single_drop", -0.05);
        ErMin = cfg.D("l3.er_min", 0.30); R2Min = cfg.D("l3.r2_min", 0.50);
        var gr = cfg.Doubles("l3.gain_range"); GainMin = gr.Count > 0 ? gr[0] : 0.03; GainMax = gr.Count > 1 ? gr[1] : 0.25;
        UpRatio = cfg.D("l3.up_candle_ratio_min", 0.5); AboveRatio = cfg.D("l3.above_ma20_ratio_min", 0.6); AtrMax = cfg.D("l3.atr_ratio_max", 0.04);
        WatchMin = cfg.I("l3.watch_min_count", 6); BuyMin = cfg.I("l3.buy_min_count", 7);
        BuyMandatory = cfg.Node("l3.buy_mandatory") is JsonArray bm ? bm.Select(x => Array.IndexOf(L3Keys, x!.GetValue<string>())).Where(x => x >= 0).ToArray() : [0, 1, 2];

        for (var j = 0; j < 7; j++) L4On[j] = On("l4", L4Keys[j]);
        MaS = cfg.I("l4.ma_short", 10); MaL = cfg.I("l4.ma_long", 30); CrossLb = cfg.I("l4.cross_lookback", 5); MaLSlopeDays = cfg.I("l4.ma_long_slope_days", 3);
        var md = cfg.Doubles("l4.macd"); MacdF = md.Count > 0 ? (int)md[0] : 12; MacdSl = md.Count > 1 ? (int)md[1] : 26; MacdSig = md.Count > 2 ? (int)md[2] : 9;
        DifRatioMax = cfg.D("l4.macd_diff_ratio_max", 0.01); CrossGap = cfg.I("l4.cross_gap_max", 3);
        L4Mode = cfg.S("l4.mode", "ordered"); for (var j = 0; j < 3; j++) L4OOn[j] = cfg.B("l4." + L4OKeys[j], true);
        MacdFromD0Off = cfg.I("l4.macd_cross_from_d0_offset", -3); MaCrossMaxAge = cfg.I("l4.ma_cross_max_age", 3); RequireOrder = cfg.B("l4.require_order", true);

        WatchValid = cfg.I("watch.valid_days", 20); ValidFromD0 = cfg.I("watch.valid_days_from_d0", 30); RankBy = cfg.S("buy.rank_by", "er"); AllowSameDay = cfg.B("watch.allow_same_day_buy", true); RequireNoDouble = cfg.B("watch.require_no_double_cross", false);
        string[] bk = ["max_pct", "max_ma20_dev", "vma5", "no_limit_up_close", "score_gate"];
        for (var j = 0; j < 5; j++) BuyOn[j] = On("buy", bk[j]);
        BuyMaxPct = cfg.D("buy.max_pct", 0.07); BuyMaxDev = cfg.D("buy.max_ma20_dev", 0.12); Vma5Min = cfg.D("buy.vma5_vs_vma20_min", 0.8); ScoreMin = cfg.D("buy.score_min", 70);
        MktOn = cfg.B("buy.market_filter.enabled", true); MktLogic = cfg.S("buy.market_filter.logic", "or"); MktMa = cfg.I("buy.market_filter.ma", 60);
        MktRetDays = cfg.I("buy.market_filter.ret_days", 5); MktRet5Min = cfg.D("buy.market_filter.ret_min", -0.05);

        for (var j = 0; j < 8; j++) SOn[j] = On("exit", ExitKeys[j]);
        StopMode = cfg.S("exit.stop_mode", "lower_capped"); HardStop = cfg.D("exit.hard_stop", 0.08); MaxStop = cfg.D("exit.max_stop", 0.12); AtrMult = cfg.D("exit.atr_mult", 2.5); AtrCap = cfg.B("exit.atr_apply_cap", true);
        StructLowDays = cfg.I("exit.struct_low_days", 10); TrailAct = cfg.D("exit.trail_activate", 0.15); TrailDd = cfg.D("exit.trail_drawdown", 0.10);
        Ma20BreakDays = cfg.I("exit.ma20_break_days", 2); BigDropPct = cfg.D("exit.big_drop_pct", -0.05); BigDropVol = cfg.D("exit.big_drop_vol_mult", 2.0);
        TimeDays = cfg.I("exit.time_stop_days", 20); TimeMinGain = cfg.D("exit.time_stop_min_gain", 0.03);
        for (var j = 0; j < 4; j++) ROn[j] = On("reduce", ReduceKeys[j]);
        R1Dev = cfg.D("reduce.r1_ma20_dev", 0.25); R2Gain = cfg.D("reduce.r2_gain", 0.30); R2Vol = cfg.D("reduce.r2_vol_mult", 2.0); R2Shadow = cfg.D("reduce.r2_shadow_body", 2.0); R2PctMax = cfg.D("reduce.r2_pct_max", 0.01);
        R3Dif = cfg.D("reduce.r3_dif_ratio", 0.03); ReduceMode = cfg.S("reduce.mode", "sell_half"); SellFrac = cfg.D("reduce.sell_fraction", 0.5); R4Mode = cfg.S("reduce.r4_mode", "raise_stop"); S8IncludeR4 = cfg.B("reduce.s8_include_r4", false);

        Sizing = cfg.S("position.sizing", "risk_budget"); Capital = cfg.D("position.initial_capital", 1_000_000); RiskPer = cfg.D("position.risk_per_trade", 0.008);
        MaxPosPct = cfg.D("position.max_position_pct", 0.10); EqualPct = cfg.D("position.equal_weight_pct", 0.10); MaxPos = cfg.I("position.max_positions", 10); MaxNew = cfg.I("position.max_new_per_day", 3);
        IndCap = cfg.B("position.industry_cap_enabled", true); IndMax = cfg.I("position.industry_max", 3); Cooldown = cfg.I("position.cooldown_days", 10);
        WeakMode = cfg.S("position.weak_market_mode", "half"); WeakMa = cfg.I("position.weak_ma", 60); WeakSlope = cfg.I("position.weak_slope_days", 5); Lot = cfg.I("position.lot", 100);
        Comm = cfg.D("cost.commission", 0.00025); CommMin = cfg.D("cost.commission_min", 5); Stamp = cfg.D("cost.stamp_tax_sell", 0.0005); Slip = cfg.D("cost.slippage", 0.001); Transfer = cfg.D("cost.transfer_fee", 0);

        double[] A(string k, double[] def) { var l = cfg.Doubles("score." + k); return l.Count == 4 ? l.ToArray() : def; }
        ScDd = A("dd", [0.40, 0.70, 8, 15]); ScDdLow = A("dd_low", [0.20, 0.40, 0, 8]);
        ScVol = A("vol_ratio", [2.0, 4.0, 4, 8]); ScClosePos = A("close_pos", [0.6, 1.0, 2, 4]); ScDry = cfg.D("score.dryup_points", 4); ScPull = cfg.D("score.pullback_points", 4);
        ScEr = A("er", [0.30, 0.70, 3.5, 7]); ScR2 = A("r2", [0.50, 0.90, 3.5, 7]); ScSigma = A("sigma", [0.025, 0.010, 3, 6]);
        ScStand = A("stand_days", [0, 5, 3, 7]); ScMa20Up = cfg.D("score.ma20_up_points", 4); ScMa60Flat = cfg.D("score.ma60_flat_points", 4); Ma60FlatTol = cfg.D("score.ma60_flat_tol", 0.002);
        ScMacdNear = cfg.D("score.macd_near_points", 10); ScMacdNearLo = cfg.D("score.macd_near_low", -0.02);
        ScMacdBelow = A("macd_below", [-0.05, -0.02, 5, 10]); ScMacdAbove = A("macd_above", [0, 0.01, 10, 4]); ScHist = A("hist_days", [0, 3, 0, 5]);
        ScReb = A("rebound", [0.05, 0.30, 8, 4]); ScDev = A("ma20_dev", [0, 0.12, 7, 3.5]);
        ScMacd = A("macd_dif", [0.01, -0.03, 3, 9]); ScHistPts = cfg.D("score.hist_expand_points", 3); ScoreScheme = cfg.S("score.scheme", "t37");
    }

    public static SrParams Load(string dataDir, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("SR_CONFIG") is { Length: > 0 } env ? (Path.IsPathRooted(env) ? env : Path.Combine(dataDir, env)) : Path.Combine(dataDir, FileName);
        if (!File.Exists(path) && File.Exists(Path.Combine(dataDir, FileNameV1))) path = Path.Combine(dataDir, FileNameV1);
        return new(BrConfig.Load(dataDir, path));
    }
    public static SrParams From(BrConfig cfg) => new(cfg);
    public SrParams With(string path, JsonNode? v) => new(Cfg.With(path, v));

    public static double Lin(double x, double[] m)
    {
        double x0 = m[0], x1 = m[1], y0 = m[2], y1 = m[3];
        if (double.IsNaN(x)) return Math.Min(y0, y1);
        var t = (x - x0) / (x1 - x0);
        t = Math.Clamp(t, 0, 1);
        return y0 + (y1 - y0) * t;
    }

    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string F(double v, int nd = 2) => double.IsNaN(v) || double.IsInfinity(v) ? "" : Math.Round(v, nd, MidpointRounding.AwayFromZero).ToString("F" + nd, Inv);
    public static string P(double v, int nd = 1) => double.IsNaN(v) || double.IsInfinity(v) ? "-" : (v >= 0 ? "+" : "") + (v * 100).ToString("F" + nd, Inv) + "%";
    public static string Pu(double v, int nd = 1) => double.IsNaN(v) || double.IsInfinity(v) ? "-" : (v * 100).ToString("F" + nd, Inv) + "%";
}
