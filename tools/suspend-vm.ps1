#Requires -Version 5.1
<#
.SYNOPSIS
  Suspend every VM running on this host, so the next measurement here is of this desk alone.

.DESCRIPTION
  QS374. run-tests-vm, run-startup-vm and run-app-vm leave the guest running when they finish, which
  is what lets the next run start in seconds. It is also what makes a performance-gate sitting on
  this desk come out too noisy to judge, and the gate now refuses while one is up and names this.

  A bare `vmrun suspend` on an encrypted guest is refused with "a password is required", so this
  goes through vm-guest.ps1 for the encryption password the runners already read. It takes the same
  desk claim they take, so a suite or a winwright run in progress is waited for, not suspended under.

  A VM that is not the configured one is suspended too: the gate's question is whether anything is
  running here, not which guest it is. One that needs a password this file does not have is named
  and left running, and the exit code says so.

.NOTES
  Credentials come from a file outside this tree. vm-guest.ps1 says which.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Tool = 'suspend-vm'

. (Join-Path $PSScriptRoot 'vm-guest.ps1')

$null = Import-EnvFile
$script:VmRun = Find-VmRun
if (-not $script:VmRun) {
    Refuse 'vmrun.exe was not found on this host' 'Without VMware there is no guest to suspend.'
}
$script:VmPassword = Get-Setting 'VM_PASSWORD'

function Get-Running {
    $listed = Invoke-VmRun -Arguments @('list')
    if (-not $listed.Ok) { Refuse "vmrun could not list the running VMs: $($listed.Output)" }
    return @($listed.Output -split "`r?`n" | ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and $_ -notmatch '^Total running VMs' })
}

$running = Get-Running
if ($running.Count -eq 0) {
    Write-Host 'suspend-vm: no VM is running on this host' -ForegroundColor Green
    exit 0
}

$configured = Get-Setting 'VMX'
Enter-DeskClaim -VmxPath $(if ($configured) { $configured } else { $running[0] })

# Read again once the claim is held: a run that held it may have been the one that started a guest.
$left = @()
foreach ($vmx in Get-Running) {
    Write-Host "  suspending  $vmx"
    $suspended = Invoke-VmRun -Arguments @('suspend', $vmx)
    if (-not $suspended.Ok) {
        Write-Host "  not suspended: $(($suspended.Output -split "`r?`n" | Where-Object { $_.Trim() }) -join ' ')" -ForegroundColor Red
        $left += $vmx
    }
}

if ($left.Count -gt 0) {
    Refuse "$($left.Count) VM(s) are still running" 'Suspend them from VMware Workstation; one encrypted with another password needs that password.'
}

Write-Host 'suspend-vm: every VM on this host is suspended' -ForegroundColor Green
exit 0
