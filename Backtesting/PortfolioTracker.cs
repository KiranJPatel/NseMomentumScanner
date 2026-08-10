// =============================================================================
//  BACKTESTING MODULE - PortfolioTracker
// -----------------------------------------------------------------------------
//  Owns cash balance and the set of currently-open positions. Enforces the
//  configurable capital-simulation limits (max concurrent positions, max
//  allocation per stock, max total portfolio exposure) - it does not decide
//  WHICH stock to buy (that is the engine's job, driven by the scanner's own
//  ranked signals), only whether/how much room is left in the budget.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace NseMomentumScanner.Backtesting
{
    internal sealed class PortfolioTracker
    {
        public decimal CashBalance { get; private set; }
        public Dictionary<string, OpenPosition> Positions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public PortfolioTracker(decimal initialCapital)
        {
            CashBalance = initialCapital;
        }

        public int OpenPositionCount => Positions.Count;

        /// <summary>Mark-to-market value of all open positions at today's close prices.</summary>
        public decimal HoldingsValue(Func<string, decimal> lastClose) =>
            Positions.Values.Sum(p => p.Quantity * lastClose(p.Symbol));

        public decimal TotalEquity(Func<string, decimal> lastClose) => CashBalance + HoldingsValue(lastClose);

        /// <summary>Maximum cash this engine is willing to commit to a *new* trade in
        /// <paramref name="symbol"/> right now, given available cash, the per-stock
        /// allocation cap, the portfolio exposure cap, and the max-positions cap.
        /// Returns 0 if no room is left (e.g. max positions already reached).</summary>
        public decimal AvailableBudgetForNewTrade(string symbol, Func<string, decimal> lastClose)
        {
            if (Positions.ContainsKey(symbol)) return 0m; // one position per symbol at a time
            if (Positions.Count >= BacktestConfig.MaxPositions) return 0m;

            decimal equity = TotalEquity(lastClose);
            decimal perStockCap = equity * (decimal)(BacktestConfig.MaxAllocationPerStockPct / 100.0);
            decimal exposureCap = equity * (decimal)(BacktestConfig.MaxPortfolioExposurePct / 100.0);
            decimal currentExposure = HoldingsValue(lastClose);
            decimal remainingExposureRoom = Math.Max(0m, exposureCap - currentExposure);

            decimal budget = Math.Min(CashBalance, Math.Min(perStockCap, remainingExposureRoom));
            return Math.Max(0m, budget);
        }

        public void Open(OpenPosition position, decimal cashOutflow)
        {
            CashBalance -= cashOutflow;
            Positions[position.Symbol] = position;
        }

        public OpenPosition? Close(string symbol, decimal cashInflow)
        {
            if (!Positions.Remove(symbol, out var pos)) return null;
            CashBalance += cashInflow;
            return pos;
        }

        public double CapitalUtilizationPct(Func<string, decimal> lastClose)
        {
            decimal equity = TotalEquity(lastClose);
            if (equity <= 0m) return 0.0;
            return (double)(HoldingsValue(lastClose) / equity) * 100.0;
        }
    }
}
