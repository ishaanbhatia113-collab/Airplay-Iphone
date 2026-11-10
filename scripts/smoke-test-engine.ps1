<#
.SYNOPSIS
    Starts the AirPlay engine on its own for a few seconds and checks that it comes up.

.DESCRIPTION
    A quick answer to "is the engine bundle complete on this PC?", independent of the app: the engine is
    started with its picture and sound sent nowhere (so no window opens and no GPU or sound card is
    needed), and the script waits for the line UxPlay prints once it is listening for devices.

    It uses random ports, so it can run while MirrorDeck itself is running. On first use Windows may ask
    whether uxplay.exe may accept connections; the answer does not affect this test.

.EXAMPLE
    pwsh scripts/smoke-test-engine.ps1
    pwsh scripts/smoke-test-engine.ps1 -EnginePath 'C:\Apps\MirrorDeck\engine\bin\uxplay.exe'
#>
[CmdletBinding()]
param(
    [string]$EnginePath,

    # how long to wait for the engine; the first start indexes the GStreamer plugins and can be slow
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

if (-not $EnginePath) {
    $EnginePath = Join-Path (Join-Path (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'engine') 'dist') 'bin') 'uxplay.exe'
}
if (-not (Test-Path -LiteralPath $EnginePath)) {
    throw "No engine at '$EnginePath'. Build it with scripts/build-engine.ps1, or pass -EnginePath."
}
$EnginePath = (Resolve-Path -LiteralPath $EnginePath).Path
$binDirectory = Split-Path -Parent $EnginePath

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("mirrordeck-smoke-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$stdout = Join-Path $work 'stdout.txt'
$stderr = Join-Path $work 'stderr.txt'

# The same isolation the app applies: only the bundle's own DLLs, and a plugin cache of its own.
$saved = @{ PATH = $env:PATH; GST_REGISTRY_1_0 = $env:GST_REGISTRY_1_0 }
$env:PATH = $binDirectory + [System.IO.Path]::PathSeparator + $env:PATH
$env:GST_REGISTRY_1_0 = Join-Path $work 'gstreamer-registry.bin'

$process = $null
$ready = $false
$output = ''
try {
    $start = @{
        FilePath               = $EnginePath
        ArgumentList           = '-n MirrorDeckSmokeTest -nh -vs fakesink -as fakesink'
        WorkingDirectory       = $work
        RedirectStandardOutput = $stdout
        RedirectStandardError  = $stderr
        PassThru               = $true
    }
    # keep the engine's console window out of sight (the parameter only exists on Windows)
    if ($PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows) { $start.WindowStyle = 'Hidden' }
    $process = Start-Process @start

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $output = (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue) + "`n" +
                  (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue)
        if ($output -match 'Initialized server socket') { $ready = $true; break }
        if ($process.HasExited) { break }
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
    $env:PATH = $saved.PATH
    $env:GST_REGISTRY_1_0 = $saved.GST_REGISTRY_1_0
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($ready) {
    Write-Host "OK: the engine started and is listening for devices."
    Write-Host ($output.Trim() -split "`r?`n" | Where-Object { $_ -match '^UxPlay \d|network ports|MAC address' } | Out-String).Trim()
    exit 0
}

Write-Host "FAILED: the engine did not report that it is listening. Its output was:"
Write-Host "----------------------------------------------------------------------"
Write-Host $output.Trim()
Write-Host "----------------------------------------------------------------------"
if ($output -match "Required gstreamer plugin") {
    Write-Host "A GStreamer plugin is missing from the bundle; rebuild it with scripts/build-engine.ps1."
} elseif (-not $output.Trim()) {
    Write-Host "No output at all usually means a DLL is missing next to uxplay.exe, or security software blocked it."
}
exit 1
