# NSE Momentum Scanner v4.6.2 — StockQuote Type Fix

## Root cause
`StockQuote` and `IndicatorArrays` are namespace-level types in `NseMomentumScanner.Program.cs` (inside namespace `NseMomentumScanner` but outside the `Program` class).

The previous v4.6.1 fix incorrectly qualified them as `Program.StockQuote` and `Program.IndicatorArrays`, producing two incompatible apparent type identities:

- `NseMomentumScanner.StockQuote`
- `NseMomentumScanner.Program.StockQuote`

This caused List/IReadOnlyList conversion errors.

## Fix
Removed the incorrect `Program.` qualification from all affected source files:

- `ChartAnalytics.cs`
- `SignalEnhancements.cs`
- `Backtesting/BacktestDataCache.cs`
- `Backtesting/BacktestEngine.cs`
- `Backtesting/DailySeriesIndex.cs`

Canonical types are now:

```csharp
NseMomentumScanner.StockQuote
NseMomentumScanner.IndicatorArrays
```

No duplicate type was introduced.

## Verification
- Searched entire `.cs` source tree: zero remaining `Program.StockQuote` references.
- Searched entire `.cs` source tree: zero remaining `Program.IndicatorArrays` references.
- Confirmed exactly one `StockQuote` declaration and one `IndicatorArrays` declaration.
- The execution environment does not have the .NET SDK installed, so `dotnet build` could not be executed here.
