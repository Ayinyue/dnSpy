param(
    [switch]$Build,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sub  = Join-Path $root 'ICSharpCode.Decompiler'
$patch = Join-Path $root 'engine.patch'
$proj = Join-Path $sub 'ICSharpCode.Decompiler\ICSharpCode.Decompiler.csproj'

if (-not (Test-Path $patch)) { throw "engine.patch not found: $patch" }
if (-not (Test-Path $sub))   { throw "submodule not initialized: run 'git submodule update --init --depth 1 $sub'" }

Push-Location $sub
try {
    $head = (& git rev-parse HEAD).Trim()
    $pristine = (& git rev-parse v3.2.0 2>$null).Trim()
    Write-Host "submodule HEAD: $($head.Substring(0, [Math]::Min(7, $head.Length)))"

    if ($head -eq $pristine) {
        Write-Host "pristine v3.2.0 - applying engine.patch ..."
        & git apply --verbose $patch
        if ($LASTEXITCODE -ne 0) { throw 'git apply failed' }
    }
    else {
        Write-Host "submodule already carries the dnSpy patches - skipping engine.patch"
    }

    if ($Build) {
        Write-Host "building ..."
        & dotnet build $proj -c $Configuration -v m
        if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    }
}
finally {
    Pop-Location
}
