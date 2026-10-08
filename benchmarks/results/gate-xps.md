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
| 2026-10-07 19:54 | 40fb448+ | 988 | 40.8 | 608 | worse: emulate - the first run with the arms on the published client (QS200), straight after two publishes; replayed by hand right after, the old harness build and the published bytes both read 50 to 53 |
| 2026-10-07 19:58 | 40fb448+ | 820 | 14.3 | 607 | worse: parse, emulate - against a client published with `-p:TieredCompilation=false` on purpose to prove QS200, never committed; the old harness build read 48 to 54 for emulate at the same time |
| 2026-10-07 20:00 | 40fb448+ | 1003 | 44.6 | 602 | baseline - discarded, never committed: the VM guest was running, and emulate's allowance came out at 52.6 % |
| 2026-10-07 20:03 | 40fb448+ | 1041 | 36.6 | 489 | baseline - discarded, never committed: emulate's allowance came out at 33.9 % while hand replays read 54 to 58 |
| 2026-10-07 20:08 | 40fb448+ | 1155 | 53.6 | 485 | baseline |
| 2026-10-07 20:08 | 40fb448+ | 1152 | 55.5 | 479 | held |
