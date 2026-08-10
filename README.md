# NSE Momentum Stock Scanner — Production v4.1

## What's new in v4.1 (sector rotation, fundamental screen, earnings blackout, weight optimizer)

Closes out the remaining Tier-2 items from the v4.0 architecture review. All four are
designed to degrade safely: missing data never blocks a BUY, and every new gate defaults
to whatever behavior keeps risk lowest.

| Area | v4.0 behavior | v4.1 behavior |
|------|---------------|---------------|
| **Sector rotation** | `sector` was a hardcoded `"—"` placeholder on the dashboard | 532-symbol best-effort static sector map (`SectorMap`, see its header comment on confidence level) + optional `data/reference.csv` override. Computes a cross-sectional sector-rank percentile each scan (which sectors are actually leading today) — informational by default; `EnableSectorGate` turns it into an actual BUY requirement |
| **Fundamental screen** | None | CSV-driven (`data/reference.csv`): PE, ROE, debt/equity, EPS growth, promoter pledge. **Off by default** — a symbol with no data always passes, so turning it on with an empty CSV is a safe no-op until populated |
| **Earnings blackout** | None | Same CSV's `next_earnings_date`. **On by default** (safe default: can only narrow the BUY list, never widen it, and only fires when a date is actually known) — excludes BUY tags within `EarningsBlackoutDays` (default 3) of a known earnings date |
| **Weight feedback loop** | Backtest engine existed but never fed results back into live scoring weights (`VcpWeight` etc. were hardcoded consts) | `--optimize-weights` runs a random search over the 6 weights, backtesting each with the *same* `BacktestEngine`/`PerformanceAnalyzer` as `--backtest`, and writes the winner to `optimized_weights.json`. The live scanner (and `--serve`/`--backtest`) loads it automatically next run if fresher than `OptimizedWeightsMaxAgeDays` (default 120) |

**Why CSV-driven, not another live API call:** fundamentals and earnings dates would
normally come from a live data source, but no such endpoint is reachable from the
environment this was built in, and the scanner already manages Yahoo rate-limit risk
carefully (`MaxConcurrentFetches`, retry/backoff, disk cache). Doubling that traffic for
fundamentals that are often missing/unreliable for small/mid caps on Yahoo anyway was a
bad trade. Point `data/reference.csv` at your own vendor export, a screener.in/Tijori
download, or maintain it by hand for the names you actually care about — see
`data/reference.csv` for the exact format. **The built-in sector map is a best-effort
seed classification, not verified NSE/vendor data** — verify before relying on it for
capital allocation, and use the CSV override to correct anything wrong.

**Weight optimizer usage:**
```bash
NseMomentumScanner --optimize-weights --iterations=30 --objective=sharpe
```
Only the first iteration re-fetches historical data (the rest reuse the backtest engine's
own disk cache), so iteration count costs mostly CPU time, not additional network/rate-limit
risk. Candidates with too few trades (`--min-trades`, default 15) or too much drawdown
(`--max-drawdown`, default 35%) are disqualified rather than allowed to win on a fluke. If
the baseline (current defaults) wins the search, no file is written — the search isn't
forced to "find" an improvement that isn't there.

**Also added:** full DB schema for all of the above (`sector_rotation` table,
`aggregated_results` sector/fundamental/earnings columns, `scan_sessions.weight_source`
audit column), migrated in place via the same idempotent `MigrateSchema()` as v4.0, plus
dashboard updates (Sector Bias panel now shows real data, `EARN` badge, Sector &
Fundamentals section in the stock detail modal, "Weights: optimized/default" indicator in
the regime banner).

---

## What's new in v4.0 (Principal Quant review pass)

A strategy-level review identified that the scanner scored every stock in isolation —
no market context, no relative strength, no coherence check across timeframes, no
liquidity/overextension floor. v4.0 addresses the five highest-impact gaps:

| Area | v3.x behavior | v4.0 behavior |
|------|---------------|---------------|
| **Market regime** | None — BUY tags generated regardless of the broader market | Fetches Nifty 50 once/scan, classifies BULLISH/NEUTRAL/BEARISH off EMA50/EMA200. In BEARISH or weak-breadth markets, the BUY score threshold rises (+15 pts, configurable) and the BUY count shrinks to ~1/3 |
| **Relative Strength** | None — a stock could rank #1 while lagging the index | IBD-style RS: blends 3/6/9/12-month returns vs Nifty (recent quarter double-weighted) into a 1–99 cross-sectional percentile. BUY requires RS ≥ 70 |
| **Timeframe coherence** | `BullishTfCount`/`BearishTfCount` computed but unused — a single strong TF could drag the composite score over the line | BUY requires ≥4/6 bullish timeframes **and** a non-negative weekly score; SELL requires the bearish mirror |
| **Liquidity** | No floor — thin/penny names could be tagged BUY | BUY requires ≥₹5cr/day avg turnover and ≥₹20 price (both configurable) |
| **Overextension** | No check — a stock 40% above its 50-EMA scored the same as one at a sane entry | BUY requires ≤25% extension above the 50-EMA (configurable); flagged `EXTENDED` on the dashboard otherwise |

All five gates are configurable in `appsettings.json` under the `v4.0` block (benchmark
symbol, RS/breadth/liquidity/extension thresholds, ATR stop parameters). None of this
changes the underlying six-signal scoring model (Supertrend/HMA/ADX/VCP/Volume/Trend) —
it changes *which* high-scoring stocks actually get tagged BUY, and adds a `GateReason`
so a stock that scored like a BUY but got blocked shows *why* on the dashboard's new
"Near-Miss" panel instead of silently disappearing.

**Also added:**
- ATR-based suggested stop-loss/target on every live BUY signal (surfaced in the dashboard
  table and stock detail modal), using the same ATR-multiple approach the backtest engine's
  `PositionSizer`/`ExitEvaluator` already use historically.
- Data-quality tracking: tickers skipped (thin history, stale data, fetch errors) are now
  persisted and summarized on the dashboard instead of only appearing in Debug logs.
- Dashboard: market-regime banner, RS Rank column, Illiquid/Extended badges, suggested
  stop/target column, a "Near-Miss" panel, and a Data Quality panel.
- Removed ~90 lines of dead, commented-out duplicate `Config` class.

---

## Contents

| File | Purpose |
|------|---------|
| `Program.cs` | C# .NET 8 scanner + live dashboard server — multi-timeframe, SQLite, 30-day history |
| `NseMomentumScanner.csproj` | .NET 8 project file with all NuGet packages + ASP.NET Core framework reference |
| `appsettings.json` | Runtime configuration overrides |
| `nse_dashboard.html` | Self-contained HTML trading dashboard (open directly, or serve live — see below) |
| `export_dashboard_data.py` | Optional: Python 3 script for a one-off static JSON snapshot (offline use) |

---

## Quick Start

### 1. Run the Scanner

```bash
# Restore packages and run
dotnet restore
dotnet run
```

The scanner will:
- Scan Nifty 200 + SMID + New Listings (~490 tickers)
- Score last 30 days across 6 timeframes: 1mo · 1wk · 1d · 1h · 15m · 5m
- Save everything to `NseMomentumScanner.db` (SQLite)
- Write day-wise CSVs to `data/` folder
- Display last-day aggregated results on console

Optional flags:
```bash
dotnet run -- --help        # show all options
dotnet run -- --dry-run     # validate DB + config, then exit
```

### 2. View the Dashboard — Live, Reading Directly From the DB

```bash
dotnet run -- --serve                 # http://localhost:5080
dotnet run -- --serve --port=8080     # custom port
dotnet run -- --serve --host=0.0.0.0  # allow access from other devices on your LAN
```

This starts a small local web server that:
- Serves `nse_dashboard.html` at `/`
- Serves `/dashboard_data.json` **built fresh from the SQLite DB on every request** — no
  export step, no stale snapshot. It's safe to run `--serve` at the same time as a scan is
  in progress (SQLite WAL mode allows concurrent reads while the scanner writes).
- The dashboard auto-refreshes every 30 seconds and shows a live "updated HH:MM:SS" indicator
  in the header so you always know how fresh what you're looking at is.

You generally don't need `export_dashboard_data.py` or a static `dashboard_data.json` anymore —
that script is kept only as an optional way to produce a one-off, fully offline snapshot
(e.g. to email someone a fixed point-in-time view). Without `--serve` and without a
`dashboard_data.json` file present, opening `nse_dashboard.html` directly falls back to
built-in demo data so you can still explore the UI.

---

## Configuration (`appsettings.json`)

```json
{
  "ScanHistoryDays":       30,
  "MaxConcurrentFetches":   2,
  "TopBuyCount":           30,
  "TopSellCount":          20,
  "KeepSessionCount":      10,
  "DbPath":  "NseMomentumScanner.db",
  "CsvBaseDir": "data",
  "SkipWeekends": true,
  "SkipHolidays": true,
  "DashboardPort": 5080,
  "DashboardHost": "localhost"
}
```

---

## NuGet Packages Required

```xml
Microsoft.Data.Sqlite       8.*   (SQLite — Microsoft-official)
Skender.Stock.Indicators    2.*   (technical indicators)
Serilog                     3.*
Serilog.Sinks.Console       4.*
Serilog.Sinks.File          5.*
CsvHelper                  33.*
```

---

## Output Structure

```
NseMomentumScanner.db          ← full SQLite database
scanner.log                    ← rolling daily log (kept 7 days)
data/
  scan_YYYY-MM-DD_1d.csv       ← daily TF results per day
  scan_YYYY-MM-DD_1h.csv       ← hourly TF results per day
  scan_YYYY-MM-DD_15m.csv
  scan_YYYY-MM-DD_5m.csv
  scan_YYYY-MM-DD_1wk.csv
  aggregated_YYYY-MM-DD.csv    ← cross-TF composite scores + recommendations
dashboard_data.json            ← generated by export_dashboard_data.py
```

---

## SQLite Tables

| Table | Contents |
|-------|---------|
| `scan_sessions` | Run metadata (timestamp, universe, counts) |
| `scan_results` | Every ticker × timeframe × date score + all 50+ indicator columns |
| `aggregated_results` | Cross-TF composite score, price levels, supertrend metrics, BUY/SELL tag |

---

## Dashboard Panels

| Panel | Shows |
|-------|-------|
| Dashboard | Top-8 buys/sells, score histogram, TF alignment, sector bias |
| Buy Signals | Sortable table: 30 buy candidates with all metrics |
| Sell Signals | Sortable table: 20 immediate-sell candidates |
| Score Matrix | Color-coded grid (every stock × every timeframe) |
| Price Levels | 52-week · Quarterly · Monthly · Weekly H/L with % distances |
| Supertrend | Days in trend · Last flip · HH / HL / LH / LL + days-ago |

Click any row for a full stock detail modal with score breakdown,
range meters, supertrend analysis, and links to NSE + Yahoo charts.

---

## Production Features (v3.0 vs v2.0)

- Fixed M&M/J&KBANK URL encoding (was double-encoding `&`)
- Fixed supertrend flip window off-by-one error
- Fixed `GetSma` compile error — now manual O(n) rolling average
- HTTP 429 / Retry-After handling with full-jitter exponential backoff
- NSE market calendar (weekends + national holidays)
- Data quality validator (stale, sparse, OHLC integrity)
- `appsettings.json` runtime config
- SQLite PRAGMA: `foreign_keys`, `temp_store=MEMORY`, `mmap_size=256MB`
- Session pruning — keeps last N sessions, auto-VACUUMs
- Live progress bar (cursor-repositioned, no console flood)
- `--help` and `--dry-run` CLI flags
- Zero `List.GetRange` allocations in hot scoring loop

## Production Readiness Changes (v3.1)

- **Fixed a DB-write bug that would have thrown on the first insert.** Every SQL parameter
  (including text columns like `symbol`, `scan_date`, `timeframe`) was pre-declared as
  `SqliteType.Real`, forcing a numeric-conversion bind. Parameter type is now inferred per row.
- **Fixed a single-ticker failure aborting the entire scan.** A thin-history new listing or an
  indicator edge case could throw inside `Parallel.ForEachAsync` and cancel the whole run —
  losing the CSV export, aggregation, and console report for every other ticker already scored.
  Failures are now caught and logged per ticker+timeframe; the scan keeps going.
- **Fixed silent split/bonus corruption of every indicator.** The code asked Yahoo for
  `includeAdjustedClose=true` but never used it — a stock split showed up as a 50%+ single-bar
  "crash" in raw OHLC, which would trigger false Supertrend flips, false breakdown scores, and
  distorted price-level percentages around the split date. OHLC is now back-adjusted using the
  adjusted-close ratio when available.
- **Real-time dashboard.** New `--serve` mode hosts the dashboard and a live `/dashboard_data.json`
  endpoint that queries the SQLite DB directly on every request, with the dashboard
  auto-refreshing every 30s. Replaces the need to manually run the Python export step.
- **Fixed a BUY/SELL inconsistency in the dashboard.** The dashboard was independently
  re-deriving "top buy/sell" using different score thresholds than the backend's actual
  `recommendation` tag (which also respects `TopBuyCount`/`TopSellCount`). It now uses the
  backend's tag as the single source of truth.
- Parameterized the session-pruning query instead of string-interpolating the LIMIT value.

## Production Readiness Changes (v3.2)

- **Fixed a bug where weekly scans have been silently returning zero results.** Weekly's
  lookback window (730 days ≈ 104 weekly bars) could never reach the 175-bar minimum the
  code required before it would score a single date — so every prior weekly scan quietly
  produced nothing. Weekly and monthly now have their own, correctly-sized minimum-bar
  thresholds (they don't run VCP, so they never needed the daily+VCP figure of 175), and a
  long enough lookback window to actually clear them (weekly: 3 years, monthly: 10 years).
- **Added monthly (`1mo`) OHLC as a full 6th timeframe** — wired through everywhere the
  other 5 are: scoring, aggregation, the SQLite schema (`aggregated_results.monthly_score`),
  CSV export, the console tables, and the dashboard (columns, TF-alignment chart, detail
  modal). Composite weights were rebalanced: `1mo=0.10, 1wk=0.15, 1d=0.30, 1h=0.20, 15m=0.15,
  5m=0.10` (previously `1wk=0.15, 1d=0.35, 1h=0.25, 15m=0.15, 5m=0.10`).
- **Fixed a staleness-check bug that would have affected weekly/monthly too.** The "is this
  data too old" check used one flat threshold sized for daily bars — a monthly bar dated the
  1st of the month would have been wrongly flagged stale for the rest of the month. The
  threshold now scales with each timeframe's own period.
- **Added a small opt-out disk cache for weekly/monthly fetches** (`CacheEnabled`,
  `SlowTfCacheHours` in `appsettings.json`, default 6h). Those bars barely change within a
  day, and the new monthly timeframe adds ~20% more Yahoo requests overall — this keeps
  same-day re-runs fast without extra API load. Daily/intraday are always fetched live.

## Known Limitations / Suggested Next Steps

- **This environment has no .NET SDK**, so `Program.cs` could not be compiled here. Every
  edit was reviewed by hand (brace/paren balance verified programmatically across the whole
  file, including after the v4.1 additions), but please run `dotnet restore && dotnet build`
  locally before trusting this in production. The dashboard's JavaScript *was* verified —
  `node --check` confirms valid syntax, and a jsdom smoke test exercised every tab, the
  regime banner (including the v4.1 weight-source indicator), the Sector & Fundamentals
  modal section, the EARN/EXT/ILLIQ badges, sorting, search, and the stock detail modal
  end-to-end with zero runtime errors — including the "no fundamentals data" fallback path.
- `WeightOptimizer.cs` (the `--optimize-weights` search) is new code that reuses the
  existing `BacktestEngine`/`PerformanceAnalyzer` rather than reimplementing simulation
  logic, which limits its own surface area for bugs, but it hasn't been run end-to-end here
  either — the same "verify with a real build" caveat applies, doubly so before trusting a
  weight file it produces in production. Start with a small `--iterations` count to sanity-check
  timing before a full run.
- **`SectorMap`'s 532-symbol classification is a best-effort seed built from general
  knowledge, not verified NSE/vendor sector data.** Cross-check anything you plan to act on,
  and use `data/reference.csv`'s `sector` column to correct entries — that always takes
  priority over the built-in map. `EnableSectorGate` defaults to `false` for this reason;
  the sector-rotation panel is informational until you're confident in the classification.
- `data/reference.csv` ships with a single placeholder row (`EXAMPLE.NS`) showing the exact
  column format — it does **not** contain real fundamental data for any actual company. The
  fundamental screen and earnings blackout do nothing until you populate this yourself from
  a data source you trust (see the v4.1 section above for why this is CSV-driven rather than
  another live API call).
- Run `dotnet ef` isn't used here — the new tables (`market_regime`, `data_quality_issues`,
  `sector_rotation`) and the new columns on `scan_results`/`aggregated_results`/`scan_sessions`
  are created via `MigrateSchema()` (idempotent `ALTER TABLE`, safe to run against an existing
  v3.x or v4.0 `NseMomentumScanner.db`).
- The daily-bar timestamp handling (`isIntraday` bars are shifted +5:30 to IST; daily bars are
  left as returned by Yahoo) wasn't changed in this pass — it's worth spot-checking a few
  symbols' daily bar dates against the NSE bhavcopy if exact trading-day alignment matters to you.
- For further modularization (e.g. splitting into the layered Domain/Infrastructure/API project
  structure you've used on other projects), this single-file `Program.cs` can be split along its
  existing region boundaries (Config, Models, Fetcher, Analytics, Database, Dashboard, Reporting)
  without changing behavior — happy to do that as a follow-up if you'd like it as separate projects.

## Backtesting Engine (new)

A full backtesting engine has been added under `Backtesting/` (21 new files, ~3,300 lines) —
**no existing scanner code was rewritten**; the only edits to `Program.cs` are a `using` line,
a new `--backtest` / `--mode=backtest` CLI branch, and two new dashboard routes. Everything else
is new, additive files.

### What it does
Runs the scanner's *own* signal-generation code (`TickerScorer` + `MultiTfAggregator` — completely
unmodified, called directly) over a historical date range instead of "today", then simulates trades
day by day: position sizing, stop-loss/target/trailing-stop/time/Supertrend/HMA exits, brokerage/STT/
GST/stamp-duty/SEBI charges/slippage, a portfolio ledger with max-positions/exposure limits, and a
full trade journal + equity curve + all the metrics from your spec (Sharpe, Sortino, Calmar, MAR,
Ulcer Index, expectancy, drawdown, profit factor, consecutive win/loss streaks, etc.).

### Running it
```bash
dotnet run -- --backtest          # or: dotnet run -- --mode=backtest
```
Configure it entirely via the new `"Backtest"` section in `appsettings.json` (already populated
with working defaults — see the file). Reports land in `backtest_reports/run_<dates>_<timestamp>/`
as `report.html` (self-contained, Chart.js equity/drawdown charts + monthly heat map + full trade
table), `trades.csv`, `equity_curve.csv`, and `report.json`. Every run is also written to six new
SQLite tables in the *same* `NseMomentumScanner.db` (`BacktestRuns`, `BacktestTrades`,
`PortfolioSnapshots`, `EquityCurve`, `PerformanceSummary`, `Parameters`) — the scanner's own tables
are never touched. A new dashboard page at `--serve` → `http://localhost:5080/backtest` lists past
runs and lets you drill into any of them; `nse_dashboard.html` itself was not modified.

### Production-hardening pass
After the first working version, this was reviewed a second time specifically for production
readiness and performance, which turned up and fixed several real issues rather than just cosmetic
ones:
- **Thread-safety fix**: the per-symbol indicator cache (`DailySeriesIndex`) was a plain
  `Dictionary` being written to concurrently from `Parallel.ForEachAsync` across tickers — switched
  to `ConcurrentDictionary`. Under the default concurrency settings this could have caused data
  corruption or an intermittent crash.
- **Crash isolation**: indicator computation (Supertrend/HMA/ATR) for one ticker wasn't wrapped in
  a try/catch inside the parallel fetch loop — a single pathological ticker's data shape could have
  taken down the entire run. It's now isolated and logged per-ticker like every other fetch step.
- **Dedicated backtest-only Yahoo-history cache** (`BacktestDataCache.cs`): the scanner's *existing*
  cache deliberately excludes daily/intraday bars, which is correct for live scanning but meant
  every backtest re-run was re-downloading years of history from Yahoo from scratch — even for
  back-to-back runs while tuning a stop-loss %. This was a real gap against the original spec's
  "reuse cached data" requirement. The new cache is entirely separate from, and never touches, the
  scanner's own `.cache/yahoo/` directory or its cache semantics; configurable via
  `Backtest.CacheEnabled` / `Backtest.CacheHours` (default 12h).
- **Removed duplicate indicator computation**: per-ticker Supertrend/HMA/ATR were being computed via
  the scanner's "compute everything" `IndicatorArrays.Compute()` (which also computes ADX, EMA
  fast/slow, and a volume SMA the backtest never reads) — and then, when `"1d"` is also a scoring
  timeframe (the default), computed *again* inside `TickerScorer.Score`. `SymbolDailySeries` now
  computes only the three arrays the exit rules actually use, directly.
- **Bulk-insert optimization**: SQLite writes for trades/snapshots/equity curve (which can run into
  the thousands of rows for a multi-year run) now reuse one prepared command with bound parameters
  per table instead of allocating a fresh command object per row.
- **Matched the scanner's own production SQLite tuning** (WAL, `synchronous=NORMAL`, cache size,
  mmap) in the new backtest repository, plus a best-effort post-commit WAL checkpoint.
- **Fail-fast config validation**: nonsensical settings (zero/negative capital, zero max positions,
  inverted dates, an empty custom universe) are now caught up front with a clear message instead of
  silently producing an empty or crashing backtest deep inside the engine.
- Minor: removed a wasteful empty-list allocation happening on every single simulated trading day,
  removed dead code left over from an earlier draft, and added fetch-progress logging so a long,
  rate-limited data-fetch phase isn't silent.

### Decision-reliability pass (new)
A second follow-up focused specifically on whether the *numbers themselves* can be trusted for a
real decision, not just whether the code runs cleanly:
- **Verified no look-ahead bias in the scanner's own scoring machinery.** Traced `VcpDetector`,
  `PriceLevelCalculator`, and `SupertrendMetricsCalculator` — all three index strictly backward
  from the scoring date at the array level (confirmed by reading the actual loop bounds, e.g.
  `VcpDetector.Detect(quotes, i - VcpLookbackDays + 1, i)` never passes an end index past `i`). This
  was worth checking explicitly: it's the kind of bug that's invisible in live scanning (there is no
  future data yet) but would silently inflate a backtest if it existed. It doesn't.
- **Added computed reliability caveats per run** (`BacktestDiagnostics.cs`), surfaced prominently —
  first thing in the HTML report, a dedicated warning panel in the dashboard, and printed to the
  console — rather than buried in this README where they're easy to skip:
  - **Survivorship bias**: `Nifty200`/`SMID`/`NewListings`/`All` are *today's* constituent lists,
    applied retroactively. A stock that got delisted or dropped from the index mid-backtest is
    silently absent; one that only qualified recently is included for the whole period. This is a
    textbook way backtests overstate real returns, and it's now called out by name on every run
    that uses one of these universes (a `Custom` ticker list you supply yourself doesn't trigger it).
  - **Small sample size**: fewer than 30 completed trades triggers a note that Sharpe/Sortino/win
    rate/profit factor aren't statistically meaningful yet — easy to overlook when a `report.html`
    shows a shiny Sharpe ratio computed from four trades.
  - **Data coverage**: flags when meaningfully fewer tickers had usable data than were requested.
  - **Intraday-history-limit reminder**: dynamic version of the caveat below, computed from the
    actual configured timeframes and date range for that specific run.
  - **A standing overfitting reminder**: re-tuning parameters against the same historical window and
    keeping the best result is a known way to fool yourself; every report says so.
  These are persisted to the database (as JSON under a `Caveats` parameter) and reconstructed by the
  dashboard, so a caveat travels with the report even months later — it's never just console output
  that scrolled away.

### US market support (new)

Three new `Backtest.Universe` options: `"Nasdaq100"` (103 tickers, cross-checked against live
holdings data rather than pulled from memory), `"DowJones"` (all 30), and `"USMarket"` (the
deduplicated union — there's real overlap, e.g. MSFT/AAPL/AMZN/GOOGL/NVDA/HON/AMGN/CSCO/WMT sit in
both). Tickers live in `Backtesting/UsMarketUniverses.cs` and use plain Yahoo symbols with no
suffix (correct for Nasdaq/NYSE-listed stock, including foreign ADSs like ASML/NXPI/ARM/MELI/PDD
that list directly on a US exchange).

This isn't just three new lists bolted onto the existing engine — selecting one of them changes
three things under the hood, all derived from a single `BacktestMarket.RegionFor(Universe)` check:
- **Trading calendar**: a proper NYSE/Nasdaq holiday calendar (`UsMarketCalendar.cs`), not the NSE
  one. All 10 real market holidays, including the moveable ones (Good Friday via an Easter-Sunday
  calculation, MLK/Presidents/Memorial/Labor Day/Thanksgiving via weekday-of-month rules), with the
  standard Saturday→Friday / Sunday→Monday observation shift for fixed-date holidays. Verified
  against known reference dates before trusting it, not just eyeballed.
- **Cost model**: US backtests are no longer charged Indian STT/GST/stamp duty/SEBI charges, which
  obviously don't apply to US equities. Instead: a configurable flat commission (`UsCommissionPerOrder`,
  defaults to $0 to reflect a typical modern zero-commission broker) plus the two small regulatory
  pass-through fees every US broker charges regardless (`UsSecFeeSellPct`, `UsFinraTafPerShare` /
  `UsFinraTafCapPerOrder`) — see `Backtesting/CostCalculator.cs`.
- **Currency display**: `$` instead of `₹` in the HTML report, dashboard, and JSON; the console
  keeps ASCII `Rs.`/`$` prefixes rather than a Unicode symbol, matching the existing console output's
  original ASCII-only design rather than introducing a Unicode character that wasn't there before.

**One thing this does NOT do**: `InitialCapital` is still a single config field interpreted in
whichever currency matches the selected universe — set it to a sensible USD figure (e.g. `100000`)
when backtesting a US universe, since the default (`1000000`) was chosen with ₹10 lakh in mind. A
caveat about this is added automatically to every US-universe run's report.

**Running a US backtest**: the default `appsettings.json` still runs `Nifty200` unchanged (the
live scanner and default backtest behavior stay India-focused) — to backtest Nasdaq-100 instead,
override the relevant keys before running `dotnet run -- --backtest`:
```json
"Backtest": {
  "Universe": "Nasdaq100",
  "InitialCapital": 100000,
  "UseFixedPercentStop": false
}
```
`UsCommissionPerOrder` / `UsSecFeeSellPct` / `UsFinraTafPerShare` / `UsFinraTafCapPerOrder` are
already populated in `appsettings.json` with sensible defaults, so no extra config is required to
get a working US-market cost simulation — only `Universe` and `InitialCapital` typically need
changing.

`Custom` ticker lists still default to the India cost model/calendar (most Custom lists on this
project will be NSE tickers) — `BacktestMarket.RegionFor` is the one place to extend if you build a
Custom list of US tickers and want it treated as a US backtest.

### Important caveats — please read before trusting the numbers
- **Not compiled here.** No NuGet/SDK access in this environment, in either pass. Every file was
  reviewed by hand against the actual class signatures in your codebase, and checked for balanced
  braces/parens, but please run `dotnet restore && dotnet build` locally; if the compiler flags
  anything, it's almost certainly a small naming/signature mismatch and quick to fix.
- **Yahoo's intraday history is short.** 5m/15m/30m bars are only available for roughly the last
  ~60 days, 1h for ~2 years, regardless of the backtest's `StartDate`. For a multi-year backtest,
  the default config (`"Timeframes": ["1d"]`) is the realistic choice — intraday timeframes will
  silently contribute nothing before their own history window starts (the aggregator already
  degrades gracefully when a timeframe has no data for a given date), so adding them mainly helps
  the *most recent* stretch of a long backtest.
- **Look-ahead-bias policy** (documented in code comments in `BacktestEngine.cs`/`ExitEvaluator.cs`):

  stop-loss/target are checked intrabar using each day's own High/Low; every other decision (new
  entries, Supertrend/HMA/time/signal exits) is decided from a day's close but only ever *executed*
  at the next trading day's open. This is a standard, defensible convention, but it is a modeling
  choice, not a law of nature — worth knowing before comparing results to a different backtester.
- **Brokerage/STT/GST/stamp-duty/SEBI rates are configurable estimates**, not certified figures.
  Double-check current rates with your broker before drawing real conclusions from the P&L.
- This is a simulation tool for evaluating a strategy's historical behavior. It is not investment
  advice, and past performance in a backtest does not guarantee future results.

## Weight Optimizer (new in v4.1)

Closes the loop between the backtest engine and the live scoring weights. Runs a random
search over the 6 scoring weights (VCP/Volume/Trend/Supertrend/HMA/ADX), backtests each
candidate with the *same* `BacktestEngine`/`PerformanceAnalyzer` as `--backtest` (identical
trade simulation, costs, look-ahead-bias discipline), and writes the best-performing set to
`optimized_weights.json`.

```bash
NseMomentumScanner --optimize-weights                          # 30 iterations, Sharpe objective
NseMomentumScanner --optimize-weights --iterations=50 --objective=calmar --seed=42
```

| Flag | Default | Meaning |
|------|---------|---------|
| `--iterations=N` | 30 | Candidates tried, plus 1 baseline (current defaults) run for comparison |
| `--objective=X` | sharpe | `sharpe` \| `cagr` \| `calmar` — which metric ranks candidates |
| `--max-drawdown=N` | 35 | Disqualify any candidate with worse than N% max drawdown |
| `--min-trades=N` | 15 | Disqualify candidates with too few trades to trust the score |
| `--seed=N` | random | Fix for a reproducible search |
| `--output=PATH` | `optimized_weights.json` | Where the winner is written |

Uses the same `"Backtest"` appsettings.json section (universe, date range, costs) as
`--backtest`. Only the **first** iteration pays for a full historical data fetch — every
subsequent candidate reuses `BacktestDataCache`'s already-downloaded daily bars, so
iteration count mostly costs CPU/wall-clock time for the trade simulation, not additional
Yahoo requests or rate-limit risk.

If the baseline weights win the search, no file is written — a run is not forced to
"discover" an improvement that isn't there over that period/iteration budget.

**The live scanner picks up a winning weight file automatically** on its next run
(`Config.TryLoadOptimizedWeightsFromDisk()`, called once at startup for scan/`--serve`/
`--backtest` alike) — no restart-with-a-flag needed, controlled by `UseOptimizedWeights` in
`appsettings.json` (default `true`). A weight file older than `OptimizedWeightsMaxAgeDays`
(default 120) is logged as stale and ignored, since weights tuned against a year-old market
regime aren't necessarily still appropriate — re-run `--optimize-weights` periodically. The
dashboard's regime banner shows which weight-set produced the current session
("Weights: default" or "Weights: optimized 2026-08-01 (sharpe)"), and `scan_sessions.weight_source`
in the DB is the permanent audit trail.

**This is a random search over a 6-dimensional simplex, not a mathematical optimizer** — a
discontinuous, path-dependent objective (trade simulation with stops/targets/transaction
costs) has no tractable closed form, so this is the same pragmatic starting point a quant
would reach for before something heavier (Bayesian optimization, CMA-ES) is justified by
iteration budget. Treat a winning weight set as a hypothesis worth walk-forward validating
on out-of-sample data, not a proof.



