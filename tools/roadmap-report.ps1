# Splits the backlog by kind, because one number cannot answer "is this converging?".
#
# Four things share the roadmap and move in opposite directions: work that was planned, work
# discovered while delivering, debt in the test harness, and scope deliberately deferred. A single
# open count mixes all four, so a block of features finishing and a block of defects opening look
# identical.
#
# Nothing here is maintained by hand. The two facts it needs are already in the files:
#
#   * an id at or above the first discovered one was opened during delivery, never planned;
#   * Block K is the harness, so its lines are debt in the measuring apparatus and not in the
#     product.
#
# Run it from the repository root: .\roadmap-report.cmd

[CmdletBinding()]
param(
    # The first id that was opened during delivery rather than in the original backlog. Everything
    # below it came from the plan.
    [int] $FirstDiscovered = 100,

    [string] $Roadmap = 'docs/ROADMAP.md',
    [string] $Changelog = 'docs/CHANGELOG.md'
)

$ErrorActionPreference = 'Stop'

function Read-Lines {
    param([string] $Path)

    if (-not (Test-Path $Path)) {
        throw "$Path is not there; run this from the repository root."
    }

    return [System.IO.File]::ReadAllLines((Resolve-Path $Path))
}

# One row per task line, carrying the block it sits under.
function Get-Tasks {
    param([string[]] $Lines)

    $block = '?'
    $found = @()

    foreach ($line in $Lines) {
        if ($line -match '^##\s+Block\s+([A-Z])\b') {
            $block = $Matches[1]
            continue
        }

        # "Done when" headings end the task lists and start the criteria.
        if ($line -match '^##\s+Done when') {
            $block = '-'
            continue
        }

        if ($block -eq '-') { continue }

        if ($line -match '^\-\s+(\S+)\s+\*\*QS(\d+)\*\*') {
            $found += [pscustomobject]@{
                Id     = [int] $Matches[2]
                Marker = $Matches[1]
                Block  = $block
            }
        }
    }

    return $found
}

$open = Get-Tasks -Lines (Read-Lines $Roadmap)
$done = Get-Tasks -Lines (Read-Lines $Changelog)

function Kind {
    param([pscustomobject] $Task)

    if ($Task.Block -eq 'K') { return 'harness' }
    if ($Task.Id -ge $FirstDiscovered) { return 'discovered' }

    return 'planned'
}

$openByKind = $open | Group-Object { Kind $_ } | Sort-Object Name
$doneByKind = $done | Group-Object { Kind $_ } | Sort-Object Name

Write-Output ''
Write-Output 'Backlog by kind'
Write-Output '---------------'

$kinds = @('planned', 'discovered', 'harness')

foreach ($kind in $kinds) {
    $o = ($openByKind | Where-Object Name -eq $kind).Count
    $d = ($doneByKind | Where-Object Name -eq $kind).Count
    if ($null -eq $o) { $o = 0 }
    if ($null -eq $d) { $d = 0 }

    $total = $o + $d
    $share = if ($total -gt 0) { [int](100 * $d / $total) } else { 0 }

    Write-Output ("  {0,-11} open {1,3}   shipped {2,3}   {3,3}% done" -f $kind, $o, $d, $share)
}

Write-Output ''
Write-Output ("  {0,-11} open {1,3}   shipped {2,3}" -f 'all', $open.Count, $done.Count)

# The ratio that says whether discovery is settling down. Above one, delivery is opening more than
# it closes; well below one, the unknowns are running out.
$discoveredTotal = ($open | Where-Object { (Kind $_) -ne 'planned' }).Count +
                   ($done | Where-Object { (Kind $_) -ne 'planned' }).Count
$plannedShipped = ($done | Where-Object { (Kind $_) -eq 'planned' }).Count

if ($plannedShipped -gt 0) {
    $rate = [math]::Round($discoveredTotal / $plannedShipped, 2)
    Write-Output ''
    Write-Output ("  discovery rate: {0} opened per planned task shipped" -f $rate)
}

Write-Output ''
Write-Output 'Open by block'
Write-Output '-------------'

foreach ($group in ($open | Group-Object Block | Sort-Object Name)) {
    Write-Output ("  Block {0}   {1,3} open" -f $group.Name, $group.Count)
}

Write-Output ''
