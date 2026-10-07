#Requires -Version 5.1
<#
.SYNOPSIS
  Time the client's start on the guest's desk, and bring back the report.

.DESCRIPTION
  QS190. `tools/Quickshell.Startup` starts the client again and again and reads the milestones it
  reports, and every start puts a window on whatever desk it runs on - eleven of them, each taking
  the foreground from the person at that desk. So the harness runs where run-app-vm runs the client:
  in the guest, on its own desk.

  It carries the tree in, publishes the client the way tools/release.ps1 does (Release,
  self-contained, ReadyToRun), builds the harness, runs it there, and copies the report back.

  The guest is not the reference machine. Its figures are compared with each other - a change
  measured before and after on the same guest - and never with the reference desk's.

.PARAMETER Label
  What to call this build in the report.

.PARAMETER Runs
  How many starts. The first is reported apart, as the harness always does.

.PARAMETER CommittedOnly
  Carry HEAD alone rather than the working tree: the "before" of a before-and-after.

.NOTES
  Credentials and the .vmx come from a file outside this tree. vm-guest.ps1 says which.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Label,

    [ValidateRange(2, 50)]
    [int] $Runs = 11,

    [switch] $CommittedOnly,

    [string] $Vmx
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'run-startup-vm'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

$vmxPath = Connect-Guest -Vmx $Vmx

$stage = Join-Path ([IO.Path]::GetTempPath()) 'quickshell-vm-startup'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage

Clear-GuestFile -Names @('startup-exit.txt', 'startup.log', 'startup.md', 'startup.json', 'sync.log')

$null = Send-Tree -RepoRoot $script:RepoRoot -Stage $stage -CommittedOnly:$CommittedOnly

# Published as a release is, and timed from there: a Debug build's start is not what anybody runs.
@"
@echo off
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "MSBUILDDISABLENODEREUSE=1"

cd /d "$script:GuestRepo"
set "PUB=$script:GuestSync\startup-publish"
if exist "%PUB%" rmdir /s /q "%PUB%"

dotnet publish "$script:GuestRepo\src\Quickshell.App\Quickshell.App.csproj" --configuration Release --runtime win-x64 --self-contained true -p:PublishReadyToRun=true --output "%PUB%" --nologo -v quiet > "$script:GuestSync\startup.log" 2>&1
if errorlevel 1 goto done

dotnet build "$script:GuestRepo\tools\Quickshell.Startup\Quickshell.Startup.csproj" -c Release --nologo -v quiet >> "$script:GuestSync\startup.log" 2>&1
if errorlevel 1 goto done

for %%E in ("%PUB%\*.exe") do if /i not "%%~nxE"=="createdump.exe" set "CLIENT=%%E"

"$script:GuestRepo\tools\Quickshell.Startup\bin\Release\net10.0-windows\Quickshell.Startup.exe" --launch "%CLIENT%" --runs $Runs --label "$Label" --out "$script:GuestSync\startup.md" --json "$script:GuestSync\startup.json" >> "$script:GuestSync\startup.log" 2>&1

:done
set "RC=%ERRORLEVEL%"
dotnet build-server shutdown >nul 2>&1
> "$script:GuestSync\startup-exit.txt" echo %RC%
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $stage 'startup.cmd') -Encoding ascii

$sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage 'startup.cmd'), "$script:GuestSync\startup.cmd")
if (-not $sent.Ok) { Refuse "could not copy startup.cmd into the guest: $($sent.Output)" }

Write-Host ''
Write-Host "  publishing and timing $Runs starts in the guest ($Label)" -ForegroundColor Cyan

# -interactive: each start is a window, and a window needs the logged-in desk.
$ran = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, '-interactive', "$script:GuestSync\startup.cmd")
if (-not $ran.Ok) {
    if ($ran.Output -match 'logged in interactively') {
        Refuse 'the guest has no interactive desktop session' 'Log in at the guest console once, and leave it unlocked.'
    }
    Refuse "the guest never finished: $($ran.Output)"
}

$results = Join-Path $script:RepoRoot 'TestResults\vm'
if (-not (Test-Path -LiteralPath $results)) { $null = New-Item -ItemType Directory -Path $results -Force }

foreach ($file in @('startup.log', 'startup.md', 'startup.json')) {
    $null = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\$file", (Join-Path $results $file))
}

$exitFile = Join-Path $stage 'startup-exit.txt'
$back = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\startup-exit.txt", $exitFile)
if (-not $back.Ok) { Refuse 'the guest wrote no exit code, so what happened there is unknown' }

$said = (Read-ConsoleText $exitFile).Trim()
$report = Join-Path $results 'startup.md'

if ($said -ne '0' -or -not (Test-Path -LiteralPath $report)) {
    $log = Join-Path $results 'startup.log'
    if (Test-Path -LiteralPath $log) { foreach ($line in ((Read-ConsoleText $log) -split "`r?`n")) { Write-Host $line } }
    Refuse "the timing did not finish in the guest (exit $said)" "The log is $log."
}

Write-Host (Read-ConsoleText $report)
Write-Host "run-startup-vm: $report" -ForegroundColor Green
exit 0
