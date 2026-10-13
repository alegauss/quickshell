#Requires -Version 5.1
<#
.SYNOPSIS
  Measures this client and MobaXterm the same way, on the guest's desk, and brings back the report
  (QS81).

.DESCRIPTION
  The comparison in docs/SWITCHING.md has to be one a reader can run again, so its figures come
  from here and from nothing else. Both clients are measured from outside, by what Windows says of
  their processes, because MobaXterm reports nothing of its own and a figure taken one way for one
  client and another way for the other is not a comparison.

  For each client, a number of times:
    - start: process creation to its first visible top-level window, by wall clock, polled every
      5 ms on the process started;
    - memory: the private bytes and the working set of its whole process tree, after it has
      settled for the given seconds;
    - idle: the processor time its process tree spent over the next idle interval, as a share of
      one core.
  Each client is started as it is shipped: this client published as tools/release.ps1 publishes it,
  opening on Home; MobaXterm Home's portable executable, opening on its own home tab.

  In the guest only: every start is a window, and the operator's desk is not where windows are
  put (AGENTS.md). The guest is not the reference machine, so its figures are compared with each
  other and never with docs/PERFORMANCE.md's.

.PARAMETER MobaXterm
  The MobaXterm Home portable zip, from https://mobaxterm.mobatek.net/download-home-edition.html.

.PARAMETER Sha256
  The SHA-256 of the MobaXterm executable inside it. Mobatek publishes none, so the report records
  the one measured and this refuses a different one.

.PARAMETER Runs
  Starts per client.

.PARAMETER SettleSeconds
  How long a started client is left before its memory is read.

.PARAMETER IdleSeconds
  The idle interval its processor time is read over.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MobaXterm,
    [Parameter(Mandatory)] [string] $Sha256,
    [ValidateRange(1, 20)] [int] $Runs = 5,
    [ValidateRange(5, 300)] [int] $SettleSeconds = 15,
    [ValidateRange(10, 600)] [int] $IdleSeconds = 60,
    [string] $Vmx
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'compare-mobaxterm-vm'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

$stage = Join-Path ([IO.Path]::GetTempPath()) 'quickshell-vm-compare'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage

Expand-Archive -LiteralPath $MobaXterm -DestinationPath (Join-Path $stage 'moba')
$mobaExe = Get-ChildItem -LiteralPath (Join-Path $stage 'moba') -Filter 'MobaXterm*.exe' | Select-Object -First 1
if (-not $mobaExe) { Refuse "$MobaXterm holds no MobaXterm executable" }

$actual = (Get-FileHash -LiteralPath $mobaExe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $Sha256.ToLowerInvariant()) {
    Refuse "$($mobaExe.Name) is not the build this was told to measure: its SHA-256 is $actual" 'Pass the hash of the executable being measured, and say which in the report.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $mobaExe.FullName
if ($signature.Status -ne 'Valid') {
    Refuse "$($mobaExe.Name) does not carry a valid signature ($($signature.Status))" 'Download it again from mobaxterm.mobatek.net.'
}

Compress-Archive -Path (Join-Path $stage 'moba\*') -DestinationPath (Join-Path $stage 'moba.zip')

$vmxPath = Connect-Guest -Vmx $Vmx

Clear-GuestFile -Names @('compare-exit.txt', 'compare.log', 'compare.json', 'sync.log')

$null = Send-Tree -RepoRoot $script:RepoRoot -Stage $stage

# The measuring itself, inside the guest. The same function for both clients.
@'
param([string] $Quickshell, [string] $Moba, [int] $Runs, [int] $Settle, [int] $Idle, [string] $Out)

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Desk {
    public delegate bool Each(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Each each, IntPtr state);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint relation);
    public static bool HasVisibleWindow(int[] pids) {
        bool found = false;
        EnumWindows((w, s) => {
            uint pid; GetWindowThreadProcessId(w, out pid);
            if (Array.IndexOf(pids, (int)pid) >= 0 && IsWindowVisible(w) && GetWindow(w, 4) == IntPtr.Zero) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

# A process's children, and theirs: only those created after it, because Windows reuses process ids
# and a process whose parent exited long ago still names that parent's id - one run counted a
# hundred and twenty strangers as MobaXterm's before this.
function Tree([int] $root) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate)
    $born = @{}
    foreach ($p in $all) { $born[[int]$p.ProcessId] = $p.CreationDate }
    $tree = New-Object System.Collections.Generic.List[int]
    $tree.Add($root)
    $grew = $true
    while ($grew) {
        $grew = $false
        foreach ($p in $all) {
            $parent = [int]$p.ParentProcessId
            if ($tree.Contains($parent) -and -not $tree.Contains([int]$p.ProcessId) -and $p.CreationDate -ge $born[$parent]) {
                $tree.Add([int]$p.ProcessId); $grew = $true
            }
        }
    }
    ,$tree.ToArray()
}

function Alive([int[]] $pids) { @($pids | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue } | Where-Object { $_ }) }

function Measure-Client([string] $name, [string] $exe, [string] $arguments) {
    $rows = @()
    for ($run = 1; $run -le $Runs; $run++) {
        $startedAt = Get-Date
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $started = if ($arguments) { Start-Process -FilePath $exe -ArgumentList $arguments -PassThru } else { Start-Process -FilePath $exe -PassThru }
        # The process started is the one whose window is waited for: both clients put their main window
        # on that process, and walking the whole tree here costs a CIM query per poll, hundreds of
        # milliseconds that would be counted as the client's.
        $shown = $null
        while ($clock.Elapsed.TotalSeconds -lt 60) {
            if ([Desk]::HasVisibleWindow([int[]]@($started.Id))) { $shown = $clock.Elapsed.TotalMilliseconds; break }
            Start-Sleep -Milliseconds 5
        }
        Start-Sleep -Seconds $Settle
        $tree = Tree $started.Id
        $procs = Alive $tree
        $private = ($procs | Measure-Object PrivateMemorySize64 -Sum).Sum
        $working = ($procs | Measure-Object WorkingSet64 -Sum).Sum
        $before = ($procs | ForEach-Object { $_.Refresh(); $_.TotalProcessorTime.TotalMilliseconds } | Measure-Object -Sum).Sum
        $idleClock = [Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds $Idle
        $after = (Alive $tree | ForEach-Object { $_.TotalProcessorTime.TotalMilliseconds } | Measure-Object -Sum).Sum
        $core = 100.0 * ($after - $before) / $idleClock.Elapsed.TotalMilliseconds
        # A Windows credential prompt opened during the run, which MobaXterm does to guard what it keeps:
        # recorded, because it is part of what starting that client is, and ended, because one left on
        # the desk holds the foreground from everything after it.
        $prompts = @(Get-CimInstance Win32_Process -Filter "Name='PickerHost.exe' or Name='CredentialUIBroker.exe'" |
            Where-Object { $_.CreationDate -ge $startedAt })
        $rows += [pscustomobject]@{
            client = $name; run = $run; shownMs = $shown; processes = $procs.Count
            privateMB = [math]::Round($private / 1MB, 1); workingSetMB = [math]::Round($working / 1MB, 1)
            idleCorePercent = [math]::Round($core, 3); credentialPrompt = ($prompts.Count -gt 0)
        }
        foreach ($p in (Alive (Tree $started.Id))) { try { $p.Kill() } catch { } }
        foreach ($p in $prompts) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 3
    }
    $rows
}

$all = @()
$all += Measure-Client 'quickshell' $Quickshell ''
$all += Measure-Client 'MobaXterm' $Moba ''

$machine = Get-CimInstance Win32_ComputerSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem

[pscustomobject]@{
    when = (Get-Date).ToString('o')
    machine = [pscustomobject]@{ cpu = $cpu.Name; logicalProcessors = $machine.NumberOfLogicalProcessors; memoryGB = [math]::Round($machine.TotalPhysicalMemory / 1GB, 1); os = "$($os.Caption) $($os.Version)" }
    quickshell = (Get-Item $Quickshell).VersionInfo.ProductVersion
    mobaxterm = (Get-Item $Moba).VersionInfo.ProductVersion
    runs = $Runs; settleSeconds = $Settle; idleSeconds = $Idle
    rows = $all
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Out -Encoding utf8
'@ | Set-Content -LiteralPath (Join-Path $stage 'compare.ps1') -Encoding utf8

@"
@echo off
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "MSBUILDDISABLENODEREUSE=1"

cd /d "$script:GuestRepo"
set "PUB=$script:GuestSync\compare-publish"
set "MOBA=$script:GuestSync\compare-moba"
if exist "%PUB%" rmdir /s /q "%PUB%"
if exist "%MOBA%" rmdir /s /q "%MOBA%"

dotnet publish "$script:GuestRepo\src\Quickshell.App\Quickshell.App.csproj" --configuration Release --runtime win-x64 --self-contained true -p:PublishReadyToRun=true --output "%PUB%" --nologo -v quiet > "$script:GuestSync\compare.log" 2>&1
if errorlevel 1 goto done
dotnet build-server shutdown >nul 2>&1

powershell -NoProfile -Command "Expand-Archive -LiteralPath '$script:GuestSync\moba.zip' -DestinationPath '%MOBA%'" >> "$script:GuestSync\compare.log" 2>&1
if errorlevel 1 goto done

for %%E in ("%PUB%\*.exe") do if /i not "%%~nxE"=="createdump.exe" set "CLIENT=%%E"
for %%E in ("%MOBA%\MobaXterm*.exe") do set "MOBAEXE=%%E"

powershell -NoProfile -ExecutionPolicy Bypass -File "$script:GuestSync\compare.ps1" -Quickshell "%CLIENT%" -Moba "%MOBAEXE%" -Runs $Runs -Settle $SettleSeconds -Idle $IdleSeconds -Out "$script:GuestSync\compare.json" >> "$script:GuestSync\compare.log" 2>&1

:done
> "$script:GuestSync\compare-exit.txt" echo %ERRORLEVEL%
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $stage 'compare.cmd') -Encoding ascii

foreach ($file in @('compare.cmd', 'compare.ps1', 'moba.zip')) {
    $sent = Invoke-VmRun -Guest -Arguments @('copyFileFromHostToGuest', $vmxPath, (Join-Path $stage $file), "$script:GuestSync\$file")
    if (-not $sent.Ok) { Refuse "could not copy $file into the guest: $($sent.Output)" }
}

Write-Host ''
Write-Host "  measuring both clients in the guest, $Runs starts each" -ForegroundColor Cyan

# -interactive: each start is a window, and a window needs the logged-in desk.
$ran = Invoke-VmRun -Guest -Arguments @('runProgramInGuest', $vmxPath, '-interactive', "$script:GuestSync\compare.cmd")
if (-not $ran.Ok) { Refuse "the guest never finished: $($ran.Output)" }

$results = Join-Path $script:RepoRoot 'TestResults\vm'
if (-not (Test-Path -LiteralPath $results)) { $null = New-Item -ItemType Directory -Path $results -Force }

foreach ($file in @('compare.log', 'compare.json', 'compare-exit.txt')) {
    $null = Invoke-VmRun -Guest -Arguments @('copyFileFromGuestToHost', $vmxPath, "$script:GuestSync\$file", (Join-Path $results $file))
}

$report = Join-Path $results 'compare.json'
$said = if (Test-Path -LiteralPath (Join-Path $results 'compare-exit.txt')) { (Read-ConsoleText (Join-Path $results 'compare-exit.txt')).Trim() } else { '' }

if ($said -ne '0' -or -not (Test-Path -LiteralPath $report)) {
    $log = Join-Path $results 'compare.log'
    if (Test-Path -LiteralPath $log) { foreach ($line in ((Read-ConsoleText $log) -split "`r?`n")) { Write-Host $line } }
    Refuse "the measuring did not finish in the guest (exit $said)" "The log is $log."
}

$measured = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json

Write-Host ''
Write-Host "  $($measured.machine.cpu), $($measured.machine.logicalProcessors) logical, $($measured.machine.memoryGB) GB, $($measured.machine.os)"
Write-Host "  quickshell $($measured.quickshell), MobaXterm $($measured.mobaxterm) (exe SHA-256 $actual)"
$measured.rows | Format-Table client, run, shownMs, processes, privateMB, workingSetMB, idleCorePercent, credentialPrompt -AutoSize | Out-String | Write-Host

foreach ($client in @('quickshell', 'MobaXterm')) {
    $mine = @($measured.rows | Where-Object client -eq $client)
    $median = { param($values) $sorted = @($values | Sort-Object); $sorted[[int][math]::Floor(($sorted.Count - 1) / 2)] }
    Write-Host ("  {0,-10} median: shown {1:0} ms, private {2} MB, working set {3} MB, idle {4}% of a core" -f $client,
        (& $median ($mine.shownMs)), (& $median ($mine.privateMB)), (& $median ($mine.workingSetMB)), (& $median ($mine.idleCorePercent)))
}

Write-Host "compare-mobaxterm-vm: $report" -ForegroundColor Green
exit 0
