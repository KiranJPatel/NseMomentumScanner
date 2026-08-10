// =============================================================================
//  BACKTESTING MODULE · BacktestSettings
// -----------------------------------------------------------------------------
//  Reads the "Backtest" section of appsettings.json (completely independent of
//  the existing AppSettings/Config classes — nothing here touches scanner
//  configuration). Falls back to sane compile-time defaults when a field is
//  missing, exactly like the existing Config class does for the scanner.
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NseMomentumScanner.Backtesting
{
    // ── Enums ────────────────────────────────────────────────────────────────
    public enum PositionSizingMethod
    {
        FixedQuantity,
        FixedCapital,
        FixedPercentage,
        AtrBased,
        KellyFraction
    }

    public enum ExitReason
    {
        None,
        StopLoss,
        AtrStop,
        TrailingStop,
        Target,
        RiskReward,
        SupertrendFlip,
        HmaCrossDown,
        TimeExit,
        SignalSell,
        EndOfBacktest
    }

    /// <summary>Raw JSON-bindable DTO for the "Backtest" appsettings.json section. All nullable
    /// so we can tell "not provided" apart from "explicitly zero/false".</summary>
    internal sealed class BacktestJsonSettings
    {
        public bool?   Enabled                    { get; set; }
        public string? StartDate                  { get; set; }
        public string? EndDate                     { get; set; }
        public decimal? InitialCapital             { get; set; }
        public string? Universe                    { get; set; }
        public List<string>? CustomTickers         { get; set; }
        public List<string>? Timeframes            { get; set; }

        public int?    MaxPositions                { get; set; }
        public double? MaxAllocationPerStockPct    { get; set; }
        public double? MaxPortfolioExposurePct     { get; set; }
        public int?    MinRankForEntry             { get; set; }

        // Position sizing
        public string? PositionSizingMethod        { get; set; }
        public decimal? FixedQuantity              { get; set; }
        public decimal? FixedCapitalPerTrade       { get; set; }
        public double? PercentageOfCapital         { get; set; }
        public double? AtrRiskPerTradePct          { get; set; }
        public int?    AtrPeriod                   { get; set; }
        public double? KellyFractionCap             { get; set; }

        // Exits
        public bool?   UseAtrStop                  { get; set; }
        public double? AtrStopMultiplier            { get; set; }
        public bool?   UseAtrTrailingStop           { get; set; }
        public double? AtrTrailMultiplier           { get; set; }
        public bool?   UseFixedPercentStop          { get; set; }
        public double? FixedStopPct                 { get; set; }
        public bool?   UseFixedPercentTarget        { get; set; }
        public double? FixedTargetPct               { get; set; }
        public bool?   UseRiskReward                { get; set; }
        public double? RiskRewardRatio              { get; set; }
        public bool?   UseTimeExit                  { get; set; }
        public int?    MaxHoldingDays               { get; set; }
        public bool?   UseSupertrendExit            { get; set; }
        public bool?   UseHmaExit                   { get; set; }
        public bool?   ExitOnImmediateSell          { get; set; }

        // Costs (all percentages are in *percent*, e.g. 0.03 == 0.03%)
        public decimal? BrokerageFlatPerOrder       { get; set; }
        public double? BrokeragePct                 { get; set; }
        public decimal? BrokerageMaxPerOrder        { get; set; }
        public double? SttBuyPct                    { get; set; }
        public double? SttSellPct                   { get; set; }
        public double? ExchangeTxnChargePct         { get; set; }
        public double? GstPct                       { get; set; }
        public double? StampDutyBuyPct              { get; set; }
        public double? SebiChargesPct               { get; set; }
        public double? SlippagePct                  { get; set; }
        public double? SpreadPct                    { get; set; }

        // US-market costs (only used when the selected Universe resolves to
        // MarketRegion.UnitedStates - see BacktestMarket). India-specific fields above are
        // untouched and remain the defaults for every existing NSE universe.
        public decimal? UsCommissionPerOrder        { get; set; }
        public double?  UsSecFeeSellPct             { get; set; }
        public decimal? UsFinraTafPerShare          { get; set; }
        public decimal? UsFinraTafCapPerOrder       { get; set; }

        // Reports
        public bool?   GenerateHtml                 { get; set; }
        public bool?   GenerateCsv                  { get; set; }
        public bool?   GenerateJson                 { get; set; }
        public bool?   WriteToDatabase              { get; set; }
        public string? OutputDir                    { get; set; }

        // Historical-data cache (backtest-only, separate from the scanner's own cache)
        public bool?   CacheEnabled                 { get; set; }
        public double? CacheHours                   { get; set; }

        private const string FileName = "appsettings.json";

        public static BacktestJsonSettings Load()
        {
            if (!File.Exists(FileName)) return new BacktestJsonSettings();
            try
            {
                var json = File.ReadAllText(FileName);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Backtest", out var section))
                    return new BacktestJsonSettings();

                return JsonSerializer.Deserialize<BacktestJsonSettings>(section.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new BacktestJsonSettings();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Could not parse Backtest section of {FileName}: {ex.Message}");
                return new BacktestJsonSettings();
            }
        }
    }

    /// <summary>Resolved, ready-to-use backtest configuration — compile-time defaults, all
    /// overridable via appsettings.json → "Backtest" section. Mirrors the existing scanner's
    /// AppSettings→Config pattern.</summary>
    internal static class BacktestConfig
    {
        private static BacktestJsonSettings _s = new();
        public static void ApplySettings(BacktestJsonSettings s) => _s = s;
        public static void Reload() => _s = BacktestJsonSettings.Load();

        public static bool     Enabled                 => _s.Enabled ?? true;
        public static DateTime StartDate                => ParseDate(_s.StartDate, DateTime.Today.AddYears(-3));
        public static DateTime EndDate                   => ParseDate(_s.EndDate, DateTime.Today);
        public static decimal  InitialCapital           => _s.InitialCapital ?? 1_000_000m;
        public static string   Universe                  => _s.Universe ?? "Nifty200";
        public static IReadOnlyList<string> CustomTickers => _s.CustomTickers ?? new List<string>();
        public static IReadOnlyList<string> Timeframes    => (_s.Timeframes is { Count: > 0 })
            ? _s.Timeframes
            : new List<string> { "1d" };

        public static int      MaxPositions              => _s.MaxPositions ?? 10;
        public static double   MaxAllocationPerStockPct   => _s.MaxAllocationPerStockPct ?? 15.0;
        public static double   MaxPortfolioExposurePct    => _s.MaxPortfolioExposurePct ?? 95.0;
        public static int      MinRankForEntry            => _s.MinRankForEntry ?? 9999; // effectively unlimited by default

        // ── Position sizing ────────────────────────────────────────────────
        public static PositionSizingMethod SizingMethod => Enum.TryParse<PositionSizingMethod>(
            _s.PositionSizingMethod, true, out var m) ? m : PositionSizingMethod.FixedPercentage;
        public static decimal  FixedQuantity            => _s.FixedQuantity ?? 10m;
        public static decimal  FixedCapitalPerTrade     => _s.FixedCapitalPerTrade ?? 50_000m;
        public static double   PercentageOfCapital      => _s.PercentageOfCapital ?? 10.0;
        public static double   AtrRiskPerTradePct       => _s.AtrRiskPerTradePct ?? 1.0;
        public static int      AtrPeriod                => Math.Max(2, _s.AtrPeriod ?? 14);
        public static double   KellyFractionCap         => _s.KellyFractionCap ?? 0.25;

        // ── Exits ─────────────────────────────────────────────────────────
        public static bool     UseAtrStop               => _s.UseAtrStop ?? true;
        public static double   AtrStopMultiplier         => _s.AtrStopMultiplier ?? 2.5;
        public static bool     UseAtrTrailingStop        => _s.UseAtrTrailingStop ?? false;
        public static double   AtrTrailMultiplier        => _s.AtrTrailMultiplier ?? 3.0;
        public static bool     UseFixedPercentStop       => _s.UseFixedPercentStop ?? false;
        public static double   FixedStopPct              => _s.FixedStopPct ?? 8.0;
        public static bool     UseFixedPercentTarget     => _s.UseFixedPercentTarget ?? false;
        public static double   FixedTargetPct            => _s.FixedTargetPct ?? 20.0;
        public static bool     UseRiskReward             => _s.UseRiskReward ?? true;
        public static double   RiskRewardRatio           => _s.RiskRewardRatio ?? 2.5;
        public static bool     UseTimeExit               => _s.UseTimeExit ?? true;
        public static int      MaxHoldingDays            => _s.MaxHoldingDays ?? 60;
        public static bool     UseSupertrendExit         => _s.UseSupertrendExit ?? true;
        public static bool     UseHmaExit                => _s.UseHmaExit ?? false;
        public static bool     ExitOnImmediateSell       => _s.ExitOnImmediateSell ?? true;

        // ── Costs ─────────────────────────────────────────────────────────
        // NOTE: these are configurable estimates for simulation purposes only — not a
        // guarantee of actual brokerage/tax rates. Verify current rates with your broker.
        public static decimal  BrokerageFlatPerOrder     => _s.BrokerageFlatPerOrder ?? 20m;
        public static double   BrokeragePct              => _s.BrokeragePct ?? 0.03;
        public static decimal  BrokerageMaxPerOrder      => _s.BrokerageMaxPerOrder ?? 20m;
        public static double   SttBuyPct                 => _s.SttBuyPct ?? 0.1;
        public static double   SttSellPct                => _s.SttSellPct ?? 0.1;
        public static double   ExchangeTxnChargePct      => _s.ExchangeTxnChargePct ?? 0.00297;
        public static double   GstPct                    => _s.GstPct ?? 18.0;
        public static double   StampDutyBuyPct           => _s.StampDutyBuyPct ?? 0.015;
        public static double   SebiChargesPct            => _s.SebiChargesPct ?? 0.0001;
        public static double   SlippagePct               => _s.SlippagePct ?? 0.05;
        public static double   SpreadPct                 => _s.SpreadPct ?? 0.02;

        // ── US-market costs (see MarketRegion / BacktestMarket) ──────────────
        // Defaults reflect a typical modern US discount broker: zero commission on
        // equities, plus the two small regulatory pass-through fees that apply to every
        // SELL order regardless of broker (SEC Section 31 fee and the FINRA Trading
        // Activity Fee). These are approximate/configurable, same disclaimer as the India
        // rates above - verify current rates before drawing real conclusions.
        public static decimal  UsCommissionPerOrder      => _s.UsCommissionPerOrder ?? 0m;
        public static double   UsSecFeeSellPct            => _s.UsSecFeeSellPct ?? 0.0008;
        public static decimal  UsFinraTafPerShare         => _s.UsFinraTafPerShare ?? 0.000166m;
        public static decimal  UsFinraTafCapPerOrder      => _s.UsFinraTafCapPerOrder ?? 8.30m;

        // ── Reports ───────────────────────────────────────────────────────
        public static bool     GenerateHtml              => _s.GenerateHtml ?? true;
        public static bool     GenerateCsv               => _s.GenerateCsv ?? true;
        public static bool     GenerateJson              => _s.GenerateJson ?? true;
        public static bool     WriteToDatabase            => _s.WriteToDatabase ?? true;
        public static string   OutputDir                  => _s.OutputDir ?? "backtest_reports";

        // ── Historical-data cache ────────────────────────────────────────
        // The scanner's own YahooDataFetcher cache deliberately excludes daily/intraday
        // bars (correct for a *live* scanner - "always fetch live" is the point). A
        // backtest is the opposite use case: the same multi-year daily history gets
        // re-requested every time a run is re-tried with different stop/target/sizing
        // settings, so it benefits from a much longer-lived cache. This is a separate,
        // backtest-only disk cache (BacktestDataCache) - it never touches the scanner's
        // own .cache/yahoo/ directory or its cache semantics.
        public static bool     CacheEnabled               => _s.CacheEnabled ?? true;
        public static double   CacheHours                 => _s.CacheHours ?? 12.0;

        private static DateTime ParseDate(string? s, DateTime fallback) =>
            !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, out var d) ? d.Date : fallback.Date;

        /// <summary>Fails fast with a clear message instead of letting a nonsensical config
        /// (zero capital, zero positions, inverted dates, etc.) silently produce a hollow or
        /// crashing backtest deep inside the engine.</summary>
        public static List<string> Validate()
        {
            var errors = new List<string>();
            if (InitialCapital <= 0m) errors.Add($"InitialCapital must be > 0 (got {InitialCapital}).");
            if (MaxPositions <= 0) errors.Add($"MaxPositions must be >= 1 (got {MaxPositions}).");
            if (MaxAllocationPerStockPct <= 0) errors.Add($"MaxAllocationPerStockPct must be > 0 (got {MaxAllocationPerStockPct}).");
            if (MaxPortfolioExposurePct <= 0) errors.Add($"MaxPortfolioExposurePct must be > 0 (got {MaxPortfolioExposurePct}).");
            if (StartDate >= EndDate) errors.Add($"StartDate ({StartDate:yyyy-MM-dd}) must be before EndDate ({EndDate:yyyy-MM-dd}).");
            if (Universe.Trim().Equals("custom", StringComparison.OrdinalIgnoreCase) && CustomTickers.Count == 0)
                errors.Add("Universe is \"Custom\" but CustomTickers is empty.");
            return errors;
        }

        /// <summary>Snapshot of the resolved config for the Parameters table / JSON report,
        /// so every run is reproducible from its own report.</summary>
        public static Dictionary<string, string> Snapshot() => new()
        {
            ["StartDate"] = StartDate.ToString("yyyy-MM-dd"),
            ["EndDate"] = EndDate.ToString("yyyy-MM-dd"),
            ["InitialCapital"] = InitialCapital.ToString("F2"),
            ["Universe"] = Universe,
            ["Timeframes"] = string.Join(",", Timeframes),
            ["MaxPositions"] = MaxPositions.ToString(),
            ["MaxAllocationPerStockPct"] = MaxAllocationPerStockPct.ToString("F2"),
            ["MaxPortfolioExposurePct"] = MaxPortfolioExposurePct.ToString("F2"),
            ["MinRankForEntry"] = MinRankForEntry.ToString(),
            ["SizingMethod"] = SizingMethod.ToString(),
            ["FixedQuantity"] = FixedQuantity.ToString("F2"),
            ["FixedCapitalPerTrade"] = FixedCapitalPerTrade.ToString("F2"),
            ["PercentageOfCapital"] = PercentageOfCapital.ToString("F2"),
            ["AtrRiskPerTradePct"] = AtrRiskPerTradePct.ToString("F2"),
            ["AtrPeriod"] = AtrPeriod.ToString(),
            ["UseAtrStop"] = UseAtrStop.ToString(),
            ["AtrStopMultiplier"] = AtrStopMultiplier.ToString("F2"),
            ["UseAtrTrailingStop"] = UseAtrTrailingStop.ToString(),
            ["AtrTrailMultiplier"] = AtrTrailMultiplier.ToString("F2"),
            ["UseFixedPercentStop"] = UseFixedPercentStop.ToString(),
            ["FixedStopPct"] = FixedStopPct.ToString("F2"),
            ["UseFixedPercentTarget"] = UseFixedPercentTarget.ToString(),
            ["FixedTargetPct"] = FixedTargetPct.ToString("F2"),
            ["UseRiskReward"] = UseRiskReward.ToString(),
            ["RiskRewardRatio"] = RiskRewardRatio.ToString("F2"),
            ["UseTimeExit"] = UseTimeExit.ToString(),
            ["MaxHoldingDays"] = MaxHoldingDays.ToString(),
            ["UseSupertrendExit"] = UseSupertrendExit.ToString(),
            ["UseHmaExit"] = UseHmaExit.ToString(),
            ["ExitOnImmediateSell"] = ExitOnImmediateSell.ToString(),
            ["BrokerageFlatPerOrder"] = BrokerageFlatPerOrder.ToString("F2"),
            ["BrokeragePct"] = BrokeragePct.ToString("F4"),
            ["SttBuyPct"] = SttBuyPct.ToString("F4"),
            ["SttSellPct"] = SttSellPct.ToString("F4"),
            ["ExchangeTxnChargePct"] = ExchangeTxnChargePct.ToString("F5"),
            ["GstPct"] = GstPct.ToString("F2"),
            ["StampDutyBuyPct"] = StampDutyBuyPct.ToString("F4"),
            ["SebiChargesPct"] = SebiChargesPct.ToString("F5"),
            ["SlippagePct"] = SlippagePct.ToString("F4"),
            ["SpreadPct"] = SpreadPct.ToString("F4"),
            ["MarketRegion"] = BacktestMarket.RegionFor(Universe).ToString(),
            ["UsCommissionPerOrder"] = UsCommissionPerOrder.ToString("F2"),
            ["UsSecFeeSellPct"] = UsSecFeeSellPct.ToString("F5"),
            ["UsFinraTafPerShare"] = UsFinraTafPerShare.ToString("F6"),
            ["UsFinraTafCapPerOrder"] = UsFinraTafCapPerOrder.ToString("F2"),
            ["CacheEnabled"] = CacheEnabled.ToString(),
            ["CacheHours"] = CacheHours.ToString("F1"),
        };
    }
}
