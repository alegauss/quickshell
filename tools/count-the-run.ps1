#Requires -Version 5.1
<#
.SYNOPSIS
  Says how much of the suite ran, out of the reports a run left behind, and holds the skips to a
  budget.

.DESCRIPTION
  QS136. A run that skipped a hundred tests printed the same "All 5 test assemblies passed" as one
  that skipped none: the docker fixture stops on its own, every test needing it calls SkipUnless,
  and the line a person reads did not change. One session saw four green runs skipping 81, 103, 96
  and 96 of 228 tests. A false green is worse than a false red, because nobody investigates it.

  So every run ends with the counts - ran, passed, failed, skipped, per assembly and in total - and
  the reasons the skips gave, grouped. And a skip is held to a budget checked in beside the tests
  (tests\skips.json): a number per assembly, so a new skip is a decision somebody wrote down rather
  than weather.

  One kind of skip is not weather and not a decision either: a desk with no SSH fixture at all,
  which is every guest run. Those are waived only where the run says so - QUICKSHELL_NO_FIXTURE set
  - and are then printed as waived, by count and reason, rather than counted silently. A desk that
  forgot to start the fixture does not say so, and its skips are over budget.

  Exits 1 where a budget is exceeded and 0 otherwise; failed tests are run-tests.cmd's to judge.

.PARAMETER From
  The directory the reports were written to.

.PARAMETER Budget
  The checked-in budget. Without one every skip is over it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $From,
    [string] $Budget
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# What a fixture test says when there is nothing to connect to: the sshd fixture's and the version
# matrix's both end by naming the script that brings them up. Matched on the reason because the
# reason is the test's own account of why it did not run.
$fixtureAbsent = '127\.0\.0\.1:\d+: run prototypes/SshProbe/(fixture|matrix)/up\.sh'
$waiving = -not [string]::IsNullOrEmpty($env:QUICKSHELL_NO_FIXTURE)

$allowed = @{}

if ($Budget -and (Test-Path -LiteralPath $Budget)) {
    # Comments are allowed in the file, as they are in every other JSON this repository keeps by
    # hand, because a number with no reason beside it is a number somebody will raise to make a run
    # pass.
    $text = (Get-Content -LiteralPath $Budget -Raw) -replace '(?m)^\s*//.*$', ''
    $parsed = $text | ConvertFrom-Json

    foreach ($property in $parsed.PSObject.Properties) {
        $allowed[$property.Name] = [int] $property.Value
    }
}

$reports = @(Get-ChildItem -LiteralPath $From -Filter '*.trx' -File -ErrorAction SilentlyContinue | Sort-Object Name)

if ($reports.Count -eq 0) {
    Write-Host "  no reports were written to $From, so nothing can be said about what ran" -ForegroundColor Yellow
    exit 1
}

$totals = @{ Total = 0; Passed = 0; Failed = 0; Skipped = 0; Waived = 0 }
$over = @()
$reasons = @{}
$waivedReasons = @{}

Write-Host ''
Write-Host ('  {0,-34} {1,6} {2,7} {3,7} {4,8}' -f 'assembly', 'tests', 'passed', 'failed', 'skipped')

foreach ($report in $reports) {
    $name = [IO.Path]::GetFileNameWithoutExtension($report.Name)

    try {
        [xml] $trx = Get-Content -LiteralPath $report.FullName -Raw
    }
    catch {
        Write-Host "  $($report.Name) could not be read: $($_.Exception.Message)" -ForegroundColor Yellow
        $over += "$name (unreadable report)"
        continue
    }

    $namespaces = New-Object System.Xml.XmlNamespaceManager $trx.NameTable
    $namespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    $results = @($trx.SelectNodes('//t:UnitTestResult', $namespaces))
    $passed = @($results | Where-Object { $_.GetAttribute('outcome') -eq 'Passed' }).Count
    $failed = @($results | Where-Object { $_.GetAttribute('outcome') -eq 'Failed' }).Count
    $skips = @($results | Where-Object { $_.GetAttribute('outcome') -eq 'NotExecuted' })

    $counted = 0
    $waived = 0

    foreach ($skip in $skips) {
        # The skip's reason is the first line of what it wrote; xunit puts it in the output.
        $said = $skip.SelectSingleNode('t:Output/t:StdOut', $namespaces)

        if (-not $said) { $said = $skip.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespaces) }

        $reason = if ($said -and $said.InnerText) {
            (($said.InnerText -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
        } else { '(no reason given)' }

        if ($waiving -and $reason -match $fixtureAbsent) {
            $waived++
            $waivedReasons[$reason] = 1 + $(if ($waivedReasons.ContainsKey($reason)) { $waivedReasons[$reason] } else { 0 })
            continue
        }

        $counted++
        $key = "$name  $reason"
        $reasons[$key] = 1 + $(if ($reasons.ContainsKey($key)) { $reasons[$key] } else { 0 })
    }

    $totals.Total += $results.Count
    $totals.Passed += $passed
    $totals.Failed += $failed
    $totals.Skipped += $counted
    $totals.Waived += $waived

    $budgeted = if ($allowed.ContainsKey($name)) { $allowed[$name] } else { 0 }
    $skippedText = if ($waived -gt 0) { "$counted+$waived" } else { "$counted" }
    $colour = if ($failed -gt 0 -or $counted -gt $budgeted) { 'Red' } elseif ($counted + $waived -gt 0) { 'Yellow' } else { 'Gray' }

    Write-Host ('  {0,-34} {1,6} {2,7} {3,7} {4,8}' -f $name, $results.Count, $passed, $failed, $skippedText) -ForegroundColor $colour

    if ($counted -gt $budgeted) {
        $over += "$name skipped $counted, budget $budgeted"
    }
}

Write-Host ('  {0,-34} {1,6} {2,7} {3,7} {4,8}' -f 'all', $totals.Total, $totals.Passed, $totals.Failed,
            $(if ($totals.Waived -gt 0) { "$($totals.Skipped)+$($totals.Waived)" } else { "$($totals.Skipped)" }))

if ($reasons.Count -gt 0) {
    Write-Host ''
    Write-Host '  skipped, and why:'

    foreach ($entry in ($reasons.GetEnumerator() | Sort-Object Value -Descending)) {
        Write-Host ('  {0,5}  {1}' -f $entry.Value, $entry.Key)
    }
}

if ($totals.Waived -gt 0) {
    Write-Host ''
    Write-Host "  WAIVED: $($totals.Waived) tests did not run because this desk declares no SSH fixture (QUICKSHELL_NO_FIXTURE)." -ForegroundColor Yellow

    foreach ($entry in ($waivedReasons.GetEnumerator() | Sort-Object Value -Descending)) {
        Write-Host ('  {0,5}  {1}' -f $entry.Value, $entry.Key) -ForegroundColor Yellow
    }
}

if ($over.Count -gt 0) {
    Write-Host ''
    Write-Host '  OVER THE SKIP BUDGET (tests\skips.json):' -ForegroundColor Red

    foreach ($line in $over) { Write-Host "    $line" -ForegroundColor Red }

    if (-not $waiving) {
        Write-Host '    A fixture that is not running is the usual cause: prototypes/SshProbe/fixture/up.sh.' -ForegroundColor Red
    }

    exit 1
}

exit 0
