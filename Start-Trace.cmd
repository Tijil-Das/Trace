@echo off
REM Double-click this. Starts the recorder + opens the GUI dashboard.
REM Both use the per-user default root (%LOCALAPPDATA%\ScreenRecall\data) so the
REM dashboard reads the same folder the recorder writes.
cd /d "%~dp0"
start "Trace recorder" "capture-service\bin\Release\net8.0-windows\ScreenRecall.CaptureService.exe" --console
timeout /t 3 /nobreak >nul
start "Trace dashboard" "dashboard-app\bin\Release\net8.0-windows\ScreenRecall.Dashboard.exe"
echo Trace is running. Look for the tray icon (green = recording).
echo Dashboard window should now be visible.
pause
