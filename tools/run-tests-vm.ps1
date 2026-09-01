#Requires -Version 5.1
<#
.SYNOPSIS
  Run the suite on a desk that is not the operator's.

.DESCRIPTION
  QS95. The render suite creates real windows and puts them on top, because DXGI stops advancing a
  swapchain's frame statistics for a window nobody can see - so for the twenty-five seconds a run
  lasts, the screen belongs to the tests. The interruption is the smaller half. The larger half is
  that the operator interrupts the run: a window dragged across the test window makes DXGI answer
  DXGI_STATUS_OCCLUDED, statistics stop, and the frame-queue measurement skips or reads nonsense.

  This moves the run to a guest under VMware Workstation and leaves the host to its owner.

  Reaching the guest at all is `vm-guest.ps1`, which `run-app-vm.ps1` dot-sources too - see QS173.
  What is left here is what is about the suite: the batch file the guest runs, the exit code it
  writes down, and the artefacts a red run leaves behind.

  Most of the mechanics were paid for once already, in winwright's tools\run-tests-vm.ps1, and are
  repeated rather than rediscovered: vmrun never relays guest output, so output is redirected to a
  file in the guest and copied out; vmrun never adopts the guest's exit code, so the guest writes it
  down; and a command is delivered as a batch file run with no arguments, because `cmd /c "..."`
  passes quoting through two layers that both rewrite it.

  What is quickshell's own is the GPU. The guest has none, so AdapterChain falls through to WARP -
  which QS6 built for exactly this case, and which QS12 measured as agreeing with this machine's
  hardware adapter to within one level of 255 on every golden scene. A guest run is therefore a
  second environment in QS12's matrix as well as a quieter desk.

.PARAMETER Configuration
  Debug or Release, handed to run-tests.cmd inside the guest.

.PARAMETER Screenshot
  Capture the guest's screen when the run finishes. A render suite whose desk drew nothing is a
  green that covered nothing, and a picture is the one answer no exit code carries.

.PARAMETER CommittedOnly
  Carry HEAD alone. By default the guest gets the working tree - uncommitted and untracked included -
  because testing a tree nobody has in front of them answers a question nobody asked.

.NOTES
  Credentials and the .vmx come from a file outside this tree. vm-guest.ps1 says which, and says why
  the out-of-tree spelling is the default.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $Screenshot,

    [switch] $CommittedOnly,

    [string] $Vmx
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'run-tests-vm'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

$vmxPath = Connect-Guest -Vmx $Vmx

# --- the tree the guest will test ---------------------------------------------------------------

$stage = Join-Path ([IO.Path]::GetTempPath()) 'quickshell-vm'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage

Clear-GuestFile -Names @('vm-exit.txt', 'vm-run.log', 'sync.log', 'results.zip')

$null = Send-Tree -RepoRoot $script:RepoRoot -Stage $stage -CommittedOnly:$CommittedOnly

# --- the batch file the guest runs, generated here so nothing is quoted through vmrun -------------

# The exit code is written down rather than left to vmrun, which reports every failure as its own
# non-zero and would make a red suite and an unreachable guest the same number.
@"
@echo off
rem The per-user SDK is not on the PATH a non-interactive vmrun session inherits, so it is put there
rem rather than assumed. DOTNET_ROOT too: without it the host resolves no runtime and every dotnet
rem call fails with a message about a framework that is plainly installed.
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "MSBUILDDISABLENODEREUSE=1"
dotnet build-server shutdown >nul 2>&1

cd /d "$script:GuestRepo"
call "$script:GuestRepo\run-tests.cmd" $Configuration > "$script:GuestSync\vm-run.log" 2>&1

rem Captured before the shutdown and not after. Anything run between the suite and the write takes
rem %ERRORLEVEL% with it, so the tidying up would have reported its own success as the suite's.
set "RC=%ERRORLEVEL%"
dotnet build-server shutdown >nul 2>&1

rem Whatever the golden-image suite wrote when a scene failed - reference, actual and difference -
rem comes back as one archive, because vmrun cannot glob and those names are not known up here.
if exist "$script:GuestRepo\TestResults" powershell -NoProfile -Command "Compress-Archive -Path '$script:GuestRepo\TestResults\*' -DestinationPath '$script:GuestSync\results.zip' -Force" >nul 2>&1

rem The redirect leads, and that is not a style choice. `echo %RC%> file` is parsed by cmd as
rem `echo` redirected to handle %RC% whenever the code is a single digit, so a run that exited 1
rem wrote "ECHO is off." into the file and the host read the suite's verdict as a string.
> "$script:GuestSync\vm-exit.txt" echo %RC%
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $stage 'run.cmd') -Encoding ascii

$sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage 'run.cmd'), "$script:GuestSync\run.cmd")
if (-not $sent.Ok) { Refuse "could not copy run.cmd into the guest: $($sent.Output)" }

# --- the run ------------------------------------------------------------------------------------

Write-Host ''
Write-Host "  running the suite in the guest ($Configuration). The host is yours." -ForegroundColor Cyan

# -interactive, because the render fixtures need a logged-in desktop session and refuse without one.
# That refusal is the right answer: a suite drawing into a lock screen renders nothing while
# reporting that everything was present.
#
# -activeWindow deliberately not. It brings this batch's console to the foreground in the guest, and
# the fixtures take the foreground for themselves - the console must never compete for it.
$ran = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, '-interactive', "$script:GuestSync\run.cmd")
if (-not $ran.Ok) {
    if ($ran.Output -match 'logged in interactively') {
        Refuse 'the guest has no interactive desktop session' 'Log in at the guest console once, and leave it unlocked. A locked desk renders nothing.'
    }
    Refuse "the guest never finished the run: $($ran.Output)"
}

# --- back to the host -----------------------------------------------------------------------------

# TestResults\vm and never TestResults itself. Two machines writing one path is a result that cannot
# say where it came from, which is the same defect as a green that cannot say what it covered.
$results = Join-Path $script:RepoRoot 'TestResults\vm'
if (-not (Test-Path -LiteralPath $results)) { $null = New-Item -ItemType Directory -Path $results -Force }

$exitFile = Join-Path $stage 'vm-exit.txt'
$back = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\vm-exit.txt", $exitFile)
if (-not $back.Ok) {
    Refuse 'the guest wrote no exit code, so what the run did there is unknown' 'Look at the guest console; the run may still be on screen.'
}

$said = (Read-ConsoleText $exitFile).Trim()
if ($said -notmatch '^-?\d+$') {
    # Refused rather than coerced. Whatever this is, it is not a verdict, and guessing a number out
    # of it would report an outcome the guest never stated.
    Refuse "the guest wrote '$said' where its exit code should be" 'Read TestResults\vm\vm-run.log: the run happened, but what it concluded did not come back.'
}
$code = [int]$said

$logFile = Join-Path $results 'vm-run.log'
$null = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\vm-run.log", $logFile)

# Only chased on a red run: the archive exists only when a golden scene wrote its difference images,
# and asking for it on a green run reports a missing file that was never due.
if ($code -ne 0) {
    $archiveBack = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\results.zip", (Join-Path $results 'results.zip'))
    if ($archiveBack.Ok) { Write-Host "  artefacts   $(Join-Path $results 'results.zip')" }
}

if (Test-Path -LiteralPath $logFile) {
    Write-Host ''
    foreach ($line in ((Read-ConsoleText $logFile) -split "`r?`n")) { Write-Host $line }
}

if ($Screenshot) {
    $shot = Join-Path $results 'vm-desk.png'
    if (Test-Path -LiteralPath $shot) { Remove-Item -LiteralPath $shot -Force }
    # -Guest, because captureScreen is a guest operation: without a login vmrun answers "Anonymous
    # guest operations are not allowed on this virtual machine".
    $capture = Invoke-VmRun -Guest -Arguments @('captureScreen', $vmxPath, $shot)
    if ($capture.Ok) { Write-Host "  desk        $shot" }
    else { Write-Host "  captureScreen failed: $($capture.Output)" -ForegroundColor Yellow }
}

Write-Host ''
if ($code -eq 0) { Write-Host 'run-tests-vm: the guest run passed.' -ForegroundColor Green }
else { Write-Host "run-tests-vm: the guest run exited $code." -ForegroundColor Red }
exit $code
