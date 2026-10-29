@echo off
rem QS374: suspend every VM running on this host, so a measurement here is of this desk alone.
rem
rem The VM runners leave the guest up when they finish, and run-perf-gate.cmd and release.cmd refuse
rem while one is running, because a sitting beside it comes out too noisy to judge. This goes through
rem tools\vm-guest.ps1 for the encryption password a bare `vmrun suspend` would be refused without,
rem and waits for a run that holds the guest desk rather than suspending under it.
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\suspend-vm.ps1"
exit /b %ERRORLEVEL%
