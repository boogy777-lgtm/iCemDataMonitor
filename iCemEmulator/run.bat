@echo off
setlocal
cd /d "%~dp0"

echo === iCemEmulator - iCem LAN/COM Stream Emulator (UI + CLI, one app) ===
echo.
echo Usage: run [mode]
echo   (no args)  - build ^& launch the app window (engine runs in-process)
echo   selftest   - transport self-test (FrameRoundTrip)
echo   landiag    - LAN loopback diagnostic on Intel 82574L
echo   landiag X  - LAN loopback diagnostic on adapter matching 'X'
echo   cli "..."  - one-shot CLI command to the RUNNING instance (e.g. cli status)
echo   build      - dotnet build only, don't run
echo.
if not "%1"=="" goto :%1 2>nul

:gui
dotnet build "%~dp0iCemEmulator.csproj" -p:Platform=x64 --nologo -v q
if %errorlevel% neq 0 ( echo BUILD FAILED & pause & exit /b 1 )
echo [run]   iCemEmulator.exe (window)
start "" "%~dp0bin\x64\Debug\net10.0-windows10.0.19041.0\iCemEmulator.exe"
exit /b 0

:selftest
dotnet build "%~dp0iCemEmulator.csproj" -p:Platform=x64 --nologo -v q
if %errorlevel% neq 0 ( echo BUILD FAILED & pause & exit /b 1 )
echo [run]   iCemEmulator.exe --selftest
"%~dp0bin\x64\Debug\net10.0-windows10.0.19041.0\iCemEmulator.exe" --selftest
exit /b %errorlevel%

:landiag
set ADAPTER=%~2
if "%ADAPTER%"=="" set ADAPTER=82574L
dotnet build "%~dp0iCemEmulator.csproj" -p:Platform=x64 --nologo -v q
if %errorlevel% neq 0 ( echo BUILD FAILED & pause & exit /b 1 )
echo [run]   iCemEmulator.exe --landiag "%ADAPTER%"
"%~dp0bin\x64\Debug\net10.0-windows10.0.19041.0\iCemEmulator.exe" --landiag "%ADAPTER%"
exit /b %errorlevel%

:cli
shift
dotnet build "%~dp0iCemEmulator.csproj" -p:Platform=x64 --nologo -v q
if %errorlevel% neq 0 ( echo BUILD FAILED & pause & exit /b 1 )
echo [run]   iCemEmulator.exe --cli %*
"%~dp0bin\x64\Debug\net10.0-windows10.0.19041.0\iCemEmulator.exe" --cli %*
exit /b %errorlevel%

:build
dotnet build "%~dp0iCemEmulator.csproj" -p:Platform=x64
exit /b %errorlevel%
