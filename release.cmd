@echo off
rem QS77: what a release ships. The client published self-contained and ReadyToRun, signed, zipped as
rem one portable copy that can also install itself, and SHA256SUMS.txt beside it.
rem
rem   release.cmd -Certificate <thumbprint>
rem   release.cmd -Unsigned
rem
rem A release is signed, so without a certificate it refuses unless -Unsigned is given - and then the
rem archive is named ...-unsigned.zip, which is how an unsigned build cannot pass for a release.
rem
rem The archive and its checksum land in artifacts\release\.
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" %*
exit /b %ERRORLEVEL%
