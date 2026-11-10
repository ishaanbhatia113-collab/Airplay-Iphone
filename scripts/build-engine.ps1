<#
.SYNOPSIS
    Builds the AirPlay engine from a normal PowerShell prompt by running engine/build-engine.sh inside MSYS2.

.DESCRIPTION
    The engine (UxPlay with GStreamer) is a native program that is compiled with the MSYS2 tool chain.
    Install MSYS2 from https://www.msys2.org first (default location C:\msys64). Everything else,
    including the compiler and GStreamer, is installed by the script through MSYS2's package manager.

.EXAMPLE
    pwsh scripts/build-engine.ps1
    pwsh scripts/build-engine.ps1 -AllPlugins          # needed for YouTube casting (HLS)
    pwsh scripts/build-engine.ps1 -Environment CLANGARM64   # Windows on ARM
#>
[CmdletBinding()]
param(
    [string]$Msys2Root = $(if ($env:MSYS2_ROOT) { $env:MSYS2_ROOT } else { 'C:\msys64' }),

    [ValidateSet('UCRT64', 'CLANGARM64', 'MINGW64')]
    [string]$Environment = 'UCRT64',

    [switch]$AllPlugins,
    [switch]$WithBonjour,
    [switch]$SkipInstall,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$bash = Join-Path $Msys2Root 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash)) {
    throw "MSYS2 was not found at '$Msys2Root'. Install it from https://www.msys2.org, or pass -Msys2Root."
}

$flags = @()
if ($AllPlugins)  { $flags += '--all-plugins' }
if ($WithBonjour) { $flags += '--with-bonjour' }
if ($SkipInstall) { $flags += '--skip-install' }
if ($Clean)       { $flags += '--clean' }

$repository = Split-Path -Parent $PSScriptRoot
$previous = @{ MSYSTEM = $env:MSYSTEM; CHERE_INVOKING = $env:CHERE_INVOKING }
Push-Location -LiteralPath $repository
try {
    # MSYSTEM selects the tool chain; CHERE_INVOKING keeps the login shell in the current directory.
    $env:MSYSTEM = $Environment
    $env:CHERE_INVOKING = '1'
    & $bash -lc ("./engine/build-engine.sh " + ($flags -join ' '))
    if ($LASTEXITCODE -ne 0) { throw "engine/build-engine.sh failed with exit code $LASTEXITCODE." }
}
finally {
    $env:MSYSTEM = $previous.MSYSTEM
    $env:CHERE_INVOKING = $previous.CHERE_INVOKING
    Pop-Location
}
