# Performance gate - xps

Every judged run of the gate on this machine, oldest first: `run-perf-gate.cmd` by hand, and
`release.cmd` before it makes an archive. The figures are that run's medians; a baseline row
is where the thresholds in `benchmarks/gate/xps.json` were taken. A commit marked `+`
was measured with uncommitted changes on top of it, the gate's own files aside.

| when | commit | parse MB/s | emulate MB/s | start ms | verdict |
|---|---|---|---|---|---|
| 2026-09-10 21:01 | 02fac99+ | 1137 | 54.5 | 627 | baseline |
| 2026-09-10 21:02 | 02fac99+ | 1134 | 55.5 | 632 | held |
| 2026-09-10 21:04 | 02fac99+ | 1150 | 52.7 | 636 | held - with a 0.2 ms wait put into `Emulator.Feed` on purpose, never committed: inside emulate's 6.4 % |
| 2026-09-10 21:06 | 02fac99+ | 1135 | 30.7 | 633 | worse: emulate - with a 1 ms wait put into `Emulator.Feed` on purpose to prove the gate, never committed |
| 2026-09-10 21:37 | 02fac99+ | 1170 | 55.1 | 644 | held |
