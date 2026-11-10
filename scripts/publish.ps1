<#
.SYNOPSIS
    Produces a self-contained build of MirrorDeck in publish\ that runs on a PC without .NET installed.

.DESCRIPTION
    The folder can be zipped and copied to another PC as it is, or turned into an MSIX package with
    packaging\make-msix.ps1. The AirPlay engine is included when engine\dist exists (build it first with
    scripts\build-engine.ps1); without it the app starts but reports that the engine is missing.

.EXAMPLE
    pwsh scripts/publish.ps1
    pwsh scripts/publish.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$Configuration = 'Release',

    [string]$Output
)

$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repository 'publish' }
$project = Join-Path $repository 'src\MirrorDeck\MirrorDeck.csproj'
$engine = Join-Path $repository 'engine\dist\bin\uxplay.exe'

if (-not (Test-Path -LiteralPath $engine)) {
    Write-Warning "engine\dist does not exist yet, so this build will not contain the AirPlay engine. Run scripts\build-engine.ps1 first."
}

if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }

& dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $Output `
    -p:PublishSingleFile=false `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$files = Get-ChildItem -LiteralPath $Output -Recurse -File
$megabytes = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB)
Write-Host ""
Write-Host "Published $($files.Count) files ($megabytes MB) to $Output"
if (Test-Path -LiteralPath (Join-Path $Output 'engine\bin\uxplay.exe')) {
    Write-Host "AirPlay engine: included"
} else {
    Write-Warning "AirPlay engine: NOT included"
}
Write-Host "Run it with: $(Join-Path $Output 'MirrorDeck.exe')"
