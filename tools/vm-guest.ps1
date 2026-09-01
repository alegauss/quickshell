#Requires -Version 5.1
<#
.SYNOPSIS
  Everything two scripts need to reach the same VMware guest, written once.

.DESCRIPTION
  QS173. `run-tests-vm.ps1` proved the plumbing: finding vmrun, reading credentials from a file
  outside the tree, telling an absent encryption password from a wrong one, starting the guest with
  its console visible, and carrying the working tree in. None of that is about tests.

  `run-app-vm.ps1` needs all of it to launch the client instead, and a second copy of two hundred
  lines is how two scripts that reach the same machine stop agreeing about it. So this is dot-sourced
  by both, and neither has an opinion of its own about the guest.

  Dot-sourced and not a module, because it runs in the caller's scope: the `$script:` variables it
  sets - the vmrun path, the credentials, the .vmx - are the caller's afterwards, which is what lets
  the caller make its own vmrun calls without passing five things to each one.

.NOTES
  Secrets are never parameters and never printed. KEY=VALUE lines in a file, searched in order:

    1. whatever QUICKSHELL_VM_ENV points at
    2. d:\tmp\quickshell-vm.env
    3. d:\tmp\winwright-vm.env      - the same guest, so the same file rather than a second copy
    4. quickshell-vm.env in the repository root

  Either spelling of the names is accepted, QUICKSHELL_ or WINWRIGHT_, so the shared file needs no
  edit to serve both repositories.

    ..._VMX              full path to the .vmx
    ..._VM_PASSWORD      the VM *encryption* password. A Windows 11 guest needs a TPM, a TPM means
                         an encrypted VM, and vmrun opens nothing without this. It is not the
                         Windows login, and confusing the two costs an afternoon.
    ..._GUEST_USER       an account inside the guest
    ..._GUEST_PASSWORD   its password - a local account with a non-empty one, since vmrun cannot
                         authenticate a Microsoft account signed in with Hello or a PIN
#>

Set-StrictMode -Version Latest

$script:GuestSync = 'C:\quickshell-sync'
$script:GuestRepo = 'C:\src\quickshell'

# Named by whichever script dot-sourced this, so a refusal says which command the operator ran.
if (-not (Get-Variable -Name 'Tool' -Scope Script -ErrorAction SilentlyContinue)) {
    $script:Tool = 'vm'
}

$script:EnvFileCandidates = @(
    'd:\tmp\quickshell-vm.env',
    'd:\tmp\winwright-vm.env',
    (Join-Path (Split-Path -Parent $PSScriptRoot) 'quickshell-vm.env')
)

function Refuse {
    param([Parameter(Mandatory)] [string] $What, [string] $Remedy)
    Write-Host ''
    Write-Host "$($script:Tool): $What" -ForegroundColor Red
    if ($Remedy) { Write-Host "         -> $Remedy" -ForegroundColor Yellow }
    exit 3
}

function Get-Setting {
    <#
      One setting under either spelling. The shared file says WINWRIGHT_; a quickshell-only file may
      say QUICKSHELL_; and an environment variable set for this shell beats both.
    #>
    param([Parameter(Mandatory)] [string] $Suffix)

    foreach ($prefix in @('QUICKSHELL_', 'WINWRIGHT_')) {
        $value = [Environment]::GetEnvironmentVariable($prefix + $Suffix)
        if ($value) { return $value }
    }
    return $null
}

function Import-EnvFile {
    if ($env:QUICKSHELL_VM_ENV) {
        # Honoured even when absent: a typo in it reads as "that file is not there" rather than
        # silently falling through to another file and reaching a VM nobody named.
        if (Test-Path -LiteralPath $env:QUICKSHELL_VM_ENV) { $path = $env:QUICKSHELL_VM_ENV } else { return $null }
    }
    else {
        $path = $script:EnvFileCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $path) { return $null }

    foreach ($line in Get-Content -LiteralPath $path) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        $split = $trimmed.IndexOf('=')
        if ($split -lt 1) { continue }
        $name = $trimmed.Substring(0, $split).Trim()
        # Only ever fills a blank, so a variable set for this shell wins and a one-off override
        # needs no edit to the file.
        if (-not [Environment]::GetEnvironmentVariable($name)) {
            Set-Item -Path "env:$name" -Value $trimmed.Substring($split + 1).Trim()
        }
    }
    return $path
}

function Find-VmRun {
    $candidates = @(
        "${env:ProgramFiles(x86)}\VMware\VMware Workstation\vmrun.exe",
        "$env:ProgramFiles\VMware\VMware Workstation\vmrun.exe",
        "$env:ProgramFiles\VMware\VMware VIX\vmrun.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    $onPath = Get-Command 'vmrun.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

function Invoke-VmRun {
    <#
      Authentication flags are assembled here and nowhere else, so no call site can put one in a log
      line. They still reach vmrun's own command line, where the host's process list can read them,
      and vmrun offers no spelling that avoids it - which is a reason for the guest to be one that
      can be thrown away, not a reason to pretend otherwise.
    #>
    param([Parameter(Mandatory)] [string[]] $Arguments, [switch] $Guest)

    $argv = New-Object System.Collections.ArrayList
    $null = $argv.Add('-T'); $null = $argv.Add('ws')
    if ($script:VmPassword) { $null = $argv.Add('-vp'); $null = $argv.Add($script:VmPassword) }
    if ($Guest) {
        $null = $argv.Add('-gu'); $null = $argv.Add($script:GuestUser)
        $null = $argv.Add('-gp'); $null = $argv.Add($script:GuestPassword)
    }
    foreach ($argument in $Arguments) { $null = $argv.Add($argument) }

    $output = & $script:VmRun @argv 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = ($output | Out-String).Trim()
        Ok       = ($LASTEXITCODE -eq 0)
    }
}

function Read-ConsoleText {
    <#
      Decodes guest output by what is in it rather than by what wrote it. dotnet writes UTF-8 and
      several Windows tools write UTF-16LE, and reading either with the wrong one is not a crash: it
      is a NUL after every character, which reads as data and reaches a report.
    #>
    param([Parameter(Mandatory)] [string] $Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) { return '' }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        return [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
    }
    $zeroes = 0
    for ($i = 1; $i -lt $bytes.Length; $i += 2) { if ($bytes[$i] -eq 0) { $zeroes++ } }
    if (($zeroes * 4) -gt $bytes.Length) { return [Text.Encoding]::Unicode.GetString($bytes) }
    return [Text.Encoding]::UTF8.GetString($bytes)
}

function Connect-Guest {
    <#
    .SYNOPSIS
      Finds vmrun, reads the credentials, opens the VM and waits for its tools to answer.

    .DESCRIPTION
      It arranges nothing. The guest is expected to have a desktop session and a .NET SDK; where it
      does not, this says which and stops. Provisioning a machine from a script is how two machines
      drift apart while both look configured.

    .PARAMETER Vmx
      Override the configured .vmx, for a second guest.
    #>
    param([string] $Vmx)

    $envFile = Import-EnvFile
    $script:VmRun = Find-VmRun
    if (-not $script:VmRun) {
        Refuse 'vmrun.exe was not found on this host' 'Install VMware Workstation, or put vmrun.exe on PATH.'
    }

    $script:VmPassword = Get-Setting 'VM_PASSWORD'
    $script:GuestUser = Get-Setting 'GUEST_USER'
    $script:GuestPassword = Get-Setting 'GUEST_PASSWORD'
    $vmxPath = if ($Vmx) { $Vmx } else { Get-Setting 'VMX' }

    if (-not $vmxPath) {
        Refuse 'no .vmx is configured' "Set QUICKSHELL_VMX, or put it in $($script:EnvFileCandidates[0])."
    }
    if (-not (Test-Path -LiteralPath $vmxPath)) {
        Refuse "the configured .vmx is $vmxPath, which does not exist" 'A path that is not there is a typo, not a stopped VM.'
    }
    if (-not $script:GuestUser -or -not $script:GuestPassword) {
        Refuse 'no guest credentials are configured' 'Set QUICKSHELL_GUEST_USER and QUICKSHELL_GUEST_PASSWORD, or the WINWRIGHT_ spellings.'
    }

    $script:VmxPath = $vmxPath

    Write-Host "$($script:Tool)  $vmxPath" -ForegroundColor Cyan
    if ($envFile) { Write-Host "  settings    $envFile" }

    # listSnapshots is the cheapest call that needs the VM actually opened, so it is what tells an
    # absent encryption password from a wrong one. The two messages differ, and matching only the
    # first sends a wrong password to the branch whose remedy says the problem is not a password.
    $snapshots = Invoke-VmRun -Arguments @('listSnapshots', $vmxPath)
    if (-not $snapshots.Ok) {
        $said = ($snapshots.Output -split "`r?`n" | Where-Object { $_.Trim() }) -join ' '
        if ($said -match 'password is required') {
            Refuse 'the VM is encrypted and no password was supplied' 'Set the VM encryption password - not the guest login.'
        }
        if ($said -match 'ncorrect password') {
            Refuse 'the VM is encrypted and the password supplied was refused' 'That setting is the password VMware asks for when opening the VM, not the Windows login inside it.'
        }
        Refuse "vmrun could not open the VM: $said"
    }

    $running = Invoke-VmRun -Arguments @('list')
    if ($running.Output -notmatch [regex]::Escape([IO.Path]::GetFileName($vmxPath))) {
        Write-Host '  the guest is not running; starting it with its console visible' -ForegroundColor Yellow
        # gui and never nogui: this needs a desk that draws, and a headless guest renders nothing
        # while reporting that everything is present.
        $start = Invoke-VmRun -Arguments @('start', $vmxPath, 'gui')
        if (-not $start.Ok) { Refuse "the guest would not start: $($start.Output)" }

        $deadline = (Get-Date).AddMinutes(10)
        do {
            Start-Sleep -Seconds 10
            $tools = Invoke-VmRun -Arguments @('checkToolsState', $vmxPath)
        } while ($tools.Output -notmatch 'running' -and (Get-Date) -lt $deadline)
        if ($tools.Output -notmatch 'running') { Refuse 'VMware Tools never answered in the guest within ten minutes' }
    }

    $tools = Invoke-VmRun -Arguments @('checkToolsState', $vmxPath)
    if ($tools.Output -notmatch 'running') {
        Refuse "VMware Tools is '$($tools.Output.Trim())' in the guest" 'Install VMware Tools there. Without it vmrun can run nothing.'
    }
    Write-Host '  guest       running, tools answering'

    return $vmxPath
}

function Send-Tree {
    <#
    .SYNOPSIS
      Carries this working tree into the guest and unpacks it, and names the SDK it found there.

    .DESCRIPTION
      Wiped and expanded rather than updated in place. A full rebuild costs under a minute on a tree
      this size, and it buys the one thing an incremental guest cannot offer: no result can be left
      over from a file that is no longer in the tree.

    .PARAMETER Stage
      A directory on the host to build the archive and the guest scripts in.

    .PARAMETER CommittedOnly
      Carry HEAD alone. By default the guest gets the working tree - uncommitted and untracked
      included - because testing a tree nobody has in front of them answers a question nobody asked.
    #>
    param(
        [Parameter(Mandatory)] [string] $RepoRoot,
        [Parameter(Mandatory)] [string] $Stage,
        [switch] $CommittedOnly
    )

    Push-Location $RepoRoot
    try {
        # -c and -o together, minus what .gitignore covers: tracked files plus the untracked ones
        # that are really part of the tree. A sync built from `git diff` alone carries neither an
        # untracked test file nor a new reference image, and a suite that never saw them is green
        # about nothing.
        if ($CommittedOnly) { $files = @(& git ls-files -c) } else { $files = @(& git ls-files -c -o --exclude-standard) }
        if ($LASTEXITCODE -ne 0) { Refuse 'git would not list this tree' }
    }
    finally { Pop-Location }

    $zip = Join-Path $Stage 'source.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($relative in $files) {
            $full = Join-Path $RepoRoot $relative
            if (Test-Path -LiteralPath $full -PathType Leaf) {
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $full, $relative)
            }
        }
    }
    finally { $archive.Dispose() }

    $size = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
    $carrying = if ($CommittedOnly) { 'HEAD only' } else { 'the working tree' }
    Write-Host "  carrying    $($files.Count) files, $size MB ($carrying)"

    @"
`$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath '$script:GuestRepo') { Remove-Item -LiteralPath '$script:GuestRepo' -Recurse -Force }
`$null = New-Item -ItemType Directory -Path '$script:GuestRepo' -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory('$script:GuestSync\source.zip', '$script:GuestRepo')
# The per-user install first, because that is where this guest has it and where `dotnet` on PATH
# is not. A machine-wide install and a PATH entry are both honoured after it.
`$candidates = @(
    (Join-Path `$env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    (Join-Path `$env:ProgramFiles 'dotnet\dotnet.exe')
)
`$dotnet = `$candidates | Where-Object { Test-Path -LiteralPath `$_ } | Select-Object -First 1
if (-not `$dotnet) {
    `$onPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (`$onPath) { `$dotnet = `$onPath.Source }
}
if (-not `$dotnet) { Write-Output 'GUEST-MISSING dotnet'; exit 91 }
Write-Output ('sdk ' + (& `$dotnet --version) + ' at ' + `$dotnet)
"@ | Set-Content -LiteralPath (Join-Path $Stage 'sync.ps1') -Encoding ascii

    @"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "$script:GuestSync\sync.ps1" > "$script:GuestSync\sync.log" 2>&1
exit /b %ERRORLEVEL%
"@ | Set-Content -LiteralPath (Join-Path $Stage 'sync.cmd') -Encoding ascii

    $null = Invoke-VmRun -Guest -Arguments @('createDirectoryInGuest', $script:VmxPath, $script:GuestSync)

    foreach ($file in @('source.zip', 'sync.ps1', 'sync.cmd')) {
        $sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $script:VmxPath, (Join-Path $Stage $file), "$script:GuestSync\$file")
        if (-not $sent.Ok) { Refuse "could not copy $file into the guest: $($sent.Output)" }
    }

    $synced = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $script:VmxPath, "$script:GuestSync\sync.cmd")
    $syncLog = Join-Path $Stage 'sync.log'
    $null = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $script:VmxPath, "$script:GuestSync\sync.log", $syncLog)
    $syncSaid = if (Test-Path -LiteralPath $syncLog) { (Read-ConsoleText $syncLog).Trim() } else { '' }

    if (-not $synced.Ok) {
        if ($syncSaid -match 'GUEST-MISSING dotnet') {
            Refuse 'the guest has no .NET SDK' 'Install it once in the VM. This script does not provision a machine.'
        }
        Refuse "the guest tree would not sync: $syncSaid $($synced.Output)"
    }

    Write-Host "  guest tree  $syncSaid"

    return $syncSaid
}

function Clear-GuestFile {
    <#
      Deleted in the guest before a run, never merely overwritten after it. A run that writes nothing
      leaves the last one's files in place, the copy back succeeds, and the caller is handed a
      previous afternoon's log as this run's result - which is worse than an error, because it looks
      like one.
    #>
    param([Parameter(Mandatory)] [string[]] $Names)

    foreach ($stale in $Names) {
        $null = Invoke-VmRun -Guest -Arguments @('deleteFileInGuest', $script:VmxPath, "$script:GuestSync\$stale")
    }
}
