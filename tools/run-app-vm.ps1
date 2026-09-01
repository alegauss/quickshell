#Requires -Version 5.1
<#
.SYNOPSIS
  Run the client itself on a desk that is not the operator's, and bring back what it showed.

.DESCRIPTION
  QS173. `run-tests-vm.ps1` moved the suite off the operator's machine. It moved nothing else, and
  the other half of the evidence this project asks for is somebody looking at the client and saying
  what it shows - so every task that changed what a window looks like ended with the picture being
  taken on the operator's own desktop, stealing their foreground and typing into whatever had focus,
  or with no picture at all.

  This is the missing verb. It carries the working tree in, builds the client there, starts it on
  the guest's desk, waits for it to settle, photographs the screen, and stops it again.

  Three things it deliberately does not do:

  - It does not wait for the client to exit. A terminal client does not exit; waiting for one is
    waiting for the timeout. It is started with -noWait, given -Seconds to draw, and then stopped.
  - It does not drive the client. Pressing keys at a window is `Quickshell.Cases` under winwright,
    which reads the accessibility tree rather than pixels and knows what it clicked. This answers
    the one question that tree cannot: what it looked like.
  - It does not provision anything. Same guest, same .NET SDK, same rule as the suite.

.PARAMETER Configuration
  Debug or Release, built and run inside the guest.

.PARAMETER Seconds
  How long to let the client draw before the screen is photographed. The default is generous on
  purpose: the first frame of a D3D11 swapchain on WARP is not the frame worth looking at.

.PARAMETER Arguments
  Handed to the client. `--tabs 3`, `--panes 4` and the rest, exactly as on this machine.

.PARAMETER Settings
  A settings file on this host, placed as the guest user's own before the client starts. This is how
  a scheme, a font or a cursor is looked at: write the file, point this at it, read the picture.

.PARAMETER Keep
  Leave the client running on the guest's desk afterwards, for a longer look at its console.

.NOTES
  Credentials and the .vmx come from a file outside this tree. vm-guest.ps1 says which.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [ValidateRange(1, 600)]
    [int] $Seconds = 12,

    [string] $Arguments = '',

    [string] $Settings,

    [switch] $Keep,

    [switch] $CommittedOnly,

    [string] $Vmx
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'run-app-vm'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

if ($Settings -and -not (Test-Path -LiteralPath $Settings)) {
    Refuse "the settings file is $Settings, which does not exist" 'A path that is not there is a typo, not an empty configuration.'
}

$vmxPath = Connect-Guest -Vmx $Vmx

# --- the tree the guest will run ------------------------------------------------------------------

$stage = Join-Path ([IO.Path]::GetTempPath()) 'quickshell-vm-app'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage

Clear-GuestFile -Names @('app-exit.txt', 'app-target.txt', 'app-build.log', 'sync.log',
                         'app-settings.json')

$null = Send-Tree -RepoRoot $script:RepoRoot -Stage $stage -CommittedOnly:$CommittedOnly

if ($Settings) {
    $sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Resolve-Path -LiteralPath $Settings).Path, "$script:GuestSync\app-settings.json")
    if (-not $sent.Ok) { Refuse "could not copy the settings file into the guest: $($sent.Output)" }
    Write-Host "  settings    $Settings -> the guest user's own"
}

# --- build, then start ----------------------------------------------------------------------------

# Built by its own batch file and waited for, so a compile error is a refusal here rather than a
# photograph of an empty desk twelve seconds later.
@"
@echo off
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "MSBUILDDISABLENODEREUSE=1"

cd /d "$script:GuestRepo"
dotnet build "$script:GuestRepo\src\Quickshell.App\Quickshell.App.csproj" -c $Configuration > "$script:GuestSync\app-build.log" 2>&1

set "RC=%ERRORLEVEL%"

rem Asked rather than assembled from parts. This project has a runtime identifier and an assembly
rem name that are both not the folder and not the project, so a path spelled out here would be a
rem path that is right until somebody changes either - which is exactly how the first version of
rem this script photographed an empty desk.
if "%RC%"=="0" (
    rem Single quotes and not backticks. The backtick form would be the natural spelling, and this
    rem batch file is written from a PowerShell here-string where a backtick is the escape character
    rem - so it never survives to the guest.
    for /f "delims=" %%T in ('dotnet build "$script:GuestRepo\src\Quickshell.App\Quickshell.App.csproj" -c $Configuration -nologo --getProperty:TargetPath') do (
        > "$script:GuestSync\app-target.txt" echo %%T
    )
)

dotnet build-server shutdown >nul 2>&1

rem The redirect leads: `echo %RC%> file` is read by cmd as a redirect to handle %RC% for a
rem single-digit code, and writes "ECHO is off." where the verdict should be.
> "$script:GuestSync\app-exit.txt" echo %RC%
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $stage 'build.cmd') -Encoding ascii

@"
@echo off
taskkill /IM quickshell.exe /F >nul 2>&1
taskkill /IM Quickshell.App.exe /F >nul 2>&1
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $stage 'stop.cmd') -Encoding ascii

foreach ($file in @('build.cmd', 'stop.cmd')) {
    $sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage $file), "$script:GuestSync\$file")
    if (-not $sent.Ok) { Refuse "could not copy $file into the guest: $($sent.Output)" }
}

Write-Host ''
Write-Host "  building the client in the guest ($Configuration)" -ForegroundColor Cyan

$built = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, "$script:GuestSync\build.cmd")
if (-not $built.Ok) { Refuse "the guest never finished the build: $($built.Output)" }

$results = Join-Path $script:RepoRoot 'TestResults\vm'
if (-not (Test-Path -LiteralPath $results)) { $null = New-Item -ItemType Directory -Path $results -Force }

$buildLog = Join-Path $results 'app-build.log'
$null = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\app-build.log", $buildLog)

$exitFile = Join-Path $stage 'app-exit.txt'
$back = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\app-exit.txt", $exitFile)
if (-not $back.Ok) { Refuse 'the guest wrote no build exit code, so what happened there is unknown' }

$said = (Read-ConsoleText $exitFile).Trim()
if ($said -ne '0') {
    if (Test-Path -LiteralPath $buildLog) {
        foreach ($line in ((Read-ConsoleText $buildLog) -split "`r?`n")) { Write-Host $line }
    }
    Refuse "the client would not build in the guest (exit $said)" "The log is $buildLog."
}

# Where the build actually put it, as MSBuild answered in the guest. `start` returns success for a
# path that is not there, so a wrong one would not fail here - it would be photographed twelve
# seconds later as an empty desk, which reads as the client having drawn nothing.
$targetFile = Join-Path $stage 'app-target.txt'
$gotTarget = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\app-target.txt", $targetFile)
if (-not $gotTarget.Ok) { Refuse 'the guest never said where it put the client' "The build log is $buildLog." }

# MSBuild's TargetPath is the assembly; the launcher beside it carries the same name.
$exe = [IO.Path]::ChangeExtension((Read-ConsoleText $targetFile).Trim(), '.exe')

$there = Invoke-VmRun -Guest -Arguments @('fileExistsInGuest', $vmxPath, $exe)
if ($there.Output -notmatch 'exists') {
    Refuse "the build succeeded but there is no client at $exe" 'MSBuild named a path the guest does not have.'
}

Write-Host "  client      $exe"

# Written now and not before the build, because until MSBuild answered nothing here knew what to
# start.
#
# The settings file goes to the guest user's own AppData, which is where the client looks. Copied by
# the guest and not by vmrun, because %AppData% is that user's and this host does not know it.
$placing = if ($Settings) {
    @"
if not exist "%AppData%\quickshell" mkdir "%AppData%\quickshell"
copy /y "$script:GuestSync\app-settings.json" "%AppData%\quickshell\settings.json" >nul
"@
}
else { 'rem no settings file was supplied; the client uses whatever the guest already had' }

@"
@echo off
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
$placing
start "" "$exe" $Arguments
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $stage 'start.cmd') -Encoding ascii

$sentStart = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage 'start.cmd'), "$script:GuestSync\start.cmd")
if (-not $sentStart.Ok) { Refuse "could not copy start.cmd into the guest: $($sentStart.Output)" }

# Stopped before it is started. A client left running by a previous call would be photographed as
# this one's, and it would be the wrong tree.
$null = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, '-interactive', "$script:GuestSync\stop.cmd")

Write-Host "  starting the client on the guest's desk" -ForegroundColor Cyan

# -interactive because a window needs a logged-in desktop session, and -noWait because a terminal
# client does not exit: waiting for one is waiting for a timeout with nothing to show for it.
$started = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, '-interactive', '-noWait', "$script:GuestSync\start.cmd")
if (-not $started.Ok) {
    if ($started.Output -match 'logged in interactively') {
        Refuse 'the guest has no interactive desktop session' 'Log in at the guest console once, and leave it unlocked. A locked desk draws nothing.'
    }
    Refuse "the client would not start in the guest: $($started.Output)"
}

Write-Host "  letting it draw for $Seconds seconds"
Start-Sleep -Seconds $Seconds

# --- the picture, which is the whole point --------------------------------------------------------

$shot = Join-Path $results 'app-desk.png'
if (Test-Path -LiteralPath $shot) { Remove-Item -LiteralPath $shot -Force }

$capture = Invoke-VmRun -Guest -Arguments @('captureScreen', $vmxPath, $shot)

if (-not $Keep) {
    $null = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, '-interactive', "$script:GuestSync\stop.cmd")
}

if (-not $capture.Ok) {
    Refuse "the client ran but the screen could not be photographed: $($capture.Output)" 'captureScreen needs the guest login, which is what -Guest supplies.'
}

Write-Host ''
Write-Host "run-app-vm: $shot" -ForegroundColor Green
if ($Keep) { Write-Host '            the client is still running on the guest.' }
exit 0
