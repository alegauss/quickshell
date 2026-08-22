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

**Results are committed** so two runs months apart on the same machine are comparable. **CI does
not gate on them**, deliberately: a measurement has to be trusted before it is allowed to fail a
build, and that is a later line.

## What is measured today, and what is not

Each stream is meant to be replayed **twice** — headless, which measures the parser alone, and
through the whole pipeline with a renderer, which measures what coalescing saves. The gap between
those two is the most informative figure this project will produce.

**Neither consumer exists yet.** There is no parser and no renderer, so `IStreamConsumer` has one
implementation: `escape-scan`, which touches every byte and counts `ESC`. That is not either arm —
it is the **floor**, and its value is that it is a ceiling. A parser at 300 MB/s on a stream whose
floor is 1,500 MB/s has spent four fifths of the budget on itself, and without this number nobody
could say so. The results file names the empty arms rather than reporting one number as if it were
the pair.
