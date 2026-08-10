// =============================================================================
//  BACKTESTING MODULE - BacktestMarket
// -----------------------------------------------------------------------------
//  Single source of truth for "which market are we backtesting" - derived
//  from BacktestConfig.Universe. Everything that differs by market (trading
//  calendar, cost model, currency display) branches off this one helper
//  instead of each place re-deriving it, so there is exactly one place that
//  decides "Nasdaq100/DowJones/USMarket => United States, everything else
//  (Nifty200/SMID/NewListings/All/Custom) => India" - Custom defaults to
//  India since that matches this project's origin and most Custom lists
//  will be NSE tickers, but this is a config choice, not a hard rule; if you
//  build a Custom list of US tickers, this is the one place to extend.
// =============================================================================
using System;

namespace NseMomentumScanner.Backtesting
{
    internal enum MarketRegion
    {
        India,
        UnitedStates,
    }

    internal static class BacktestMarket
    {
        public static MarketRegion RegionFor(string universe) =>
            universe.Trim().ToLowerInvariant() switch
            {
                "nasdaq100" => MarketRegion.UnitedStates,
                "dowjones" => MarketRegion.UnitedStates,
                "usmarket" => MarketRegion.UnitedStates,
                _ => MarketRegion.India,
            };

        public static MarketRegion CurrentRegion => RegionFor(BacktestConfig.Universe);

        public static string CurrencySymbol(MarketRegion region) =>
            region == MarketRegion.UnitedStates ? "$" : "\u20B9"; // $ or ₹ - for HTML/JSON/dashboard display

        /// <summary>ASCII-safe currency prefix for console/terminal output - the existing
        /// scanner console output deliberately used "Rs." rather than the Unicode ₹ symbol
        /// (terminal/encoding compatibility), so this preserves that choice rather than
        /// introducing Unicode into console output that wasn't there before.</summary>
        public static string ConsoleCurrencyPrefix(MarketRegion region) =>
            region == MarketRegion.UnitedStates ? "$" : "Rs.";

        public static string CurrencyCode(MarketRegion region) =>
            region == MarketRegion.UnitedStates ? "USD" : "INR";

        public static string MarketName(MarketRegion region) =>
            region == MarketRegion.UnitedStates ? "NYSE/Nasdaq" : "NSE";
    }
}
