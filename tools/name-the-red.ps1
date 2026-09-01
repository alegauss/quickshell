#Requires -Version 5.1
<#
.SYNOPSIS
  Names every test that failed, out of the reports a run left behind.

.DESCRIPTION
  QS175. A run of `Quickshell.App.Tests` went red once, out of three hundred tests, and what was
  known afterwards was the count. The console had named it and the console was gone - piped into a
  filter, scrolled past, or thrown away by the guest.

  So every assembly now writes a TRX beside the run, and this reads them. The summary a red run
  prints is the names, not a number, and the file is still there tomorrow.

  It is a reporter and never a verdict: it exits 0 whatever it found. The suite's exit code is
  run-tests.cmd's, and a second thing that could fail a build is a second thing that can be wrong
  about one.

.PARAMETER From
  The directory the reports were written to.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $From
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $From)) {
    Write-Host "  no reports were written to $From" -ForegroundColor Yellow
    exit 0
}

$reports = @(Get-ChildItem -LiteralPath $From -Filter '*.trx' -File -ErrorAction SilentlyContinue)

if ($reports.Count -eq 0) {
    Write-Host "  no reports were written to $From" -ForegroundColor Yellow
    exit 0
}

$found = 0

foreach ($report in $reports) {
    try {
        [xml] $trx = Get-Content -LiteralPath $report.FullName -Raw
    }
    catch {
        # A report that will not parse is itself worth saying, and is not a reason to skip the rest.
        Write-Host "  $($report.Name) could not be read: $($_.Exception.Message)" -ForegroundColor Yellow
        continue
    }

    # SelectNodes with the TRX namespace rather than dotted access: a run with one result gives a
    # single node where a run with several gives a collection, and dotted access spells those two
    # differently.
    $namespaces = New-Object System.Xml.XmlNamespaceManager $trx.NameTable
    $namespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    $failed = @($trx.SelectNodes('//t:UnitTestResult[@outcome="Failed"]', $namespaces))

    foreach ($one in $failed) {
        $found++

        Write-Host "  $($one.GetAttribute('testName'))" -ForegroundColor Red

        $message = $one.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespaces)

        if ($message -and $message.InnerText) {
            # The first two lines. The whole of an assertion failure belongs in the report, and a
            # summary that reprints it is one nobody reads to the end of.
            foreach ($line in (($message.InnerText -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 2)) {
                Write-Host "      $($line.Trim())"
            }
        }
    }
}

if ($found -eq 0) {
    # A red run whose reports name nothing failed is itself the finding: the assembly died before it
    # could write one, which is a crash or a thread left running rather than a test.
    Write-Host '  the reports name no failed test, so what went wrong was not a test' -ForegroundColor Yellow
}

Write-Host "  reports     $From"
exit 0
