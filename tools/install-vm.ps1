#Requires -Version 5.1
<#
.SYNOPSIS
  Install the release archive on a desk that is not the operator's, look where Windows looks, start
  the installed copy, and take it away again the way the list of installed apps would.

.DESCRIPTION
  QS77. The suite installs into places of its own - a temporary folder, a registry key under a test's
  name - because an entry under the real key is an app in the list of whoever runs it. So the suite
  cannot say the one thing a user would: that the archive, unzipped on a real profile, installs
  there, starts from there, and leaves again.

  This says it, on the guest. The archive release.cmd built is unzipped there and installed with
  `--install --quiet` under the guest user's own token, which the log names - a medium one means no
  administrator was asked. Then the program folder, the Start menu shortcut and the uninstall entry
  are read back; the installed copy is started and has to reach its first interactive frame; the
  entry's own QuietUninstallString is run, which is what a deployment runs; and the three are read
  back again, gone, with the settings folder still there.

  A machine-wide install is asked for too, without elevation, and has to be refused with 740 -
  the code a deployment tool reads as "run this as an administrator".

.PARAMETER Archive
  The archive to install. The newest in artifacts\release unless given.

.NOTES
  Credentials and the .vmx come from a file outside this tree. vm-guest.ps1 says which.
#>
[CmdletBinding()]
param(
    [string] $Archive,

    [string] $Vmx
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'install-vm'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

if (-not $Archive) {
    $newest = Get-ChildItem -Path (Join-Path $script:RepoRoot 'artifacts\release') -Filter '*.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) { $Archive = $newest.FullName }
}
if (-not $Archive -or -not (Test-Path -LiteralPath $Archive)) {
    Refuse 'there is no archive to install' 'Build one with release.cmd first.'
}

$vmxPath = Connect-Guest -Vmx $Vmx

$stage = Join-Path ([IO.Path]::GetTempPath()) 'quickshell-vm-install'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage

$null = Invoke-VmRun -Guest -Arguments @('createDirectoryInGuest', $vmxPath, $script:GuestSync)
Clear-GuestFile -Names @('install.log', 'release.zip', 'install.ps1', 'install.cmd')

# Everything the guest does is in one script, and it writes one log: a line per thing read back, each
# saying ok or FAIL, and a verdict last. Single-quoted, so nothing in it is this host's to expand.
@'
$ErrorActionPreference = 'Stop'
$sync = '@SYNC@'
$log = Join-Path $sync 'install.log'
$script:failed = 0

function Say([string] $line) { Add-Content -LiteralPath $log -Value $line -Encoding UTF8 }
function Check([bool] $held, [string] $what) {
    if ($held) { Say "ok    $what" } else { Say "FAIL  $what"; $script:failed++ }
}

try {
    # By the integrity level's SID and not its name, which whoami translates: a guest in another
    # language says Medium in that language.
    $sid = [regex]::Match((whoami /groups | Out-String), 'S-1-16-(\d+)').Groups[1].Value
    $level = switch ($sid) { '8192' { 'Medium' } '12288' { 'High' } '16384' { 'System' } default { "unknown (S-1-16-$sid)" } }
    Say "token $level"
    Check ($level -eq 'Medium') 'installed under a token with no administrator in it'

    $download = Join-Path $sync 'download'
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $sync 'release.zip'), $download)
    $portable = Join-Path $download 'quickshell\quickshell.exe'
    Check (Test-Path -LiteralPath $portable) "the archive unzips to one folder with quickshell.exe in it"

    $everyone = Start-Process -FilePath $portable -ArgumentList '--install', '--all-users', '--quiet' -Wait -PassThru
    Check ($everyone.ExitCode -eq 740) "--install --all-users without an administrator exits 740 (it exited $($everyone.ExitCode))"

    $installing = Start-Process -FilePath $portable -ArgumentList '--install', '--quiet' -Wait -PassThru
    Check ($installing.ExitCode -eq 0) "--install --quiet exits 0 (it exited $($installing.ExitCode))"

    $folder = Join-Path $env:LOCALAPPDATA 'Programs\quickshell'
    $program = Join-Path $folder 'quickshell.exe'
    $files = @(Get-ChildItem -LiteralPath $folder -File -Recurse -ErrorAction SilentlyContinue).Count
    $archived = @(Get-ChildItem -LiteralPath (Join-Path $download 'quickshell') -File -Recurse).Count
    Check (Test-Path -LiteralPath $program) "the program is at $program"
    Check ($files -eq $archived - 1) "every file but the marker was installed ($files of $archived)"
    Check (-not (Test-Path -LiteralPath (Join-Path $folder 'quickshell.portable'))) 'the installed copy is not portable'

    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'quickshell.lnk'
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    Check (Test-Path -LiteralPath $shortcut) "the Start menu has $shortcut"
    Check ($link.TargetPath -eq $program) "it starts $($link.TargetPath)"
    Check ($link.WorkingDirectory -eq '%USERPROFILE%') "it starts in $($link.WorkingDirectory)"

    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\quickshell'
    $entry = Get-ItemProperty -LiteralPath $key
    Say "entry $($entry.DisplayName) $($entry.DisplayVersion), $($entry.EstimatedSize) KB, from $($entry.InstallLocation)"
    Check ($entry.DisplayName -eq 'quickshell' -and $entry.InstallLocation -eq $folder) 'the list of installed apps names it and where it is'

    # The installed copy has to start, and a start is a frame with the shell's prompt on it.
    $report = Join-Path $sync 'installed-start.json'
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }
    $running = Start-Process -FilePath $program -ArgumentList '--startup-report', $report -PassThru
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path -LiteralPath $report) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Check (Test-Path -LiteralPath $report) 'the installed copy started and reached its first interactive frame'
    if (Test-Path -LiteralPath $report) { Say "start $((Get-Content -LiteralPath $report -Raw).Trim())" }
    Stop-Process -Id $running.Id -Force -ErrorAction SilentlyContinue
    $running.WaitForExit()

    # Standing in for somebody's saved sessions, which an uninstall must not reach.
    $settings = Join-Path $env:APPDATA 'quickshell'
    $null = New-Item -ItemType Directory -Path $settings -Force
    $kept = Join-Path $settings 'install-vm-kept.txt'
    Set-Content -LiteralPath $kept -Value 'an uninstall leaves this'

    # Exactly what the entry says to run, through cmd the way a deployment runs a command line.
    $removing = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList '/d', '/c', $entry.QuietUninstallString -Wait -PassThru
    Check ($removing.ExitCode -eq 0) "the entry's QuietUninstallString exits 0 (it exited $($removing.ExitCode))"

    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath $folder) -and $clock.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 250 }
    Check (-not (Test-Path -LiteralPath $folder)) "the program folder is gone ($([math]::Round($clock.Elapsed.TotalSeconds, 1)) s after the uninstall returned)"
    Check (-not (Test-Path -LiteralPath $shortcut)) 'the Start menu shortcut is gone'
    Check (-not (Test-Path -LiteralPath $key)) 'the uninstall entry is gone'
    Check (Test-Path -LiteralPath $kept) "the settings folder is still there: $settings"

    Remove-Item -LiteralPath $kept -Force
}
catch {
    Say "FAIL  $($_.Exception.Message)"
    $script:failed++
}

if ($script:failed -eq 0) { Say 'verdict pass' } else { Say "verdict FAIL ($($script:failed))" }
'@.Replace('@SYNC@', $script:GuestSync) | Set-Content -LiteralPath (Join-Path $stage 'install.ps1') -Encoding UTF8

@"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "$script:GuestSync\install.ps1"
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $stage 'install.cmd') -Encoding ascii

Write-Host "  archive     $Archive"

$sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Resolve-Path -LiteralPath $Archive).Path, "$script:GuestSync\release.zip")
if (-not $sent.Ok) { Refuse "could not copy the archive into the guest: $($sent.Output)" }

foreach ($file in @('install.ps1', 'install.cmd')) {
    $sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage $file), "$script:GuestSync\$file")
    if (-not $sent.Ok) { Refuse "could not copy $file into the guest: $($sent.Output)" }
}

Write-Host '  installing, starting and uninstalling it in the guest' -ForegroundColor Cyan

# -interactive, because the installed copy is started and a window needs the logged-in desk.
$ran = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, '-interactive', "$script:GuestSync\install.cmd")
if (-not $ran.Ok) {
    if ($ran.Output -match 'logged in interactively') {
        Refuse 'the guest has no interactive desktop session' 'Log in at the guest console once, and leave it unlocked.'
    }
    Refuse "the guest never finished: $($ran.Output)"
}

$results = Join-Path $script:RepoRoot 'TestResults\vm'
if (-not (Test-Path -LiteralPath $results)) { $null = New-Item -ItemType Directory -Path $results -Force }
$log = Join-Path $results 'install.log'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }

$back = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\install.log", $log)
if (-not $back.Ok) { Refuse 'the guest wrote no log, so what happened there is unknown' }

# Less the byte-order mark Windows PowerShell puts at the head of a UTF-8 file it creates.
$said = (Read-ConsoleText $log).Trim().TrimStart([char]0xFEFF)
foreach ($line in ($said -split "`r?`n")) {
    $colour = if ($line -like 'FAIL*' -or $line -like 'verdict FAIL*') { 'Red' } else { 'Gray' }
    Write-Host "  $line" -ForegroundColor $colour
}

if ($said -notmatch '(?m)^verdict pass\r?$') {
    Write-Host ''
    Write-Host "install-vm: FAILED - $log" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host "install-vm: passed - $log" -ForegroundColor Green
exit 0
