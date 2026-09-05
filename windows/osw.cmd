@echo off
REM Dev shim: runs the built CLI without needing it on PATH.
REM
REM   windows\osw devices
REM   windows\osw listen
REM
REM Prefers Release, falls back to Debug. Build first with:
REM   dotnet build windows\OpenSuperWhisper.slnx -c Release

setlocal
set "TFM=net10.0-windows10.0.22000.0"
set "BASE=%~dp0src\OpenSuperWhisper.Cli\bin"

set "EXE=%BASE%\Release\%TFM%\win-x64\osw.exe"
if not exist "%EXE%" set "EXE=%BASE%\Debug\%TFM%\win-x64\osw.exe"

if not exist "%EXE%" (
    echo osw is not built yet.
    echo   dotnet build "%~dp0OpenSuperWhisper.slnx" -c Release
    exit /b 1
)

"%EXE%" %*
