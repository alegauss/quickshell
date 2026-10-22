#Requires -Version 5.1
<#
.SYNOPSIS
  Build a release with release.ps1, tag the commit it was built from, and publish it on GitHub with
  the archive and its checksum attached.

.DESCRIPTION
  QS371. release.ps1 (QS77) makes what a release ships and stops at artifacts\release; this takes it
  to the page people download from. It runs here and not on a hosted runner because the signing
  certificate and the performance baseline both live on this desk - CI builds an archive, unsigned
  and ungated, and that is not a release.

  A release names one commit, so everything that could make the archive and the tag disagree is
  refused before the two-minute publish starts: a tree with changes in it, a HEAD that is not what
  the branch's upstream has, a tag v<version> that already exists here or on GitHub, a release of
  that name already on GitHub. Bump <Version> in Directory.Build.props for a new one.

  Then release.ps1 runs with the same flags, HEAD is tagged v<version> and the tag pushed, and the
  GitHub release is created with the archive and SHA256SUMS.txt. An unsigned archive is published
  as a pre-release, so the listing says what the file name already says.

.PARAMETER Certificate
  Passed to release.ps1: the thumbprint of the code-signing certificate.

.PARAMETER Timestamp
  Passed to release.ps1: the RFC 3161 timestamp server.

.PARAMETER Unsigned
  Passed to release.ps1. The archive is named ...-unsigned.zip and the release is a pre-release.

.PARAMETER Ungated
  Passed to release.ps1: build without the performance gate, and say so.

.PARAMETER Notes
  A file whose text is the release's description. Without it GitHub writes one from the commits
  since the last release.

.PARAMETER Draft
  Create the release as a draft, which nobody but the maintainers sees until it is published from
  the release's page.

.PARAMETER DryRun
  Run every check and the build, and stop before anything is tagged, pushed or uploaded.
#>
[CmdletBinding()]
param(
    [string] $Certificate,

    [string] $Timestamp,

    [switch] $Unsigned,

    [switch] $Ungated,

    [string] $Notes,

    [switch] $Draft,

    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Quickshell.App\Quickshell.App.csproj'
$output = Join-Path $root 'artifacts\release'

function Refuse {
    param([Parameter(Mandatory)] [string] $What, [string] $Remedy)
    Write-Host ''
    Write-Host "publish-release: $What" -ForegroundColor Red
    if ($Remedy) { Write-Host "                 -> $Remedy" -ForegroundColor Yellow }
    exit 3
}

function Read-Git {
    # Continue, because under Stop Windows PowerShell turns git's stderr into an exception, and a
    # failed git here is an answer (no upstream, no such tag) that the caller reads as $null.
    $ErrorActionPreference = 'Continue'
    $text = (& git -C $root @args 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { return $null }
    return $text
}

function Test-Native {
    # Whether a command succeeded, with everything it printed thrown away - under Continue, for the
    # reason Read-Git gives.
    $ErrorActionPreference = 'Continue'
    $exe, $rest = $args
    & $exe @rest *> $null
    return $LASTEXITCODE -eq 0
}

# release.ps1 makes this refusal too, but only after it has been asked; asked here it costs nothing.
if (-not $Certificate -and -not $Unsigned) {
    Refuse 'a release is signed, and no certificate was given' 'Pass -Certificate <thumbprint>, or -Unsigned for a pre-release whose name says it is not signed.'
}
if ($Certificate -and $Unsigned) {
    Refuse '-Certificate and -Unsigned were both given' 'A release is one or the other.'
}
if ($Notes -and -not (Test-Path -LiteralPath $Notes -PathType Leaf)) {
    Refuse "the notes file $Notes does not exist"
}

if (-not (Get-Command 'gh' -ErrorAction SilentlyContinue)) {
    Refuse 'the GitHub CLI (gh) is not on PATH' 'winget install GitHub.cli, then gh auth login.'
}
if (-not (Test-Native gh auth status)) { Refuse 'the GitHub CLI is not signed in' 'gh auth login.' }

# The tree the archive is built from is the tree the tag names, or the tag is a lie about it.
$dirty = Read-Git status --porcelain
if ($null -eq $dirty) { Refuse 'git could not read the working tree' }
if ($dirty) {
    Refuse 'the working tree has changes, and a release is built from a commit' 'Commit or stash them first; git status lists them.'
}

$branch = Read-Git rev-parse --abbrev-ref HEAD
$upstream = Read-Git rev-parse --abbrev-ref '@{u}'
if (-not $upstream) {
    Refuse "the branch $branch has no upstream, so the commit a tag would name is on no remote" 'git push -u origin <branch> first.'
}
$remote = $upstream.Split('/')[0]

& git -C $root fetch --quiet --tags $remote
if ($LASTEXITCODE -ne 0) { Refuse "git could not fetch from $remote" }

$head = Read-Git rev-parse HEAD
if ($head -ne (Read-Git rev-parse '@{u}')) {
    Refuse "HEAD is not what $upstream has" "git push, or git pull, until git status says the branch is up to date."
}

$version = (& dotnet msbuild $project -nologo -getProperty:Version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { Refuse 'the version could not be read from the project' }
$tag = "v$version"

if (Read-Git rev-parse -q --verify "refs/tags/$tag") {
    Refuse "the tag $tag already exists" 'Raise <Version> in Directory.Build.props for a new release.'
}
if (Test-Native gh release view $tag --repo (Read-Git remote get-url $remote)) {
    Refuse "a GitHub release named $tag already exists" 'Raise <Version> in Directory.Build.props for a new release.'
}

Write-Host "publish  quickshell $version  ($tag on $($head.Substring(0, 9)), $upstream)" -ForegroundColor Cyan

# A process of its own, because release.ps1 ends with exit and its exit code is the verdict.
$build = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'release.ps1'))
if ($Certificate) { $build += @('-Certificate', $Certificate) }
if ($Timestamp) { $build += @('-Timestamp', $Timestamp) }
if ($Unsigned) { $build += '-Unsigned' }
if ($Ungated) { $build += '-Ungated' }
& powershell @build
if ($LASTEXITCODE -ne 0) { Refuse 'release.ps1 refused the build, so nothing was tagged or published' 'What it printed above says why.' }

$suffix = if ($Unsigned) { '-unsigned' } else { '' }
$zip = Join-Path $output "quickshell-$version-win-x64$suffix.zip"
$sums = Join-Path $output 'SHA256SUMS.txt'
foreach ($file in @($zip, $sums)) {
    if (-not (Test-Path -LiteralPath $file)) { Refuse "release.ps1 succeeded and $file is not there" }
}

# The build can write nothing git tracks; if it did, the archive is not of the commit about to be tagged.
if (Read-Git status --porcelain) {
    Refuse 'the build left changes in the working tree' 'git status lists them; .gitignore should cover what a build writes.'
}

if ($DryRun) {
    Write-Host '  DRY RUN    checked and built; nothing was tagged, pushed or uploaded' -ForegroundColor Yellow
    exit 0
}

& git -C $root tag -a $tag -m "quickshell $version" $head
if ($LASTEXITCODE -ne 0) { Refuse "git could not create the tag $tag" }
& git -C $root push --quiet $remote "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) {
    $null = Test-Native git -C $root tag -d $tag
    Refuse "the tag $tag could not be pushed to $remote, and was removed here again"
}
Write-Host "  tagged     $tag, pushed to $remote"

$create = @('release', 'create', $tag, $zip, $sums, '--verify-tag', '--title', "quickshell $version")
if ($Notes) { $create += @('--notes-file', $Notes) } else { $create += '--generate-notes' }
if ($Unsigned) { $create += '--prerelease' }
if ($Draft) { $create += '--draft' }

Push-Location $root
try { $url = (& gh @create | Out-String).Trim() }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    # The tag stays: it names the commit that was built, and a second run would refuse on it. Say how
    # to finish by hand rather than take back what is already public.
    Refuse "the tag $tag is pushed but the GitHub release was not created" "gh release create $tag `"$zip`" `"$sums`" --verify-tag --generate-notes"
}

Write-Host "  released   $url"
exit 0
