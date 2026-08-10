// =============================================================================
//  BACKTESTING MODULE - EquityCurveBuilder
// -----------------------------------------------------------------------------
//  Accumulates one EquityPoint (and one PortfolioSnapshot) per simulated
//  trading day and tracks the running peak/drawdown as it goes.
// =============================================================================
using System.Collections.Generic;

namespace NseMomentumScanner.Backtesting
{
    internal sealed class EquityCurveBuilder
    {
        public List<EquityPoint> Points { get; } = new();
        public List<PortfolioSnapshot> Snapshots { get; } = new();

        private decimal _peak = 0m;

        public void Record(System.DateTime date, decimal cash, decimal holdingsValue, int openPositions, double utilizationPct)
        {
            decimal equity = cash + holdingsValue;
            if (equity > _peak) _peak = equity;
            double drawdownPct = _peak > 0m ? (double)((_peak - equity) / _peak) * 100.0 : 0.0;

            Points.Add(new EquityPoint
            {
                Date = date,
                Equity = equity,
                PeakEquity = _peak,
                DrawdownPct = drawdownPct,
            });

            Snapshots.Add(new PortfolioSnapshot
            {
                Date = date,
                CashBalance = cash,
                HoldingsValue = holdingsValue,
                PortfolioValue = equity,
                OpenPositions = openPositions,
                CapitalUtilizationPct = utilizationPct,
            });
        }
    }
}
