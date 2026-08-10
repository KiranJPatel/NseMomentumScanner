// =============================================================================
//  BACKTESTING MODULE - BacktestRepository
// -----------------------------------------------------------------------------
//  Owns ONLY new tables (BacktestRuns, BacktestTrades, PortfolioSnapshots,
//  EquityCurve, PerformanceSummary, Parameters). Never touches the scanner's
//  own tables and never uses the scanner's SqliteRepository class - it opens
//  its own connection to the same database file (Config.DbPath) so a single
//  .db file holds both scan history and backtest history side by side.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Serilog;

namespace NseMomentumScanner.Backtesting
{
    internal sealed class BacktestRepository : IAsyncDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public BacktestRepository()
        {
            _conn = new SqliteConnection($"Data Source={NseMomentumScanner.Config.DbPath};Cache=Shared");
        }

        public async Task InitializeAsync()
        {
            await _conn.OpenAsync();
            // Same production tuning as the scanner's own SqliteRepository, since this
            // connection will do bulk writes (thousands of trade/equity rows per run).
            await ExecAsync("PRAGMA journal_mode = WAL;");
            await ExecAsync("PRAGMA synchronous = NORMAL;");
            await ExecAsync("PRAGMA cache_size = -32000;");   // 32 MB page cache
            await ExecAsync("PRAGMA temp_store = MEMORY;");
            await ExecAsync("PRAGMA mmap_size = 268435456;"); // 256 MB memory-mapped I/O
            await ExecAsync("PRAGMA busy_timeout = 5000;");

            await ExecAsync(@"
CREATE TABLE IF NOT EXISTS BacktestRuns (
    RunId             INTEGER PRIMARY KEY AUTOINCREMENT,
    RunTimestampUtc   TEXT NOT NULL,
    StartDate         TEXT NOT NULL,
    EndDate           TEXT NOT NULL,
    Universe          TEXT NOT NULL,
    TickerCount       INTEGER NOT NULL,
    TickersRequested  INTEGER NOT NULL DEFAULT 0,
    Timeframes        TEXT NOT NULL,
    InitialCapital    REAL NOT NULL,
    FinalEquity       REAL NOT NULL,
    NetProfit         REAL NOT NULL,
    ReturnPct         REAL NOT NULL,
    TotalTrades       INTEGER NOT NULL
);");

            await ExecAsync(@"
CREATE TABLE IF NOT EXISTS BacktestTrades (
    RunId         INTEGER NOT NULL,
    TradeId       INTEGER NOT NULL,
    Symbol        TEXT NOT NULL,
    EntryDate     TEXT NOT NULL,
    EntryPrice    REAL NOT NULL,
    ExitDate      TEXT NOT NULL,
    ExitPrice     REAL NOT NULL,
    Quantity      REAL NOT NULL,
    StopLoss      REAL NOT NULL,
    Target        REAL NOT NULL,
    EntryReason   TEXT,
    ExitReason    TEXT,
    GrossProfit   REAL NOT NULL,
    Charges       REAL NOT NULL,
    NetProfit     REAL NOT NULL,
    ReturnPct     REAL NOT NULL,
    HoldingDays   INTEGER NOT NULL,
    MaxFavorableExcursionPct REAL NOT NULL,
    MaxAdverseExcursionPct   REAL NOT NULL,
    PRIMARY KEY (RunId, TradeId)
);");

            await ExecAsync(@"
CREATE TABLE IF NOT EXISTS PortfolioSnapshots (
    RunId                 INTEGER NOT NULL,
    Date                  TEXT NOT NULL,
    CashBalance           REAL NOT NULL,
    HoldingsValue         REAL NOT NULL,
    PortfolioValue        REAL NOT NULL,
    OpenPositions         INTEGER NOT NULL,
    CapitalUtilizationPct REAL NOT NULL,
    PRIMARY KEY (RunId, Date)
);");

            await ExecAsync(@"
CREATE TABLE IF NOT EXISTS EquityCurve (
    RunId       INTEGER NOT NULL,
    Date        TEXT NOT NULL,
    Equity      REAL NOT NULL,
    PeakEquity  REAL NOT NULL,
    DrawdownPct REAL NOT NULL,
    PRIMARY KEY (RunId, Date)
);");

            await ExecAsync(@"
CREATE TABLE IF NOT EXISTS PerformanceSummary (
    RunId                INTEGER PRIMARY KEY,
    TotalTrades          INTEGER, WinningTrades INTEGER, LosingTrades INTEGER,
    WinRatePct           REAL, AverageWin REAL, AverageLoss REAL,
    LargestWin           REAL, LargestLoss REAL, ProfitFactor REAL,
    AverageHoldingDays   REAL, MaxDrawdownPct REAL,
    MaxConsecutiveWins   INTEGER, MaxConsecutiveLosses INTEGER,
    SharpeRatio          REAL, SortinoRatio REAL, CalmarRatio REAL,
    RecoveryFactor       REAL, Expectancy REAL, NetProfit REAL,
    ReturnPct            REAL, AnnualReturnPct REAL, AverageMonthlyReturnPct REAL,
    CagrPct              REAL, UlcerIndex REAL, MarRatio REAL,
    TotalCharges         REAL, FinalEquity REAL, InitialCapital REAL
);");

            await ExecAsync(@"
CREATE TABLE IF NOT EXISTS Parameters (
    RunId INTEGER NOT NULL,
    Key   TEXT NOT NULL,
    Value TEXT,
    PRIMARY KEY (RunId, Key)
);");
        }

        private async Task ExecAsync(string sql)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Persists an entire finished run (header + trades + snapshots + equity
        /// curve + metrics + parameters) in one transaction, returns the new RunId.</summary>
        public async Task<int> SaveRunAsync(BacktestRunResult run)
        {
            await _gate.WaitAsync();
            try
            {
                using var tx = _conn.BeginTransaction();

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO BacktestRuns (RunTimestampUtc, StartDate, EndDate, Universe, TickerCount, TickersRequested, Timeframes,
                           InitialCapital, FinalEquity, NetProfit, ReturnPct, TotalTrades)
VALUES ($ts, $sd, $ed, $uni, $tc, $treq, $tfs, $ic, $fe, $np, $rp, $tt);
SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("$ts", run.RunTimestampUtc.ToString("O"));
                    cmd.Parameters.AddWithValue("$sd", run.StartDate.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$ed", run.EndDate.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$uni", run.Universe);
                    cmd.Parameters.AddWithValue("$tc", run.TickerCount);
                    cmd.Parameters.AddWithValue("$treq", run.TickersRequested);
                    cmd.Parameters.AddWithValue("$tfs", string.Join(",", run.Timeframes));
                    cmd.Parameters.AddWithValue("$ic", (double)run.Metrics.InitialCapital);
                    cmd.Parameters.AddWithValue("$fe", (double)run.Metrics.FinalEquity);
                    cmd.Parameters.AddWithValue("$np", (double)run.Metrics.NetProfit);
                    cmd.Parameters.AddWithValue("$rp", run.Metrics.ReturnPct);
                    cmd.Parameters.AddWithValue("$tt", run.Metrics.TotalTrades);
                    var result = await cmd.ExecuteScalarAsync();
                    run.RunId = Convert.ToInt32(result);
                }

                // Trades / snapshots / equity rows can number in the thousands for a
                // multi-year run, so each table gets ONE prepared command reused across all
                // rows (only .Value is updated per iteration) instead of allocating a fresh
                // SqliteCommand + parameter set per row - this is the standard high-throughput
                // bulk-insert pattern for Microsoft.Data.Sqlite.
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO BacktestTrades (RunId, TradeId, Symbol, EntryDate, EntryPrice, ExitDate, ExitPrice,
    Quantity, StopLoss, Target, EntryReason, ExitReason, GrossProfit, Charges, NetProfit,
    ReturnPct, HoldingDays, MaxFavorableExcursionPct, MaxAdverseExcursionPct)
VALUES ($rid,$tid,$sym,$ed,$ep,$xd,$xp,$q,$sl,$tg,$er,$xr,$gp,$ch,$np,$rp,$hd,$mfe,$mae);";
                    var pRid = cmd.Parameters.Add("$rid", SqliteType.Integer);
                    var pTid = cmd.Parameters.Add("$tid", SqliteType.Integer);
                    var pSym = cmd.Parameters.Add("$sym", SqliteType.Text);
                    var pEd  = cmd.Parameters.Add("$ed",  SqliteType.Text);
                    var pEp  = cmd.Parameters.Add("$ep",  SqliteType.Real);
                    var pXd  = cmd.Parameters.Add("$xd",  SqliteType.Text);
                    var pXp  = cmd.Parameters.Add("$xp",  SqliteType.Real);
                    var pQ   = cmd.Parameters.Add("$q",   SqliteType.Real);
                    var pSl  = cmd.Parameters.Add("$sl",  SqliteType.Real);
                    var pTg  = cmd.Parameters.Add("$tg",  SqliteType.Real);
                    var pEr  = cmd.Parameters.Add("$er",  SqliteType.Text);
                    var pXr  = cmd.Parameters.Add("$xr",  SqliteType.Text);
                    var pGp  = cmd.Parameters.Add("$gp",  SqliteType.Real);
                    var pCh  = cmd.Parameters.Add("$ch",  SqliteType.Real);
                    var pNp  = cmd.Parameters.Add("$np",  SqliteType.Real);
                    var pRp  = cmd.Parameters.Add("$rp",  SqliteType.Real);
                    var pHd  = cmd.Parameters.Add("$hd",  SqliteType.Integer);
                    var pMfe = cmd.Parameters.Add("$mfe", SqliteType.Real);
                    var pMae = cmd.Parameters.Add("$mae", SqliteType.Real);

                    foreach (var t in run.Trades)
                    {
                        pRid.Value = run.RunId;
                        pTid.Value = t.TradeId;
                        pSym.Value = t.Symbol;
                        pEd.Value  = t.EntryDate.ToString("yyyy-MM-dd");
                        pEp.Value  = (double)t.EntryPrice;
                        pXd.Value  = t.ExitDate.ToString("yyyy-MM-dd");
                        pXp.Value  = (double)t.ExitPrice;
                        pQ.Value   = (double)t.Quantity;
                        pSl.Value  = (double)t.StopLoss;
                        pTg.Value  = (double)t.Target;
                        pEr.Value  = t.EntryReason ?? "";
                        pXr.Value  = t.ExitReason ?? "";
                        pGp.Value  = (double)t.GrossProfit;
                        pCh.Value  = (double)t.Charges;
                        pNp.Value  = (double)t.NetProfit;
                        pRp.Value  = t.ReturnPct;
                        pHd.Value  = t.HoldingDays;
                        pMfe.Value = t.MaxFavorableExcursionPct;
                        pMae.Value = t.MaxAdverseExcursionPct;
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT OR REPLACE INTO PortfolioSnapshots (RunId, Date, CashBalance, HoldingsValue, PortfolioValue, OpenPositions, CapitalUtilizationPct)
VALUES ($rid,$d,$cb,$hv,$pv,$op,$cu);";
                    var pRid = cmd.Parameters.Add("$rid", SqliteType.Integer);
                    var pD   = cmd.Parameters.Add("$d",   SqliteType.Text);
                    var pCb  = cmd.Parameters.Add("$cb",  SqliteType.Real);
                    var pHv  = cmd.Parameters.Add("$hv",  SqliteType.Real);
                    var pPv  = cmd.Parameters.Add("$pv",  SqliteType.Real);
                    var pOp  = cmd.Parameters.Add("$op",  SqliteType.Integer);
                    var pCu  = cmd.Parameters.Add("$cu",  SqliteType.Real);

                    foreach (var s in run.DailySnapshots)
                    {
                        pRid.Value = run.RunId;
                        pD.Value   = s.Date.ToString("yyyy-MM-dd");
                        pCb.Value  = (double)s.CashBalance;
                        pHv.Value  = (double)s.HoldingsValue;
                        pPv.Value  = (double)s.PortfolioValue;
                        pOp.Value  = s.OpenPositions;
                        pCu.Value  = s.CapitalUtilizationPct;
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT OR REPLACE INTO EquityCurve (RunId, Date, Equity, PeakEquity, DrawdownPct)
VALUES ($rid,$d,$eq,$pk,$dd);";
                    var pRid = cmd.Parameters.Add("$rid", SqliteType.Integer);
                    var pD   = cmd.Parameters.Add("$d",   SqliteType.Text);
                    var pEq  = cmd.Parameters.Add("$eq",  SqliteType.Real);
                    var pPk  = cmd.Parameters.Add("$pk",  SqliteType.Real);
                    var pDd  = cmd.Parameters.Add("$dd",  SqliteType.Real);

                    foreach (var e in run.EquityCurve)
                    {
                        pRid.Value = run.RunId;
                        pD.Value   = e.Date.ToString("yyyy-MM-dd");
                        pEq.Value  = (double)e.Equity;
                        pPk.Value  = (double)e.PeakEquity;
                        pDd.Value  = e.DrawdownPct;
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                var pm = run.Metrics;
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT OR REPLACE INTO PerformanceSummary (RunId, TotalTrades, WinningTrades, LosingTrades, WinRatePct,
    AverageWin, AverageLoss, LargestWin, LargestLoss, ProfitFactor, AverageHoldingDays, MaxDrawdownPct,
    MaxConsecutiveWins, MaxConsecutiveLosses, SharpeRatio, SortinoRatio, CalmarRatio, RecoveryFactor,
    Expectancy, NetProfit, ReturnPct, AnnualReturnPct, AverageMonthlyReturnPct, CagrPct, UlcerIndex,
    MarRatio, TotalCharges, FinalEquity, InitialCapital)
VALUES ($rid,$tt,$wt,$lt,$wr,$aw,$al,$lw,$ll,$pf,$ahd,$mdd,$mcw,$mcl,$sh,$so,$ca,$rf,$ex,$np,$rp,$ar,
        $amr,$cagr,$ui,$mar,$tc,$fe,$ic);";
                    cmd.Parameters.AddWithValue("$rid", run.RunId);
                    cmd.Parameters.AddWithValue("$tt", pm.TotalTrades);
                    cmd.Parameters.AddWithValue("$wt", pm.WinningTrades);
                    cmd.Parameters.AddWithValue("$lt", pm.LosingTrades);
                    cmd.Parameters.AddWithValue("$wr", pm.WinRatePct);
                    cmd.Parameters.AddWithValue("$aw", (double)pm.AverageWin);
                    cmd.Parameters.AddWithValue("$al", (double)pm.AverageLoss);
                    cmd.Parameters.AddWithValue("$lw", (double)pm.LargestWin);
                    cmd.Parameters.AddWithValue("$ll", (double)pm.LargestLoss);
                    cmd.Parameters.AddWithValue("$pf", double.IsInfinity(pm.ProfitFactor) ? 999999.0 : pm.ProfitFactor);
                    cmd.Parameters.AddWithValue("$ahd", pm.AverageHoldingDays);
                    cmd.Parameters.AddWithValue("$mdd", pm.MaxDrawdownPct);
                    cmd.Parameters.AddWithValue("$mcw", pm.MaxConsecutiveWins);
                    cmd.Parameters.AddWithValue("$mcl", pm.MaxConsecutiveLosses);
                    cmd.Parameters.AddWithValue("$sh", pm.SharpeRatio);
                    cmd.Parameters.AddWithValue("$so", pm.SortinoRatio);
                    cmd.Parameters.AddWithValue("$ca", pm.CalmarRatio);
                    cmd.Parameters.AddWithValue("$rf", pm.RecoveryFactor);
                    cmd.Parameters.AddWithValue("$ex", (double)pm.Expectancy);
                    cmd.Parameters.AddWithValue("$np", (double)pm.NetProfit);
                    cmd.Parameters.AddWithValue("$rp", pm.ReturnPct);
                    cmd.Parameters.AddWithValue("$ar", pm.AnnualReturnPct);
                    cmd.Parameters.AddWithValue("$amr", pm.AverageMonthlyReturnPct);
                    cmd.Parameters.AddWithValue("$cagr", pm.CagrPct);
                    cmd.Parameters.AddWithValue("$ui", pm.UlcerIndex);
                    cmd.Parameters.AddWithValue("$mar", pm.MarRatio);
                    cmd.Parameters.AddWithValue("$tc", (double)pm.TotalCharges);
                    cmd.Parameters.AddWithValue("$fe", (double)pm.FinalEquity);
                    cmd.Parameters.AddWithValue("$ic", (double)pm.InitialCapital);
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO Parameters (RunId, Key, Value) VALUES ($rid,$k,$v);";
                    var pRid = cmd.Parameters.Add("$rid", SqliteType.Integer);
                    var pK   = cmd.Parameters.Add("$k",   SqliteType.Text);
                    var pV   = cmd.Parameters.Add("$v",   SqliteType.Text);

                    foreach (var kv in run.Parameters)
                    {
                        pRid.Value = run.RunId;
                        pK.Value   = kv.Key;
                        pV.Value   = kv.Value ?? "";
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                tx.Commit();

                // Best-effort WAL checkpoint so the -wal file doesn't grow unbounded across
                // many backtest runs; never let this fail the save itself.
                try { await ExecAsync("PRAGMA wal_checkpoint(PASSIVE);"); }
                catch (Exception ex) { Log.Debug("Backtest WAL checkpoint skipped: {Msg}", ex.Message); }

                return run.RunId;
            }
            catch (Exception ex)
            {
                Log.Warning("Backtest DB write error: {Msg}", ex.Message);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<List<(int RunId, string RunTimestampUtc, string StartDate, string EndDate,
            string Universe, int TotalTrades, double ReturnPct, double FinalEquity)>> ListRunsAsync()
        {
            var list = new List<(int, string, string, string, string, int, double, double)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT RunId, RunTimestampUtc, StartDate, EndDate, Universe, TotalTrades, ReturnPct, FinalEquity
                                 FROM BacktestRuns ORDER BY RunId DESC;";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                           r.GetString(4), r.GetInt32(5), r.GetDouble(6), r.GetDouble(7)));
            }
            return list;
        }

        /// <summary>Loads one full run back out of the database for the dashboard's drill-down
        /// view - header + trades + equity curve + persisted metrics + parameters. Monthly/
        /// yearly returns are NOT persisted separately; they are cheap to recompute from the
        /// equity curve + trades via PerformanceAnalyzer, which also keeps them perfectly
        /// consistent with whatever is actually stored.</summary>
        public async Task<BacktestRunResult?> GetRunAsync(int runId)
        {
            BacktestRunResult? run = null;

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT RunTimestampUtc, StartDate, EndDate, Universe, TickerCount, TickersRequested, Timeframes
                                     FROM BacktestRuns WHERE RunId=$rid;";
                cmd.Parameters.AddWithValue("$rid", runId);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return null;
                run = new BacktestRunResult
                {
                    RunId = runId,
                    RunTimestampUtc = DateTime.Parse(r.GetString(0), null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    StartDate = DateTime.Parse(r.GetString(1)),
                    EndDate = DateTime.Parse(r.GetString(2)),
                    Universe = r.GetString(3),
                    TickerCount = r.GetInt32(4),
                    TickersRequested = r.GetInt32(5),
                    Timeframes = r.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                };
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT TradeId, Symbol, EntryDate, EntryPrice, ExitDate, ExitPrice, Quantity,
                                     StopLoss, Target, EntryReason, ExitReason, GrossProfit, Charges, NetProfit,
                                     ReturnPct, HoldingDays, MaxFavorableExcursionPct, MaxAdverseExcursionPct
                                     FROM BacktestTrades WHERE RunId=$rid ORDER BY TradeId;";
                cmd.Parameters.AddWithValue("$rid", runId);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    run.Trades.Add(new BacktestTrade
                    {
                        TradeId = r.GetInt32(0),
                        Symbol = r.GetString(1),
                        EntryDate = DateTime.Parse(r.GetString(2)),
                        EntryPrice = (decimal)r.GetDouble(3),
                        ExitDate = DateTime.Parse(r.GetString(4)),
                        ExitPrice = (decimal)r.GetDouble(5),
                        Quantity = (decimal)r.GetDouble(6),
                        StopLoss = (decimal)r.GetDouble(7),
                        Target = (decimal)r.GetDouble(8),
                        EntryReason = r.IsDBNull(9) ? "" : r.GetString(9),
                        ExitReason = r.IsDBNull(10) ? "" : r.GetString(10),
                        GrossProfit = (decimal)r.GetDouble(11),
                        Charges = (decimal)r.GetDouble(12),
                        NetProfit = (decimal)r.GetDouble(13),
                        ReturnPct = r.GetDouble(14),
                        HoldingDays = r.GetInt32(15),
                        MaxFavorableExcursionPct = r.GetDouble(16),
                        MaxAdverseExcursionPct = r.GetDouble(17),
                    });
                }
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Date, Equity, PeakEquity, DrawdownPct FROM EquityCurve WHERE RunId=$rid ORDER BY Date;";
                cmd.Parameters.AddWithValue("$rid", runId);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    run.EquityCurve.Add(new EquityPoint
                    {
                        Date = DateTime.Parse(r.GetString(0)),
                        Equity = (decimal)r.GetDouble(1),
                        PeakEquity = (decimal)r.GetDouble(2),
                        DrawdownPct = r.GetDouble(3),
                    });
                }
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Key, Value FROM Parameters WHERE RunId=$rid;";
                cmd.Parameters.AddWithValue("$rid", runId);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    run.Parameters[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }

            if (run.Parameters.TryGetValue("Caveats", out var caveatsJson) && !string.IsNullOrWhiteSpace(caveatsJson))
            {
                try
                {
                    run.Caveats = System.Text.Json.JsonSerializer.Deserialize<List<string>>(caveatsJson) ?? new List<string>();
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not parse persisted Caveats for run {RunId}: {Msg}", runId, ex.Message);
                }
            }

            var (monthly, yearly) = PerformanceAnalyzer.BuildPeriodReturns(run.EquityCurve, run.Trades);
            run.MonthlyReturns = monthly;
            run.YearlyReturns = yearly;

            // Read the metrics back exactly as persisted at run time, rather than recomputing
            // (keeps the dashboard's numbers identical to what the HTML/CSV report showed).
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT TotalTrades, WinningTrades, LosingTrades, WinRatePct, AverageWin, AverageLoss,
                    LargestWin, LargestLoss, ProfitFactor, AverageHoldingDays, MaxDrawdownPct, MaxConsecutiveWins,
                    MaxConsecutiveLosses, SharpeRatio, SortinoRatio, CalmarRatio, RecoveryFactor, Expectancy, NetProfit,
                    ReturnPct, AnnualReturnPct, AverageMonthlyReturnPct, CagrPct, UlcerIndex, MarRatio, TotalCharges,
                    FinalEquity, InitialCapital FROM PerformanceSummary WHERE RunId=$rid;";
                cmd.Parameters.AddWithValue("$rid", runId);
                using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    run.Metrics = new PerformanceMetrics
                    {
                        TotalTrades = r.GetInt32(0),
                        WinningTrades = r.GetInt32(1),
                        LosingTrades = r.GetInt32(2),
                        WinRatePct = r.GetDouble(3),
                        AverageWin = (decimal)r.GetDouble(4),
                        AverageLoss = (decimal)r.GetDouble(5),
                        LargestWin = (decimal)r.GetDouble(6),
                        LargestLoss = (decimal)r.GetDouble(7),
                        ProfitFactor = r.GetDouble(8),
                        AverageHoldingDays = r.GetDouble(9),
                        MaxDrawdownPct = r.GetDouble(10),
                        MaxConsecutiveWins = r.GetInt32(11),
                        MaxConsecutiveLosses = r.GetInt32(12),
                        SharpeRatio = r.GetDouble(13),
                        SortinoRatio = r.GetDouble(14),
                        CalmarRatio = r.GetDouble(15),
                        RecoveryFactor = r.GetDouble(16),
                        Expectancy = (decimal)r.GetDouble(17),
                        NetProfit = (decimal)r.GetDouble(18),
                        ReturnPct = r.GetDouble(19),
                        AnnualReturnPct = r.GetDouble(20),
                        AverageMonthlyReturnPct = r.GetDouble(21),
                        CagrPct = r.GetDouble(22),
                        UlcerIndex = r.GetDouble(23),
                        MarRatio = r.GetDouble(24),
                        TotalCharges = (decimal)r.GetDouble(25),
                        FinalEquity = (decimal)r.GetDouble(26),
                        InitialCapital = (decimal)r.GetDouble(27),
                    };
                }
            }

            return run;
        }

        public ValueTask DisposeAsync()
        {
            _conn.Dispose();
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
