@echo off
rem QS77: the release archive installed on a desk that is not the operator's, looked at where Windows
rem looks, started from where it was installed, and uninstalled the way the list of installed apps
rem would - on the guest, because an install on this machine is an app in its owner's list.
rem
rem   run-install-vm.cmd
rem   run-install-vm.cmd -Archive artifacts\release\quickshell-0.1.0-win-x64-unsigned.zip
rem
rem Build the archive with release.cmd first. The log lands in TestResults\vm\install.log.
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install-vm.ps1" %*
exit /b %ERRORLEVEL%
