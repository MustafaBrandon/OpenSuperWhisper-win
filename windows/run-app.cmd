@echo off
REM Stop, rebuild, and relaunch the desktop app.
REM
REM   windows\run-app
REM   windows\run-app --paste-target
REM
REM The stop step is not optional: a running instance holds its DLLs open and the
REM build fails with "file is being used by another process".

setlocal
set "TFM=net10.0-windows10.0.22000.0"
set "EXE=%~dp0src\OpenSuperWhisper.App\bin\Release\%TFM%\win-x64\OpenSuperWhisper.exe"

echo stopping any running instance...
powershell -NoProfile -Command "Get-Process OpenSuperWhisper -ErrorAction SilentlyContinue | Stop-Process -Force"
timeout /t 1 /nobreak >nul

echo building...
dotnet build "%~dp0OpenSuperWhisper.slnx" -c Release --nologo -v:q
if errorlevel 1 exit /b 1

echo starting...
start "" "%EXE%" %*

echo.
echo log: %LOCALAPPDATA%\OpenSuperWhisper\log.txt
echo quit: right-click the tray icon (behind the ^^ chevron), or run:
echo   powershell -c "Get-Process OpenSuperWhisper ^| Stop-Process -Force"
