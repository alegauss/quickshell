@echo off
rem QS79: a performance regression fails here instead of reaching a release.
rem
rem   run-perf-gate.cmd                check this tree against this machine's baseline
rem   run-perf-gate.cmd --baseline     take this machine's baseline, then commit what it writes
rem   run-perf-gate.cmd --client <exe> time that client's start instead of publishing one
rem
rem It builds the replay and startup harnesses in Release, publishes the client the way a release
rem does, and measures parse and emulate throughput and warm start. Each figure fails when it is worse
rem than the baseline by more than the baseline's own noise, unless a commit since then says it was
rem meant with a trailer:  Performance-Moved: <figure> - <what it bought>
rem
rem Exit 0 held, 1 worse, 2 worse but meant (take a new baseline before the next release), 3 not
rem judged. Every judged run is a row in benchmarks\results\gate-<machine>.md. It starts the client
rem again and again, so it is run on the machine whose figures these are.
setlocal

rem Built as its own step: a gate that did not compile has judged nothing, and read through
rem `dotnet run` its failure would come back as the exit code that means a figure got worse.
dotnet build "%~dp0tools\Quickshell.Gate\Quickshell.Gate.csproj" -c Release --nologo -v quiet
if errorlevel 1 (
    echo run-perf-gate: the gate itself did not build, so nothing was judged.
    exit /b 3
)

"%~dp0tools\Quickshell.Gate\bin\Release\net10.0-windows\Quickshell.Gate.exe" %*
exit /b %ERRORLEVEL%
