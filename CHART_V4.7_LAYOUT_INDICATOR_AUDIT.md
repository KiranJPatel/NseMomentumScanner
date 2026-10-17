# Chart v4.7 — Layout + Indicator Coverage

## Layout
- Popout is a chart-only full-window mode.
- Popout retains toolbar, timeframe selector, indicator toggles, status/coverage strip, price chart, RSI/ADX pane, overlays and settings.
- Popout no longer hides the RSI/ADX pane.
- Maximize button resizes the popout to available screen dimensions; Fullscreen API is used as fallback.
- Market context (NSEI regime + breadth/bullish/bearish summary cards) is one collapsible unit.
- Collapse state is persisted in localStorage.

## Indicator-to-chart mapping
- EMA Fast/Slow -> main price pane line series.
- HMA Fast/Slow -> main price pane line series.
- Supertrend -> bullish/bearish main-pane line series.
- Volume -> main-pane histogram.
- ADX, +DI, -DI -> compact oscillator pane.
- RSI -> same compact oscillator pane.
- Current/previous day, weekly, 52-week H/L -> main-pane horizontal levels.
- HH/HL/LH/LL -> candle markers.
- BOS/CHoCH -> candle event markers.
- Support/resistance -> main-pane horizontal overlay lines with swing quality in tooltip.
- VCP -> info strip score/detection state.
- Volume-price confirmation -> VP candle markers + info strip + hover values.
- Volume profile -> right-edge main-pane profile overlay.
- Major FVG -> main-pane price/time zones, last 100 source candles and visible-zone cap.
- ATR -> hover + info strip.
- ATR-normalized distance -> hover + info strip.
- RVOL -> hover + info strip.

## Alignment
Server payload continues to emit every indicator array at exactly `bars.Count`, with warm-up entries represented by nulls. Browser rendering intentionally filters null warm-up points only when creating Lightweight Charts series; timestamp/value alignment is preserved.
