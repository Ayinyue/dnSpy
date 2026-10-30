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
    $head = (& git rev-parse --short HEAD).Trim()
    Write-Host "submodule HEAD: $head"

    & git diff --quiet
    if ($LASTEXITCODE -eq 0) {
        Write-Host "applying engine.patch ..."
        & git apply --verbose $patch
        if ($LASTEXITCODE -ne 0) { throw 'git apply failed' }
    }
    else {
        Write-Host "submodule already has local changes - skipping patch"
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
