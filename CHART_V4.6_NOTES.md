# NSE Momentum Stock Scanner — v4.6 Chart Intelligence

## Chart architecture
- Standard technical indicators are calculated server-side with the existing `Skender.Stock.Indicators` package.
- Browser JavaScript no longer reimplements EMA/HMA/ADX/ATR/Supertrend calculations.
- Yahoo Finance remains the OHLCV source through the existing `/charts/{symbol}` API.
- For non-daily chart timeframes, a 400-day daily reference series is fetched to make day/week/52-week levels correct.

## Standard indicators
- Supertrend — Skender
- HMA Fast / Slow — Skender
- ADX / +DI / -DI — Skender
- RSI — Skender
- ATR — Skender
- Volume SMA — existing rolling calculation
- ATR-normalized distance from slow HMA — derived chart metric
- Volume-price confirmation — custom RVOL + close-location-value + price-direction confirmation

## Market structure
Custom logic is retained because these are not generic TA-library outputs:
- HH / HL / LH / LL swing classification
- BOS / CHoCH events
- Swing-quality score
- Support / resistance from recent confirmed pivots
- VCP contraction score
- Volume profile
- FVG detection restricted to the latest 100 candles, with a 0.05 ATR minimum gap threshold and fill tracking

## UX
- Main price pane gets the majority of available height.
- Volume is integrated into the main price chart.
- RSI + ADX/+DI/-DI share one compact lower pane.
- ATR is not given a separate pane; current ATR-normalized HMA distance is shown in the information strip and hover card.
- Key levels are dashed and color-grouped.
- Structure labels are capped to recent pivots/events to avoid clutter.
- Volume profile is a translucent right-edge overlay.
- FVG visibility is capped to the most recent major zones.
- Initial viewport remains the latest 100 candles with right-side padding.

## Validation
- `nse_dashboard.html` embedded JavaScript passes `node --check`.
- `appsettings.json` passes JSON validation.
- Full .NET compilation could not be executed in this environment because the .NET SDK is not installed. The package/API calls were checked against the current Stock Indicators documentation; the project continues to use its existing v2 API surface to avoid an unnecessary breaking upgrade.
