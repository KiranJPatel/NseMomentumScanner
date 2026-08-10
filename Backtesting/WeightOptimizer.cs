// =============================================================================
//  BACKTESTING MODULE - WeightOptimizer  (v4.1)
// -----------------------------------------------------------------------------
//  Closes the loop the original architecture review flagged: the backtest engine
//  existed, but nothing ever fed its results back into the live scoring weights
//  (Config.VcpWeight etc. were hand-tuned constants, disconnected from any
//  measured performance). This runs a random search over weight combinations,
//  backtests each one with the EXISTING BacktestEngine/PerformanceAnalyzer
//  (identical trade simulation, costs, and look-ahead-bias discipline as
//  `--backtest`), and writes the best-performing set to optimized_weights.json
//  for Config.TryLoadOptimizedWeightsFromDisk() to pick up.
//
//  Cost note: BacktestDataCache means only the FIRST iteration pays for a full
//  historical fetch of the universe - every subsequent iteration re-scores and
//  re-simulates against already-cached daily bars, so iteration count mostly
//  costs CPU time, not network/rate-limit risk.
//
//  This is a search, not an optimizer in the mathematical sense: 6 weights has
//  no tractable closed-form optimum given a discontinuous, path-dependent
//  objective (trade simulation with stops/targets/costs), so random search
//  over the simplex is the pragmatic choice - the same approach a quant would
//  reach for before something heavier (Bayesian optimization, CMA-ES) is
//  justified by iteration budget.
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace NseMomentumScanner.Backtesting
{
    internal sealed class WeightCandidate
    {
        public double VcpWeight;
        public double VolumeWeight;
        public double TrendAlignmentWeight;
        public double SupertrendWeight;
        public double HmaCrossoverWeight;
        public double AdxWeight;
        public bool   IsBaseline;

        public OptimizedWeights ToOptimizedWeights(string objective) => new()
        {
            VcpWeight = VcpWeight, VolumeWeight = VolumeWeight,
            TrendAlignmentWeight = TrendAlignmentWeight, SupertrendWeight = SupertrendWeight,
            HmaCrossoverWeight = HmaCrossoverWeight, AdxWeight = AdxWeight,
            GeneratedAtUtc = DateTime.UtcNow, Objective = objective,
        };
    }

    internal sealed class WeightSearchResult
    {
        public WeightCandidate    Candidate = null!;
        public PerformanceMetrics Metrics   = null!;
        public double             Score;
        public bool               Disqualified;
        public string             DisqualifyReason = string.Empty;
    }

    internal static class WeightOptimizer
    {
        // Baseline = the current hardcoded defaults, always evaluated first (iteration 0) so
        // every search has a same-conditions comparison point, not just a list of unranked runs.
        private static WeightCandidate Baseline => new()
        {
            VcpWeight = 25, VolumeWeight = 20, TrendAlignmentWeight = 20,
            SupertrendWeight = 15, HmaCrossoverWeight = 10, AdxWeight = 10,
            IsBaseline = true,
        };

        public static async Task RunAsync(
            int iterations, string objective, double maxDrawdownCapPct,
            int minTradesForConfidence, int? seed, string outputPath, CancellationToken ct)
        {
            BacktestConfig.Reload();
            if (!BacktestConfig.Enabled)
            {
                Console.WriteLine("Weight optimizer needs Backtest.Enabled=true in appsettings.json.");
                return;
            }
            var configErrors = BacktestConfig.Validate();
            if (configErrors.Count > 0)
            {
                Console.WriteLine("Backtest configuration is invalid:");
                foreach (var e in configErrors) Console.WriteLine($"  - {e}");
                return;
            }
            objective = objective.Trim().ToLowerInvariant();
            if (objective is not ("sharpe" or "cagr" or "calmar"))
            {
                Console.WriteLine($"Unknown objective '{objective}' — use sharpe, cagr, or calmar.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("=====================================================================");
            Console.WriteLine("  NSE MOMENTUM SCANNER — WEIGHT OPTIMIZER");
            Console.WriteLine("=====================================================================");
            Console.WriteLine($"  Universe    : {BacktestConfig.Universe}");
            Console.WriteLine($"  Period      : {BacktestConfig.StartDate:yyyy-MM-dd} -> {BacktestConfig.EndDate:yyyy-MM-dd}");
            Console.WriteLine($"  Iterations  : {iterations} (+ 1 baseline)");
            Console.WriteLine($"  Objective   : {objective}");
            Console.WriteLine($"  Max drawdown cap : {maxDrawdownCapPct:F0}%  |  Min trades : {minTradesForConfidence}");
            Console.WriteLine("  Note: only the first iteration re-fetches history; the rest reuse");
            Console.WriteLine("  the backtest data cache and cost mainly CPU time, not network calls.");
            Console.WriteLine("=====================================================================");
            Console.WriteLine();

            var rng = seed.HasValue ? new Random(seed.Value) : new Random();
            var candidates = new List<WeightCandidate> { Baseline };
            for (int i = 0; i < iterations; i++)
                candidates.Add(RandomCandidate(rng));

            var results = new List<WeightSearchResult>(candidates.Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cand = candidates[i];
                string tag = cand.IsBaseline ? "baseline" : $"#{i}";
                Console.Write($"  [{i + 1,3}/{candidates.Count}] {tag,-9} " +
                    $"VCP={cand.VcpWeight,5:F1} Vol={cand.VolumeWeight,5:F1} Trend={cand.TrendAlignmentWeight,5:F1} " +
                    $"ST={cand.SupertrendWeight,5:F1} HMA={cand.HmaCrossoverWeight,5:F1} ADX={cand.AdxWeight,5:F1}  ... ");

                Config.ApplyOptimizedWeights(cand.ToOptimizedWeights(objective));
                BacktestRunResult run;
                try
                {
                    run = await new BacktestEngine().RunAsync(ct);
                }
                catch (OperationCanceledException) { Config.ApplyOptimizedWeights(null); throw; }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAILED ({ex.Message})");
                    Log.Warning("Weight optimizer: iteration {I} failed: {Msg}", i, ex.Message);
                    continue;
                }
                finally { Config.ApplyOptimizedWeights(null); }

                var m = run.Metrics;
                bool disqualified = false;
                string reason = string.Empty;
                if (m.TotalTrades < minTradesForConfidence)
                {
                    disqualified = true;
                    reason = $"only {m.TotalTrades} trades (< {minTradesForConfidence})";
                }
                else if (Math.Abs(m.MaxDrawdownPct) > maxDrawdownCapPct)
                {
                    disqualified = true;
                    reason = $"drawdown {m.MaxDrawdownPct:F1}% exceeds cap {maxDrawdownCapPct:F0}%";
                }

                double score = objective switch
                {
                    "cagr"   => m.CagrPct,
                    "calmar" => m.CalmarRatio,
                    _        => m.SharpeRatio,
                };
                if (disqualified) score = double.NegativeInfinity;

                results.Add(new WeightSearchResult
                {
                    Candidate = cand, Metrics = m, Score = score,
                    Disqualified = disqualified, DisqualifyReason = reason,
                });

                Console.WriteLine(disqualified
                    ? $"disqualified ({reason})"
                    : $"Sharpe={m.SharpeRatio:F2} CAGR={m.CagrPct:F1}% MaxDD={m.MaxDrawdownPct:F1}% " +
                      $"Trades={m.TotalTrades} WinRate={m.WinRatePct:F0}%");
            }
            sw.Stop();

            var baselineResult = results.FirstOrDefault(r => r.Candidate.IsBaseline);
            var ranked = results.Where(r => !r.Disqualified).OrderByDescending(r => r.Score).ToList();

            Console.WriteLine();
            Console.WriteLine($"  Completed {results.Count}/{candidates.Count} runs in {sw.Elapsed:hh\\:mm\\:ss}.");
            Console.WriteLine();

            if (ranked.Count == 0)
            {
                Console.WriteLine("  No candidate cleared the trade-count/drawdown filters — nothing to write.");
                Console.WriteLine("  Try more iterations, a longer backtest period, or relaxing --max-drawdown.");
                return;
            }

            Console.WriteLine("  Top 5 candidates:");
            Console.WriteLine("  " + new string('─', 90));
            foreach (var r in ranked.Take(5))
            {
                string tag = r.Candidate.IsBaseline ? "[baseline]" : "";
                Console.WriteLine($"  Score={r.Score,7:F2}  Sharpe={r.Metrics.SharpeRatio,5:F2}  " +
                    $"CAGR={r.Metrics.CagrPct,6:F1}%  MaxDD={r.Metrics.MaxDrawdownPct,6:F1}%  " +
                    $"Trades={r.Metrics.TotalTrades,4}  " +
                    $"[VCP={r.Candidate.VcpWeight:F1} Vol={r.Candidate.VolumeWeight:F1} " +
                    $"Trend={r.Candidate.TrendAlignmentWeight:F1} ST={r.Candidate.SupertrendWeight:F1} " +
                    $"HMA={r.Candidate.HmaCrossoverWeight:F1} ADX={r.Candidate.AdxWeight:F1}] {tag}");
            }
            Console.WriteLine();

            var winner = ranked[0];
            if (winner.Candidate.IsBaseline)
            {
                Console.WriteLine("  The baseline (current default) weights won this search — nothing to");
                Console.WriteLine("  improve on with this iteration budget/period. Not writing a weights file");
                Console.WriteLine("  (an existing one, if any, is left untouched).");
                return;
            }

            var winningWeights = winner.Candidate.ToOptimizedWeights(objective);
            winningWeights.IterationsRun = candidates.Count;
            winningWeights.BacktestStartDate = BacktestConfig.StartDate;
            winningWeights.BacktestEndDate = BacktestConfig.EndDate;
            winningWeights.Universe = BacktestConfig.Universe;
            winningWeights.SharpeRatio = winner.Metrics.SharpeRatio;
            winningWeights.CagrPct = winner.Metrics.CagrPct;
            winningWeights.MaxDrawdownPct = winner.Metrics.MaxDrawdownPct;
            winningWeights.CalmarRatio = winner.Metrics.CalmarRatio;
            winningWeights.TotalTrades = winner.Metrics.TotalTrades;
            winningWeights.WinRatePct = winner.Metrics.WinRatePct;
            if (baselineResult != null)
            {
                winningWeights.BaselineSharpeRatio = baselineResult.Metrics.SharpeRatio;
                winningWeights.BaselineCagrPct = baselineResult.Metrics.CagrPct;
                winningWeights.BaselineMaxDrawdownPct = baselineResult.Metrics.MaxDrawdownPct;
            }

            try
            {
                string json = JsonSerializer.Serialize(winningWeights, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(outputPath, json);
                Console.WriteLine($"  Winning weights written to {outputPath}");
                Console.WriteLine($"  (vs baseline: Sharpe {winningWeights.BaselineSharpeRatio:F2} -> {winningWeights.SharpeRatio:F2}, " +
                    $"CAGR {winningWeights.BaselineCagrPct:F1}% -> {winningWeights.CagrPct:F1}%, " +
                    $"MaxDD {winningWeights.BaselineMaxDrawdownPct:F1}% -> {winningWeights.MaxDrawdownPct:F1}%)");
                Console.WriteLine();
                Console.WriteLine("  The live scanner will pick this up automatically next run (Config.");
                Console.WriteLine("  UseOptimizedWeights defaults to true). Re-run --optimize-weights");
                Console.WriteLine($"  periodically — a weight file older than {Config.OptimizedWeightsMaxAgeDays} days is");
                Console.WriteLine("  ignored automatically (see appsettings.json: OptimizedWeightsMaxAgeDays).");
            }
            catch (Exception ex)
            {
                Log.Error("Could not write {Path}: {Msg}", outputPath, ex.Message);
                Console.WriteLine($"  Failed to write {outputPath}: {ex.Message}");
            }
        }

        /// <summary>Draws 6 independent Exponential(1) samples and normalizes to sum=100 — the
        /// standard trick for a uniform-ish draw over a simplex (equivalent to a symmetric
        /// Dirichlet(1,1,1,1,1,1)), so no single weight is systematically favored by the search.
        /// Floating-point rounding to 1 decimal can leave the sum a few hundredths off 100;
        /// the remainder is folded into the largest weight rather than left silently drifting.</summary>
        private static WeightCandidate RandomCandidate(Random rng)
        {
            double NextExp() => -Math.Log(1.0 - rng.NextDouble());
            var raw = new double[6];
            double total = 0;
            for (int i = 0; i < 6; i++) { raw[i] = NextExp(); total += raw[i]; }
            for (int i = 0; i < 6; i++) raw[i] = Math.Round(raw[i] / total * 100.0, 1);

            double sum = raw.Sum();
            int maxIdx = Array.IndexOf(raw, raw.Max());
            raw[maxIdx] = Math.Round(raw[maxIdx] + (100.0 - sum), 1);

            return new WeightCandidate
            {
                VcpWeight = raw[0], VolumeWeight = raw[1], TrendAlignmentWeight = raw[2],
                SupertrendWeight = raw[3], HmaCrossoverWeight = raw[4], AdxWeight = raw[5],
            };
        }
    }
}
