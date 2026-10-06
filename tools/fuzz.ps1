#Requires -Version 5.1
<#
.SYNOPSIS
  Fuzz the parser and the model with libFuzzer, steered by coverage, for as long as it is given.

.DESCRIPTION
  QS102. The suite's own mutator (HostileInputTests) runs three thousand fixed mutations on every
  build: bounded, reproducible, and the same inputs for ever. This is the other half - a search
  rather than a list - and it runs outside the suite because a campaign is as long as it is given.

  What it does, in order:
    1. builds tools/Quickshell.Fuzz in Release;
    2. instruments the Quickshell.Terminal.dll beside it with SharpFuzz, pinned in dotnet-tools.json;
    3. fetches libFuzzer's .NET driver from its author's GitHub release into artifacts/fuzz, and
       refuses any file whose SHA-256 is not the one pinned below - the release publishes no digest
       and the binary is unsigned, so the hash measured when this was written is the trust;
    4. seeds artifacts/fuzz/corpus from benchmarks/corpus/streams, cut into 4 KB pieces, once;
    5. runs the campaign. The corpus grows across runs, and a crash lands in artifacts/fuzz/findings
       as the exact input that caused it.

  A finding becomes a named shape in HostileInputTests, so the build fails on it from then on.

.PARAMETER Seconds
  How long the campaign runs. Default ten minutes.

.PARAMETER Jobs
  How many fuzzing processes run in parallel. Default one.
#>
[CmdletBinding()]
param(
    [int] $Seconds = 600,
    [int] $Jobs = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'artifacts\fuzz'
$corpus = Join-Path $work 'corpus'
$findings = Join-Path $work 'findings'
$driver = Join-Path $work 'libfuzzer-dotnet-windows.exe'

# libfuzzer-dotnet v2025.05.02.0904, measured 2026-10-06.
$driverRelease = 'v2025.05.02.0904'
$driverSha256 = '17AF5B3F6FF4D2C57B44B9A35C13051B570EB66F0557D00015DF3832709050BF'

New-Item -ItemType Directory -Force $work, $corpus, $findings | Out-Null

Write-Host 'fuzz: building tools/Quickshell.Fuzz (Release)'
& dotnet build (Join-Path $root 'tools\Quickshell.Fuzz\Quickshell.Fuzz.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'the fuzz target did not build' }

$output = Join-Path $root 'tools\Quickshell.Fuzz\bin\Release\net10.0-windows'
$staged = Join-Path $work 'target'
$target = Join-Path $staged 'Quickshell.Fuzz.exe'

# A fresh copy, instrumented fresh, every run. Instrumenting in the build's own folder went wrong the
# first time it was tried: the next build copied the plain DLL back with its old timestamp, nothing
# could tell it from the instrumented one, and a ten-minute campaign ran blind - six million inputs,
# no coverage gained.
if (Test-Path $staged) { Remove-Item -Recurse -Force $staged }
Copy-Item -Recurse $output $staged

Write-Host 'fuzz: instrumenting Quickshell.Terminal.dll'
Push-Location $root
try {
    & dotnet tool restore | Out-Null
    & dotnet sharpfuzz (Join-Path $staged 'Quickshell.Terminal.dll')
    if ($LASTEXITCODE -ne 0) { throw 'SharpFuzz could not instrument Quickshell.Terminal.dll' }
}
finally { Pop-Location }

if (-not (Test-Path $driver) -or (Get-FileHash $driver -Algorithm SHA256).Hash -ne $driverSha256) {
    Write-Host "fuzz: fetching libfuzzer-dotnet $driverRelease"
    & gh release download $driverRelease --repo Metalnem/libfuzzer-dotnet --pattern 'libfuzzer-dotnet-windows.exe' --dir $work --clobber
    if ($LASTEXITCODE -ne 0) { throw 'the driver could not be fetched; gh must be signed in' }

    $actual = (Get-FileHash $driver -Algorithm SHA256).Hash
    if ($actual -ne $driverSha256) {
        Remove-Item $driver
        throw "the driver's SHA-256 is $actual and $driverSha256 was pinned; nothing was run"
    }
}

if (-not (Get-ChildItem $corpus -File | Select-Object -First 1)) {
    Write-Host 'fuzz: seeding the corpus from benchmarks/corpus/streams'
    foreach ($stream in Get-ChildItem (Join-Path $root 'benchmarks\corpus\streams') -Filter '*.raw.gz') {
        $file = [System.IO.File]::OpenRead($stream.FullName)
        $unzip = New-Object System.IO.Compression.GZipStream($file, [System.IO.Compression.CompressionMode]::Decompress)
        $bytes = New-Object System.IO.MemoryStream
        $unzip.CopyTo($bytes)
        $unzip.Dispose(); $file.Dispose()

        # The first 64 pieces of each, which is variety without a corpus libFuzzer spends its first
        # hour minimising.
        $all = $bytes.ToArray()
        $name = $stream.Name -replace '\.raw\.gz$', ''
        for ($piece = 0; $piece -lt 64 -and $piece * 4096 -lt $all.Length; $piece++) {
            $length = [Math]::Min(4096, $all.Length - $piece * 4096)
            $slice = New-Object byte[] $length
            [Array]::Copy($all, $piece * 4096, $slice, 0, $length)
            [System.IO.File]::WriteAllBytes((Join-Path $corpus "$name-$piece"), $slice)
        }
    }
}

Write-Host "fuzz: running for $Seconds s, $Jobs job(s); findings land in $findings"

# Inside the work folder, because libFuzzer writes its per-job logs to wherever it was started.
Push-Location $work
try {
    & $driver "--target_path=$target" $corpus "-max_total_time=$Seconds" "-jobs=$Jobs" "-workers=$Jobs" "-artifact_prefix=$findings\" -max_len=4096 -print_final_stats=1
    $exit = $LASTEXITCODE
}
finally { Pop-Location }

$found = @(Get-ChildItem $findings -File -Filter 'crash-*')
if ($found.Count -gt 0) {
    Write-Host "fuzz: $($found.Count) crash(es) in $findings - each becomes a shape in HostileInputTests"
    exit 1
}

Write-Host "fuzz: no crash in $Seconds s (driver exit $exit)"
exit 0
