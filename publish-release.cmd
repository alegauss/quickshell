@echo off
rem QS371: a release published on GitHub. release.cmd builds the archive (QS77); this checks that the
rem tree is committed and pushed and that the version is new, builds it, tags the commit v<version>,
rem pushes the tag, and creates the GitHub release with the archive and SHA256SUMS.txt attached.
rem
rem   publish-release.cmd -Certificate <thumbprint>
rem   publish-release.cmd -Unsigned                    published as a pre-release
rem   publish-release.cmd -Unsigned -DryRun            every check and the build, nothing published
rem
rem Also -Ungated, -Timestamp <url> (as release.cmd), -Notes <file> and -Draft.
rem A new release is a new <Version> in Directory.Build.props.
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-release.ps1" %*
exit /b %ERRORLEVEL%
