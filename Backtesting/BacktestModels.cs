// =============================================================================
//  BACKTESTING MODULE · Domain Models
// -----------------------------------------------------------------------------
//  Pure data containers used across the backtesting engine. None of these types
//  duplicate or shadow the scanner's own domain models (ScanResult, PriceLevels,
//  SupertrendMetrics, etc.) — they only describe simulation state (positions,
//  trades, equity, performance) that the scanner has no concept of.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace NseMomentumScanner.Backtesting
{
    /// <summary>A currently-open simulated position.</summary>
    internal sealed class OpenPosition
    {
        public string   Symbol           { get; init; } = string.Empty;
        public DateTime EntryDate        { get; init; }
        public decimal  EntryPrice       { get; init; }
        public decimal  Quantity         { get; init; }
        public decimal  EntryCharges     { get; init; }
        public string   EntryReason      { get; init; } = string.Empty;

        public decimal  InitialStopLoss  { get; set; }
        public decimal  CurrentStopLoss  { get; set; }
        public decimal  Target           { get; set; }
        public decimal  EntryAtr         { get; set; }

        public decimal  HighestCloseSinceEntry { get; set; }
        public decimal  MaxFavorableExcursion  { get; set; } // best unrealized gain % seen
        public decimal  MaxAdverseExcursion    { get; set; } // worst unrealized loss % seen

        public decimal  CapitalDeployed  => EntryPrice * Quantity;
    }

    /// <summary>A completed round-trip trade — the trade-journal row.</summary>
    internal sealed class BacktestTrade
    {
        public int       TradeId          { get; set; }
        public string    Symbol           { get; set; } = string.Empty;
        public DateTime  EntryDate        { get; set; }
        public decimal   EntryPrice       { get; set; }
        public DateTime  ExitDate         { get; set; }
        public decimal   ExitPrice        { get; set; }
        public decimal   Quantity         { get; set; }
        public decimal   StopLoss         { get; set; }
        public decimal   Target           { get; set; }
        public string    EntryReason      { get; set; } = string.Empty;
        public string    ExitReason       { get; set; } = string.Empty;

        public decimal   GrossProfit      { get; set; }
        public decimal   Charges          { get; set; }
        public decimal   NetProfit        { get; set; }
        public double    ReturnPct        { get; set; }
        public int       HoldingDays      { get; set; }
        public double    MaxFavorableExcursionPct { get; set; }
        public double    MaxAdverseExcursionPct   { get; set; }
        public bool      IsWin            => NetProfit > 0;
    }

    /// <summary>Portfolio value snapshot recorded once per simulated trading day.</summary>
    internal sealed class PortfolioSnapshot
    {
        public DateTime Date              { get; set; }
        public decimal  CashBalance       { get; set; }
        public decimal  HoldingsValue     { get; set; }
        public decimal  PortfolioValue    { get; set; }
        public int      OpenPositions     { get; set; }
        public double   CapitalUtilizationPct { get; set; }
    }

    /// <summary>One point on the equity curve, including running drawdown.</summary>
    internal sealed class EquityPoint
    {
        public DateTime Date           { get; set; }
        public decimal  Equity         { get; set; }
        public decimal  PeakEquity     { get; set; }
        public double   DrawdownPct    { get; set; }
    }

    /// <summary>Fully computed performance-metrics bundle for one backtest run.</summary>
    internal sealed class PerformanceMetrics
    {
        public int      TotalTrades              { get; set; }
        public int      WinningTrades             { get; set; }
        public int      LosingTrades              { get; set; }
        public double   WinRatePct                { get; set; }
        public decimal  AverageWin                { get; set; }
        public decimal  AverageLoss               { get; set; }
        public decimal  LargestWin                { get; set; }
        public decimal  LargestLoss               { get; set; }
        public double   ProfitFactor              { get; set; }
        public double   AverageHoldingDays        { get; set; }
        public double   MaxDrawdownPct            { get; set; }
        public int      MaxConsecutiveWins        { get; set; }
        public int      MaxConsecutiveLosses      { get; set; }
        public double   SharpeRatio               { get; set; }
        public double   SortinoRatio              { get; set; }
        public double   CalmarRatio               { get; set; }
        public double   RecoveryFactor            { get; set; }
        public decimal  Expectancy                { get; set; }
        public decimal  NetProfit                 { get; set; }
        public double   ReturnPct                 { get; set; }
        public double   AnnualReturnPct           { get; set; }
        public double   AverageMonthlyReturnPct   { get; set; }
        public double   CagrPct                   { get; set; }
        public double   UlcerIndex                { get; set; }
        public double   MarRatio                  { get; set; }
        public decimal  TotalCharges              { get; set; }
        public decimal  FinalEquity               { get; set; }
        public decimal  InitialCapital            { get; set; }
    }

    /// <summary>Aggregated stats for one calendar bucket (month or year) used in the
    /// monthly/yearly returns tables.</summary>
    internal sealed class PeriodReturn
    {
        public string  PeriodLabel  { get; set; } = string.Empty; // "2024-03" or "2024"
        public decimal StartEquity  { get; set; }
        public decimal EndEquity    { get; set; }
        public double  ReturnPct    { get; set; }
        public int     Trades       { get; set; }
    }

    /// <summary>Everything needed to write the DB rows / CSV / JSON / HTML report for a run.</summary>
    internal sealed class BacktestRunResult
    {
        public int       RunId            { get; set; }
        public DateTime  RunTimestampUtc   { get; set; } = DateTime.UtcNow;
        public DateTime  StartDate         { get; set; }
        public DateTime  EndDate           { get; set; }
        public string    Universe          { get; set; } = string.Empty;
        public int       TickerCount       { get; set; }
        public int       TickersRequested  { get; set; }
        public List<string> Timeframes     { get; set; } = new();

        public List<BacktestTrade>      Trades          { get; set; } = new();
        public List<EquityPoint>        EquityCurve     { get; set; } = new();
        public List<PortfolioSnapshot>  DailySnapshots  { get; set; } = new();
        public PerformanceMetrics       Metrics         { get; set; } = new();
        public List<PeriodReturn>       MonthlyReturns  { get; set; } = new();
        public List<PeriodReturn>       YearlyReturns   { get; set; } = new();
        public Dictionary<string, string> Parameters    { get; set; } = new();

        /// <summary>Plain-language reliability notes computed for this specific run (e.g.
        /// survivorship bias in the chosen universe, a trade count too small to trust the
        /// ratio-based metrics, low data coverage) - surfaced in the console summary, the
        /// HTML report, the JSON report, and persisted to the database (as a JSON array
        /// under the "Caveats" key in the Parameters table) so a decision made from a saved
        /// report is never separated from the caveats that qualify it.</summary>
        public List<string> Caveats { get; set; } = new();

        public List<BacktestTrade> TopWinners(int n) => Trades.OrderByDescending(t => t.NetProfit).Take(n).ToList();
        public List<BacktestTrade> TopLosers(int n)  => Trades.OrderBy(t => t.NetProfit).Take(n).ToList();
    }
}
