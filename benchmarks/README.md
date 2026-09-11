# benchmarks

The corpus and the two harnesses that replay it. What each stream is and how it was captured is
[corpus/MANIFEST.md](corpus/MANIFEST.md); the figures they are read against are
[docs/PERFORMANCE.md](../docs/PERFORMANCE.md).

```
dotnet run --project benchmarks/Quickshell.Replay -c Release
dotnet run --project benchmarks/Quickshell.Benchmarks -c Release -- --filter "*"
```

- **`Quickshell.Replay`** — whole-stream replay. Feeds each captured stream through every consumer
  that exists in 64 KB chunks, best of five after a warmup, and writes
  `results/replay-<machine>.md`. It is not BenchmarkDotNet on purpose: a one-shot stream of this
  size does not fit that iteration model.
- **`Quickshell.Benchmarks`** — the microbenchmarks, BenchmarkDotNet, over the same corpus and
  never over generated input. `[MemoryDiagnoser]` is on: allocation is a result and not a footnote,
  because a run that got faster while allocating more has borrowed from a collection that will
  happen during somebody's `vim` session.

**Results are committed** so two runs months apart on the same machine are comparable.

**The performance gate runs this harness itself and does not read those files** (QS79).
`run-perf-gate.cmd` replays `cat-log` through `parse` and `emulate` with
`--only cat-log:parse,cat-log:emulate --json <file>`, which writes what a program can read and leaves
`results/replay-<machine>.md` alone. It judges the figures against its own baseline,
`gate/<machine>.json`, and records each run in `results/gate-<machine>.md`; a new replay results file
moves neither. See [PERFORMANCE.md](../docs/PERFORMANCE.md#how-a-regression-is-caught). CI does not
run it: a hosted runner is a different machine every time, and a figure only means something against
the desk it was taken on.

## What is measured today

Six consumers, in order of how much of a terminal each one is, so consecutive arms differ by one
stage: `escape-scan` (the floor, which no parser can beat), `parse` (the state machine alone),
`decode`, `segment`, `emulate` (the real `Emulator`, which is what a session costs) and `render` (the
glyph path). What each one adds, and how to read the gap between them, is written under *Reading these
numbers* in `results/replay-<machine>.md`, beside the figures it explains.
