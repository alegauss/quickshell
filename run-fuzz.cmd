@echo off
rem QS102: coverage-guided fuzzing of the parser and the model, for as long as it is given.
rem
rem   run-fuzz.cmd                      ten minutes, one job
rem   run-fuzz.cmd -Seconds 3600 -Jobs 4
rem
rem The suite's own mutator runs a fixed list on every build; this searches. It instruments
rem Quickshell.Terminal.dll with SharpFuzz and drives it with libFuzzer, whose .NET driver is fetched
rem from its author's release and run only if its SHA-256 is the one pinned in tools\fuzz.ps1. The
rem corpus grows in artifacts\fuzz\corpus across runs; a crash lands in artifacts\fuzz\findings as the
rem input that caused it, and becomes a shape in HostileInputTests.
rem
rem Exit 0 nothing found, 1 a crash was found, anything else the campaign did not run.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\fuzz.ps1" %*
exit /b %ERRORLEVEL%
