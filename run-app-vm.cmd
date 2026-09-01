@echo off
rem QS173: the client itself on a desk that is not the operator's, and a picture of what it showed.
rem
rem run-tests-vm.cmd moved the suite off this machine. It moved nothing else, so every task that
rem changed what a window looks like still ended with a screenshot taken here - stealing the
rem foreground, moving the operator's windows, typing into whatever had focus. This starts the
rem client on the guest instead, lets it draw, photographs the screen and stops it again.
rem
rem   run-app-vm.cmd
rem   run-app-vm.cmd Debug -Arguments "--tabs 3 --panes 2"
rem   run-app-vm.cmd Debug -Settings d:\tmp\look.json
rem   run-app-vm.cmd Debug -Keep
rem
rem The picture lands in TestResults\vm\app-desk.png.
rem
rem It arranges nothing. Credentials come from a file outside this tree - tools\vm-guest.ps1 says
rem which, and says why the out-of-tree spelling is the default.
setlocal

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug
shift

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\run-app-vm.ps1" -Configuration %CONFIG% %1 %2 %3 %4 %5 %6
exit /b %ERRORLEVEL%
