@echo off
setlocal

rem One task, one commit. This stages everything and commits it, so `git status` is the
rem read that comes first: a stray scratch file rides along otherwise.
rem
rem Usage: run-commit.cmd -m "<ascii conventional-commits title>"

if /I not "%~1"=="-m" goto :usage
if "%~2"=="" goto :usage

git add -A
if errorlevel 1 exit /b 1

git commit -m "%~2"
if errorlevel 1 exit /b 1

git --no-pager log -1 --stat
exit /b 0

:usage
echo Usage: run-commit.cmd -m "<ascii conventional-commits title>"
exit /b 2
