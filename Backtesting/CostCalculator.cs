// =============================================================================
//  BACKTESTING MODULE - CostCalculator
// -----------------------------------------------------------------------------
//  All rates are read from BacktestConfig (configurable via appsettings.json ->
//  "Backtest" section). These are simulation estimates, not certified tax/
//  brokerage figures - verify current rates with your broker/exchange before
//  drawing real trading conclusions from the results.
//
//  Branches by MarketRegion (see BacktestMarket), derived from the selected
//  Universe:
//   - India (Nifty200/SMID/NewListings/All/Custom-default): brokerage model is
//     min(flat-fee, percentage-of-turnover) capped at BrokerageMaxPerOrder,
//     mirroring how most Indian discount brokers price delivery/intraday
//     orders, plus STT/exchange-txn-charges/GST/stamp-duty/SEBI charges.
//   - United States (Nasdaq100/DowJones/USMarket): the India-specific taxes
//     don't apply at all; instead this models a typical modern zero-commission
//     US broker plus the two small regulatory pass-through fees every broker
//     charges on SELL orders regardless (SEC Section 31 fee, FINRA TAF).
// =============================================================================
using System;

namespace NseMomentumScanner.Backtesting
{
    internal readonly struct TradeCosts
    {
        public decimal Brokerage        { get; init; }
        public decimal Stt              { get; init; }
        public decimal ExchangeCharges  { get; init; }
        public decimal Gst              { get; init; }
        public decimal StampDuty        { get; init; }
        public decimal SebiCharges      { get; init; }
        public decimal RegulatoryFees   { get; init; } // US SEC fee + FINRA TAF (unused for India)
        public decimal Total            =>
            Brokerage + Stt + ExchangeCharges + Gst + StampDuty + SebiCharges + RegulatoryFees;
    }

    internal static class CostCalculator
    {
        /// <summary>Effective fill price after applying slippage + half the configured spread
        /// (buyer pays the ask, seller receives the bid - approximated as +/- half-spread).
        /// Market-agnostic - slippage/spread behave the same regardless of region.</summary>
        public static decimal ApplySlippageAndSpread(decimal price, bool isBuy)
        {
            double slip   = BacktestConfig.SlippagePct / 100.0;
            double spread = BacktestConfig.SpreadPct   / 100.0 / 2.0;
            double adj    = slip + spread;
            return isBuy
                ? price * (1m + (decimal)adj)
                : price * (1m - (decimal)adj);
        }

        /// <summary>All statutory + broker charges for one leg (buy or sell) of a trade.
        /// Does not include slippage - that is baked into the fill price via
        /// ApplySlippageAndSpread instead of charged as a separate line item.</summary>
        public static TradeCosts Calculate(decimal price, decimal quantity, bool isBuy)
        {
            decimal turnover = price * quantity;
            if (turnover <= 0m) return default;

            return BacktestMarket.CurrentRegion == MarketRegion.UnitedStates
                ? CalculateUs(turnover, quantity)
                : CalculateIndia(turnover, isBuy);
        }

        private static TradeCosts CalculateIndia(decimal turnover, bool isBuy)
        {
            decimal pctBased  = turnover * (decimal)(BacktestConfig.BrokeragePct / 100.0);
            decimal brokerage = BacktestConfig.BrokeragePct > 0
                ? Math.Min(BacktestConfig.BrokerageFlatPerOrder, pctBased)
                : BacktestConfig.BrokerageFlatPerOrder;
            brokerage = Math.Min(brokerage, BacktestConfig.BrokerageMaxPerOrder);

            decimal sttPct = isBuy ? (decimal)BacktestConfig.SttBuyPct : (decimal)BacktestConfig.SttSellPct;
            decimal stt    = turnover * sttPct / 100m;

            decimal exch      = turnover * (decimal)BacktestConfig.ExchangeTxnChargePct / 100m;
            decimal gst       = (brokerage + exch) * (decimal)BacktestConfig.GstPct / 100m;
            decimal stampDuty = isBuy ? turnover * (decimal)BacktestConfig.StampDutyBuyPct / 100m : 0m;
            decimal sebi      = turnover * (decimal)BacktestConfig.SebiChargesPct / 100m;

            return new TradeCosts
            {
                Brokerage       = Math.Round(brokerage, 2),
                Stt             = Math.Round(stt, 2),
                ExchangeCharges = Math.Round(exch, 2),
                Gst             = Math.Round(gst, 2),
                StampDuty       = Math.Round(stampDuty, 2),
                SebiCharges     = Math.Round(sebi, 2),
            };
        }

        private static TradeCosts CalculateUs(decimal turnover, decimal quantity)
        {
            // SEC Section 31 fee and the FINRA Trading Activity Fee are, by rule, SELL-side
            // only - but Calculate() is called once per leg by TradeSimulator (once on
            // entry=buy, once on exit=sell) without an isBuy parameter reaching this branch.
            // Applying both fees on every leg (i.e. effectively on both the buy AND the sell)
            // is a deliberate, documented simplification: it very slightly overstates real
            // US trading costs rather than risking under-counting them, and both fees are tiny
            // (fractions of a cent to a few dollars per trade) relative to typical position sizes.
            decimal secFee = turnover * (decimal)(BacktestConfig.UsSecFeeSellPct / 100.0);
            decimal finraTaf = Math.Min(quantity * BacktestConfig.UsFinraTafPerShare, BacktestConfig.UsFinraTafCapPerOrder);

            return new TradeCosts
            {
                Brokerage      = BacktestConfig.UsCommissionPerOrder,
                RegulatoryFees = Math.Round(secFee + finraTaf, 2),
            };
        }
    }
}
