@echo off
rem Batch 0 regression: one command, the full gate from VB-Dev-Plan.md section 0.
rem Builds the 3.2 engine, re-dumps the three sample assemblies with the VB backend,
rem then runs syntax validation and semantic compilation, and prints the baseline
rem table (syntax errors 0/0/0, realBadFiles 989/527/10 after Batch 4) next to the
rem documented reference numbers. Exit code 1 only on a regression (a value that got worse).
rem
rem Usage (from anywhere):  Extensions\ILSpy32.Decompiler\baseline\run-regression.cmd
setlocal
cd /d "%~dp0"
set DN=..\..\..\dnSpy\dnSpy\bin\Release\net10.0-windows

echo === [1/4] build engine (gate: 0 errors) ===
dotnet build ..\dnSpy.Decompiler.ILSpy32\dnSpy.Decompiler.ILSpy32.csproj -c Release -f net10.0-windows --no-restore || exit /b 1

echo === [2/4] dump ===
dotnet run -c Release -- dump --asm %DN%\dnSpy.dll                  --impl ilspy32 --lang vb --all --out out/v32-vb-dnspy     || exit /b 1
dotnet run -c Release -- dump --asm %DN%\dnSpy.AsmEditor.x.dll      --impl ilspy32 --lang vb --all --out out/v32-vb-asm       || exit /b 1
dotnet run -c Release -- dump --asm %DN%\dnSpy.Contracts.Logic.dll --impl ilspy32 --lang vb       --out out/v32-vb-contracts || exit /b 1

echo === [3/4] syntax validation ===
rem validate/compile exit 2 when bad files exist - that is expected at the current
rem baseline; only a dump failure is fatal. The final table decides pass or fail.
dotnet run -c Release -- validate --dir out/v32-vb-dnspy     --lang vb --out out/syntax-vb-dnspy.md
dotnet run -c Release -- validate --dir out/v32-vb-asm       --lang vb --out out/syntax-vb-asm.md
dotnet run -c Release -- validate --dir out/v32-vb-contracts --lang vb --out out/syntax-vb-contracts.md

echo === [4/4] semantic compile ===
dotnet run -c Release -- compile --dir out/v32-vb-dnspy     --lang vb --refs %DN% --out out/compile-vb-dnspy.md
dotnet run -c Release -- compile --dir out/v32-vb-asm       --lang vb --refs %DN% --out out/compile-vb-asm.md
dotnet run -c Release -- compile --dir out/v32-vb-contracts --lang vb --refs %DN% --out out/compile-vb-contracts.md

echo.
echo === baseline table (current vs documented reference) ===
set REG=0
call :Check dnSpy.dll          syntax-vb-dnspy.md     compile-vb-dnspy.md     0    989
if errorlevel 1 set REG=1
call :Check AsmEditor.x        syntax-vb-asm.md       compile-vb-asm.md       0    527
if errorlevel 1 set REG=1
call :Check Contracts.Logic    syntax-vb-contracts.md compile-vb-contracts.md 0    10
if errorlevel 1 set REG=1
echo.
echo degraded/failed must be 0 in every out\v32-vb-*\_meta.txt (checked by dump exit code).
if %REG%==1 (
    echo RESULT: REGRESSION - a number got worse; do not proceed.
    exit /b 1
)
echo RESULT: OK - no regression. If a number improved, update VB-Dev-Plan.md section 0.
exit /b 0

:Check
rem %1=name %2=syntax report %3=compile report %4=expected syntax %5=expected realBad
set SYN=?
set BAD=?
for /f "tokens=6" %%a in ('findstr /c:"files with syntax errors:" out\%2') do set SYN=%%a
for /f "tokens=6" %%a in ('findstr /c:"files with real errors:" out\%3') do set BAD=%%a
if "%SYN%"=="?" goto :CheckFail
if "%BAD%"=="?" goto :CheckFail
set FAIL=
set /a CMP=SYN-%4 >nul 2>&1
if %CMP% GTR 0 set FAIL=1
set /a CMP=BAD-%5 >nul 2>&1
if %CMP% GTR 0 set FAIL=1
if defined FAIL (
    echo %1: syntax=%SYN% ^(ref %4^) realBadFiles=%BAD% ^(ref %5^)  ** REGRESSION **
    exit /b 1
)
echo %1: syntax=%SYN% ^(ref %4^) realBadFiles=%BAD% ^(ref %5^)  ok
exit /b 0

:CheckFail
echo %1: report missing or malformed (syntax=%SYN% realBadFiles=%BAD%)  ** FAILED **
exit /b 1
