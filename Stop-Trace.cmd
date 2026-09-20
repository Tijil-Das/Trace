@echo off
REM Double-click this to stop Trace.
taskkill /f /im ScreenRecall.CaptureService.exe 2>nul
taskkill /f /im ScreenRecall.Dashboard.exe 2>nul
echo Trace stopped.
pause
