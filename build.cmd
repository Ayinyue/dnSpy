@echo off
setlocal EnableExtensions

rem ---------------------------------------------------------------
rem  build.cmd - thin wrapper around build.ps1 with build logging
rem
rem  Usage:
rem    build.cmd                    full build (netframework + net + net-x86 + net-x64)
rem    build.cmd net                only the .NET (net10.0-windows) target
rem    build.cmd netframework       only the .NET Framework (net48) target
rem    build.cmd net-x64            self contained win-x64 only
rem    build.cmd msbuild [args]     force the MSBuild engine instead of dotnet
rem
rem  Logs:
rem    BuildLogs\build_<stamp>_<label>.log   full transcript of the run
rem    BuildLogs\build_history.log           one summary line per build
rem    Set DNSPY_LOG_DIR to override the folder.
rem
rem  Notes:
rem    * Defaults to the dotnet engine (-NoMsbuild). VS2022 17.14's
rem      MSBuild resolves SDK 9.0.317 (C# 13) and therefore CANNOT
rem      target net10.0-windows (nor the C# 14 'field' keyword used
rem      by dnSpy.StringSearcher). The dotnet CLI resolves SDK
rem      10.0.401 (C# 14) and builds cleanly. Only pass "msbuild"
rem      on a VS/toolchain that supports net10.0.
rem    * Always runs from this script's own directory: build.ps1 uses
rem      relative paths and relies on the implicit dnSpy.sln lookup,
rem      so the current directory must be the repository root.
rem    * -ExecutionPolicy Bypass is required: this machine's policy is
rem      Restricted, which would otherwise block build.ps1 entirely.
rem ---------------------------------------------------------------

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%build.ps1"
set "LOGGER=%SCRIPT_DIR%Build\build-log.ps1"

if not exist "%PS_SCRIPT%" (
    echo [ERROR] build.ps1 not found: "%PS_SCRIPT%"
    exit /b 1
)
if not exist "%LOGGER%" (
    echo [ERROR] build-log.ps1 not found: "%LOGGER%"
    exit /b 1
)

rem ---- optional leading "msbuild" sentinel -------------------------------
set "FORCE_MSBUILD="
set "ARGS=%*"
if /i "%~1"=="msbuild" goto :force_msbuild
goto :dispatch

:force_msbuild
set "FORCE_MSBUILD=1"
shift
set "ARGS=%1 %2 %3 %4 %5 %6 %7 %8 %9"
goto :dispatch

:dispatch

rem ---- locate a PowerShell host -----------------------------------------
rem (prefer pwsh when installed, fall back to Windows PowerShell 5.1)
set "PSEXE="
where pwsh.exe >nul 2>&1 && set "PSEXE=pwsh.exe"
if not defined PSEXE where powershell.exe >nul 2>&1 && set "PSEXE=powershell.exe"
if not defined PSEXE (
    echo [ERROR] Neither pwsh.exe nor powershell.exe was found in PATH.
    exit /b 1
)

rem ---- warn early if the dotnet engine is requested but missing ---------
if not defined FORCE_MSBUILD (
    where dotnet.exe >nul 2>&1
    if errorlevel 1 echo [WARN] 'dotnet' was not found in PATH - the default engine will fail.
)

if defined FORCE_MSBUILD (echo [build] engine : MSBuild ^(forced^)) else (echo [build] engine : dotnet ^(-NoMsbuild^))
if not defined FORCE_MSBUILD set "ARGS=-NoMsbuild %ARGS%"
echo [build] host   : %PSEXE%
echo [build] script : "%PS_SCRIPT%"
echo [build] args   : %ARGS%
echo.

rem ---- optional log folder override -------------------------------------
set "LOGARG="
if defined DNSPY_LOG_DIR set "LOGARG=-LogDir ""%DNSPY_LOG_DIR%"""

rem Build arguments travel via the environment on purpose: with '-File',
rem PowerShell would try to bind '-NoMsbuild' as a parameter of
rem build-log.ps1 instead of forwarding it to build.ps1.
set "DNSPY_BUILD_ARGS=%ARGS%"

pushd "%SCRIPT_DIR%" || exit /b 1
"%PSEXE%" -NoProfile -NoLogo -ExecutionPolicy Bypass -File "%LOGGER%" -Script "%PS_SCRIPT%" %LOGARG%
set "EXITCODE=%ERRORLEVEL%"
popd

echo.
if "%EXITCODE%"=="0" (
    echo [OK] build completed - see BuildLogs\build_history.log
) else (
    echo [FAIL] build failed with exit code %EXITCODE% - see the log path printed above.
)
exit /b %EXITCODE%
