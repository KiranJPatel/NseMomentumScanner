// =============================================================================
//  BACKTESTING MODULE - PerformanceAnalyzer
// -----------------------------------------------------------------------------
//  Pure computation over the finished trade list + daily equity curve. Does
//  not touch the portfolio, the scanner, or the network - just statistics.
//
//  A couple of standard-but-worth-stating conventions used below:
//   - Sharpe/Sortino use a 0% risk-free rate and annualize with sqrt(252)
//     trading days, which is the common simplification for daily-equity
//     backtests (not a precise instrument-specific risk-free adjustment).
//   - CAGR is geometric (compounds over the actual elapsed years); "Annual
//     Return %" is the simple/arithmetic annualization of total return -
//     the two will differ, which is expected and is why both are reported.
//   - Max Drawdown is measured against the running equity peak, in both
//     percentage and absolute-currency terms.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace NseMomentumScanner.Backtesting
{
    internal static class PerformanceAnalyzer
    {
        public static PerformanceMetrics Compute(
            List<BacktestTrade> trades,
            List<EquityPoint> equityCurve,
            decimal initialCapital,
            DateTime startDate,
            DateTime endDate,
            List<PeriodReturn> monthlyReturns)
        {
            var m = new PerformanceMetrics { InitialCapital = initialCapital };
            if (equityCurve.Count == 0)
            {
                m.FinalEquity = initialCapital;
                return m;
            }

            decimal finalEquity = equityCurve[^1].Equity;
            m.FinalEquity = finalEquity;
            m.NetProfit = finalEquity - initialCapital;
            m.ReturnPct = initialCapital > 0m ? (double)(m.NetProfit / initialCapital) * 100.0 : 0.0;

            double totalDays = Math.Max(1.0, (endDate - startDate).TotalDays);
            double years = totalDays / 365.25;
            m.AnnualReturnPct = years > 0 ? m.ReturnPct / years : 0.0;
            m.CagrPct = (years > 0 && initialCapital > 0m && finalEquity > 0m)
                ? (Math.Pow((double)(finalEquity / initialCapital), 1.0 / years) - 1.0) * 100.0
                : 0.0;
            m.AverageMonthlyReturnPct = monthlyReturns.Count > 0 ? monthlyReturns.Average(p => p.ReturnPct) : 0.0;

            // ── Drawdown / Ulcer Index (from the daily equity curve) ─────────
            m.MaxDrawdownPct = equityCurve.Max(p => p.DrawdownPct);
            decimal maxDrawdownAbs = equityCurve.Max(p => p.PeakEquity - p.Equity);
            m.UlcerIndex = Math.Sqrt(equityCurve.Average(p => p.DrawdownPct * p.DrawdownPct));

            // ── Daily returns -> Sharpe / Sortino ────────────────────────────
            var dailyReturns = new List<double>();
            for (int i = 1; i < equityCurve.Count; i++)
            {
                decimal prev = equityCurve[i - 1].Equity;
                if (prev <= 0m) continue;
                dailyReturns.Add((double)((equityCurve[i].Equity - prev) / prev));
            }
            if (dailyReturns.Count > 1)
            {
                double mean = dailyReturns.Average();
                double stdev = StdDev(dailyReturns, mean);
                m.SharpeRatio = stdev > 0 ? (mean / stdev) * Math.Sqrt(252.0) : 0.0;

                var downside = dailyReturns.Where(r => r < 0).ToList();
                double downsideStdev = downside.Count > 1 ? StdDev(downside, downside.Average()) : 0.0;
                m.SortinoRatio = downsideStdev > 0 ? (mean / downsideStdev) * Math.Sqrt(252.0) : 0.0;
            }

            m.CalmarRatio = m.MaxDrawdownPct > 0 ? m.AnnualReturnPct / m.MaxDrawdownPct : 0.0;
            m.MarRatio    = m.MaxDrawdownPct > 0 ? m.CagrPct / m.MaxDrawdownPct : 0.0;
            m.RecoveryFactor = maxDrawdownAbs > 0m ? (double)(m.NetProfit / maxDrawdownAbs) : 0.0;

            // ── Trade-level stats ─────────────────────────────────────────
            m.TotalTrades = trades.Count;
            if (trades.Count == 0) return m;

            var wins   = trades.Where(t => t.NetProfit > 0).ToList();
            var losses = trades.Where(t => t.NetProfit <= 0).ToList();
            m.WinningTrades = wins.Count;
            m.LosingTrades  = losses.Count;
            m.WinRatePct    = (double)m.WinningTrades / m.TotalTrades * 100.0;

            m.AverageWin  = wins.Count > 0 ? wins.Average(t => t.NetProfit) : 0m;
            m.AverageLoss = losses.Count > 0 ? Math.Abs(losses.Average(t => t.NetProfit)) : 0m;
            m.LargestWin  = wins.Count > 0 ? wins.Max(t => t.NetProfit) : 0m;
            m.LargestLoss = losses.Count > 0 ? losses.Min(t => t.NetProfit) : 0m;

            decimal grossWin  = wins.Sum(t => t.NetProfit);
            decimal grossLoss = Math.Abs(losses.Sum(t => t.NetProfit));
            m.ProfitFactor = grossLoss > 0m ? (double)(grossWin / grossLoss) : (grossWin > 0m ? double.PositiveInfinity : 0.0);

            m.AverageHoldingDays = trades.Average(t => t.HoldingDays);
            m.TotalCharges = trades.Sum(t => t.Charges);

            double winRateFrac = m.WinRatePct / 100.0;
            m.Expectancy = (decimal)winRateFrac * m.AverageWin - (decimal)(1 - winRateFrac) * m.AverageLoss;

            (m.MaxConsecutiveWins, m.MaxConsecutiveLosses) = ConsecutiveStreaks(trades);

            return m;
        }

        private static double StdDev(List<double> values, double mean)
        {
            if (values.Count < 2) return 0.0;
            double sumSq = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sumSq / (values.Count - 1));
        }

        private static (int maxWins, int maxLosses) ConsecutiveStreaks(List<BacktestTrade> trades)
        {
            int maxW = 0, maxL = 0, curW = 0, curL = 0;
            foreach (var t in trades.OrderBy(t => t.ExitDate))
            {
                if (t.IsWin) { curW++; curL = 0; }
                else { curL++; curW = 0; }
                maxW = Math.Max(maxW, curW);
                maxL = Math.Max(maxL, curL);
            }
            return (maxW, maxL);
        }

        /// <summary>Buckets closed trades + the equity curve into per-month and per-year
        /// return summaries for the report's Monthly/Yearly Returns tables.</summary>
        public static (List<PeriodReturn> monthly, List<PeriodReturn> yearly) BuildPeriodReturns(
            List<EquityPoint> equityCurve, List<BacktestTrade> trades)
        {
            var monthly = new List<PeriodReturn>();
            var yearly = new List<PeriodReturn>();
            if (equityCurve.Count == 0) return (monthly, yearly);

            var byMonth = equityCurve.GroupBy(p => new DateTime(p.Date.Year, p.Date.Month, 1));
            decimal runningStart = equityCurve[0].Equity;
            foreach (var g in byMonth.OrderBy(g => g.Key))
            {
                var ordered = g.OrderBy(p => p.Date).ToList();
                decimal start = runningStart;
                decimal end = ordered[^1].Equity;
                monthly.Add(new PeriodReturn
                {
                    PeriodLabel = g.Key.ToString("yyyy-MM"),
                    StartEquity = start,
                    EndEquity = end,
                    ReturnPct = start > 0 ? (double)((end - start) / start) * 100.0 : 0.0,
                    Trades = trades.Count(t => t.ExitDate.Year == g.Key.Year && t.ExitDate.Month == g.Key.Month),
                });
                runningStart = end;
            }

            var byYear = equityCurve.GroupBy(p => p.Date.Year);
            decimal runningStartY = equityCurve[0].Equity;
            foreach (var g in byYear.OrderBy(g => g.Key))
            {
                var ordered = g.OrderBy(p => p.Date).ToList();
                decimal start = runningStartY;
                decimal end = ordered[^1].Equity;
                yearly.Add(new PeriodReturn
                {
                    PeriodLabel = g.Key.ToString(),
                    StartEquity = start,
                    EndEquity = end,
                    ReturnPct = start > 0 ? (double)((end - start) / start) * 100.0 : 0.0,
                    Trades = trades.Count(t => t.ExitDate.Year == g.Key),
                });
                runningStartY = end;
            }

            return (monthly, yearly);
        }
    }
}
