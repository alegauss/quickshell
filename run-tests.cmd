@echo off
setlocal enabledelayedexpansion

rem The one command whose exit code is the suite's verdict.
rem
rem It is a script and not `dotnet test` because that command does not answer honestly here: on
rem this tree it reports "zero tests ran" and exits 5 while every test assembly, run directly,
rem discovers and passes its tests and exits 0. A command that reports a failure the tree does not
rem have is worse than no command, because the first thing it teaches is to stop reading it.
rem
rem Usage:  run-tests.cmd [Configuration]        default Debug
rem CI runs it as:  run-tests.cmd Release

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

pushd "%~dp0"

echo Building Quickshell.sln (%CONFIG%)
dotnet build Quickshell.sln --configuration %CONFIG% --nologo -v quiet
if errorlevel 1 (
    echo.
    echo BUILD FAILED - no tests were run.
    popd
    exit /b 1
)

set /a RAN=0
set /a FAILED=0
set "BROKEN="

for /d %%P in (tests\*) do (
    set "APP=%%P\bin\%CONFIG%\net10.0-windows\%%~nxP.exe"
    if exist "!APP!" (
        echo.
        echo === %%~nxP ===
        "!APP!"
        if errorlevel 1 (
            set /a FAILED+=1
            set "BROKEN=!BROKEN! %%~nxP"
        )
        set /a RAN+=1
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

if %FAILED%==0 (
    echo All %RAN% test assemblies passed.
    popd
    exit /b 0
)

echo %FAILED% of %RAN% test assemblies failed:!BROKEN!
popd
exit /b 1
