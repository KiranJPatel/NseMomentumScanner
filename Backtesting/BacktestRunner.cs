// =============================================================================
//  BACKTESTING MODULE - BacktestRunner
// -----------------------------------------------------------------------------
//  Top-level orchestrator invoked from Program.cs for `--backtest` /
//  `--mode=backtest`. Runs the engine, persists results (DB/CSV/JSON/HTML per
//  BacktestConfig.Reports flags), and prints a console summary. This is the
//  ONLY new file Program.cs needs to call into.
// =============================================================================
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace NseMomentumScanner.Backtesting
{
    internal static class BacktestRunner
    {
        public static async Task RunAsync(CancellationToken ct = default)
        {
            BacktestConfig.Reload();

            if (!BacktestConfig.Enabled)
            {
                Console.WriteLine("Backtest is disabled (Backtest.Enabled=false in appsettings.json). Nothing to do.");
                return;
            }

            var configErrors = BacktestConfig.Validate();
            if (configErrors.Count > 0)
            {
                Console.WriteLine("Backtest configuration is invalid:");
                foreach (var e in configErrors) Console.WriteLine($"  - {e}");
                Console.WriteLine("Fix the \"Backtest\" section of appsettings.json and try again.");
                return;
            }

            var startRegion = BacktestMarket.RegionFor(BacktestConfig.Universe);
            string startCur = BacktestMarket.ConsoleCurrencyPrefix(startRegion);
            Console.WriteLine();
            Console.WriteLine("=====================================================================");
            Console.WriteLine("  NSE MOMENTUM SCANNER - BACKTEST MODE");
            Console.WriteLine("=====================================================================");
            Console.WriteLine($"  Universe   : {BacktestConfig.Universe}  ({BacktestMarket.MarketName(startRegion)})");
            Console.WriteLine($"  Period     : {BacktestConfig.StartDate:yyyy-MM-dd} -> {BacktestConfig.EndDate:yyyy-MM-dd}");
            Console.WriteLine($"  Capital    : {startCur}{BacktestConfig.InitialCapital:N0}");
            Console.WriteLine($"  Timeframes : {string.Join(", ", BacktestConfig.Timeframes)}");
            Console.WriteLine($"  Sizing     : {BacktestConfig.SizingMethod}");
            Console.WriteLine("=====================================================================");
            Console.WriteLine();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var engine = new BacktestEngine();
            BacktestRunResult run;
            try
            {
                run = await engine.RunAsync(ct);
            }
            catch (Exception ex)
            {
                Log.Error("Backtest run failed: {Msg}", ex.Message);
                Console.WriteLine($"Backtest failed: {ex.Message}");
                return;
            }
            sw.Stop();

            if (BacktestConfig.WriteToDatabase)
            {
                try
                {
                    await using var repo = new BacktestRepository();
                    await repo.InitializeAsync();
                    await repo.SaveRunAsync(run);
                }
                catch (Exception ex)
                {
                    Log.Warning("Could not persist backtest run to database: {Msg}", ex.Message);
                }
            }

            string runFolder = Path.Combine(BacktestConfig.OutputDir,
                $"run_{run.StartDate:yyyyMMdd}_{run.EndDate:yyyyMMdd}_{DateTime.Now:yyyyMMdd_HHmmss}");

            string? htmlPath = null;
            if (BacktestConfig.GenerateHtml) htmlPath = BacktestHtmlReport.Write(run, runFolder);
            if (BacktestConfig.GenerateCsv) BacktestCsvExporter.Write(run, runFolder);
            if (BacktestConfig.GenerateJson) WriteJsonReport(run, runFolder);

            PrintSummary(run, sw.Elapsed, htmlPath, runFolder);
        }

        private static void WriteJsonReport(BacktestRunResult run, string runFolder)
        {
            try
            {
                Directory.CreateDirectory(runFolder);
                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                };
                var payload = new
                {
                    run.RunId,
                    run.RunTimestampUtc,
                    StartDate = run.StartDate.ToString("yyyy-MM-dd"),
                    EndDate = run.EndDate.ToString("yyyy-MM-dd"),
                    run.Universe,
                    run.TickerCount,
                    run.TickersRequested,
                    run.Timeframes,
                    run.Metrics,
                    run.Caveats,
                    run.MonthlyReturns,
                    run.YearlyReturns,
                    run.Trades,
                    run.Parameters,
                    EquityCurve = run.EquityCurve.Select(p => new
                    {
                        Date = p.Date.ToString("yyyy-MM-dd"),
                        p.Equity,
                        p.DrawdownPct,
                    }),
                };
                File.WriteAllText(Path.Combine(runFolder, "report.json"), JsonSerializer.Serialize(payload, opts));
            }
            catch (Exception ex)
            {
                Log.Warning("Backtest JSON report error: {Msg}", ex.Message);
            }
        }

        private static void PrintSummary(BacktestRunResult run, TimeSpan elapsed, string? htmlPath, string runFolder)
        {
            var m = run.Metrics;
            string cur = BacktestMarket.ConsoleCurrencyPrefix(BacktestMarket.RegionFor(run.Universe));
            Console.WriteLine();
            Console.WriteLine("------------------------------- RESULTS ----------------------------");
            Console.WriteLine($"  Trades           : {m.TotalTrades}  (W:{m.WinningTrades} / L:{m.LosingTrades}, win rate {m.WinRatePct:F1}%)");
            Console.WriteLine($"  Net Profit       : {cur}{m.NetProfit:N0}   ({m.ReturnPct:F2}%)");
            Console.WriteLine($"  CAGR / Annual    : {m.CagrPct:F2}%  /  {m.AnnualReturnPct:F2}%");
            Console.WriteLine($"  Max Drawdown     : {m.MaxDrawdownPct:F2}%");
            Console.WriteLine($"  Sharpe / Sortino : {m.SharpeRatio:F2} / {m.SortinoRatio:F2}");
            Console.WriteLine($"  Profit Factor    : {(double.IsInfinity(m.ProfitFactor) ? "inf" : m.ProfitFactor.ToString("F2"))}");
            Console.WriteLine($"  Expectancy/Trade : {cur}{m.Expectancy:N0}");
            Console.WriteLine($"  Total Charges    : {cur}{m.TotalCharges:N0}");
            Console.WriteLine($"  Elapsed          : {elapsed:mm\\:ss}");
            Console.WriteLine($"  Reports          : {runFolder}/");
            if (!string.IsNullOrEmpty(htmlPath)) Console.WriteLine($"                     {htmlPath}");
            Console.WriteLine("---------------------------------------------------------------------");
            Console.WriteLine();

            if (run.Caveats.Count > 0)
            {
                Console.WriteLine("NOTES & CAVEATS - read before trusting these numbers:");
                foreach (var note in run.Caveats)
                    Console.WriteLine($"  * {Wrap(note, 90, "    ")}");
                Console.WriteLine();
            }
        }

        /// <summary>Wraps a long caveat sentence to a readable console width, indenting
        /// continuation lines so multi-line bullets stay visually grouped.</summary>
        private static string Wrap(string text, int width, string indent)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder();
            int lineLen = 0;
            foreach (var w in words)
            {
                if (lineLen > 0 && lineLen + 1 + w.Length > width)
                {
                    sb.Append('\n').Append(indent);
                    lineLen = 0;
                }
                if (lineLen > 0) { sb.Append(' '); lineLen++; }
                sb.Append(w);
                lineLen += w.Length;
            }
            return sb.ToString();
        }
    }
}
