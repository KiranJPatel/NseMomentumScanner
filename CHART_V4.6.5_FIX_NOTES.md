# NSE Momentum Scanner v4.6.5 — Chart Indicator Alignment Fix

## Root causes fixed

1. `ChartAnalytics.Build(...)` was calculated by `ChartDataExporter.BuildTimeframeSeries(...)` but the returned `analytics` object was not included in the JSON payload. This prevented levels, market structure, FVG, volume profile and VCP from reaching the Charts tab.
2. Standard indicator arrays were sparse: warm-up values were omitted from JSON rather than represented at their original bar index. This broke the 1:1 relationship between OHLC bars and indicator arrays.
3. Intraday structural/FVG timestamps did not apply the same IST-to-epoch adjustment as OHLC bars.
4. Volume-price confirmation existed only as a final summary, not as an aligned per-bar series.

## New chart data contract

For every timeframe:

- `bars.length === N`
- Every standard indicator array also has length `N`.
- Warm-up/unavailable values are `null` at the exact original bar index.
- Non-null indicator points carry the exact same `t` timestamp as `bars[i].t`.
- `analytics` is included in the API response.
- `integrity.barCount` reports N.
- `integrity.<series>` verifies length and timestamp alignment.

Added aligned series:

- EMA fast / slow
- HMA fast / slow
- Supertrend
- ADX
- +DI / -DI
- ATR
- RSI
- Volume SMA
- RVOL
- ATR-normalized distance from HMA slow
- Volume-price confirmation signal

## Browser behavior

The dashboard now filters only null warm-up points when passing data to Lightweight Charts. It does not re-index or recalculate indicators in JavaScript.

Additional rendering:

- Volume-price confirmation markers (`VP`) on confirmed bullish/bearish bars.
- ATR-normalized distance shown in the chart info strip and crosshair details.
- VCP, swing-quality and structure summaries remain in the compact information strip.
- FVG, support/resistance, structure and volume-profile overlays consume the newly restored `analytics` payload.
