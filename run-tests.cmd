@echo off
setlocal enabledelayedexpansion

rem The one command whose exit code is the suite's verdict.
rem
rem It was written because `dotnet test` did not answer honestly here: it reported "zero tests
rem ran" and exited 5 while every assembly, run directly, passed. Since QS89 it does answer -
rem `dotnet test --solution Quickshell.sln` runs the same 1307 tests and exits 2 on a broken one
rem (SDK 10.0.401). This stays the suite for what follows: the TRX per assembly, the names of a
rem red run's tests, and the same command on the guest.
rem
rem QS175: every assembly writes a TRX beside the run, and a red run prints the names out of it.
rem A run that fails once in eight and leaves only a count teaches the reader to rerun rather than
rem to look, and the console it named the test on is gone the moment anybody pipes this anywhere.
rem
rem Usage:  run-tests.cmd [Configuration]        default Debug
rem CI runs it as:  run-tests.cmd Release

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

rem QS230: how long one test may run before it is taken as hung, ended, and named - the one place
rem this is set, beside the skip budget the end of the run is held to. A hung test used to hold a
rem guest run for over an hour with nothing on screen. Ten minutes is far past the slowest test this
rem suite has; QUICKSHELL_HANG overrides it for a run that means to show the timeout working.
if "%QUICKSHELL_HANG%"=="" set "QUICKSHELL_HANG=10m"

pushd "%~dp0"

set "REPORTS=%CD%\TestResults\reports"

rem Wiped first. A run that dies before writing one leaves the last run's report in place, and the
rem summary would then name a test that passed today - which is worse than naming none, because it
rem looks like an answer.
if exist "%REPORTS%" rd /s /q "%REPORTS%"
mkdir "%REPORTS%" 2>nul

echo Building Quickshell.sln (%CONFIG%)
rem Without node reuse, so MSBuild's workers leave with the build instead of idling for fifteen
rem minutes beside the tests (QS108).
dotnet build Quickshell.sln --configuration %CONFIG% --nologo -v quiet -nodeReuse:false
if errorlevel 1 (
    echo.
    echo BUILD FAILED - no tests were run.
    popd
    exit /b 1
)

rem And the compiler server stopped before anything is timed. A build leaves VBCSCompiler resident,
rem measured at 1.1 GB and two thousand CPU-seconds, and the suite's wall-clock latency tests were
rem failing only on the runs that followed one: the build was what they measured (QS108).
dotnet build-server shutdown >nul 2>&1

set /a RAN=0
set /a FAILED=0
set "BROKEN="

for /d %%P in (tests\*) do (
    set "APP=%%P\bin\%CONFIG%\net10.0-windows\%%~nxP.exe"
    if exist "!APP!" (
        echo.
        echo === %%~nxP ===
        rem A mini dump, because a full one is gigabytes and the point is the name of the test,
        rem which the platform prints when it ends the process.
        "!APP!" --results-directory "%REPORTS%" --report-xunit-trx --report-xunit-trx-filename %%~nxP.trx --hangdump --hangdump-timeout %QUICKSHELL_HANG% --hangdump-type Mini
        if errorlevel 1 (
            set /a FAILED+=1
            set "BROKEN=!BROKEN! %%~nxP"
        )
        set /a RAN=RAN+1
    ) else (
        echo.
        echo === %%~nxP ===
        echo   no test application at !APP!
        set /a FAILED+=1
        set "BROKEN=!BROKEN! %%~nxP^(missing^)"
    )
)

echo.

rem Nothing found is not a pass. A suite that silently shrinks to zero is the exact failure this
rem script exists to make impossible.
if %RAN%==0 (
    echo NO TEST APPLICATIONS FOUND under tests\ for configuration %CONFIG%.
    popd
    exit /b 1
)

rem QS136: how much ran, always, and the skips held to the budget beside the tests. A run that
rem skipped a hundred tests used to print the same line as one that skipped none, and a green that
rem proves nothing is worse than a red, because nobody investigates it. The counts are printed on
rem every run; a skip past tests\skips.json fails it, unless it is the SSH fixture's on a desk that
rem declares it has none (QUICKSHELL_NO_FIXTURE), which is then printed as waived.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\count-the-run.ps1" -From "%REPORTS%" -Budget "%~dp0tests\skips.json"
set "OVER=%ERRORLEVEL%"
echo.

if %FAILED%==0 if %OVER%==0 (
    echo All %RAN% test assemblies passed.
    popd
    exit /b 0
)

if not %OVER%==0 echo SKIPPED PAST THE BUDGET - more tests did not run than tests\skips.json allows.

if %FAILED%==0 (
    popd
    exit /b 1
)

echo %FAILED% of %RAN% test assemblies failed:!BROKEN!
echo.

rem Which tests, out of the reports, and last, so the end of the output is the answer. A reporter
rem and never a verdict - the exit code below is the suite's.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\name-the-red.ps1" -From "%REPORTS%"

popd
exit /b 1
