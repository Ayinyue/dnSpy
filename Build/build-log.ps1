<#
    build-log.ps1 - run a build script under a timestamped log directory.

    Wraps build.ps1 so that every build produces:
      BuildLogs\build_<yyyyMMdd_HHmmss>_<label>.log   full transcript of the run
      BuildLogs\build_history.log                      one summary line per build

    Normally invoked by build.cmd, which forwards the build arguments through
    the DNSPY_BUILD_ARGS environment variable.

    Two implementation notes that are easy to get wrong:

    1. Output capture. build.ps1 reports progress with Write-Host, which under
       Windows PowerShell 5.1 bypasses the success stream. Both '2>&1' and a
       plain '> file' redirection silently DROP those lines; only '*>&1'
       captures them. Hence the '& $Script @splat *>&1' below.

    2. Argument binding. The arguments cannot simply be splatted as an array:
       '@array' passes elements POSITIONALLY, so a token like '-NoMsbuild'
       would be bound to $buildtfm and trip its ValidateSet. The parameter
       names are therefore read from the target script's AST and re-bound by
       name, mirroring how PowerShell itself would bind a command line.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Script,

    [string]$LogDir,

    # Used only when this script is called directly; build.cmd uses the
    # environment variable so that '-File' cannot swallow the arguments.
    [Parameter(ValueFromRemainingArguments = $true)]
    [object[]]$Rest
)

$ErrorActionPreference = 'Continue'

# PowerShell 5.1 renders localized error text (for example the ValidateSet
# failure message) through [Console]::OutputEncoding, which defaults to the OEM
# code page. On a Chinese Windows that turns every such message into mojibake
# such as the ValidateSet rejection text, so force UTF-8 up front.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ---- resolve what we are wrapping ---------------------------------------
$Script = (Resolve-Path -LiteralPath $Script -ErrorAction Stop).Path
$repoRoot = Split-Path -Parent $PSScriptRoot

$forwarded = @()
if ($env:DNSPY_BUILD_ARGS) {
    $forwarded = @($env:DNSPY_BUILD_ARGS -split '\s+' | Where-Object { $_ -ne '' })
}
if ($Rest) { $forwarded += @($Rest | ForEach-Object { [string]$_ }) }

# ---- read the target's parameter block -----------------------------------
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$tokens, [ref]$parseErrors)

$declared = @()      # ordered: positional binding follows declaration order
$isSwitch = @{}      # name -> $true when declared as [switch]
if ($ast.ParamBlock) {
    foreach ($p in $ast.ParamBlock.Parameters) {
        $name = $p.Name.VariablePath.UserPath
        $declared += $name
        foreach ($attr in $p.Attributes) {
            if ($attr -is [System.Management.Automation.Language.TypeConstraintAst] -and
                $attr.TypeName.Name -eq 'switch') {
                $isSwitch[$name] = $true
            }
        }
    }
}

# ---- re-bind the forwarded tokens by name -------------------------------
$splat = @{}
$positional = New-Object System.Collections.Generic.List[string]
$unknown = New-Object System.Collections.Generic.List[string]
$expectValueFor = $null

foreach ($tok in $forwarded) {
    if ($null -ne $expectValueFor) {
        $splat[$expectValueFor] = $tok
        $expectValueFor = $null
        continue
    }
    if ($tok.StartsWith('-') -and $tok.Length -gt 1) {
        $name = $tok.TrimStart('-')
        $match = $declared | Where-Object { $_ -eq $name } | Select-Object -First 1
        if ($match) {
            if ($isSwitch.ContainsKey($match)) {
                $splat[$match] = $true
            } else {
                $expectValueFor = $match
            }
        } else {
            $unknown.Add($tok)
        }
    } else {
        $positional.Add($tok)
    }
}

# positional tokens bind to the declared parameters, in declaration order,
# skipping the ones already supplied by name
if ($positional.Count -gt 0) {
    $taken = @{}
    foreach ($k in $splat.Keys) { $taken[$k] = $true }
    $queue = New-Object System.Collections.Generic.Queue[string]
    foreach ($t in $positional) { $queue.Enqueue($t) }
    foreach ($pname in $declared) {
        if ($queue.Count -eq 0) { break }
        if ($taken.ContainsKey($pname)) { continue }
        $splat[$pname] = $queue.Dequeue()
    }
    while ($queue.Count -gt 0) { $unknown.Add($queue.Dequeue()) }
}

if ($unknown.Count -gt 0) {
    Write-Warning ('build-log.ps1: ignoring unrecognized argument(s): {0}' -f ($unknown -join ' '))
}

# ---- log location --------------------------------------------------------
if (-not $LogDir) { $LogDir = Join-Path $repoRoot 'BuildLogs' }
if (-not (Test-Path -LiteralPath $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}
$LogDir = (Resolve-Path -LiteralPath $LogDir).Path

# label = positional/switch-free view of what is being built
$labelTokens = @($forwarded | Where-Object { -not $_.StartsWith('-') })
$label = ((($labelTokens | ForEach-Object { [string]$_ }) -join ' ') -replace '[\\/:*?"<>|]', '_') -replace '\s+', '_'
$label = $label.Trim('_')
if (-not $label) { $label = 'default' }

$start = Get-Date
$logFile = Join-Path $LogDir ('build_{0}_{1}.log' -f $start.ToString('yyyyMMdd_HHmmss'), $label)
$historyFile = Join-Path $LogDir 'build_history.log'

# ---- environment facts for the header ------------------------------------
$dotnetVer = 'n/a'
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $v = & dotnet --version 2>$null | Select-Object -First 1
    if ($v) { $dotnetVer = [string]$v }
}

$gitHead = 'n/a'; $gitBranch = 'n/a'; $gitState = 'n/a'
if (Get-Command git -ErrorAction SilentlyContinue) {
    $h = & git -C $repoRoot rev-parse --short HEAD 2>$null | Select-Object -First 1
    if ($h) { $gitHead = [string]$h }
    $b = & git -C $repoRoot rev-parse --abbrev-ref HEAD 2>$null | Select-Object -First 1
    if ($b) { $gitBranch = [string]$b }
    $st = & git -C $repoRoot status --porcelain 2>$null
    if ($st) { $gitState = '{0} changed path(s)' -f @($st).Count } else { $gitState = 'clean' }
}

# ---- UTF-8 log writer ----------------------------------------------------
# StreamWriter is used instead of Tee-Object -FilePath because on PowerShell
# 5.1 Tee-Object hardcodes UTF-16LE and offers no -Encoding switch, which
# makes the resulting logs awkward to grep from other tools.
$writer = New-Object System.IO.StreamWriter($logFile, $false, (New-Object System.Text.UTF8Encoding($false)))
$writer.AutoFlush = $true

function Write-Log {
    param([string[]]$Lines, [switch]$Quiet)
    foreach ($l in $Lines) {
        $writer.WriteLine($l)
        if (-not $Quiet) { Write-Host $l }
    }
}

$bar = '=' * 68
Write-Log -Lines @(
    $bar
    ' dnSpyEx build log'
    (' started    : {0}' -f $start.ToString('yyyy-MM-dd HH:mm:ss'))
    (' script     : {0}' -f $Script)
    (' arguments  : {0}' -f $(if ($forwarded) { $forwarded -join ' ' } else { '(none)' }))
    (' bound as   : {0}' -f $(if ($splat.Count) {
        ($splat.GetEnumerator() | Sort-Object Name | ForEach-Object { '{0}={1}' -f $_.Name, $_.Value }) -join ' '
    } else { '(defaults)' }))
    (' log file   : {0}' -f $logFile)
    (' host       : {0} {1}' -f $PSVersionTable.PSEdition, $PSVersionTable.PSVersion)
    (' dotnet     : {0}' -f $dotnetVer)
    (' git HEAD   : {0} ({1})' -f $gitHead, $gitBranch)
    (' git state  : {0}' -f $gitState)
    $bar
    ''
)

# ---- run the build -------------------------------------------------------
# '*>&1' is required: it folds Write-Host / Write-Warning / native output into
# the success stream so nothing is lost.
#
# The try/catch is load-bearing. A parameter-binding failure (e.g. a TFM that
# fails ValidateSet) never reaches the redirected pipeline and leaves
# $LASTEXITCODE untouched, so without this an invalid argument would be
# recorded as an empty but SUCCESSFUL build.
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$code = 0
try {
    & $Script @splat *>&1 | ForEach-Object {
        $writer.WriteLine([string]$_)
        Write-Host $_
    }
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
} catch {
    $code = 1
    $msg = 'build invocation failed: ' + $_.Exception.Message
    $writer.WriteLine($msg)
    Write-Host $msg -ForegroundColor Red
}
$sw.Stop()
$writer.Dispose()

# ---- footer + history ----------------------------------------------------
$verdict = if ($code -eq 0) { 'SUCCESS' } else { 'FAILURE' }
$footer = @(
    '',
    $bar
    (' finished   : {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
    (' duration   : {0:hh\:mm\:ss}' -f $sw.Elapsed)
    (' exit code  : {0}' -f $code)
    (' result     : {0}' -f $verdict)
    $bar
)

$fw = New-Object System.IO.StreamWriter($logFile, $true, (New-Object System.Text.UTF8Encoding($false)))
foreach ($l in $footer) { $fw.WriteLine($l) }
$fw.Dispose()

Add-Content -LiteralPath $historyFile -Encoding UTF8 -Value (
    '{0} | {1,-7} | exit={2} | {3:hh\:mm\:ss} | {4}' -f
    $start.ToString('yyyy-MM-dd HH:mm:ss'), $verdict, $code, $sw.Elapsed, (Split-Path -Leaf $logFile))

Write-Host ''
Write-Host ('[build] {0} - {1} (exit {2})' -f $verdict, $sw.Elapsed.ToString('hh\:mm\:ss'), $code)
Write-Host ('[log]    {0}' -f $logFile)

exit $code
