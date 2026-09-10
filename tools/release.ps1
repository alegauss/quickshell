#Requires -Version 5.1
<#
.SYNOPSIS
  Build what a release ships: the client published self-contained, signed, zipped as a portable
  copy, and the checksum a download is checked against.

.DESCRIPTION
  QS77. One archive, and it is both editions. Unzipped and started, it is a portable copy that keeps
  its settings beside itself and writes nothing to the profile; `quickshell --install`, or "Install
  quickshell for this user" in its palette, installs it for the person running it, with no
  administrator prompt.

  The build is the one QS75 measured - Release, self-contained, ReadyToRun - so the start-up figure
  in benchmarks\results\startup-h.md is a figure about the thing people download.

  It does not ship unsigned by accident. Signing is part of making a release rather than a later
  improvement: an unsigned binary is refused by SmartScreen and by corporate policy, which for a
  client that handles credentials is the worst first impression on offer. So with a certificate it
  signs and refuses a signature this machine does not verify, and without one it refuses to run
  unless -Unsigned says so - and then the archive's own name says so too, where nobody can miss it.

.PARAMETER Certificate
  The thumbprint of a code-signing certificate in the current user's store. Its private key may be
  on a token; signtool asks the token for its PIN itself.

.PARAMETER Timestamp
  An RFC 3161 timestamp server. A signature without a timestamp dies with its certificate; one with
  a timestamp stays valid for what was signed while the certificate was.

.PARAMETER Unsigned
  Build without signing. The archive is named ...-unsigned.zip, so it cannot pass for a release.

.PARAMETER Output
  Where the archive and SHA256SUMS.txt go: artifacts\release unless given, which .gitignore covers.
#>
[CmdletBinding()]
param(
    [string] $Certificate,

    [string] $Timestamp = 'http://timestamp.digicert.com',

    [switch] $Unsigned,

    [string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Quickshell.App\Quickshell.App.csproj'
$publish = Join-Path $root 'artifacts\publish\quickshell'
if (-not $Output) { $Output = Join-Path $root 'artifacts\release' }

function Refuse {
    param([Parameter(Mandatory)] [string] $What, [string] $Remedy)
    Write-Host ''
    Write-Host "release: $What" -ForegroundColor Red
    if ($Remedy) { Write-Host "         -> $Remedy" -ForegroundColor Yellow }
    exit 3
}

function Find-SignTool {
    # The newest SDK's, because an older signtool may not know the digest a timestamp server answers
    # with. x64 and never arm or x86: this is the machine the release is built on.
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $found = Get-ChildItem -LiteralPath $kits -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version] $_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($found) { return $found }
    }
    $onPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

if (-not $Certificate -and -not $Unsigned) {
    Refuse 'a release is signed, and no certificate was given' 'Pass -Certificate <thumbprint>, or -Unsigned for a build whose name says it is not a release.'
}
if ($Certificate -and $Unsigned) {
    Refuse '-Certificate and -Unsigned were both given' 'A build is one or the other.'
}

$signtool = $null
if ($Certificate) {
    # Asked before a two-minute publish rather than after it.
    $signtool = Find-SignTool
    if (-not $signtool) { Refuse 'signtool.exe was not found' 'Install the signing tools of the Windows SDK.' }
    if (-not (Test-Path -LiteralPath "Cert:\CurrentUser\My\$Certificate")) {
        Refuse "no certificate with the thumbprint $Certificate is in the current user's store" 'certmgr.msc shows what is there, under Personal.'
    }
}

$version = (& dotnet msbuild $project -nologo -getProperty:Version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { Refuse 'the version could not be read from the project' }

Write-Host "release  quickshell $version" -ForegroundColor Cyan

# Wiped, never published over: a file the last build had and this one does not would otherwise ride
# into the archive as part of a version it is not.
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }

& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishReadyToRun=true --output $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Refuse 'the publish failed' }

if ($Certificate) {
    # This client's own files. The runtime's are Microsoft's, already signed by Microsoft.
    $ours = @(Get-ChildItem -LiteralPath $publish -File |
        Where-Object { $_.Name -eq 'quickshell.exe' -or $_.Name -like 'Quickshell.*.dll' })

    & $signtool sign /sha1 $Certificate /fd sha256 /tr $Timestamp /td sha256 /q @($ours | ForEach-Object { $_.FullName })
    if ($LASTEXITCODE -ne 0) { Refuse 'signtool could not sign the build' }

    foreach ($file in $ours) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid') {
            Refuse "$($file.Name) is signed but does not verify: $($signature.StatusMessage)" 'A release is signed with a certificate this machine trusts, or it is not signed at all.'
        }
    }
    Write-Host "  signed     $($ours.Count) files, by $((Get-Item "Cert:\CurrentUser\My\$Certificate").Subject)"
}
else {
    Write-Host '  UNSIGNED   no certificate was given, and the archive is named for it' -ForegroundColor Yellow
}

# The marker that makes an unzipped copy portable. Installing it leaves the marker behind.
$null = New-Item -ItemType File -Path (Join-Path $publish 'quickshell.portable') -Force

$suffix = if ($Unsigned) { '-unsigned' } else { '' }
$name = "quickshell-$version-win-x64$suffix.zip"
$null = New-Item -ItemType Directory -Path $Output -Force
$zip = Join-Path $Output $name
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

# With its folder, so unzipping it in Downloads makes one folder and not two hundred files.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

# The line `sha256sum -c` reads, with a newline it reads too.
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $Output 'SHA256SUMS.txt'), "$hash  $name`n")

$files = @(Get-ChildItem -LiteralPath $publish -File -Recurse).Count
$unpacked = [math]::Round((Get-ChildItem -LiteralPath $publish -File -Recurse | Measure-Object Length -Sum).Sum / 1MB)
$packed = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)

Write-Host "  archive    $zip"
Write-Host "             $packed MB, $files files, $unpacked MB unpacked"
Write-Host "  sha256     $hash"
exit 0
