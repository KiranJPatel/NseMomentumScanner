@echo off
setlocal
cd /d "%~dp0"

echo ================================================================
echo NSE Momentum Scanner v4.5 - clean build
 echo ================================================================
where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: .NET 8 SDK was not found on PATH.
  echo Install the .NET 8 SDK, reopen the terminal, and rerun this file.
  exit /b 1
)

dotnet --version
if errorlevel 1 exit /b 1

echo.
echo Restoring packages...
dotnet restore NseMomentumScanner.csproj
if errorlevel 1 exit /b 1

echo.
echo Building Release...
dotnet build NseMomentumScanner.csproj -c Release --no-restore
if errorlevel 1 exit /b 1

echo.
echo Build successful.
echo Run a configuration/database smoke test with:
echo   dotnet run -c Release --no-build -- --dry-run
echo Then scan with:
echo   dotnet run -c Release --no-build
endlocal
