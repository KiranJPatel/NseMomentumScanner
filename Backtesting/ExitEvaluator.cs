// =============================================================================
//  BACKTESTING MODULE - ExitEvaluator
// -----------------------------------------------------------------------------
//  Small, pure building blocks shared by BacktestEngine's two exit checks:
//    - Intrabar stop-loss/target (BacktestEngine Step B) uses the CURRENT
//      day's own High/Low - fully known once that bar closes, so this is
//      not look-ahead.
//    - Signal-based exits - Supertrend flip / HMA cross / time / the
//      scanner's own aggregated IMMEDIATE_SELL (BacktestEngine Step C) -
//      are decided from today's close/indicators but only ever EXECUTED at
//      tomorrow's open, which is where BacktestEngine applies the priority
//      order across both checks (capital protection first, then signals).
//  This file only holds the reusable pieces (stop/target initialization,
//  trailing-stop ratchet, MFE/MAE bookkeeping) - the day-by-day priority
//  logic itself lives in BacktestEngine so there is exactly one place that
//  decides "what wins if two conditions fire the same day".
// =============================================================================
using System;

namespace NseMomentumScanner.Backtesting
{
    internal static class ExitEvaluator
    {
        /// <summary>Computes the initial stop-loss and target for a brand-new position.
        /// ATR stop takes precedence over fixed-% stop when both are enabled; a
        /// Risk-Reward target (derived from the actual stop distance) takes precedence
        /// over a fixed-% target when both are enabled. Either can resolve to 0
        /// (disabled) if the relevant config flags are off.</summary>
        public static (decimal stop, decimal target) InitializeStops(decimal entryPrice, decimal atr)
        {
            decimal stop;
            if (BacktestConfig.UseAtrStop && atr > 0)
                stop = entryPrice - atr * (decimal)BacktestConfig.AtrStopMultiplier;
            else if (BacktestConfig.UseFixedPercentStop)
                stop = entryPrice * (1m - (decimal)(BacktestConfig.FixedStopPct / 100.0));
            else
                stop = 0m;
            stop = Math.Max(stop, 0m);

            decimal target;
            if (BacktestConfig.UseRiskReward && stop > 0m && entryPrice > stop)
            {
                decimal risk = entryPrice - stop;
                target = entryPrice + risk * (decimal)BacktestConfig.RiskRewardRatio;
            }
            else if (BacktestConfig.UseFixedPercentTarget)
                target = entryPrice * (1m + (decimal)(BacktestConfig.FixedTargetPct / 100.0));
            else
                target = 0m;

            return (stop, Math.Max(target, 0m));
        }

        /// <summary>Ratchets the trailing stop up (never down) based on the highest close
        /// seen since entry. No-op unless UseAtrTrailingStop is enabled.</summary>
        public static void UpdateTrailingStop(OpenPosition pos, decimal close, decimal atr)
        {
            if (close > pos.HighestCloseSinceEntry) pos.HighestCloseSinceEntry = close;
            if (!BacktestConfig.UseAtrTrailingStop || atr <= 0m) return;

            decimal candidate = pos.HighestCloseSinceEntry - atr * (decimal)BacktestConfig.AtrTrailMultiplier;
            if (candidate > pos.CurrentStopLoss) pos.CurrentStopLoss = candidate;
        }

        /// <summary>Updates the running maximum-favorable/adverse excursion (as % of entry
        /// price) using today's High/Low - used purely for the trade journal, not for
        /// exit decisions.</summary>
        public static void UpdateExcursion(OpenPosition pos, decimal high, decimal low)
        {
            if (pos.EntryPrice <= 0m) return;
            decimal favorable = (high - pos.EntryPrice) / pos.EntryPrice * 100m;
            decimal adverse   = (low  - pos.EntryPrice) / pos.EntryPrice * 100m;
            if (favorable > pos.MaxFavorableExcursion) pos.MaxFavorableExcursion = favorable;
            if (adverse   < pos.MaxAdverseExcursion)   pos.MaxAdverseExcursion   = adverse;
        }
    }
}
