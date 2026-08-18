@echo off
setlocal

cd /d "%~dp0"

python export_dashboard_data.py --db "bin\Debug\net8.0\NseMomentumScanner.db" --out "dashboard_data.json"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Export failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Dashboard data exported successfully to dashboard_data.json
pause