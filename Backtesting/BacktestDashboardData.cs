// =============================================================================
//  BACKTESTING MODULE - BacktestDashboardData
// -----------------------------------------------------------------------------
//  Builds the JSON payloads served to the new /backtest dashboard page. Reads
//  only from BacktestRepository (the new tables) - never touches the
//  scanner's own DashboardDataBuilder/session tables.
// =============================================================================
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace NseMomentumScanner.Backtesting
{
    internal static class BacktestDashboardData
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public static async Task<string> BuildRunsListJsonAsync()
        {
            await using var repo = new BacktestRepository();
            await repo.InitializeAsync();
            var runs = await repo.ListRunsAsync();

            var payload = runs.Select(r => new
            {
                runId = r.RunId,
                runTimestampUtc = r.RunTimestampUtc,
                startDate = r.StartDate,
                endDate = r.EndDate,
                universe = r.Universe,
                totalTrades = r.TotalTrades,
                returnPct = Math.Round(r.ReturnPct, 2),
                finalEquity = Math.Round(r.FinalEquity, 2),
            }).ToList();

            return JsonSerializer.Serialize(new { runs = payload }, JsonOpts);
        }

        public static async Task<string?> BuildRunDetailJsonAsync(int runId)
        {
            await using var repo = new BacktestRepository();
            await repo.InitializeAsync();
            var run = await repo.GetRunAsync(runId);
            if (run is null) return null;

            var payload = new
            {
                runId = run.RunId,
                runTimestampUtc = run.RunTimestampUtc,
                startDate = run.StartDate.ToString("yyyy-MM-dd"),
                endDate = run.EndDate.ToString("yyyy-MM-dd"),
                universe = run.Universe,
                tickerCount = run.TickerCount,
                tickersRequested = run.TickersRequested,
                timeframes = run.Timeframes,
                metrics = run.Metrics,
                caveats = run.Caveats,
                equityCurve = run.EquityCurve.Select(p => new
                {
                    date = p.Date.ToString("yyyy-MM-dd"),
                    equity = Math.Round(p.Equity, 2),
                    drawdownPct = Math.Round(p.DrawdownPct, 2),
                }),
                monthlyReturns = run.MonthlyReturns,
                yearlyReturns = run.YearlyReturns,
                trades = run.Trades,
                parameters = run.Parameters,
            };

            return JsonSerializer.Serialize(payload, JsonOpts);
        }
    }
}
