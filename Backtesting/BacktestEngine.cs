// =============================================================================
//  BACKTESTING MODULE - BacktestEngine
// -----------------------------------------------------------------------------
//  Orchestrates the whole historical simulation:
//    1. Fetch history for every ticker via the EXISTING YahooDataFetcher.
//    2. Score every ticker/timeframe/date via the EXISTING TickerScorer -
//       the exact same buy/sell logic the live scanner uses, just run over
//       a historical date range instead of "today".
//    3. Aggregate each day via the EXISTING MultiTfAggregator to get ranked
//       BUY / IMMEDIATE_SELL calls, identical to what the scanner would have
//       shown that day.
//    4. Walk trading days in chronological order, simulating fills, stops,
//       targets, position sizing, and costs (never touching a future bar).
//
//  Look-ahead-bias policy (see also ExitEvaluator's header comment):
//    - Stop-loss / target are checked INTRABAR on the day being processed
//      (using that day's own High/Low, which is fully known once the bar
//      closes - not future data).
//    - Every other decision (new entries, Supertrend/HMA/time/signal exits)
//      is DECIDED from a day's close/indicators but EXECUTED at the next
//      trading day's open - the standard "signal at EOD, fill next open"
//      convention for avoiding look-ahead bias.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using NseMomentumScanner;

namespace NseMomentumScanner.Backtesting
{
    internal sealed class BacktestEngine
    {
        // Yahoo will simply return less data than requested beyond these horizons;
        // capping the request avoids wasting retry/backoff time on doomed requests.
        private static readonly Dictionary<YahooInterval, int> MaxSafeLookbackDays = new()
        {
            [YahooInterval.FiveMinutes]     = 59,
            [YahooInterval.FifteenMinutes]  = 59,
            [YahooInterval.ThirtyMinutes]   = 59,
            [YahooInterval.NinetyMinutes]   = 59,
            [YahooInterval.SixtyMinutes]    = 729,
            [YahooInterval.OneHour]         = 729,
        };

        private sealed class PendingEntry { public string Symbol = ""; public string Reason = ""; }
        private sealed class PendingExit  { public string Symbol = ""; public ExitReason Reason; }

        // Reused across every "no scan results today" lookup instead of allocating a fresh
        // empty list on every trading day of the loop.
        private static readonly IReadOnlyList<ScanResult> EmptyScanResults = Array.Empty<ScanResult>();

        public async Task<BacktestRunResult> RunAsync(CancellationToken ct = default)
        {
            var startDate = BacktestConfig.StartDate;
            var endDate   = BacktestConfig.EndDate <= DateTime.Today ? BacktestConfig.EndDate : DateTime.Today;
            if (endDate <= startDate)
                throw new InvalidOperationException(
                    $"Backtest EndDate ({endDate:yyyy-MM-dd}) must be after StartDate ({startDate:yyyy-MM-dd}).");

            var tickers = BuildUniverse();
            var timeframes = ResolveTimeframes();

            Log.Information("Backtest: {Uni} ({N} tickers), {Start:yyyy-MM-dd} -> {End:yyyy-MM-dd}, timeframes: {Tfs}",
                BacktestConfig.Universe, tickers.Count, startDate, endDate, string.Join(",", timeframes.Select(t => t.Label)));

            // ── 1+2. Fetch & score every ticker (parallelized exactly like the live scanner) ──
            var seriesIndex = new DailySeriesIndex();
            var allScanResults = new ConcurrentBag<ScanResult>();
            int warmupCalendarDays = 750; // buffer for EMA200 warm-up + weekends/holidays
            int dailyLookback = (int)Math.Ceiling((DateTime.Today - startDate).TotalDays) + warmupCalendarDays;

            using var fetcher = new YahooDataFetcher();
            int fetched = 0, skippedTickers = 0, cacheHits = 0;
            DateTime dailyEarliestNeeded = DateTime.Today.AddDays(-dailyLookback);

            // v4.5: benchmark is part of the historical strategy, not just the live scanner.
            // Fetch it once and pass the as-of-date close map into the exact same scorer used by
            // live mode. This removes the v4.4 live/backtest parity gap for RS and market context.
            List<StockQuote> benchmarkQuotes = new();
            Dictionary<DateTime, decimal> benchmarkCloseByDate = new();
            Dictionary<DateTime, MarketRegimeInfo> historicalRegimes = new();
            try
            {
                benchmarkQuotes = await fetcher.FetchAsync(Config.MarketBenchmarkSymbol, YahooInterval.OneDay,
                    Math.Max(dailyLookback, Config.MarketRegimeLookbackDays), ct);
                foreach (var q in benchmarkQuotes) benchmarkCloseByDate[q.Date.Date] = q.Close;
                historicalRegimes = MarketRegimeAnalyzer.BuildHistoricalSnapshots(benchmarkQuotes, startDate, endDate);
                Log.Information("Backtest benchmark context ready: {Sym}, {Bars} bars, {Regimes} historical regimes.",
                    Config.MarketBenchmarkSymbol, benchmarkQuotes.Count, historicalRegimes.Count);
            }
            catch (Exception ex)
            {
                Log.Warning("Backtest benchmark fetch failed ({Sym}): {Msg} — RS/regime gates will degrade gracefully.",
                    Config.MarketBenchmarkSymbol, ex.Message);
            }

            await Parallel.ForEachAsync(tickers,
                new ParallelOptions { MaxDegreeOfParallelism = Config.MaxConcurrentFetches, CancellationToken = ct },
                async (symbol, innerCt) =>
                {
                    List<StockQuote> dailyQuotes;
                    var cachedDaily = await BacktestDataCache.TryReadAsync(
                        symbol, YahooInterval.OneDay, dailyEarliestNeeded, innerCt);
                    if (cachedDaily is not null)
                    {
                        dailyQuotes = cachedDaily;
                        Interlocked.Increment(ref cacheHits);
                    }
                    else
                    {
                        try
                        {
                            dailyQuotes = await fetcher.FetchAsync(symbol, YahooInterval.OneDay, dailyLookback, innerCt);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log.Debug("Backtest fetch failed {Sym} [1d]: {Msg}", symbol, ex.Message);
                            Interlocked.Increment(ref skippedTickers);
                            return;
                        }
                        await BacktestDataCache.WriteAsync(symbol, YahooInterval.OneDay, dailyQuotes, innerCt);
                    }

                    var dqDaily = DataValidator.Validate(dailyQuotes, YahooInterval.OneDay);
                    if (!dqDaily.IsValid)
                    {
                        Log.Debug("Backtest skip {Sym}: {Reason}", symbol, dqDaily.FailReason);
                        Interlocked.Increment(ref skippedTickers);
                        return;
                    }

                    // Wrapped defensively: indicator computation (Supertrend/HMA/ATR) can in
                    // principle throw for a pathological data shape even after DataValidator
                    // passed the raw quotes - one bad ticker must not abort the whole run.
                    try
                    {
                        seriesIndex.Add(symbol, new SymbolDailySeries(dailyQuotes, BacktestConfig.AtrPeriod));
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Backtest indicator computation failed {Sym}: {Msg}", symbol, ex.Message);
                        Interlocked.Increment(ref skippedTickers);
                        return;
                    }

                    foreach (var tf in timeframes)
                    {
                        if (innerCt.IsCancellationRequested) break;
                        try
                        {
                            List<StockQuote> quotes;
                            DataQuality dq;
                            if (tf.Interval == YahooInterval.OneDay)
                            {
                                quotes = dailyQuotes;
                                dq = dqDaily;
                            }
                            else
                            {
                                int lb = SafeLookback(tf.Interval, dailyLookback);
                                var cachedTf = await BacktestDataCache.TryReadAsync(
                                    symbol, tf.Interval, DateTime.Today.AddDays(-lb), innerCt);
                                if (cachedTf is not null)
                                {
                                    quotes = cachedTf;
                                    Interlocked.Increment(ref cacheHits);
                                }
                                else
                                {
                                    quotes = await fetcher.FetchAsync(symbol, tf.Interval, lb, innerCt);
                                    await BacktestDataCache.WriteAsync(symbol, tf.Interval, quotes, innerCt);
                                }
                                dq = DataValidator.Validate(quotes, tf.Interval);
                            }
                            if (!dq.IsValid) continue;

                            var results = TickerScorer.Score(
                                symbol, tf.Interval, tf.Label, tf.VcpEnabled, quotes, startDate, endDate, benchmarkCloseByDate);
                            foreach (var r in results) allScanResults.Add(r);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log.Debug("Backtest scoring error {Sym} [{Tf}]: {Msg}", symbol, tf.Label, ex.Message);
                        }
                    }
                    Interlocked.Increment(ref fetched);
                    int done = fetched + skippedTickers;
                    if (done % 20 == 0 || done == tickers.Count)
                        Log.Information("Backtest fetch progress: {Done}/{Total} tickers processed.", done, tickers.Count);
                });

            Log.Information("Backtest data ready: {Fetched} usable tickers ({Skipped} skipped, {CacheHits} series served from cache).",
                fetched, skippedTickers, cacheHits);

            var resultsByDate = allScanResults
                .GroupBy(r => r.ScanDate.Date)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ScanResult>)g.ToList());

            // ── 3+4. Day-by-day simulation ────────────────────────────────────
            var portfolio = new PortfolioTracker(BacktestConfig.InitialCapital);
            var simulator = new TradeSimulator(portfolio);
            var equityBuilder = new EquityCurveBuilder();

            var tradingDays = (BacktestMarket.CurrentRegion == MarketRegion.UnitedStates
                ? UsMarketCalendar.TradingDays(startDate, endDate)
                : NseCalendar.TradingDays(startDate, endDate)).ToList();
            var pendingEntries = new List<PendingEntry>();
            var pendingExits = new List<PendingExit>();

            decimal LastCloseAsOf(string symbol, DateTime date) =>
                seriesIndex.TryGet(symbol, out var s) ? s.CloseOnOrBefore(date) : 0m;

            foreach (var day in tradingDays)
            {
                ct.ThrowIfCancellationRequested();

                // Step A: execute anything queued from a previous day's close, at TODAY's open.
                var stillPendingExits = new List<PendingExit>();
                foreach (var pe in pendingExits)
                {
                    if (!portfolio.Positions.TryGetValue(pe.Symbol, out var pos)) continue; // already closed (e.g. stopped out)
                    if (!seriesIndex.TryGet(pe.Symbol, out var series)) continue;
                    var idx = series.ExactIndex(day);
                    if (idx is null) { stillPendingExits.Add(pe); continue; } // no bar today - retry tomorrow
                    simulator.Close(pos, day, series.Quotes[idx.Value].Open, pe.Reason);
                }
                pendingExits = stillPendingExits;

                var stillPendingEntries = new List<PendingEntry>();
                foreach (var peu in pendingEntries)
                {
                    if (portfolio.Positions.ContainsKey(peu.Symbol)) continue; // already opened elsewhere
                    if (!seriesIndex.TryGet(peu.Symbol, out var series)) continue;
                    var idx = series.ExactIndex(day);
                    if (idx is null) { stillPendingEntries.Add(peu); continue; }
                    decimal atrToday = series.AtrAt(idx.Value);
                    simulator.TryOpen(peu.Symbol, day, series.Quotes[idx.Value].Open, atrToday,
                        sym => LastCloseAsOf(sym, day), peu.Reason);
                }
                pendingEntries = stillPendingEntries;

                // Step B: intrabar stop-loss / target check on TODAY's own bar for every open position.
                var closedToday = new HashSet<string>();
                foreach (var symbol in portfolio.Positions.Keys.ToList())
                {
                    if (!seriesIndex.TryGet(symbol, out var series)) continue;
                    var idx = series.ExactIndex(day);
                    if (idx is null) continue; // no bar today for this stock - can't evaluate

                    var pos = portfolio.Positions[symbol];
                    var bar = series.Quotes[idx.Value];

                    // Check today's bar against the stop/target LEVEL AS OF YESTERDAY's close
                    // first - moving the trailing level using today's own close before testing
                    // today's own low against it would let same-bar information leak backwards.
                    if (pos.CurrentStopLoss > 0m && bar.Low <= pos.CurrentStopLoss)
                    {
                        decimal fill = bar.Open < pos.CurrentStopLoss ? bar.Open : pos.CurrentStopLoss;
                        var reason = BacktestConfig.UseAtrTrailingStop && pos.CurrentStopLoss > pos.InitialStopLoss
                            ? ExitReason.TrailingStop
                            : (BacktestConfig.UseAtrStop ? ExitReason.AtrStop : ExitReason.StopLoss);
                        simulator.Close(pos, day, fill, reason);
                        closedToday.Add(symbol);
                        continue;
                    }
                    if (pos.Target > 0m && bar.High >= pos.Target)
                    {
                        decimal fill = bar.Open > pos.Target ? bar.Open : pos.Target;
                        simulator.Close(pos, day, fill, ExitReason.Target);
                        closedToday.Add(symbol);
                        continue;
                    }

                    // Survived today - ratchet the trailing stop and update MFE/MAE using
                    // today's own bar, ready for tomorrow's check.
                    decimal atrToday = series.AtrAt(idx.Value);
                    ExitEvaluator.UpdateTrailingStop(pos, bar.Close, atrToday);
                    ExitEvaluator.UpdateExcursion(pos, bar.High, bar.Low);
                }

                // Step C: signal-based exits (Supertrend/HMA/time/aggregated sell), decided on
                // today's close/indicators, queued for execution at tomorrow's open.
                if (!resultsByDate.TryGetValue(day.Date, out var todaysResults))
                    todaysResults = EmptyScanResults;
                historicalRegimes.TryGetValue(day.Date, out var regimeToday);
                var aggToday = MultiTfAggregator.Aggregate(todaysResults, day, regimeToday);
                var sellSignalSymbols = aggToday
                    .Where(a => a.RecommendationType == "IMMEDIATE_SELL")
                    .Select(a => a.Symbol)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var symbol in portfolio.Positions.Keys.ToList())
                {
                    if (closedToday.Contains(symbol)) continue;
                    if (pendingExits.Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!seriesIndex.TryGet(symbol, out var series)) continue;
                    var idx = series.ExactIndex(day);
                    if (idx is null) continue;

                    var pos = portfolio.Positions[symbol];
                    var (bullish, hmaFast, hmaSlow) = series.IndicatorsAt(idx.Value);
                    bool immediateSell = sellSignalSymbols.Contains(symbol);

                    ExitReason? reason = null;
                    if (BacktestConfig.UseSupertrendExit && bullish == false) reason = ExitReason.SupertrendFlip;
                    else if (BacktestConfig.UseHmaExit && hmaFast.HasValue && hmaSlow.HasValue && hmaFast < hmaSlow) reason = ExitReason.HmaCrossDown;
                    else if (BacktestConfig.UseTimeExit && (day - pos.EntryDate).Days >= BacktestConfig.MaxHoldingDays) reason = ExitReason.TimeExit;
                    else if (BacktestConfig.ExitOnImmediateSell && immediateSell) reason = ExitReason.SignalSell;

                    if (reason.HasValue)
                        pendingExits.Add(new PendingExit { Symbol = symbol, Reason = reason.Value });
                }

                // Step D: new entries from today's BUY-ranked list, queued for tomorrow's open.
                int roomLeft = BacktestConfig.MaxPositions - portfolio.Positions.Count - pendingEntries.Count;
                if (roomLeft > 0)
                {
                    var candidates = aggToday
                        .Where(a => a.RecommendationType == "BUY"
                                 && a.Rank <= BacktestConfig.MinRankForEntry
                                 && !portfolio.Positions.ContainsKey(a.Symbol)
                                 && !pendingEntries.Any(p => p.Symbol.Equals(a.Symbol, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(a => a.Rank)
                        .Take(roomLeft);

                    foreach (var cand in candidates)
                        pendingEntries.Add(new PendingEntry { Symbol = cand.Symbol, Reason = $"BUY rank {cand.Rank} (score {cand.AggregateScore:F1})" });
                }

                // Step E: end-of-day mark-to-market snapshot for the equity curve.
                decimal holdingsValue = portfolio.HoldingsValue(sym => LastCloseAsOf(sym, day));
                double utilization = portfolio.CapitalUtilizationPct(sym => LastCloseAsOf(sym, day));
                equityBuilder.Record(day, portfolio.CashBalance, holdingsValue, portfolio.Positions.Count, utilization);
            }

            // Force-close any still-open positions at the last available close for reporting.
            foreach (var symbol in portfolio.Positions.Keys.ToList())
            {
                var pos = portfolio.Positions[symbol];
                decimal lastClose = LastCloseAsOf(symbol, endDate);
                if (lastClose > 0m)
                    simulator.Close(pos, endDate, lastClose, ExitReason.EndOfBacktest);
                else
                    portfolio.Close(symbol, 0m); // no price data at all - drop the position, no cash recovered
            }

            var (monthly, yearly) = PerformanceAnalyzer.BuildPeriodReturns(equityBuilder.Points, simulator.ClosedTrades);
            var metrics = PerformanceAnalyzer.Compute(
                simulator.ClosedTrades, equityBuilder.Points, BacktestConfig.InitialCapital, startDate, endDate, monthly);
            var timeframeLabels = timeframes.Select(t => t.Label).ToList();

            var caveats = BacktestDiagnostics.BuildCaveats(
                BacktestConfig.Universe, tickers.Count, fetched, metrics.TotalTrades,
                timeframeLabels, startDate, endDate);

            var parameters = BacktestConfig.Snapshot();
            parameters["Caveats"] = System.Text.Json.JsonSerializer.Serialize(caveats);

            var run = new BacktestRunResult
            {
                StartDate = startDate,
                EndDate = endDate,
                Universe = BacktestConfig.Universe,
                TickerCount = fetched,
                TickersRequested = tickers.Count,
                Timeframes = timeframeLabels,
                Trades = simulator.ClosedTrades,
                EquityCurve = equityBuilder.Points,
                DailySnapshots = equityBuilder.Snapshots,
                Metrics = metrics,
                MonthlyReturns = monthly,
                YearlyReturns = yearly,
                Parameters = parameters,
                Caveats = caveats,
            };

            Log.Information("Backtest complete: {Trades} trades, Net P/L {NP:F2}, Return {RP:F2}%, MaxDD {DD:F2}%",
                metrics.TotalTrades, metrics.NetProfit, metrics.ReturnPct, metrics.MaxDrawdownPct);
            foreach (var note in caveats)
                Log.Warning("Backtest caveat: {Note}", note);

            return run;
        }

        private static int SafeLookback(YahooInterval interval, int requested) =>
            MaxSafeLookbackDays.TryGetValue(interval, out var cap) ? Math.Min(requested, cap) : requested;

        private static List<string> BuildUniverse()
        {
            string uni = BacktestConfig.Universe.Trim();
            IEnumerable<string> baseList = uni.ToLowerInvariant() switch
            {
                "nifty200"                    => Nifty200Universe.Tickers,
                "smid" or "niftysmid"         => NiftySMIDUniverse.Tickers,
                "newlistings"                 => NewListingsUniverse.Tickers,
                "all"                         => Nifty200Universe.Tickers
                                                    .Concat(NiftySMIDUniverse.Tickers)
                                                    .Concat(NewListingsUniverse.Tickers),
                "nasdaq100"                   => Nasdaq100Universe.Tickers,
                "dowjones"                    => DowJonesUniverse.Tickers,
                "usmarket"                    => UsMarketUniverse.Tickers,
                "custom"                      => BacktestConfig.CustomTickers,
                _                             => Nifty200Universe.Tickers,
            };

            if (BacktestConfig.CustomTickers.Count > 0 && uni.ToLowerInvariant() != "custom")
                baseList = baseList.Concat(BacktestConfig.CustomTickers);

            return baseList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<(YahooInterval Interval, string Label, bool VcpEnabled, int LookbackDays)> ResolveTimeframes()
        {
            var wanted = BacktestConfig.Timeframes.Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
            var list = new List<(YahooInterval, string, bool, int)>();
            foreach (var tf in Config.ScanTimeframes)
                if (wanted.Contains(tf.Label.ToLowerInvariant()))
                    list.Add((tf.Interval, tf.Label, tf.VcpEnabled, tf.LookbackDays));

            if (list.Count == 0)
            {
                var daily = Config.ScanTimeframes.First(t => t.Interval == YahooInterval.OneDay);
                list.Add((daily.Interval, daily.Label, daily.VcpEnabled, daily.LookbackDays));
            }
            return list;
        }
    }
}
