// =============================================================================
//  NSE Momentum Stock Scanner  ·  Multi-Timeframe  ·  Production v3.3  ·  .NET 8
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

using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // ── Timeframes (fixed — not overridable via JSON) ─────────────────────
        // NOTE: weekly's lookback used to be 730 days (~104 weekly bars) while requiring
        // 175 bars to ever produce a score — meaning the weekly timeframe silently scored
        // ZERO results in every prior scan. Fixed by giving weekly/monthly their own,
        // correctly-sized minimum-bar thresholds (see MinBarsWeekly/MinBarsMonthly below)
        // instead of reusing the daily+VCP threshold, and by giving both a long enough
        // lookback window to actually reach that threshold.
        public static readonly (YahooInterval Interval, string Label, double Weight,
                                 int LookbackDays, bool VcpEnabled)[] ScanTimeframes =
        {
            (YahooInterval.OneMonth,       "1mo",  0.10, 3650, false), // ~10y → up to ~120 monthly bars
            (YahooInterval.OneWeek,        "1wk",  0.15, 1095, false), // ~3y  → up to ~156 weekly bars
            (YahooInterval.OneDay,         "1d",   0.30, 365,  true ),
            (YahooInterval.OneHour,        "1h",   0.20, 60,   false),
            (YahooInterval.FifteenMinutes, "15m",  0.15, 55,   false),
            (YahooInterval.FiveMinutes,    "5m",   0.10, 55,   false),
        };

        public static int    ScanHistoryDays       => _s.ScanHistoryDays      ?? 30;
        public static int    MaxConcurrentFetches  => _s.MaxConcurrentFetches ?? 2;
        public static int    FetchTimeoutSeconds   => _s.FetchTimeoutSeconds  ?? 30;
        public static int    MaxRetries            => _s.MaxRetries           ?? 3;
        public static int    RetryBaseDelayMs      => _s.RetryBaseDelayMs     ?? 800;
        public static int    KeepSessionCount      => _s.KeepSessionCount     ?? 10;
        public static string DbPath                => _s.DbPath               ?? "NseMomentumScanner.db";
        public static string CsvBaseDir            => _s.CsvBaseDir           ?? "data";
        public static bool   SkipWeekends          => _s.SkipWeekends         ?? true;
        public static bool   SkipHolidays          => _s.SkipHolidays         ?? true;
        public static int    TopBuyCount           => _s.TopBuyCount          ?? 30;
        public static int    TopSellCount          => _s.TopSellCount         ?? 20;
        public static int    DashboardPort         => _s.DashboardPort        ?? 5080;
        public static string DashboardHost         => _s.DashboardHost        ?? "localhost";
        public static bool   CacheEnabled          => _s.CacheEnabled         ?? true;
        public static string CacheBaseDir          => _s.CacheBaseDir         ?? Path.Combine(".cache", "yahoo");
        public static double SlowTfCacheHours       => _s.SlowTfCacheHours    ?? 6.0;

        // ── Indicator periods (compile-time) ──────────────────────────────────
        public static int    SupertrendPeriod      => _s.SupertrendPeriod     ?? 9;
        public static double SupertrendMultiplier  => _s.SupertrendMultiplier ?? 1.6;
        public const  int    FastHmaPeriod         = 9;
        public const  int    SlowHmaPeriod         = 21;
        public const  int    AdxPeriod             = 14;
        public const  int    VolumeSmaPeriod       = 20;
        public const  int    EmaFastPeriod         = 9;
        public const  int    EmaSlowPeriod         = 63;

        // ── VCP parameters ────────────────────────────────────────────────────
        public const int    VcpLookbackDays       = 100;
        public const int    VcpSwingSensitivity   = 3;
        public const int    VcpMinSwings          = 2;
        public const double VcpVolatilityRatio    = 0.80;
        public const double VcpVolumeDropRatio    = 0.70;

        // ── Scoring weights ───────────────────────────────────────────────────
        public const int    SupertrendWeight      = 30;
        public const int    HmaCrossoverWeight    = 15;
        public const int    AdxWeight             = 15;
        public const int    VcpWeight             = 15;
        public const int    VolumeWeight          = 15;
        public const int    TrendAlignmentWeight  = 10;

        // ── Thresholds ────────────────────────────────────────────────────────
        public const double AdxStrongThreshold    = 21.0;
        public const double VolumeSurgeRatio      = 1.5;
        public const double VolumeLowRatio        = 0.5;

        // ── Minimum bars for scoring (warmup) ─────────────────────────────────
        // Daily needs EMA63 + VCP(100 bars) + buffer. Weekly/monthly don't run VCP, so they
        // only need to clear EMA63 with a reasonable buffer — reusing the daily threshold here
        // was the root cause of weekly scans silently returning zero rows (175 weekly bars
        // needs ~3.4 years of history; the old lookback window only fetched ~2 years).
        public const int MinBarsDaily             = 175; // EMA63 + VCP + buffer
        public const int MinBarsWeekly            = 80;  // EMA63(63) + buffer, no VCP
        public const int MinBarsMonthly           = 70;  // EMA63(63) + buffer, no VCP
        public const int MinBarsIntraday          = 50;

        // ── Data-quality gates ────────────────────────────────────────────────
        public const int    MinQuotesRequired       = 30;
        public const double MaxAllowedGapPct        = 25.0; // >25% single-bar move = suspect
        public const double MaxStaleDataDays        = 7.0;  // data older than 7d is stale
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
        public int      Rank                { get; set; }
        public decimal  LastClose           { get; set; }
        public string   DataSourceTf        { get; set; } = string.Empty;
        public PriceLevels       Levels     { get; set; } = new();
        public SupertrendMetrics StMetrics  { get; set; } = new();
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
    // =========================================================================
    internal sealed class YahooDataFetcher : IDisposable
    {
        private static readonly string[] _hosts =
            { "query1.finance.yahoo.com", "query2.finance.yahoo.com" };

        private readonly HttpClient _http;
        // Shared RNG — safe for concurrent reads with Random.Shared
        private static readonly Random _rng = Random.Shared;

        public YahooDataFetcher()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Config.FetchTimeoutSeconds)
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
            int totalAttempts = Config.MaxRetries + 2; // extra budget for 429 retries

            for (int attempt = 0; attempt < totalAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                string host = _hosts[attempt % _hosts.Length];
                string url  = $"https://{host}/v8/finance/chart/{escaped}" +
                              $"?period1={period1}&period2={period2}&interval={intStr}" +
                              $"&events=history&includeAdjustedClose=true";
                try
                {
                    using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                    // Handle rate limiting specifically
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        int waitSec = (int)(resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60);
                        waitSec = Math.Clamp(waitSec, 30, 300);
                        Log.Warning("429 for {Ticker} — waiting {Sec}s then retrying", ticker, waitSec);
                        await Task.Delay(TimeSpan.FromSeconds(waitSec), ct);
                        continue; // retry without decrementing effective budget
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        lastEx = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                        await ApplyBackoff(attempt, ct);
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
                }
            }

            Log.Warning("Gave up on {Ticker} ({Interval}) after {N} attempts: {Msg}",
                ticker, intStr, totalAttempts, lastEx?.Message);
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
            DateTime         scoreEnd)
        {
            var results = new List<ScanResult>();
            if (quotes.Count < Config.MinQuotesRequired) return results;

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
        public static List<AggregatedResult> Aggregate(
            IReadOnlyList<ScanResult> allResults,
            DateTime scanDate)
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

            foreach (var (sym, grp) in dayResults)
            {
                double wtScore = 0, totalWt = 0;
                int bullish = 0, bearish = 0;
                double mo = 0, wk = 0, d1 = 0, h1 = 0, m15 = 0, m5 = 0;
                ScanResult? best = null;

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
                    if (r.Timeframe == "1d") best = r; else best ??= r;
                }

                double composite = totalWt > 0 ? Math.Round(wtScore / totalWt, 2) : 0;
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
                    Levels         = best?.Levels    ?? new(),
                    StMetrics      = best?.StMetrics ?? new(),
                });
            }

            // Sort and tag
            aggList.Sort((a, b) => b.AggregateScore.CompareTo(a.AggregateScore));

            int buyCount = 0, sellCount = 0;
            for (int r = 0; r < aggList.Count; r++)
            {
                aggList[r].Rank = r + 1;
                if (buyCount  < Config.TopBuyCount  && aggList[r].AggregateScore >= 20)
                { aggList[r].RecommendationType = "BUY";           buyCount++;  }
                else if (sellCount < Config.TopSellCount)
                {
                    // Scan from bottom for sells
                }
            }
            // Tag sells from the bottom
            sellCount = 0;
            for (int r = aggList.Count - 1; r >= 0 && sellCount < Config.TopSellCount; r--)
            {
                if (aggList[r].AggregateScore <= -20)
                { aggList[r].RecommendationType = "IMMEDIATE_SELL"; sellCount++; }
            }

            return aggList;
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
                scored_tickers INTEGER NOT NULL DEFAULT 0
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
                UNIQUE (session_id, symbol, scan_date)
            );

            CREATE INDEX IF NOT EXISTS idx_sr_sym_date  ON scan_results(symbol, scan_date);
            CREATE INDEX IF NOT EXISTS idx_sr_date_tf   ON scan_results(scan_date, timeframe);
            CREATE INDEX IF NOT EXISTS idx_sr_score     ON scan_results(scan_date, total_score DESC);
            CREATE INDEX IF NOT EXISTS idx_agg_date_rec ON aggregated_results(scan_date, recommendation);
            ");
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
                 st_hh,st_hh_ago,st_hl,st_hl_ago,st_lh,st_lh_ago,st_ll,st_ll_ago)
                VALUES(@sid,@sym,@dt,@tf,@rk,
                 @ts,@sts,@hms,@ads,@vcs,@vls,@trs,
                 @ib,@is,@lc,@lo,@lh,@ll,@lv,
                 @sv,@bs,@hf,@hs,@ef,@es,
                 @av,@vr,@ax,@pd,@md,@vcp,
                 @wh,@wl,@mh,@ml,@qh,@ql,@yh,@yl,@p2h,@p2l,@pmh,@pwh,
                 @stb,@std,@stf,@stc,@shh,@sha,@shl,@shla,@slh,@slha,@sll,@slla)";

                using var tx  = _conn.BeginTransaction();
                using var cmd = new SqliteCommand(sql, _conn, tx);
                var p = cmd.Parameters;
                AddParams(p, "@sid,@sym,@dt,@tf,@rk,@ts,@sts,@hms,@ads,@vcs,@vls,@trs," +
                    "@ib,@is,@lc,@lo,@lh,@ll,@lv,@sv,@bs,@hf,@hs,@ef,@es,@av,@vr,@ax,@pd,@md,@vcp," +
                    "@wh,@wl,@mh,@ml,@qh,@ql,@yh,@yl,@p2h,@p2l,@pmh,@pwh," +
                    "@stb,@std,@stf,@stc,@shh,@sha,@shl,@shla,@slh,@slha,@sll,@slla");

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
                 st_bullish,st_days,st_flip_date,st_cal_days,st_hh,st_hh_ago,st_ll,st_ll_ago)
                VALUES(@sid,@sym,@dt,@as,@mos,@ws,@ds,@hs,@ms,@fs,@bt,@be,@rec,@rk,@lc,@tf,
                 @wh,@wl,@mh,@ml,@qh,@ql,@yh,@yl,@p2h,@p2l,
                 @stb,@std,@stf,@stc,@shh,@sha,@sll,@sla)";

                using var tx  = _conn.BeginTransaction();
                using var cmd = new SqliteCommand(sql, _conn, tx);
                var p = cmd.Parameters;
                AddParams(p, "@sid,@sym,@dt,@as,@mos,@ws,@ds,@hs,@ms,@fs,@bt,@be,@rec,@rk,@lc,@tf," +
                    "@wh,@wl,@mh,@ml,@qh,@ql,@yh,@yl,@p2h,@p2l,@stb,@std,@stf,@stc,@shh,@sha,@sll,@sla");

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
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
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
  ║   NSE MOMENTUM SCANNER  ·  Multi-Timeframe  ·  v3.3  ·  .NET 8      ║
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

            // All last-day results kept for aggregation; full history streamed to DB
            var lastDayResults = new ConcurrentBag<ScanResult>();
            var allForCsv      = new ConcurrentBag<ScanResult>(); // for per-date CSV

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
                                continue;
                            }

                            var tfResults = TickerScorer.Score(
                                symbol, interval, tfLabel, vcpEnabled,
                                quotes, scoreStart, scoreEnd);

                            if (tfResults.Count == 0) continue;

                            // All results → CSV accumulator
                            foreach (var r in tfResults) allForCsv.Add(r);

                            // Last day only → aggregation accumulator
                            var last = tfResults.MaxBy(r => r.ScanDate);
                            if (last != null) lastDayResults.Add(last);

                            tickerResults.AddRange(tfResults);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Isolate failures (thin history on new listings, indicator edge cases,
                            // transient bad data) to this ticker+timeframe. Never let a single symbol
                            // take down the whole Parallel.ForEachAsync batch.
                            Log.Warning("Scoring error {Sym} [{TF}]: {Msg}", symbol, tfLabel, ex.Message);
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
            Log.Information("Writing CSVs…");
            var csvList   = allForCsv.ToList();
            var allDates  = csvList.Select(r => r.ScanDate.Date).Distinct().OrderBy(d => d).ToList();

            foreach (var date in allDates)
            {
                foreach (var (_, tfLabel, _, _, _) in Config.ScanTimeframes)
                {
                    var subset = csvList
                        .Where(r => r.ScanDate.Date == date && r.Timeframe == tfLabel)
                        .ToList();
                    if (subset.Count > 0)
                        CsvExporter.WriteScanResults(subset, date, tfLabel);
                }
            }

            // ── Aggregate last day ────────────────────────────────────────────
            var lastDay     = allDates.Count > 0 ? allDates[^1] : today;
            var allLastDay  = lastDayResults.ToList();
            var aggResults  = MultiTfAggregator.Aggregate(allLastDay, lastDay);

            if (aggResults.Count > 0)
            {
                try { await _db.BulkInsertAggregatedAsync(sessionId, aggResults); }
                catch (Exception ex) { Log.Warning("DB agg insert error: {Msg}", ex.Message); }
                CsvExporter.WriteAggregated(aggResults, lastDay);
            }

            // ── Prune old sessions ────────────────────────────────────────────
            _db.PruneOldSessions();

            // ── Console output — last day only ────────────────────────────────
            Console.WriteLine();
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
        private sealed record SessionRow(string RunTimestamp, string Universe, int Total, int Scored);

        private sealed record AggRow(
            string Symbol, string? Rec, int? Rank, double? Close, double? AggScore,
            double? MoSc, double? WSc, double? DSc, double? HSc, double? M15Sc, double? M5Sc,
            int? BullTf, int? BearTf,
            double? YH, double? YL, double? QH, double? QL, double? MH, double? ML,
            double? WH, double? WL, double? P52H, double? P52L,
            bool StBull, int? StDays, string? StFlip, int? StCalDays,
            double? StHH, int? StHHAgo, double? StLL, int? StLLAgo);

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

            var root = new JsonObject
            {
                ["generated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["scanDate"]  = scanDate,
                ["session"]   = new JsonObject
                {
                    ["id"]       = sid,
                    ["runDate"]  = session.RunTimestamp.Length >= 10 ? session.RunTimestamp[..10] : session.RunTimestamp,
                    ["total"]    = session.Total,
                    ["scored"]   = session.Scored,
                    ["universe"] = session.Universe,
                },
                ["stocks"]      = stocks,
                ["tfBreakdown"] = tfOut,
            };

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
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
                ["sector"]   = "—", // not captured by the scanner — left as a placeholder, not fabricated
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
                ["history"] = histArr,
            };
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
                "SELECT run_timestamp, universe, total_tickers, scored_tickers " +
                "FROM scan_sessions WHERE session_id=@sid", c);
            cmd.Parameters.AddWithValue("@sid", sid);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            return new SessionRow(rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3));
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
                       st_bullish, st_days, st_flip_date, st_cal_days, st_hh, st_hh_ago, st_ll, st_ll_ago
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
                    GetD(rd, 27), GetI(rd, 28), GetD(rd, 29), GetI(rd, 30)));
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

            Log.Information("Dashboard live at   http://{Host}:{Port}/", host, port);
            Log.Information("Live JSON API at    http://{Host}:{Port}/dashboard_data.json", host, port);
            Log.Information("Health check at     http://{Host}:{Port}/api/health", host, port);
            Log.Information("Press Ctrl+C to stop.");

            await app.RunAsync(ct);
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
NSE Momentum Scanner v3.3

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

CONFIGURATION
  Create appsettings.json in the working directory to override defaults.
  Example:
  {
    ""ScanHistoryDays"":     30,
    ""MaxConcurrentFetches"": 2,
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
    ""SlowTfCacheHours"":    6
  }
");
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
