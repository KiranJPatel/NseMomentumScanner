# NSE Momentum Stock Scanner v4.6.1 — Build Fix

## Root cause

`StockQuote`, `IndicatorArrays`, `Config`, `YahooInterval` and `AppSettings` are nested types inside `NseMomentumScanner.Program` in this codebase.

The v4.6 chart analytics file was introduced as a namespace-level class and referenced `StockQuote` / `IndicatorArrays` as if they were namespace-level types. This produced:

- `StockQuote could not be found`
- `IndicatorArrays does not exist in the current context`
- incompatible `List<NseMomentumScanner.StockQuote>` / `List<StockQuote>` arguments

## Fix

- `ChartAnalytics` now explicitly uses `Program.StockQuote`.
- `ChartAnalytics` now explicitly uses `Program.IndicatorArrays`.
- External backtesting and signal files now explicitly reference nested `Program.StockQuote`, `Program.Config`, `Program.YahooInterval`, and `Program.AppSettings` where required.
- No duplicate `StockQuote` or duplicate indicator implementation was introduced.
- Existing Skender.Stock.Indicators v2 implementation remains unchanged.

## Validation

The supplied execution environment does not contain the .NET SDK, so a local `dotnet build` cannot be executed here. The project structure and type references were statically checked after the correction.

Build command on Windows:

    dotnet restore
    dotnet build -c Release

Run:

    dotnet run -c Release -- --serve
