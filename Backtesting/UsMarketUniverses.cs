// =============================================================================
//  BACKTESTING MODULE - US Market Universes
// -----------------------------------------------------------------------------
//  Nasdaq-100 and Dow Jones Industrial Average constituent lists, plus a
//  combined/deduplicated US-market universe, for use as Backtest.Universe
//  values ("Nasdaq100", "DowJones", "USMarket"). Mirrors the existing
//  Nifty200Universe / NiftySMIDUniverse / NewListingsUniverse pattern in
//  Program.cs exactly, but lives in the Backtesting module rather than
//  touching the scanner's own universe classes - the live NSE scanner's
//  default universe and behavior are completely unaffected by this file.
//
//  Tickers are plain Yahoo Finance symbols with no exchange suffix (unlike
//  the NSE lists, which use ".NS") - this is correct for Nasdaq/NYSE-listed
//  common stock and ADSs, including foreign-domiciled names that list
//  directly on a US exchange (e.g. ASML, NXPI, ARM, MELI, PDD).
//
//  SNAPSHOT, NOT LIVE: constituents change via periodic index
//  reconstitutions (Nasdaq-100 annually each December, the Dow on an
//  as-needed basis). This list reflects each index's roster as of mid-2026
//  (cross-checked against slickcharts.com's live holdings pages). Like the
//  existing Nifty200/SMID lists, using this for a historical backtest is
//  subject to survivorship bias - see BacktestDiagnostics, which already
//  flags this automatically for any of these three universes.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace NseMomentumScanner.Backtesting
{
    internal static class Nasdaq100Universe
    {
        public static readonly IReadOnlyList<string> Tickers = new[]
        {
            "NVDA","AAPL","MSFT","AMZN","GOOGL","GOOG","AVGO","SPCX","META","TSLA",
            "MU","WMT","AMD","ASML","INTC","CSCO","AMAT","COST","LRCX","PLTR",
            "NFLX","ARM","PANW","TXN","KLAC","LIN","CRWD","SNDK","AMGN","MRVL",
            "STX","WDC","PEP","TMUS","ADI","QCOM","GILD","SHOP","BKNG","APP",
            "ISRG","PDD","FTNT","VRTX","SBUX","ADP","DDOG","ADBE","CEG","MELI",
            "CDNS","CSX","MNST","MAR","ABNB","INTU","CMCSA","DASH","CTAS","ROST",
            "HON","MDLZ","REGN","SNPS","ORLY","PCAR","AEP","HONA","LITE","MPWR",
            "WBD","TER","ALAB","BKR","NXPI","NBIS","FAST","FANG","EA","CRWV",
            "PYPL","ADSK","XEL","AXON","FER","IDXX","EXC","RKLB","CCEP","TRI",
            "ODFL","TTWO","MCHP","KDP","PAYX","WDAY","ROP","MSTR","DXCM","GEHC",
            "KHC","ALNY","CPRT",
        };
    }

    internal static class DowJonesUniverse
    {
        public static readonly IReadOnlyList<string> Tickers = new[]
        {
            "GS","CAT","MSFT","UNH","AMGN","TRV","GOOGL","V","SHW","JPM",
            "AXP","HD","AAPL","AMZN","MCD","JNJ","HON","BA","IBM","NVDA",
            "CVX","CRM","MMM","PG","MRK","CSCO","WMT","DIS","KO","NKE",
        };
    }

    /// <summary>Union of Nasdaq-100 + Dow Jones, deduplicated (there is real overlap -
    /// e.g. MSFT, AAPL, AMZN, GOOGL, NVDA, HON, AMGN, CSCO, WMT sit in both indices).</summary>
    internal static class UsMarketUniverse
    {
        public static readonly IReadOnlyList<string> Tickers =
            Nasdaq100Universe.Tickers
                .Concat(DowJonesUniverse.Tickers)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
