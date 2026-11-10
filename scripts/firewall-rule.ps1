#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Adds (or removes) the Windows Firewall rule that lets Apple devices reach the AirPlay engine.

.DESCRIPTION
    The app offers the same thing behind its "Allow through Windows Firewall" button, and the MSIX
    package installs equivalent rules by itself. This script is for unattended set-up, or for doing it by hand.

    The rule is bound to the engine executable rather than to port numbers, and it only applies on
    Private and Domain networks. For reference, the engine listens on:

        TCP 7000, 7001, 7100        control channel and mirrored video
        UDP 6000, 6001, 7011        audio and clock synchronisation
        UDP 5353                    mDNS discovery

    Existing inbound rules for the same executable are removed first. That matters: if "Cancel" was ever
    chosen on Windows' own "allow this app" prompt, Windows stored a blocking rule, and a blocking rule
    wins over every allow rule.

.EXAMPLE
    pwsh scripts/firewall-rule.ps1 -EnginePath 'C:\Apps\MirrorDeck\engine\bin\uxplay.exe'
    pwsh scripts/firewall-rule.ps1 -EnginePath 'C:\Apps\MirrorDeck\engine\bin\uxplay.exe' -Remove
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EnginePath,

    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$ruleName = 'MirrorDeck AirPlay receiver'

if (-not $Remove -and -not (Test-Path -LiteralPath $EnginePath)) {
    throw "No file at '$EnginePath'. Pass the full path of uxplay.exe (it is in the app folder under engine\bin)."
}
$program = [System.IO.Path]::GetFullPath($EnginePath)

# Each argument is one quoted string, so that nothing in it (spaces in the path, the comma in
# "profile=private,domain") is left to PowerShell's argument parsing.
& netsh advfirewall firewall delete rule 'name=all' 'dir=in' "program=$program" | Out-Null

if ($Remove) {
    Write-Host "Removed the inbound firewall rules for $program"
    return
}

& netsh advfirewall firewall add rule "name=$ruleName" 'dir=in' 'action=allow' "program=$program" `
    'profile=private,domain' 'enable=yes' `
    'description=Lets Apple devices on your home or work network find this PC and mirror to it.' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "netsh failed with exit code $LASTEXITCODE." }

Write-Host "Added firewall rule '$ruleName' for $program (Private and Domain networks)."
$public = Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public'
if ($public) {
    Write-Warning ("These networks are set to Public, where the rule does not apply: " + (($public | ForEach-Object Name) -join ', ') +
        ". If one of them is your home or office network, switch it to Private in Settings > Network & internet.")
}
