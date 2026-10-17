namespace NseMomentumScanner;


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Skender.Stock.Indicators;

internal static class ChartAnalytics
{
    public sealed record ReferenceLevels(
        decimal CurrentDayHigh, decimal CurrentDayLow,
        decimal PreviousDayHigh, decimal PreviousDayLow,
        decimal CurrentWeekHigh, decimal CurrentWeekLow,
        decimal High52Week, decimal Low52Week);

    public static JsonObject Build(
        List<StockQuote> quotes,
        List<StockQuote>? dailyReferenceQuotes = null,
        bool isIntraday = false,
        bool lastBarPartial = false)
    {
        var ind = IndicatorArrays.Compute(quotes);
        var levels = BuildLevels(
            dailyReferenceQuotes is { Count: > 0 } ? dailyReferenceQuotes : quotes);

        var root = new JsonObject
        {
            ["levels"] = new JsonObject
            {
                ["currentDayHigh"] = (double)levels.CurrentDayHigh,
                ["currentDayLow"] = (double)levels.CurrentDayLow,
                ["previousDayHigh"] = (double)levels.PreviousDayHigh,
                ["previousDayLow"] = (double)levels.PreviousDayLow,
                ["weeklyHigh"] = (double)levels.CurrentWeekHigh,
                ["weeklyLow"] = (double)levels.CurrentWeekLow,
                ["high52Week"] = (double)levels.High52Week,
                ["low52Week"] = (double)levels.Low52Week
            },
            ["structures"] = BuildStructures(quotes, ind.Atr, isIntraday),
            ["fvg"] = BuildFvg(quotes, ind.Atr, isIntraday),
            ["volumeProfile"] = BuildVolumeProfile(quotes, 28),
            ["volumeProfileStats"] = BuildVolumeProfileStats(quotes, 28),
            ["volumePrice"] = BuildVolumePriceConfirmation(quotes, lastBarPartial),
            ["vcp"] = BuildVcp(quotes)
        };
        root["tradePlan"] = BuildTradePlan(quotes, ind.Atr, (JsonObject)root["structures"]!, levels, lastBarPartial, isIntraday, @short: false);
        root["tradePlanShort"] = BuildTradePlan(quotes, ind.Atr, (JsonObject)root["structures"]!, levels, lastBarPartial, isIntraday, @short: true);

        return root;
    }

    private static ReferenceLevels BuildLevels(IReadOnlyList<StockQuote> q)
    {
        if (q.Count == 0) return new ReferenceLevels(0, 0, 0, 0, 0, 0, 0, 0);

        var days = q.GroupBy(x => x.Date.Date).OrderBy(g => g.Key).ToList();
        var today = days[^1];
        var prev = days.Count > 1 ? days[^2] : null;

        var weekStart = today.Key.AddDays(-(((int)today.Key.DayOfWeek + 6) % 7));
        var week = days.Where(g => g.Key >= weekStart).SelectMany(g => g).ToList();

        var last365 = q.Where(x => x.Date.Date >= today.Key.AddDays(-364)).ToList();
        return new ReferenceLevels(
            today.Max(x => x.High), today.Min(x => x.Low),
            prev?.Max(x => x.High) ?? 0, prev?.Min(x => x.Low) ?? 0,
            week.Count > 0 ? week.Max(x => x.High) : today.Max(x => x.High),
            week.Count > 0 ? week.Min(x => x.Low) : today.Min(x => x.Low),
            last365.Count > 0 ? last365.Max(x => x.High) : today.Max(x => x.High),
            last365.Count > 0 ? last365.Min(x => x.Low) : today.Min(x => x.Low));
    }

    private const int PivotLeft = 3, PivotRight = 3;

    /// <summary>
    /// Market structure, computed in ONE chronological pass so every list it emits is
    /// time-ordered and the BOS/CHoCH trend state is consistent:
    ///   · swing pivots are confirmed PivotRight bars after they form (no repainting);
    ///   · a BOS/CHoCH fires on the first CLOSE beyond the most recent unbroken swing;
    ///   · CHoCH = break against the prevailing trend, BOS = break with it.
    /// Support/resistance are the nearest clustered swing levels BELOW / ABOVE the last
    /// close (a swing high price has already closed above is no longer resistance).
    /// </summary>
    private static JsonObject BuildStructures(IReadOnlyList<StockQuote> q, Skender.Stock.Indicators.AtrResult[] atr, bool isIntraday)
    {
        int n = q.Count;
        var pivots = new JsonArray();
        var events = new JsonArray();
        var sr = new JsonArray();

        // Strict on the left, inclusive on the right: a flat double-top yields ONE pivot.
        var swingHighs = new List<(int i, decimal p)>();
        var swingLows = new List<(int i, decimal p)>();
        for (int i = PivotLeft; i < n - PivotRight; i++)
        {
            bool hi = true, lo = true;
            for (int j = i - PivotLeft; j <= i + PivotRight && (hi || lo); j++)
            {
                if (j == i) continue;
                if (j < i) { hi &= q[i].High > q[j].High; lo &= q[i].Low < q[j].Low; }
                else       { hi &= q[i].High >= q[j].High; lo &= q[i].Low <= q[j].Low; }
            }
            if (hi) swingHighs.Add((i, q[i].High));
            if (lo) swingLows.Add((i, q[i].Low));
        }

        // Classify HH/LH and HL/LL against the previous pivot on the same side, then merge
        // both sides into one chronologically sorted list.
        var pivotRows = new List<(int i, decimal p, string type, string side)>();
        for (int k = 0; k < swingHighs.Count; k++)
        {
            var h = swingHighs[k];
            string type = k == 0 ? "SH" : h.p > swingHighs[k - 1].p ? "HH" : "LH";
            pivotRows.Add((h.i, h.p, type, "H"));
        }
        for (int k = 0; k < swingLows.Count; k++)
        {
            var l = swingLows[k];
            string type = k == 0 ? "SL" : l.p > swingLows[k - 1].p ? "HL" : "LL";
            pivotRows.Add((l.i, l.p, type, "L"));
        }
        pivotRows.Sort((x, y) => x.i != y.i ? x.i.CompareTo(y.i) : string.CompareOrdinal(x.side, y.side));
        foreach (var p in pivotRows)
            pivots.Add(Pivot(q[p.i], p.i, p.p, p.type, p.side, isIntraday));

        // Chronological BOS / CHoCH state machine.
        string trend = "RANGE";
        (int i, decimal p)? activeHigh = null, activeLow = null;
        int pr = 0;
        for (int j = 0; j < n; j++)
        {
            // register pivots that become confirmed on this bar
            while (pr < pivotRows.Count && pivotRows[pr].i + PivotRight <= j)
            {
                var pv = pivotRows[pr++];
                if (pv.side == "H") activeHigh = (pv.i, pv.p); else activeLow = (pv.i, pv.p);
            }
            if (activeHigh is { } ah && q[j].Close > ah.p)
            {
                string ev = trend == "DOWN" ? "CHoCH_UP" : "BOS_UP";
                trend = "UP";
                events.Add(new JsonObject { ["t"] = Epoch(q[j], isIntraday), ["price"] = (double)ah.p, ["type"] = ev,
                                            ["pivotT"] = Epoch(q[ah.i], isIntraday) });
                activeHigh = null;
            }
            else if (activeLow is { } al && q[j].Close < al.p)
            {
                string ev = trend == "UP" ? "CHoCH_DOWN" : "BOS_DOWN";
                trend = "DOWN";
                events.Add(new JsonObject { ["t"] = Epoch(q[j], isIntraday), ["price"] = (double)al.p, ["type"] = ev,
                                            ["pivotT"] = Epoch(q[al.i], isIntraday) });
                activeLow = null;
            }
        }

        // Support / resistance: cluster recent swing levels, then split around the last close.
        decimal last = n > 0 ? q[n - 1].Close : 0;
        double lastAtr = 0;
        for (int i = Math.Min(n, atr.Length) - 1; i >= 0; i--)
            if (atr[i].Atr is double av && av > 0) { lastAtr = av; break; }
        double tol = lastAtr > 0 ? lastAtr * 0.5 : (double)last * 0.006;

        var cand = new List<(decimal price, int idx, bool high, double quality)>();
        int minIdx = Math.Max(0, n - 250);
        foreach (var h in swingHighs) if (h.i >= minIdx) cand.Add((h.p, h.i, true, ScoreSwing(q, h.i, true, atr)));
        foreach (var l in swingLows)  if (l.i >= minIdx) cand.Add((l.p, l.i, false, ScoreSwing(q, l.i, false, atr)));
        cand.Sort((x, y) => x.price.CompareTo(y.price));

        var zones = new List<(decimal price, int touches, int lastIdx, double quality)>();
        int c0 = 0;
        while (c0 < cand.Count)
        {
            int c1 = c0;
            while (c1 + 1 < cand.Count && (double)(cand[c1 + 1].price - cand[c1].price) <= tol) c1++;
            var grp = cand.Skip(c0).Take(c1 - c0 + 1).ToList();
            decimal avgPrice = grp.Average(x => x.price);
            int touches = grp.Count;
            int lastIdx = grp.Max(x => x.idx);
            double quality = Math.Clamp(grp.Average(x => x.quality) + 8 * (touches - 1)
                                        + (lastIdx >= n - 60 ? 4 : 0), 0, 100);
            zones.Add((avgPrice, touches, lastIdx, quality));
            c0 = c1 + 1;
        }
        foreach (var z in zones.Where(z => z.price > last).OrderBy(z => z.price).Take(3))
            sr.Add(new JsonObject { ["type"] = "resistance", ["price"] = (double)Math.Round(z.price, 2),
                                    ["quality"] = Math.Round(z.quality, 1), ["touches"] = z.touches });
        foreach (var z in zones.Where(z => z.price <= last).OrderByDescending(z => z.price).Take(3))
            sr.Add(new JsonObject { ["type"] = "support", ["price"] = (double)Math.Round(z.price, 2),
                                    ["quality"] = Math.Round(z.quality, 1), ["touches"] = z.touches });

        string lastHighType = pivotRows.LastOrDefault(x => x.side == "H").type ?? "SH";
        string lastLowType  = pivotRows.LastOrDefault(x => x.side == "L").type ?? "SL";

        return new JsonObject
        {
            ["pivots"] = pivots,
            ["events"] = events,
            ["trend"] = trend,
            ["swingState"] = lastHighType + "/" + lastLowType,
            ["swingQuality"] = sr.Count == 0 ? 0 : Math.Round(sr.Select(x => ((JsonObject)x!)["quality"]!.GetValue<double>()).Average(), 1),
            ["sr"] = sr
        };
    }

    /// <summary>
    /// Daily long + short breakout plans for one symbol, for the scan-time sidecar file that feeds the
    /// Buy / Sell / Score-matrix tables. Same maths as the Charts tab, evaluated on the scan's daily bars.
    /// </summary>
    public static (JsonObject Long, JsonObject Short) BuildPlansForScan(List<StockQuote> quotes, bool lastBarPartial)
    {
        var ind = IndicatorArrays.Compute(quotes);
        var levels = BuildLevels(quotes);
        var st = BuildStructures(quotes, ind.Atr, false);
        return (BuildTradePlan(quotes, ind.Atr, st, levels, lastBarPartial, false, @short: false),
                BuildTradePlan(quotes, ind.Atr, st, levels, lastBarPartial, false, @short: true));
    }

    private static double Tick(double price) => Math.Round(price / 0.05) * 0.05;

    /// <summary>
    /// Breakout (LONG) / breakdown (SHORT) plan.
    ///   LONG : entry = close through the nearest overhead resistance (+0.1 ATR); stop = last confirmed swing low
    ///          below the trigger (−0.25 ATR); targets above.
    ///   SHORT: mirror image — close through the nearest support (−0.1 ATR); stop = last swing high above the
    ///          trigger (+0.25 ATR); targets below.
    /// Risk is kept between the scanner's own floor (max(1 ATR, MinimumRiskPct), daily+ only) and ceiling
    /// (LiveAtrStopMultiplier × ATR). The next opposing structure level is checked against MinimumRiskReward.
    /// Share count is NOT computed here — capital and risk % belong to the user's browser.
    /// </summary>
    private static JsonObject BuildTradePlan(IReadOnlyList<StockQuote> q, Skender.Stock.Indicators.AtrResult[] atr,
                                             JsonObject structures, ReferenceLevels levels, bool lastBarPartial, bool isIntraday, bool @short = false)
    {
        bool sh = @short;
        double dir = sh ? -1 : 1;
        string side = sh ? "SHORT" : "LONG";
        JsonObject NoPlan(string reason) => new() { ["state"] = "NO_PLAN", ["reason"] = reason, ["side"] = side };
        int n = q.Count;
        if (n < 30) return NoPlan("Not enough history.");
        double a = 0;
        for (int i = Math.Min(n, atr.Length) - 1; i >= 0; i--) if (atr[i].Atr is double av && av > 0) { a = av; break; }
        if (a <= 0) return NoPlan("ATR unavailable.");
        string trend = structures["trend"]?.GetValue<string>() ?? "RANGE";
        if (!sh && trend == "DOWN") return NoPlan("Structure is DOWN (last break was a lower low) — no long breakout plan.");
        if (sh && trend == "UP") return NoPlan("Structure is UP (last break was a higher high) — no short breakdown plan.");

        double last = (double)q[n - 1].Close;
        var zones = structures["sr"]!.AsArray().Select(x => x!.AsObject())
                        .Where(x => x["type"]!.GetValue<string>() == (sh ? "support" : "resistance"))
                        .Select(x => x["price"]!.GetValue<double>()).ToList();
        if (sh)
        {
            double l52 = (double)levels.Low52Week;
            if (l52 > 0 && l52 < last && !zones.Any(z => Math.Abs(z - l52) <= a * 0.5)) zones.Add(l52);
            zones = zones.OrderByDescending(x => x).ToList();      // nearest support first (highest below price)
        }
        else
        {
            double h52 = (double)levels.High52Week;
            if (h52 > last && !zones.Any(z => Math.Abs(z - h52) <= a * 0.5)) zones.Add(h52);
            zones.Sort();                                           // nearest resistance first (lowest above price)
        }
        if (zones.Count == 0)
            return NoPlan(sh ? "No support below — price is at/below every recent swing low (free-fall); define a manual trigger."
                             : "No overhead resistance — price is at/above every recent swing high (blue sky); define a manual trigger.");

        double level = zones[0];
        double trigger = Tick(level - dir * 0.1 * a);
        double distPct = -dir * (last - trigger) / last * 100.0;    // positive = price has not reached the trigger yet

        double structStop = double.NaN;
        foreach (var pv in structures["pivots"]!.AsArray().Reverse())
        {
            var o = pv!.AsObject();
            if (o["side"]!.GetValue<string>() != (sh ? "H" : "L")) continue;
            double price = (double)o["p"]!.GetValue<decimal>();
            if (sh ? price > trigger : price < trigger) { structStop = price + dir * -0.25 * a; break; }
        }
        // The scanner's 2% minimum-risk floor is a swing-trade concept; on intraday charts it would dwarf the ATR.
        double lo = isIntraday ? 1.0 * a : Math.Max(1.0 * a, trigger * Config.MinimumRiskPct / 100.0);
        double hi = Math.Max(a * Config.LiveAtrStopMultiplier, lo);
        double risk = double.IsNaN(structStop) ? hi : Math.Clamp(Math.Abs(trigger - structStop), lo, hi);
        double stop = Tick(trigger - dir * risk);
        risk = Math.Abs(trigger - stop);

        var targets = new JsonArray();
        foreach (var r in new[] { 1.5, Config.MinimumRiskReward, Config.LiveRiskRewardRatio, 3.0 }.Distinct().OrderBy(x => x).Take(3))
            targets.Add(new JsonObject { ["label"] = $"T{targets.Count + 1}", ["r"] = Math.Round(r, 2), ["price"] = Math.Round(Tick(trigger + dir * r * risk), 2) });

        double? next = zones.Where(z => dir * (z - trigger) > 0.5 * a).Cast<double?>().FirstOrDefault();
        double? nextR = next is double nx ? Math.Round(Math.Abs(nx - trigger) / risk, 2) : null;

        // Volume of the last COMPLETED bar vs its 20-bar average (a forming bar's volume is not comparable).
        int ci = lastBarPartial ? n - 2 : n - 1;
        double rvolNow = 0;
        if (ci >= 20) { double avg = q.Skip(ci - 20).Take(20).Average(x => (double)x.Volume); rvolNow = avg > 0 ? (double)q[ci].Volume / avg : 0; }

        string state, reason;
        string word = sh ? "below" : "above", through = sh ? "breakdown" : "breakout";
        double lastDone = (double)q[ci].Close;
        bool doneThrough = dir * (lastDone - trigger) >= 0, liveThrough = dir * (last - trigger) >= 0;
        string nextName = sh ? "support" : "resistance";
        if (nextR is double nr && nr < Config.MinimumRiskReward)
        { state = "BLOCKED"; reason = $"Next {nextName} {next:F2} is only {nr:F1}R {(sh ? "below" : "above")} the trigger (< {Config.MinimumRiskReward:F1}R minimum)."; }
        else if (doneThrough)
        {
            double ext = dir * (last - trigger) / a;
            if (ext > 0.5) { state = "EXTENDED"; reason = $"Price is {ext:F1} ATR beyond the trigger — do not chase; wait for a retest or a new base."; }
            else if (lastBarPartial && liveThrough) { state = "TRIGGERED"; reason = "Through the trigger on a forming candle — confirm at the close."; }
            else { state = "TRIGGERED"; reason = rvolNow >= Config.BreakoutMinRvol ? $"Closed through the trigger on {rvolNow:F1}× volume." : $"Closed through the trigger but volume is only {rvolNow:F1}× (needs ≥ {Config.BreakoutMinRvol:F1}×) — weak {through}."; }
        }
        else if (liveThrough && lastBarPartial)
        { state = "TRIGGERED"; reason = "Trading through the trigger on a forming candle — confirm at the close."; }
        else if (distPct <= (isIntraday ? Math.Min(Config.MaxBreakoutDistancePct, 150.0 * a / last) : Config.MaxBreakoutDistancePct))
        { state = "ARMED"; reason = $"{distPct:F1}% {(sh ? "above" : "below")} the trigger. Take it only on a close {word} {trigger:F2} with ≥ {Config.BreakoutMinRvol:F1}× volume."; }
        else
        { state = "WATCH"; reason = $"{distPct:F1}% {(sh ? "above" : "below")} the trigger — not actionable yet."; }

        return new JsonObject
        {
            ["side"] = side, ["state"] = state, ["reason"] = reason,
            ["trigger"] = Math.Round(trigger, 2), ["stop"] = Math.Round(stop, 2), ["level"] = Math.Round(level, 2),
            ["riskPerShare"] = Math.Round(risk, 2), ["riskPct"] = Math.Round(risk / trigger * 100, 2),
            ["atr"] = Math.Round(a, 2), ["distancePct"] = Math.Round(distPct, 2),
            ["targets"] = targets, ["nextResistance"] = next is double nn ? Math.Round(nn, 2) : null, ["nextResistanceR"] = nextR,
            ["rvolNeeded"] = Config.BreakoutMinRvol, ["rvolNow"] = Math.Round(rvolNow, 2), ["lastBarPartial"] = lastBarPartial,
            ["invalidation"] = sh ? $"Plan void on a close above {stop:F2}, or a bullish CHoCH before the trigger is hit."
                                  : $"Plan void on a close below {stop:F2}, or a bearish CHoCH before the trigger is hit."
        };
    }

    private static JsonObject Pivot(StockQuote q, int i, decimal p, string type, string side, bool isIntraday) =>
        new() { ["i"] = i, ["t"] = Epoch(q, isIntraday), ["p"] = p, ["type"] = type, ["side"] = side };

    private static double ScoreSwing(IReadOnlyList<StockQuote> q, int i, bool high, Skender.Stock.Indicators.AtrResult[] atr)
    {
        int start = Math.Max(0, i - 5), end = Math.Min(q.Count - 1, i + 5);
        decimal range = q.Skip(start).Take(end - start + 1).Max(x => x.High) -
                        q.Skip(start).Take(end - start + 1).Min(x => x.Low);
        double a = i < atr.Length && atr[i].Atr is double av ? av : 0;
        if (a <= 0) return 50;
        double rejection = high ? (double)(q[i].High - q[i].Close) : (double)(q[i].Close - q[i].Low);
        return Math.Clamp(50 + rejection / a * 20 + (double)range / (double)a * 3, 0, 100);
    }

    private static JsonArray BuildFvg(IReadOnlyList<StockQuote> q, Skender.Stock.Indicators.AtrResult[] atr, bool isIntraday)
    {
        var result = new JsonArray();
        int start = Math.Max(2, q.Count - 100);
        for (int i = start; i < q.Count; i++)
        {
            double a = i < atr.Length && atr[i].Atr is double x ? x : 0;
            if (a <= 0) continue;
            if (q[i].Low > q[i - 2].High && (double)(q[i].Low - q[i - 2].High) >= a * 0.05)
                result.Add(Fvg("bull", q[i - 2].High, q[i].Low, q[i - 2], q[i], q, i, isIntraday));
            else if (q[i].High < q[i - 2].Low && (double)(q[i - 2].Low - q[i].High) >= a * 0.05)
                result.Add(Fvg("bear", q[i].High, q[i - 2].Low, q[i - 2], q[i], q, i, isIntraday));
        }
        return result;
    }

    private static JsonObject Fvg(string type, decimal from, decimal to, StockQuote a, StockQuote c, IReadOnlyList<StockQuote> q, int endIndex, bool isIntraday)
    {
        long? fill = null;
        int start = endIndex + 1;
        for (int i = start; i < q.Count; i++)
        {
            if ((type == "bull" && q[i].Low <= from) || (type == "bear" && q[i].High >= to))
            { fill = Epoch(q[i], isIntraday); break; }
        }
        return new JsonObject { ["type"] = type, ["from"] = (double)from, ["to"] = (double)to,
            ["start"] = Epoch(a, isIntraday), ["end"] = Epoch(c, isIntraday), ["fill"] = fill };
    }

    private static (double[] vol, decimal lo, decimal step)? ProfileBins(IReadOnlyList<StockQuote> q, int bins)
    {
        if (q.Count == 0) return null;
        var bars = q.TakeLast(Math.Min(150, q.Count)).ToList();
        decimal lo = bars.Min(x => x.Low), hi = bars.Max(x => x.High);
        if (hi <= lo) return null;
        var vol = new double[bins];
        decimal step = (hi - lo) / bins;
        foreach (var b in bars)
        {
            int a = Math.Clamp((int)((b.Low - lo) / step), 0, bins - 1);
            int z = Math.Clamp((int)((b.High - lo) / step), 0, bins - 1);
            double share = (double)b.Volume / Math.Max(1, z - a + 1);
            for (int i = a; i <= z; i++) vol[i] += share;
        }
        return (vol, lo, step);
    }

    private static JsonArray BuildVolumeProfile(IReadOnlyList<StockQuote> q, int bins)
    {
        var outp = new JsonArray();
        if (ProfileBins(q, bins) is not { } pb) return outp;
        double max = pb.vol.Max();
        for (int i = 0; i < bins; i++)
            outp.Add(new JsonObject { ["price"] = (double)(pb.lo + pb.step * (i + .5m)), ["volume"] = max > 0 ? pb.vol[i] / max : 0 });
        return outp;
    }

    /// <summary>Point of control + 70% value area (VAL/VAH) of the same profile the chart draws.</summary>
    private static JsonObject BuildVolumeProfileStats(IReadOnlyList<StockQuote> q, int bins)
    {
        if (ProfileBins(q, bins) is not { } pb) return new JsonObject();
        double total = pb.vol.Sum();
        if (total <= 0) return new JsonObject();
        int poc = Array.IndexOf(pb.vol, pb.vol.Max());
        int lo = poc, hi = poc; double acc = pb.vol[poc];
        while (acc < total * 0.70 && (lo > 0 || hi < bins - 1))
        {
            double below = lo > 0 ? pb.vol[lo - 1] : -1, above = hi < bins - 1 ? pb.vol[hi + 1] : -1;
            if (above >= below) { hi++; acc += pb.vol[hi]; } else { lo--; acc += pb.vol[lo]; }
        }
        double Mid(int i) => (double)(pb.lo + pb.step * (i + .5m));
        return new JsonObject
        {
            ["poc"] = Math.Round(Mid(poc), 2),
            ["val"] = Math.Round((double)(pb.lo + pb.step * lo), 2),
            ["vah"] = Math.Round((double)(pb.lo + pb.step * (hi + 1)), 2)
        };
    }

    /// <summary>
    /// Volume-price confirmation for the latest bar. While that bar is still forming its volume is
    /// only a fraction of the final figure, so RVOL is understated and "confirmation" would be
    /// meaningless — in that case the result is reported as partial and never confirmed.
    /// </summary>
    private static JsonObject BuildVolumePriceConfirmation(IReadOnlyList<StockQuote> q, bool lastBarPartial = false)
    {
        if (q.Count == 0) return new JsonObject { ["confirmed"] = false, ["direction"] = "NONE", ["rvol"] = 0, ["clv"] = 0 };
        int p = Math.Min(20, q.Count);
        double avgVol = q.Skip(Math.Max(0, q.Count - p - 1)).Take(Math.Max(1, p)).Average(x => (double)x.Volume);
        var last = q[^1];
        var prev = q.Count > 1 ? q[^2] : last;
        double rvol = avgVol > 0 ? (double)last.Volume / avgVol : 0;
        double range = Math.Max(1e-9, (double)(last.High - last.Low));
        double clv = ((double)(last.Close - last.Low) - (double)(last.High - last.Close)) / range;
        double change = prev.Close != 0 ? (double)(last.Close - prev.Close) / (double)prev.Close * 100 : 0;
        bool bull = !lastBarPartial && rvol >= 1.20 && change > 0 && clv >= 0.20;
        bool bear = !lastBarPartial && rvol >= 1.20 && change < 0 && clv <= -0.20;
        return new JsonObject { ["confirmed"] = bull || bear, ["direction"] = bull ? "BULLISH" : bear ? "BEARISH" : "NEUTRAL",
            ["partial"] = lastBarPartial,
            ["rvol"] = Math.Round(rvol, 2), ["clv"] = Math.Round(clv, 2), ["priceChangePct"] = Math.Round(change, 2) };
    }

    /// <summary>
    /// Volatility-contraction heuristic: the 10-bar range must be at least 10% tighter than the
    /// 15-bar range, which must be at least 10% tighter than the 20-bar range, AND price must sit
    /// above a rising 50-bar average — a tightening range in a downtrend is not a VCP base.
    /// </summary>
    private static JsonObject BuildVcp(IReadOnlyList<StockQuote> q)
    {
        if (q.Count < 30) return new JsonObject { ["detected"] = false, ["score"] = 0, ["uptrend"] = false };
        var windows = new[] { 10, 15, 20 };
        var contractions = new List<double>();
        foreach (var w in windows)
        {
            var x = q.TakeLast(w).ToList();
            double range = (double)(x.Max(b => b.High) - x.Min(b => b.Low)) / Math.Max(1e-9, (double)x.Average(b => b.Close));
            contractions.Add(range);
        }
        bool contracting = contractions[0] <= contractions[1] * 0.9 && contractions[1] <= contractions[2] * 0.9;

        bool uptrend = true;
        if (q.Count >= 55)
        {
            double smaNow  = (double)q.TakeLast(50).Average(b => b.Close);
            double smaPrev = (double)q.Skip(q.Count - 55).Take(50).Average(b => b.Close);
            uptrend = (double)q[^1].Close > smaNow && smaNow > smaPrev;
        }
        bool volumeDryUp = q.TakeLast(10).Average(b => (double)b.Volume) < q.TakeLast(30).Average(b => (double)b.Volume);
        bool detected = contracting && uptrend;
        double score = Math.Clamp((contracting ? 55 : 25) + (uptrend ? 20 : 0) + (volumeDryUp ? 15 : 0), 0, 100);
        return new JsonObject { ["detected"] = detected, ["score"] = Math.Round(score, 1), ["uptrend"] = uptrend,
            ["range10"] = contractions[0], ["range15"] = contractions[1], ["range20"] = contractions[2] };
    }

    private static long Epoch(StockQuote q, bool isIntraday = false)
    {
        long t = new DateTimeOffset(DateTime.SpecifyKind(q.Date, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return isIntraday ? t - (5 * 60 * 60 + 30 * 60) : t;
    }
}
