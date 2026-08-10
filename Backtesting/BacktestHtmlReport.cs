// =============================================================================
//  BACKTESTING MODULE - BacktestHtmlReport
// -----------------------------------------------------------------------------
//  Renders one self-contained HTML file per run: summary cards, equity/
//  drawdown charts (Chart.js via the same CDN the live dashboard already
//  uses), a monthly-returns heat map, yearly returns, top winners/losers,
//  most-traded stocks, and the full trade journal.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Serilog;

namespace NseMomentumScanner.Backtesting
{
    internal static class BacktestHtmlReport
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Write(BacktestRunResult run, string runFolder)
        {
            try
            {
                Directory.CreateDirectory(runFolder);
                string path = Path.Combine(runFolder, "report.html");
                File.WriteAllText(path, Build(run), Encoding.UTF8);
                return path;
            }
            catch (Exception ex)
            {
                Log.Warning("Backtest HTML report error: {Msg}", ex.Message);
                return string.Empty;
            }
        }

        private static string Build(BacktestRunResult run)
        {
            var m = run.Metrics;
            var sb = new StringBuilder();
            var region = BacktestMarket.RegionFor(run.Universe);
            string currencySymbol = BacktestMarket.CurrencySymbol(region);
            string Money(decimal v) => currencySymbol + v.ToString("N0", Inv);

            var equityLabels = run.EquityCurve.Select(p => p.Date.ToString("yyyy-MM-dd")).ToList();
            var equityValues = run.EquityCurve.Select(p => Math.Round(p.Equity, 2)).ToList();
            var drawdownValues = run.EquityCurve.Select(p => Math.Round(p.DrawdownPct, 2)).ToList();

            var jsonOpts = new JsonSerializerOptions();
            string equityLabelsJson = JsonSerializer.Serialize(equityLabels, jsonOpts);
            string equityValuesJson = JsonSerializer.Serialize(equityValues, jsonOpts);
            string drawdownValuesJson = JsonSerializer.Serialize(drawdownValues, jsonOpts);

            sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"UTF-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.Append($"<title>Backtest Report · {Html(run.Universe)} · {run.StartDate:yyyy-MM-dd} to {run.EndDate:yyyy-MM-dd}</title>");
            sb.Append("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.js\"></script>");
            sb.Append("<style>").Append(Css()).Append("</style></head><body>");

            sb.Append("<div class=\"wrap\">");
            sb.Append("<h1>NSE Momentum Scanner — Backtest Report</h1>");
            sb.Append("<div class=\"subtitle\">")
              .Append($"{Html(run.Universe)} &middot; {run.TickerCount} tickers &middot; ")
              .Append($"{run.StartDate:dd-MMM-yyyy} → {run.EndDate:dd-MMM-yyyy} &middot; ")
              .Append($"Timeframes: {Html(string.Join(", ", run.Timeframes))}")
              .Append($"<br><span class=\"muted\">Generated {run.RunTimestampUtc:yyyy-MM-dd HH:mm} UTC" +
                      (run.RunId > 0 ? $" · Run #{run.RunId}" : "") + "</span></div>");

            // ── Reliability caveats (shown first, deliberately, before any numbers) ──
            if (run.Caveats.Count > 0)
            {
                sb.Append("<div class=\"panel\" style=\"border-color:#f59e0b\">");
                sb.Append("<h2 style=\"color:#f59e0b\">&#9888; Notes &amp; Caveats — read before trusting these numbers</h2>");
                sb.Append("<ul style=\"padding-left:20px;line-height:1.7;color:#cbd5e1;margin:0\">");
                foreach (var note in run.Caveats)
                    sb.Append($"<li>{Html(note)}</li>");
                sb.Append("</ul></div>");
            }

            // ── Summary cards ──────────────────────────────────────────────
            sb.Append("<div class=\"cards\">");
            Card(sb, "Net Profit", Money(m.NetProfit), m.NetProfit >= 0 ? "pos" : "neg");
            Card(sb, "Return", Pct(m.ReturnPct), m.ReturnPct >= 0 ? "pos" : "neg");
            Card(sb, "CAGR", Pct(m.CagrPct), m.CagrPct >= 0 ? "pos" : "neg");
            Card(sb, "Annual Return", Pct(m.AnnualReturnPct), m.AnnualReturnPct >= 0 ? "pos" : "neg");
            Card(sb, "Max Drawdown", Pct(-Math.Abs(m.MaxDrawdownPct)), "neg");
            Card(sb, "Sharpe Ratio", m.SharpeRatio.ToString("F2", Inv), "");
            Card(sb, "Sortino Ratio", m.SortinoRatio.ToString("F2", Inv), "");
            Card(sb, "Calmar Ratio", m.CalmarRatio.ToString("F2", Inv), "");
            Card(sb, "MAR Ratio", m.MarRatio.ToString("F2", Inv), "");
            Card(sb, "Recovery Factor", m.RecoveryFactor.ToString("F2", Inv), "");
            Card(sb, "Ulcer Index", m.UlcerIndex.ToString("F2", Inv), "");
            Card(sb, "Win Rate", Pct(m.WinRatePct), "");
            Card(sb, "Profit Factor", double.IsInfinity(m.ProfitFactor) ? "∞" : m.ProfitFactor.ToString("F2", Inv), "");
            Card(sb, "Expectancy / Trade", Money(m.Expectancy), m.Expectancy >= 0 ? "pos" : "neg");
            Card(sb, "Total Trades", m.TotalTrades.ToString(Inv), "");
            Card(sb, "Avg Holding Days", m.AverageHoldingDays.ToString("F1", Inv), "");
            Card(sb, "Max Consec. Wins", m.MaxConsecutiveWins.ToString(Inv), "");
            Card(sb, "Max Consec. Losses", m.MaxConsecutiveLosses.ToString(Inv), "");
            Card(sb, "Total Charges", Money(m.TotalCharges), "");
            Card(sb, "Final Equity", Money(m.FinalEquity), "");
            Card(sb, "Initial Capital", Money(m.InitialCapital), "");
            sb.Append("</div>");

            // ── Charts ──────────────────────────────────────────────────────
            sb.Append("<div class=\"panel\"><h2>Equity Curve</h2><canvas id=\"equityChart\" height=\"90\"></canvas></div>");
            sb.Append("<div class=\"panel\"><h2>Drawdown</h2><canvas id=\"ddChart\" height=\"70\"></canvas></div>");

            // ── Monthly returns heat map ─────────────────────────────────────
            sb.Append("<div class=\"panel\"><h2>Monthly Returns Heat Map</h2>");
            sb.Append(BuildHeatMap(run.MonthlyReturns));
            sb.Append("</div>");

            // ── Yearly returns ────────────────────────────────────────────
            sb.Append("<div class=\"panel\"><h2>Yearly Returns</h2><table class=\"tbl\"><thead><tr>" +
                      "<th>Year</th><th>Start Equity</th><th>End Equity</th><th>Return %</th><th>Trades</th></tr></thead><tbody>");
            foreach (var y in run.YearlyReturns)
                sb.Append($"<tr><td>{Html(y.PeriodLabel)}</td><td>{Money(y.StartEquity)}</td><td>{Money(y.EndEquity)}</td>" +
                          $"<td class=\"{(y.ReturnPct >= 0 ? "pos" : "neg")}\">{Pct(y.ReturnPct)}</td><td>{y.Trades}</td></tr>");
            sb.Append("</tbody></table></div>");

            // ── Top winners / losers ──────────────────────────────────────
            sb.Append("<div class=\"twocol\">");
            sb.Append("<div class=\"panel\"><h2>Top Winners</h2>").Append(TradeMiniTable(run.TopWinners(15), currencySymbol)).Append("</div>");
            sb.Append("<div class=\"panel\"><h2>Top Losers</h2>").Append(TradeMiniTable(run.TopLosers(15), currencySymbol)).Append("</div>");
            sb.Append("</div>");

            // ── Most traded stocks ────────────────────────────────────────
            sb.Append("<div class=\"panel\"><h2>Most Traded Stocks</h2><table class=\"tbl\"><thead><tr>" +
                      "<th>Symbol</th><th>Trades</th><th>Win Rate</th><th>Net Profit</th></tr></thead><tbody>");
            foreach (var g in run.Trades.GroupBy(t => t.Symbol)
                         .Select(g => new { Symbol = g.Key, Count = g.Count(), Wins = g.Count(t => t.IsWin), Net = g.Sum(t => t.NetProfit) })
                         .OrderByDescending(x => x.Count).Take(20))
            {
                double wr = g.Count > 0 ? (double)g.Wins / g.Count * 100.0 : 0;
                sb.Append($"<tr><td>{Html(g.Symbol)}</td><td>{g.Count}</td><td>{Pct(wr)}</td>" +
                          $"<td class=\"{(g.Net >= 0 ? "pos" : "neg")}\">{Money(g.Net)}</td></tr>");
            }
            sb.Append("</tbody></table></div>");

            // ── Full trade journal ─────────────────────────────────────────
            sb.Append("<div class=\"panel\"><h2>Trade Journal (" + run.Trades.Count + " trades)</h2>");
            sb.Append("<input id=\"tradeFilter\" class=\"filter\" placeholder=\"Filter by symbol…\" oninput=\"filterTrades()\">");
            sb.Append("<table class=\"tbl\" id=\"tradeTable\"><thead><tr>" +
                      "<th>#</th><th>Symbol</th><th>Entry Date</th><th>Entry ₹</th><th>Exit Date</th><th>Exit ₹</th>" +
                      "<th>Qty</th><th>Stop</th><th>Target</th><th>Entry Reason</th><th>Exit Reason</th>" +
                      "<th>Gross</th><th>Charges</th><th>Net</th><th>Return %</th><th>Days</th><th>MFE %</th><th>MAE %</th>" +
                      "</tr></thead><tbody>");
            foreach (var t in run.Trades.OrderBy(t => t.EntryDate))
            {
                sb.Append($"<tr><td>{t.TradeId}</td><td>{Html(t.Symbol)}</td><td>{t.EntryDate:yyyy-MM-dd}</td>" +
                          $"<td>{t.EntryPrice:F2}</td><td>{t.ExitDate:yyyy-MM-dd}</td><td>{t.ExitPrice:F2}</td>" +
                          $"<td>{t.Quantity:F0}</td><td>{t.StopLoss:F2}</td><td>{t.Target:F2}</td>" +
                          $"<td>{Html(t.EntryReason)}</td><td>{Html(t.ExitReason)}</td>" +
                          $"<td>{Money(t.GrossProfit)}</td><td>{Money(t.Charges)}</td>" +
                          $"<td class=\"{(t.IsWin ? "pos" : "neg")}\">{Money(t.NetProfit)}</td>" +
                          $"<td class=\"{(t.IsWin ? "pos" : "neg")}\">{t.ReturnPct:F2}%</td>" +
                          $"<td>{t.HoldingDays}</td><td>{t.MaxFavorableExcursionPct:F2}%</td><td>{t.MaxAdverseExcursionPct:F2}%</td></tr>");
            }
            sb.Append("</tbody></table></div>");

            // ── Parameters snapshot ────────────────────────────────────────
            sb.Append("<div class=\"panel\"><h2>Run Parameters</h2><table class=\"tbl\"><tbody>");
            foreach (var kv in run.Parameters)
                sb.Append($"<tr><td>{Html(kv.Key)}</td><td>{Html(kv.Value)}</td></tr>");
            sb.Append("</tbody></table></div>");

            sb.Append("<p class=\"disclaimer\">Simulation only — brokerage/tax figures are configurable estimates, " +
                      "not certified rates. Past performance in a backtest does not guarantee future results.</p>");
            sb.Append("</div>"); // .wrap

            // ── Chart scripts ──────────────────────────────────────────────
            sb.Append("<script>");
            sb.Append("const eqLabels = ").Append(equityLabelsJson).Append(";");
            sb.Append("const eqValues = ").Append(equityValuesJson).Append(";");
            sb.Append("const ddValues = ").Append(drawdownValuesJson).Append(";");
            sb.Append(@"
new Chart(document.getElementById('equityChart'), {
  type: 'line',
  data: { labels: eqLabels, datasets: [{ label: 'Portfolio Equity', data: eqValues,
    borderColor: '#3b82f6', backgroundColor: 'rgba(59,130,246,0.08)', fill: true, pointRadius: 0, borderWidth: 2 }] },
  options: { responsive: true, scales: { x: { ticks: { maxTicksLimit: 12 } } }, plugins: { legend: { display: false } } }
});
new Chart(document.getElementById('ddChart'), {
  type: 'line',
  data: { labels: eqLabels, datasets: [{ label: 'Drawdown %', data: ddValues.map(v => -v),
    borderColor: '#ef4444', backgroundColor: 'rgba(239,68,68,0.12)', fill: true, pointRadius: 0, borderWidth: 1.5 }] },
  options: { responsive: true, scales: { x: { ticks: { maxTicksLimit: 12 } }, y: { max: 0 } }, plugins: { legend: { display: false } } }
});
function filterTrades() {
  const q = document.getElementById('tradeFilter').value.toUpperCase();
  document.querySelectorAll('#tradeTable tbody tr').forEach(row => {
    row.style.display = row.children[1].textContent.toUpperCase().includes(q) ? '' : 'none';
  });
}
");
            sb.Append("</script></body></html>");
            return sb.ToString();
        }

        private static string BuildHeatMap(List<PeriodReturn> monthly)
        {
            if (monthly.Count == 0) return "<p class=\"muted\">No completed months yet.</p>";

            var byYear = monthly
                .Select(p => (Year: int.Parse(p.PeriodLabel[..4], Inv), Month: int.Parse(p.PeriodLabel[5..7], Inv), p.ReturnPct))
                .GroupBy(x => x.Year)
                .OrderBy(g => g.Key);

            var sb = new StringBuilder();
            sb.Append("<table class=\"heatmap\"><thead><tr><th>Year</th>");
            for (int mth = 1; mth <= 12; mth++)
                sb.Append($"<th>{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(mth)}</th>");
            sb.Append("</tr></thead><tbody>");

            foreach (var yg in byYear)
            {
                sb.Append($"<tr><td class=\"yr\">{yg.Key}</td>");
                var byMonth = yg.ToDictionary(x => x.Month, x => x.ReturnPct);
                for (int mth = 1; mth <= 12; mth++)
                {
                    if (byMonth.TryGetValue(mth, out var r))
                    {
                        string cls = r >= 0 ? "hpos" : "hneg";
                        double intensity = Math.Min(Math.Abs(r) / 10.0, 1.0); // saturate at ±10%
                        sb.Append($"<td class=\"{cls}\" style=\"opacity:{(0.25 + 0.75 * intensity).ToString("F2", Inv)}\">{r:F1}</td>");
                    }
                    else sb.Append("<td class=\"hnone\">·</td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
            return sb.ToString();
        }

        private static string TradeMiniTable(List<BacktestTrade> trades, string currencySymbol)
        {
            var sb = new StringBuilder();
            sb.Append("<table class=\"tbl\"><thead><tr><th>Symbol</th><th>Entry</th><th>Exit</th><th>Net</th><th>Return %</th></tr></thead><tbody>");
            foreach (var t in trades)
                sb.Append($"<tr><td>{Html(t.Symbol)}</td><td>{t.EntryDate:yyyy-MM-dd}</td><td>{t.ExitDate:yyyy-MM-dd}</td>" +
                          $"<td class=\"{(t.IsWin ? "pos" : "neg")}\">{currencySymbol}{t.NetProfit.ToString("N0", Inv)}</td>" +
                          $"<td class=\"{(t.IsWin ? "pos" : "neg")}\">{t.ReturnPct:F2}%</td></tr>");
            sb.Append("</tbody></table>");
            return sb.ToString();
        }

        private static void Card(StringBuilder sb, string label, string value, string cls) =>
            sb.Append($"<div class=\"card\"><div class=\"card-label\">{Html(label)}</div>" +
                      $"<div class=\"card-value {cls}\">{value}</div></div>");

        private static string Pct(double v) => v.ToString("F2", Inv) + "%";
        private static string Html(string s) => WebUtility.HtmlEncode(s ?? string.Empty);

        private static string Css() => @"
:root{color-scheme:light;}
*{box-sizing:border-box;}
body{font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;background:#0f172a;color:#e2e8f0;margin:0;padding:24px;}
.wrap{max-width:1200px;margin:0 auto;}
h1{font-size:1.6rem;margin-bottom:4px;color:#f8fafc;}
.subtitle{color:#94a3b8;margin-bottom:24px;font-size:0.9rem;}
.muted{color:#64748b;font-size:0.8rem;}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(160px,1fr));gap:12px;margin-bottom:24px;}
.card{background:#1e293b;border-radius:10px;padding:14px;border:1px solid #334155;}
.card-label{font-size:0.72rem;color:#94a3b8;text-transform:uppercase;letter-spacing:.04em;margin-bottom:6px;}
.card-value{font-size:1.25rem;font-weight:600;color:#f1f5f9;}
.pos{color:#4ade80 !important;}
.neg{color:#f87171 !important;}
.panel{background:#1e293b;border:1px solid #334155;border-radius:10px;padding:18px;margin-bottom:20px;}
.panel h2{margin-top:0;font-size:1.05rem;color:#f1f5f9;}
.twocol{display:grid;grid-template-columns:1fr 1fr;gap:20px;}
.tbl{width:100%;border-collapse:collapse;font-size:0.82rem;}
.tbl th{text-align:left;color:#94a3b8;font-weight:600;border-bottom:1px solid #334155;padding:6px 8px;position:sticky;top:0;background:#1e293b;}
.tbl td{padding:5px 8px;border-bottom:1px solid #24324a;color:#cbd5e1;}
.tbl tbody tr:hover{background:#24324a;}
.filter{width:100%;padding:8px 10px;margin-bottom:10px;border-radius:6px;border:1px solid #334155;background:#0f172a;color:#e2e8f0;}
.heatmap{border-collapse:collapse;font-size:0.78rem;width:100%;}
.heatmap th{color:#94a3b8;padding:4px 6px;}
.heatmap td{text-align:center;padding:8px 4px;border-radius:4px;color:#0f172a;font-weight:600;}
.heatmap .yr{color:#e2e8f0;font-weight:600;background:none;}
.hpos{background:#22c55e;}
.hneg{background:#ef4444;}
.hnone{background:#1e293b;color:#475569 !important;}
.disclaimer{color:#64748b;font-size:0.78rem;margin-top:12px;}
@media (max-width:800px){.twocol{grid-template-columns:1fr;}}
";
    }
}
