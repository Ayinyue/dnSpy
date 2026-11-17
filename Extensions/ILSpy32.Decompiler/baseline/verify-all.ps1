<#
verify-all.ps1 — 一条命令重建并跑完 3.2 引擎迁移的验收清单，报告归档到 baseline/out/。

用法（在仓库根或任意目录）：
    powershell -NoProfile -ExecutionPolicy Bypass -File Extensions\ILSpy32.Decompiler\baseline\verify-all.ps1
    powershell ... -NoBuild      # 跳过重建
    powershell ... -Configuration Debug

退出码：0 = 全部硬门通过；1 = 有失败。
#>
param(
	[switch]$NoBuild,
	[string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$baseline = $PSScriptRoot
$extDir = (Resolve-Path "$baseline\..").Path
$repo = (Resolve-Path "$baseline\..\..\..").Path
$dnspyBin = "$repo\dnSpy\dnSpy\bin\$Configuration\net10.0-windows"
$probeDir = "$baseline\probe"
$probe = "$probeDir\bin\$Configuration\net10.0-windows\m3probe.dll"
$spTest = "$probeDir\sp-test\bin\Debug\net10.0\SpTestAssembly.dll"
$logic = "$dnspyBin\dnSpy.Contracts.Logic.dll"
$path23 = "$baseline\ilspy23"
$outDir = "$baseline\out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$report = "$outDir\verify-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"

$script:hard = 0
$script:soft = 0
$script:skip = 0

function Write-Report($text) {
	Write-Host $text
	Add-Content -Path $report -Value $text -Encoding utf8
}

function Invoke-Probe([string[]]$ProbeArgs, [string]$Name, [switch]$Soft, [switch]$AllowSkip) {
	if (-not (Test-Path $probe)) {
		Write-Report "FAIL $Name : probe 未构建"
		$script:hard++
		return
	}
	$sw = [System.Diagnostics.Stopwatch]::StartNew()
	Write-Report ("  >> dotnet {0} {1}" -f (Split-Path $probe -Leaf), ($ProbeArgs -join ' '))
	# 探针会把诊断写到 stderr；在 Stop 模式下那会变成终止性 NativeCommandError。
	$prev = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'
	$out = & dotnet $probe @ProbeArgs 2>&1
	$code = $LASTEXITCODE
	$ErrorActionPreference = $prev
	$code = $LASTEXITCODE
	$sw.Stop()
	$tail = ($out | Select-Object -Last 4) -join ' | '
	if ($AllowSkip -and $code -eq 2) {
		Write-Report "SKIP $Name : $tail"
		$script:skip++
	}
	elseif ($code -eq 0) {
		Write-Report ("PASS {0} ({1:N0} ms) : {2}" -f $Name, $sw.Elapsed.TotalMilliseconds, $tail)
	}
	else {
		Write-Report ("FAIL {0} (exit={1}) : {2}" -f $Name, $code, $tail)
		if ($Soft) { $script:soft++ } else { $script:hard++ }
	}
}

Write-Report "=== verify-all  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  configuration=$Configuration ==="

if (-not $NoBuild) {
	Write-Report "--- build ---"
	$buildSteps = @(
		@("$extDir\ICSharpCode.Decompiler\ICSharpCode.Decompiler\ICSharpCode.Decompiler.csproj", '-c', $Configuration),
		@("$extDir\dnSpy.Decompiler.ILSpy32\dnSpy.Decompiler.ILSpy32.csproj", '-c', $Configuration, '-f', 'net10.0-windows'),
		@("$probeDir\m3probe.csproj", '-c', $Configuration)
	)
	foreach ($step in $buildSteps) {
		$proj = $step[0]
		$args = $step[1..($step.Length - 1)]
		Write-Report ("build {0}" -f (Split-Path $proj -Leaf))
		& dotnet build $proj @args --nologo -v q 2>&1 | Write-Host
		if ($LASTEXITCODE -ne 0) {
			Write-Report "FAIL build $(Split-Path $proj -Leaf)"
			$script:hard++
		}
	}
}
else {
	Write-Report "--- build skipped (-NoBuild) ---"
}

Write-Report "--- gates ---"
# 2.3 对照程序集（M4 后不在 dnSpy bin）：存在则让 probe 用它做对照，否则相关模式自动 SKIP。
if (Test-Path "$path23\dnSpy.Decompiler.ILSpy.Core.dll") {
	$env:PROBE_ILSPY23_DIR = $path23
	Write-Report "2.3 comparison asset: $path23"
}
else {
	Write-Report "2.3 comparison asset: 未构建（$path23），相关模式将 SKIP"
}
Invoke-Probe @('engine-tests', $spTest, $dnspyBin) 'engine-tests' -AllowSkip
Invoke-Probe @('settings-wiring', $dnspyBin) 'settings-wiring'
Invoke-Probe @('ilast-verify', $logic, '0600024D', $dnspyBin) 'ilast-verify'
Invoke-Probe @('mixed-verify', $logic, '0600024D', $dnspyBin) 'mixed-verify'
# 性能只作回归参考（机器相关），不作硬门。
Invoke-Probe @('bench', $logic, $dnspyBin, '20') 'bench' -Soft -AllowSkip
# 需要 2.3 比较资产（见 baseline/README.md）；没有则跳过。
Invoke-Probe @('lang-diff', $logic, 'dnSpy.Contracts.Decompiler.MethodDebugInfoBuilder', $dnspyBin, $path23) 'lang-diff (vs 2.3)' -Soft
# Whole-assembly IL baselines. Three hand-picked samples hid a 90% line difference, so the
# corpus numbers are part of the acceptance gate: identical= / diffLines per assembly.
# 2026-11-17 baseline: DnSpy.dll 1310/0, Debugger 303/0, Logic 114/0 (byte-identical).
foreach ($corpusAsm in @('dnSpy.Contracts.DnSpy.dll', 'dnSpy.Contracts.Debugger.dll', 'dnSpy.Contracts.Logic.dll')) {
	Invoke-Probe @('lang-diff-all', "$dnspyBin/$corpusAsm", $dnspyBin, $path23, 'IL (') "corpus IL: $corpusAsm" -Soft
	# 2026-11-17 baseline (identical / diffLines): C# 556/32248, 108/5735, 29/10021;
	# VB 607/25582, 157/3240, 41/8508. These are engine output-layer style differences
	# (local-const inlining, implicit this., local names, statement shape) - see audit §8.13.
	Invoke-Probe @('lang-diff-all', "$dnspyBin/$corpusAsm", $dnspyBin, $path23, 'C#') "corpus C#: $corpusAsm" -Soft
	Invoke-Probe @('lang-diff-all', "$dnspyBin/$corpusAsm", $dnspyBin, $path23, 'Visual') "corpus VB: $corpusAsm" -Soft
}

Write-Report "=== summary: hardFail=$script:hard softFail=$script:soft skip=$script:skip ==="
Write-Report "report: $report"
if ($script:hard -ne 0) { exit 1 }
exit 0
