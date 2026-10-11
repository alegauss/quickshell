@echo off
setlocal

rem Builds the client and starts it on this desk, for a person who wants to use it.
rem
rem   run.cmd                              Debug, the build run-tests.cmd makes
rem   run.cmd Release                      the configuration CI tests
rem   run.cmd Release --session prod       anything after the configuration goes to quickshell.exe
rem   run.cmd --tabs 3                     ... and with no configuration named, all of it does
rem
rem Only the client is built, so a change is seen in seconds rather than after the whole solution.
rem The window opens and this returns: the client is a windowed program and nothing here waits on
rem it. Settings and sessions are the ones %AppData% holds, as for an installed copy; set
rem QUICKSHELL_DATA to a folder first to try something without touching them.
rem
rem This is the desk somebody is sitting at. Evidence for a change is a UI case in the guest
rem (run-tests-vm.cmd), and a picture of it is run-app-vm.cmd - neither takes this screen.

set "CONFIG=Debug"
if /i "%~1"=="Debug" (set "CONFIG=Debug" & shift)
if /i "%~1"=="Release" (set "CONFIG=Release" & shift)

rem What is left after the configuration, passed on as it was typed. %* would still hold the
rem configuration, because shift does not change it.
set "ARGS="
:collect
if "%~1"=="" goto built
set ARGS=%ARGS% %1
shift
goto collect

:built
echo Building the client (%CONFIG%)
dotnet build "%~dp0src\Quickshell.App\Quickshell.App.csproj" --configuration %CONFIG% --nologo -v quiet
if errorlevel 1 (
    echo run: the client did not build, so nothing was started 1>&2
    exit /b 1
)

set "EXE=%~dp0src\Quickshell.App\bin\%CONFIG%\net10.0-windows\win-x64\quickshell.exe"
if not exist "%EXE%" (
    echo run: the build said it succeeded and %EXE% is not there 1>&2
    exit /b 2
)

echo Starting %EXE%%ARGS%
start "" "%EXE%"%ARGS%
exit /b 0
