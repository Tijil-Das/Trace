@echo off
setlocal EnableExtensions EnableDelayedExpansion
REM ============================================================================
REM  Trace — one entry point for the whole app.
REM
REM    Trace.cmd            build what is missing, start recorder, start dashboard
REM    Trace.cmd start      same as above
REM    Trace.cmd stop       ask the recorder to stop, then close the dashboard
REM    Trace.cmd restart    stop, then start
REM    Trace.cmd status     what is running, what is registered, what it costs
REM    Trace.cmd build      build the .NET solution (Release)
REM    Trace.cmd test       run the xUnit suite
REM    Trace.cmd bench [m]  CPU budget check (spec 3): [m] min idle + [m] min active, default 5, logged
REM    Trace.cmd ui         build the React dashboard (needs npm)
REM    Trace.cmd dev-ui     run the dashboard against the Vite dev server
REM
REM  Why the DOTNET dance: on this machine the `dotnet` on PATH is a runtime-only
REM  install ("No .NET SDKs were found"). The SDK lives in %LOCALAPPDATA%\Microsoft\dotnet.
REM ============================================================================

cd /d "%~dp0"

set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

set "CAPTURE=capture-service\bin\Release\net8.0-windows\ScreenRecall.CaptureService.exe"
set "DASHBOARD=dashboard-app\bin\Release\net8.0-windows\ScreenRecall.Dashboard.exe"
set "UI=dashboard-ui\dist\index.html"
set "PIPE=ScreenRecall.Capture"

set "ACTION=%~1"
if "%ACTION%"=="" set "ACTION=start"

if /i "%ACTION%"=="start"   goto :start
if /i "%ACTION%"=="stop"    goto :stop
if /i "%ACTION%"=="restart" goto :restart
if /i "%ACTION%"=="status"  goto :status
if /i "%ACTION%"=="build"   goto :build
if /i "%ACTION%"=="test"    goto :test
if /i "%ACTION%"=="ui"      goto :ui
if /i "%ACTION%"=="dev-ui"  goto :devui
if /i "%ACTION%"=="bench"   goto :bench
echo Unknown action "%ACTION%".
goto :usage

REM ---------------------------------------------------------------- build ----
:build
echo [build] dotnet build ScreenRecall.sln -c Release
"%DOTNET%" build ScreenRecall.sln -c Release --nologo -v minimal
if errorlevel 1 ( echo [build] FAILED & exit /b 1 )
echo [build] ok
goto :eof

REM ------------------------------------------------------------------ ui -----
:ui
if not exist "dashboard-ui\node_modules" (
  echo [ui] installing dependencies once ^(npm install^)
  pushd dashboard-ui
  cmd /c "npm install"
  set "INSTALL_RESULT=%ERRORLEVEL%"
  popd
  if not "%INSTALL_RESULT%"=="0" ( echo [ui] npm install FAILED & exit /b 1 )
)
echo [ui] npm run build
pushd dashboard-ui
cmd /c "npm run build"
set "UI_RESULT=%ERRORLEVEL%"
popd
if not "%UI_RESULT%"=="0" ( echo [ui] FAILED & exit /b 1 )
echo [ui] ok — %UI%
goto :eof

REM --------------------------------------------------------------- start -----
:start
tasklist /fi "imagename eq ScreenRecall.Capture*" 2>nul | find /i "ScreenRecall.CaptureServi" >nul
if not errorlevel 1 (
  echo [start] the recorder is already running
) else (
  if not exist "%CAPTURE%" (
    echo [start] recorder not built yet — building
    call "%~f0" build || exit /b 1
  )
  echo [start] recorder ^(minimised console; it keeps running if you close the dashboard^)
  start "Trace recorder" /min "%CAPTURE%" --console
  powershell -NoProfile -Command "Start-Sleep -Seconds 3" >nul
)

if not exist "%UI%" (
  if exist "dashboard-ui\node_modules" (
    echo [start] dashboard UI not built — building it
    call "%~f0" ui
  ) else (
    echo [start] note: dashboard-ui\dist is missing and its node_modules is absent, so the
    echo         dashboard will use its built-in WPF shell. "Trace.cmd ui" builds the React one.
  )
)

if not exist "%DASHBOARD%" (
  echo [start] dashboard not built yet — building
  call "%~f0" build || exit /b 1
)

echo [start] dashboard
start "Trace dashboard" "%DASHBOARD%"
echo.
echo Trace is running. Tray icon green = recording. "Trace.cmd status" for numbers.
goto :eof

REM ---------------------------------------------------------------- stop -----
:stop
REM Ask the recorder to stop properly before killing anything: a shutdown drains the queued tile writes and
REM flushes the log, so the last seconds stay readable. Termination is the fallback, not the first move,
REM because a killed recorder loses whatever it had not yet flushed.
REM
REM The send-and-wait is one PowerShell process on purpose: a cmd loop polling with a PowerShell call per
REM second spends more time starting shells than waiting, and the drain can legitimately take ~20 seconds.
> "%TEMP%\trace-shutdown.json" echo {"Id":1,"Command":"shutdown"}
powershell -NoProfile -ExecutionPolicy Bypass -Command "$errorActionPreference = 'SilentlyContinue'; $name = 'ScreenRecall.CaptureService'; if (-not (Get-Process -Name $name)) { Write-Host '[stop] the recorder is not running'; exit 0 }; $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '%PIPE%', [System.IO.Pipes.PipeDirection]::InOut); try { $p.Connect(2000); $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true; $w.WriteLine((Get-Content -Raw '%TEMP%\trace-shutdown.json')); $r = New-Object System.IO.StreamReader($p); [void]$r.ReadLine(); Write-Host '[stop] asked the recorder to shut down' } catch { Write-Host '[stop] the recorder did not accept the request' } finally { $p.Dispose() }; $waited = 0; while ((Get-Process -Name $name) -and $waited -lt 25) { Start-Sleep -Milliseconds 500; $waited += 0.5 }; if (Get-Process -Name $name) { Write-Host ('[stop] still running after ' + $waited + 's -- terminating (the last flush interval may be missing)'); Stop-Process -Name $name -Force; Start-Sleep -Seconds 2; if (Get-Process -Name $name) { Write-Host '[stop] WARNING: the recorder is still running' } else { Write-Host '[stop] recorder terminated' } } else { Write-Host ('[stop] recorder stopped cleanly after ' + $waited + 's (it wrote out what it had)') }"

:stop_dashboard
tasklist /fi "imagename eq ScreenRecall.Dashboard*" 2>nul | find /i "ScreenRecall.Dashboard.ex" >nul
if not errorlevel 1 (
  echo [stop] dashboard
  taskkill /f /im ScreenRecall.Dashboard.exe >nul 2>&1
) else (
  echo [stop] the dashboard is not running
)
goto :eof

REM ------------------------------------------------------------- restart -----
:restart
call "%~f0" stop
powershell -NoProfile -Command "Start-Sleep -Seconds 2" >nul
call "%~f0" start
goto :eof

REM -------------------------------------------------------------- status -----
:status
echo === processes ===
tasklist /fi "imagename eq ScreenRecall.Capture*" 2>nul | find /i "ScreenRecall.CaptureServi" >nul
if errorlevel 1 (echo   recorder : stopped) else (echo   recorder : running)
tasklist /fi "imagename eq ScreenRecall.Dashboard*" 2>nul | find /i "ScreenRecall.Dashboard.ex" >nul
if errorlevel 1 (echo   dashboard: closed) else (echo   dashboard: open)

echo === start with windows ^(HKCU Run^) ===
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v ScreenRecall.Capture 2>nul | find /i "ScreenRecall.Capture" >nul
if errorlevel 1 (echo   not registered) else (echo   registered)

echo === dashboard UI ===
if exist "%UI%" (echo   built: %UI%) else (echo   not built — the WPF shell will be used)

echo === recorder ^(asked over its own pipe^) ===
REM The root the recorder is actually writing to is in its config, which is not necessarily the
REM per-user default — so ask it rather than guessing, and remember the answer for the store listing.
> "%TEMP%\trace-status.json" echo {"Id":1,"Command":"status"}
powershell -NoProfile -ExecutionPolicy Bypass -Command "$tmp = '%TEMP%\trace-status.json'; $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '%PIPE%', [System.IO.Pipes.PipeDirection]::InOut); $reply = $null; try { $p.Connect(2000); $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true; $w.WriteLine((Get-Content -Raw $tmp)); $r = New-Object System.IO.StreamReader($p); $reply = $r.ReadLine() } catch { } finally { $p.Dispose() }; if (-not $reply) { Write-Host '  not answering (the recorder is probably not running)'; Remove-Item '%TEMP%\trace-root.txt' -ErrorAction SilentlyContinue; exit 0 }; $s = ($reply | ConvertFrom-Json).Status; Write-Host ('  state  : ' + $s.State + '    paused: ' + $s.Paused); Write-Host ('  root   : ' + $s.StorageRoot); Write-Host ('  day    : ' + $s.Day); Write-Host ('  frames : ' + $s.FramesAcquired + '    tiles stored: ' + $s.TilesStored + '    log entries: ' + $s.LogEntries); Write-Host ('  error  : ' + $s.LastError); Set-Content -Path '%TEMP%\trace-root.txt' -Value $s.StorageRoot -NoNewline" 2>nul

set "STORE_ROOT="
if exist "%TEMP%\trace-root.txt" for /f "usebackq delims=" %%r in ("%TEMP%\trace-root.txt") do set "STORE_ROOT=%%r"
if not defined STORE_ROOT set "STORE_ROOT=%LOCALAPPDATA%\ScreenRecall\data"

echo === store: !STORE_ROOT! ===
REM Fast on purpose: each day's own folder answers "what was recorded" in milliseconds, while player-cli's
REM per-day summary also walks the shared asset store — minutes on a large one. Run player-cli directly when
REM that number is what you actually want.
set "TRACE_ROOT=!STORE_ROOT!"
powershell -NoProfile -Command "$r = $env:TRACE_ROOT; $s = Join-Path $r 'sessions'; if (-not (Test-Path $s)) { Write-Host '  no recorded days'; exit 0 }; $days = Get-ChildItem $s -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}$' } | Sort-Object Name -Descending; if (-not $days) { Write-Host '  no recorded days'; exit 0 }; foreach ($d in $days) { $logs = @(Get-ChildItem (Join-Path $d.FullName 'log*.bin') -File -ErrorAction SilentlyContinue); $bytes = ($logs | Measure-Object Length -Sum).Sum; $ck = @(Get-ChildItem (Join-Path $d.FullName 'checkpoints\*.ckpt') -File -ErrorAction SilentlyContinue).Count; Write-Host ('  {0}   log {1:N1} KB in {2} segment(s), {3} checkpoint(s)' -f $d.Name, ($bytes / 1KB), $logs.Count, $ck) }" 2>nul
goto :eof

REM ---------------------------------------------------------------- bench ----
:bench
REM The spec's one hard performance requirement is <2% steady-state CPU on a Release build (spec 3). Run
REM this before calling any capture-loop change done. A Debug build cannot produce evidence here - the
REM harness detects that itself and refuses to certify the run.
if not exist "%CAPTURE%" (
  echo [bench] recorder not built yet - building
  call "%~f0" build || exit /b 1
)
set "BENCH_MIN=%~2"
if "%BENCH_MIN%"=="" set "BENCH_MIN=5"
if not exist ".dev-logs" mkdir ".dev-logs"
echo [bench] Release CPU budget: %BENCH_MIN% min static screen + %BENCH_MIN% min active use
echo [bench] phase 1 needs a static screen: hands off the mouse and keyboard until phase 2 starts
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$log = Join-Path '.dev-logs' ('cpu-bench-' + (Get-Date -Format 'yyyy-MM-dd-HHmmss') + '.log'); & '%CAPTURE%' --cpu-bench %BENCH_MIN% 2>&1 | Tee-Object -FilePath $log; $code = $LASTEXITCODE; Write-Host ''; Write-Host ('[bench] log: ' + (Resolve-Path $log)); if ($code -eq 2) { Write-Host '[bench] INVALID measurement: Debug build, a busy idle phase, or nothing recorded' }; exit $code"
if errorlevel 2 (echo [bench] FAILED - the measurement was not valid; fix the INVALID verdict above and rerun & exit /b 2)
if errorlevel 1 (echo [bench] FAILED - over the 2 percent steady-state budget & exit /b 1)
echo [bench] within budget
goto :eof

REM ---------------------------------------------------------------- test -----
:test
"%DOTNET%" test tests\ScreenRecall.Tests\ScreenRecall.Tests.csproj -c Release --nologo
goto :eof

REM --------------------------------------------------------------- dev-ui ----
:devui
echo [dev-ui] Vite dev server on http://localhost:5173
echo [dev-ui] point the dashboard at it with:
echo           set SCREENRECALL_UI_DEV=http://localhost:5173
pushd dashboard-ui
cmd /c "npm run dev"
popd
goto :eof

:usage
echo   Trace.cmd [start ^| stop ^| restart ^| status ^| build ^| test ^| bench ^| ui ^| dev-ui]
