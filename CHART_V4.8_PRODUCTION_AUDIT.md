# v4.8 — Charts production audit

## Verified how
- Full project compiled against stand-in NuGet stubs (sandbox has no NuGet): shipped v4.5 FAILED (CS0103), v4.8 builds with 0 errors.
- Real ChartAnalytics/ChartDataExporter run on synthetic OHLCV (3 regimes x closed/live x 6 timeframes): invariants asserted.
- Real dashboard driven in headless Chromium against those payloads (hover, failure, stale, live refresh, intraday axis).
- NOT verified: live Yahoo calls, real Skender output (stub maths used), the other tabs' scoring logic.

## Fixed
1. BUILD BREAK: ChartAnalytics.Fvg() used isIntraday without receiving it.
2. Fetch failure rendered RANDOM candles labelled "Yahoo Finance" -> now explicit NO DATA + Retry.
3. Hover popover read indicator values by bar position from null-filtered arrays -> wrong bar. Now resolved by timestamp.
4. Structure pivots were emitted highs-then-lows, so "last 16" showed only lows -> chronological.
5. BOS/CHoCH could be out of order / inconsistent -> single chronological state machine.
6. S/R: last 6 swing highs/lows regardless of price -> clustered, split around last close, nearest 3 each side.
7. /charts/{symbol}: symbol allow-list (reached file path + upstream URL); CORS restricted from any-origin.
8. Data never refreshed -> per-timeframe TTL, auto-refresh while visible, server cache + single-flight (protects against Yahoo 429).
9. Forming candle: RVOL/volume-price confirmation suppressed + LIVE badge.
10. Levels: 8 full-length line series + duplicate DOM lines distorted autoscale -> native price lines.
11. Intraday x-axis showed dates only -> IST clock times.
12. Chart prices split-adjusted only (match broker); scanner scoring still dividend-adjusted.
13. VCP requires a rising 50-bar trend; VP markers capped; POC/VAH/VAL added.
14. Charting library vendored (vendor/) and pinned to 4.2.3 (was unpkg @4, floating).

## Still open (not changed)
- appsettings Backtest.EndDate is 2025-12-31.
- NseCalendar has holidays only through 2026 — add 2027 when NSE publishes.
- Chart.js and Tabler icons still load from CDNs.
- ExportChartData=true writes ~6 JSON series per symbol every scan; now only a stale fallback.
- Dashboard falls back to demo stocks when the DB is empty.

## New: Trade Plan & Risk Planner (breakout entry)
Server (`ChartAnalytics.BuildTradePlan`, payload key `analytics.tradePlan`) — LONG only:
- Entry = close through the nearest overhead resistance (+0.1 ATR, 0.05 tick). 52-week high counts as resistance if above price.
- Stop = last confirmed swing low below the trigger (-0.25 ATR), clamped to [max(1 ATR, MinimumRiskPct%), LiveAtrStopMultiplier x ATR]. On intraday charts the 2% floor is not applied.
- Targets = 1.5R, MinimumRiskReward, LiveRiskRewardRatio (3 distinct R-multiples).
- States: NO_PLAN (structure DOWN / no history / no overhead resistance), WATCH, ARMED (within MaxBreakoutDistancePct; intraday: 1.5 ATR),
  TRIGGERED (volume vs BreakoutMinRvol reported), EXTENDED (> 0.5 ATR past trigger), BLOCKED (next resistance < MinimumRiskReward away).
- Forming candle: volume judged on the last COMPLETED bar; TRIGGERED on a forming bar says "confirm at the close".
Browser: plan card + ENTRY/STOP/T1-T3 price lines; quantity = min(capital x risk% / risk-per-share, capital x allocation cap / entry);
BLOCKED/EXTENDED hide sizing. Capital (default 10,00,000), risk % (1) and allocation cap (15%) live in Settings (stored in browser IndexedDB).
Not modelled: brokerage/STT/slippage in the P&L figures, gap-up opens beyond the trigger, shorts.

## Popup + lower-pane fixes
- ADX/RSI pane drifted ~190px from the main chart: the oscillator series skip warm-up bars, and the two charts are synced by
  bar INDEX, so the lower pane indexed bars ~27 positions late. An invisible anchor series carrying every bar time now fixes the
  mapping; both price scales also use minimumWidth 76 (were 68 vs 48px) so the plot areas span the same x-range.
  Measured: 175-198px offset before, 0.0px after (dashboard + popup, 1D and 15m, after pan/zoom).
- Popup could not scroll: JS set `chart-layout-scroll` on the panel but the CSS rules were keyed on <body>, and the panel was
  overflow:hidden. At 1920x1080 the ADX/RSI pane ended at y=1126 of 1080 and was clipped. Panel is now overflow-y:auto, the class is
  set on body too, the plan card height is counted in the layout, and window.open uses scrollbars=yes.
  Note: the mouse wheel over a chart still zooms it (expected); scroll with the scrollbar or the wheel over the toolbar/plan card.

## Trade-plan columns in Buy / Sell / Score Matrix
- New columns right after Score (Buy, Sell) and after Signal (Score Matrix): Plan, Entry, Stop, Risk, T1, T2, T3, Vol need/last, Qty, Capital, Max loss, % ADV.
- Buy = LONG breakout plan, Sell = SHORT breakdown plan (mirror: trigger below the nearest support, stop above the last swing high, targets below),
  Score Matrix = long plan for BUY/neutral rows, short plan for IMMEDIATE_SELL rows (marked ▲ / ▼).
- The tables read the scan database, which holds no price bars, so the scan now computes both plans from its daily bars and writes
  `charts/_plans.json` (independent of ExportChartData); `/dashboard_data.json` merges them in as `planLong` / `planShort` and `plansAsOf`.
  **The columns are as of the scan's daily close.** The Charts tab recomputes the live plan; when the long plan is NO_PLAN because
  structure is DOWN, the Charts tab now shows the short plan instead.
- Quantity / capital / max loss use Charts -> Settings (capital, risk %, allocation cap); saved values are loaded at startup and the
  tables re-render when Settings are saved. Gated plans (BLOCKED / EXTENDED) show "gated" instead of sizing.
- Needs one new scan to populate the columns; older data shows "—" with a hint.
- Caveat: shorting delivery stock is not possible on NSE cash — a SHORT plan is for intraday cash or futures.
- Fixed while building this: generalising the plan to shorts briefly inverted the distance sign (every plan became ARMED); caught by the
  harness, fixed, and the tests now assert ARMED/WATCH distance >= 0 and that WATCH plans exist.
- Not verified: the `DashboardDataBuilder` merge of `_plans.json` (it needs a real SQLite DB; the sandbox only has a stub) — the merge is
  ~10 lines and the browser tests used a fixture with the same JSON shape.
