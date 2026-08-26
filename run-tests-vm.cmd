@echo off
rem QS95: the same suite as run-tests.cmd, on a desk that is not the operator's.
rem
rem The render tests put real topmost windows on screen, because DXGI advances no frame statistics
rem for a window nobody can see. For the twenty-five seconds a run lasts the screen is theirs - and
rem worse, a window the operator drags across one makes DXGI answer DXGI_STATUS_OCCLUDED and the
rem frame-queue measurement reads nonsense. This hands the run to a VMware guest so the host stays
rem usable and the measurement stays honest.
rem
rem It arranges nothing. Credentials come from a file outside this tree - tools\run-tests-vm.ps1
rem says which, and says why the out-of-tree spelling is the default.
setlocal

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\run-tests-vm.ps1" -Configuration %CONFIG% %2 %3 %4
exit /b %ERRORLEVEL%
