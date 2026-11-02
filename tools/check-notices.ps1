#Requires -Version 5.1
<#
.SYNOPSIS
  Refuse a publish that ships a package THIRD-PARTY-NOTICES.md does not name, at that version, under
  an OSI-approved licence.

.DESCRIPTION
  QS378. A free code-signing certificate for open source (SignPath Foundation) is granted to a
  project whose every shipped component is OSI-licensed, and the notices file is where that claim is
  written. It had already drifted once: two packages listed at a version the build no longer
  resolved, and the runtime called "not bundled" in an archive that carries it. So the claim is
  checked against what the publish actually resolved, every time release.ps1 makes an archive.

  What shipped is read from the publish's own deps.json: every library of type package or
  runtimepack. A row of the notices table matches one when its Packages cell names the id and its
  Version cell is that version exactly.

.PARAMETER Deps
  The publish's <app>.deps.json.

.PARAMETER Notices
  THIRD-PARTY-NOTICES.md.

.OUTPUTS
  Exit 0 and one line when every package is listed; exit 3 and each missing one named otherwise.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Deps,
    [Parameter(Mandatory)] [string] $Notices
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The licences a shipped component may carry: OSI-approved, and the ones this tree has met or is
# likely to. One outside the list is a decision to make in the notices file, not here.
$osi = @('MIT', 'ISC', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'MS-PL', 'Zlib', 'BSL-1.0')

$rows = @()
foreach ($line in Get-Content -LiteralPath $Notices) {
    $cells = @($line.Trim().Trim('|') -split '\|' | ForEach-Object { $_.Trim() })
    # Library, Packages, Version, Licence, Copyright - the header and its rule are not rows.
    if ($cells.Count -ne 5 -or $cells[1] -eq 'Packages' -or $cells[1] -match '^-+$') { continue }
    $rows += [pscustomobject]@{
        Packages = @($cells[1] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        Version  = $cells[2]
        Licence  = $cells[3]
    }
}

$json = Get-Content -LiteralPath $Deps -Raw | ConvertFrom-Json
$missing = @()
$checked = 0
foreach ($library in $json.libraries.PSObject.Properties) {
    if ($library.Value.type -notin @('package', 'runtimepack')) { continue }
    $id, $version = $library.Name -split '/', 2
    $id = $id -replace '^runtimepack\.', ''
    $checked++

    $row = $rows | Where-Object { $_.Packages -contains $id -and $_.Version -eq $version } | Select-Object -First 1
    if (-not $row) {
        $missing += "$id $version is shipped and not listed at that version"
    }
    elseif ($row.Licence -notin $osi) {
        $missing += "$id $version is listed under '$($row.Licence)', which is not an OSI licence this check knows"
    }
}

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host 'release: THIRD-PARTY-NOTICES.md does not account for what this archive ships' -ForegroundColor Red
    foreach ($one in $missing) { Write-Host "         $one" -ForegroundColor Red }
    Write-Host '         -> List each in the table with its package id, the version shipped and its licence.' -ForegroundColor Yellow
    exit 3
}

Write-Host "  notices    $checked packages and runtime packs, each listed under an OSI licence"
exit 0
