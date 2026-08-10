// =============================================================================
//  BACKTESTING MODULE - TradeSimulator
// -----------------------------------------------------------------------------
//  Bridges PortfolioTracker (capital/exposure bookkeeping), PositionSizer
//  (how much to buy), CostCalculator (fees/slippage) and ExitEvaluator
//  (stop/target math) into two simple operations the engine drives day by
//  day: TryOpen(...) and Close(...). All money actually moves through
//  PortfolioTracker so cash/exposure limits stay consistent everywhere.
// =============================================================================
using System;
using System.Collections.Generic;

namespace NseMomentumScanner.Backtesting
{
    internal sealed class TradeSimulator
    {
        private readonly PortfolioTracker _portfolio;
        private int _nextTradeId = 1;

        public List<BacktestTrade> ClosedTrades { get; } = new();

        public TradeSimulator(PortfolioTracker portfolio) => _portfolio = portfolio;

        /// <summary>Attempts to open a new long position in <paramref name="symbol"/>.
        /// Returns false (no-op, no cash moved) if there is no budget room, the sized
        /// quantity rounds to zero, or fees would exceed available cash.</summary>
        public bool TryOpen(
            string symbol, DateTime date, decimal rawPrice, decimal atr,
            Func<string, decimal> lastClose, string entryReason)
        {
            decimal budget = _portfolio.AvailableBudgetForNewTrade(symbol, lastClose);
            if (budget <= 0m) return false;

            decimal fillPrice = CostCalculator.ApplySlippageAndSpread(rawPrice, isBuy: true);
            if (fillPrice <= 0m) return false;

            decimal equity = _portfolio.TotalEquity(lastClose);
            decimal qty = PositionSizer.ComputeQuantity(fillPrice, budget, atr, equity);
            if (qty <= 0m) return false;

            var costs = CostCalculator.Calculate(fillPrice, qty, isBuy: true);
            decimal cashOutflow = fillPrice * qty + costs.Total;

            // Back off quantity by 1 share at a time if fees push us over available cash
            // (rare, but possible near the cash/exposure ceiling).
            while (qty > 0m && cashOutflow > _portfolio.CashBalance)
            {
                qty -= 1m;
                if (qty <= 0m) return false;
                costs = CostCalculator.Calculate(fillPrice, qty, isBuy: true);
                cashOutflow = fillPrice * qty + costs.Total;
            }

            var (stop, target) = ExitEvaluator.InitializeStops(fillPrice, atr);
            var pos = new OpenPosition
            {
                Symbol = symbol,
                EntryDate = date,
                EntryPrice = fillPrice,
                Quantity = qty,
                EntryCharges = costs.Total,
                EntryReason = entryReason,
                InitialStopLoss = stop,
                CurrentStopLoss = stop,
                Target = target,
                EntryAtr = atr,
                HighestCloseSinceEntry = fillPrice,
                MaxFavorableExcursion = 0m,
                MaxAdverseExcursion = 0m,
            };
            _portfolio.Open(pos, cashOutflow);
            return true;
        }

        /// <summary>Closes an already-open position and records the completed trade.
        /// The position must already have been removed from the caller's iteration set;
        /// this method itself removes it from the PortfolioTracker.</summary>
        public BacktestTrade Close(OpenPosition pos, DateTime date, decimal rawExitPrice, ExitReason reason)
        {
            decimal fillPrice = CostCalculator.ApplySlippageAndSpread(rawExitPrice, isBuy: false);
            var costs = CostCalculator.Calculate(fillPrice, pos.Quantity, isBuy: false);
            decimal cashInflow = fillPrice * pos.Quantity - costs.Total;
            _portfolio.Close(pos.Symbol, cashInflow);

            decimal grossProfit  = (fillPrice - pos.EntryPrice) * pos.Quantity;
            decimal totalCharges = pos.EntryCharges + costs.Total;
            decimal netProfit    = grossProfit - totalCharges;
            double  returnPct    = pos.EntryPrice > 0m
                ? (double)((fillPrice - pos.EntryPrice) / pos.EntryPrice) * 100.0
                : 0.0;

            var trade = new BacktestTrade
            {
                TradeId       = _nextTradeId++,
                Symbol        = pos.Symbol,
                EntryDate     = pos.EntryDate,
                EntryPrice    = pos.EntryPrice,
                ExitDate      = date,
                ExitPrice     = fillPrice,
                Quantity      = pos.Quantity,
                StopLoss      = pos.InitialStopLoss,
                Target        = pos.Target,
                EntryReason   = pos.EntryReason,
                ExitReason    = reason.ToString(),
                GrossProfit   = grossProfit,
                Charges       = totalCharges,
                NetProfit     = netProfit,
                ReturnPct     = returnPct,
                HoldingDays   = Math.Max(0, (date - pos.EntryDate).Days),
                MaxFavorableExcursionPct = (double)pos.MaxFavorableExcursion,
                MaxAdverseExcursionPct   = (double)pos.MaxAdverseExcursion,
            };
            ClosedTrades.Add(trade);
            return trade;
        }
    }
}
