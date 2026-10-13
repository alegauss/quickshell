#Requires -Version 5.1
<#
.SYNOPSIS
  Gives the guest an SSH server for the UI cases on a connected tab (QS273). Run once; running it
  again changes nothing that is already right.

.DESCRIPTION
  The owner chose Windows' own OpenSSH Server in the guest (2026-10-13) over Docker there or the host's
  fixture opened to the VM. It serves one case: a session tab connected to a real server, read through
  the accessibility tree.

  What it sets up, inside the guest only:
    - the OpenSSH.Server capability, installed if it is not;
    - sshd on 127.0.0.1:2240, loopback only, key sign-in only. Not 2222: the Docker fixture's tests
      decide whether their fixture is up by a TCP connect to 127.0.0.1:2222, and a server answering
      there that is not theirs would turn every one of them red;
    - a local account, probe, with no rights beyond Users and a password nobody is told, whose one
      authorized key is a pair made in the guest for the cases, C:\ProgramData\quickshell-cases\
      probe_ed25519, kept outside any profile in C:\ProgramData\ssh\probe_authorized_keys with the
      access OpenSSH requires;
    - the service started, and starting with the guest.

  Nothing on the host is touched, and no port leaves the guest.

.PARAMETER Vmx
  The guest, where it is not the one the settings file names.

.NOTES
  The guest account vmrun signs in as must be an administrator, which installing a capability and
  making an account need. Credentials come from the file vm-guest.ps1 names.
#>
[CmdletBinding()]
param([string] $Vmx)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'provision-guest-sshd'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

$vmxPath = Connect-Guest -Vmx $Vmx

$stage = Join-Path ([IO.Path]::GetTempPath()) 'quickshell-vm-sshd'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage

@"
`$ErrorActionPreference = 'Stop'
`$said = New-Object System.Collections.Generic.List[string]

`$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not `$admin) { 'REFUSED not an administrator'; exit 2 }

# The server, installed where it is not.
`$capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server*'
if (`$capability.State -eq 'NotPresent') {
    `$null = Add-WindowsCapability -Online -Name `$capability.Name
    `$capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server*'
}

# Windows finishes some installs only on a restart, which the host does and then runs this again.
if (`$capability.State -ne 'Installed') { 'RESTART ' + `$capability.State; exit 4 }

# The account: Users only, a password nobody is told, never expiring so the case does not age out.
if (-not (Get-LocalUser -Name 'probe' -ErrorAction SilentlyContinue)) {
    `$secret = ConvertTo-SecureString ([Guid]::NewGuid().ToString('N') + 'aA1!') -AsPlainText -Force
    `$null = New-LocalUser -Name 'probe' -Password `$secret -PasswordNeverExpires -AccountNeverExpires -Description 'quickshell UI cases (QS273)'
    Add-LocalGroupMember -Group (Get-LocalGroup -SID 'S-1-5-32-545').Name -Member 'probe'
    `$said.Add('made the probe account')
}

# The case's own key pair, made here and kept here: the fixture's keys are made per machine and
# never committed, so the guest's copy of the tree has none. The client reads the private half from
# this path, which connected.json names.
`$pair = 'C:\ProgramData\quickshell-cases\probe_ed25519'
`$null = New-Item -ItemType Directory -Path (Split-Path `$pair) -Force
if (-not (Test-Path -LiteralPath `$pair)) {
    & "`$env:WINDIR\System32\OpenSSH\ssh-keygen.exe" -q -t ed25519 -N '""' -C 'quickshell-ui-cases' -f `$pair | Out-Null
    if (-not (Test-Path -LiteralPath `$pair)) { 'REFUSED ssh-keygen made no key'; exit 5 }
    `$said.Add('made the case key')
}

# Its one key, outside any profile, with the access sshd insists on: SYSTEM and Administrators own
# it, probe may read it, and nothing is inherited.
`$keys = 'C:\ProgramData\ssh\probe_authorized_keys'
`$null = New-Item -ItemType Directory -Path 'C:\ProgramData\ssh' -Force
Set-Content -LiteralPath `$keys -Value (Get-Content -LiteralPath (`$pair + '.pub') -Raw).Trim() -Encoding ascii
& icacls.exe `$keys /inheritance:r /grant '*S-1-5-18:F' /grant '*S-1-5-32-544:F' /grant 'probe:R' | Out-Null

# The configuration: a block of its own at the end, written once and replaced whole after that.
`$config = 'C:\ProgramData\ssh\sshd_config'
if (-not (Test-Path -LiteralPath `$config)) {
    Start-Service sshd
    Stop-Service sshd
}
`$text = Get-Content -LiteralPath `$config -Raw
`$block = @'
# quickshell UI cases (QS273): loopback only, keys only, one account.
Port 2240
ListenAddress 127.0.0.1
PasswordAuthentication no
'@
`$match = @'
# quickshell UI cases (QS273)
Match User probe
    AuthorizedKeysFile __PROGRAMDATA__/ssh/probe_authorized_keys
'@
`$text = [regex]::Replace(`$text, '(?ms)^# quickshell UI cases \(QS273\).*?(?=^\S|\z)', '')
`$text = `$block + "``r``n" + `$text.TrimEnd() + "``r``n``r``n" + `$match + "``r``n"
Set-Content -LiteralPath `$config -Value `$text -Encoding ascii

Set-Service sshd -StartupType Automatic
Restart-Service sshd

Start-Sleep -Seconds 2
`$listening = Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort 2240 -State Listen -ErrorAction SilentlyContinue
if (-not `$listening) { 'REFUSED sshd is not listening on 127.0.0.1:2240'; exit 3 }

`$said.Add('sshd listening on 127.0.0.1:2240')
'OK ' + (`$said -join '; ')
"@ | Set-Content -LiteralPath (Join-Path $stage 'sshd.ps1') -Encoding ascii

@"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "$script:GuestSync\sshd.ps1" > "$script:GuestSync\sshd.log" 2>&1
exit /b %ERRORLEVEL%
"@ | Set-Content -LiteralPath (Join-Path $stage 'sshd.cmd') -Encoding ascii

$null = Invoke-VmRun -Guest -Arguments @('createDirectoryInGuest', $vmxPath, $script:GuestSync)

foreach ($file in @('sshd.ps1', 'sshd.cmd')) {
    $sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage $file), "$script:GuestSync\$file")
    if (-not $sent.Ok) { Refuse "could not copy $file into the guest: $($sent.Output)" }
}

function Set-Up {
    $ran = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, "$script:GuestSync\sshd.cmd")
    $log = Join-Path $stage 'sshd.log'
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
    $null = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\sshd.log", $log)
    $text = if (Test-Path -LiteralPath $log) { (Read-ConsoleText $log).Trim() } else { '' }
    return @{ Ran = $ran; Said = $text }
}

Write-Host '  guest       setting up sshd (installing the capability can take minutes the first time)'
$result = Set-Up

# Installed, but not usable until Windows restarts: a soft restart, which the guest's own auto-logon
# brings back to a desk, and the set-up once more.
if ($result.Said -match '^RESTART') {
    Write-Host "  guest       $($result.Said -replace '^RESTART ', 'the capability is ') - restarting the guest"
    $null = Invoke-VmRun -Arguments @('reset', $vmxPath, 'soft')
    Start-Sleep -Seconds 60
    $vmxPath = Connect-Guest -Vmx $Vmx
    $result = Set-Up
}

$ran = $result.Ran
$said = $result.Said

if (-not $ran.Ok -or $said -notmatch '^OK') {
    Refuse "the guest's sshd was not set up: $said $($ran.Output)" 'The account vmrun uses must be an administrator in the guest.'
}

Write-Host "  guest       $($said -replace '^OK ', '')"
exit 0
