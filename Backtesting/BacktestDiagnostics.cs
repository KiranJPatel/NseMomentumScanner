// =============================================================================
//  BACKTESTING MODULE - BacktestDiagnostics
// -----------------------------------------------------------------------------
//  Computes plain-language reliability caveats for one run. These exist
//  because a backtest can be methodologically clean (no look-ahead bias in
//  the fill/exit timing, no look-ahead in the scanner's own indicator/VCP/
//  price-level calculators - all verified strictly backward-looking) and
//  still mislead someone using it to make a real decision, for reasons that
//  have nothing to do with a coding bug:
//    - Survivorship bias in a "current constituents" universe list
//    - A trade count too small for ratio-based metrics to mean anything
//    - Silently low data coverage across the requested universe
//    - Intraday timeframes that only actually cover a fraction of a long run
//  Surfaced in the console summary, the HTML report, the JSON report, and
//  persisted to the database, so the caveats travel with the numbers rather
//  than living only in a README someone may never open.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace NseMomentumScanner.Backtesting
{
    internal static class BacktestDiagnostics
    {
        private static readonly HashSet<string> IntradayLabels =
            new(StringComparer.OrdinalIgnoreCase) { "1h", "60m", "30m", "15m", "5m" };

        public static List<string> BuildCaveats(
            string universe,
            int tickersRequested,
            int tickersUsable,
            int totalTrades,
            List<string> timeframes,
            DateTime startDate,
            DateTime endDate)
        {
            var notes = new List<string>();

            if (IsBuiltInIndexUniverse(universe))
            {
                notes.Add(
                    $"Survivorship bias: \"{universe}\" is today's constituent list, applied " +
                    $"retroactively across {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}. Stocks that " +
                    "were delisted, went bankrupt, or fell out of the index during that period are NOT " +
                    "included, while stocks that only qualified for the index recently ARE included for " +
                    "the full period. This tends to inflate backtested returns relative to what an " +
                    "investor using that index in real time would actually have experienced.");
            }

            if (totalTrades == 0)
            {
                notes.Add(
                    "Zero trades were executed - the equity curve is flat because no BUY signal ever " +
                    "both met the entry criteria and had cash/position-slot room available. Check " +
                    "MinRankForEntry, MaxPositions, MaxAllocationPerStockPct, and the configured " +
                    "Timeframes/Universe before concluding the strategy itself performed poorly.");
            }
            else if (totalTrades < 30)
            {
                notes.Add(
                    $"Small sample size: only {totalTrades} completed trade(s). Win rate, profit " +
                    "factor, Sharpe/Sortino, and other ratio-based metrics are not statistically " +
                    "reliable below roughly 30 trades - treat them as directional, not conclusive.");
            }

            if (tickersRequested > 0)
            {
                double coveragePct = (double)tickersUsable / tickersRequested * 100.0;
                if (coveragePct < 90.0)
                {
                    notes.Add(
                        $"Data coverage: only {tickersUsable} of {tickersRequested} requested tickers " +
                        $"({coveragePct:F0}%) had usable historical data and were included in this run; " +
                        "the rest were skipped (recent IPOs with too little history and Yahoo fetch " +
                        "failures are the two most common reasons - see the run log for specifics).");
                }
            }

            var intraday = timeframes.Where(t => IntradayLabels.Contains(t)).ToList();
            double totalYears = (endDate - startDate).TotalDays / 365.25;
            if (intraday.Count > 0 && totalYears > 1.1)
            {
                notes.Add(
                    $"Intraday timeframe(s) {string.Join(", ", intraday)} were included, but Yahoo only " +
                    $"retains roughly 60 days to 2 years of intraday history regardless of the backtest's " +
                    $"start date - across this {totalYears:F1}-year run they will have contributed " +
                    "nothing to scoring for the earlier stretch (the aggregator degrades gracefully to " +
                    "the remaining timeframes when one has no data for a given date, so this doesn't " +
                    "break the run - it just means intraday signals only apply near the end).");
            }

            notes.Add(
                "General: this is one historical simulation over one date range with one set of " +
                "parameters. Re-tuning stop-loss/target/position-sizing repeatedly against the same " +
                "historical window and keeping the best-looking result risks overfitting to noise " +
                "specific to that period - treat a strong result as a hypothesis worth validating on a " +
                "different or later period, not as a guarantee.");

            var region = BacktestMarket.RegionFor(universe);
            if (region == MarketRegion.UnitedStates)
            {
                notes.Add(
                    $"Market/cost model: this run used the NYSE/Nasdaq trading calendar and a simplified " +
                    "US cost model (a configurable flat commission, defaulting to $0 to reflect a typical " +
                    "modern zero-commission broker, plus the small SEC Section 31 fee and FINRA Trading " +
                    "Activity Fee). It does NOT include the India-specific STT/GST/stamp-duty/SEBI charges " +
                    "used for NSE universes - those don't apply to US equities. Initial Capital is " +
                    "interpreted in USD for this run (there's a single InitialCapital field either way, so " +
                    "double-check its value makes sense for the currency of the universe you selected).");
            }

            return notes;
        }

        private static bool IsBuiltInIndexUniverse(string universe) =>
            universe.Trim().ToLowerInvariant() is
                "nifty200" or "smid" or "niftysmid" or "newlistings" or "all" or
                "nasdaq100" or "dowjones" or "usmarket";
    }
}
