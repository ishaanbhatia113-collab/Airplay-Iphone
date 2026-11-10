<#
.SYNOPSIS
    Packs the published app into an MSIX package for sideloading or for the Microsoft Store.

.DESCRIPTION
    Needs the Windows SDK (for makeappx.exe and signtool.exe); it is part of Visual Studio's
    "Windows application development" workload and can also be installed on its own.

    Steps: publish the app (unless -PublishDirectory points at an existing build), assemble the package
    layout (app + tiles + manifest), pack it, and optionally sign it with a self-signed test certificate.

    For the Microsoft Store: reserve the app name in Partner Center, then pass the three identity values
    shown under "Product identity". Do not sign the package; the Store signs it for you.

.EXAMPLE
    # a package you can install on your own PC
    pwsh packaging/make-msix.ps1 -SelfSign

.EXAMPLE
    # a package for Store submission
    pwsh packaging/make-msix.ps1 -IdentityName '12345Contoso.MirrorDeck' `
        -Publisher 'CN=0A1B2C3D-0000-0000-0000-000000000000' -PublisherDisplayName 'Contoso' -Version 1.0.0.0
#>
[CmdletBinding()]
param(
    [string]$IdentityName = 'MirrorDeck',

    # Must be identical to the subject of the certificate that signs the package.
    [string]$Publisher = 'CN=MirrorDeck Test',

    [string]$PublisherDisplayName = 'MirrorDeck (test build)',

    # Four parts. The Store requires the last one to be 0.
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0.0',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    # An existing "dotnet publish" output. When omitted, scripts\publish.ps1 is run.
    [string]$PublishDirectory,

    # Create (or reuse) a self-signed certificate for $Publisher and sign the package with it.
    [switch]$SelfSign
)

$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent $PSScriptRoot
$architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$outputDirectory = Join-Path $PSScriptRoot 'out'
$layout = Join-Path $outputDirectory 'layout'
$package = Join-Path $outputDirectory "MirrorDeck_${Version}_${architecture}.msix"

function Find-SdkTool([string]$name) {
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ } |
        ForEach-Object { Join-Path $_ 'Windows Kits\10\bin' } | Where-Object { Test-Path -LiteralPath $_ }
    $hostArchitecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $candidates = foreach ($root in $roots) {
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "$hostArchitecture\$name" } |
            Where-Object { Test-Path -LiteralPath $_ }
    }
    $tool = $candidates | Select-Object -First 1
    if (-not $tool) {
        throw "$name was not found. Install the Windows SDK (https://developer.microsoft.com/windows/downloads/windows-sdk/)."
    }
    return $tool
}

# ---- 1. the app --------------------------------------------------------------------------------------

if (-not $PublishDirectory) {
    $PublishDirectory = Join-Path $repository 'publish'
    & (Join-Path $repository 'scripts\publish.ps1') -Runtime $Runtime -Output $PublishDirectory
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'MirrorDeck.exe'))) {
    throw "No MirrorDeck.exe in '$PublishDirectory'."
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'engine\bin\uxplay.exe'))) {
    # The manifest's firewall rules name this file, and an app without its engine is of no use to anyone.
    throw "The published app has no AirPlay engine (engine\bin\uxplay.exe). Run scripts\build-engine.ps1, then try again."
}

# ---- 2. the package layout ---------------------------------------------------------------------------

if (Test-Path -LiteralPath $outputDirectory) { Remove-Item -LiteralPath $outputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $layout | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $layout -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets') -Destination $layout -Recurse

$values = @{
    IdentityName         = $IdentityName
    Publisher            = $Publisher
    PublisherDisplayName = $PublisherDisplayName
    Version              = $Version
    Architecture         = $architecture
}
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
foreach ($key in $values.Keys) {
    $manifest = $manifest.Replace("{{$key}}", [System.Security.SecurityElement]::Escape($values[$key]))
}
# UTF-8 without a byte order mark
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding $false))

# ---- 3. pack -----------------------------------------------------------------------------------------

$makeappx = Find-SdkTool 'makeappx.exe'
& $makeappx pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE (it validates the manifest; its message above says what is wrong)." }

# ---- 4. sign (test packages only) ----------------------------------------------------------------------

if ($SelfSign) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1
    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
            -KeyUsage DigitalSignature -FriendlyName 'MirrorDeck test signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    $signtool = Find-SdkTool 'signtool.exe'
    & $signtool sign /fd SHA256 /sha1 $certificate.Thumbprint $package
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }

    $publicCertificate = Join-Path $outputDirectory 'MirrorDeck-test.cer'
    Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null

    Write-Host ""
    Write-Host "Signed test package: $package"
    Write-Host "To install it, trust the test certificate once (elevated PowerShell), then add the package:"
    Write-Host "  Import-Certificate -FilePath '$publicCertificate' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "  Add-AppxPackage -Path '$package'"
} else {
    Write-Host ""
    Write-Host "Unsigned package: $package"
    Write-Host "Upload it to Partner Center as it is (the Store signs it), or run again with -SelfSign to test it locally."
}
