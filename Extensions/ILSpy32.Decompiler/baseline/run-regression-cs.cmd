@echo off
rem C# quality gate (M3 addition): the default decompiler switch covers C# too, but the
rem VB gate never measured C# output. One command dumps all three samples with BOTH engines,
rem validates syntax and runs the semantic compile, then prints 3.2 vs 2.3 side by side.
rem Exit code 1 when 3.2 is worse than 2.3 on any metric (syntax errors or realBadFiles).
rem
rem Usage: Extensions\ILSpy32.Decompiler\baseline\run-regression-cs.cmd
setlocal
cd /d "%~dp0"
set DN=..\..\..\dnSpy\dnSpy\bin\Release\net10.0-windows

echo === [1/3] build engine ===
dotnet build ..\dnSpy.Decompiler.ILSpy32\dnSpy.Decompiler.ILSpy32.csproj -c Release -f net10.0-windows --no-restore || exit /b 1

echo === [2/3] dump (3.2 and 2.3) ===
dotnet run -c Release -- dump --asm %DN%\dnSpy.dll                  --impl ilspy32 --lang cs --all --out out/v32-cs-dnspy     || exit /b 1
dotnet run -c Release -- dump --asm %DN%\dnSpy.AsmEditor.x.dll      --impl ilspy32 --lang cs --all --out out/v32-cs-asm       || exit /b 1
dotnet run -c Release -- dump --asm %DN%\dnSpy.Contracts.Logic.dll  --impl ilspy32 --lang cs      --out out/v32-cs-contracts  || exit /b 1
dotnet run -c Release -- dump --asm %DN%\dnSpy.dll                  --impl core    --lang cs --all --out out/core-cs-dnspy     || exit /b 1
dotnet run -c Release -- dump --asm %DN%\dnSpy.AsmEditor.x.dll      --impl core    --lang cs --all --out out/core-cs-asm       || exit /b 1
dotnet run -c Release -- dump --asm %DN%\dnSpy.Contracts.Logic.dll  --impl core    --lang cs      --out out/core-cs-contracts  || exit /b 1

echo === [3/3] validate + compile ===
dotnet run -c Release -- validate --dir out/v32-cs-dnspy     --lang cs --out out/syntax-cs-dnspy.md
dotnet run -c Release -- validate --dir out/v32-cs-asm       --lang cs --out out/syntax-cs-asm.md
dotnet run -c Release -- validate --dir out/v32-cs-contracts --lang cs --out out/syntax-cs-contracts.md
dotnet run -c Release -- validate --dir out/core-cs-dnspy     --lang cs --out out/syntax-cs-dnspy-core.md
dotnet run -c Release -- validate --dir out/core-cs-asm       --lang cs --out out/syntax-cs-asm-core.md
dotnet run -c Release -- validate --dir out/core-cs-contracts --lang cs --out out/syntax-cs-contracts-core.md
dotnet run -c Release -- compile --dir out/v32-cs-dnspy     --lang cs --refs %DN% --out out/compile-cs-dnspy.md
dotnet run -c Release -- compile --dir out/v32-cs-asm       --lang cs --refs %DN% --out out/compile-cs-asm.md
dotnet run -c Release -- compile --dir out/v32-cs-contracts --lang cs --refs %DN% --out out/compile-cs-contracts.md
dotnet run -c Release -- compile --dir out/core-cs-dnspy     --lang cs --refs %DN% --out out/compile-cs-dnspy-core.md
dotnet run -c Release -- compile --dir out/core-cs-asm       --lang cs --refs %DN% --out out/compile-cs-asm-core.md
dotnet run -c Release -- compile --dir out/core-cs-contracts --lang cs --refs %DN% --out out/compile-cs-contracts-core.md

echo.
echo === C# baseline table (3.2 vs 2.3) ===
set REG=0
call :Check dnSpy.dll       v32 syntax-cs-dnspy.md     compile-cs-dnspy.md     core syntax-cs-dnspy-core.md     compile-cs-dnspy-core.md
if errorlevel 1 set REG=1
call :Check AsmEditor.x     v32 syntax-cs-asm.md       compile-cs-asm.md       core syntax-cs-asm-core.md       compile-cs-asm-core.md
if errorlevel 1 set REG=1
call :Check Contracts.Logic v32 syntax-cs-contracts.md compile-cs-contracts.md core syntax-cs-contracts-core.md compile-cs-contracts-core.md
if errorlevel 1 set REG=1
echo.
if %REG%==1 (
    echo RESULT: REGRESSION - 3.2 is worse than 2.3 somewhere; do not proceed.
    exit /b 1
)
echo RESULT: OK - 3.2 C# output is no worse than 2.3 on every metric.
exit /b 0

:Check
rem %1=name %2=ignored %3=syntax %4=compile %5=ignored %6=syntax-core %7=compile-core
set SYN32=?& set BAD32=?& set SYN23=?& set BAD23=?
for /f "tokens=6" %%a in ('findstr /c:"files with syntax errors:" out\%3') do set SYN32=%%a
for /f "tokens=6" %%a in ('findstr /c:"files with real errors:" out\%4') do set BAD32=%%a
for /f "tokens=6" %%a in ('findstr /c:"files with syntax errors:" out\%6') do set SYN23=%%a
for /f "tokens=6" %%a in ('findstr /c:"files with real errors:" out\%7') do set BAD23=%%a
if "%SYN32%"=="?" goto :CsFail
if "%BAD32%"=="?" goto :CsFail
if "%SYN23%"=="?" goto :CsFail
if "%BAD23%"=="?" goto :CsFail
set /a CMP=%SYN32%-%SYN23% >nul 2>&1
if %CMP% GTR 0 goto :CsWorse
set /a CMP=%BAD32%-%BAD23% >nul 2>&1
if %CMP% GTR 0 goto :CsWorse
echo %1: 3.2 syntax=%SYN32% realBad=%BAD32%  ^|  2.3 syntax=%SYN23% realBad=%BAD23%  ok
exit /b 0
:CsWorse
echo %1: 3.2 syntax=%SYN32% realBad=%BAD32%  ^|  2.3 syntax=%SYN23% realBad=%BAD23%  ** 3.2 WORSE **
exit /b 1
:CsFail
echo %1: report missing or malformed  ** FAILED **
exit /b 1
