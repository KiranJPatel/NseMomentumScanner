// =============================================================================
//  BACKTESTING MODULE - BacktestDataCache
// -----------------------------------------------------------------------------
//  A backtest-only disk cache for Yahoo history, separate from and never
//  touching the scanner's own YahooDataFetcher cache (which deliberately only
//  caches weekly/monthly bars for a few hours, since a *live* scanner must
//  always fetch daily/intraday data fresh - see the comment on
//  YahooDataFetcher.FetchAsync). A backtest is the opposite access pattern:
//  the same multi-year daily history is re-requested every time a run is
//  re-tried with different stop-loss/target/sizing settings, so caching it
//  for a longer window (BacktestConfig.CacheHours, default 12h) meaningfully
//  speeds up iterative strategy tuning without hammering Yahoo on every run.
//
//  Cache key is (ticker, interval) only - on a hit, the cached series is used
//  as-is if it is fresh enough AND its earliest bar reaches back far enough
//  to cover the currently requested lookback; otherwise it's treated as a
//  miss and re-fetched (and the cache file is overwritten with the new,
//  longer series). This keeps the logic simple while still satisfying the
//  common "tune a parameter, re-run the same date range" workflow.
// =============================================================================
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using NseMomentumScanner;

namespace NseMomentumScanner.Backtesting
{
    internal static class BacktestDataCache
    {
        private static readonly string CacheDir = Path.Combine(".cache", "backtest");

        private sealed class CachedSeries
        {
            public DateTime CachedAtUtc { get; set; }
            public System.Collections.Generic.List<StockQuote> Quotes { get; set; } = new();
        }

        public static async Task<System.Collections.Generic.List<StockQuote>?> TryReadAsync(
            string ticker, YahooInterval interval, DateTime earliestNeeded, CancellationToken ct)
        {
            if (!BacktestConfig.CacheEnabled) return null;
            try
            {
                var path = CachePath(ticker, interval);
                if (!File.Exists(path)) return null;

                var json = await File.ReadAllTextAsync(path, ct);
                var cached = JsonSerializer.Deserialize<CachedSeries>(json);
                if (cached is null || cached.Quotes.Count == 0) return null;
                if ((DateTime.UtcNow - cached.CachedAtUtc).TotalHours > BacktestConfig.CacheHours) return null;
                if (cached.Quotes[0].Date.Date > earliestNeeded.Date) return null; // doesn't reach back far enough

                return cached.Quotes;
            }
            catch (Exception ex)
            {
                Log.Debug("Backtest cache read miss for {Ticker} [{Iv}]: {Msg}", ticker, interval, ex.Message);
                return null;
            }
        }

        public static async Task WriteAsync(
            string ticker, YahooInterval interval, System.Collections.Generic.List<StockQuote> quotes, CancellationToken ct)
        {
            if (!BacktestConfig.CacheEnabled || quotes.Count == 0) return;
            try
            {
                Directory.CreateDirectory(CacheDir);
                var payload = new CachedSeries { CachedAtUtc = DateTime.UtcNow, Quotes = quotes };
                var json = JsonSerializer.Serialize(payload);
                await File.WriteAllTextAsync(CachePath(ticker, interval), json, ct);
            }
            catch (Exception ex)
            {
                Log.Debug("Backtest cache write failed for {Ticker} [{Iv}]: {Msg}", ticker, interval, ex.Message);
            }
        }

        private static string CachePath(string ticker, YahooInterval interval) =>
            Path.Combine(CacheDir, $"{Sanitize(ticker)}_{interval}.json");

        private static string Sanitize(string s) =>
            new(s.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-').ToArray());
    }
}
