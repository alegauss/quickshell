@echo off
setlocal EnableDelayedExpansion

rem One task, one commit.
rem
rem   run-commit.cmd -m "<ascii conventional-commits title>" [-- <path> ...]
rem
rem With paths, exactly those are staged: that is the scope the task's claim declared, and
rem `roadkeep ship` prints it as a `git add --` line at the moment it releases the claim.
rem Pass it. Without paths the whole tree is staged, which is fine for one session in one
rem checkout and is how a second session's half-written work once landed inside another
rem task's commit - so the sweep now says, first, exactly what it is about to take.

if /I not "%~1"=="-m" goto :usage
if "%~2"=="" goto :usage

set "MESSAGE=%~2"
shift
shift

set "PATHSPEC="
if "%~1"=="--" (
    shift
    goto :collect
)
if not "%~1"=="" goto :usage
goto :stage

:collect
if "%~1"=="" goto :stage
set PATHSPEC=!PATHSPEC! "%~1"
shift
goto :collect

:stage
if defined PATHSPEC (
    echo staging the paths this commit declares:
    for %%p in (%PATHSPEC%) do echo    %%~p
    git add -- %PATHSPEC%
) else (
    echo no scope declared, so the whole tree is going in:
    git --no-pager status --short
    echo.
    git add -A
)
if errorlevel 1 exit /b 1

git --no-pager diff --cached --quiet
if not errorlevel 1 (
    echo nothing staged: the paths matched no change in this tree
    exit /b 3
)

git commit -m "%MESSAGE%"
if errorlevel 1 exit /b 1

git --no-pager log -1 --stat
exit /b 0

:usage
echo Usage: run-commit.cmd -m "ascii conventional-commits title" [-- path ...]
echo.
echo   -m       the commit title, always, and ASCII
echo   -- ...   the paths this task owns; roadkeep ship prints them as a git add -- line
echo.
echo Without paths the whole tree is staged, and listed before it is.
echo Invoke it as .\run-commit.cmd: the bare name resolves elsewhere on this machine.
exit /b 2
