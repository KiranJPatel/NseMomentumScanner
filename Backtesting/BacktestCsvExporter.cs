// =============================================================================
//  BACKTESTING MODULE - BacktestCsvExporter
// -----------------------------------------------------------------------------
//  Same CsvHelper-based approach as the scanner's own CsvExporter, writing
//  into a per-run subfolder under BacktestConfig.OutputDir so results from
//  different runs never overwrite each other.
// =============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using Serilog;

namespace NseMomentumScanner.Backtesting
{
    internal static class BacktestCsvExporter
    {
        private static readonly CsvConfiguration Cfg =
            new(CultureInfo.InvariantCulture) { HasHeaderRecord = true, NewLine = "\n" };

        private sealed record TradeRow(
            int TradeId, string Symbol, string EntryDate, decimal EntryPrice,
            string ExitDate, decimal ExitPrice, decimal Quantity, decimal StopLoss, decimal Target,
            string EntryReason, string ExitReason, decimal GrossProfit, decimal Charges,
            decimal NetProfit, double ReturnPct, int HoldingDays, double MfePct, double MaePct)
        {
            public TradeRow(BacktestTrade t) : this(
                t.TradeId, t.Symbol, t.EntryDate.ToString("yyyy-MM-dd"), t.EntryPrice,
                t.ExitDate.ToString("yyyy-MM-dd"), t.ExitPrice, t.Quantity, t.StopLoss, t.Target,
                t.EntryReason, t.ExitReason, t.GrossProfit, t.Charges, t.NetProfit, t.ReturnPct,
                t.HoldingDays, t.MaxFavorableExcursionPct, t.MaxAdverseExcursionPct)
            { }
        }

        private sealed record EquityRow(string Date, decimal Equity, decimal PeakEquity, double DrawdownPct)
        {
            public EquityRow(EquityPoint p) : this(p.Date.ToString("yyyy-MM-dd"), p.Equity, p.PeakEquity, p.DrawdownPct) { }
        }

        public static (string tradesPath, string equityPath) Write(BacktestRunResult run, string runFolder)
        {
            string tradesPath = string.Empty, equityPath = string.Empty;
            try
            {
                Directory.CreateDirectory(runFolder);

                tradesPath = Path.Combine(runFolder, "trades.csv");
                using (var w = new StreamWriter(tradesPath, false, System.Text.Encoding.UTF8))
                using (var c = new CsvWriter(w, Cfg))
                    c.WriteRecords(run.Trades.OrderBy(t => t.EntryDate).Select(t => new TradeRow(t)));

                equityPath = Path.Combine(runFolder, "equity_curve.csv");
                using (var w = new StreamWriter(equityPath, false, System.Text.Encoding.UTF8))
                using (var c = new CsvWriter(w, Cfg))
                    c.WriteRecords(run.EquityCurve.Select(p => new EquityRow(p)));
            }
            catch (Exception ex)
            {
                Log.Warning("Backtest CSV write error: {Msg}", ex.Message);
            }
            return (tradesPath, equityPath);
        }
    }
}
