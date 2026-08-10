// =============================================================================
//  BACKTESTING MODULE - DailySeriesIndex
// -----------------------------------------------------------------------------
//  Wraps one symbol's daily StockQuote series with O(1)/O(log n) lookups the
//  engine needs every simulated day: "the bar on this exact date", "the bar on
//  or before this date" (for mark-to-market of a stock that didn't trade
//  today), and "the next bar after this date" (for next-open execution).
//  Computes only the indicators the backtest's exit rules actually read
//  (Supertrend, HMA-fast/slow, ATR) directly via the same Skender calls and
//  Config periods the scanner's own IndicatorArrays.Compute() uses - see the
//  perf note on SymbolDailySeries below for why this is deliberately NOT a
//  call to the scanner's "compute everything" IndicatorArrays.Compute().
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Skender.Stock.Indicators;
using NseMomentumScanner;

namespace NseMomentumScanner.Backtesting
{
    /// <summary>Per-symbol daily series + only the indicators the backtest's exit rules
    /// actually use (Supertrend, HMA-fast/slow, ATR) — computed directly via the same
    /// Skender calls and Config periods the scanner's own IndicatorArrays.Compute() uses
    /// internally, so values are identical to what the scanner would show. Deliberately
    /// does NOT call the scanner's full IndicatorArrays.Compute() (which also computes
    /// ADX, EMA fast/slow and a volume SMA) since none of those are used for exit
    /// evaluation - computing them here would be pure wasted work, doubled again by
    /// TickerScorer's own (separate, necessary) call to IndicatorArrays.Compute() when
    /// "1d" is also a configured scoring timeframe.</summary>
    internal sealed class SymbolDailySeries
    {
        public List<StockQuote> Quotes { get; }
        public SuperTrendResult[] SuperTrend { get; }
        public HmaResult[] HmaFast { get; }
        public HmaResult[] HmaSlow { get; }
        public AtrResult[] Atr { get; }
        private readonly Dictionary<DateTime, int> _exactIndex;

        public SymbolDailySeries(List<StockQuote> quotes, int atrPeriod)
        {
            Quotes = quotes;
            SuperTrend = quotes.GetSuperTrend(Config.SupertrendPeriod, Config.SupertrendMultiplier).ToArray();
            HmaFast = quotes.GetHma(Config.FastHmaPeriod).ToArray();
            HmaSlow = quotes.GetHma(Config.SlowHmaPeriod).ToArray();
            Atr = quotes.GetAtr(atrPeriod).ToArray();

            _exactIndex = new Dictionary<DateTime, int>(quotes.Count);
            for (int i = 0; i < quotes.Count; i++)
                _exactIndex[quotes[i].Date.Date] = i; // last one wins if duplicate dates ever occur
        }

        public int? ExactIndex(DateTime date) =>
            _exactIndex.TryGetValue(date.Date, out var i) ? i : (int?)null;

        /// <summary>Largest index whose Date &lt;= <paramref name="date"/>, or null if none.</summary>
        public int? IndexOnOrBefore(DateTime date)
        {
            int lo = 0, hi = Quotes.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Quotes[mid].Date.Date <= date.Date) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans >= 0 ? ans : (int?)null;
        }

        /// <summary>Smallest index whose Date &gt; <paramref name="date"/>, or null if none
        /// (used to find "tomorrow's open" for next-bar execution).</summary>
        public int? IndexOfNextAfter(DateTime date)
        {
            int lo = 0, hi = Quotes.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Quotes[mid].Date.Date > date.Date) { ans = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            return ans >= 0 ? ans : (int?)null;
        }

        public decimal CloseOnOrBefore(DateTime date)
        {
            var i = IndexOnOrBefore(date);
            return i.HasValue ? Quotes[i.Value].Close : 0m;
        }

        public decimal AtrAt(int idx)
        {
            if (idx < 0 || idx >= Atr.Length) return 0m;
            var a = Atr[idx].Atr;
            return a.HasValue ? (decimal)a.Value : 0m;
        }

        /// <summary>Supertrend-bullish / HMA-fast / HMA-slow at a given index, matching
        /// exactly how TickerScorer itself reads the same arrays.</summary>
        public (bool? bullish, double? hmaFast, double? hmaSlow) IndicatorsAt(int idx)
        {
            bool? bull = (idx >= 0 && idx < SuperTrend.Length)
                ? SuperTrend[idx].UpperBand == null
                : (bool?)null;
            double? hf = (idx >= 0 && idx < HmaFast.Length) ? HmaFast[idx].Hma : null;
            double? hs = (idx >= 0 && idx < HmaSlow.Length) ? HmaSlow[idx].Hma : null;
            return (bull, hf, hs);
        }
    }

    /// <summary>Thread-safe: populated concurrently by Parallel.ForEachAsync during the
    /// fetch/score phase (one writer per distinct symbol key, many symbols in flight).</summary>
    internal sealed class DailySeriesIndex
    {
        private readonly ConcurrentDictionary<string, SymbolDailySeries> _map = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string symbol, SymbolDailySeries series) => _map[symbol] = series;
        public bool TryGet(string symbol, out SymbolDailySeries series) => _map.TryGetValue(symbol, out series!);
        public IEnumerable<string> Symbols => _map.Keys;
        public int Count => _map.Count;
    }
}
