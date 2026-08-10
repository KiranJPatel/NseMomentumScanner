// =============================================================================
//  BACKTESTING MODULE - PositionSizer
// -----------------------------------------------------------------------------
//  Computes the quantity to buy for a new position under each configurable
//  sizing method. Never allocates more than the caller's available cash for
//  the trade (the caller passes in whatever cash/exposure budget already
//  accounts for MaxPositions / MaxAllocationPerStockPct / MaxPortfolioExposurePct).
// =============================================================================
using System;

namespace NseMomentumScanner.Backtesting
{
    internal static class PositionSizer
    {
        /// <summary>
        /// Returns the quantity (whole shares, floor) to buy.
        /// </summary>
        /// <param name="price">Planned entry (fill) price.</param>
        /// <param name="budgetForThisTrade">Cash ceiling already narrowed down by the caller
        /// (respecting available cash, per-stock allocation cap, and portfolio exposure cap).</param>
        /// <param name="atr">Current ATR for the symbol (used only by AtrBased sizing).</param>
        /// <param name="totalEquity">Current total portfolio equity (used by FixedPercentage /
        /// AtrBased / KellyFraction sizing, which size relative to overall capital, not just
        /// the per-trade budget).</param>
        /// <param name="winRateEstimate">Optional running win-rate estimate (0..1) for the
        /// Kelly-fraction placeholder. Defaults to 0.5 (i.e. no edge assumed) when unknown.</param>
        /// <param name="avgWinLossRatio">Optional running average-win/average-loss ratio for the
        /// Kelly-fraction placeholder. Defaults to 1.0 when unknown.</param>
        public static decimal ComputeQuantity(
            decimal price,
            decimal budgetForThisTrade,
            decimal atr,
            decimal totalEquity,
            double winRateEstimate = 0.5,
            double avgWinLossRatio = 1.0)
        {
            if (price <= 0m || budgetForThisTrade <= 0m) return 0m;

            decimal rawQty = BacktestConfig.SizingMethod switch
            {
                PositionSizingMethod.FixedQuantity =>
                    BacktestConfig.FixedQuantity,

                PositionSizingMethod.FixedCapital =>
                    Math.Floor(Math.Min(BacktestConfig.FixedCapitalPerTrade, budgetForThisTrade) / price),

                PositionSizingMethod.FixedPercentage =>
                    Math.Floor(Math.Min(totalEquity * (decimal)(BacktestConfig.PercentageOfCapital / 100.0),
                                         budgetForThisTrade) / price),

                PositionSizingMethod.AtrBased =>
                    ComputeAtrBasedQuantity(price, atr, totalEquity, budgetForThisTrade),

                // Kelly fraction: f* = W - (1-W)/R, clamped to [0, KellyFractionCap] of equity.
                // This is a simplified, optional placeholder (per the spec) - it is NOT a
                // rigorous Kelly implementation and should be treated as illustrative only.
                PositionSizingMethod.KellyFraction =>
                    ComputeKellyQuantity(price, totalEquity, budgetForThisTrade, winRateEstimate, avgWinLossRatio),

                _ => Math.Floor(budgetForThisTrade / price),
            };

            decimal maxAffordable = Math.Floor(budgetForThisTrade / price);
            return Math.Max(0m, Math.Min(rawQty, maxAffordable));
        }

        private static decimal ComputeAtrBasedQuantity(
            decimal price, decimal atr, decimal totalEquity, decimal budgetForThisTrade)
        {
            if (atr <= 0m) return Math.Floor(budgetForThisTrade / price); // no ATR yet - fall back
            decimal riskBudget  = totalEquity * (decimal)(BacktestConfig.AtrRiskPerTradePct / 100.0);
            decimal riskPerShare = atr * (decimal)BacktestConfig.AtrStopMultiplier;
            if (riskPerShare <= 0m) return Math.Floor(budgetForThisTrade / price);
            return Math.Floor(Math.Min(riskBudget / riskPerShare, budgetForThisTrade / price));
        }

        private static decimal ComputeKellyQuantity(
            decimal price, decimal totalEquity, decimal budgetForThisTrade,
            double winRateEstimate, double avgWinLossRatio)
        {
            double w = Math.Clamp(winRateEstimate, 0.0, 1.0);
            double r = avgWinLossRatio <= 0 ? 1.0 : avgWinLossRatio;
            double kelly = w - (1.0 - w) / r;
            double fraction = Math.Clamp(kelly, 0.0, BacktestConfig.KellyFractionCap);
            decimal capital = totalEquity * (decimal)fraction;
            return Math.Floor(Math.Min(capital, budgetForThisTrade) / price);
        }
    }
}
