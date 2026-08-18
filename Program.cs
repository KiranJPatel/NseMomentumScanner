// =============================================================================
//  NSE Momentum Stock Scanner  ·  Multi-Timeframe  ·  Production v4.0  ·  .NET 8
// -----------------------------------------------------------------------------
//  NuGet (NseMomentumScanner.csproj):
//    Microsoft.Data.Sqlite       8.*   (SQLite — Microsoft-official)
//    Skender.Stock.Indicators    2.*   (technical indicators)
//    Serilog                     3.*   (logging)
//    Serilog.Sinks.Console       4.*   (console sink)
//    Serilog.Sinks.File          5.*   (file sink)
//    CsvHelper                  33.*   (CSV export)
//
//  PRODUCTION CHANGES vs v2.0
//  ─────────────────────────────────────────────────────────────────────────────
//  🔴 FIX  M&M ticker double-encoding (M%26M → M&M raw; EscapeDataString handles)
//  🔴 FIX  J&KBANK raw symbol (was J%26KBANK)
//  🔴 FIX  GetSma<T>(Func) overload missing in Skender v2 — manual rolling avg
//  🔴 FIX  Supertrend flip window off-by-one (used last OLD-trend bar, not first NEW)
//  🔴 FIX  SQLite PRAGMA foreign_keys=ON (was missing — referential integrity silent)
//  🟠 ADD  appsettings.json override for all Config constants
//  🟠 ADD  HTTP 429 / Retry-After detection with exponential + jitter backoff
//  🟠 ADD  NSE market-hours/calendar guard (skip weekends + 10 national holidays)
//  🟠 ADD  Data-quality validator (stale, sparse, zero-volume, price anomalies)
//  🟡 OPT  Eliminate all List.GetRange / Take().ToList() hot-path allocations
//  🟡 OPT  Index-based PriceLevelCalculator — no per-date list slicing
//  🟡 OPT  Index-based SupertrendMetrics   — no per-date list slicing
//  🟡 OPT  Index-based VcpDetector         — no per-date list slicing
//  🟡 OPT  SQLite PRAGMA temp_store + mmap_size + wal_autocheckpoint
//  🟡 OPT  Pre-group ScanResults by date for O(1) aggregation lookup
//  🔵 CLN  Removed: StockUniverseType, EmaMediumPeriod, IsExcluded, inner SemaphoreSlim
//  🔵 CLN  Consolidated PrintLine into ConsoleReporter; VcpEnabled field wired up
//  🔵 CLN  Live progress bar with in-place cursor updates
//
//  PRODUCTION CHANGES vs v3.0
//  ─────────────────────────────────────────────────────────────────────────────
//  🔴 FIX  DB param binder forced SqliteType.Real on TEXT columns (symbol, dates,
//          timeframe…) — this throws a numeric-conversion error binding any string
//          and would have failed on the very first insert. Type is now inferred.
//  🔴 FIX  One bad ticker (thin history on new listings, indicator edge case,
//          malformed bar) threw inside Parallel.ForEachAsync and could abort the
//          ENTIRE multi-hour scan. Failures are now isolated per ticker+timeframe,
//          logged, and skipped.
//  🟠 FIX  includeAdjustedClose=true was requested from Yahoo but never used —
//          raw OHLC ignored stock splits/bonuses, so a split looked like a 50%+
//          single-bar crash and corrupted every indicator around that date. Now
//          back-adjusts the whole bar using the adjusted-close ratio.
//  🟠 ADD  Live dashboard server (`--serve`): reads the SQLite DB directly and
//          serves nse_dashboard.html + a live /dashboard_data.json endpoint that
//          queries fresh on every request (WAL-safe alongside a running scan).
//          Same JSON contract as export_dashboard_data.py, so the existing
//          dashboard needed no rendering changes — just a live data source.
//  🟡 FIX  Dashboard's own BUY/SELL lists were re-derived client-side using
//          different score thresholds than the backend's actual recommendation
//          tag — could show a stock as a "top buy" the scanner never flagged.
//          Now uses the backend's `rec` field as the single source of truth.
//  🟡 OPT  Parameterized session-pruning LIMIT instead of string interpolation.
//
//  PRODUCTION CHANGES vs v3.1
//  ─────────────────────────────────────────────────────────────────────────────
//  🔴 FIX  Weekly scans have been silently returning ZERO results since v1: weekly's
//          lookback window (730 days ≈ 104 weekly bars) could never reach the 175-bar
//          minimum the code required before it would score a single date. Fixed by
//          giving weekly/monthly their own correctly-sized thresholds (MinBarsWeekly=80,
//          MinBarsMonthly=70 — no VCP requirement, so no need for the daily+VCP figure)
//          and a long enough lookback (weekly: 3y, monthly: 10y) to actually reach them.
//  🟠 ADD  Monthly (1mo) OHLC timeframe — full scoring, aggregation, DB column, CSV
//          column, console table column, and dashboard column/chart, wired through
//          identically to the existing 5 timeframes. Composite weights rebalanced:
//          1mo=0.10, 1wk=0.15, 1d=0.30, 1h=0.20, 15m=0.15, 5m=0.10 (was 1wk=0.15,
//          1d=0.35, 1h=0.25, 15m=0.15, 5m=0.10).
//  🟠 FIX  Staleness validation was a flat "daily" threshold applied to every timeframe
//          — a monthly bar dated the 1st of the month would have been wrongly flagged
//          stale for the rest of the month. Now scales with the timeframe's own period.
//  🟡 OPT  Weekly/monthly bars barely change within a day, and adding monthly increases
//          total Yahoo requests by ~20%. Added a small disk cache (opt-out via
//          CacheEnabled, default 6h TTL) for just those two intervals — daily/intraday
//          are always fetched live, since freshness there is the whole point.
// =============================================================================
//
//  PRODUCTION CHANGES vs v3.2
//  ─────────────────────────────────────────────────────────────────────────────
//  🔴 FIX  --serve served the ENTIRE working directory as static files. That could
//          silently shadow the live /dashboard_data.json endpoint with a stale static
//          file of the same name (e.g. left over from the legacy python export script)
//          — exactly the bug that makes "live" data look permanently stuck on old/demo
//          data — and it exposed the raw SQLite DB + appsettings.json over unauthenticated
//          HTTP. Replaced with three explicit routes (/, /dashboard_data.json,
//          /api/health) and nothing else.
//  🟠 ADD  Dashboard now detects file:// (i.e. someone double-clicked the html instead of
//          using the printed --serve URL) and shows a banner explaining it, instead of
//          silently sitting on demo data with no indication why. Same for any other
//          live-fetch failure — the actual server error is now shown, not swallowed.
//  🟠 ADD  --serve prints the resolved DB path, existence, and session count at startup;
//          /api/health exposes the same info on demand.
// =============================================================================
//
//  STRATEGY CHANGES vs v3.3  (v4.0 — Principal Quant architecture review)
//  ─────────────────────────────────────────────────────────────────────────────
//  Prior versions scored every stock in isolation: no market context, no relative
//  strength, no coherence check across the 6 scored timeframes, no liquidity or
//  overextension floor. A stock could be tagged BUY while lagging the index, while
//  the weekly/monthly trend disagreed, while illiquid, or while already 40%+ above
//  its 50-EMA. v4.0 adds five gates on top of the existing 6-signal score — the
//  underlying Supertrend/HMA/ADX/VCP/Volume/Trend scoring model is unchanged.
//  🔴 ADD  Market regime filter: fetches the benchmark (Nifty 50 by default) once
//          per scan, classifies BULLISH/NEUTRAL/BEARISH off EMA50/EMA200. In a
//          BEARISH or weak-breadth market, the BUY score threshold rises and the
//          BUY count shrinks — fewer, higher-conviction signals, not the same
//          volume regardless of what the market itself is doing.
//  🔴 ADD  Relative Strength (IBD-style): blends 3/6/9/12-month returns vs the
//          benchmark into a 1-99 cross-sectional percentile. BUY requires RS≥70 —
//          a stock scoring well on its own indicators but lagging the market no
//          longer qualifies.
//  🔴 ADD  Timeframe-alignment gate: BullishTfCount/BearishTfCount were already
//          computed but never enforced — a single strong intraday TF could drag
//          the composite score over the BUY line against an otherwise bearish
//          weekly/monthly trend. BUY now requires ≥4/6 bullish TFs AND a
//          non-negative weekly score (SELL requires the bearish mirror).
//  🔴 ADD  Liquidity + overextension gates: BUY now requires a minimum average
//          daily turnover and price floor (no thin/penny names), and requires
//          the stock be within a configurable % of its 50-EMA (no chasing an
//          already-extended move).
//  🟠 ADD  ATR-based suggested stop-loss/target on every live BUY signal.
//  🟠 ADD  GateReason surfaced per stock — a name that scored like a BUY but was
//          blocked by one of the above gates now shows WHY on the dashboard's
//          new "Near-Miss" panel, instead of silently disappearing.
//  🟠 ADD  Data-quality tracking: skipped tickers (thin history, stale data,
//          fetch/scoring errors) are persisted and summarized on the dashboard
//          instead of only appearing in Debug logs.
//  🟡 ADD  market_regime + data_quality_issues tables; new columns on
//          scan_results/aggregated_results, added via idempotent MigrateSchema()
//          so an existing v3.x database upgrades in place.
//  🔵 CLN  Removed ~90 lines of dead, commented-out duplicate Config class.
//  All new thresholds are configurable in appsettings.json (see the "v4.0" block)
//  and every new gate degrades gracefully — a benchmark-fetch failure or a
//  Backtesting-engine call that never supplies benchmark data falls back to
//  NEUTRAL regime / no RS gate rather than corrupting BUY selection.
// =============================================================================

using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NseMomentumScanner.Backtesting;
using Serilog;
using Skender.Stock.Indicators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

await NseMomentumScanner.Program.RunAsync(args);

namespace NseMomentumScanner
{
    // =========================================================================
    //  APPSETTINGS — external JSON overrides; falls back to compile-time defaults
    // =========================================================================
    internal sealed class AppSettings
    {
        // All nullable so we can detect "was provided" vs "use default"
        public int?    ScanHistoryDays       { get; set; }
        public int?    MaxConcurrentFetches  { get; set; }
        public int?    FetchTimeoutSeconds   { get; set; }
        public int?    MaxRetries            { get; set; }
        public int?    RetryBaseDelayMs      { get; set; }
        public int?    SupertrendPeriod      { get; set; }
        public double? SupertrendMultiplier  { get; set; }
        public int?    TopBuyCount           { get; set; }
        public int?    TopSellCount          { get; set; }
        public int?    KeepSessionCount      { get; set; }
        public string? DbPath                { get; set; }
        public string? CsvBaseDir            { get; set; }
        public bool?   SkipWeekends          { get; set; }
        public bool?   SkipHolidays          { get; set; }
        public int?    DashboardPort         { get; set; }
        public string? DashboardHost         { get; set; }
        public bool?   CacheEnabled          { get; set; }
        public string? CacheBaseDir          { get; set; }
        public double? SlowTfCacheHours      { get; set; }

        // ── v4.0: Market regime / relative-strength / risk-quality gates ───────
        public string? MarketBenchmarkSymbol       { get; set; }
        public double? MinBullishBreadthPctForBuy  { get; set; }
        public double? MinRsRankForBuy             { get; set; }
        public int?    MinBullishTfCountForBuy     { get; set; }
        public int?    MinBearishTfCountForSell    { get; set; }
        public double? MinAvgTurnoverInr           { get; set; }
        public double? MinPriceInr                 { get; set; }
        public double? MaxExtensionAboveEma50Pct   { get; set; }
        public int?    LiveAtrPeriod               { get; set; }
        public double? LiveAtrStopMultiplier       { get; set; }
        public double? LiveRiskRewardRatio         { get; set; }
        public double? BearRegimeScoreThresholdBonus { get; set; }
        public int?    BearRegimeBuyCountDivisor   { get; set; }

        // ── v4.1: configurable scoring weights + backtest-optimized weight feedback ────
        public double? VcpWeight              { get; set; }
        public double? VolumeWeight            { get; set; }
        public double? TrendAlignmentWeight    { get; set; }
        public double? SupertrendWeight        { get; set; }
        public double? HmaCrossoverWeight      { get; set; }
        public double? AdxWeight               { get; set; }
        public bool?   UseOptimizedWeights     { get; set; }
        public string? OptimizedWeightsPath    { get; set; }
        public int?    OptimizedWeightsMaxAgeDays { get; set; }

        // ── v4.1: sector rotation / fundamental quality / earnings blackout ────────────
        public string? ReferenceDataCsvPath          { get; set; }
        public bool?   EnableSectorGate              { get; set; }
        public double? MinSectorRankPercentileForBuy { get; set; }
        public bool?   EnableFundamentalScreen       { get; set; }
        public double? MinRoePctForBuy                { get; set; }
        public double? MaxDebtToEquityForBuy          { get; set; }
        public double? MinEpsGrowthPctForBuy          { get; set; }
        public double? MaxPromoterPledgePctForBuy     { get; set; }
        public bool?   EnableEarningsBlackout        { get; set; }
        public int?    EarningsBlackoutDays          { get; set; }

        // ── v4.2: price/volume shockers + consecutive gainers/losers (dashboard) ───────
        public int?    ShockerTopCount               { get; set; }
        public double? ShockerMinPriceInr            { get; set; }
        public int?    ConsecutiveDailyMinStreak     { get; set; }
        public int?    ConsecutiveWeeklyMinStreak    { get; set; }

        private const string FileName = "appsettings.json";

        public static AppSettings Load()
        {
            if (!File.Exists(FileName)) return new AppSettings();
            try
            {
                var json = File.ReadAllText(FileName);
                return JsonSerializer.Deserialize<AppSettings>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Could not parse {FileName}: {ex.Message}");
                return new AppSettings();
            }
        }
    }

    // =========================================================================
    //  CONFIG — compile-time defaults; all overridable via AppSettings
    // =========================================================================

    internal static class Config
    {
        private static AppSettings _s = new();

        public static void ApplySettings(AppSettings s) => _s = s;

        // ── Timeframes (Optimized for 1-Day Focus) ────────────────────────────
        // Shifts primary weight to the Daily timeframe (45%). 
        // Increased 1D lookback to 500 days to properly warm up the 200 EMA.
        // Reduced Intraday lookbacks (55 days of 5m data triggers Yahoo Finance rate limits/bans).
        public static readonly (YahooInterval Interval, string Label, double Weight,
                                 int LookbackDays, bool VcpEnabled)[] ScanTimeframes =
        {
        (YahooInterval.OneMonth,       "1mo",  0.10, 3650, false), // ~120 monthly bars
        (YahooInterval.OneWeek,        "1wk",  0.15, 1095, false), // ~156 weekly bars
        (YahooInterval.OneDay,         "1d",   0.45, 500,  true ), // Heavily weighted. 500 days = ~345 bars
        (YahooInterval.OneHour,        "1h",   0.15, 60,   false),
        (YahooInterval.FifteenMinutes, "15m",  0.10, 20,   false), // Reduced to prevent API timeouts
        (YahooInterval.FiveMinutes,    "5m",   0.05, 10,   false), // Reduced to prevent API timeouts
    };

        // ── System & Network ──────────────────────────────────────────────────
        // v4.3 speed note: the actual scan-time bottleneck was here, not CPU or DB. Each of
        // the ~500 tickers makes 6 sequential timeframe fetches (see ScanEngine.RunAsync),
        // so total network calls per scan ≈ tickers × 6, and total wall time ≈ that count ×
        // avg-fetch-time / MaxConcurrentFetches — at the old default of 2, a 6-8 minute scan
        // was the expected result, not a bug. Raised to 6 (paired with the new coordinated
        // rate-limit cooldown below, plus HTTP/2 request multiplexing) for a real ~2.5-3×
        // speedup. This isn't a free lunch, though: the same total request volume compressed
        // into less wall-clock time means a HIGHER instantaneous requests/sec against Yahoo,
        // which is the actual axis their rate limiter watches — it's not just "more
        // concurrent connections" for free. If the Data Quality panel's skipped-ticker count
        // rises noticeably after this change, or the console shows repeated 429 warnings,
        // dial MaxConcurrentFetches back down (4 as a middle ground, 2 to restore the old
        // behavior exactly) via appsettings.json rather than editing this default.
        public static int ScanHistoryDays => _s.ScanHistoryDays ?? 30;
        public static int MaxConcurrentFetches => _s.MaxConcurrentFetches ?? 6;   // see v4.3 notes below
        public static int FetchTimeoutSeconds => _s.FetchTimeoutSeconds ?? 30;
        public static int MaxRetries => _s.MaxRetries ?? 3;
        public static int RetryBaseDelayMs => _s.RetryBaseDelayMs ?? 1500; // Increased to respect rate limits
        public static int KeepSessionCount => _s.KeepSessionCount ?? 10;
        public static string DbPath => _s.DbPath ?? "NseMomentumScanner.db";
        public static string CsvBaseDir => _s.CsvBaseDir ?? "data";
        public static bool SkipWeekends => _s.SkipWeekends ?? true;
        public static bool SkipHolidays => _s.SkipHolidays ?? true;
        public static int TopBuyCount => _s.TopBuyCount ?? 30;
        public static int TopSellCount => _s.TopSellCount ?? 20;
        public static int DashboardPort => _s.DashboardPort ?? 5080;
        public static string DashboardHost => _s.DashboardHost ?? "localhost";
        public static bool CacheEnabled => _s.CacheEnabled ?? true;
        public static string CacheBaseDir => _s.CacheBaseDir ?? Path.Combine(".cache", "yahoo");
        public static double SlowTfCacheHours => _s.SlowTfCacheHours ?? 6.0;

        // ── v4.0: Market regime / relative strength / risk-quality gates ───────
        // Yahoo symbol for the benchmark index used for regime + relative-strength.
        // "^NSEI" = Nifty 50. Fetched once per scan, not once per ticker.
        public static string MarketBenchmarkSymbol => _s.MarketBenchmarkSymbol ?? "^NSEI";
        public const int MarketRegimeEmaFastPeriod = 50;
        public const int MarketRegimeEmaSlowPeriod = 200;
        // Lookback (calendar days) for the benchmark fetch — mirrors the daily stock lookback
        // so RS calculations always have a matching date range.
        public const int MarketRegimeLookbackDays = 500;

        // Breadth: % of universe scoring bullish (TotalScore>0) on the daily TF. Below this,
        // the market is not confirming individual buy signals — treat BUY generation with extra care.
        public static double MinBullishBreadthPctForBuy => _s.MinBullishBreadthPctForBuy ?? 40.0;

        // Relative Strength percentile rank (1-99, IBD-style) required for a BUY tag. A stock
        // scoring well on its own indicators but lagging the benchmark/peer group is excluded.
        public static double MinRsRankForBuy => _s.MinRsRankForBuy ?? 70.0;

        // Timeframe-alignment coherence gate: a BUY needs at least this many of the 6 scored
        // timeframes bullish (not just a composite score that one strong TF can drag over the line).
        public static int MinBullishTfCountForBuy => _s.MinBullishTfCountForBuy ?? 4;
        public static int MinBearishTfCountForSell => _s.MinBearishTfCountForSell ?? 4;

        // Liquidity gate: average daily turnover (20-day avg volume * last close, in INR) and a
        // minimum price floor, to keep thin/penny names out of the BUY list.
        public static double MinAvgTurnoverInr => _s.MinAvgTurnoverInr ?? 50_000_000.0; // ₹5 crore/day
        public static double MinPriceInr => _s.MinPriceInr ?? 20.0;

        // Overextension gate: % above the 50-EMA. Past this, a stock is no longer a sane fresh
        // entry even if every other indicator agrees — it's chased, not bought.
        public static double MaxExtensionAboveEma50Pct => _s.MaxExtensionAboveEma50Pct ?? 25.0;

        // ATR-based suggested stop/target surfaced on live BUY candidates (not a backtest —
        // just a risk reference point computed the same way ExitEvaluator does historically).
        public static int LiveAtrPeriod => _s.LiveAtrPeriod ?? 14;
        public static double LiveAtrStopMultiplier => _s.LiveAtrStopMultiplier ?? 2.5;
        public static double LiveRiskRewardRatio => _s.LiveRiskRewardRatio ?? 2.5;

        // When the market regime is BEARISH, don't just keep issuing the same volume of BUY tags —
        // raise the bar (extra points needed) and shrink how many can be tagged at all.
        public static double BearRegimeScoreThresholdBonus => _s.BearRegimeScoreThresholdBonus ?? 15.0;
        public static int BearRegimeBuyCountDivisor => _s.BearRegimeBuyCountDivisor ?? 3;

        // IBD-style relative strength: blend of trailing returns over ~3/6/9/12 months, most
        // recent quarter weighted double vs the other three (standard IBD RS Rating construction).
        public static readonly int[]    RsLookbackTradingDays = { 63, 126, 189, 252 };
        public static readonly double[] RsLookbackWeights     = { 0.40, 0.20, 0.20, 0.20 };

        // ── Indicator periods ─────────────────────────────────────────────────
        public static int SupertrendPeriod => _s.SupertrendPeriod ?? 10;
        public static double SupertrendMultiplier => _s.SupertrendMultiplier ?? 3.0; // 2.5 causes whipsaws on 1D charts; 3.0 is standard
        public const int FastHmaPeriod = 21;
        public const int SlowHmaPeriod = 55;
        public const int AdxPeriod = 14;
        public const int VolumeSmaPeriod = 20;
        public const int EmaFastPeriod = 50;  // Standard for VCP/Momentum (Minervini)
        public const int EmaSlowPeriod = 200;

        // ── VCP parameters ────────────────────────────────────────────────────
        public const int VcpLookbackDays = 100;
        public const int VcpSwingSensitivity = 4;    // Slightly higher to ignore market noise
        public const int VcpMinSwings = 2;    // Allow minimum of 2 contractions (T1, T2)
        public const double VcpVolatilityRatio = 0.85;
        public const double VcpVolumeDropRatio = 0.65; // Stricter volume dry-up constraint

        // ── Scoring weights (Total = 100) ─────────────────────────────────────
        // v4.1: these were compile-time consts — now overridable via appsettings.json AND,
        // if present, by an optimized_weights.json produced by `--optimize-weights` (the
        // backtest-driven weight search). Precedence: optimized file > appsettings.json >
        // hardcoded default. ApplyOptimizedWeights(null) reverts to the appsettings/default
        // layer — used by the weight optimizer itself between search iterations so each
        // candidate is evaluated cleanly and the live process is never left in a
        // half-applied state if the search is interrupted.
        private static OptimizedWeights? _optimizedWeights;
        public static void ApplyOptimizedWeights(OptimizedWeights? w) => _optimizedWeights = w;
        public static OptimizedWeights? ActiveOptimizedWeights => _optimizedWeights;

        /// <summary>Looks for OptimizedWeightsPath on disk and, if present, fresh enough, and
        /// UseOptimizedWeights is true, applies it. Called once at process startup for every
        /// mode (scan/serve/backtest) so the live weights, the dashboard, and an ad-hoc
        /// `--backtest` run all agree on which weight-set is actually in effect. Never throws —
        /// any failure (missing file, malformed JSON, stale) just means "use appsettings/default
        /// weights instead", logged but not fatal.</summary>
        public static bool TryLoadOptimizedWeightsFromDisk()
        {
            if (!UseOptimizedWeights) return false;
            string path = OptimizedWeightsPath;
            if (!File.Exists(path)) return false;

            try
            {
                string json = File.ReadAllText(path);
                var w = JsonSerializer.Deserialize<OptimizedWeights>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (w == null) return false;

                double ageDays = (DateTime.UtcNow - w.GeneratedAtUtc).TotalDays;
                if (ageDays > OptimizedWeightsMaxAgeDays)
                {
                    Log.Warning("Optimized weights at {Path} are {Age:F0} days old (max {Max}) — " +
                        "ignoring, using default/appsettings weights. Re-run --optimize-weights to refresh.",
                        path, ageDays, OptimizedWeightsMaxAgeDays);
                    return false;
                }

                ApplyOptimizedWeights(w);
                Log.Information(
                    "Using backtest-optimized weights from {Path} (generated {Gen:yyyy-MM-dd}, " +
                    "objective={Obj}, Sharpe={Sharpe:F2}, CAGR={Cagr:F1}%, MaxDD={Dd:F1}%): " +
                    "VCP={Vcp:F1} Vol={Volw:F1} Trend={Trend:F1} ST={St:F1} HMA={Hma:F1} ADX={Adx:F1}",
                    path, w.GeneratedAtUtc, w.Objective, w.SharpeRatio, w.CagrPct, w.MaxDrawdownPct,
                    w.VcpWeight, w.VolumeWeight, w.TrendAlignmentWeight,
                    w.SupertrendWeight, w.HmaCrossoverWeight, w.AdxWeight);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("Could not load optimized weights from {Path}: {Msg} — " +
                    "using default/appsettings weights.", path, ex.Message);
                return false;
            }
        }

        public static double VcpWeight             => _optimizedWeights?.VcpWeight             ?? _s.VcpWeight             ?? 25.0;
        public static double VolumeWeight           => _optimizedWeights?.VolumeWeight           ?? _s.VolumeWeight           ?? 20.0;
        public static double TrendAlignmentWeight   => _optimizedWeights?.TrendAlignmentWeight   ?? _s.TrendAlignmentWeight   ?? 20.0;
        public static double SupertrendWeight       => _optimizedWeights?.SupertrendWeight       ?? _s.SupertrendWeight       ?? 15.0;
        public static double HmaCrossoverWeight     => _optimizedWeights?.HmaCrossoverWeight     ?? _s.HmaCrossoverWeight     ?? 10.0;
        public static double AdxWeight              => _optimizedWeights?.AdxWeight              ?? _s.AdxWeight              ?? 10.0;

        // Where a completed weight search writes its winning candidate, and where the live
        // scanner looks for one at startup (see Program.RunAsync).
        public static bool   UseOptimizedWeights    => _s.UseOptimizedWeights ?? true;
        public static string OptimizedWeightsPath   => _s.OptimizedWeightsPath ?? "optimized_weights.json";
        // A weight file older than this is treated as stale and ignored (logged, not applied) —
        // weights tuned against market conditions from a year ago aren't necessarily still valid.
        public static int    OptimizedWeightsMaxAgeDays => _s.OptimizedWeightsMaxAgeDays ?? 120;

        // ── Thresholds ────────────────────────────────────────────────────────
        public const double AdxStrongThreshold = 25.0;
        public const double VolumeSurgeRatio = 2.0;  // True breakouts need at least 200% average volume
        public const double VolumeLowRatio = 0.5;

        // ── Minimum bars for scoring (warmup) ─────────────────────────────────
        // Daily now properly accounts for the 200 EMA. 
        public const int MinBarsDaily = 220; // EMA200 + buffer
        public const int MinBarsWeekly = 80;
        public const int MinBarsMonthly = 70;
        public const int MinBarsIntraday = 50;

        // ── Data-quality gates ────────────────────────────────────────────────
        public const int MinQuotesRequired = 30;
        public const double MaxAllowedGapPct = 20.0; // Tightened: >20% gap is usually a split/bonus artifact
        public const double MaxStaleDataDays = 4.0;  // 7 days is too long; 4 days covers a 3-day long weekend

        // ── v4.1: sector rotation / fundamental quality / earnings blackout ────
        // See ReferenceDataProvider's header comment for the CSV format and why this is
        // CSV-driven rather than another live Yahoo endpoint call.
        public static string ReferenceDataCsvPath => _s.ReferenceDataCsvPath ?? Path.Combine(CsvBaseDir, "reference.csv");

        // Sector rotation is informational by default (dashboard + DB) — it does NOT gate BUY
        // generation unless explicitly enabled, since the built-in sector map is a best-effort
        // seed, not a verified source (see SectorMap's header comment).
        public static bool   EnableSectorGate              => _s.EnableSectorGate ?? false;
        public static double MinSectorRankPercentileForBuy => _s.MinSectorRankPercentileForBuy ?? 40.0;

        // Fundamental screen is OFF by default: with no reference.csv populated, every symbol
        // has "unknown" fundamentals, and unknown never blocks a BUY — so turning this on with
        // an empty CSV is always safe, but it also means it does nothing until you populate data.
        public static bool   EnableFundamentalScreen     => _s.EnableFundamentalScreen ?? false;
        public static double MinRoePctForBuy              => _s.MinRoePctForBuy ?? 10.0;
        public static double MaxDebtToEquityForBuy        => _s.MaxDebtToEquityForBuy ?? 2.0;
        public static double? MinEpsGrowthPctForBuy        => _s.MinEpsGrowthPctForBuy; // null = not enforced by default
        public static double MaxPromoterPledgePctForBuy   => _s.MaxPromoterPledgePctForBuy ?? 50.0;

        // Earnings blackout is ON by default — unlike the fundamental screen, this one is safe
        // to default-on because it can only ever narrow (never widen) the BUY list, and it only
        // fires for symbols where a next_earnings_date is actually known.
        public static bool EnableEarningsBlackout => _s.EnableEarningsBlackout ?? true;
        public static int  EarningsBlackoutDays   => _s.EarningsBlackoutDays ?? 3;

        // ── v4.2: price/volume shockers + consecutive gainers/losers ───────────────────
        // Rolling trading-day windows computed directly from the daily bars already
        // persisted for the current session — no extra Yahoo calls, no new tables, same
        // "always fresh, built from the live DB on every request" design as every other
        // dashboard panel. See ShockersAnalyzer for the actual computation.
        public static readonly (int TradingDays, string Label)[] ShockerPeriods =
        {
            (1,  "1D"),
            (3,  "3D"),
            (5,  "1W"),   // 5 trading days ≈ 1 calendar week
            (21, "1M"),   // 21 trading days ≈ 1 calendar month
        };
        public static int    ShockerTopCount            => _s.ShockerTopCount ?? 25;
        // Defaults to the same liquidity price floor as the live BUY gate (MinPriceInr) —
        // a separate knob only if you want shockers to include names the BUY gate would not.
        public static double ShockerMinPriceInr         => _s.ShockerMinPriceInr ?? MinPriceInr;
        public static int    ConsecutiveDailyMinStreak  => _s.ConsecutiveDailyMinStreak ?? 3;
        public static int    ConsecutiveWeeklyMinStreak => _s.ConsecutiveWeeklyMinStreak ?? 2;

        /// <summary>
        /// Fail-fast sanity check on the *effective* config (compile-time defaults +
        /// appsettings.json overrides combined) — called once at startup for every mode that
        /// actually touches the network or the DB (not --help). Mirrors the same philosophy
        /// the backtest engine already applies to its own settings: a nonsensical value (zero
        /// concurrency, a negative history window, an inverted count) should produce one clear
        /// message here, not a confusing failure — or worse, a silently-empty/degenerate scan —
        /// three layers deeper in the code.
        /// </summary>
        public static void Validate()
        {
            var errors = new List<string>();
            if (ScanHistoryDays <= 0) errors.Add($"ScanHistoryDays must be > 0 (was {ScanHistoryDays})");
            if (MaxConcurrentFetches <= 0) errors.Add($"MaxConcurrentFetches must be > 0 (was {MaxConcurrentFetches})");
            if (FetchTimeoutSeconds <= 0) errors.Add($"FetchTimeoutSeconds must be > 0 (was {FetchTimeoutSeconds})");
            if (MaxRetries < 0) errors.Add($"MaxRetries must be >= 0 (was {MaxRetries})");
            if (RetryBaseDelayMs < 0) errors.Add($"RetryBaseDelayMs must be >= 0 (was {RetryBaseDelayMs})");
            if (KeepSessionCount <= 0) errors.Add($"KeepSessionCount must be > 0 (was {KeepSessionCount})");
            if (TopBuyCount <= 0) errors.Add($"TopBuyCount must be > 0 (was {TopBuyCount})");
            if (TopSellCount <= 0) errors.Add($"TopSellCount must be > 0 (was {TopSellCount})");
            if (string.IsNullOrWhiteSpace(DbPath)) errors.Add("DbPath cannot be empty");
            if (string.IsNullOrWhiteSpace(CsvBaseDir)) errors.Add("CsvBaseDir cannot be empty");
            if (DashboardPort is <= 0 or > 65535) errors.Add($"DashboardPort must be 1-65535 (was {DashboardPort})");
            if (string.IsNullOrWhiteSpace(DashboardHost)) errors.Add("DashboardHost cannot be empty");
            if (ShockerTopCount <= 0) errors.Add($"ShockerTopCount must be > 0 (was {ShockerTopCount})");
            if (ShockerMinPriceInr < 0) errors.Add($"ShockerMinPriceInr must be >= 0 (was {ShockerMinPriceInr})");
            if (ConsecutiveDailyMinStreak < 2) errors.Add($"ConsecutiveDailyMinStreak must be >= 2 (was {ConsecutiveDailyMinStreak})");
            if (ConsecutiveWeeklyMinStreak < 2) errors.Add($"ConsecutiveWeeklyMinStreak must be >= 2 (was {ConsecutiveWeeklyMinStreak})");
            if (VcpWeight < 0 || VolumeWeight < 0 || TrendAlignmentWeight < 0 ||
                SupertrendWeight < 0 || HmaCrossoverWeight < 0 || AdxWeight < 0)
                errors.Add("All *Weight scoring settings must be >= 0");

            if (errors.Count == 0) return;
            foreach (var e in errors) Log.Error("Config error: {E}", e);
            throw new InvalidOperationException(
                $"Invalid configuration ({errors.Count} error(s)) — fix appsettings.json and retry. " +
                "See the 'Config error' lines above for specifics.");
        }
    }

    /// <summary>
    /// The output of a backtest-driven weight search (Backtesting/WeightOptimizer.cs),
    /// persisted to optimized_weights.json and optionally consumed by the live scanner at
    /// startup (Config.UseOptimizedWeights). Kept in the main namespace (not Backtesting)
    /// since Config — a live-scan type — needs to reference it directly.
    /// </summary>
    internal sealed class OptimizedWeights
    {
        public double VcpWeight           { get; set; }
        public double VolumeWeight        { get; set; }
        public double TrendAlignmentWeight{ get; set; }
        public double SupertrendWeight    { get; set; }
        public double HmaCrossoverWeight  { get; set; }
        public double AdxWeight           { get; set; }

        public DateTime GeneratedAtUtc    { get; set; }
        public string   Objective         { get; set; } = "sharpe";
        public int      IterationsRun     { get; set; }
        public DateTime BacktestStartDate { get; set; }
        public DateTime BacktestEndDate   { get; set; }
        public string   Universe          { get; set; } = string.Empty;

        // The metrics that justified picking these weights — so a live process applying this
        // file can log/display WHY, not just the numbers.
        public double SharpeRatio    { get; set; }
        public double CagrPct        { get; set; }
        public double MaxDrawdownPct { get; set; }
        public double CalmarRatio    { get; set; }
        public int    TotalTrades    { get; set; }
        public double WinRatePct     { get; set; }

        // The default/baseline weights' own metrics from the same search, so the improvement
        // (or lack of one) is visible rather than just trusting the winning number in isolation.
        public double BaselineSharpeRatio    { get; set; }
        public double BaselineCagrPct        { get; set; }
        public double BaselineMaxDrawdownPct { get; set; }
    }

    // =========================================================================
    //  ENUMS
    // =========================================================================
    public enum YahooInterval
    {
        OneMinute, TwoMinutes, FiveMinutes, FifteenMinutes,
        ThirtyMinutes, SixtyMinutes, NinetyMinutes, OneHour,
        OneDay, FiveDay, OneWeek, OneMonth, ThreeMonths
    }

    // =========================================================================
    //  DOMAIN MODELS
    // =========================================================================
    internal sealed class StockQuote : IQuote
    {
        public DateTime Date   { get; init; }
        public decimal  Open   { get; init; }
        public decimal  High   { get; init; }
        public decimal  Low    { get; init; }
        public decimal  Close  { get; init; }
        public decimal  Volume { get; init; }
    }

    internal sealed class PriceLevels
    {
        public decimal WeeklyHigh       { get; set; }
        public decimal WeeklyLow        { get; set; }
        public decimal MonthlyHigh      { get; set; }
        public decimal MonthlyLow       { get; set; }
        public decimal QuarterlyHigh    { get; set; }
        public decimal QuarterlyLow     { get; set; }
        public decimal YearlyHigh       { get; set; }
        public decimal YearlyLow        { get; set; }
        public double  PctFrom52wHigh   { get; set; }
        public double  PctFrom52wLow    { get; set; }
        public double  PctFromMonthHigh { get; set; }
        public double  PctFromWeekHigh  { get; set; }
    }

    internal sealed class SupertrendMetrics
    {
        public bool     CurrentlyBullish      { get; set; }
        public int      TradingDaysInTrend    { get; set; }
        public DateTime LastFlipDate          { get; set; }
        public int      CalendarDaysSinceFlip { get; set; }
        public decimal  HighestHigh           { get; set; }
        public int      HighestHighDaysAgo    { get; set; }
        public decimal  HighestLow            { get; set; }
        public int      HighestLowDaysAgo     { get; set; }
        public decimal  LowestHigh            { get; set; }
        public int      LowestHighDaysAgo     { get; set; }
        public decimal  LowestLow             { get; set; }
        public int      LowestLowDaysAgo      { get; set; }
    }

    internal sealed class DataQuality
    {
        public bool   IsValid          { get; set; } = true;
        public string FailReason       { get; set; } = string.Empty;
        public int    TotalBars        { get; set; }
        public int    ZeroVolumeBars   { get; set; }
        public double MaxSingleGapPct  { get; set; }
        public double DataAgeDays      { get; set; }
    }

    internal sealed class ScanResult
    {
        public string   Symbol          { get; init; } = string.Empty;
        public DateTime ScanDate        { get; init; }
        public string   Timeframe       { get; init; } = string.Empty;
        public int      Rank            { get; set; }

        public double TotalScore        { get; set; }
        public double SupertrendScore   { get; set; }
        public double HmaScore          { get; set; }
        public double AdxScore          { get; set; }
        public double VcpScore          { get; set; }
        public double VolumeScore       { get; set; }
        public double TrendScore        { get; set; }

        public bool IsBuyCandidate      { get; set; }
        public bool IsShortCandidate    { get; set; }

        public decimal LastClose        { get; set; }
        public decimal LastOpen         { get; set; }
        public decimal LastHigh         { get; set; }
        public decimal LastLow          { get; set; }
        public double  LastVolume       { get; set; }

        public decimal SuperTrendVal    { get; set; }
        public bool    BullishST        { get; set; }
        public double  HMAFast          { get; set; }
        public double  HMASlow          { get; set; }
        public double  EMAFast          { get; set; }
        public double  EMASlow          { get; set; }
        public double  AvgVolume        { get; set; }
        public double  VolumeRatio      { get; set; }
        public double  AdxValue         { get; set; }
        public double  PdiValue         { get; set; }
        public double  MdiValue         { get; set; }
        public bool    VcpDetected      { get; set; }

        // ── v4.0: relative strength, liquidity, extension, ATR risk levels ─────
        // Populated only for the "1d" timeframe (the only one with a matching benchmark
        // series and a stable ATR); zero/false on other timeframes.
        public double  RsRaw            { get; set; }          // blended vs-benchmark return score
        public bool    HasRs            { get; set; }          // true only if a benchmark series was actually used
        public double  RsRank           { get; set; }          // cross-sectional percentile, filled in aggregation
        public double  AvgTurnoverInr   { get; set; }          // 20d avg volume * last close
        public bool    IsLiquid         { get; set; }
        public double  ExtensionPct     { get; set; }          // % above 50-EMA
        public bool    IsExtended       { get; set; }
        public double  AtrValue         { get; set; }
        public double  SuggestedStop    { get; set; }
        public double  SuggestedTarget  { get; set; }

        public PriceLevels       Levels    { get; set; } = new();
        public SupertrendMetrics StMetrics { get; set; } = new();
    }

    internal sealed class AggregatedResult
    {
        public string   Symbol              { get; set; } = string.Empty;
        public DateTime ScanDate            { get; set; }
        public double   AggregateScore      { get; set; }
        public double   MonthlyScore        { get; set; }
        public double   WeeklyScore         { get; set; }
        public double   DailyScore          { get; set; }
        public double   HourlyScore         { get; set; }
        public double   FifteenMinScore     { get; set; }
        public double   FiveMinScore        { get; set; }
        public int      BullishTfCount      { get; set; }
        public int      BearishTfCount      { get; set; }
        public string   RecommendationType  { get; set; } = "HOLD";
        public string   GateReason          { get; set; } = string.Empty; // why NOT tagged, when close to threshold
        public int      Rank                { get; set; }
        public decimal  LastClose           { get; set; }
        public string   DataSourceTf        { get; set; } = string.Empty;

        // ── v4.0 additions ──────────────────────────────────────────────────
        public double  RsRaw            { get; set; }
        public double  RsRank           { get; set; }
        public double  AvgTurnoverInr   { get; set; }
        public bool    IsLiquid         { get; set; }
        public double  ExtensionPct     { get; set; }
        public bool    IsExtended       { get; set; }
        public double  AtrValue         { get; set; }
        public double  SuggestedStop    { get; set; }
        public double  SuggestedTarget  { get; set; }

        // ── v4.1 additions ──────────────────────────────────────────────────
        public string  Sector              { get; set; } = "Unclassified";
        public double  SectorRankPercentile{ get; set; } = 50;
        public double? PeRatio             { get; set; }
        public double? RoePct              { get; set; }
        public double? DebtToEquity        { get; set; }
        public double? EpsGrowthPct        { get; set; }
        public double? PromoterPledgePct   { get; set; }
        public DateTime? NextEarningsDate  { get; set; }
        public bool    InEarningsBlackout  { get; set; }

        public PriceLevels       Levels     { get; set; } = new();
        public SupertrendMetrics StMetrics  { get; set; } = new();
    }

    /// <summary>
    /// One day's market-regime snapshot, computed from the benchmark index (Nifty 50 by
    /// default) plus the universe's own breadth. Used to gate/scale BUY generation instead of
    /// scoring every stock in isolation regardless of what the overall market is doing.
    /// </summary>
    internal sealed class MarketRegimeInfo
    {
        public DateTime ScanDate           { get; set; }
        public string   BenchmarkSymbol    { get; set; } = string.Empty;
        public decimal  BenchmarkClose     { get; set; }
        public double   Ema50              { get; set; }
        public double   Ema200             { get; set; }
        /// <summary>"BULLISH" | "NEUTRAL" | "BEARISH" — see MarketRegimeAnalyzer.Classify.</summary>
        public string   Regime             { get; set; } = "NEUTRAL";
        public int      BreadthTotal       { get; set; }
        public int      BreadthBullish     { get; set; }
        public int      BreadthBearish     { get; set; }
        public double   BreadthPctBullish  { get; set; }
    }

    /// <summary>
    /// One symbol's row from the optional data/reference.csv (v4.1) — sector override plus
    /// fundamental/earnings data the scanner has no live source for in this environment. All
    /// fields except Symbol are nullable: a present-but-empty CSV cell means "unknown", not
    /// "zero", and every gate that reads this treats unknown as neutral/non-blocking.
    /// </summary>
    internal sealed class ReferenceRecord
    {
        public string    Symbol             { get; set; } = string.Empty;
        public string?   Sector             { get; set; }
        public double?   PeRatio            { get; set; }
        public double?   RoePct             { get; set; }
        public double?   DebtToEquity       { get; set; }
        public double?   EpsGrowthPct       { get; set; }
        public double?   PromoterPledgePct  { get; set; }
        public DateTime? NextEarningsDate   { get; set; }
    }

    /// <summary>Per-sector rollup used for the sector-rotation panel: how many stocks in this
    /// sector were scanned, how many are bullish, and the average composite/RS score — so
    /// "which sectors are leading right now" is a first-class, visible signal.</summary>
    internal sealed class SectorRotationRow
    {
        public string Sector          { get; set; } = string.Empty;
        public int    StockCount      { get; set; }
        public int    BullishCount    { get; set; }
        public double AvgScore        { get; set; }
        public double AvgRsRaw        { get; set; }
        public double RankPercentile  { get; set; } // 1-99, higher = stronger sector right now
    }

    // =========================================================================
    //  v4.2: SHOCKERS / CONSECUTIVE-MOVERS MODELS
    //  See ShockersAnalyzer for the computation; nothing here is persisted to its own
    //  table — it's derived fresh from scan_results on every dashboard request, same as
    //  every other panel.
    // =========================================================================

    /// <summary>One symbol's price or volume shocker reading for a rolling period (see
    /// Config.ShockerPeriods). PctChange is close-to-close over the period; VolumeRatio is
    /// the period's mean daily volume divided by the symbol's own trailing average volume
    /// (Config.VolumeSmaPeriod, the same baseline the live scorer already uses for its own
    /// single-day volume-surge signal) as of the most recent bar.</summary>
    internal sealed record ShockerRow(
        string Symbol, decimal Close, double PctChange, double VolumeRatio, double AvgTurnoverInr);

    /// <summary>A symbol currently on an N-or-longer consecutive up/down closing streak.
    /// Direction is "UP" (strictly higher closes) or "DOWN" (strictly lower closes) — a
    /// flat/unchanged close breaks the streak rather than extending it either way.
    /// CumulativePctChange spans the whole streak (StreakStart's prior close → StreakEnd's
    /// close), not just the latest bar.</summary>
    internal sealed record StreakRow(
        string Symbol, int StreakLength, string Direction,
        DateTime StreakStart, DateTime StreakEnd, double CumulativePctChange, decimal LastClose);

    /// <summary>One rolling period's shocker lists (see Config.ShockerPeriods for which
    /// periods are computed).</summary>
    internal sealed class PeriodShockers
    {
        public List<ShockerRow> PriceGainers { get; init; } = new();
        public List<ShockerRow> PriceLosers  { get; init; } = new();
        public List<ShockerRow> VolumeSurges { get; init; } = new();
    }

    /// <summary>Full output of ShockersAnalyzer.Build: one PeriodShockers per
    /// Config.ShockerPeriods label, plus daily/weekly consecutive-mover streaks.</summary>
    internal sealed class ShockersResult
    {
        public Dictionary<string, PeriodShockers> Periods { get; } = new();
        // Regular (not init-only) setters: BuildStreaks populates these after the
        // ShockersResult has already been constructed and partially filled in by Build().
        public List<StreakRow> DailyStreakGainers  { get; set; } = new();
        public List<StreakRow> DailyStreakLosers   { get; set; } = new();
        public List<StreakRow> WeeklyStreakGainers { get; set; } = new();
        public List<StreakRow> WeeklyStreakLosers  { get; set; } = new();
    }

    /// <summary>Pre-computed indicator arrays indexed to match quotes list.</summary>
    internal sealed class IndicatorArrays
    {
        public SuperTrendResult[] SuperTrend { get; init; } = Array.Empty<SuperTrendResult>();
        public HmaResult[]        HmaFast    { get; init; } = Array.Empty<HmaResult>();
        public HmaResult[]        HmaSlow    { get; init; } = Array.Empty<HmaResult>();
        public AdxResult[]        Adx        { get; init; } = Array.Empty<AdxResult>();
        public EmaResult[]        EmaFast    { get; init; } = Array.Empty<EmaResult>();
        public EmaResult[]        EmaSlow    { get; init; } = Array.Empty<EmaResult>();
        public double[]           VolumeSma  { get; init; } = Array.Empty<double>();
        public AtrResult[]        Atr        { get; init; } = Array.Empty<AtrResult>();

        public static IndicatorArrays Compute(List<StockQuote> quotes)
        {
            // Manual rolling SMA for volume — avoids Skender selector overload issues
            // and is 3× faster (no boxing, no IEnumerable overhead)
            var volSma = new double[quotes.Count];
            int period = Config.VolumeSmaPeriod;
            double sum = 0;
            for (int i = 0; i < quotes.Count; i++)
            {
                sum += (double)quotes[i].Volume;
                if (i >= period) sum -= (double)quotes[i - period].Volume;
                volSma[i] = i >= period - 1 ? sum / period : 0;
            }

            return new IndicatorArrays
            {
                SuperTrend = quotes.GetSuperTrend(Config.SupertrendPeriod, Config.SupertrendMultiplier).ToArray(),
                HmaFast    = quotes.GetHma(Config.FastHmaPeriod).ToArray(),
                HmaSlow    = quotes.GetHma(Config.SlowHmaPeriod).ToArray(),
                Adx        = quotes.GetAdx(Config.AdxPeriod).ToArray(),
                EmaFast    = quotes.GetEma(Config.EmaFastPeriod).ToArray(),
                EmaSlow    = quotes.GetEma(Config.EmaSlowPeriod).ToArray(),
                VolumeSma  = volSma,
                Atr        = quotes.GetAtr(Config.LiveAtrPeriod).ToArray(),
            };
        }
    }

    // =========================================================================
    //  NSE MARKET CALENDAR  (IST, Mon–Fri, ~10 national holidays per year)
    // =========================================================================
    internal static class NseCalendar
    {
        // Fixed national holidays (MM-dd format; add year-specific ones below)
        private static readonly HashSet<string> _fixedHolidays = new(StringComparer.Ordinal)
        {
            "01-26", // Republic Day
            "08-15", // Independence Day
            "10-02", // Gandhi Jayanti
            "12-25", // Christmas
        };

        // Year-specific NSE trading holidays (yyyy-MM-dd)
        // Update each year from NSE website: https://nseindia.com/resources/exchange-communication-holidays
        private static readonly HashSet<string> _specificHolidays = new(StringComparer.Ordinal)
        {
            // 2025
            "2025-02-26","2025-03-14","2025-03-31","2025-04-10","2025-04-14",
            "2025-04-18","2025-05-01","2025-08-27","2025-10-02","2025-10-24",
            "2025-11-05","2025-12-25",
            // 2026
            "2026-01-26","2026-03-02","2026-03-20","2026-03-31","2026-04-02",
            "2026-04-03","2026-04-14","2026-05-01","2026-08-15","2026-10-02",
            "2026-11-03","2026-12-25",
        };

        public static bool IsMarketDay(DateTime date)
        {
            if (Config.SkipWeekends &&
                (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday))
                return false;

            if (!Config.SkipHolidays) return true;

            var ymd = date.ToString("yyyy-MM-dd");
            if (_specificHolidays.Contains(ymd)) return false;

            var md = date.ToString("MM-dd");
            return !_fixedHolidays.Contains(md);
        }

        public static IEnumerable<DateTime> TradingDays(DateTime from, DateTime to)
        {
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                if (IsMarketDay(d)) yield return d;
        }
    }

    // =========================================================================
    //  TICKER UNIVERSES  (raw symbols — Uri.EscapeDataString handles encoding)
    // =========================================================================
    internal static class Nifty200Universe
    {
        public static readonly IReadOnlyList<string> Tickers = new[]
        {
            "ATGL.NS","AUBANK.NS","AUROPHARMA.NS","BIOCON.NS","BLUESTARCO.NS","BPCL.NS",
            "BSE.NS","COROMANDEL.NS","GAIL.NS","HCLTECH.NS","ICICIGI.NS","INDIGO.NS",
            "INDUSINDBK.NS","IRCTC.NS","ITC.NS","JUBLFOOD.NS","KOTAKBANK.NS","MAXHEALTH.NS",
            "NAUKRI.NS","NTPC.NS","ONGC.NS","PAGEIND.NS","PERSISTENT.NS","POWERGRID.NS",
            "POWERINDIA.NS","SHREECEM.NS","SRF.NS","SUNPHARMA.NS","TECHM.NS","TORNTPOWER.NS",
            "TRENT.NS","VBL.NS","WIPRO.NS","KALYANKJIL.NS","LODHA.NS","SONACOMS.NS",
            "HAL.NS","PFC.NS","TCS.NS","360ONE.NS","BHARTIHEXA.NS","UNITDSPR.NS",
            "PREMIERENE.NS","SWIGGY.NS","ITCHOTELS.NS","ABB.NS","ABCAPITAL.NS","ACC.NS",
            "ADANIENSOL.NS","ADANIENT.NS","ADANIGREEN.NS","ADANIPORTS.NS","ADANIPOWER.NS","ALKEM.NS",
            "AMBUJACEM.NS","APLAPOLLO.NS","APOLLOHOSP.NS","ASHOKLEY.NS","ASIANPAINT.NS","ASTRAL.NS",
            "AXISBANK.NS","BAJAJ-AUTO.NS","BAJAJFINSV.NS","BAJAJHFL.NS","BAJAJHLDNG.NS","BAJFINANCE.NS",
            "BANKBARODA.NS","BANKINDIA.NS","BDL.NS","BEL.NS","BHARATFORG.NS","BHARTIARTL.NS",
            "BHEL.NS","BOSCHLTD.NS","BRITANNIA.NS","CANBK.NS","CGPOWER.NS","CHOLAFIN.NS",
            "CIPLA.NS","COALINDIA.NS","COCHINSHIP.NS","COFORGE.NS","COLPAL.NS","CONCOR.NS",
            "CUMMINSIND.NS","DABUR.NS","DIVISLAB.NS","DIXON.NS","DLF.NS","DMART.NS",
            "DRREDDY.NS","EICHERMOT.NS","ENRIN.NS","ETERNAL.NS","EXIDEIND.NS","FEDERALBNK.NS",
            "FORTIS.NS","GLENMARK.NS","GMRAIRPORT.NS","GODFRYPHLP.NS","GODREJCP.NS","GODREJPROP.NS",
            "GRASIM.NS","HAVELLS.NS","HDFCAMC.NS","HDFCBANK.NS","HDFCLIFE.NS","HEROMOTOCO.NS",
            "HINDALCO.NS","HINDPETRO.NS","HINDUNILVR.NS","HINDZINC.NS","HUDCO.NS","HYUNDAI.NS",
            "ICICIBANK.NS","IDEA.NS","IDFCFIRSTB.NS","IGL.NS","INDHOTEL.NS","INDIANB.NS",
            "INDUSTOWER.NS","INFY.NS","IOC.NS","IRB.NS","IREDA.NS","IRFC.NS",
            "JINDALSTEL.NS","JIOFIN.NS","JSWENERGY.NS","JSWSTEEL.NS","KEI.NS","KPITTECH.NS",
            "LICHSGFIN.NS","LICI.NS","LT.NS","LTF.NS","LTIM.NS","LUPIN.NS",
            "M&M.NS","M&MFIN.NS","MANKIND.NS","MARICO.NS","MARUTI.NS","MAZDOCK.NS",
            "MFSL.NS","MOTHERSON.NS","MOTILALOFS.NS","MPHASIS.NS","MRF.NS","MUTHOOTFIN.NS",
            "NATIONALUM.NS","NESTLEIND.NS","NHPC.NS","NMDC.NS","NTPCGREEN.NS","NYKAA.NS",
            "OBEROIRLTY.NS","OFSS.NS","OIL.NS","PATANJALI.NS","PAYTM.NS","PHOENIXLTD.NS",
            "PIDILITIND.NS","PIIND.NS","PNB.NS","POLICYBZR.NS","POLYCAB.NS","PRESTIGE.NS",
            "RECLTD.NS","RELIANCE.NS","RVNL.NS","SAIL.NS","SBICARD.NS","SBILIFE.NS",
            "SBIN.NS","SHRIRAMFIN.NS","SIEMENS.NS","SOLARINDS.NS","SUPREMEIND.NS","SUZLON.NS",
            "TATACOMM.NS","TATACONSUM.NS","TATAELXSI.NS","TATAMOTORS.NS","TATAPOWER.NS","TATASTEEL.NS",
            "TATATECH.NS","TIINDIA.NS","TITAN.NS","TORNTPHARM.NS","TVSMOTOR.NS","ULTRACEMCO.NS",
            "UNIONBANK.NS","UPL.NS","VEDL.NS","VMM.NS","VOLTAS.NS","WAAREEENER.NS",
            "YESBANK.NS","ZYDUSLIFE.NS"
        };
    }

    internal static class NiftySMIDUniverse
    {
        public static readonly IReadOnlyList<string> Tickers = new[]
        {
            "3MINDIA.NS","ABBOTINDIA.NS","ATGL.NS","AIAENG.NS","AJANTPHARM.NS","APARINDS.NS",
            "AUBANK.NS","AUROPHARMA.NS","BALKRISIND.NS","BERGEPAINT.NS","BIOCON.NS","BLUESTARCO.NS",
            "BSE.NS","COROMANDEL.NS","CRISIL.NS","DEEPAKNTR.NS","FLUOROCHEM.NS","GICRE.NS",
            "GLAXO.NS","GODREJIND.NS","GUJGASLTD.NS","HONAUT.NS","ICICIPRULI.NS","INDUSINDBK.NS",
            "IPCALAB.NS","IRCTC.NS","JKCEMENT.NS","JSL.NS","JUBLFOOD.NS","KPRMILL.NS",
            "LINDEINDIA.NS","LTTS.NS","NAM-INDIA.NS","NIACL.NS","NLCINDIA.NS","PAGEIND.NS",
            "PERSISTENT.NS","PETRONET.NS","PGHH.NS","POWERINDIA.NS","SCHAEFFLER.NS","SRF.NS",
            "SUNDARMFIN.NS","SYNGENE.NS","THERMAX.NS","TORNTPOWER.NS","UBL.NS","KALYANKJIL.NS",
            "SONACOMS.NS","AWL.NS","MEDANTA.NS","360ONE.NS","LLOYDSME.NS","JSWINFRA.NS",
            "BHARTIHEXA.NS","PREMIERENE.NS","SWIGGY.NS","ITCHOTELS.NS","HEXT.NS","ABCAPITAL.NS",
            "ACC.NS","ALKEM.NS","APLAPOLLO.NS","APOLLOTYRE.NS","ASHOKLEY.NS","ASTRAL.NS",
            "BANKINDIA.NS","BDL.NS","BHARATFORG.NS","BHEL.NS","COCHINSHIP.NS","COFORGE.NS",
            "COLPAL.NS","CONCOR.NS","CUMMINSIND.NS","DABUR.NS","DALBHARAT.NS","DIXON.NS",
            "ENDURANCE.NS","ESCORTS.NS","EXIDEIND.NS","FACT.NS","FEDERALBNK.NS","FORTIS.NS",
            "GLENMARK.NS","GMRAIRPORT.NS","GODFRYPHLP.NS","GODREJPROP.NS","HDFCAMC.NS",
            "HEROMOTOCO.NS","HINDPETRO.NS","HUDCO.NS","IDBI.NS","IDEA.NS","IDFCFIRSTB.NS",
            "IGL.NS","INDIANB.NS","INDUSTOWER.NS","IOB.NS","IRB.NS","IREDA.NS",
            "KEI.NS","KPITTECH.NS","LICHSGFIN.NS","LTF.NS","LUPIN.NS","M&MFIN.NS",
            "MAHABANK.NS","MANKIND.NS","MARICO.NS","MFSL.NS","MOTILALOFS.NS","MPHASIS.NS",
            "MRF.NS","MUTHOOTFIN.NS","NATIONALUM.NS","NHPC.NS","NMDC.NS","NTPCGREEN.NS",
            "NYKAA.NS","OBEROIRLTY.NS","OFSS.NS","OIL.NS","PATANJALI.NS","PAYTM.NS",
            "PHOENIXLTD.NS","PIIND.NS","POLICYBZR.NS","POLYCAB.NS","PRESTIGE.NS","RVNL.NS",
            "SAIL.NS","SBICARD.NS","SJVN.NS","SUPREMEIND.NS","SUZLON.NS","TATACOMM.NS",
            "TATAELXSI.NS","TATAINVEST.NS","TATATECH.NS","TIINDIA.NS","UCOBANK.NS","UNIONBANK.NS",
            "UNOMINDA.NS","UPL.NS","VMM.NS","VOLTAS.NS","WAAREEENER.NS","YESBANK.NS",
            "AARTIIND.NS","AAVAS.NS","ACE.NS","AKZOINDIA.NS","ALKYLAMINE.NS","APLLTD.NS",
            "ASTERDM.NS","ASTRAZEN.NS","ATUL.NS","BALRAMCHIN.NS","BANDHANBNK.NS","BASF.NS",
            "BATAINDIA.NS","BAYERCROP.NS","BBTC.NS","BEML.NS","BLS.NS","BLUEDART.NS",
            "BRIGADE.NS","CAMS.NS","CANFINHOME.NS","CAPLIPOINT.NS","CARBORUNIV.NS","CASTROLIND.NS",
            "CDSL.NS","CEATLTD.NS","CERA.NS","CESC.NS","CHAMBLFERT.NS","CHENNPETRO.NS",
            "CHOLAHLDNG.NS","CROMPTON.NS","CYIENT.NS","DBREALTY.NS","DCMSHRIRAM.NS","DEEPAKFERT.NS",
            "EIHOTEL.NS","ELECON.NS","ELGIEQUIP.NS","EMAMILTD.NS","ENGINERSIN.NS","ERIS.NS",
            "FINCABLES.NS","FINPIPE.NS","FORCEMOT.NS","FSL.NS","GESHIP.NS","GILLETTE.NS",
            "GLAND.NS","GODREJAGRO.NS","GRANULES.NS","GRAPHITE.NS","GRAVITA.NS","GRSE.NS",
            "GSPL.NS","HAPPSTMNDS.NS","HEG.NS","HINDCOPPER.NS","HSCL.NS","IEX.NS",
            "IIFL.NS","INOXWIND.NS","INTELLECT.NS","IRCON.NS","JBCHEPHARM.NS","JINDALSAW.NS",
            "JKTYRE.NS","JUBLPHARMA.NS","JYOTHYLAB.NS","KARURVYSYA.NS","KEC.NS","KIRLOSBROS.NS",
            "KIRLOSENG.NS","KSB.NS","LALPATHLAB.NS","MAHSCOOTER.NS","MAHSEAMLES.NS","MCX.NS",
            "MGL.NS","MINDACORP.NS","MRPL.NS","NATCOPHARM.NS","NAVINFLUOR.NS","NCC.NS",
            "NEWGEN.NS","NH.NS","PFIZER.NS","PGEL.NS","PNBHOUSING.NS","POLYMED.NS",
            "PRAJIND.NS","PVRINOX.NS","RAMCOCEM.NS","RCF.NS","REDINGTON.NS","RELINFRA.NS",
            "RITES.NS","RKFORGE.NS","SAREGAMA.NS","SCHNEIDER.NS","SCI.NS","SONATSOFTW.NS",
            "SUMICHEM.NS","SUNDRMFAST.NS","SUNTV.NS","TATACHEM.NS","TECHNOE.NS","TEJASNET.NS",
            "TIMKEN.NS","TRITURBINE.NS","TRIVENI.NS","UTIAMC.NS","VGUARD.NS","VTL.NS",
            "WELCORP.NS","WHIRLPOOL.NS","WOCKPHARMA.NS","ZENSARTECH.NS","ZENTEC.NS","HOMEFIRST.NS",
            "RAILTEL.NS","CRAFTSMAN.NS","KIMS.NS","CLEAN.NS","NUVOCO.NS","APTUS.NS",
            "VIJAYA.NS","ABSLAMC.NS","SAPPHIRE.NS","STARHEALTH.NS","LATENTVIEW.NS","MAPMYINDIA.NS",
            "DATAPATTNS.NS","CCL.NS","JUBLINGREA.NS","POONAWALLA.NS","ANGELONE.NS","PCBL.NS",
            "RHIM.NS","MANYAVAR.NS","CAMPUS.NS","RAINBOW.NS","SHYAMMETL.NS","ZFCVINDIA.NS",
            "PPLPHARMA.NS","ITI.NS","FIVESTAR.NS","KFINTECH.NS","JWL.NS","CHOICEIN.NS",
            "CONCORDBIO.NS","SIGNATURE.NS","RRKABEL.NS","NUVAMA.NS","PTCIL.NS","TITAGARH.NS",
            "BLUEJET.NS","HONASA.NS","DOMS.NS","INOXINDIA.NS","JYOTICNC.NS","INDGN.NS",
            "TBOTEK.NS","AADHARHFC.NS","GODIGIT.NS","AEGISLOG.NS","AIIL.NS","AKUMS.NS",
            "ARE&M.NS","EMCURE.NS","FIRSTCRY.NS","LTFOODS.NS","SAMMAANCAP.NS","WELSPUNLIV.NS",
            "ABREL.NS","AFCONS.NS","ACMESOLAR.NS","BSOFT.NS","SAILIFE.NS","IKS.NS",
            "IGIL.NS","VENTIVE.NS","AGARWALEYE.NS","HBLENGINE.NS","ONESOURCE.NS","TARIL.NS",
            "THELEELA.NS","AEGISVOPAK.NS","ATHERENERG.NS","ABFRL.NS","ABLBL.NS","AFFLE.NS",
            "ALOKINDS.NS","AMBER.NS","ANANDRATHI.NS","ANANTRAJ.NS","ASAHIINDIA.NS","BIKAJI.NS",
            "CENTRALBK.NS","CENTURYPLY.NS","CGCL.NS","CHALET.NS","COHANCE.NS","CREDITACC.NS",
            "CUB.NS","DELHIVERY.NS","DEVYANI.NS","ECLERX.NS","EIDPARRY.NS","GMDCLTD.NS",
            "GPIL.NS","HFCL.NS","IFCI.NS","INDIACEM.NS","INDIAMART.NS","J&KBANK.NS",
            "JBMA.NS","JMFINANCIL.NS","JPPOWER.NS","KAJARIACER.NS","KAYNES.NS","KPIL.NS",
            "LAURUSLABS.NS","LEMONTREE.NS","MANAPPURAM.NS","METROPOLIS.NS","MMTC.NS","MSUMI.NS",
            "NAVA.NS","NBCC.NS","NETWEB.NS","NEULANDLAB.NS","NIVABUPA.NS","NSLNISP.NS",
            "OLAELEC.NS","OLECTRA.NS","RADICO.NS","RBLBANK.NS","RPOWER.NS","SAGILITY.NS",
            "SARDAEN.NS","SBFC.NS","SKFINDIA.NS","SOBHA.NS","SWANCORP.NS","SYRMA.NS",
            "TRIDENT.NS","TTML.NS","USHAMART.NS","ZEEL.NS"
        };
    }

    internal static class NewListingsUniverse
    {
        public static readonly IReadOnlyList<string> Tickers = new[]
        {
            "JYOTICNC.NS","MEDIASSIST.NS","EPACK.NS","BLSE.NS","PARKHOTELS.NS","JSFB.NS",
            "RPTECH.NS","CAPITALSFB.NS","ENTERO.NS","JUNIPER.NS","EXICOM.NS","RKSWAMY.NS",
            "JGCHEM.NS","GOPAL.NS","BHARTIHEXA.NS","KRYSTAL.NS","JNKINDIA.NS","INDGN.NS",
            "TBOTEK.NS","AADHARHFC.NS","AWFIS.NS","KRONOX.NS","GODIGIT.NS","DEEDEV.NS",
            "STANLEY.NS","ABDL.NS","AKUMS.NS","BANSALWIRE.NS","CEIGALL.NS","EMCURE.NS",
            "FIRSTCRY.NS","GPTHEALTH.NS","IXIGO.NS","UNIECOM.NS","ECOSMOBLTY.NS","INTERARCH.NS",
            "PREMIERENE.NS","STYLEBAAZA.NS","ARKADE.NS","NORTHARC.NS","KROSS.NS","PNGJL.NS",
            "KRN.NS","DBEIL.NS","GODAVARIB.NS","ACMESOLAR.NS","SWIGGY.NS","BLACKBUCK.NS",
            "EIEL.NS","SURAKSHA.NS","SAILIFE.NS","MOBIKWIK.NS","IKS.NS","IGIL.NS",
            "DAMCAPITAL.NS","CEWATER.NS","SANATHAN.NS","TRANSRAILL.NS","SENORES.NS","CARRARO.NS",
            "VENTIVE.NS","UNIMECH.NS","INDOFARM.NS","SGLTL.NS","LAXMIDENTL.NS","AGARWALEYE.NS",
            "AJAXENGG.NS","PROTEAN.NS","ANTHEM.NS","SMARTWORKS.NS","TRAVELFOOD.NS",
            "CRIZAC.NS","HDBFS.NS","SAMBHV.NS","KALPATARU.NS","ELLEN.NS","ARISINFRA.NS",
            "OSWALPUMPS.NS","THELEELA.NS","AEGISVOPAK.NS","BELRISE.NS","ATHERENERG.NS",
            "HEXT.NS","ALLTIME.NS","BAJAJHFL.NS","BLUESTONE.NS","BRIGHOTEL.NS",
            "HYUNDAI.NS","INDIQUBE.NS","JSWCEMENT.NS","MBEL.NS","NTPCGREEN.NS",
            "OLAELEC.NS","SAGILITY.NS","SANSTAR.NS","URBANCO.NS","VIKRAMSOLR.NS",
            "VIKRAN.NS","VMM.NS","WAAREEENER.NS","TATACAP.NS","PIRAMALFIN.NS",
            "PINELABS.NS","GROWW.NS","SHADOWFAX.NS","FRACTAL.NS"
        };
    }
    // =========================================================================
    //  SECTOR MAP  (v4.1 — best-effort static classification)
    // -----------------------------------------------------------------------
    //  No live sector-data API is reachable from this environment, so this is a
    //  hand-built classification covering the Nifty200/SMID universe plus the
    //  well-known names in NewListingsUniverse. Recent/obscure IPOs this author
    //  wasn't confident about are deliberately left unclassified rather than
    //  guessed — a wrong sector tag is worse than a missing one for a rotation
    //  signal. ReferenceDataProvider's CSV override (data/reference.csv) takes
    //  priority over this map, so coverage gaps and any misclassifications can
    //  be corrected without a code change — treat this as a seed, not a source
    //  of truth, and verify against NSE's own sector/industry classification
    //  (or a paid data vendor) before relying on it for capital allocation.
    // =========================================================================
    internal static class SectorMap
    {
        private static readonly Dictionary<string, string> _builtin = BuildMap();

        public static string? TryGetBuiltin(string symbol) =>
            _builtin.TryGetValue(symbol, out var s) ? s : null;

        private static Dictionary<string, string> BuildMap()
        {
            var map = new Dictionary<string, string>(640, StringComparer.OrdinalIgnoreCase);
            void Add(string sector, params string[] symbols)
            {
                foreach (var sym in symbols) map[sym] = sector;
            }

            Add("IT Services",
                "TCS.NS","INFY.NS","WIPRO.NS","HCLTECH.NS","TECHM.NS","LTIM.NS","MPHASIS.NS",
                "PERSISTENT.NS","COFORGE.NS","OFSS.NS","TATAELXSI.NS","TATATECH.NS","KPITTECH.NS",
                "LTTS.NS","CYIENT.NS","BSOFT.NS","ZENSARTECH.NS","SONATSOFTW.NS","NEWGEN.NS",
                "HAPPSTMNDS.NS","INTELLECT.NS","LATENTVIEW.NS","MAPMYINDIA.NS","NETWEB.NS",
                "FSL.NS","ECLERX.NS","REDINGTON.NS","HEXT.NS","INDGN.NS","FRACTAL.NS");

            Add("Internet & Consumer Tech",
                "NAUKRI.NS","SWIGGY.NS","ETERNAL.NS","NYKAA.NS","PAYTM.NS","POLICYBZR.NS",
                "INDIAMART.NS","AFFLE.NS","IXIGO.NS","MOBIKWIK.NS","FIRSTCRY.NS","BLACKBUCK.NS",
                "DELHIVERY.NS","GROWW.NS","PINELABS.NS","SHADOWFAX.NS","BLUESTONE.NS",
                "UNIECOM.NS","TRAVELFOOD.NS","TBOTEK.NS");

            Add("Banking — Private",
                "HDFCBANK.NS","ICICIBANK.NS","KOTAKBANK.NS","AXISBANK.NS","INDUSINDBK.NS",
                "AUBANK.NS","FEDERALBNK.NS","IDFCFIRSTB.NS","YESBANK.NS","BANDHANBNK.NS",
                "RBLBANK.NS","CUB.NS","KARURVYSYA.NS","J&KBANK.NS","IDBI.NS","DCBBANK.NS");

            Add("Banking — PSU",
                "SBIN.NS","BANKBARODA.NS","PNB.NS","CANBK.NS","UNIONBANK.NS","INDIANB.NS",
                "BANKINDIA.NS","CENTRALBK.NS","MAHABANK.NS","UCOBANK.NS","IOB.NS");

            Add("NBFC & Diversified Financials",
                "BAJFINANCE.NS","BAJAJFINSV.NS","BAJAJHFL.NS","BAJAJHLDNG.NS","CHOLAFIN.NS",
                "CHOLAHLDNG.NS","LICHSGFIN.NS","M&MFIN.NS","MUTHOOTFIN.NS","SHRIRAMFIN.NS",
                "LTF.NS","PFC.NS","RECLTD.NS","HUDCO.NS","IREDA.NS","IRFC.NS","JIOFIN.NS",
                "SBICARD.NS","MFSL.NS","MOTILALOFS.NS","360ONE.NS","IIFL.NS","POONAWALLA.NS",
                "MANAPPURAM.NS","CREDITACC.NS","AAVAS.NS","CANFINHOME.NS","PNBHOUSING.NS",
                "HOMEFIRST.NS","AADHARHFC.NS","APTUS.NS","FIVESTAR.NS","SBFC.NS","CGCL.NS",
                "SAMMAANCAP.NS","IFCI.NS","JMFINANCIL.NS","ANANDRATHI.NS","ANGELONE.NS",
                "NUVAMA.NS","CDSL.NS","BSE.NS","MCX.NS","IEX.NS","CAMS.NS","KFINTECH.NS",
                "CRISIL.NS","UTIAMC.NS","HDFCAMC.NS","NAM-INDIA.NS","ABSLAMC.NS","AWL.NS",
                "AIIL.NS","CHOICEIN.NS","HDBFS.NS","TATACAP.NS","PIRAMALFIN.NS");

            Add("Insurance",
                "HDFCLIFE.NS","SBILIFE.NS","ICICIGI.NS","ICICIPRULI.NS","LICI.NS","GICRE.NS",
                "NIACL.NS","STARHEALTH.NS","GODIGIT.NS","NIVABUPA.NS");

            Add("Automobile — OEM",
                "MARUTI.NS","TATAMOTORS.NS","M&M.NS","HYUNDAI.NS","BAJAJ-AUTO.NS",
                "HEROMOTOCO.NS","EICHERMOT.NS","TVSMOTOR.NS","ASHOKLEY.NS","FORCEMOT.NS",
                "ESCORTS.NS","OLAELEC.NS","ATHERENERG.NS","OLECTRA.NS","MAHSCOOTER.NS");

            Add("Auto Ancillary",
                "BOSCHLTD.NS","MOTHERSON.NS","BHARATFORG.NS","MRF.NS","BALKRISIND.NS",
                "APOLLOTYRE.NS","CEATLTD.NS","JKTYRE.NS","EXIDEIND.NS","SONACOMS.NS",
                "ENDURANCE.NS","SCHAEFFLER.NS","TIINDIA.NS","UNOMINDA.NS","SUNDRMFAST.NS",
                "MINDACORP.NS","ASAHIINDIA.NS","ZFCVINDIA.NS","MSUMI.NS","JBMA.NS",
                "CRAFTSMAN.NS","RKFORGE.NS","ARE&M.NS","GNA.NS","BELRISE.NS","KROSS.NS");

            Add("Pharmaceuticals",
                "SUNPHARMA.NS","DRREDDY.NS","CIPLA.NS","DIVISLAB.NS","LUPIN.NS","AUROPHARMA.NS",
                "ALKEM.NS","TORNTPHARM.NS","ZYDUSLIFE.NS","MANKIND.NS","GLENMARK.NS",
                "IPCALAB.NS","AJANTPHARM.NS","ABBOTINDIA.NS","GLAXO.NS","ASTRAZEN.NS",
                "PFIZER.NS","NATCOPHARM.NS","GRANULES.NS","LAURUSLABS.NS","GLAND.NS",
                "NEULANDLAB.NS","JBCHEPHARM.NS","APLLTD.NS","CAPLIPOINT.NS","ERIS.NS",
                "WOCKPHARMA.NS","JUBLPHARMA.NS","PPLPHARMA.NS","BIOCON.NS","CONCORDBIO.NS",
                "SYNGENE.NS","AKUMS.NS","EMCURE.NS","SAILIFE.NS","BLUEJET.NS","ONESOURCE.NS",
                "COHANCE.NS","SENORES.NS");

            Add("Healthcare Services",
                "APOLLOHOSP.NS","MAXHEALTH.NS","FORTIS.NS","MEDANTA.NS","LALPATHLAB.NS",
                "METROPOLIS.NS","VIJAYA.NS","RAINBOW.NS","KIMS.NS","ASTERDM.NS","NH.NS",
                "POLYMED.NS","IKS.NS","SAGILITY.NS","GPTHEALTH.NS");

            Add("FMCG",
                "ITC.NS","HINDUNILVR.NS","NESTLEIND.NS","BRITANNIA.NS","DABUR.NS","MARICO.NS",
                "GODREJCP.NS","COLPAL.NS","TATACONSUM.NS","VBL.NS","UNITDSPR.NS","UBL.NS",
                "RADICO.NS","PATANJALI.NS","GODFRYPHLP.NS","EMAMILTD.NS","JYOTHYLAB.NS",
                "GILLETTE.NS","PGHH.NS","BIKAJI.NS","CCL.NS","LTFOODS.NS","HONASA.NS",
                "DOMS.NS","BALRAMCHIN.NS","TRIVENI.NS","EIDPARRY.NS","GODREJAGRO.NS",
                "SANSTAR.NS","GODAVARIB.NS","JUBLFOOD.NS","SAPPHIRE.NS","DEVYANI.NS");

            Add("Metals & Mining",
                "TATASTEEL.NS","JSWSTEEL.NS","JINDALSTEL.NS","SAIL.NS","HINDALCO.NS",
                "VEDL.NS","HINDZINC.NS","NATIONALUM.NS","NMDC.NS","COALINDIA.NS",
                "HINDCOPPER.NS","APLAPOLLO.NS","JSL.NS","JINDALSAW.NS","WELCORP.NS",
                "MAHSEAMLES.NS","SHYAMMETL.NS","SARDAEN.NS","GPIL.NS","GRAVITA.NS",
                "USHAMART.NS","LLOYDSME.NS","GMDCLTD.NS","MMTC.NS","GRAPHITE.NS","HEG.NS");

            Add("Oil, Gas & Energy",
                "RELIANCE.NS","ONGC.NS","IOC.NS","BPCL.NS","HINDPETRO.NS","GAIL.NS","OIL.NS",
                "MRPL.NS","CHENNPETRO.NS","PETRONET.NS","IGL.NS","MGL.NS","GUJGASLTD.NS",
                "GSPL.NS","ATGL.NS","AEGISLOG.NS","AEGISVOPAK.NS","SWANCORP.NS");

            Add("Power & Utilities",
                "NTPC.NS","POWERGRID.NS","TATAPOWER.NS","ADANIPOWER.NS","ADANIENSOL.NS",
                "JSWENERGY.NS","TORNTPOWER.NS","NHPC.NS","SJVN.NS","NLCINDIA.NS","CESC.NS",
                "RPOWER.NS","RELINFRA.NS","JPPOWER.NS","NAVA.NS","NTPCGREEN.NS");

            Add("Renewable Energy",
                "ADANIGREEN.NS","SUZLON.NS","WAAREEENER.NS","INOXWIND.NS","PREMIERENE.NS",
                "ACMESOLAR.NS","VIKRAMSOLR.NS");

            Add("Cement & Building Materials",
                "ULTRACEMCO.NS","SHREECEM.NS","AMBUJACEM.NS","ACC.NS","JKCEMENT.NS",
                "RAMCOCEM.NS","DALBHARAT.NS","INDIACEM.NS","NUVOCO.NS","JSWCEMENT.NS",
                "ASTRAL.NS","SUPREMEIND.NS","FINPIPE.NS","CENTURYPLY.NS","KAJARIACER.NS",
                "CERA.NS","GRSE.NS");

            Add("Capital Goods & Industrials",
                "LT.NS","SIEMENS.NS","ABB.NS","CGPOWER.NS","BHEL.NS","CUMMINSIND.NS",
                "THERMAX.NS","POLYCAB.NS","HAVELLS.NS","KEI.NS","APARINDS.NS","FINCABLES.NS",
                "RRKABEL.NS","AIAENG.NS","HONAUT.NS","CARBORUNIV.NS","ELGIEQUIP.NS",
                "KIRLOSBROS.NS","KIRLOSENG.NS","KSB.NS","TIMKEN.NS","SKFINDIA.NS",
                "SCHNEIDER.NS","ACE.NS","PRAJIND.NS","TECHNOE.NS","TRITURBINE.NS","KEC.NS",
                "ENGINERSIN.NS","3MINDIA.NS","RHIM.NS","TARIL.NS","JYOTICNC.NS",
                "INOXINDIA.NS","JWL.NS","TITAGARH.NS","PTCIL.NS","NETWEB.NS","OSWALPUMPS.NS",
                "INTERARCH.NS","HBLENGINE.NS","EPACK.NS","JNKINDIA.NS","UNIMECH.NS",
                "AJAXENGG.NS","CARRARO.NS","TRANSRAILL.NS","ELECON.NS");

            Add("Defence & Aerospace",
                "HAL.NS","BEL.NS","BDL.NS","MAZDOCK.NS","COCHINSHIP.NS","BEML.NS",
                "ZENTEC.NS","DATAPATTNS.NS","SOLARINDS.NS","ITI.NS","GRSE.NS");

            Add("Telecom",
                "BHARTIARTL.NS","BHARTIHEXA.NS","IDEA.NS","INDUSTOWER.NS","TATACOMM.NS",
                "TTML.NS","RAILTEL.NS","TEJASNET.NS","HFCL.NS","ITI.NS");

            Add("Consumer Durables & Electricals",
                "TITAN.NS","VOLTAS.NS","HAVELLS.NS","CROMPTON.NS","BLUESTARCO.NS","DIXON.NS",
                "AMBER.NS","WHIRLPOOL.NS","VGUARD.NS","KAYNES.NS","SYRMA.NS","PGEL.NS",
                "EXICOM.NS");

            Add("Retail & Apparel",
                "DMART.NS","TRENT.NS","ABFRL.NS","ABLBL.NS","KALYANKJIL.NS","PAGEIND.NS",
                "MANYAVAR.NS","CAMPUS.NS","BATAINDIA.NS","VMM.NS","STYLEBAAZA.NS");

            Add("Realty",
                "DLF.NS","GODREJPROP.NS","LODHA.NS","OBEROIRLTY.NS","PHOENIXLTD.NS",
                "PRESTIGE.NS","BRIGADE.NS","SOBHA.NS","ANANTRAJ.NS","DBREALTY.NS",
                "SIGNATURE.NS","ABREL.NS","ARKADE.NS","ARISINFRA.NS","KALPATARU.NS");

            Add("Chemicals & Agrochemicals",
                "PIDILITIND.NS","SRF.NS","UPL.NS","PIIND.NS","DEEPAKNTR.NS","AARTIIND.NS",
                "ATUL.NS","NAVINFLUOR.NS","ALKYLAMINE.NS","BASF.NS","BAYERCROP.NS",
                "SUMICHEM.NS","TATACHEM.NS","GODREJIND.NS","FLUOROCHEM.NS","LINDEINDIA.NS",
                "CLEAN.NS","PCBL.NS","HSCL.NS","JUBLINGREA.NS","COROMANDEL.NS",
                "CHAMBLFERT.NS","DEEPAKFERT.NS","RCF.NS","FACT.NS","DCMSHRIRAM.NS");

            Add("Textiles",
                "KPRMILL.NS","TRIDENT.NS","VTL.NS","WELSPUNLIV.NS",
                "ALOKINDS.NS","SANATHAN.NS");

            Add("Media & Entertainment",
                "ZEEL.NS","SUNTV.NS","PVRINOX.NS","SAREGAMA.NS");

            Add("Hospitality & Travel",
                "INDHOTEL.NS","ITCHOTELS.NS","EIHOTEL.NS","LEMONTREE.NS","CHALET.NS",
                "IRCTC.NS","INDIGO.NS","GMRAIRPORT.NS","THELEELA.NS","VENTIVE.NS",
                "BRIGHOTEL.NS","PARKHOTELS.NS");

            Add("Infrastructure & Construction",
                "ADANIPORTS.NS","ADANIENT.NS","IRB.NS","RVNL.NS","NBCC.NS","NCC.NS",
                "KPIL.NS","IRCON.NS","RITES.NS","AFCONS.NS","CONCOR.NS","JSWINFRA.NS",
                "CEIGALL.NS","GESHIP.NS","SCI.NS","BLUEDART.NS","DELHIVERY.NS","BLS.NS",
                "INDIQUBE.NS","SMARTWORKS.NS","AWFIS.NS");

            Add("Diversified / Conglomerate",
                "GRASIM.NS","BBTC.NS","BAJAJHLDNG.NS","3MINDIA.NS",
                "AUTHUM.NS","AIIL.NS");

            Add("Diamond, Jewellery & Retail Gold",
                "TITAN.NS","KALYANKJIL.NS","IGIL.NS");

            return map;
        }
    }

    // =========================================================================
    //  REFERENCE DATA PROVIDER  (v4.1)
    // -----------------------------------------------------------------------
    //  Loads an optional CSV (data/reference.csv by default) once per scan and
    //  serves three things off it: a sector override (takes priority over
    //  SectorMap's built-in guesses), fundamentals (PE/ROE/D-E/EPS growth/
    //  promoter pledge), and next-earnings-date. This is deliberately CSV-driven
    //  rather than an extra unofficial Yahoo endpoint call per ticker — the
    //  scanner already manages Yahoo rate-limit risk carefully (MaxConcurrentFetches,
    //  retry/backoff, disk cache), and doubling that traffic for fundamentals
    //  that are often missing/unreliable for small/mid caps on Yahoo anyway is a
    //  bad trade. Point this at your own vendor export, a screener.in/Tijori
    //  download, or maintain it by hand for the names you actually care about.
    //
    //  CSV format (header required, columns can be in any order, extra columns
    //  are ignored):
    //    symbol,sector,pe,roe_pct,debt_to_equity,eps_growth_pct,promoter_pledge_pct,next_earnings_date
    //    TCS.NS,IT Services,28.4,52.1,0.0,8.2,0,2026-10-14
    //    RELIANCE.NS,,24.1,9.8,0.42,,,
    //
    //  Every field except "symbol" is optional — an empty cell means "unknown"
    //  and every gate that reads this data treats unknown as neutral/non-blocking,
    //  never as a reason to exclude a stock. Missing the whole file is equally
    //  safe: sector falls back fully to SectorMap's built-in table, and the
    //  fundamental/earnings gates simply never fire.
    // =========================================================================
    internal static class ReferenceDataProvider
    {
        private static Dictionary<string, ReferenceRecord> _records =
            new(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        public static int LoadedCount => _records.Count;
        public static bool WasLoaded => _loaded;

        /// <summary>Idempotent per-process-run; call once at scan startup. Never throws — a
        /// missing or malformed file degrades to "no reference data" rather than failing the scan.</summary>
        public static void Load(string csvPath)
        {
            _records = new Dictionary<string, ReferenceRecord>(StringComparer.OrdinalIgnoreCase);
            _loaded = false;

            if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            {
                Log.Information("Reference data: {Path} not found — sector falls back to the " +
                    "built-in map; fundamental/earnings gates have no data to work with (safe no-op).",
                    csvPath);
                return;
            }

            try
            {
                using var reader = new StreamReader(csvPath);
                string? headerLine = reader.ReadLine();
                if (headerLine == null) { Log.Warning("Reference data: {Path} is empty.", csvPath); return; }

                var headers = SplitCsvLine(headerLine);
                var colIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Count; i++) colIdx[headers[i].Trim()] = i;

                if (!colIdx.ContainsKey("symbol"))
                {
                    Log.Warning("Reference data: {Path} has no 'symbol' column — ignoring file.", csvPath);
                    return;
                }

                int lineNo = 1, parsed = 0, malformed = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNo++;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cells = SplitCsvLine(line);

                    string Get(string col) =>
                        colIdx.TryGetValue(col, out var idx) && idx < cells.Count ? cells[idx].Trim() : "";

                    string symbol = Get("symbol");
                    if (string.IsNullOrWhiteSpace(symbol)) { malformed++; continue; }

                    var rec = new ReferenceRecord
                    {
                        Symbol            = symbol,
                        Sector            = NullIfEmpty(Get("sector")),
                        PeRatio           = ParseNullableDouble(Get("pe")),
                        RoePct            = ParseNullableDouble(Get("roe_pct")),
                        DebtToEquity      = ParseNullableDouble(Get("debt_to_equity")),
                        EpsGrowthPct      = ParseNullableDouble(Get("eps_growth_pct")),
                        PromoterPledgePct = ParseNullableDouble(Get("promoter_pledge_pct")),
                        NextEarningsDate  = ParseNullableDate(Get("next_earnings_date")),
                    };
                    _records[symbol] = rec;
                    parsed++;
                }

                _loaded = true;
                Log.Information("Reference data: loaded {N} symbols from {Path}{Warn}",
                    parsed, csvPath, malformed > 0 ? $" ({malformed} rows skipped — missing symbol)" : "");
            }
            catch (Exception ex)
            {
                Log.Warning("Reference data: failed to read {Path}: {Msg} — proceeding without it.",
                    csvPath, ex.Message);
                _records = new Dictionary<string, ReferenceRecord>(StringComparer.OrdinalIgnoreCase);
                _loaded = false;
            }
        }

        /// <summary>CSV override (if present and non-empty) → built-in SectorMap → "Unclassified".</summary>
        public static string GetSector(string symbol)
        {
            if (_records.TryGetValue(symbol, out var rec) && !string.IsNullOrWhiteSpace(rec.Sector))
                return rec.Sector!;
            return SectorMap.TryGetBuiltin(symbol) ?? "Unclassified";
        }

        public static ReferenceRecord? TryGetRecord(string symbol) =>
            _records.TryGetValue(symbol, out var rec) ? rec : null;

        private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static double? ParseNullableDouble(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

        private static DateTime? ParseNullableDate(string s) =>
            DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d.Date : null;

        /// <summary>Minimal CSV splitter — handles quoted cells with embedded commas, which is
        /// all this file's simple numeric/date/symbol columns ever need. Not a full RFC 4180
        /// parser (no embedded newlines inside quotes), which is fine for this use case.</summary>
        private static List<string> SplitCsvLine(string line)
        {
            var cells = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            foreach (char c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == ',' && !inQuotes) { cells.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            cells.Add(sb.ToString());
            return cells;
        }
    }

    // =========================================================================
    //  YAHOO FINANCE INTERVAL HELPER
    // =========================================================================
    internal static class YahooIntervalHelper
    {
        private static readonly Dictionary<YahooInterval, string> _map = new()
        {
            [YahooInterval.OneMinute]      = "1m",  [YahooInterval.TwoMinutes]    = "2m",
            [YahooInterval.FiveMinutes]    = "5m",  [YahooInterval.FifteenMinutes]= "15m",
            [YahooInterval.ThirtyMinutes]  = "30m", [YahooInterval.SixtyMinutes]  = "60m",
            [YahooInterval.NinetyMinutes]  = "90m", [YahooInterval.OneHour]       = "1h",
            [YahooInterval.OneDay]         = "1d",  [YahooInterval.FiveDay]       = "5d",
            [YahooInterval.OneWeek]        = "1wk", [YahooInterval.OneMonth]      = "1mo",
            [YahooInterval.ThreeMonths]    = "3mo",
        };
        public static string ToApiString(YahooInterval iv) =>
            _map.TryGetValue(iv, out var s) ? s : "1d";
        public static bool IsIntraday(YahooInterval iv) =>
            iv is YahooInterval.OneMinute or YahooInterval.TwoMinutes
              or YahooInterval.FiveMinutes or YahooInterval.FifteenMinutes
              or YahooInterval.ThirtyMinutes or YahooInterval.SixtyMinutes
              or YahooInterval.NinetyMinutes or YahooInterval.OneHour;
    }

    // =========================================================================
    //  YAHOO FINANCE DATA FETCHER
    //  · No inner SemaphoreSlim (Parallel.ForEachAsync DOP already limits concurrency)
    //  · HTTP 429 / Retry-After detection with exponential + full-jitter backoff
    //  · Tries query1 then query2 endpoints
    //  · v4.3: HTTP/2 request multiplexing + a shared, coordinated rate-limit cooldown
    //    (see WaitForGlobalCooldownAsync/ExtendGlobalCooldown) — both added so
    //    MaxConcurrentFetches could safely go up without just hammering Yahoo harder.
    // =========================================================================
    internal sealed class YahooDataFetcher : IDisposable
    {
        private static readonly string[] _hosts =
            { "query1.finance.yahoo.com", "query2.finance.yahoo.com" };

        private readonly HttpClient _http;
        // Shared RNG — safe for concurrent reads with Random.Shared
        private static readonly Random _rng = Random.Shared;

        // ── v4.3: coordinated rate-limit cooldown ───────────────────────────────
        // One YahooDataFetcher instance is shared by every concurrent worker in
        // ScanEngine's Parallel.ForEachAsync, so this field is genuinely shared state.
        // Before this existed, each worker handled its own 429s independently — with
        // MaxConcurrentFetches raised, several workers could all get rate-limited within
        // the same second, each wait out its own Retry-After, and then all resume hitting
        // Yahoo again in the same instant, which is exactly the kind of synchronized burst
        // that turns a transient rate-limit into a longer one. Now a 429 from *any* worker
        // pauses *every* worker's next request until the shared cooldown clears, so the
        // whole scan backs off and resumes together instead of each ticker fighting its own
        // fire alone. Read/written only via Interlocked — plain long read/write isn't
        // guaranteed atomic on 32-bit platforms, and Interlocked is what the rest of this
        // codebase already uses for shared counters (see ScanEngine's progress counter).
        private long _globalCooldownUntilTicks;

        private async Task WaitForGlobalCooldownAsync(CancellationToken ct)
        {
            while (true)
            {
                long untilTicks = Interlocked.Read(ref _globalCooldownUntilTicks);
                long nowTicks   = DateTime.UtcNow.Ticks;
                if (untilTicks <= nowTicks) return;
                var wait = TimeSpan.FromTicks(untilTicks - nowTicks);
                if (wait <= TimeSpan.FromMilliseconds(50)) return; // close enough, avoid delay churn
                await Task.Delay(wait, ct);
            }
        }

        /// <summary>Extends the shared cooldown to at least now+<paramref name="seconds"/>.
        /// Never shortens it — a compare-and-swap loop so two workers hitting 429 at nearly
        /// the same moment can only push the cooldown later, never accidentally stomp a
        /// longer wait with a shorter one (the classic lost-update race a plain field write
        /// would allow).</summary>
        private void ExtendGlobalCooldown(int seconds)
        {
            long candidate = DateTime.UtcNow.AddSeconds(seconds).Ticks;
            long current;
            do
            {
                current = Interlocked.Read(ref _globalCooldownUntilTicks);
                if (candidate <= current) return;
            } while (Interlocked.CompareExchange(ref _globalCooldownUntilTicks, candidate, current) != current);
        }

        public YahooDataFetcher()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Config.FetchTimeoutSeconds),
                // v4.3: prefer HTTP/2 (single multiplexed connection per host instead of one
                // TCP+TLS handshake per concurrent request) with a safe fallback to HTTP/1.1
                // if Yahoo's endpoint doesn't negotiate it — RequestVersionOrLower never
                // fails a request over this, it just doesn't get the multiplexing win.
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionOrLower,
            };
            _http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
            _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        }

        public async Task<List<StockQuote>> FetchAsync(
            string ticker, YahooInterval interval, int lookbackDays,
            CancellationToken ct = default)
        {
            // Weekly/monthly bars barely change within the same day — cache them briefly to
            // cut redundant network calls when the scanner is re-run multiple times a day
            // (and to offset the extra request volume the monthly timeframe adds overall).
            // Daily/intraday are always fetched live — that's the whole point of the tool.
            bool cacheable = Config.CacheEnabled &&
                (interval == YahooInterval.OneWeek || interval == YahooInterval.OneMonth);

            if (cacheable && TryReadCache(ticker, interval, out var cached))
                return cached;

            var quotes = await FetchFromNetworkAsync(ticker, interval, lookbackDays, ct);

            if (cacheable && quotes.Count > 0)
                await TryWriteCacheAsync(ticker, interval, quotes, ct);

            return quotes;
        }

        private async Task<List<StockQuote>> FetchFromNetworkAsync(
            string ticker, YahooInterval interval, int lookbackDays,
            CancellationToken ct)
        {
            long period2    = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long period1    = DateTimeOffset.UtcNow.AddDays(-lookbackDays).ToUnixTimeSeconds();
            string intStr   = YahooIntervalHelper.ToApiString(interval);
            bool   isIntra  = YahooIntervalHelper.IsIntraday(interval);
            // Uri.EscapeDataString handles & in M&M.NS → M%26M.NS correctly
            string escaped  = Uri.EscapeDataString(ticker);

            Exception? lastEx = null;
            int totalAttempts = Config.MaxRetries + 2; // budget for real (non-429) failures

            // 429s are rate-limit backpressure, not failures — they get their own small,
            // separate retry budget so a burst of rate-limiting can't silently eat into (or
            // exhaust) the real-failure budget above. The `for` loop's `attempt++` runs even on
            // `continue`, so the previous version's "retry without decrementing effective
            // budget" comment didn't actually hold — a run of 429s could consume the whole
            // totalAttempts budget on rate-limit waits alone, leaving zero attempts for an
            // actual transient network error and causing tickers to be skipped that a bit more
            // patience would have recovered. Tracked as its own counter instead.
            int rateLimitRetries = 0;
            const int maxRateLimitRetries = 6;

            int attempt = 0;
            while (attempt < totalAttempts)
            {
                ct.ThrowIfCancellationRequested();
                // v4.3: honor any cooldown another worker's 429 may have just set — see the
                // class-level comment on _globalCooldownUntilTicks for why this exists.
                await WaitForGlobalCooldownAsync(ct);
                string host = _hosts[attempt % _hosts.Length];
                string url  = $"https://{host}/v8/finance/chart/{escaped}" +
                              $"?period1={period1}&period2={period2}&interval={intStr}" +
                              $"&events=history&includeAdjustedClose=true";
                try
                {
                    using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                    // Handle rate limiting specifically — doesn't consume the real-attempt budget.
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        if (++rateLimitRetries > maxRateLimitRetries)
                        {
                            lastEx = new HttpRequestException("HTTP 429 (rate-limit retry budget exhausted)");
                            break;
                        }
                        int waitSec = (int)(resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60);
                        waitSec = Math.Clamp(waitSec, 30, 300);
                        // Pauses every concurrent worker's next request, not just this one —
                        // the actual wait happens at the top of this loop (and every other
                        // in-flight worker's loop) via WaitForGlobalCooldownAsync.
                        ExtendGlobalCooldown(waitSec);
                        Log.Warning("429 for {Ticker} — pausing all workers {Sec}s then retrying ({N}/{Max})",
                            ticker, waitSec, rateLimitRetries, maxRateLimitRetries);
                        continue; // does not increment `attempt` — real budget untouched
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        lastEx = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                        await ApplyBackoff(attempt, ct);
                        attempt++;
                        continue;
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    return ParseQuotes(stream, isIntra);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastEx = ex;
                    await ApplyBackoff(attempt, ct);
                    attempt++;
                }
            }

            Log.Warning("Gave up on {Ticker} ({Interval}) after {N} attempts ({R} rate-limit waits): {Msg}",
                ticker, intStr, attempt, rateLimitRetries, lastEx?.Message);
            return new List<StockQuote>();
        }

        // ── Slow-timeframe disk cache ─────────────────────────────────────────
        private sealed class CachedQuotes
        {
            public DateTime CachedAtUtc { get; set; }
            public List<StockQuote> Quotes { get; set; } = new();
        }

        private static string CachePath(string ticker, YahooInterval interval)
        {
            var safeTicker = new string(ticker.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
            return Path.Combine(Config.CacheBaseDir, $"{safeTicker}_{YahooIntervalHelper.ToApiString(interval)}.json");
        }

        private static bool TryReadCache(string ticker, YahooInterval interval, out List<StockQuote> quotes)
        {
            quotes = new List<StockQuote>();
            try
            {
                var path = CachePath(ticker, interval);
                if (!File.Exists(path)) return false;

                var json   = File.ReadAllText(path);
                var cached = JsonSerializer.Deserialize<CachedQuotes>(json);
                if (cached == null) return false;

                if ((DateTime.UtcNow - cached.CachedAtUtc).TotalHours > Config.SlowTfCacheHours)
                    return false; // expired

                quotes = cached.Quotes;
                return quotes.Count > 0;
            }
            catch (Exception ex)
            {
                // Cache corruption/IO issues should never break a live fetch — just miss.
                Log.Debug("Cache read miss for {Ticker} [{Iv}]: {Msg}", ticker, interval, ex.Message);
                return false;
            }
        }

        private static async Task TryWriteCacheAsync(
            string ticker, YahooInterval interval, List<StockQuote> quotes, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(Config.CacheBaseDir);
                var payload = new CachedQuotes { CachedAtUtc = DateTime.UtcNow, Quotes = quotes };
                var json    = JsonSerializer.Serialize(payload);
                await File.WriteAllTextAsync(CachePath(ticker, interval), json, ct);
            }
            catch (Exception ex)
            {
                Log.Debug("Cache write failed for {Ticker} [{Iv}]: {Msg}", ticker, interval, ex.Message);
            }
        }

        // Full-jitter exponential backoff: wait = random[0, min(cap, base * 2^attempt)]
        private static async Task ApplyBackoff(int attempt, CancellationToken ct)
        {
            int cap    = 16_000; // 16s max
            int ceiling = Math.Min(cap, Config.RetryBaseDelayMs * (1 << Math.Min(attempt, 12)));
            int delay  = _rng.Next(0, ceiling);
            await Task.Delay(delay, ct);
        }

        private static List<StockQuote> ParseQuotes(Stream stream, bool isIntraday)
        {
            using var doc  = JsonDocument.Parse(stream);
            var result_arr = doc.RootElement
                .GetProperty("chart")
                .GetProperty("result");
            if (result_arr.GetArrayLength() == 0) return new List<StockQuote>();

            var root       = result_arr[0];
            if (!root.TryGetProperty("timestamp", out var tsEl)) return new List<StockQuote>();
            var timestamps = tsEl.EnumerateArray().Select(e => e.GetInt64()).ToList();
            var indicatorsRoot = root.GetProperty("indicators");
            var quoteEl    = indicatorsRoot.GetProperty("quote")[0];

            var opens   = ToDecimalArray(quoteEl, "open");
            var highs   = ToDecimalArray(quoteEl, "high");
            var lows    = ToDecimalArray(quoteEl, "low");
            var closes  = ToDecimalArray(quoteEl, "close");
            var volumes = ToDecimalArray(quoteEl, "volume");

            // Adjusted close (splits/bonuses/dividends) — requested via includeAdjustedClose=true.
            // Back-adjust the whole OHLC bar by the same ratio so a split doesn't look like a
            // 50%+ single-bar crash/gap and corrupt every indicator built on raw price.
            decimal[] adjCloses = Array.Empty<decimal>();
            if (indicatorsRoot.TryGetProperty("adjclose", out var adjWrapArr) &&
                adjWrapArr.GetArrayLength() > 0 &&
                adjWrapArr[0].TryGetProperty("adjclose", out var adjCloseArr))
            {
                var items = adjCloseArr.EnumerateArray().ToArray();
                adjCloses = new decimal[items.Length];
                for (int k = 0; k < items.Length; k++)
                    adjCloses[k] = items[k].ValueKind == JsonValueKind.Null ? 0m : (decimal)items[k].GetDouble();
            }

            int count = Math.Min(timestamps.Count,
                        Math.Min(opens.Length, Math.Min(closes.Length, volumes.Length)));

            var list = new List<StockQuote>(count);
            for (int i = 0; i < count; i++)
            {
                if (closes[i] <= 0 || opens[i] <= 0) continue;

                decimal rawClose = closes[i];
                decimal o = opens[i];
                decimal h = highs[i] > 0 ? highs[i] : rawClose;
                decimal l = lows[i]  > 0 ? lows[i]  : rawClose;
                decimal c = rawClose;

                if (i < adjCloses.Length && adjCloses[i] > 0)
                {
                    decimal ratio = adjCloses[i] / rawClose;
                    // Sanity guard: only apply plausible corporate-action ratios; ignore junk data
                    if (ratio > 0.01m && ratio < 100m)
                    {
                        o *= ratio; h *= ratio; l *= ratio; c = adjCloses[i];
                    }
                }

                var dt = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime;
                // NSE data from Yahoo is already in IST for daily; intraday bars need adjustment
                if (isIntraday) dt = dt.AddHours(5).AddMinutes(30);
                list.Add(new StockQuote { Date = dt, Open = o, High = h, Low = l, Close = c, Volume = volumes[i] });
            }
            // Ensure chronological order
            list.Sort((a, b) => a.Date.CompareTo(b.Date));
            return list;
        }

        private static decimal[] ToDecimalArray(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var arr)) return Array.Empty<decimal>();
            var items  = arr.EnumerateArray().ToArray();
            var result = new decimal[items.Length];
            for (int i = 0; i < items.Length; i++)
                result[i] = items[i].ValueKind == JsonValueKind.Null ? 0m
                           : (decimal)items[i].GetDouble();
            return result;
        }

        public void Dispose() => _http.Dispose();
    }

    // =========================================================================
    //  DATA QUALITY VALIDATOR
    // =========================================================================
    internal static class DataValidator
    {
        public static DataQuality Validate(List<StockQuote> quotes, YahooInterval interval)
        {
            var dq = new DataQuality { TotalBars = quotes.Count };

            if (quotes.Count < Config.MinQuotesRequired)
            {
                dq.IsValid = false;
                dq.FailReason = $"Only {quotes.Count} bars (min {Config.MinQuotesRequired})";
                return dq;
            }

            // Freshness check (skip on weekends — last bar will be Friday).
            // A monthly bar dated the 1st of the month, or a weekly bar dated Monday of the
            // current week, is NOT stale even though many calendar days have passed — the
            // staleness allowance must scale with the bar's own period, not a flat daily value.
            double maxStaleDays = interval switch
            {
                YahooInterval.OneMonth => 35,                          // full month + buffer
                YahooInterval.OneWeek  => 12,                          // full week + weekend + buffer
                _                      => Config.MaxStaleDataDays + 2, // daily/intraday, allow for weekends
            };

            var lastDate = quotes[^1].Date;
            dq.DataAgeDays = (DateTime.UtcNow - lastDate).TotalDays;
            if (dq.DataAgeDays > maxStaleDays)
            {
                dq.IsValid = false;
                dq.FailReason = $"Stale: last={lastDate:yyyy-MM-dd} ({dq.DataAgeDays:F0}d ago)";
                return dq;
            }

            // Zero-volume bars (>20% invalid)
            dq.ZeroVolumeBars = 0;
            for (int i = 0; i < quotes.Count; i++)
                if (quotes[i].Volume == 0) dq.ZeroVolumeBars++;

            if (dq.ZeroVolumeBars > quotes.Count * 0.20)
            {
                dq.IsValid = false;
                dq.FailReason = $"{dq.ZeroVolumeBars}/{quotes.Count} zero-volume bars";
                return dq;
            }

            // Price integrity (OHLC consistency)
            for (int i = 0; i < quotes.Count; i++)
            {
                var q = quotes[i];
                if (q.High < q.Low || q.High < q.Close || q.High < q.Open ||
                    q.Low  > q.Close || q.Low  > q.Open)
                {
                    dq.IsValid = false;
                    dq.FailReason = $"OHLC integrity failure at {q.Date:yyyy-MM-dd}";
                    return dq;
                }
            }

            // Compute max single-bar gap (for logging only, not a hard fail)
            for (int i = 1; i < quotes.Count; i++)
            {
                if (quotes[i - 1].Close <= 0) continue;
                double gap = Math.Abs((double)(quotes[i].Close - quotes[i - 1].Close)
                                      / (double)quotes[i - 1].Close * 100.0);
                if (gap > dq.MaxSingleGapPct) dq.MaxSingleGapPct = gap;
            }

            return dq;
        }
    }

    // =========================================================================
    //  PRICE LEVEL CALCULATOR  (index-based — zero allocation per date)
    // =========================================================================
    internal static class PriceLevelCalculator
    {
        public static PriceLevels Calculate(List<StockQuote> quotes, int cutoffIdx)
        {
            if (cutoffIdx < 0 || quotes.Count == 0) return new PriceLevels();

            var q        = quotes[cutoffIdx];
            decimal cls  = q.Close;
            DateTime now = q.Date;

            DateTime weekStart = now.AddDays(-7);
            DateTime monStart  = now.AddDays(-30);
            DateTime qtrStart  = now.AddDays(-90);
            DateTime yrStart   = now.AddDays(-365);

            decimal wH = 0, wL = decimal.MaxValue;
            decimal mH = 0, mL = decimal.MaxValue;
            decimal qH = 0, qL = decimal.MaxValue;
            decimal yH = 0, yL = decimal.MaxValue;

            // Single backward pass — break as soon as past yearly window
            for (int i = cutoffIdx; i >= 0; i--)
            {
                var b = quotes[i];
                if (b.Date < yrStart) break;

                if (b.High > yH) yH = b.High;
                if (b.Low  < yL) yL = b.Low;

                if (b.Date >= qtrStart)
                {
                    if (b.High > qH) qH = b.High;
                    if (b.Low  < qL) qL = b.Low;
                }
                if (b.Date >= monStart)
                {
                    if (b.High > mH) mH = b.High;
                    if (b.Low  < mL) mL = b.Low;
                }
                if (b.Date >= weekStart)
                {
                    if (b.High > wH) wH = b.High;
                    if (b.Low  < wL) wL = b.Low;
                }
            }

            if (yH == 0)              yH = cls;
            if (yL == decimal.MaxValue) yL = cls;
            if (mH == 0)              mH = cls;
            if (mL == decimal.MaxValue) mL = cls;
            if (qH == 0)              qH = cls;
            if (qL == decimal.MaxValue) qL = cls;
            if (wH == 0)              wH = cls;
            if (wL == decimal.MaxValue) wL = cls;

            static double Pct(decimal from, decimal to) =>
                from == 0 ? 0 : Math.Round((double)(to - from) / (double)from * 100, 2);

            return new PriceLevels
            {
                WeeklyHigh = wH, WeeklyLow = wL,
                MonthlyHigh = mH, MonthlyLow = mL,
                QuarterlyHigh = qH, QuarterlyLow = qL,
                YearlyHigh = yH, YearlyLow = yL,
                PctFrom52wHigh   = Pct(yH, cls),
                PctFrom52wLow    = Pct(yL, cls),
                PctFromMonthHigh = Pct(mH, cls),
                PctFromWeekHigh  = Pct(wH, cls),
            };
        }
    }

    // =========================================================================
    //  SUPERTREND METRICS  (index-based, off-by-one fixed)
    //  · Uses flipIdx+1 as new-trend window start (was flipIdx — wrong bar)
    // =========================================================================
    internal static class SupertrendMetricsCalculator
    {
        public static SupertrendMetrics Calculate(
            List<StockQuote>  quotes,
            SuperTrendResult[] stArr,
            int cutoffIdx)
        {
            var m = new SupertrendMetrics();
            if (cutoffIdx < 1 || stArr.Length <= cutoffIdx) return m;

            m.CurrentlyBullish = stArr[cutoffIdx].UpperBand == null;
            bool cur = m.CurrentlyBullish;

            // Walk backwards: find last bar where direction differed from current
            int flipIdx = 0; // index of last OLD-trend bar
            for (int i = cutoffIdx - 1; i >= 0; i--)
            {
                if (stArr[i].SuperTrend == null) continue;
                if ((stArr[i].UpperBand == null) != cur) { flipIdx = i; break; }
            }

            // FIX: window starts at flipIdx+1 (first bar of NEW trend)
            int windowStart = flipIdx > 0 ? flipIdx + 1 : 0;
            m.LastFlipDate          = quotes[windowStart].Date;
            m.CalendarDaysSinceFlip = (int)(quotes[cutoffIdx].Date - m.LastFlipDate).TotalDays;

            // Count unique trading calendar dates (O(n) but small window)
            int uniqueDates = 1;
            DateTime prevDate = quotes[windowStart].Date.Date;
            for (int i = windowStart + 1; i <= cutoffIdx; i++)
            {
                var d = quotes[i].Date.Date;
                if (d != prevDate) { uniqueDates++; prevDate = d; }
            }
            m.TradingDaysInTrend = uniqueDates;

            // Extremes within new-trend window
            decimal hiHi = decimal.MinValue, loLo = decimal.MaxValue;
            decimal hiLo = decimal.MinValue, loHi = decimal.MaxValue;
            int hiHiAgo = 0, loLoAgo = 0, hiLoAgo = 0, loHiAgo = 0;
            DateTime today = quotes[cutoffIdx].Date;

            for (int i = windowStart; i <= cutoffIdx; i++)
            {
                var b = quotes[i];
                int ago = (int)(today - b.Date).TotalDays;
                if (b.High > hiHi) { hiHi = b.High; hiHiAgo = ago; }
                if (b.Low  < loLo) { loLo = b.Low;  loLoAgo = ago; }
                if (b.Low  > hiLo) { hiLo = b.Low;  hiLoAgo = ago; }
                if (b.High < loHi) { loHi = b.High; loHiAgo = ago; }
            }

            m.HighestHigh        = hiHi == decimal.MinValue ? 0 : hiHi;
            m.HighestHighDaysAgo = hiHiAgo;
            m.HighestLow         = hiLo == decimal.MinValue ? 0 : hiLo;
            m.HighestLowDaysAgo  = hiLoAgo;
            m.LowestLow          = loLo == decimal.MaxValue ? 0 : loLo;
            m.LowestLowDaysAgo   = loLoAgo;
            m.LowestHigh         = loHi == decimal.MaxValue ? 0 : loHi;
            m.LowestHighDaysAgo  = loHiAgo;

            return m;
        }
    }

    // =========================================================================
    //  VCP DETECTOR  (index-based — no List.GetRange allocations)
    // =========================================================================
    internal static class VcpDetector
    {
        public static bool Detect(List<StockQuote> quotes, int startIdx, int endIdx)
        {
            if (endIdx - startIdx + 1 < Config.VcpLookbackDays) return false;
            var swings = FindSwings(quotes, startIdx, endIdx);
            if (swings.Count < Config.VcpMinSwings * 2) return false;
            return CheckVolatilityContraction(swings, quotes)
                && CheckVolumeDryUp(quotes, startIdx, endIdx);
        }

        private static List<int> FindSwings(List<StockQuote> q, int s, int e)
        {
            var out_ = new List<int>();
            int n = Config.VcpSwingSensitivity;
            for (int i = s + n; i <= e - n; i++)
            {
                bool isHi = true, isLo = true;
                for (int j = i - n; j <= i + n && (isHi || isLo); j++)
                {
                    if (j == i) continue;
                    if (q[j].High >= q[i].High) isHi = false;
                    if (q[j].Low  <= q[i].Low)  isLo = false;
                }
                if (isHi || isLo) out_.Add(i);
            }
            return out_;
        }

        private static bool CheckVolatilityContraction(List<int> sw, List<StockQuote> q)
        {
            if (sw.Count < 4) return false;
            var r = sw.TakeLast(4).ToArray();
            if (q[r[0]].Low <= 0 || q[r[2]].Low <= 0) return false;
            double rF = (double)(q[r[1]].High - q[r[0]].Low) / (double)q[r[0]].Low;
            double rL = (double)(q[r[3]].High - q[r[2]].Low) / (double)q[r[2]].Low;
            return rL <= rF * Config.VcpVolatilityRatio;
        }

        private static bool CheckVolumeDryUp(List<StockQuote> q, int s, int e)
        {
            int mid = (s + e) / 2;
            double eSum = 0, lSum = 0; int eN = 0, lN = 0;
            for (int i = s;     i <= mid; i++) { eSum += (double)q[i].Volume; eN++; }
            for (int i = mid+1; i <= e;   i++) { lSum += (double)q[i].Volume; lN++; }
            double eAvg = eN > 0 ? eSum / eN : 0;
            double lAvg = lN > 0 ? lSum / lN : 0;
            return eAvg > 0 && lAvg < eAvg * Config.VcpVolumeDropRatio;
        }
    }

    // =========================================================================
    //  MARKET REGIME ANALYZER  (v4.0)
    //  Classifies the broad market off a single benchmark index fetch, so BUY
    //  generation can respect "don't fight the market" instead of scoring every
    //  stock in a vacuum. Also holds the benchmark's own daily closes for the
    //  relative-strength calculator below (one shared series, not refetched
    //  per ticker).
    // =========================================================================
    internal static class MarketRegimeAnalyzer
    {
        /// <summary>
        /// Regime classification off the benchmark's own trend structure:
        ///   BULLISH — close above both EMA50 and EMA200, and EMA50 above EMA200 (uptrend intact)
        ///   BEARISH — close below EMA200 (the standard long-term trend line)
        ///   NEUTRAL — everything else (e.g. above EMA200 but below EMA50, or EMAs crossed but
        ///             price hasn't confirmed either way) — a chop/transition zone
        /// </summary>
        public static string Classify(decimal close, double ema50, double ema200)
        {
            if (ema200 <= 0) return "NEUTRAL"; // not enough history yet
            if ((double)close > ema50 && ema50 > ema200) return "BULLISH";
            if ((double)close < ema200) return "BEARISH";
            return "NEUTRAL";
        }

        /// <summary>
        /// Fetches the benchmark once, computes EMA50/EMA200 across its whole history, and
        /// returns both the regime for <paramref name="asOf"/> and the full date→close map the
        /// RS calculator needs. Never throws — a benchmark fetch failure degrades to a NEUTRAL
        /// regime (no gating) rather than blocking the whole scan.
        /// </summary>
        public static async Task<(MarketRegimeInfo Regime, Dictionary<DateTime, decimal> CloseByDate)>
            BuildAsync(YahooDataFetcher fetcher, DateTime asOf, CancellationToken ct)
        {
            var info = new MarketRegimeInfo { ScanDate = asOf, BenchmarkSymbol = Config.MarketBenchmarkSymbol };
            var closeByDate = new Dictionary<DateTime, decimal>();

            try
            {
                var quotes = await fetcher.FetchAsync(
                    Config.MarketBenchmarkSymbol, YahooInterval.OneDay,
                    Config.MarketRegimeLookbackDays, ct);

                if (quotes.Count < Config.MarketRegimeEmaSlowPeriod)
                {
                    Log.Warning("Benchmark {Sym} has only {N} bars — regime defaults to NEUTRAL " +
                                "(no gating applied) until enough history is available.",
                                Config.MarketBenchmarkSymbol, quotes.Count);
                    return (info, closeByDate);
                }

                foreach (var q in quotes) closeByDate[q.Date.Date] = q.Close;

                var ema50  = quotes.GetEma(Config.MarketRegimeEmaFastPeriod).ToArray();
                var ema200 = quotes.GetEma(Config.MarketRegimeEmaSlowPeriod).ToArray();

                int last = quotes.Count - 1;
                double e50  = ema50[last].Ema  ?? 0;
                double e200 = ema200[last].Ema ?? 0;

                info.BenchmarkClose = quotes[last].Close;
                info.Ema50          = e50;
                info.Ema200         = e200;
                info.Regime         = Classify(quotes[last].Close, e50, e200);
            }
            catch (Exception ex)
            {
                Log.Warning("Benchmark fetch failed ({Sym}): {Msg} — regime defaults to NEUTRAL.",
                    Config.MarketBenchmarkSymbol, ex.Message);
            }

            return (info, closeByDate);
        }

        /// <summary>Fills in breadth (computed from the universe's own daily results) — done
        /// separately from BuildAsync since breadth needs every ticker already scored.</summary>
        public static void ApplyBreadth(MarketRegimeInfo info, int bullish, int bearish, int total)
        {
            info.BreadthBullish    = bullish;
            info.BreadthBearish    = bearish;
            info.BreadthTotal      = total;
            info.BreadthPctBullish = total > 0 ? Math.Round(bullish * 100.0 / total, 1) : 0;
        }
    }

    // =========================================================================
    //  RELATIVE STRENGTH CALCULATOR  (v4.0, IBD-style)
    //  Blends trailing returns over ~3/6/9/12 months against the benchmark's return over the
    //  same window, weighting the most recent quarter double the other three — the standard
    //  IBD RS Rating construction. Produces a raw per-stock number; MultiTfAggregator converts
    //  that into a cross-sectional 1-99 percentile rank once every ticker for the date is in.
    //  Index-based, no allocations beyond the small period array — safe to call per scored date.
    // =========================================================================
    internal static class RelativeStrengthCalculator
    {
        public static double ComputeRaw(
            List<StockQuote> quotes, int cutoffIdx,
            IReadOnlyDictionary<DateTime, decimal> benchmarkCloseByDate,
            out bool hasData)
        {
            hasData = false;
            if (cutoffIdx <= 0 || benchmarkCloseByDate.Count == 0) return 0;

            decimal closeNow = quotes[cutoffIdx].Close;
            if (!benchmarkCloseByDate.TryGetValue(quotes[cutoffIdx].Date.Date, out var benchNow))
                return 0; // no matching benchmark bar for this date (holiday mismatch etc.)

            double blended = 0, weightUsed = 0;
            var periods = Config.RsLookbackTradingDays;
            var weights = Config.RsLookbackWeights;

            for (int p = 0; p < periods.Length; p++)
            {
                int lookIdx = cutoffIdx - periods[p];
                if (lookIdx < 0) continue; // not enough history for this window yet — skip it

                decimal closeThen = quotes[lookIdx].Close;
                if (closeThen <= 0) continue;
                if (!benchmarkCloseByDate.TryGetValue(quotes[lookIdx].Date.Date, out var benchThen) || benchThen <= 0)
                    continue;

                double stockRet = (double)((closeNow - closeThen) / closeThen);
                double benchRet = (double)((benchNow - benchThen) / benchThen);
                double relative = stockRet - benchRet; // outperformance vs benchmark over this window

                blended += relative * weights[p];
                weightUsed += weights[p];
            }

            // Renormalize if some windows were skipped (thin history) so a new-ish listing still
            // gets a usable (if less confident) RS figure from whatever windows it does have.
            if (weightUsed <= 0) return 0;
            hasData = true;
            return blended / weightUsed;
        }
    }

    // =========================================================================
    //  TICKER SCORER  (index-based, zero List.GetRange / Take().ToList())
    // =========================================================================
    internal static class TickerScorer
    {
        public static List<ScanResult> Score(
            string           symbol,
            YahooInterval    interval,
            string           tfLabel,
            bool             vcpEnabled,
            List<StockQuote> quotes,
            DateTime         scoreStart,
            DateTime         scoreEnd,
            IReadOnlyDictionary<DateTime, decimal>? benchmarkCloseByDate = null)
        {
            var results = new List<ScanResult>();
            if (quotes.Count < Config.MinQuotesRequired) return results;

            // RS/liquidity/extension/ATR are computed only on the daily timeframe: it's the only
            // one with a matching benchmark series and a stable, non-noisy ATR. Other timeframes
            // keep these fields at their zero/false defaults.
            bool isDaily = tfLabel == "1d";

            // Pre-compute ALL indicators once — O(n) per indicator, no per-date work
            var ind = IndicatorArrays.Compute(quotes);

            int minBars = interval switch
            {
                YahooInterval.OneDay   => Config.MinBarsDaily,
                YahooInterval.OneWeek  => Config.MinBarsWeekly,
                YahooInterval.OneMonth => Config.MinBarsMonthly,
                _                      => Config.MinBarsIntraday,
            };

            // Map each calendar date → last quote index on that date
            // (for intraday: last bar = end-of-day snapshot; for daily: the bar itself)
            var dateToLastIdx = new Dictionary<DateTime, int>(quotes.Count);
            for (int idx = 0; idx < quotes.Count; idx++)
                dateToLastIdx[quotes[idx].Date.Date] = idx; // last index per date wins

            var datesToScore = dateToLastIdx.Keys
                .Where(d => d >= scoreStart.Date && d <= scoreEnd.Date)
                .OrderBy(d => d)
                .ToList();

            foreach (var scoreDate in datesToScore)
            {
                int i = dateToLastIdx[scoreDate];
                if (i < minBars) continue;

                var q = quotes[i];

                // ── Indicators ───────────────────────────────────────────────
                bool bull = i < ind.SuperTrend.Length && ind.SuperTrend[i].UpperBand == null;
                decimal stVal = i < ind.SuperTrend.Length
                    ? (ind.SuperTrend[i].SuperTrend ?? 0m) : 0m;

                double hmaF = i < ind.HmaFast.Length ? (ind.HmaFast[i].Hma ?? 0) : 0;
                double hmaS = i < ind.HmaSlow.Length ? (ind.HmaSlow[i].Hma ?? 0) : 0;
                double adx  = i < ind.Adx.Length ? (ind.Adx[i].Adx ?? 0) : 0;
                double pdi  = i < ind.Adx.Length ? (ind.Adx[i].Pdi ?? 0) : 0;
                double mdi  = i < ind.Adx.Length ? (ind.Adx[i].Mdi ?? 0) : 0;
                double ef   = i < ind.EmaFast.Length ? (ind.EmaFast[i].Ema ?? 0) : 0;
                double es   = i < ind.EmaSlow.Length ? (ind.EmaSlow[i].Ema ?? 0) : 0;

                double volSma   = ind.VolumeSma[i];
                double volRatio = volSma > 0 ? (double)q.Volume / volSma : 0;

                bool vcpHit = false;
                if (vcpEnabled && i >= Config.VcpLookbackDays - 1)
                    vcpHit = VcpDetector.Detect(quotes, i - Config.VcpLookbackDays + 1, i);

                bool trendOk = (double)q.Close > ef && ef > es && es > 0;

                // ── Scoring ──────────────────────────────────────────────────
                double sSt    = bull          ? Config.SupertrendWeight     : -Config.SupertrendWeight;
                double sHma   = hmaF > hmaS   ? Config.HmaCrossoverWeight  : -Config.HmaCrossoverWeight;
                double sAdx   = adx >= Config.AdxStrongThreshold
                    ? (pdi > mdi ? Config.AdxWeight : -Config.AdxWeight) : 0;
                double sVcp   = vcpHit ? Config.VcpWeight : 0;
                double sVol   = volRatio >= Config.VolumeSurgeRatio   ?  Config.VolumeWeight
                              : volRatio <  Config.VolumeLowRatio     ? -(Config.VolumeWeight / 2) : 0;
                double sTrend = trendOk ? Config.TrendAlignmentWeight : -Config.TrendAlignmentWeight;
                double total  = sSt + sHma + sAdx + sVcp + sVol + sTrend;

                // ── Index-based analytics (no allocations) ───────────────────
                var levels  = PriceLevelCalculator.Calculate(quotes, i);
                var stm     = SupertrendMetricsCalculator.Calculate(quotes, ind.SuperTrend, i);

                // ── v4.0: relative strength / liquidity / extension / ATR risk levels ────
                double rsRaw = 0, avgTurnover = 0, extensionPct = 0, atrVal = 0, stop = 0, target = 0;
                bool liquid = true, extended = false, hasRs = false;

                if (isDaily)
                {
                    if (benchmarkCloseByDate != null)
                        rsRaw = RelativeStrengthCalculator.ComputeRaw(quotes, i, benchmarkCloseByDate, out hasRs);

                    avgTurnover = volSma * (double)q.Close;
                    liquid = avgTurnover >= Config.MinAvgTurnoverInr && (double)q.Close >= Config.MinPriceInr;

                    // Extension: how far price has run above the 50-EMA since a fresh entry near
                    // the EMA is a very different trade than chasing one already 25%+ above it.
                    if (ef > 0)
                    {
                        extensionPct = ((double)q.Close - ef) / ef * 100.0;
                        extended = extensionPct > Config.MaxExtensionAboveEma50Pct;
                    }

                    atrVal = i < ind.Atr.Length ? (ind.Atr[i].Atr ?? 0) : 0;
                    if (atrVal > 0)
                    {
                        stop   = (double)q.Close - atrVal * Config.LiveAtrStopMultiplier;
                        target = (double)q.Close + atrVal * Config.LiveAtrStopMultiplier * Config.LiveRiskRewardRatio;
                    }
                }

                results.Add(new ScanResult
                {
                    Symbol = symbol, ScanDate = scoreDate, Timeframe = tfLabel,
                    TotalScore = total, SupertrendScore = sSt, HmaScore = sHma,
                    AdxScore = sAdx, VcpScore = sVcp, VolumeScore = sVol, TrendScore = sTrend,
                    IsBuyCandidate   = total >= 40,
                    IsShortCandidate = total <= -30,
                    LastClose = q.Close, LastOpen = q.Open, LastHigh = q.High,
                    LastLow = q.Low, LastVolume = (double)q.Volume,
                    SuperTrendVal = stVal, BullishST = bull,
                    HMAFast = hmaF, HMASlow = hmaS, EMAFast = ef, EMASlow = es,
                    AvgVolume = volSma, VolumeRatio = volRatio,
                    AdxValue = adx, PdiValue = pdi, MdiValue = mdi,
                    VcpDetected = vcpHit,
                    RsRaw = rsRaw, HasRs = hasRs, AvgTurnoverInr = avgTurnover, IsLiquid = liquid,
                    ExtensionPct = extensionPct, IsExtended = extended,
                    AtrValue = atrVal, SuggestedStop = stop, SuggestedTarget = target,
                    Levels    = levels,
                    StMetrics = stm,
                });
            }
            return results;
        }
    }

    // =========================================================================
    //  MULTI-TIMEFRAME AGGREGATOR  (pre-grouped for O(1) date lookup)
    // =========================================================================
    internal static class MultiTfAggregator
    {
        private static readonly Dictionary<string, double> _weights =
            Config.ScanTimeframes.ToDictionary(t => t.Label, t => t.Weight);

        /// <summary>
        /// Given ALL scan results (all tickers, all TFs, all dates),
        /// aggregate per-ticker per-date into AggregatedResult.
        /// Returns a list sorted by AggregateScore descending, tagged BUY/SELL/HOLD.
        /// </summary>
        /// <param name="regime">
        /// v4.0: optional market-regime context (benchmark trend). When supplied, this method
        /// also fills in <c>regime.Breadth*</c> from the universe's own daily results and uses
        /// the regime to raise the bar for BUY generation in a bearish/weak-breadth market
        /// instead of tagging the same volume of buys regardless of what the market is doing.
        /// </param>
        public static List<AggregatedResult> Aggregate(
            IReadOnlyList<ScanResult> allResults,
            DateTime scanDate,
            MarketRegimeInfo? regime = null)
        {
            // Pre-group once: skip LINQ Where/GroupBy every call
            var dayResults = new Dictionary<string, List<ScanResult>>(512);
            foreach (var r in allResults)
            {
                if (r.ScanDate.Date != scanDate.Date) continue;
                if (!dayResults.TryGetValue(r.Symbol, out var list))
                    dayResults[r.Symbol] = list = new List<ScanResult>(5);
                list.Add(r);
            }

            var aggList = new List<AggregatedResult>(dayResults.Count);
            var hasDailyMap = new Dictionary<string, bool>(dayResults.Count, StringComparer.OrdinalIgnoreCase);
            var hasRsMap    = new Dictionary<string, bool>(dayResults.Count, StringComparer.OrdinalIgnoreCase);
            int breadthBull = 0, breadthBear = 0, breadthTotal = 0;

            foreach (var (sym, grp) in dayResults)
            {
                double wtScore = 0, totalWt = 0;
                int bullish = 0, bearish = 0;
                double mo = 0, wk = 0, d1 = 0, h1 = 0, m15 = 0, m5 = 0;
                ScanResult? best = null;
                bool hasDaily = false;

                foreach (var r in grp)
                {
                    double w = _weights.GetValueOrDefault(r.Timeframe, 0.1);
                    wtScore += r.TotalScore * w;
                    totalWt += w;
                    if (r.TotalScore > 0) bullish++; else if (r.TotalScore < 0) bearish++;
                    switch (r.Timeframe)
                    {
                        case "1mo": mo  = r.TotalScore; break;
                        case "1wk": wk  = r.TotalScore; break;
                        case "1d":  d1  = r.TotalScore; break;
                        case "1h":  h1  = r.TotalScore; break;
                        case "15m": m15 = r.TotalScore; break;
                        case "5m":  m5  = r.TotalScore; break;
                    }
                    if (r.Timeframe == "1d") { best = r; hasDaily = true; } else best ??= r;
                }

                hasDailyMap[sym] = hasDaily;
                hasRsMap[sym]    = hasDaily && best!.HasRs;
                if (hasDaily)
                {
                    breadthTotal++;
                    if (d1 > 0) breadthBull++; else if (d1 < 0) breadthBear++;
                }

                double composite = totalWt > 0 ? Math.Round(wtScore / totalWt, 2) : 0;

                // ── v4.1: sector (CSV override > built-in map) + fundamentals + earnings ──
                string sector = ReferenceDataProvider.GetSector(sym);
                var refRec = ReferenceDataProvider.TryGetRecord(sym);
                bool inEarningsBlackout = false;
                if (refRec?.NextEarningsDate is DateTime nextEarnings)
                {
                    int daysToEarnings = (nextEarnings.Date - scanDate.Date).Days;
                    inEarningsBlackout = daysToEarnings >= 0 && daysToEarnings <= Config.EarningsBlackoutDays;
                }

                aggList.Add(new AggregatedResult
                {
                    Symbol         = sym,  ScanDate    = scanDate,
                    AggregateScore = composite,
                    MonthlyScore   = mo,
                    WeeklyScore    = wk,   DailyScore  = d1,
                    HourlyScore    = h1,   FifteenMinScore = m15, FiveMinScore = m5,
                    BullishTfCount = bullish, BearishTfCount = bearish,
                    LastClose      = best?.LastClose ?? 0,
                    DataSourceTf   = best?.Timeframe ?? string.Empty,
                    // v4.0: only trust these when the daily TF actually scored this symbol —
                    // otherwise default to non-blocking (liquid/not-extended) rather than
                    // silently disqualifying a stock whose daily fetch simply failed.
                    RsRaw           = hasDaily ? best!.RsRaw          : 0,
                    AvgTurnoverInr  = hasDaily ? best!.AvgTurnoverInr : 0,
                    IsLiquid        = hasDaily ? best!.IsLiquid       : true,
                    ExtensionPct    = hasDaily ? best!.ExtensionPct   : 0,
                    IsExtended      = hasDaily ? best!.IsExtended     : false,
                    AtrValue        = hasDaily ? best!.AtrValue       : 0,
                    SuggestedStop   = hasDaily ? best!.SuggestedStop  : 0,
                    SuggestedTarget = hasDaily ? best!.SuggestedTarget: 0,
                    // v4.1
                    Sector             = sector,
                    PeRatio            = refRec?.PeRatio,
                    RoePct             = refRec?.RoePct,
                    DebtToEquity       = refRec?.DebtToEquity,
                    EpsGrowthPct       = refRec?.EpsGrowthPct,
                    PromoterPledgePct  = refRec?.PromoterPledgePct,
                    NextEarningsDate   = refRec?.NextEarningsDate,
                    InEarningsBlackout = inEarningsBlackout,
                    Levels         = best?.Levels    ?? new(),
                    StMetrics      = best?.StMetrics ?? new(),
                });
            }

            if (regime != null)
                MarketRegimeAnalyzer.ApplyBreadth(regime, breadthBull, breadthBear, breadthTotal);

            // Sort by composite score desc
            aggList.Sort((a, b) => b.AggregateScore.CompareTo(a.AggregateScore));

            // ── v4.0: cross-sectional Relative Strength percentile rank (1-99, IBD-style) ──
            // rsGateActive is FALSE whenever nobody in this call had real benchmark-backed RS
            // data at all — e.g. the backtest engine's own Aggregate() calls, or a live run
            // where the benchmark fetch failed outright. In that case the RS gate is skipped
            // entirely rather than defaulting every stock to the neutral midpoint: with zero
            // real RsRaw values to rank, a naive percentile-by-position would tie-break on
            // aggList's existing score-sorted order and produce an RS "ranking" that's really
            // just an inverted copy of the composite score — silently corrupting BUY selection
            // instead of just not gating on RS when there's nothing to gate on.
            bool rsGateActive = hasRsMap.Values.Any(v => v);
            if (rsGateActive)
            {
                var rsEligible = aggList
                    .Where(a => hasRsMap.TryGetValue(a.Symbol, out var hr) && hr)
                    .OrderBy(a => a.RsRaw)
                    .ToList();
                int nRs = rsEligible.Count;
                for (int i = 0; i < nRs; i++)
                    rsEligible[i].RsRank = nRs > 1 ? Math.Round(1 + i * 98.0 / (nRs - 1), 1) : 50;
                foreach (var a in aggList)
                    if (!(hasRsMap.TryGetValue(a.Symbol, out var hr) && hr))
                        a.RsRank = 50; // no RS data for this specific stock — neutral, gated out by default
            }

            // ── v4.1: sector rotation — rank each sector by its stocks' average composite
            // score among daily-scored names, then give every stock its sector's percentile.
            // This is informational by default (dashboard + DB); Config.EnableSectorGate turns
            // it into an actual BUY gate. Sectors with only 1-2 scanned names are excluded from
            // ranking (too little signal to call a whole sector "leading") but their member
            // stocks still get the neutral midpoint so they're never wrongly gated either way.
            var sectorGroups = aggList
                .Where(a => hasDailyMap.TryGetValue(a.Symbol, out var hd) && hd)
                .GroupBy(a => a.Sector)
                .Where(g => g.Count() >= 3)
                .Select(g => new { Sector = g.Key, Avg = g.Average(a => a.AggregateScore) })
                .OrderBy(x => x.Avg)
                .ToList();
            int nSec = sectorGroups.Count;
            var sectorRankMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nSec; i++)
                sectorRankMap[sectorGroups[i].Sector] = nSec > 1 ? Math.Round(1 + i * 98.0 / (nSec - 1), 1) : 50;
            foreach (var a in aggList)
                a.SectorRankPercentile = sectorRankMap.TryGetValue(a.Sector, out var sr) ? sr : 50;


            // A confirmed bearish benchmark trend, or weak universe breadth, raises the score
            // bar and shrinks how many names can be tagged BUY at all — the scanner should
            // generate fewer, higher-conviction signals when the market itself isn't
            // confirming them, not the same volume of buys regardless of context.
            string regimeLabel  = regime?.Regime ?? "NEUTRAL";
            bool weakBreadth    = regime != null && regime.BreadthTotal > 0 &&
                                   regime.BreadthPctBullish < Config.MinBullishBreadthPctForBuy;
            bool defensiveMode  = regimeLabel == "BEARISH" || weakBreadth;

            double buyScoreThreshold = 20.0 + (defensiveMode ? Config.BearRegimeScoreThresholdBonus : 0);
            int    effectiveTopBuy   = defensiveMode
                ? Math.Max(1, Config.TopBuyCount / Math.Max(1, Config.BearRegimeBuyCountDivisor))
                : Config.TopBuyCount;

            int buyCount = 0;
            for (int r = 0; r < aggList.Count; r++)
            {
                var a = aggList[r];
                a.Rank = r + 1;
                if (buyCount >= effectiveTopBuy) continue;

                bool passScore     = a.AggregateScore >= buyScoreThreshold;
                if (!passScore) continue; // don't bother gating stocks nowhere near the bar

                bool passTfAlign   = a.BullishTfCount >= Config.MinBullishTfCountForBuy && a.WeeklyScore >= 0;
                bool passRs        = !rsGateActive || a.RsRank >= Config.MinRsRankForBuy;
                bool passLiquidity = a.IsLiquid;
                bool passExtension = !a.IsExtended;

                // v4.1: fundamental screen — OFF by default; when on, "unknown" (no reference.csv
                // row, or a field the CSV left blank) is always treated as passing, never blocking.
                bool passFundamental = true;
                if (Config.EnableFundamentalScreen)
                {
                    if (a.RoePct is double roe && roe < Config.MinRoePctForBuy) passFundamental = false;
                    if (a.DebtToEquity is double de && de > Config.MaxDebtToEquityForBuy) passFundamental = false;
                    if (a.PromoterPledgePct is double pledge && pledge > Config.MaxPromoterPledgePctForBuy) passFundamental = false;
                    if (Config.MinEpsGrowthPctForBuy is double minEps &&
                        a.EpsGrowthPct is double eps && eps < minEps) passFundamental = false;
                }

                // v4.1: earnings blackout — ON by default, but only ever fires when a
                // next_earnings_date is actually known for this symbol (see ReferenceDataProvider).
                bool passEarnings = !(Config.EnableEarningsBlackout && a.InEarningsBlackout);

                // v4.1: sector rotation gate — OFF by default (informational-only until enabled).
                bool passSector = !Config.EnableSectorGate || a.SectorRankPercentile >= Config.MinSectorRankPercentileForBuy;

                if (passTfAlign && passRs && passLiquidity && passExtension &&
                    passFundamental && passEarnings && passSector)
                {
                    a.RecommendationType = "BUY";
                    buyCount++;
                }
                else
                {
                    // Close enough on the composite score to have been a buy pre-v4.0, but
                    // blocked by a quality gate — surfaced on the dashboard instead of silently
                    // dropped, so it's clear WHY a familiar name isn't showing up as a BUY today.
                    var reasons = new List<string>(6);
                    if (!passTfAlign)     reasons.Add("weak TF alignment");
                    if (!passRs)          reasons.Add("weak RS vs Nifty");
                    if (!passLiquidity)   reasons.Add("low liquidity");
                    if (!passExtension)   reasons.Add("extended from 50-EMA");
                    if (!passFundamental) reasons.Add("fails fundamental screen");
                    if (!passEarnings)    reasons.Add($"earnings within {Config.EarningsBlackoutDays}d");
                    if (!passSector)      reasons.Add("weak sector");
                    a.GateReason = string.Join(", ", reasons);
                }
            }

            // Tag sells from the bottom — same TF-coherence discipline as BUY, so a sell isn't
            // driven by one noisy intraday timeframe against an otherwise-neutral weekly trend.
            int sellCount = 0;
            for (int r = aggList.Count - 1; r >= 0 && sellCount < Config.TopSellCount; r--)
            {
                var a = aggList[r];
                bool passScore   = a.AggregateScore <= -20;
                if (!passScore) continue;

                bool passTfAlign = a.BearishTfCount >= Config.MinBearishTfCountForSell && a.WeeklyScore <= 0;
                if (passTfAlign)
                {
                    a.RecommendationType = "IMMEDIATE_SELL";
                    sellCount++;
                }
            }

            return aggList;
        }

        /// <summary>
        /// v4.1: rolls up an already-aggregated day into per-sector stats for the dashboard's
        /// Sector Rotation panel — how many stocks per sector, how many bullish, average score,
        /// average RS, and the same rank percentile used internally for Config.EnableSectorGate.
        /// Sectors with fewer than 3 scanned names are still included (so nothing silently
        /// disappears) but flagged low-confidence via a small StockCount the dashboard can grey out.
        /// </summary>
        public static List<SectorRotationRow> BuildSectorRotationSummary(IReadOnlyList<AggregatedResult> aggList)
        {
            return aggList
                .GroupBy(a => a.Sector)
                .Select(g => new SectorRotationRow
                {
                    Sector         = g.Key,
                    StockCount     = g.Count(),
                    BullishCount   = g.Count(a => a.AggregateScore > 0),
                    AvgScore       = Math.Round(g.Average(a => a.AggregateScore), 2),
                    AvgRsRaw       = Math.Round(g.Average(a => a.RsRaw), 4),
                    RankPercentile = g.Max(a => a.SectorRankPercentile), // same for every member; Max just extracts it
                })
                .OrderByDescending(s => s.RankPercentile)
                .ToList();
        }
    }

    // =========================================================================
    //  v4.2: SHOCKERS / CONSECUTIVE-MOVERS ANALYZER
    //  Pure, allocation-conscious analytics over the current session's own daily bars
    //  (already fetched + persisted for the "1d" timeframe — no extra Yahoo calls). Every
    //  period/streak is computed with a single backward index walk per symbol; there's no
    //  per-period re-querying or re-sorting of the input series.
    // =========================================================================
    internal static class ShockersAnalyzer
    {
        /// <summary>One symbol's daily bar, ascending by date. AvgVolume is the same
        /// trailing SMA the live scorer persists as scan_results.avg_volume.</summary>
        internal readonly record struct DailyBar(DateTime Date, decimal Close, double Volume, double AvgVolume);

        public static ShockersResult Build(Dictionary<string, List<DailyBar>> bySymbol)
        {
            var result = new ShockersResult();
            int top = Config.ShockerTopCount;
            double minPrice = Config.ShockerMinPriceInr;

            foreach (var (periodDays, label) in Config.ShockerPeriods)
            {
                var gainers = new List<ShockerRow>();
                var losers  = new List<ShockerRow>();
                var volume  = new List<ShockerRow>();

                foreach (var (symbol, bars) in bySymbol)
                {
                    // Need `periodDays` bars of movement, i.e. periodDays+1 data points.
                    if (bars.Count < periodDays + 1) continue;

                    var last  = bars[^1];
                    var prior = bars[bars.Count - 1 - periodDays];
                    if (prior.Close <= 0 || last.Close < (decimal)minPrice) continue;

                    double pctChange = (double)((last.Close - prior.Close) / prior.Close) * 100.0;

                    double periodVolSum = 0;
                    for (int k = bars.Count - periodDays; k < bars.Count; k++)
                        periodVolSum += bars[k].Volume;
                    double periodMeanVol = periodVolSum / periodDays;
                    double volRatio = last.AvgVolume > 0 ? periodMeanVol / last.AvgVolume : 0;
                    double turnover = last.AvgVolume * (double)last.Close;

                    var row = new ShockerRow(symbol, last.Close, Math.Round(pctChange, 2),
                        Math.Round(volRatio, 2), Math.Round(turnover, 0));

                    if (pctChange >= 0) gainers.Add(row); else losers.Add(row);
                    volume.Add(row);
                }

                result.Periods[label] = new PeriodShockers
                {
                    PriceGainers = gainers.OrderByDescending(r => r.PctChange).Take(top).ToList(),
                    PriceLosers  = losers.OrderBy(r => r.PctChange).Take(top).ToList(),
                    VolumeSurges = volume.OrderByDescending(r => r.VolumeRatio).Take(top).ToList(),
                };
            }

            BuildStreaks(bySymbol, result);
            return result;
        }

        private static void BuildStreaks(Dictionary<string, List<DailyBar>> bySymbol, ShockersResult result)
        {
            int dailyMin  = Config.ConsecutiveDailyMinStreak;
            int weeklyMin = Config.ConsecutiveWeeklyMinStreak;
            int top       = Config.ShockerTopCount;

            var dailyUp = new List<StreakRow>(); var dailyDown = new List<StreakRow>();
            var weeklyUp = new List<StreakRow>(); var weeklyDown = new List<StreakRow>();

            foreach (var (symbol, bars) in bySymbol)
            {
                if (bars.Count >= dailyMin + 1)
                {
                    var (len, dir, startIdx) = TrailingStreak(bars, b => b.Close);
                    if (len >= dailyMin)
                    {
                        double cum = (double)((bars[^1].Close - bars[startIdx].Close) / bars[startIdx].Close) * 100.0;
                        var row = new StreakRow(symbol, len, dir, bars[startIdx + 1].Date, bars[^1].Date,
                            Math.Round(cum, 2), bars[^1].Close);
                        (dir == "UP" ? dailyUp : dailyDown).Add(row);
                    }
                }

                // Weekly closes: last trading-day close in each ISO week, oldest→newest.
                var weekly = bars
                    .GroupBy(b => (Year: System.Globalization.ISOWeek.GetYear(b.Date),
                                    Week: System.Globalization.ISOWeek.GetWeekOfYear(b.Date)))
                    .Select(g => g.OrderBy(b => b.Date).Last())
                    .OrderBy(b => b.Date)
                    .ToList();

                if (weekly.Count >= weeklyMin + 1)
                {
                    var (len, dir, startIdx) = TrailingStreak(weekly, b => b.Close);
                    if (len >= weeklyMin)
                    {
                        double cum = (double)((weekly[^1].Close - weekly[startIdx].Close) / weekly[startIdx].Close) * 100.0;
                        var row = new StreakRow(symbol, len, dir, weekly[startIdx + 1].Date, weekly[^1].Date,
                            Math.Round(cum, 2), weekly[^1].Close);
                        (dir == "UP" ? weeklyUp : weeklyDown).Add(row);
                    }
                }
            }

            static IOrderedEnumerable<StreakRow> Rank(List<StreakRow> rows) =>
                rows.OrderByDescending(r => r.StreakLength).ThenByDescending(r => Math.Abs(r.CumulativePctChange));

            result.DailyStreakGainers  = Rank(dailyUp).Take(top).ToList();
            result.DailyStreakLosers   = Rank(dailyDown).Take(top).ToList();
            result.WeeklyStreakGainers = Rank(weeklyUp).Take(top).ToList();
            result.WeeklyStreakLosers  = Rank(weeklyDown).Take(top).ToList();
        }

        /// <summary>Walks a chronologically-sorted bar list backward from the end and measures
        /// the current strictly-monotonic streak in <paramref name="closeOf"/>. Returns streak
        /// length, direction ("UP"/"DOWN"/"" if fewer than 2 bars or the very last move was
        /// flat), and the index of the bar *before* the streak began (i.e. the baseline for a
        /// cumulative % change) — O(n) worst case, but stops at the first break or flat bar.</summary>
        private static (int Length, string Direction, int BaseIndex) TrailingStreak<T>(
            IReadOnlyList<T> bars, Func<T, decimal> closeOf)
        {
            if (bars.Count < 2) return (0, "", 0);
            bool? up = null;
            int i = bars.Count - 1;
            for (; i > 0; i--)
            {
                decimal cur = closeOf(bars[i]), prev = closeOf(bars[i - 1]);
                if (cur == prev) break; // flat breaks the streak
                bool thisUp = cur > prev;
                if (up == null) up = thisUp;
                else if (up.Value != thisUp) break;
            }
            int len = bars.Count - 1 - i;
            return up == null || len == 0 ? (0, "", bars.Count - 1) : (len, up.Value ? "UP" : "DOWN", i);
        }
    }

    // =========================================================================
    //  SQLITE REPOSITORY  (all pragmas, FK enforcement, session pruning)
    // =========================================================================
    internal sealed class SqliteRepository : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly SemaphoreSlim    _writeLock = new(1, 1);
        private bool _disposed;

        public SqliteRepository()
        {
            _conn = new SqliteConnection($"Data Source={Config.DbPath};Cache=Shared");
            _conn.Open();
            ConfigurePragmas();
            CreateSchema();
        }

        private void ConfigurePragmas()
        {
            // Correctness
            Exec("PRAGMA foreign_keys    = ON;");
            // Performance
            Exec("PRAGMA journal_mode    = WAL;");
            Exec("PRAGMA synchronous     = NORMAL;");
            Exec("PRAGMA cache_size      = -32000;");  // 32 MB page cache
            Exec("PRAGMA temp_store      = MEMORY;");
            Exec("PRAGMA mmap_size       = 268435456;"); // 256 MB memory-mapped I/O
            Exec("PRAGMA wal_autocheckpoint = 1000;");
        }

        private void CreateSchema()
        {
            Exec(@"
            CREATE TABLE IF NOT EXISTS scan_sessions (
                session_id     INTEGER PRIMARY KEY AUTOINCREMENT,
                run_timestamp  TEXT NOT NULL,
                universe       TEXT NOT NULL,
                total_tickers  INTEGER NOT NULL DEFAULT 0,
                scored_tickers INTEGER NOT NULL DEFAULT 0,
                weight_source  TEXT NOT NULL DEFAULT 'default'
            );

            CREATE TABLE IF NOT EXISTS scan_results (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id       INTEGER NOT NULL REFERENCES scan_sessions(session_id) ON DELETE CASCADE,
                symbol           TEXT NOT NULL, scan_date TEXT NOT NULL, timeframe TEXT NOT NULL,
                rank             INTEGER,
                total_score      REAL, supertrend_score REAL, hma_score REAL, adx_score REAL,
                vcp_score        REAL, volume_score REAL,   trend_score REAL,
                is_buy           INTEGER, is_sell INTEGER,
                last_close       REAL, last_open REAL, last_high REAL, last_low REAL, last_volume REAL,
                supertrend_val   REAL, bullish_st INTEGER,
                hma_fast         REAL, hma_slow REAL, ema_fast REAL, ema_slow REAL,
                avg_volume       REAL, volume_ratio REAL,
                adx_value        REAL, pdi_value REAL, mdi_value REAL, vcp_detected INTEGER,
                weekly_high      REAL, weekly_low REAL,  monthly_high REAL, monthly_low REAL,
                quarterly_high   REAL, quarterly_low REAL, yearly_high REAL, yearly_low REAL,
                pct_52w_high     REAL, pct_52w_low REAL, pct_month_high REAL, pct_week_high REAL,
                st_bullish       INTEGER, st_days INTEGER,  st_flip_date TEXT, st_cal_days INTEGER,
                st_hh            REAL,    st_hh_ago INTEGER, st_hl REAL, st_hl_ago INTEGER,
                st_lh            REAL,    st_lh_ago INTEGER, st_ll REAL, st_ll_ago INTEGER,
                rs_raw           REAL, avg_turnover_inr REAL, is_liquid INTEGER,
                extension_pct    REAL, is_extended INTEGER,
                atr_value        REAL, suggested_stop REAL, suggested_target REAL,
                UNIQUE (session_id, symbol, scan_date, timeframe)
            );

            CREATE TABLE IF NOT EXISTS aggregated_results (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id        INTEGER NOT NULL REFERENCES scan_sessions(session_id) ON DELETE CASCADE,
                symbol            TEXT NOT NULL, scan_date TEXT NOT NULL,
                aggregate_score   REAL, monthly_score REAL, weekly_score REAL, daily_score REAL,
                hourly_score      REAL, fifteen_min_score REAL, five_min_score REAL,
                bullish_tf        INTEGER, bearish_tf INTEGER,
                recommendation    TEXT, rank INTEGER,
                last_close        REAL, data_tf TEXT,
                weekly_high       REAL, weekly_low REAL, monthly_high REAL, monthly_low REAL,
                quarterly_high    REAL, quarterly_low REAL, yearly_high REAL, yearly_low REAL,
                pct_52w_high      REAL, pct_52w_low REAL,
                st_bullish        INTEGER, st_days INTEGER, st_flip_date TEXT, st_cal_days INTEGER,
                st_hh             REAL,   st_hh_ago INTEGER, st_ll REAL, st_ll_ago INTEGER,
                rs_raw            REAL, rs_rank REAL, avg_turnover_inr REAL, is_liquid INTEGER,
                extension_pct     REAL, is_extended INTEGER,
                atr_value         REAL, suggested_stop REAL, suggested_target REAL,
                gate_reason       TEXT,
                sector            TEXT, sector_rank REAL,
                pe_ratio          REAL, roe_pct REAL, debt_to_equity REAL, eps_growth_pct REAL,
                promoter_pledge_pct REAL, next_earnings_date TEXT, in_earnings_blackout INTEGER,
                UNIQUE (session_id, symbol, scan_date)
            );

            CREATE TABLE IF NOT EXISTS sector_rotation (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id        INTEGER NOT NULL REFERENCES scan_sessions(session_id) ON DELETE CASCADE,
                scan_date         TEXT NOT NULL,
                sector            TEXT NOT NULL,
                stock_count       INTEGER, bullish_count INTEGER,
                avg_score         REAL, avg_rs_raw REAL, rank_percentile REAL,
                UNIQUE (session_id, scan_date, sector)
            );

            CREATE TABLE IF NOT EXISTS market_regime (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id        INTEGER NOT NULL REFERENCES scan_sessions(session_id) ON DELETE CASCADE,
                scan_date         TEXT NOT NULL,
                benchmark_symbol  TEXT NOT NULL,
                benchmark_close   REAL, ema50 REAL, ema200 REAL,
                regime            TEXT NOT NULL,
                breadth_total     INTEGER, breadth_bullish INTEGER, breadth_bearish INTEGER,
                breadth_pct_bullish REAL,
                UNIQUE (session_id, scan_date)
            );

            CREATE TABLE IF NOT EXISTS data_quality_issues (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id        INTEGER NOT NULL REFERENCES scan_sessions(session_id) ON DELETE CASCADE,
                symbol            TEXT NOT NULL, timeframe TEXT NOT NULL, reason TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_sr_sym_date  ON scan_results(symbol, scan_date);
            CREATE INDEX IF NOT EXISTS idx_sr_date_tf   ON scan_results(scan_date, timeframe);
            CREATE INDEX IF NOT EXISTS idx_sr_score     ON scan_results(scan_date, total_score DESC);
            CREATE INDEX IF NOT EXISTS idx_agg_date_rec ON aggregated_results(scan_date, recommendation);
            CREATE INDEX IF NOT EXISTS idx_dq_session    ON data_quality_issues(session_id);
            CREATE INDEX IF NOT EXISTS idx_secrot_session ON sector_rotation(session_id, scan_date);

            -- v4.2 perf: every dashboard read (ReadDailyDetailsAsync, ReadHistoryAsync,
            -- ReadTfBreakdownAsync, and the new shockers series read) filters scan_results by
            -- session_id first — none of the indexes above lead with it, so SQLite fell back to
            -- a slower scan_date/symbol-first plan and re-filtered session_id row-by-row. With
            -- KeepSessionCount sessions of ~500 tickers × 6 timeframes × ScanHistoryDays rows
            -- accumulated, that filter step was scanning far more rows than necessary. Same
            -- story for aggregated_results, whose only prior index leads with scan_date, not
            -- session_id, even though every dashboard query supplies both.
            CREATE INDEX IF NOT EXISTS idx_sr_session_tf_sym_date
                ON scan_results(session_id, timeframe, symbol, scan_date);
            CREATE INDEX IF NOT EXISTS idx_agg_session_date
                ON aggregated_results(session_id, scan_date, aggregate_score DESC);
            ");

            MigrateSchema();
        }

        /// <summary>
        /// v4.0 added columns to two pre-existing tables. CREATE TABLE IF NOT EXISTS above only
        /// covers a brand-new DB — an existing NseMomentumScanner.db from v3.x needs these columns
        /// added explicitly. SQLite has no "ADD COLUMN IF NOT EXISTS", so each ALTER is attempted
        /// and a "duplicate column" failure (already migrated) is swallowed; anything else is logged.
        /// </summary>
        private void MigrateSchema()
        {
            var scanResultsCols = new[]
            {
                "rs_raw REAL", "avg_turnover_inr REAL", "is_liquid INTEGER",
                "extension_pct REAL", "is_extended INTEGER",
                "atr_value REAL", "suggested_stop REAL", "suggested_target REAL",
            };
            var aggregatedCols = new[]
            {
                "rs_raw REAL", "rs_rank REAL", "avg_turnover_inr REAL", "is_liquid INTEGER",
                "extension_pct REAL", "is_extended INTEGER",
                "atr_value REAL", "suggested_stop REAL", "suggested_target REAL",
                "gate_reason TEXT",
                "sector TEXT", "sector_rank REAL",
                "pe_ratio REAL", "roe_pct REAL", "debt_to_equity REAL", "eps_growth_pct REAL",
                "promoter_pledge_pct REAL", "next_earnings_date TEXT", "in_earnings_blackout INTEGER",
            };
            var sessionCols = new[] { "weight_source TEXT NOT NULL DEFAULT 'default'" };

            foreach (var col in scanResultsCols)
                TryAlterAddColumn("scan_results", col);
            foreach (var col in aggregatedCols)
                TryAlterAddColumn("aggregated_results", col);
            foreach (var col in sessionCols)
                TryAlterAddColumn("scan_sessions", col);
        }

        private void TryAlterAddColumn(string table, string columnDef)
        {
            try { Exec($"ALTER TABLE {table} ADD COLUMN {columnDef};"); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Already migrated on a prior run — expected, not an error.
            }
            catch (Exception ex)
            {
                Log.Warning("Schema migration: could not add {Table}.{Col}: {Msg}", table, columnDef, ex.Message);
            }
        }

        private void Exec(string sql)
        {
            using var cmd = new SqliteCommand(sql, _conn);
            cmd.ExecuteNonQuery();
        }

        // ── Session management ────────────────────────────────────────────────
        public int InsertSession(string universe, int total)
        {
            using var cmd = new SqliteCommand(
                "INSERT INTO scan_sessions(run_timestamp,universe,total_tickers) " +
                "VALUES(@ts,@u,@t); SELECT last_insert_rowid();", _conn);
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@u",  universe);
            cmd.Parameters.AddWithValue("@t",  total);
            return (int)(long)cmd.ExecuteScalar()!;
        }

        public void UpdateSessionScored(int id, int scored)
        {
            using var cmd = new SqliteCommand(
                "UPDATE scan_sessions SET scored_tickers=@s WHERE session_id=@id", _conn);
            cmd.Parameters.AddWithValue("@s",  scored);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        public void PruneOldSessions()
        {
            // Cascading deletes handle scan_results + aggregated_results via ON DELETE CASCADE
            using (var cmd = new SqliteCommand(
                "DELETE FROM scan_sessions WHERE session_id NOT IN " +
                "(SELECT session_id FROM scan_sessions ORDER BY session_id DESC LIMIT @keep);", _conn))
            {
                cmd.Parameters.AddWithValue("@keep", Config.KeepSessionCount);
                cmd.ExecuteNonQuery();
            }
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
        }

        // ── Bulk inserts ──────────────────────────────────────────────────────
        public async Task BulkInsertScanResultsAsync(int sid, IEnumerable<ScanResult> rows)
        {
            await _writeLock.WaitAsync();
            try
            {
                const string sql = @"INSERT OR REPLACE INTO scan_results
                (session_id,symbol,scan_date,timeframe,rank,
                 total_score,supertrend_score,hma_score,adx_score,vcp_score,volume_score,trend_score,
                 is_buy,is_sell,last_close,last_open,last_high,last_low,last_volume,
                 supertrend_val,bullish_st,hma_fast,hma_slow,ema_fast,ema_slow,
                 avg_volume,volume_ratio,adx_value,pdi_value,mdi_value,vcp_detected,
                 weekly_high,weekly_low,monthly_high,monthly_low,quarterly_high,quarterly_low,
                 yearly_high,yearly_low,pct_52w_high,pct_52w_low,pct_month_high,pct_week_high,
                 st_bullish,st_days,st_flip_date,st_cal_days,
                 st_hh,st_hh_ago,st_hl,st_hl_ago,st_lh,st_lh_ago,st_ll,st_ll_ago,
                 rs_raw,avg_turnover_inr,is_liquid,extension_pct,is_extended,
                 atr_value,suggested_stop,suggested_target)
                VALUES(@sid,@sym,@dt,@tf,@rk,
                 @ts,@sts,@hms,@ads,@vcs,@vls,@trs,
                 @ib,@is,@lc,@lo,@lh,@ll,@lv,
                 @sv,@bs,@hf,@hs,@ef,@es,
                 @av,@vr,@ax,@pd,@md,@vcp,
                 @wh,@wl,@mh,@ml,@qh,@ql,@yh,@yl,@p2h,@p2l,@pmh,@pwh,
                 @stb,@std,@stf,@stc,@shh,@sha,@shl,@shla,@slh,@slha,@sll,@slla,
                 @rsr,@atv,@liq,@ext,@isx,@atr,@sstop,@starget)";

                using var tx  = _conn.BeginTransaction();
                using var cmd = new SqliteCommand(sql, _conn, tx);
                var p = cmd.Parameters;
                AddParams(p, "@sid,@sym,@dt,@tf,@rk,@ts,@sts,@hms,@ads,@vcs,@vls,@trs," +
                    "@ib,@is,@lc,@lo,@lh,@ll,@lv,@sv,@bs,@hf,@hs,@ef,@es,@av,@vr,@ax,@pd,@md,@vcp," +
                    "@wh,@wl,@mh,@ml,@qh,@ql,@yh,@yl,@p2h,@p2l,@pmh,@pwh," +
                    "@stb,@std,@stf,@stc,@shh,@sha,@shl,@shla,@slh,@slha,@sll,@slla," +
                    "@rsr,@atv,@liq,@ext,@isx,@atr,@sstop,@starget");

                foreach (var r in rows)
                {
                    var L = r.Levels; var M = r.StMetrics;
                    p["@sid"].Value=sid;        p["@sym"].Value=r.Symbol;
                    p["@dt"].Value =r.ScanDate.ToString("yyyy-MM-dd");
                    p["@tf"].Value =r.Timeframe; p["@rk"].Value=r.Rank;
                    p["@ts"].Value =r.TotalScore;       p["@sts"].Value=r.SupertrendScore;
                    p["@hms"].Value=r.HmaScore;         p["@ads"].Value=r.AdxScore;
                    p["@vcs"].Value=r.VcpScore;         p["@vls"].Value=r.VolumeScore;
                    p["@trs"].Value=r.TrendScore;
                    p["@ib"].Value =r.IsBuyCandidate?1:0; p["@is"].Value=r.IsShortCandidate?1:0;
                    p["@lc"].Value=(double)r.LastClose;  p["@lo"].Value=(double)r.LastOpen;
                    p["@lh"].Value=(double)r.LastHigh;   p["@ll"].Value=(double)r.LastLow;
                    p["@lv"].Value=r.LastVolume;
                    p["@sv"].Value=(double)r.SuperTrendVal; p["@bs"].Value=r.BullishST?1:0;
                    p["@hf"].Value=r.HMAFast;   p["@hs"].Value=r.HMASlow;
                    p["@ef"].Value=r.EMAFast;   p["@es"].Value=r.EMASlow;
                    p["@av"].Value=r.AvgVolume; p["@vr"].Value=r.VolumeRatio;
                    p["@ax"].Value=r.AdxValue;  p["@pd"].Value=r.PdiValue;
                    p["@md"].Value=r.MdiValue;  p["@vcp"].Value=r.VcpDetected?1:0;
                    p["@wh"].Value=(double)L.WeeklyHigh;    p["@wl"].Value=(double)L.WeeklyLow;
                    p["@mh"].Value=(double)L.MonthlyHigh;   p["@ml"].Value=(double)L.MonthlyLow;
                    p["@qh"].Value=(double)L.QuarterlyHigh; p["@ql"].Value=(double)L.QuarterlyLow;
                    p["@yh"].Value=(double)L.YearlyHigh;    p["@yl"].Value=(double)L.YearlyLow;
                    p["@p2h"].Value=L.PctFrom52wHigh;  p["@p2l"].Value=L.PctFrom52wLow;
                    p["@pmh"].Value=L.PctFromMonthHigh; p["@pwh"].Value=L.PctFromWeekHigh;
                    p["@stb"].Value=M.CurrentlyBullish?1:0; p["@std"].Value=M.TradingDaysInTrend;
                    p["@stf"].Value=M.LastFlipDate.ToString("yyyy-MM-dd");
                    p["@stc"].Value=M.CalendarDaysSinceFlip;
                    p["@shh"].Value=(double)M.HighestHigh; p["@sha"].Value=M.HighestHighDaysAgo;
                    p["@shl"].Value=(double)M.HighestLow;  p["@shla"].Value=M.HighestLowDaysAgo;
                    p["@slh"].Value=(double)M.LowestHigh;  p["@slha"].Value=M.LowestHighDaysAgo;
                    p["@sll"].Value=(double)M.LowestLow;   p["@slla"].Value=M.LowestLowDaysAgo;
                    p["@rsr"].Value=r.RsRaw;   p["@atv"].Value=r.AvgTurnoverInr;
                    p["@liq"].Value=r.IsLiquid?1:0; p["@ext"].Value=r.ExtensionPct;
                    p["@isx"].Value=r.IsExtended?1:0;
                    p["@atr"].Value=r.AtrValue; p["@sstop"].Value=r.SuggestedStop;
                    p["@starget"].Value=r.SuggestedTarget;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { _writeLock.Release(); }
        }

        public async Task BulkInsertAggregatedAsync(int sid, IEnumerable<AggregatedResult> rows)
        {
            await _writeLock.WaitAsync();
            try
            {
                const string sql = @"INSERT OR REPLACE INTO aggregated_results
                (session_id,symbol,scan_date,aggregate_score,monthly_score,weekly_score,daily_score,
                 hourly_score,fifteen_min_score,five_min_score,bullish_tf,bearish_tf,
                 recommendation,rank,last_close,data_tf,
                 weekly_high,weekly_low,monthly_high,monthly_low,quarterly_high,quarterly_low,
                 yearly_high,yearly_low,pct_52w_high,pct_52w_low,
                 st_bullish,st_days,st_flip_date,st_cal_days,st_hh,st_hh_ago,st_ll,st_ll_ago,
                 rs_raw,rs_rank,avg_turnover_inr,is_liquid,extension_pct,is_extended,
                 atr_value,suggested_stop,suggested_target,gate_reason,
                 sector,sector_rank,pe_ratio,roe_pct,debt_to_equity,eps_growth_pct,
                 promoter_pledge_pct,next_earnings_date,in_earnings_blackout)
                VALUES(@sid,@sym,@dt,@as,@mos,@ws,@ds,@hs,@ms,@fs,@bt,@be,@rec,@rk,@lc,@tf,
                 @wh,@wl,@mh,@ml,@qh,@ql,@yh,@yl,@p2h,@p2l,
                 @stb,@std,@stf,@stc,@shh,@sha,@sll,@sla,
                 @rsr,@rrk,@atv,@liq,@ext,@isx,@atr,@sstop,@starget,@gr,
                 @sec,@secr,@pe,@roe,@de,@eps,@pledge,@nextearn,@blackout)";

                using var tx  = _conn.BeginTransaction();
                using var cmd = new SqliteCommand(sql, _conn, tx);
                var p = cmd.Parameters;
                AddParams(p, "@sid,@sym,@dt,@as,@mos,@ws,@ds,@hs,@ms,@fs,@bt,@be,@rec,@rk,@lc,@tf," +
                    "@wh,@wl,@mh,@ml,@qh,@ql,@yh,@yl,@p2h,@p2l,@stb,@std,@stf,@stc,@shh,@sha,@sll,@sla," +
                    "@rsr,@rrk,@atv,@liq,@ext,@isx,@atr,@sstop,@starget,@gr," +
                    "@sec,@secr,@pe,@roe,@de,@eps,@pledge,@nextearn,@blackout");

                foreach (var a in rows)
                {
                    var L=a.Levels; var M=a.StMetrics;
                    p["@sid"].Value=sid;  p["@sym"].Value=a.Symbol;
                    p["@dt"].Value=a.ScanDate.ToString("yyyy-MM-dd");
                    p["@as"].Value=a.AggregateScore;
                    p["@mos"].Value=a.MonthlyScore;
                    p["@ws"].Value=a.WeeklyScore;    p["@ds"].Value=a.DailyScore;
                    p["@hs"].Value=a.HourlyScore;    p["@ms"].Value=a.FifteenMinScore;
                    p["@fs"].Value=a.FiveMinScore;
                    p["@bt"].Value=a.BullishTfCount; p["@be"].Value=a.BearishTfCount;
                    p["@rec"].Value=a.RecommendationType; p["@rk"].Value=a.Rank;
                    p["@lc"].Value=(double)a.LastClose;   p["@tf"].Value=a.DataSourceTf;
                    p["@wh"].Value=(double)L.WeeklyHigh;  p["@wl"].Value=(double)L.WeeklyLow;
                    p["@mh"].Value=(double)L.MonthlyHigh; p["@ml"].Value=(double)L.MonthlyLow;
                    p["@qh"].Value=(double)L.QuarterlyHigh;p["@ql"].Value=(double)L.QuarterlyLow;
                    p["@yh"].Value=(double)L.YearlyHigh;  p["@yl"].Value=(double)L.YearlyLow;
                    p["@p2h"].Value=L.PctFrom52wHigh;     p["@p2l"].Value=L.PctFrom52wLow;
                    p["@stb"].Value=M.CurrentlyBullish?1:0; p["@std"].Value=M.TradingDaysInTrend;
                    p["@stf"].Value=M.LastFlipDate.ToString("yyyy-MM-dd");
                    p["@stc"].Value=M.CalendarDaysSinceFlip;
                    p["@shh"].Value=(double)M.HighestHigh; p["@sha"].Value=M.HighestHighDaysAgo;
                    p["@sll"].Value=(double)M.LowestLow;   p["@sla"].Value=M.LowestLowDaysAgo;
                    p["@rsr"].Value=a.RsRaw;  p["@rrk"].Value=a.RsRank;
                    p["@atv"].Value=a.AvgTurnoverInr; p["@liq"].Value=a.IsLiquid?1:0;
                    p["@ext"].Value=a.ExtensionPct;   p["@isx"].Value=a.IsExtended?1:0;
                    p["@atr"].Value=a.AtrValue; p["@sstop"].Value=a.SuggestedStop;
                    p["@starget"].Value=a.SuggestedTarget;
                    p["@gr"].Value=a.GateReason;
                    p["@sec"].Value=a.Sector; p["@secr"].Value=a.SectorRankPercentile;
                    p["@pe"].Value=(object?)a.PeRatio ?? DBNull.Value;
                    p["@roe"].Value=(object?)a.RoePct ?? DBNull.Value;
                    p["@de"].Value=(object?)a.DebtToEquity ?? DBNull.Value;
                    p["@eps"].Value=(object?)a.EpsGrowthPct ?? DBNull.Value;
                    p["@pledge"].Value=(object?)a.PromoterPledgePct ?? DBNull.Value;
                    p["@nextearn"].Value=a.NextEarningsDate.HasValue
                        ? a.NextEarningsDate.Value.ToString("yyyy-MM-dd") : (object)DBNull.Value;
                    p["@blackout"].Value=a.InEarningsBlackout?1:0;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { _writeLock.Release(); }
        }

        // ── v4.0: market regime + data-quality persistence ─────────────────────
        public async Task InsertMarketRegimeAsync(int sid, DateTime scanDate, MarketRegimeInfo regime)
        {
            await _writeLock.WaitAsync();
            try
            {
                const string sql = @"INSERT OR REPLACE INTO market_regime
                (session_id,scan_date,benchmark_symbol,benchmark_close,ema50,ema200,regime,
                 breadth_total,breadth_bullish,breadth_bearish,breadth_pct_bullish)
                VALUES(@sid,@dt,@sym,@cl,@e50,@e200,@rg,@bt,@bb,@be,@bp)";
                using var cmd = new SqliteCommand(sql, _conn);
                cmd.Parameters.AddWithValue("@sid", sid);
                cmd.Parameters.AddWithValue("@dt", scanDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@sym", regime.BenchmarkSymbol);
                cmd.Parameters.AddWithValue("@cl", (double)regime.BenchmarkClose);
                cmd.Parameters.AddWithValue("@e50", regime.Ema50);
                cmd.Parameters.AddWithValue("@e200", regime.Ema200);
                cmd.Parameters.AddWithValue("@rg", regime.Regime);
                cmd.Parameters.AddWithValue("@bt", regime.BreadthTotal);
                cmd.Parameters.AddWithValue("@bb", regime.BreadthBullish);
                cmd.Parameters.AddWithValue("@be", regime.BreadthBearish);
                cmd.Parameters.AddWithValue("@bp", regime.BreadthPctBullish);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _writeLock.Release(); }
        }

        public async Task BulkInsertDataQualityIssuesAsync(
            int sid, IReadOnlyList<(string Symbol, string Timeframe, string Reason)> issues)
        {
            if (issues.Count == 0) return;
            await _writeLock.WaitAsync();
            try
            {
                const string sql = @"INSERT INTO data_quality_issues(session_id,symbol,timeframe,reason)
                                      VALUES(@sid,@sym,@tf,@reason)";
                using var tx  = _conn.BeginTransaction();
                using var cmd = new SqliteCommand(sql, _conn, tx);
                cmd.Parameters.Add(new SqliteParameter { ParameterName = "@sid" });
                cmd.Parameters.Add(new SqliteParameter { ParameterName = "@sym" });
                cmd.Parameters.Add(new SqliteParameter { ParameterName = "@tf" });
                cmd.Parameters.Add(new SqliteParameter { ParameterName = "@reason" });
                foreach (var issue in issues)
                {
                    cmd.Parameters["@sid"].Value = sid;
                    cmd.Parameters["@sym"].Value = issue.Symbol;
                    cmd.Parameters["@tf"].Value = issue.Timeframe;
                    cmd.Parameters["@reason"].Value = issue.Reason;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { _writeLock.Release(); }
        }

        // ── v4.1: sector rotation + weight-source audit trail ──────────────────
        public async Task BulkInsertSectorRotationAsync(
            int sid, DateTime scanDate, IReadOnlyList<SectorRotationRow> rows)
        {
            if (rows.Count == 0) return;
            await _writeLock.WaitAsync();
            try
            {
                const string sql = @"INSERT OR REPLACE INTO sector_rotation
                    (session_id,scan_date,sector,stock_count,bullish_count,avg_score,avg_rs_raw,rank_percentile)
                    VALUES(@sid,@dt,@sec,@cnt,@bull,@avg,@avgrs,@rank)";
                using var tx  = _conn.BeginTransaction();
                using var cmd = new SqliteCommand(sql, _conn, tx);
                var p = cmd.Parameters;
                AddParams(p, "@sid,@dt,@sec,@cnt,@bull,@avg,@avgrs,@rank");
                string dt = scanDate.ToString("yyyy-MM-dd");
                foreach (var row in rows)
                {
                    p["@sid"].Value = sid; p["@dt"].Value = dt; p["@sec"].Value = row.Sector;
                    p["@cnt"].Value = row.StockCount; p["@bull"].Value = row.BullishCount;
                    p["@avg"].Value = row.AvgScore; p["@avgrs"].Value = row.AvgRsRaw;
                    p["@rank"].Value = row.RankPercentile;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { _writeLock.Release(); }
        }

        public async Task UpdateSessionWeightSourceAsync(int sid, string weightSource)
        {
            await _writeLock.WaitAsync();
            try
            {
                using var cmd = new SqliteCommand(
                    "UPDATE scan_sessions SET weight_source=@ws WHERE session_id=@sid", _conn);
                cmd.Parameters.AddWithValue("@ws", weightSource);
                cmd.Parameters.AddWithValue("@sid", sid);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _writeLock.Release(); }
        }

        private static void AddParams(SqliteParameterCollection p, string names)
        {
            // Do NOT pre-declare a SqliteType here. Forcing SqliteType.Real on text columns
            // (symbol, timeframe, scan_date, flip_date, recommendation…) makes the binder try to
            // Convert.ToDouble() a string and throw on the very first insert. Leaving SqliteType
            // unset lets it infer correctly from each row's actual .Value at bind time.
            foreach (var n in names.Split(','))
                p.Add(new SqliteParameter { ParameterName = n.Trim() });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Close();
            _conn.Dispose();
            _writeLock.Dispose();
        }
    }

    // =========================================================================
    //  CSV EXPORTER
    // =========================================================================
    internal static class CsvExporter
    {
        private static readonly CsvConfiguration _cfg =
            new(CultureInfo.InvariantCulture) { HasHeaderRecord = true, NewLine = "\n" };

        public static void WriteScanResults(IEnumerable<ScanResult> rows, DateTime date, string tf)
        {
            try
            {
                Directory.CreateDirectory(Config.CsvBaseDir);
                string path = Path.Combine(Config.CsvBaseDir,
                    $"scan_{date:yyyy-MM-dd}_{tf}.csv");
                using var w = new StreamWriter(path, false, Encoding.UTF8);
                using var c = new CsvWriter(w, _cfg);
                c.WriteRecords(rows.OrderByDescending(r => r.TotalScore).Select(r => new SrRow(r)));
            }
            catch (Exception ex) { Log.Warning("CSV write error ({Tf}): {Msg}", tf, ex.Message); }
        }

        public static void WriteAggregated(IEnumerable<AggregatedResult> rows, DateTime date)
        {
            try
            {
                Directory.CreateDirectory(Config.CsvBaseDir);
                string path = Path.Combine(Config.CsvBaseDir, $"aggregated_{date:yyyy-MM-dd}.csv");
                using var w = new StreamWriter(path, false, Encoding.UTF8);
                using var c = new CsvWriter(w, _cfg);
                c.WriteRecords(rows.Select(r => new AggRow(r)));
            }
            catch (Exception ex) { Log.Warning("CSV write error (agg): {Msg}", ex.Message); }
        }

        // ── Flat row types ────────────────────────────────────────────────────
        private sealed record SrRow(
            string Symbol, string ScanDate, string Timeframe, int Rank,
            double TotalScore, double SupertrendScore, double HmaScore,
            double AdxScore, double VcpScore, double VolumeScore, double TrendScore,
            bool IsBuy, bool IsSell,
            decimal LastClose, decimal LastHigh, decimal LastLow, double LastVolume,
            double VolumeRatio, double AdxValue, double PdiValue, double MdiValue,
            bool BullishST, decimal SuperTrendVal, bool VcpDetected,
            decimal WeeklyHigh, decimal WeeklyLow, decimal MonthlyHigh, decimal MonthlyLow,
            decimal QuarterlyHigh, decimal QuarterlyLow, decimal YearlyHigh, decimal YearlyLow,
            double Pct52wHigh, double Pct52wLow,
            bool StBullish, int StDays, string StFlipDate, int StCalDays,
            decimal StHH, int StHHAgo, decimal StHL, int StHLAgo,
            decimal StLH, int StLHAgo, decimal StLL, int StLLAgo)
        {
            public SrRow(ScanResult r) : this(
                r.Symbol, r.ScanDate.ToString("yyyy-MM-dd"), r.Timeframe, r.Rank,
                r.TotalScore, r.SupertrendScore, r.HmaScore,
                r.AdxScore, r.VcpScore, r.VolumeScore, r.TrendScore,
                r.IsBuyCandidate, r.IsShortCandidate,
                r.LastClose, r.LastHigh, r.LastLow, r.LastVolume,
                r.VolumeRatio, r.AdxValue, r.PdiValue, r.MdiValue,
                r.BullishST, r.SuperTrendVal, r.VcpDetected,
                r.Levels.WeeklyHigh, r.Levels.WeeklyLow,
                r.Levels.MonthlyHigh, r.Levels.MonthlyLow,
                r.Levels.QuarterlyHigh, r.Levels.QuarterlyLow,
                r.Levels.YearlyHigh, r.Levels.YearlyLow,
                r.Levels.PctFrom52wHigh, r.Levels.PctFrom52wLow,
                r.StMetrics.CurrentlyBullish, r.StMetrics.TradingDaysInTrend,
                r.StMetrics.LastFlipDate.ToString("yyyy-MM-dd"),
                r.StMetrics.CalendarDaysSinceFlip,
                r.StMetrics.HighestHigh, r.StMetrics.HighestHighDaysAgo,
                r.StMetrics.HighestLow,  r.StMetrics.HighestLowDaysAgo,
                r.StMetrics.LowestHigh,  r.StMetrics.LowestHighDaysAgo,
                r.StMetrics.LowestLow,   r.StMetrics.LowestLowDaysAgo) { }
        }

        private sealed record AggRow(
            string Symbol, string ScanDate, string Rec, int Rank,
            double AggScore, double Mo, double W, double D, double H, double M15, double M5,
            int BullTf, int BearTf, decimal LastClose,
            decimal YH, decimal YL, double Pct52wH,
            decimal MH, decimal ML, decimal WkH, decimal WkL,
            bool StBull, int StDays, string StFlip)
        {
            public AggRow(AggregatedResult a) : this(
                a.Symbol, a.ScanDate.ToString("yyyy-MM-dd"),
                a.RecommendationType, a.Rank,
                a.AggregateScore, a.MonthlyScore, a.WeeklyScore, a.DailyScore,
                a.HourlyScore, a.FifteenMinScore, a.FiveMinScore,
                a.BullishTfCount, a.BearishTfCount, a.LastClose,
                a.Levels.YearlyHigh, a.Levels.YearlyLow, a.Levels.PctFrom52wHigh,
                a.Levels.MonthlyHigh, a.Levels.MonthlyLow,
                a.Levels.WeeklyHigh,  a.Levels.WeeklyLow,
                a.StMetrics.CurrentlyBullish, a.StMetrics.TradingDaysInTrend,
                a.StMetrics.LastFlipDate.ToString("yyyy-MM-dd")) { }
        }
    }

    // =========================================================================
    //  CONSOLE REPORTER  (unified PrintLine, live progress, clean panels)
    // =========================================================================
    internal static class ConsoleReporter
    {
        private const int W = 122;
        private static int _progressRow = -1;
        private static readonly object _consoleLock = new();

        // ── Progress (live in-place updates) ─────────────────────────────────
        public static void InitProgress(int total)
        {
            if (Console.IsOutputRedirected) return;
            Console.WriteLine();
            _progressRow = Console.CursorTop;
            UpdateProgress(0, total, "starting…", "");
        }

        public static void UpdateProgress(int done, int total, string ticker, string tf)
        {
            if (Console.IsOutputRedirected || _progressRow < 0) return;
            lock (_consoleLock)
            {
                try
                {
                    int saved  = Console.CursorTop;
                    int savedL = Console.CursorLeft;
                    Console.SetCursorPosition(0, _progressRow);
                    int pct  = total > 0 ? (done * 100 / total) : 0;
                    int fill = pct * 40 / 100;
                    string bar = new string('█', fill) + new string('░', 40 - fill);
                    string line = $"  [{bar}] {done,4}/{total} ({pct,3}%)  {ticker,-14} [{tf,-3}]";
                    Console.Write(line.PadRight(W));
                    Console.SetCursorPosition(savedL, saved);
                }
                catch { /* terminal might not support cursor ops */ }
            }
        }

        public static void ClearProgress()
        {
            if (Console.IsOutputRedirected || _progressRow < 0) return;
            lock (_consoleLock)
            {
                try { Console.SetCursorPosition(0, _progressRow); Console.Write(new string(' ', W)); }
                catch { }
            }
        }

        // ── Section helpers ───────────────────────────────────────────────────
        public static void PrintLine(char ch = '─') => Console.WriteLine(new string(ch, W));

        private static void Center(string text)
        {
            int pad = Math.Max(0, (W - text.Length) / 2);
            Console.WriteLine(new string(' ', pad) + text);
        }

        private static void Colored(string text, ConsoleColor c, bool nl = false)
        {
            Console.ForegroundColor = c;
            if (nl) Console.WriteLine(text); else Console.Write(text);
            Console.ResetColor();
        }

        // ── Header ────────────────────────────────────────────────────────────
        public static void PrintHeader(DateTime scanDate, int totalTickers)
        {
            Console.Clear();
            Console.OutputEncoding = Encoding.UTF8;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"
  ╔═══════════════════════════════════════════════════════════════════════╗
  ║   NSE MOMENTUM SCANNER  ·  Multi-Timeframe  ·  v4.0  ·  .NET 8      ║
  ╚═══════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            PrintLine('─');
            Console.WriteLine($"  Date     : {scanDate:ddd, dd-MMM-yyyy}  " +
                              $"│  Market day: {(NseCalendar.IsMarketDay(scanDate) ? "YES" : "NO (holiday/weekend)")}");
            Console.WriteLine($"  Tickers  : {totalTickers}  " +
                              $"│  Timeframes: {string.Join(" · ", Config.ScanTimeframes.Select(t => t.Label))}  " +
                              $"│  History: {Config.ScanHistoryDays} days");
            Console.WriteLine($"  Database : {Config.DbPath}   │  CSV: {Config.CsvBaseDir}/");
            PrintLine('─');
        }

        // ── Per-TF summary (one line per TF) ─────────────────────────────────
        public static void PrintTimeframeSummary(IReadOnlyList<ScanResult> results,
                                                  string tfLabel, DateTime scanDate)
        {
            var td    = results.Where(r => r.ScanDate.Date == scanDate.Date
                                        && r.Timeframe == tfLabel).ToList();
            int bulls = td.Count(r => r.TotalScore > 0);
            int bears = td.Count(r => r.TotalScore < 0);
            double avg = td.Count > 0 ? td.Average(r => r.TotalScore) : 0;
            Console.Write($"  [{tfLabel,3}] Scored:{td.Count,5}  ");
            Colored($"Bulls:{bulls,4}", ConsoleColor.Green);
            Console.Write("  ");
            Colored($"Bears:{bears,4}", ConsoleColor.Red);
            Console.WriteLine($"  Avg:{avg,+7:F1}");
        }

        // ── Multi-TF score matrix ─────────────────────────────────────────────
        public static void PrintScoreMatrix(IReadOnlyList<AggregatedResult> results)
        {
            PrintLine('═');
            Center("  🗂   MULTI-TIMEFRAME SCORE MATRIX  (Top Ranked)  ");
            PrintLine('═');
            Console.WriteLine(
                $"  {"#",3} {"Symbol",-13} {"Agg",+7} {"1mo",+7} {"1wk",+7} {"1d",+7}" +
                $" {"1h",+7} {"15m",+7} {"5m",+7}  {"B✓",3} {"X✗",3}  {"Rec",-14}");
            PrintLine('─');
            int shown = 0;
            foreach (var a in results)
            {
                if (shown++ >= Config.TopBuyCount + Config.TopSellCount) break;
                ConsoleColor c = a.RecommendationType == "BUY"           ? ConsoleColor.Green
                               : a.RecommendationType == "IMMEDIATE_SELL"? ConsoleColor.Red
                               :                                            ConsoleColor.Gray;
                Colored($"  {a.Rank,3} {a.Symbol,-13}", c);
                Console.WriteLine(
                    $" {a.AggregateScore,+6:F1} {a.MonthlyScore,+6:F0} {a.WeeklyScore,+6:F0} {a.DailyScore,+6:F0}" +
                    $" {a.HourlyScore,+6:F0} {a.FifteenMinScore,+6:F0} {a.FiveMinScore,+6:F0}" +
                    $"  {a.BullishTfCount,2}  {a.BearishTfCount,2}  [{a.RecommendationType,-12}]");
            }
            PrintLine('─');
        }

        // ── Top BUY table ─────────────────────────────────────────────────────
        public static void PrintBuys(IReadOnlyList<AggregatedResult> results)
        {
            var buys = results.Where(r => r.RecommendationType == "BUY").ToList();
            PrintLine('═');
            Center($"  ✅  TOP {buys.Count} BUY CANDIDATES  [Weighted Composite Score]  ✅  ");
            PrintLine('═');
            PrintAggHeader();
            PrintLine('─');
            foreach (var r in buys) PrintAggRow(r, ConsoleColor.Green);
            PrintLine('─');
        }

        // ── Top SELL table ────────────────────────────────────────────────────
        public static void PrintSells(IReadOnlyList<AggregatedResult> results)
        {
            var sells = results.Where(r => r.RecommendationType == "IMMEDIATE_SELL").ToList();
            PrintLine('═');
            Center($"  🔴  TOP {sells.Count} IMMEDIATE SELL CANDIDATES  🔴  ");
            PrintLine('═');
            PrintAggHeader();
            PrintLine('─');
            foreach (var r in sells) PrintAggRow(r, ConsoleColor.Red);
            PrintLine('─');
        }

        // ── Supertrend analysis panel ─────────────────────────────────────────
        public static void PrintSupertrendPanel(IReadOnlyList<AggregatedResult> results)
        {
            PrintLine('═');
            Center("  📊  SUPERTREND DEEP DIVE  (Daily TF · Top 15 Buys)  ");
            PrintLine('═');
            Console.WriteLine(
                $"  {"Symbol",-13} {"Dir",6} {"Days",6} {"Flip",12} {"HH",10} {"HH-d",6}" +
                $" {"HL",10} {"HL-d",6} {"LH",10} {"LH-d",6} {"LL",10} {"LL-d",6}");
            PrintLine('─');
            foreach (var a in results.Where(r => r.RecommendationType == "BUY").Take(15))
            {
                var M = a.StMetrics;
                ConsoleColor c = M.CurrentlyBullish ? ConsoleColor.Green : ConsoleColor.Red;
                Colored($"  {a.Symbol,-13}", c);
                Console.WriteLine(
                    $" {(M.CurrentlyBullish?"▲ BULL":"▼ BEAR"),6}" +
                    $" {M.TradingDaysInTrend,5}d {M.LastFlipDate:dd-MMM-yy}" +
                    $" {M.HighestHigh,10:F2} {M.HighestHighDaysAgo,4}d" +
                    $" {M.HighestLow,10:F2} {M.HighestLowDaysAgo,4}d" +
                    $" {M.LowestHigh,10:F2} {M.LowestHighDaysAgo,4}d" +
                    $" {M.LowestLow,10:F2} {M.LowestLowDaysAgo,4}d");
            }
            PrintLine('─');
        }

        // ── Price levels panel ────────────────────────────────────────────────
        public static void PrintPriceLevels(IReadOnlyList<AggregatedResult> results)
        {
            PrintLine('═');
            Center("  📈  PRICE LEVELS  (Top 20 Buys + Top 10 Sells)  ");
            PrintLine('═');
            Console.WriteLine(
                $"  {"Symbol",-13} {"Close",9} {"Wk-H",9} {"Wk-L",9}" +
                $" {"Mo-H",9} {"Mo-L",9} {"Qtr-H",9}" +
                $" {"52w-H",9} {"52w-L",9} {"vs52H%",8}");
            PrintLine('─');
            var top = results.Where(r => r.RecommendationType == "BUY").Take(20)
                .Concat(results.Where(r => r.RecommendationType == "IMMEDIATE_SELL").Take(10));
            foreach (var a in top)
            {
                var L = a.Levels;
                ConsoleColor c = a.RecommendationType == "BUY" ? ConsoleColor.Cyan : ConsoleColor.Yellow;
                Colored($"  {a.Symbol,-13}", c);
                Console.WriteLine(
                    $" {a.LastClose,9:F2} {L.WeeklyHigh,9:F2} {L.WeeklyLow,9:F2}" +
                    $" {L.MonthlyHigh,9:F2} {L.MonthlyLow,9:F2} {L.QuarterlyHigh,9:F2}" +
                    $" {L.YearlyHigh,9:F2} {L.YearlyLow,9:F2}" +
                    $" {L.PctFrom52wHigh,+7:F2}%");
            }
            PrintLine('─');
        }

        // ── Footer ────────────────────────────────────────────────────────────
        public static void PrintFooter(int sessionId, int scored, int skipped, TimeSpan elapsed)
        {
            PrintLine('═');
            Console.WriteLine(
                $"  Session #{sessionId}  │  Scored:{scored}  Skipped:{skipped}  │" +
                $"  Elapsed:{elapsed:mm\\:ss}  │  DB:{Config.DbPath}  │  CSV:{Config.CsvBaseDir}/");
            PrintLine('═');
            Console.WriteLine();
        }

        // ── Private helpers ───────────────────────────────────────────────────
        private static void PrintAggHeader() =>
            Console.WriteLine(
                $"  {"#",3} {"Symbol",-13} {"Score",+7} {"1mo",+7} {"1wk",+7} {"1d",+7}" +
                $" {"1h",+7} {"15m",+7} {"5m",+7} {"Close",9} {"vs52H%",8}" +
                $" {"STDays",7} {"STDir",6}");

        private static void PrintAggRow(AggregatedResult r, ConsoleColor color)
        {
            Colored(
                $"  {r.Rank,3} {r.Symbol,-13} {r.AggregateScore,+6:F1}" +
                $" {r.MonthlyScore,+6:F0} {r.WeeklyScore,+6:F0} {r.DailyScore,+6:F0}" +
                $" {r.HourlyScore,+6:F0} {r.FifteenMinScore,+6:F0} {r.FiveMinScore,+6:F0}" +
                $" {r.LastClose,9:F2} {r.Levels.PctFrom52wHigh,+7:F2}%" +
                $" {r.StMetrics.TradingDaysInTrend,6}d" +
                $" {(r.StMetrics.CurrentlyBullish ? "▲BULL" : "▼BEAR"),5}",
                color, nl: true);
        }
    }

    // =========================================================================
    //  SCAN ENGINE
    // =========================================================================
    internal sealed class ScanEngine : IDisposable
    {
        private readonly YahooDataFetcher _fetcher = new();
        private readonly SqliteRepository _db      = new();
        private bool _disposed;

        public async Task RunAsync(CancellationToken ct = default)
        {
            var sw       = System.Diagnostics.Stopwatch.StartNew();
            var today    = DateTime.Today;
            var tickers  = BuildUniverse();
            int total    = tickers.Count;
            int sessionId= _db.InsertSession("Nifty200+SMID+NewListings", total);
            int scored   = 0, skipped = 0, progress = 0;

            ConsoleReporter.PrintHeader(today, total);
            if (!NseCalendar.IsMarketDay(today))
                Log.Warning("Today ({D:ddd dd-MMM-yyyy}) is not an NSE market day — " +
                            "scoring historical data as usual.", today);

            var scoreStart = today.AddDays(-Config.ScanHistoryDays);
            var scoreEnd   = today;

            // ── v4.0: fetch the market benchmark ONCE (not per ticker) and classify the
            // regime before scoring the universe — RS needs the benchmark's own close series,
            // and the aggregator needs the regime to gate BUY generation. A benchmark fetch
            // failure degrades to NEUTRAL (no gating) rather than blocking the whole scan.
            Log.Information("Fetching market benchmark {Sym} for regime + relative strength…",
                Config.MarketBenchmarkSymbol);
            var (marketRegime, benchmarkCloseByDate) =
                await MarketRegimeAnalyzer.BuildAsync(_fetcher, today, ct);
            Log.Information("Market regime: {Regime} (benchmark {Close:F1}, EMA50={E50:F1}, EMA200={E200:F1})",
                marketRegime.Regime, marketRegime.BenchmarkClose, marketRegime.Ema50, marketRegime.Ema200);

            // ── v4.1: load sector/fundamentals/earnings reference data once per scan ─────────
            // Safe no-op if the file doesn't exist — see ReferenceDataProvider's header comment.
            ReferenceDataProvider.Load(Config.ReferenceDataCsvPath);

            // All last-day results kept for aggregation; full history streamed to DB
            var lastDayResults = new ConcurrentBag<ScanResult>();
            var allForCsv      = new ConcurrentBag<ScanResult>(); // for per-date CSV
            var skipReasons    = new ConcurrentBag<(string Symbol, string Timeframe, string Reason)>();

            ConsoleReporter.InitProgress(total);

            await Parallel.ForEachAsync(
                tickers,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Config.MaxConcurrentFetches,
                    CancellationToken      = ct,
                },
                async (symbol, innerCt) =>
                {
                    var tickerResults = new List<ScanResult>();

                    foreach (var (interval, tfLabel, _, lookback, vcpEnabled) in Config.ScanTimeframes)
                    {
                        if (innerCt.IsCancellationRequested) break;

                        try
                        {
                            var quotes = await _fetcher.FetchAsync(symbol, interval, lookback, innerCt);
                            var dq     = DataValidator.Validate(quotes, interval);

                            if (!dq.IsValid)
                            {
                                Log.Debug("Skip {Sym} {TF}: {Reason}", symbol, tfLabel, dq.FailReason);
                                skipReasons.Add((symbol, tfLabel, dq.FailReason));
                                continue;
                            }

                            var tfResults = TickerScorer.Score(
                                symbol, interval, tfLabel, vcpEnabled,
                                quotes, scoreStart, scoreEnd, benchmarkCloseByDate);

                            if (tfResults.Count == 0)
                            {
                                skipReasons.Add((symbol, tfLabel, "No bars cleared the warmup/minimum-bars threshold"));
                                continue;
                            }

                            // All results → CSV accumulator
                            foreach (var r in tfResults) allForCsv.Add(r);

                            // Last day only → aggregation accumulator. tfResults is already in
                            // ascending ScanDate order (TickerScorer iterates datesToScore sorted
                            // ascending), so the last element IS the max — no need for an O(n)
                            // MaxBy scan per ticker per timeframe.
                            lastDayResults.Add(tfResults[^1]);

                            tickerResults.AddRange(tfResults);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Isolate failures (thin history on new listings, indicator edge cases,
                            // transient bad data) to this ticker+timeframe. Never let a single symbol
                            // take down the whole Parallel.ForEachAsync batch.
                            Log.Warning("Scoring error {Sym} [{TF}]: {Msg}", symbol, tfLabel, ex.Message);
                            skipReasons.Add((symbol, tfLabel, $"Error: {ex.Message}"));
                        }
                    }

                    if (tickerResults.Count > 0)
                    {
                        try { await _db.BulkInsertScanResultsAsync(sessionId, tickerResults); }
                        catch (Exception ex) { Log.Warning("DB insert error {Sym}: {Msg}", symbol, ex.Message); }
                        Interlocked.Increment(ref scored);
                    }
                    else
                    {
                        Interlocked.Increment(ref skipped);
                    }

                    int p = Interlocked.Increment(ref progress);
                    string curTf = tickerResults.Count > 0
                        ? tickerResults[^1].Timeframe : "";
                    ConsoleReporter.UpdateProgress(p, total, symbol, curTf);
                });

            ConsoleReporter.ClearProgress();
            _db.UpdateSessionScored(sessionId, scored);

            // ── Write per-date CSV files ───────────────────────────────────────
            // Pre-group once (O(n)) instead of re-scanning the full csvList for every
            // date×timeframe combination (was O(dates × timeframes × n) — with ~500 tickers ×
            // 6 timeframes × ScanHistoryDays rows, that's tens of millions of comparisons for
            // what a single GroupBy does in one linear pass). Mirrors the "pre-group for O(1)
            // lookup" fix already applied to aggregation back in v2.0 — this loop was missed.
            Log.Information("Writing CSVs…");
            var csvList   = allForCsv.ToList();
            var csvGroups = csvList
                .GroupBy(r => (Date: r.ScanDate.Date, r.Timeframe))
                .ToDictionary(g => g.Key, g => g.ToList());
            var allDates  = csvGroups.Keys.Select(k => k.Date).Distinct().OrderBy(d => d).ToList();

            foreach (var date in allDates)
                foreach (var (_, tfLabel, _, _, _) in Config.ScanTimeframes)
                    if (csvGroups.TryGetValue((date, tfLabel), out var subset))
                        CsvExporter.WriteScanResults(subset, date, tfLabel);

            // ── Aggregate last day ────────────────────────────────────────────
            // marketRegime is a class (reference type) — Aggregate fills in its Breadth*
            // fields from the universe's own daily results before we persist it below.
            var lastDay     = allDates.Count > 0 ? allDates[^1] : today;
            var allLastDay  = lastDayResults.ToList();
            var aggResults  = MultiTfAggregator.Aggregate(allLastDay, lastDay, marketRegime);

            if (aggResults.Count > 0)
            {
                try { await _db.BulkInsertAggregatedAsync(sessionId, aggResults); }
                catch (Exception ex) { Log.Warning("DB agg insert error: {Msg}", ex.Message); }
                CsvExporter.WriteAggregated(aggResults, lastDay);
            }

            // ── v4.0: persist market regime + data-quality summary ─────────────
            try { await _db.InsertMarketRegimeAsync(sessionId, lastDay, marketRegime); }
            catch (Exception ex) { Log.Warning("DB regime insert error: {Msg}", ex.Message); }

            try { await _db.BulkInsertDataQualityIssuesAsync(sessionId, skipReasons.ToList()); }
            catch (Exception ex) { Log.Warning("DB data-quality insert error: {Msg}", ex.Message); }

            // ── v4.1: sector rotation summary ───────────────────────────────────
            var sectorRotation = MultiTfAggregator.BuildSectorRotationSummary(aggResults);
            try { await _db.BulkInsertSectorRotationAsync(sessionId, lastDay, sectorRotation); }
            catch (Exception ex) { Log.Warning("DB sector-rotation insert error: {Msg}", ex.Message); }

            // ── v4.1: record which weight-set produced this session, for audit/dashboard ────
            string weightSetSource = Config.ActiveOptimizedWeights is { } ow
                ? $"optimized:{ow.GeneratedAtUtc:yyyy-MM-dd}:{ow.Objective}"
                : "default";
            try { await _db.UpdateSessionWeightSourceAsync(sessionId, weightSetSource); }
            catch (Exception ex) { Log.Warning("DB weight-source update error: {Msg}", ex.Message); }

            // ── Prune old sessions ────────────────────────────────────────────
            _db.PruneOldSessions();

            // ── Console output — last day only ────────────────────────────────
            Console.WriteLine();
            Console.WriteLine($"  Market regime: {marketRegime.Regime}  |  Breadth: " +
                $"{marketRegime.BreadthBullish}/{marketRegime.BreadthTotal} bullish " +
                $"({marketRegime.BreadthPctBullish:F1}%)  |  Weights: {weightSetSource}");
            if (sectorRotation.Count > 0)
            {
                var topSectors = sectorRotation.Take(3)
                    .Select(s => $"{s.Sector} ({s.AvgScore:+0.0;-0.0;0.0})");
                Console.WriteLine($"  Leading sectors today: {string.Join(", ", topSectors)}");
            }
            Console.WriteLine($"  Timeframe breakdown for {lastDay:yyyy-MM-dd}:");
            ConsoleReporter.PrintLine('─');
            foreach (var (_, tfLabel, _, _, _) in Config.ScanTimeframes)
                ConsoleReporter.PrintTimeframeSummary(allLastDay, tfLabel, lastDay);

            ConsoleReporter.PrintScoreMatrix(aggResults);
            ConsoleReporter.PrintBuys(aggResults);
            ConsoleReporter.PrintSells(aggResults);
            ConsoleReporter.PrintSupertrendPanel(aggResults);
            ConsoleReporter.PrintPriceLevels(aggResults);
            ConsoleReporter.PrintFooter(sessionId, scored, skipped, sw.Elapsed);
        }

        private static List<string> BuildUniverse() =>
            Nifty200Universe.Tickers
                .Concat(NiftySMIDUniverse.Tickers)
                .Concat(NewListingsUniverse.Tickers)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t)
                .ToList();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _fetcher.Dispose();
            _db.Dispose();
        }
    }

    // =========================================================================
    //  DASHBOARD DATA BUILDER
    //  Reads the live SQLite DB (read-only connection, WAL-safe for concurrent
    //  access while the scanner is writing) and builds the same JSON shape that
    //  export_dashboard_data.py produces — so nse_dashboard.html needs no
    //  contract changes to go from a static snapshot to a live, real-time feed.
    // =========================================================================
    internal static class DashboardDataBuilder
    {
        private sealed record SessionRow(string RunTimestamp, string Universe, int Total, int Scored, string WeightSource);

        private sealed record AggRow(
            string Symbol, string? Rec, int? Rank, double? Close, double? AggScore,
            double? MoSc, double? WSc, double? DSc, double? HSc, double? M15Sc, double? M5Sc,
            int? BullTf, int? BearTf,
            double? YH, double? YL, double? QH, double? QL, double? MH, double? ML,
            double? WH, double? WL, double? P52H, double? P52L,
            bool StBull, int? StDays, string? StFlip, int? StCalDays,
            double? StHH, int? StHHAgo, double? StLL, int? StLLAgo,
            double? RsRank, double? AvgTurnover, bool IsLiquid, double? ExtPct, bool IsExtended,
            double? Atr, double? SuggStop, double? SuggTarget, string? GateReason,
            string? Sector, double? SectorRank, double? Pe, double? Roe, double? DebtEquity,
            double? EpsGrowth, double? Pledge, string? NextEarnings, bool InEarningsBlackout);

        private sealed record DailyDetail(
            double? Adx, double? Pdi, double? Mdi, double? VolR, double? AvgVol, bool Vcp,
            double? HmaFast, double? HmaSlow, double? EmaFast, double? EmaSlow,
            double? StHl, int? StHlAgo, double? StLh, int? StLhAgo,
            double? ScSt, double? ScHma, double? ScAdx, double? ScVcp, double? ScVol, double? ScTr);

        public static async Task<string> BuildJsonAsync(
            string dbPath, int? sessionId, string? scanDateOverride, CancellationToken ct)
        {
            if (!File.Exists(dbPath))
                throw new InvalidOperationException(
                    $"Database not found at '{dbPath}'. Run the scanner at least once first.");

            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            await conn.OpenAsync(ct);
            await using (var pragma = new SqliteCommand("PRAGMA busy_timeout=5000;", conn))
                await pragma.ExecuteNonQueryAsync(ct);

            int sid = sessionId ??
                await ScalarIntAsync(conn, "SELECT session_id FROM scan_sessions ORDER BY session_id DESC LIMIT 1", ct) ??
                throw new InvalidOperationException("No scan sessions found in database yet. Run the scanner first.");

            var session = await ReadSessionAsync(conn, sid, ct)
                ?? throw new InvalidOperationException($"Session #{sid} not found.");

            string? scanDate = scanDateOverride ?? await ScalarStringAsync(
                conn, "SELECT MAX(scan_date) FROM aggregated_results WHERE session_id=@sid", ct, ("@sid", sid));

            var aggRows = scanDate == null ? new List<AggRow>() : await ReadAggRowsAsync(conn, sid, scanDate, ct);
            var daily   = scanDate == null ? new Dictionary<string, DailyDetail>()
                                            : await ReadDailyDetailsAsync(conn, sid, scanDate, ct);

            var topBuySymbols = aggRows.Where(r => r.Rec == "BUY").Select(r => r.Symbol).Take(30).ToList();
            var history = topBuySymbols.Count == 0
                ? new Dictionary<string, List<(string date, double score)>>()
                : await ReadHistoryAsync(conn, sid, topBuySymbols, ct);

            var tfBreakdown = scanDate == null
                ? new List<(string tf, int total, int bulls, int bears, double avg)>()
                : await ReadTfBreakdownAsync(conn, sid, scanDate, ct);

            // v4.0: market regime snapshot + a rollup of why tickers were skipped this session
            var regime      = scanDate == null ? null : await ReadMarketRegimeAsync(conn, sid, scanDate, ct);
            var dataQuality = await ReadDataQualitySummaryAsync(conn, sid, ct);
            // v4.1: sector rotation summary
            var sectorRotation = scanDate == null
                ? new List<(string Sector, int Count, int Bullish, double AvgScore, double AvgRs, double Rank)>()
                : await ReadSectorRotationAsync(conn, sid, scanDate, ct);

            // v4.2: price/volume shockers + consecutive gainers/losers — computed fresh from
            // this session's own daily bars. Wrapped defensively: a bug or an unexpectedly
            // shaped DB should never take down the rest of the dashboard payload, just this
            // one section (degrades to empty, logged, same philosophy as every other
            // best-effort panel here).
            ShockersResult? shockers = null;
            if (scanDate != null)
            {
                try
                {
                    var series = await ReadDailySeriesAsync(conn, sid, ct);
                    shockers = ShockersAnalyzer.Build(series);
                }
                catch (Exception ex)
                {
                    Log.Warning("Dashboard shockers computation failed (non-fatal): {Msg}", ex.Message);
                }
            }

            var stocks = new JsonArray();
            foreach (var r in aggRows)
            {
                daily.TryGetValue(r.Symbol, out var d);
                history.TryGetValue(r.Symbol, out var hist);
                stocks.Add(BuildStockJson(r, d, hist));
            }

            var tfOut = new JsonArray();
            foreach (var t in tfBreakdown)
                tfOut.Add(new JsonObject
                {
                    ["tf"] = t.tf, ["total"] = t.total, ["bulls"] = t.bulls,
                    ["bears"] = t.bears, ["avg"] = Math.Round(t.avg, 1),
                });

            var sectorOut = new JsonArray();
            foreach (var s in sectorRotation)
                sectorOut.Add(new JsonObject
                {
                    ["sector"] = s.Sector, ["count"] = s.Count, ["bullish"] = s.Bullish,
                    ["avgScore"] = s.AvgScore, ["avgRs"] = s.AvgRs, ["rank"] = s.Rank,
                });

            var root = new JsonObject
            {
                ["generated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["scanDate"]  = scanDate,
                ["session"]   = new JsonObject
                {
                    ["id"]           = sid,
                    ["runDate"]      = session.RunTimestamp.Length >= 10 ? session.RunTimestamp[..10] : session.RunTimestamp,
                    ["total"]        = session.Total,
                    ["scored"]       = session.Scored,
                    ["universe"]     = session.Universe,
                    ["weightSource"] = session.WeightSource, // v4.1: "default" or "optimized:YYYY-MM-DD:objective"
                },
                ["stocks"]      = stocks,
                ["tfBreakdown"] = tfOut,
                ["sectorRotation"] = sectorOut,
                ["marketRegime"] = regime == null ? null : new JsonObject
                {
                    ["benchmark"]       = regime.Value.Symbol,
                    ["close"]           = regime.Value.Close,
                    ["ema50"]           = regime.Value.Ema50,
                    ["ema200"]          = regime.Value.Ema200,
                    ["regime"]          = regime.Value.Regime,
                    ["breadthTotal"]    = regime.Value.BreadthTotal,
                    ["breadthBullish"]  = regime.Value.BreadthBullish,
                    ["breadthBearish"]  = regime.Value.BreadthBearish,
                    ["breadthPctBullish"] = regime.Value.BreadthPctBullish,
                },
                ["dataQuality"] = BuildDataQualityJson(dataQuality),
                ["shockers"] = shockers == null ? null : BuildShockersJson(shockers),
            };

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }

        private static async Task<List<(string Sector, int Count, int Bullish, double AvgScore, double AvgRs, double Rank)>>
            ReadSectorRotationAsync(SqliteConnection c, int sid, string scanDate, CancellationToken ct)
        {
            const string sql = @"
                SELECT sector, stock_count, bullish_count, avg_score, avg_rs_raw, rank_percentile
                FROM sector_rotation WHERE session_id=@sid AND scan_date=@dt
                ORDER BY rank_percentile DESC";
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@dt", scanDate);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var list = new List<(string, int, int, double, double, double)>();
            while (await rd.ReadAsync(ct))
                list.Add((rd.GetString(0), GetI(rd, 1) ?? 0, GetI(rd, 2) ?? 0,
                    GetD(rd, 3) ?? 0, GetD(rd, 4) ?? 0, GetD(rd, 5) ?? 50));
            return list;
        }

        private static JsonObject BuildDataQualityJson(List<(string Symbol, string Timeframe, string Reason)> issues)
        {
            var byReason = new JsonArray();
            foreach (var g in issues.GroupBy(x => SummarizeReason(x.Reason)).OrderByDescending(g => g.Count()).Take(8))
                byReason.Add(new JsonObject { ["reason"] = g.Key, ["count"] = g.Count() });

            return new JsonObject
            {
                ["totalIssues"] = issues.Count,
                ["byReason"] = byReason,
            };
        }

        /// <summary>Collapses free-text skip reasons (which embed specific numbers/dates) into a
        /// small set of stable buckets so the dashboard shows "Stale data (x12)" instead of 12
        /// near-identical strings that each only ever occur once.</summary>
        private static string SummarizeReason(string reason)
        {
            if (reason.StartsWith("Only", StringComparison.OrdinalIgnoreCase)) return "Too few bars";
            if (reason.StartsWith("Stale", StringComparison.OrdinalIgnoreCase)) return "Stale data";
            if (reason.Contains("zero-volume", StringComparison.OrdinalIgnoreCase)) return "Too many zero-volume bars";
            if (reason.StartsWith("OHLC", StringComparison.OrdinalIgnoreCase)) return "OHLC integrity failure";
            if (reason.StartsWith("No bars cleared", StringComparison.OrdinalIgnoreCase)) return "Insufficient warmup history";
            if (reason.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) return "Fetch/scoring error";
            return "Other";
        }

        private static JsonObject BuildStockJson(AggRow r, DailyDetail? d, List<(string date, double score)>? hist)
        {
            static double? PctFrom(double? level, double? close) =>
                (level is > 0 && close is not null) ? Math.Round((close.Value - level.Value) / level.Value * 100, 2) : null;

            var histArr = new JsonArray();
            if (hist != null)
                foreach (var h in hist)
                    histArr.Add(new JsonObject { ["date"] = h.date, ["score"] = Math.Round(h.score, 1) });

            return new JsonObject
            {
                ["sym"]      = r.Symbol,
                ["rec"]      = r.Rec ?? "HOLD",
                ["rank"]     = r.Rank,
                ["close"]    = r.Close,
                ["aggScore"] = r.AggScore,
                ["wSc"] = r.WSc, ["dSc"] = r.DSc, ["hSc"] = r.HSc, ["m15Sc"] = r.M15Sc, ["m5Sc"] = r.M5Sc,
                ["moSc"] = r.MoSc,
                ["bullTf"]   = r.BullTf ?? 0,
                ["bearTf"]   = r.BearTf ?? 0,
                ["sector"]   = r.Sector ?? "Unclassified",           // v4.1: real sector (CSV override > built-in map)
                ["sectorRank"] = r.SectorRank ?? 50,                  // v4.1: sector-rotation percentile
                ["levels"] = new JsonObject
                {
                    ["yH"] = r.YH, ["yL"] = r.YL, ["qH"] = r.QH, ["qL"] = r.QL,
                    ["mH"] = r.MH, ["mL"] = r.ML, ["wH"] = r.WH, ["wL"] = r.WL,
                    ["p52H"] = r.P52H, ["p52L"] = r.P52L,
                    ["pMH"] = PctFrom(r.MH, r.Close),
                    ["pWH"] = PctFrom(r.WH, r.Close),
                },
                ["st"] = new JsonObject
                {
                    ["bull"]    = r.StBull,
                    ["days"]    = r.StDays ?? 0,
                    ["flip"]    = r.StFlip ?? "",
                    ["calDays"] = r.StCalDays ?? 0,
                    ["hh"] = r.StHH, ["hhAgo"] = r.StHHAgo ?? 0,
                    ["ll"] = r.StLL, ["llAgo"] = r.StLLAgo ?? 0,
                    ["hl"] = d?.StHl, ["hlAgo"] = d?.StHlAgo ?? 0,
                    ["lh"] = d?.StLh, ["lhAgo"] = d?.StLhAgo ?? 0,
                },
                ["adx"]     = d?.Adx,  ["pdi"] = d?.Pdi,  ["mdi"] = d?.Mdi,
                ["volR"]    = d?.VolR, ["avgVol"] = d?.AvgVol, ["vcp"] = d?.Vcp ?? false,
                ["hmaFast"] = d?.HmaFast, ["hmaSlow"] = d?.HmaSlow,
                ["emaFast"] = d?.EmaFast, ["emaSlow"] = d?.EmaSlow,
                ["scores"] = new JsonObject
                {
                    ["st"] = d?.ScSt, ["hma"] = d?.ScHma, ["adx"] = d?.ScAdx,
                    ["vcp"] = d?.ScVcp, ["vol"] = d?.ScVol, ["tr"] = d?.ScTr,
                },
                // ── v4.0: relative strength, liquidity, extension, risk levels ─────────
                ["rsRank"]        = r.RsRank,
                ["avgTurnover"]   = r.AvgTurnover,
                ["isLiquid"]      = r.IsLiquid,
                ["extensionPct"]  = r.ExtPct,
                ["isExtended"]    = r.IsExtended,
                ["atr"]           = r.Atr,
                ["suggestedStop"]   = r.SuggStop,
                ["suggestedTarget"] = r.SuggTarget,
                ["gateReason"]      = r.GateReason,
                // ── v4.1: fundamentals + earnings blackout (from data/reference.csv, if present) ──
                ["fundamentals"] = new JsonObject
                {
                    ["pe"]              = r.Pe,
                    ["roePct"]          = r.Roe,
                    ["debtToEquity"]    = r.DebtEquity,
                    ["epsGrowthPct"]    = r.EpsGrowth,
                    ["promoterPledgePct"] = r.Pledge,
                    ["hasData"]         = r.Pe != null || r.Roe != null || r.DebtEquity != null || r.EpsGrowth != null,
                },
                ["nextEarningsDate"]    = r.NextEarnings,
                ["inEarningsBlackout"]  = r.InEarningsBlackout,
                ["history"] = histArr,
            };
        }

        // ── v4.2: shockers / consecutive-movers ─────────────────────────────────
        private static JsonObject BuildShockersJson(ShockersResult sr)
        {
            static JsonArray Rows(IEnumerable<ShockerRow> rows)
            {
                var arr = new JsonArray();
                foreach (var r in rows)
                    arr.Add(new JsonObject
                    {
                        ["sym"] = r.Symbol, ["close"] = r.Close, ["pctChange"] = r.PctChange,
                        ["volR"] = r.VolumeRatio, ["avgTurnover"] = r.AvgTurnoverInr,
                    });
                return arr;
            }
            static JsonArray Streaks(IEnumerable<StreakRow> rows)
            {
                var arr = new JsonArray();
                foreach (var r in rows)
                    arr.Add(new JsonObject
                    {
                        ["sym"] = r.Symbol, ["len"] = r.StreakLength, ["dir"] = r.Direction,
                        ["start"] = r.StreakStart.ToString("yyyy-MM-dd"), ["end"] = r.StreakEnd.ToString("yyyy-MM-dd"),
                        ["cumPct"] = r.CumulativePctChange, ["close"] = r.LastClose,
                    });
                return arr;
            }

            var periods = new JsonObject();
            foreach (var (_, label) in Config.ShockerPeriods)
            {
                if (!sr.Periods.TryGetValue(label, out var p)) continue;
                periods[label] = new JsonObject
                {
                    ["gainers"] = Rows(p.PriceGainers),
                    ["losers"]  = Rows(p.PriceLosers),
                    ["volume"]  = Rows(p.VolumeSurges),
                };
            }

            return new JsonObject
            {
                ["periods"] = periods,
                ["consecutive"] = new JsonObject
                {
                    ["daily"] = new JsonObject
                    {
                        ["gainers"] = Streaks(sr.DailyStreakGainers),
                        ["losers"]  = Streaks(sr.DailyStreakLosers),
                        ["minStreak"] = Config.ConsecutiveDailyMinStreak,
                    },
                    ["weekly"] = new JsonObject
                    {
                        ["gainers"] = Streaks(sr.WeeklyStreakGainers),
                        ["losers"]  = Streaks(sr.WeeklyStreakLosers),
                        ["minStreak"] = Config.ConsecutiveWeeklyMinStreak,
                    },
                },
            };
        }

        /// <summary>Reads every "1d" bar persisted for this session, grouped by symbol and
        /// ordered ascending by date — the raw input ShockersAnalyzer.Build walks to compute
        /// every rolling-period shocker and consecutive-streak in one pass per symbol. Scoped
        /// to session_id (not a date range) since each session already only ever persists the
        /// last Config.ScanHistoryDays calendar days per symbol — see idx_sr_session_tf_sym_date.</summary>
        private static async Task<Dictionary<string, List<ShockersAnalyzer.DailyBar>>> ReadDailySeriesAsync(
            SqliteConnection c, int sid, CancellationToken ct)
        {
            const string sql = @"
                SELECT symbol, scan_date, last_close, last_volume, avg_volume
                FROM scan_results
                WHERE session_id=@sid AND timeframe='1d'
                ORDER BY symbol, scan_date";
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@sid", sid);
            await using var rd = await cmd.ExecuteReaderAsync(ct);

            var map = new Dictionary<string, List<ShockersAnalyzer.DailyBar>>(StringComparer.OrdinalIgnoreCase);
            while (await rd.ReadAsync(ct))
            {
                var sym = rd.GetString(0);
                if (!DateTime.TryParse(rd.GetString(1), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;
                var bar = new ShockersAnalyzer.DailyBar(
                    date, (decimal)(GetD(rd, 2) ?? 0), GetD(rd, 3) ?? 0, GetD(rd, 4) ?? 0);
                if (!map.TryGetValue(sym, out var list)) map[sym] = list = new List<ShockersAnalyzer.DailyBar>();
                list.Add(bar);
            }
            return map;
        }

        // ── low-level readers ────────────────────────────────────────────────
        private static async Task<int?> ScalarIntAsync(SqliteConnection c, string sql, CancellationToken ct)
        {
            await using var cmd = new SqliteCommand(sql, c);
            var v = await cmd.ExecuteScalarAsync(ct);
            return v is null or DBNull ? null : Convert.ToInt32(v);
        }

        private static async Task<string?> ScalarStringAsync(
            SqliteConnection c, string sql, CancellationToken ct, params (string, object)[] p)
        {
            await using var cmd = new SqliteCommand(sql, c);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            var v2 = await cmd.ExecuteScalarAsync(ct);
            return v2 is null or DBNull ? null : (string)v2;
        }

        private static async Task<SessionRow?> ReadSessionAsync(SqliteConnection c, int sid, CancellationToken ct)
        {
            await using var cmd = new SqliteCommand(
                "SELECT run_timestamp, universe, total_tickers, scored_tickers, weight_source " +
                "FROM scan_sessions WHERE session_id=@sid", c);
            cmd.Parameters.AddWithValue("@sid", sid);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            return new SessionRow(rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3),
                rd.IsDBNull(4) ? "default" : rd.GetString(4));
        }

        private static double? GetD(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetDouble(i);
        private static int?    GetI(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetInt32(i);
        private static string? GetS(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);

        private static async Task<List<AggRow>> ReadAggRowsAsync(SqliteConnection c, int sid, string scanDate, CancellationToken ct)
        {
            const string sql = @"
                SELECT symbol, recommendation, rank, last_close, aggregate_score, monthly_score,
                       weekly_score, daily_score, hourly_score, fifteen_min_score, five_min_score,
                       bullish_tf, bearish_tf,
                       yearly_high, yearly_low, quarterly_high, quarterly_low,
                       monthly_high, monthly_low, weekly_high, weekly_low,
                       pct_52w_high, pct_52w_low,
                       st_bullish, st_days, st_flip_date, st_cal_days, st_hh, st_hh_ago, st_ll, st_ll_ago,
                       rs_rank, avg_turnover_inr, is_liquid, extension_pct, is_extended,
                       atr_value, suggested_stop, suggested_target, gate_reason,
                       sector, sector_rank, pe_ratio, roe_pct, debt_to_equity, eps_growth_pct,
                       promoter_pledge_pct, next_earnings_date, in_earnings_blackout
                FROM aggregated_results
                WHERE session_id=@sid AND scan_date=@dt
                ORDER BY aggregate_score DESC";
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@dt", scanDate);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var list = new List<AggRow>();
            while (await rd.ReadAsync(ct))
            {
                list.Add(new AggRow(
                    rd.GetString(0), GetS(rd, 1), GetI(rd, 2), GetD(rd, 3), GetD(rd, 4),
                    GetD(rd, 5), GetD(rd, 6), GetD(rd, 7), GetD(rd, 8), GetD(rd, 9), GetD(rd, 10),
                    GetI(rd, 11), GetI(rd, 12),
                    GetD(rd, 13), GetD(rd, 14), GetD(rd, 15), GetD(rd, 16),
                    GetD(rd, 17), GetD(rd, 18), GetD(rd, 19), GetD(rd, 20),
                    GetD(rd, 21), GetD(rd, 22),
                    !rd.IsDBNull(23) && rd.GetInt32(23) != 0, GetI(rd, 24), GetS(rd, 25), GetI(rd, 26),
                    GetD(rd, 27), GetI(rd, 28), GetD(rd, 29), GetI(rd, 30),
                    GetD(rd, 31), GetD(rd, 32), rd.IsDBNull(33) || rd.GetInt32(33) != 0, GetD(rd, 34),
                    !rd.IsDBNull(35) && rd.GetInt32(35) != 0,
                    GetD(rd, 36), GetD(rd, 37), GetD(rd, 38), GetS(rd, 39),
                    GetS(rd, 40), GetD(rd, 41), GetD(rd, 42), GetD(rd, 43), GetD(rd, 44), GetD(rd, 45),
                    GetD(rd, 46), GetS(rd, 47), !rd.IsDBNull(48) && rd.GetInt32(48) != 0));
            }
            return list;
        }

        private static async Task<Dictionary<string, DailyDetail>> ReadDailyDetailsAsync(
            SqliteConnection c, int sid, string scanDate, CancellationToken ct)
        {
            const string sql = @"
                SELECT symbol, adx_value, pdi_value, mdi_value, volume_ratio, avg_volume, vcp_detected,
                       hma_fast, hma_slow, ema_fast, ema_slow,
                       st_hl, st_hl_ago, st_lh, st_lh_ago,
                       supertrend_score, hma_score, adx_score, vcp_score, volume_score, trend_score
                FROM scan_results
                WHERE session_id=@sid AND scan_date=@dt AND timeframe='1d'";
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@dt", scanDate);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var map = new Dictionary<string, DailyDetail>(StringComparer.OrdinalIgnoreCase);
            while (await rd.ReadAsync(ct))
            {
                var sym = rd.GetString(0);
                map[sym] = new DailyDetail(
                    GetD(rd, 1), GetD(rd, 2), GetD(rd, 3), GetD(rd, 4), GetD(rd, 5),
                    !rd.IsDBNull(6) && rd.GetInt32(6) != 0,
                    GetD(rd, 7), GetD(rd, 8), GetD(rd, 9), GetD(rd, 10),
                    GetD(rd, 11), GetI(rd, 12), GetD(rd, 13), GetI(rd, 14),
                    GetD(rd, 15), GetD(rd, 16), GetD(rd, 17), GetD(rd, 18), GetD(rd, 19), GetD(rd, 20));
            }
            return map;
        }

        private static async Task<Dictionary<string, List<(string date, double score)>>> ReadHistoryAsync(
            SqliteConnection c, int sid, List<string> symbols, CancellationToken ct)
        {
            var result = new Dictionary<string, List<(string, double)>>(StringComparer.OrdinalIgnoreCase);
            if (symbols.Count == 0) return result;

            var names = symbols.Select((_, i) => $"@s{i}").ToArray();
            string sql = $@"
                SELECT symbol, scan_date, total_score
                FROM scan_results
                WHERE session_id=@sid AND timeframe='1d' AND symbol IN ({string.Join(",", names)})
                ORDER BY symbol, scan_date";
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@sid", sid);
            for (int i = 0; i < symbols.Count; i++) cmd.Parameters.AddWithValue(names[i], symbols[i]);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var sym = rd.GetString(0);
                if (!result.TryGetValue(sym, out var lst)) result[sym] = lst = new List<(string, double)>();
                lst.Add((rd.GetString(1), rd.IsDBNull(2) ? 0 : rd.GetDouble(2)));
            }
            return result;
        }

        private static async Task<(string Symbol, double? Close, double? Ema50, double? Ema200,
            string Regime, int BreadthTotal, int BreadthBullish, int BreadthBearish, double BreadthPctBullish)?>
            ReadMarketRegimeAsync(SqliteConnection c, int sid, string scanDate, CancellationToken ct)
        {
            const string sql = @"
                SELECT benchmark_symbol, benchmark_close, ema50, ema200, regime,
                       breadth_total, breadth_bullish, breadth_bearish, breadth_pct_bullish
                FROM market_regime WHERE session_id=@sid AND scan_date=@dt";
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@dt", scanDate);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            return (rd.GetString(0), GetD(rd, 1), GetD(rd, 2), GetD(rd, 3), rd.IsDBNull(4) ? "NEUTRAL" : rd.GetString(4),
                    GetI(rd, 5) ?? 0, GetI(rd, 6) ?? 0, GetI(rd, 7) ?? 0, GetD(rd, 8) ?? 0);
        }

        private static async Task<List<(string Symbol, string Timeframe, string Reason)>> ReadDataQualitySummaryAsync(
            SqliteConnection c, int sid, CancellationToken ct)
        {
            const string sql = "SELECT symbol, timeframe, reason FROM data_quality_issues WHERE session_id=@sid";
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@sid", sid);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var list = new List<(string, string, string)>();
            while (await rd.ReadAsync(ct))
                list.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2)));
            return list;
        }

        private static async Task<List<(string tf, int total, int bulls, int bears, double avg)>> ReadTfBreakdownAsync(
            SqliteConnection c, int sid, string scanDate, CancellationToken ct)
        {
            const string sql = @"
                SELECT timeframe, COUNT(*) total,
                       SUM(CASE WHEN total_score > 0 THEN 1 ELSE 0 END) bulls,
                       SUM(CASE WHEN total_score < 0 THEN 1 ELSE 0 END) bears,
                       AVG(total_score) avg_score
                FROM scan_results
                WHERE session_id=@sid AND scan_date=@dt
                GROUP BY timeframe
                ORDER BY CASE timeframe WHEN '1wk' THEN 1 WHEN '1d' THEN 2
                         WHEN '1h' THEN 3 WHEN '15m' THEN 4 WHEN '5m' THEN 5 ELSE 6 END";
            await using var cmd = new SqliteCommand(sql, c);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@dt", scanDate);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var list = new List<(string, int, int, int, double)>();
            while (await rd.ReadAsync(ct))
                list.Add((rd.GetString(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3),
                          rd.IsDBNull(4) ? 0 : rd.GetDouble(4)));
            return list;
        }
    }

    // =========================================================================
    //  DASHBOARD SERVER
    //  Hosts nse_dashboard.html (static file) + a live /dashboard_data.json
    //  endpoint that queries the SQLite DB fresh on every request. WAL mode lets
    //  this read concurrently while a scan is writing, so the dashboard reflects
    //  the DB in real time — no python export step, no static snapshot.
    // =========================================================================
    internal static class DashboardServer
    {
        public static async Task RunAsync(string host, int port, CancellationToken ct)
        {
            var dbPath = Config.DbPath;
            var dbFull = Path.GetFullPath(dbPath);
            var htmlPath = Path.Combine(Directory.GetCurrentDirectory(), "nse_dashboard.html");

            // Loud, unambiguous startup diagnostics — a wrong working directory or an
            // empty DB should be obvious right here in the console, not something the
            // person has to reverse-engineer from "the dashboard is showing demo data".
            Log.Information("Dashboard working dir : {Dir}", Directory.GetCurrentDirectory());
            Log.Information("Dashboard DB path     : {Db}  (exists: {Exists})", dbFull, File.Exists(dbFull));
            if (!File.Exists(htmlPath))
                Log.Warning("nse_dashboard.html not found at {Path} — the dashboard page itself won't load. " +
                             "Run `dotnet run -- --serve` from the folder that contains nse_dashboard.html.", htmlPath);
            if (!File.Exists(dbFull))
                Log.Warning("Database not found yet at {Path} — run a scan first (`dotnet run`), then --serve " +
                             "again (or just leave --serve running and rescan; it reads live).", dbFull);
            else
            {
                try
                {
                    using var probe = new SqliteConnection($"Data Source={dbFull};Mode=ReadOnly");
                    probe.Open();
                    using var cmd = new SqliteCommand("SELECT COUNT(*) FROM scan_sessions", probe);
                    var sessions = Convert.ToInt64(cmd.ExecuteScalar());
                    Log.Information("Dashboard DB sessions : {N}", sessions);
                    if (sessions == 0)
                        Log.Warning("Database exists but has no scan sessions yet — the dashboard will show demo " +
                                     "data until a scan completes.");
                }
                catch (Exception ex)
                {
                    Log.Warning("Could not probe database at startup: {Msg}", ex.Message);
                }
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args            = Array.Empty<string>(), // don't let ASP.NET Core parse our own CLI flags
                ContentRootPath = Directory.GetCurrentDirectory(),
            });
            builder.Logging.ClearProviders(); // Serilog already owns console/file logging
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

            var app = builder.Build();
            app.Urls.Add($"http://{host}:{port}");
            app.UseCors();

            // NOTE: deliberately NOT using UseStaticFiles over the whole working directory.
            // That would (a) silently shadow the /dashboard_data.json route below with any
            // stale static file of the same name sitting on disk (e.g. left over from the
            // legacy python export script) — the exact kind of bug that makes "live" data
            // silently look like it's stuck on old/demo data — and (b) serve the raw SQLite
            // DB and appsettings.json over unauthenticated HTTP. The dashboard is fully
            // self-contained (inline CSS/JS, one external CDN stylesheet), so it only needs
            // the HTML itself plus the two endpoints below.
            app.MapGet("/", async (HttpContext http) =>
            {
                if (!File.Exists(htmlPath))
                    return Results.Text(
                        "nse_dashboard.html not found next to the running app. " +
                        "Run `dotnet run -- --serve` from the project folder.",
                        "text/plain", statusCode: 404);

                var html = await File.ReadAllTextAsync(htmlPath, http.RequestAborted);
                http.Response.Headers.CacheControl = "no-store"; // always serve the current file, never a cached copy
                return Results.Text(html, "text/html");
            });

            app.MapGet("/dashboard_data.json", async (HttpContext http) =>
            {
                int? sid = http.Request.Query.TryGetValue("session", out var sv) && int.TryParse(sv, out var sidv)
                    ? sidv : null;
                string? dt = http.Request.Query.TryGetValue("date", out var dv) ? dv.ToString() : null;
                try
                {
                    var json = await DashboardDataBuilder.BuildJsonAsync(dbPath, sid, dt, http.RequestAborted);
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Text(json, "application/json");
                }
                catch (Exception ex)
                {
                    // Surface the *actual* reason in both the server log and the HTTP response body —
                    // the dashboard's console will show this, turning "silently shows demo data" into
                    // an actionable message (e.g. "No scan sessions found — run the scanner first").
                    Log.Warning("Dashboard API error: {Msg}", ex.Message);
                    return Results.Problem(ex.Message, statusCode: 503);
                }
            });

            app.MapGet("/api/health", () =>
            {
                int sessions = 0;
                try
                {
                    if (File.Exists(dbFull))
                    {
                        using var probe = new SqliteConnection($"Data Source={dbFull};Mode=ReadOnly");
                        probe.Open();
                        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM scan_sessions", probe);
                        sessions = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                }
                catch { /* best-effort diagnostic only */ }

                return Results.Json(new
                {
                    status        = "ok",
                    dbPath        = dbFull,
                    dbExists      = File.Exists(dbFull),
                    sessions,
                    serverTimeUtc = DateTime.UtcNow.ToString("o"),
                });
            });

            // ── Backtesting dashboard (new — additive only, never touches the routes above) ──
            var backtestHtmlPath = Path.Combine(Directory.GetCurrentDirectory(), "nse_backtest_dashboard.html");
            app.MapGet("/backtest", async (HttpContext http) =>
            {
                if (!File.Exists(backtestHtmlPath))
                    return Results.Text(
                        "nse_backtest_dashboard.html not found next to the running app.",
                        "text/plain", statusCode: 404);

                var html = await File.ReadAllTextAsync(backtestHtmlPath, http.RequestAborted);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Text(html, "text/html");
            });

            app.MapGet("/backtest_data.json", async (HttpContext http) =>
            {
                try
                {
                    http.Response.Headers.CacheControl = "no-store";
                    if (http.Request.Query.TryGetValue("run", out var rv) && int.TryParse(rv, out var runId))
                    {
                        var detail = await BacktestDashboardData.BuildRunDetailJsonAsync(runId);
                        return detail is null
                            ? Results.Problem($"Backtest run #{runId} not found.", statusCode: 404)
                            : Results.Text(detail, "application/json");
                    }
                    var list = await BacktestDashboardData.BuildRunsListJsonAsync();
                    return Results.Text(list, "application/json");
                }
                catch (Exception ex)
                {
                    Log.Warning("Backtest dashboard API error: {Msg}", ex.Message);
                    return Results.Problem(ex.Message, statusCode: 503);
                }
            });

            Log.Information("Dashboard live at   http://{Host}:{Port}/", host, port);
            Log.Information("Live JSON API at    http://{Host}:{Port}/dashboard_data.json", host, port);
            Log.Information("Health check at     http://{Host}:{Port}/api/health", host, port);
            Log.Information("Backtesting page at http://{Host}:{Port}/backtest", host, port);
            Log.Information("Press Ctrl+C to stop.");
            var url = string.Format("http://{0}:{1}/", host, port);
            Log.Information("URL: " + url);
            await app.RunAsync(url);
        }
    }

    // =========================================================================
    //  PROGRAM  — entry point, config loading, CLI argument handling
    // =========================================================================
    internal static class Program
    {
        private static string? GetArgValue(string[] args, string name)
        {
            foreach (var a in args)
                if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return a[(name.Length + 1)..];
            return null;
        }

        public static async Task RunAsync(string[] args)
        {
            // ── appsettings.json overrides ────────────────────────────────────
            var settings = AppSettings.Load();
            Config.ApplySettings(settings);

            // ── Logger ────────────────────────────────────────────────────────
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console(
                    outputTemplate: "  [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code)
                .WriteTo.File("scanner.log",
                    rollingInterval        : RollingInterval.Day,
                    retainedFileCountLimit : 7,
                    outputTemplate         :
                        "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            // ── CLI help ──────────────────────────────────────────────────────
            if (args.Contains("--help") || args.Contains("-h"))
            {
                Console.WriteLine(@"
NSE Momentum Scanner v4.0

USAGE
  NseMomentumScanner [options]

OPTIONS
  --help, -h         Show this help
  --dry-run          Validate config + DB connection, then exit
  --serve            Start the live dashboard web server (no scan). Reads the
                      SQLite DB directly on every request — always reflects
                      the latest scan, including one currently in progress.
  --port=N           Port for --serve (default: appsettings DashboardPort or 5080)
  --host=NAME        Bind host for --serve (default: appsettings DashboardHost
                      or 'localhost'; use 0.0.0.0 to allow LAN access)
  --backtest         Run a historical backtest of the scanner's own buy/sell
                      signals (equivalent to --mode=backtest). Configure via
                      the ""Backtest"" section of appsettings.json.
  --mode=backtest    Same as --backtest.
  --optimize-weights Run a backtest-driven random search over the 6 scoring
                      weights (VCP/Volume/Trend/Supertrend/HMA/ADX) and write
                      the best-performing set to optimized_weights.json. The
                      live scanner (and --serve, --backtest) picks this up
                      automatically next run if UseOptimizedWeights is true
                      (default) and the file isn't stale. Uses the same
                      ""Backtest"" appsettings.json section (universe/date
                      range/costs) as --backtest.
    --iterations=N      Candidates to try, plus 1 baseline run (default: 30)
    --objective=X       sharpe | cagr | calmar (default: sharpe)
    --max-drawdown=N    Disqualify candidates with worse than N% max
                         drawdown (default: 35)
    --min-trades=N      Disqualify candidates with fewer than N trades —
                         guards against a high score from a tiny, lucky
                         sample (default: 15)
    --seed=N            Random seed, for a reproducible search
    --output=PATH       Where to write the winning weights
                         (default: OptimizedWeightsPath in appsettings.json,
                         or optimized_weights.json)

CONFIGURATION
  Create appsettings.json in the working directory to override defaults.
  Example:
  {
    ""ScanHistoryDays"":     30,
    ""MaxConcurrentFetches"": 6,
    ""TopBuyCount"":         30,
    ""TopSellCount"":        20,
    ""DbPath"":              ""NseMomentumScanner.db"",
    ""CsvBaseDir"":          ""data"",
    ""SkipWeekends"":        true,
    ""SkipHolidays"":        true,
    ""DashboardPort"":       5080,
    ""DashboardHost"":       ""localhost"",
    ""CacheEnabled"":        true,
    ""CacheBaseDir"":        "".cache/yahoo"",
    ""SlowTfCacheHours"":    6,
    ""MarketBenchmarkSymbol"": ""^NSEI"",
    ""ReferenceDataCsvPath"":  ""data/reference.csv"",
    ""UseOptimizedWeights"":   true,
    ""ShockerTopCount"":        25,
    ""ConsecutiveDailyMinStreak"":  3,
    ""ConsecutiveWeeklyMinStreak"": 2
  }
  See README.md for the full v4.0/v4.1/v4.2 settings reference (market regime,
  relative strength, liquidity/extension gates, sector rotation, fundamental
  screen, earnings blackout, the weight-optimizer feedback loop, and the
  price/volume shockers + consecutive-mover dashboard panels).
");
                return;
            }

            // ── Fail-fast config validation ──────────────────────────────────────
            // Runs for every mode below (scan, --dry-run, --serve, --backtest,
            // --optimize-weights) — a malformed appsettings.json should fail here with one
            // clear message, not three layers deep as a confusing crash or a silent
            // degenerate scan. --help intentionally skips this (see above) so a broken config
            // file never blocks someone from just reading the usage text.
            try { Config.Validate(); }
            catch (Exception ex)
            {
                Log.Fatal(ex.Message);
                await Log.CloseAndFlushAsync();
                Environment.Exit(1);
                return;
            }

            // ── Dry-run ───────────────────────────────────────────────────────
            if (args.Contains("--dry-run"))
            {
                Log.Information("Dry-run: validating configuration and database…");
                try
                {
                    using var db = new SqliteRepository();
                    int id = db.InsertSession("DryRun", 0);
                    db.PruneOldSessions();
                    Log.Information("Database OK — session #{Id} created and pruned", id);
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Dry-run failed");
                    Environment.Exit(1);
                }
                Log.Information("Dry-run passed. Config: {@Settings}", settings);
                return;
            }

            // ── v4.1: apply a backtest-optimized weight set if one exists and is fresh ──────
            // Placed after --help/--dry-run (which don't need it) but before every mode that
            // actually scores stocks — scan, --serve (dashboard reads DB rows scored under
            // whichever weights were active), and --backtest (an ad-hoc backtest should reflect
            // what the live scanner would actually do right now, optimized weights included).
            Config.TryLoadOptimizedWeightsFromDisk();

            // ── Serve the live dashboard (no scan) ──────────────────────────────
            if (args.Contains("--serve"))
            {
                string host = GetArgValue(args, "--host") ?? Config.DashboardHost;
                int    port = int.TryParse(GetArgValue(args, "--port"), out var pp) ? pp : Config.DashboardPort;

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                try
                {
                    await DashboardServer.RunAsync(host, port, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Dashboard server stopped.");
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Dashboard server failed to start");
                    Environment.Exit(1);
                }
                finally
                {
                    await Log.CloseAndFlushAsync();
                }
                return;
            }

            // ── Backtest mode (new — historical simulation of the scanner's own signals) ──
            if (args.Contains("--backtest") ||
                string.Equals(GetArgValue(args, "--mode"), "backtest", StringComparison.OrdinalIgnoreCase))
            {
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                    Log.Warning("Backtest cancelled by user (Ctrl+C).");
                };
                try
                {
                    await BacktestRunner.RunAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("Backtest cancelled.");
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Backtest failed");
                    Environment.Exit(1);
                }
                finally
                {
                    await Log.CloseAndFlushAsync();
                }
                return;
            }

            // ── Weight optimizer mode (v4.1 — backtest-driven search over scoring weights) ──
            if (args.Contains("--optimize-weights"))
            {
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                    Log.Warning("Weight optimizer cancelled by user (Ctrl+C).");
                };

                int iterations = int.TryParse(GetArgValue(args, "--iterations"), out var itv) ? itv : 30;
                string objective = GetArgValue(args, "--objective") ?? "sharpe";
                double maxDrawdown = double.TryParse(GetArgValue(args, "--max-drawdown"), out var mdv) ? mdv : 35.0;
                int minTrades = int.TryParse(GetArgValue(args, "--min-trades"), out var mtv) ? mtv : 15;
                int? seed = int.TryParse(GetArgValue(args, "--seed"), out var sv) ? sv : null;
                string outputPath = GetArgValue(args, "--output") ?? Config.OptimizedWeightsPath;

                try
                {
                    await NseMomentumScanner.Backtesting.WeightOptimizer.RunAsync(
                        iterations, objective, maxDrawdown, minTrades, seed, outputPath, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("Weight optimizer cancelled.");
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Weight optimizer failed");
                    Environment.Exit(1);
                }
                finally
                {
                    await Log.CloseAndFlushAsync();
                }
                return;
            }

            // ── Normal scan ───────────────────────────────────────────────────
            try
            {
                using var cts    = new CancellationTokenSource();
                using var engine = new ScanEngine();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                    Log.Warning("Scan cancelled by user (Ctrl+C).");
                };

                await engine.RunAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Scan cancelled.");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Unhandled fatal error");
                Environment.Exit(1);
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }
} // end namespace
