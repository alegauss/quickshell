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
| 2026-10-10 18:38 | d972ff7+ | 992 | 46.2 | 558 | worse: parse, start - the VM guest was running |
| 2026-10-10 18:40 | d972ff7+ | 1005 | 45.3 | 542 | worse: parse, start - guest suspended; another session's pytest held one core |
| 2026-10-10 18:46 | 40fb448 | 967 | 48.6 | 582 | worse: emulate - the baseline's own commit, checked out in a worktree and judged against the baseline it carried (02fac99's): the same code reads as slow as d972ff7 today, so the 13 % is the desk's and not the code's (QS253) |
| 2026-10-10 20:58 | 2ac10cb+ | 758 | 36.5 | 670 | held - beside 40fb448, the first side-by-side check (QS253); its allowances came out at 42, 62 and 40 % with other projects running, which is why the gate now judges nothing above 10 % |
| 2026-10-10 21:01 | 2ac10cb+ | 858 | 40.5 | 682 | not judged, too noisy: parse, emulate, start - beside 40fb448 |
| 2026-10-13 12:37 | 2ddca86+ | 845 | 141.8 | 1315 | worse: start - beside 40fb448 |
