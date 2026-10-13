#Requires -Version 5.1
<#
.SYNOPSIS
  Puts a real Pageant in the guest, with the UI cases' key in PuTTY's format beside it (QS114). Run
  once; running it again changes nothing that is already right.

.DESCRIPTION
  The client reaches Pageant two ways: its named pipe, and before 0.78 a hidden window and a shared
  mapping. The window was tested against a stand-in answering as Pageant does; RealPageantTests
  meets the real one.

  What it does, inside the guest only (the owner allowed PuTTY there on 2026-10-13):
    - places the pageant.exe given at C:\ProgramData\quickshell-cases\pageant.exe — a standalone
      Pageant needs no installing, and the one to give it is 0.77, the last with only the window;
    - converts the key provision-guest-sshd.ps1 made, C:\ProgramData\quickshell-cases\probe_ed25519,
      to probe.ppk beside it, since Pageant loads PuTTY's format;
    - leaves no Pageant running. The tests start this one and end it: one left on the desk would
      answer for the stand-in window the other tests open, which therefore skip while it is there.

  The executable is downloaded on the host by whoever runs this and checked there; this script
  checks it again against the SHA-256 it is given before it carries it in.

.PARAMETER Pageant
  A standalone pageant.exe, as published at https://the.earth.li/~sgtatham/putty/<version>/w64/.

.PARAMETER Sha256
  Its published SHA-256, from the same release's sha256sums.

.NOTES
  Run provision-guest-sshd.ps1 first: it makes the key this loads.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Pageant,
    [Parameter(Mandatory)] [string] $Sha256,
    [string] $Vmx
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'provision-guest-pageant'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

$stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $Pageant).Path)
try { $actual = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
finally { $stream.Dispose() }

if ($actual -ne $Sha256.ToLowerInvariant()) {
    Refuse "$Pageant is not the Pageant that was published: its SHA-256 is $actual" 'Download it again from the PuTTY site and check it against that release''s sha256sums.'
}

$vmxPath = Connect-Guest -Vmx $Vmx

$stage = Join-Path ([IO.Path]::GetTempPath()) 'quickshell-vm-pageant'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage

Copy-Item -LiteralPath $Pageant -Destination (Join-Path $stage 'pageant.exe')

@"
`$ErrorActionPreference = 'Stop'
`$said = New-Object System.Collections.Generic.List[string]
`$key = 'C:\ProgramData\quickshell-cases\probe_ed25519'
`$ppk = 'C:\ProgramData\quickshell-cases\probe.ppk'
`$exe = 'C:\ProgramData\quickshell-cases\pageant.exe'

if (-not (Test-Path -LiteralPath `$key)) { 'REFUSED no case key: run provision-guest-sshd.ps1 first'; exit 2 }

# None left running, this one or an installed PuTTY's: each would answer for every window test.
if (Get-Process pageant -ErrorAction SilentlyContinue) {
    Get-Process pageant | Stop-Process -Force
    `$said.Add('ended the Pageant that was running')
}

`$given = (Get-FileHash -LiteralPath '$script:GuestSync\pageant.exe' -Algorithm SHA256).Hash
if (-not (Test-Path -LiteralPath `$exe) -or (Get-FileHash -LiteralPath `$exe -Algorithm SHA256).Hash -ne `$given) {
    Copy-Item -LiteralPath '$script:GuestSync\pageant.exe' -Destination `$exe -Force
    `$said.Add('placed Pageant ' + (Get-Item -LiteralPath `$exe).VersionInfo.ProductVersion)
}

& {
    # PuTTY's own format written here, not by PuTTYgen: on Windows PuTTYgen opens its window for a
    # key named on its command line, which on this unwatched desk waits for ever. An unencrypted
    # ed25519 key in PPK 3 is a public blob, the 32-byte seed as one string — PuTTY reads it as a
    # little-endian number of fixed length, so the bytes go in as OpenSSH holds them — and an
    # HMAC-SHA-256 over both with an empty key (PuTTY's manual, appendix C).
    `$text = (Get-Content -LiteralPath `$key) | Where-Object { `$_ -notmatch '^-----' }
    `$raw = [Convert]::FromBase64String((`$text -join ''))
    `$at = 15  # past 'openssh-key-v1\0'
    function Take([byte[]] `$from) { `$length = [int](`$from[`$script:at] * 16777216 + `$from[`$script:at + 1] * 65536 + `$from[`$script:at + 2] * 256 + `$from[`$script:at + 3]); `$script:at += 4; `$bytes = `$from[`$script:at..(`$script:at + `$length - 1)]; `$script:at += `$length; ,[byte[]]`$bytes }
    `$script:at = 15
    `$null = Take `$raw; `$null = Take `$raw; `$null = Take `$raw; `$script:at += 4  # cipher, kdf, options, count
    `$publicBlob = Take `$raw
    `$block = Take `$raw
    `$script:at = 8  # past the two check words
    `$null = Take `$block
    `$public = Take `$block
    `$secret = Take `$block
    `$comment = [Text.Encoding]::UTF8.GetString((Take `$block))
    `$seed = `$secret[0..31]
    function Field([byte[]] `$bytes) { [byte[]]@([byte](`$bytes.Length -shr 24), [byte](`$bytes.Length -shr 16 -band 255), [byte](`$bytes.Length -shr 8 -band 255), [byte](`$bytes.Length -band 255)) + `$bytes }
    [byte[]]`$privateBlob = Field ([byte[]]`$seed)
    `$ascii = [Text.Encoding]::ASCII
    [byte[]]`$macked = (Field `$ascii.GetBytes('ssh-ed25519')) + (Field `$ascii.GetBytes('none')) + (Field ([Text.Encoding]::UTF8.GetBytes(`$comment))) + (Field `$publicBlob) + (Field `$privateBlob)
    `$hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (,[byte[]]@())
    `$mac = (-join (`$hmac.ComputeHash(`$macked) | ForEach-Object { `$_.ToString('x2') }))
    function Lines([byte[]] `$bytes) { `$b64 = [Convert]::ToBase64String(`$bytes); `$out = @(); for (`$i = 0; `$i -lt `$b64.Length; `$i += 64) { `$out += `$b64.Substring(`$i, [Math]::Min(64, `$b64.Length - `$i)) }; ,`$out }
    `$pub = Lines `$publicBlob
    `$priv = Lines `$privateBlob
    `$ppkText = @('PuTTY-User-Key-File-3: ssh-ed25519', 'Encryption: none', ('Comment: ' + `$comment), ('Public-Lines: ' + `$pub.Count)) + `$pub + @(('Private-Lines: ' + `$priv.Count)) + `$priv + @(('Private-MAC: ' + `$mac))
    `$written = ((`$ppkText -join "``n") + "``n")
    if (-not (Test-Path -LiteralPath `$ppk) -or [IO.File]::ReadAllText(`$ppk) -ne `$written) {
        [IO.File]::WriteAllText(`$ppk, `$written)
        `$said.Add('converted the case key to probe.ppk')
    }
}

# Not at logon either, which an earlier form of this script arranged.
`$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path `$run -Name 'quickshell-pageant' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path `$run -Name 'quickshell-pageant'
    `$said.Add('Pageant no longer starts at logon')
}

'OK ' + (`$said -join '; ')
"@ | Set-Content -LiteralPath (Join-Path $stage 'pageant.ps1') -Encoding ascii

@"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "$script:GuestSync\pageant.ps1" > "$script:GuestSync\pageant.log" 2>&1
exit /b %ERRORLEVEL%
"@ | Set-Content -LiteralPath (Join-Path $stage 'pageant.cmd') -Encoding ascii

$null = Invoke-VmRun -Guest -Arguments @('createDirectoryInGuest', $vmxPath, $script:GuestSync)

foreach ($file in @('pageant.exe', 'pageant.ps1', 'pageant.cmd')) {
    $sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage $file), "$script:GuestSync\$file")
    if (-not $sent.Ok) { Refuse "could not copy $file into the guest: $($sent.Output)" }
}

Write-Host '  guest       setting up Pageant'
$ran = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, "$script:GuestSync\pageant.cmd")

$log = Join-Path $stage 'pageant.log'
$null = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\pageant.log", $log)
$said = if (Test-Path -LiteralPath $log) { (Read-ConsoleText $log).Trim() } else { '' }

if (-not $ran.Ok -or $said -notmatch '^OK') {
    Refuse "Pageant was not set up in the guest: $said $($ran.Output)" 'The account vmrun uses must be an administrator in the guest.'
}

$done = $said -replace '^OK ?', ''
Write-Host "  guest       $(if ($done) { $done } else { 'already set up' })"
exit 0
