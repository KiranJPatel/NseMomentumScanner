using System;
using System.Collections.Generic;
using System.Linq;

namespace NseMomentumScanner
{
    /// <summary>
    /// Advanced contextual signal layer introduced in v4.5.
    /// It deliberately sits on top of the existing v4.4 indicator model so the
    /// legacy score remains available for continuity/backward-compatible reports.
    /// All calculations use bars at or before the scoring bar (no look-ahead).
    /// </summary>
    internal sealed class AdvancedSignalMetrics
    {
        public double TrendQuality { get; init; }
        public double StructureScore { get; init; }
        public double SetupScore { get; init; }
        public double TriggerScore { get; init; }
        public double VolumeQuality { get; init; }
        public double BreakoutQuality { get; init; }
        public double PullbackQuality { get; init; }
        public double QualityScore { get; init; }
        public double EntryScore { get; init; }
        public double RiskReward { get; init; }
        public double ScoreVelocity { get; init; }
        public double ScoreAcceleration { get; init; }
        public double AtrDistanceFromPivot { get; init; }
        public double RelativeExtensionQuality { get; init; }
        public int DistributionCount { get; init; }
        public bool IsBreakout { get; init; }
        public bool IsPullback { get; init; }
        public bool IsMomentumExhaustion { get; init; }
        public string StructureState { get; init; } = "RANGE";
        public string SetupState { get; init; } = "NONE";
        public string TriggerState { get; init; } = "NONE";
        public string SignalState { get; init; } = "WATCH";
    }

    internal static class SignalEnhancementEngine
    {
        public static AdvancedSignalMetrics Analyze(
            IReadOnlyList<StockQuote> quotes,
            int i,
            double hmaFast,
            double hmaSlow,
            double emaFast,
            double emaSlow,
            double adx,
            double pdi,
            double mdi,
            double volumeRatio,
            double atr,
            bool bullishSupertrend,
            bool vcpDetected,
            double legacyScore,
            double previousLegacyScore)
        {
            if (i <= 0 || i >= quotes.Count)
                return Neutral();

            int start = Math.Max(0, i - Config.StructureLookbackBars + 1);
            int lookback = i - start + 1;
            if (lookback < 8) return Neutral();

            var q = quotes[i];
            double close = (double)q.Close;
            double range = Math.Max(0.000001, (double)(q.High - q.Low));
            double clv = ((double)(q.Close - q.Low) - (double)(q.High - q.Close)) / range;

            // Trend quality: direction + slopes + separation, rather than binary crosses.
            double hmaSlopeFast = Slope(quotes, i, Config.SlopeLookbackBars, hmaFast, true);
            double hmaSlopeSlow = Slope(quotes, i, Config.SlopeLookbackBars, hmaSlow, false);
            double emaSlope = close > 0 && emaFast > 0 ? emaFast / Math.Max(1.0, emaSlow) : 0;
            double hmaSpread = hmaSlow > 0 ? (hmaFast - hmaSlow) / hmaSlow * 100.0 : 0;
            double trend = 50;
            trend += bullishSupertrend ? 15 : -15;
            trend += hmaFast > hmaSlow ? 10 : -10;
            trend += Math.Clamp(hmaSlopeFast * 1800, -15, 15);
            trend += Math.Clamp(hmaSlopeSlow * 1200, -10, 10);
            trend += Math.Clamp(hmaSpread * 2.0, -10, 10);
            trend += close > emaFast && emaFast > emaSlow ? 15 : (close < emaFast && emaFast < emaSlow ? -15 : 0);
            trend = Clamp100(trend);

            // Swing structure: classify HH/HL/LH/LL using local pivots. This is deliberately
            // simple and deterministic; it is not a future-looking fractal detector.
            var pivots = FindRecentPivots(quotes, start, i, Config.PivotStrength);
            string structureState = ClassifyStructure(pivots);
            double structure = structureState switch
            {
                "HH_HL" => 95,
                "HH" => 84,
                "HL" => 78,
                "LH_LL" => 15,
                "LL" => 20,
                "LH" => 30,
                _ => 50
            };

            // Relative volume and candle location. High volume is only bullish when the close
            // is also strong; high volume with a close near the low is treated as distribution.
            double volumeQuality = VolumeQualityScore(volumeRatio, clv);
            int distribution = CountDistribution(quotes, start, i, Config.DistributionLookbackBars);

            double priorRange = AverageRange(quotes, Math.Max(start, i - 10), i - 1);
            double currentRange = (double)(q.High - q.Low);
            double rangeExpansion = priorRange > 0 ? currentRange / priorRange : 1;

            bool breakout = IsBreakout(quotes, i, Config.BreakoutLookbackBars, out var pivot);
            bool pullback = IsPullback(quotes, i, emaFast, emaSlow, bullishSupertrend, structureState);

            double breakoutQuality = breakout
                ? Clamp100(45 + volumeQuality * 0.30 + trend * 0.20 + structure * 0.20 +
                           Math.Clamp(rangeExpansion * 12, 0, 20))
                : 0;

            double pullbackQuality = pullback
                ? Clamp100(40 + trend * 0.30 + volumeQuality * 0.20 + structure * 0.20 +
                           Math.Clamp((Config.MaxPullbackDepthPct - Math.Abs(PctFromEma(close, emaFast))) * 2, 0, 20))
                : 0;

            double vcpQuality = VcpQuality(quotes, start, i, volumeRatio, vcpDetected);
            double setup = Math.Max(vcpQuality, Math.Max(breakoutQuality, pullbackQuality));
            string setupState = vcpQuality >= 70 ? "VCP" : breakout ? "BREAKOUT" : pullback ? "PULLBACK" : "NONE";

            bool bullishTrigger = breakout && volumeRatio >= Config.BreakoutMinRvol && clv >= Config.MinBullishCloseLocation;
            bool reversalTrigger = structureState is "HH" or "HL" && pdi > mdi && adx >= Config.AdxTriggerThreshold && clv > 0;
            double trigger = setupState == "NONE" ? 0 : Clamp100(
                (bullishTrigger ? 55 : 25) +
                (reversalTrigger ? 15 : 0) +
                volumeQuality * 0.20 +
                trend * 0.10 +
                (clv + 1) * 10);
            string triggerState = bullishTrigger ? "BREAKOUT_CONFIRMED" : reversalTrigger ? "STRUCTURE_CONFIRM" : "WAIT";

            bool exhaustion = close > emaFast * (1.0 + Config.ExhaustionExtensionPct / 100.0) &&
                               adx >= Config.ExhaustionAdxThreshold && volumeRatio >= Config.ExhaustionRvolThreshold &&
                               clv < Config.ExhaustionCloseLocation;

            double extensionQuality = emaFast > 0
                ? Clamp100(100 - Math.Max(0, ((close - emaFast) / emaFast * 100.0)) * Config.ExtensionPenaltyPerPct)
                : 50;

            double rr = 0;
            if (atr > 0 && close > 0)
            {
                double stopDistance = Math.Max(atr * Config.LiveAtrStopMultiplier, close * Config.MinimumRiskPct / 100.0);
                double target = 0;
                if (breakout && pivot > 0)
                    target = Math.Max(close + stopDistance * Config.LiveRiskRewardRatio,
                                      pivot + Math.Max(atr * Config.BreakoutTargetAtr, stopDistance * Config.LiveRiskRewardRatio));
                else
                    target = close + stopDistance * Config.LiveRiskRewardRatio;
                rr = stopDistance > 0 ? Math.Max(0, (target - close) / stopDistance) : 0;
            }

            double velocity = legacyScore - previousLegacyScore;
            double acceleration = velocity - PreviousVelocity(quotes, i, emaFast, emaSlow);

            double quality = Clamp100(
                trend * 0.28 + structure * 0.24 + volumeQuality * 0.14 +
                vcpQuality * 0.14 + extensionQuality * 0.10 +
                Math.Clamp(50 + velocity * 2.0, 0, 100) * 0.10);

            double entry = Clamp100(
                (setup > 0 ? setup * 0.30 : 20) +
                trigger * 0.30 +
                volumeQuality * 0.15 +
                Math.Min(100, rr / Math.Max(0.1, Config.MinimumRiskReward) * 100) * 0.15 +
                Math.Clamp(50 + velocity * 3.0, 0, 100) * 0.10);

            string signal = quality >= Config.StrongBuyQualityScore && entry >= Config.StrongBuyEntryScore &&
                            rr >= Config.MinimumRiskReward && !exhaustion ? "STRONG_BUY" :
                            quality >= Config.MinQualityScore && entry >= Config.MinEntryScore && rr >= Config.MinimumRiskReward && !exhaustion ? "BUY" :
                            setup >= Config.MinSetupScore ? "SETUP" :
                            exhaustion || distribution >= Config.DistributionWarningCount ? "WEAKENING" : "WATCH";

            return new AdvancedSignalMetrics
            {
                TrendQuality = Math.Round(trend, 2),
                StructureScore = Math.Round(structure, 2),
                SetupScore = Math.Round(setup, 2),
                TriggerScore = Math.Round(trigger, 2),
                VolumeQuality = Math.Round(volumeQuality, 2),
                BreakoutQuality = Math.Round(breakoutQuality, 2),
                PullbackQuality = Math.Round(pullbackQuality, 2),
                QualityScore = Math.Round(quality, 2),
                EntryScore = Math.Round(entry, 2),
                RiskReward = Math.Round(rr, 2),
                ScoreVelocity = Math.Round(velocity, 2),
                ScoreAcceleration = Math.Round(acceleration, 2),
                AtrDistanceFromPivot = pivot > 0 && atr > 0 ? Math.Round((close - pivot) / atr, 2) : 0,
                RelativeExtensionQuality = Math.Round(extensionQuality, 2),
                DistributionCount = distribution,
                IsBreakout = breakout,
                IsPullback = pullback,
                IsMomentumExhaustion = exhaustion,
                StructureState = structureState,
                SetupState = setupState,
                TriggerState = triggerState,
                SignalState = signal,
            };
        }

        private static double PreviousVelocity(IReadOnlyList<StockQuote> q, int i, double ef, double es)
        {
            if (i < 2) return 0;
            double c0 = (double)q[i].Close;
            double c1 = (double)q[i - 1].Close;
            if (c1 <= 0) return 0;
            return (c0 - c1) / c1 * 100.0;
        }

        private static double Slope(IReadOnlyList<StockQuote> q, int i, int bars, double current, bool fast)
        {
            if (i < bars || current <= 0) return 0;
            // Price-normalized slope is used as a proxy when the exact historical HMA array
            // is not passed through. This keeps the enhancement layer independent of Skender's
            // result object implementation while remaining deterministic.
            double now = (double)q[i].Close;
            double old = (double)q[i - bars].Close;
            return old > 0 ? (now - old) / old / bars : 0;
        }

        private static double VolumeQualityScore(double rvol, double clv)
        {
            double baseScore = rvol switch
            {
                >= 2.5 => 95,
                >= 1.8 => 88,
                >= 1.3 => 75,
                >= 1.0 => 58,
                >= 0.7 => 40,
                _ => 20
            };
            baseScore += clv * 15;
            return Clamp100(baseScore);
        }

        private static double VcpQuality(IReadOnlyList<StockQuote> q, int start, int i, double rvol, bool detected)
        {
            if (!detected && i - start < 40) return 0;
            int n = Math.Min(60, i - start + 1);
            if (n < 20) return detected ? 55 : 0;
            double oldRange = AverageRange(q, Math.Max(start, i - n + 1), Math.Max(start, i - n / 2));
            double newRange = AverageRange(q, Math.Max(start, i - n / 2 + 1), i);
            double compression = oldRange > 0 ? newRange / oldRange : 1;
            double dryup = rvol <= 0 ? 0 : Math.Clamp(1.0 / rvol, 0, 1);
            double score = detected ? 65 : 25;
            score += Math.Clamp((1.0 - compression) * 100, 0, 25);
            score += dryup * 10;
            return Clamp100(score);
        }

        private static bool IsBreakout(IReadOnlyList<StockQuote> q, int i, int lookback, out double pivot)
        {
            pivot = 0;
            if (i < lookback) return false;
            int from = Math.Max(0, i - lookback);
            double priorHigh = 0;
            for (int j = from; j < i; j++) priorHigh = Math.Max(priorHigh, (double)q[j].High);
            pivot = priorHigh;
            return priorHigh > 0 && (double)q[i].Close > priorHigh &&
                   (double)q[i].Close <= priorHigh * (1.0 + Config.MaxBreakoutDistancePct / 100.0);
        }

        private static bool IsPullback(IReadOnlyList<StockQuote> q, int i, double emaFast, double emaSlow,
            bool bullSt, string structure)
        {
            if (!bullSt || emaFast <= 0 || emaSlow <= 0 || emaFast <= emaSlow) return false;
            double close = (double)q[i].Close;
            double dist = Math.Abs((close - emaFast) / emaFast * 100.0);
            if (dist > Config.MaxPullbackDepthPct) return false;
            if (structure is "LH_LL" or "LL") return false;
            return i >= 2 && (double)q[i].Close >= (double)q[i - 1].Close;
        }

        private static List<(int Index, bool High, double Price)> FindRecentPivots(IReadOnlyList<StockQuote> q, int start, int end, int strength)
        {
            var result = new List<(int Index, bool High, double Price)>();
            for (int i = Math.Max(start + strength, strength); i <= end - 1; i++)
            {
                bool high = true, low = true;
                double h = (double)q[i].High, l = (double)q[i].Low;
                for (int k = 1; k <= strength; k++)
                {
                    high &= h >= (double)q[i - k].High && h >= (double)q[Math.Min(end, i + k)].High;
                    low &= l <= (double)q[i - k].Low && l <= (double)q[Math.Min(end, i + k)].Low;
                }
                if (high) result.Add((i, true, h));
                if (low) result.Add((i, false, l));
            }
            return result.OrderBy(x => x.Index).TakeLast(8).ToList();
        }

        private static string ClassifyStructure(List<(int Index, bool High, double Price)> p)
        {
            var highs = p.Where(x => x.High).TakeLast(2).ToArray();
            var lows = p.Where(x => !x.High).TakeLast(2).ToArray();
            if (highs.Length < 2 || lows.Length < 2) return "RANGE";
            bool hh = highs[1].Price > highs[0].Price;
            bool hl = lows[1].Price > lows[0].Price;
            bool lh = highs[1].Price < highs[0].Price;
            bool ll = lows[1].Price < lows[0].Price;
            if (hh && hl) return "HH_HL";
            if (lh && ll) return "LH_LL";
            if (hh) return "HH";
            if (hl) return "HL";
            if (lh) return "LH";
            if (ll) return "LL";
            return "RANGE";
        }

        private static int CountDistribution(IReadOnlyList<StockQuote> q, int start, int end, int lookback)
        {
            int from = Math.Max(start, end - lookback + 1), count = 0;
            for (int i = from; i <= end; i++)
            {
                double range = Math.Max(0.000001, (double)(q[i].High - q[i].Low));
                double clv = ((double)(q[i].Close - q[i].Low) - (double)(q[i].High - q[i].Close)) / range;
                double priorVol = i > 0 ? (double)q[i - 1].Volume : 0;
                if (q[i].Volume > (decimal)(priorVol * Config.DistributionRvolThreshold) && clv <= Config.DistributionCloseLocation)
                    count++;
            }
            return count;
        }

        private static double AverageRange(IReadOnlyList<StockQuote> q, int start, int end)
        {
            if (end < start) return 0;
            double sum = 0; int n = 0;
            for (int i = start; i <= end; i++) { sum += (double)(q[i].High - q[i].Low); n++; }
            return n == 0 ? 0 : sum / n;
        }

        private static double PctFromEma(double close, double ema) => ema > 0 ? (close - ema) / ema * 100.0 : 0;
        private static double Clamp100(double v) => Math.Clamp(v, 0, 100);

        private static AdvancedSignalMetrics Neutral() => new()
        {
            TrendQuality = 50, StructureScore = 50, SetupScore = 0, TriggerScore = 0,
            VolumeQuality = 50, QualityScore = 50, EntryScore = 20, RiskReward = 0,
            SignalState = "WATCH", StructureState = "RANGE", SetupState = "NONE", TriggerState = "NONE"
        };
    }
}
