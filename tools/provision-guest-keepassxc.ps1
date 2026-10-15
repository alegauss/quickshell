#Requires -Version 5.1
<#
.SYNOPSIS
  Puts KeePassXC's command line in the guest, for KdbxWriterTests to open the vaults the client
  writes (QS305). Run once; running it again changes nothing that is already right.

.DESCRIPTION
  The KDBX 4 writer is proven by the reader that matters, not by one of this repository's own:
  keepassxc-cli opens what it wrote and exports every field back. The portable release needs no
  installing, so this unpacks it at C:\ProgramData\quickshell-cases\keepassxc\, inside the guest only.
  Nothing is started and nothing is registered: the tests run keepassxc-cli.exe and it exits.

  The archive is downloaded on the host by whoever runs this and checked there; this script checks
  it again against the SHA-256 it is given before it carries it in.

.PARAMETER Zip
  The portable archive, KeePassXC-<version>-Win64.zip, from
  https://github.com/keepassxreboot/keepassxc/releases.

.PARAMETER Sha256
  Its published SHA-256, from the .DIGEST file beside it in the same release.

.EXAMPLE
  .\tools\provision-guest-keepassxc.ps1 -Zip KeePassXC-2.7.12-Win64.zip -Sha256 958234b0669d757b53eacf42bdd5de0fa1cc1ab7527709ddf4f7e29c06a8305f
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Zip,
    [Parameter(Mandatory)] [string] $Sha256,
    [string] $Vmx
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'provision-guest-keepassxc'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

$stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $Zip).Path)
try { $actual = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
finally { $stream.Dispose() }

if ($actual -ne $Sha256.ToLowerInvariant()) {
    Refuse "$Zip is not the archive that was published: its SHA-256 is $actual" 'Download it again from the KeePassXC releases and check it against that release''s .DIGEST.'
}

$vmxPath = Connect-Guest -Vmx $Vmx

$stage = Join-Path ([IO.Path]::GetTempPath()) 'quickshell-vm-keepassxc'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage

Copy-Item -LiteralPath $Zip -Destination (Join-Path $stage 'keepassxc.zip')

@"
`$ErrorActionPreference = 'Stop'
`$folder = 'C:\ProgramData\quickshell-cases\keepassxc'
`$marker = Join-Path `$folder 'archive.sha256'

if ((Test-Path -LiteralPath `$marker) -and (Get-Content -LiteralPath `$marker -Raw).Trim() -eq '$actual' -and (Test-Path -LiteralPath (Join-Path `$folder 'keepassxc-cli.exe'))) {
    'OK'
    exit 0
}

`$unpacked = Join-Path `$env:TEMP 'quickshell-keepassxc'
if (Test-Path -LiteralPath `$unpacked) { Remove-Item -LiteralPath `$unpacked -Recurse -Force }
Expand-Archive -LiteralPath '$script:GuestSync\keepassxc.zip' -DestinationPath `$unpacked

# The archive holds one folder named for its release; what the tests look for is its contents.
`$cli = Get-ChildItem -LiteralPath `$unpacked -Recurse -Filter 'keepassxc-cli.exe' | Select-Object -First 1
if (-not `$cli) { 'REFUSED the archive holds no keepassxc-cli.exe'; exit 2 }

if (Test-Path -LiteralPath `$folder) { Remove-Item -LiteralPath `$folder -Recurse -Force }
`$null = New-Item -ItemType Directory -Path `$folder
Copy-Item -Path (Join-Path `$cli.DirectoryName '*') -Destination `$folder -Recurse
Set-Content -LiteralPath `$marker -Value '$actual' -Encoding ascii
Remove-Item -LiteralPath `$unpacked -Recurse -Force

'OK placed KeePassXC ' + (Get-Item -LiteralPath (Join-Path `$folder 'keepassxc-cli.exe')).VersionInfo.ProductVersion
"@ | Set-Content -LiteralPath (Join-Path $stage 'keepassxc.ps1') -Encoding ascii

@"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "$script:GuestSync\keepassxc.ps1" > "$script:GuestSync\keepassxc.log" 2>&1
exit /b %ERRORLEVEL%
"@ | Set-Content -LiteralPath (Join-Path $stage 'keepassxc.cmd') -Encoding ascii

$null = Invoke-VmRun -Guest -Arguments @('createDirectoryInGuest', $vmxPath, $script:GuestSync)

foreach ($file in @('keepassxc.zip', 'keepassxc.ps1', 'keepassxc.cmd')) {
    $sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage $file), "$script:GuestSync\$file")
    if (-not $sent.Ok) { Refuse "could not copy $file into the guest: $($sent.Output)" }
}

Write-Host '  guest       placing KeePassXC'
$ran = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, "$script:GuestSync\keepassxc.cmd")

$log = Join-Path $stage 'keepassxc.log'
$null = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\keepassxc.log", $log)
$said = if (Test-Path -LiteralPath $log) { (Read-ConsoleText $log).Trim() } else { '' }

if (-not $ran.Ok -or $said -notmatch '^OK') {
    Refuse "KeePassXC was not placed in the guest: $said $($ran.Output)" 'The account vmrun uses must be an administrator in the guest.'
}

$done = $said -replace '^OK ?', ''
Write-Host "  guest       $(if ($done) { $done } else { 'already placed' })"
exit 0
