# Soak — the harness, validated

Figure 6 of the budget asks that resident memory stay **flat** across a seventy-two-hour soak, and
Block H's criterion repeats it. `tools/Quickshell.Soak` is the arrangement the design asks for:
twenty sessions in six roles against the docker fixture — idle, printing continuously, opening and
closing on a loop, a forward carrying traffic, dropped and reconnected on a timer, and a full-screen
program redrawing on its own clock.

**The seventy-two-hour run has not been done.** What is recorded here is the harness and the runs
that validated it, on 2026-08-31, on the reference machine. The long run is the remainder on QS78.

```
prototypes\SshProbe\fixture\up.sh
dotnet build Quickshell.sln
tools\Quickshell.Soak\bin\Debug\net10.0-windows\Quickshell.Soak.exe --hours 72 --sessions 20
```

## What the validation runs established

**Every role connects and does its work.** A three-minute run with eight sessions: 15 connections,
0 failures, six roles represented, 0.16 GB of host output parsed.

**The scrollback ring is the counter-example and it behaves.** It reached its configured 2,000 lines
and stayed there — watched with a tolerance of zero, so a ring that kept growing would be the one
failure that row can report.

**Flat is a computed verdict, not an eyeball.** Each counter carries a least-squares slope,
extrapolated over twenty-one days because that is ordinary uptime for a terminal client. A counter
under a limit while rising is a leak that reaches the limit in three weeks, and a bound cannot tell
those apart.

## Three flaws in the harness, found by running it

Each of these was in the first version and is fixed:

1. **It reported a leak on every short run.** Three minutes of a parser reaching full speed fitted a
   slope of 35,523 MB/h, which extrapolated to 17,903,676 MB and printed **RISING**. Arithmetically
   correct, and evidence of nothing. A verdict now needs two hours of span and sixty samples; below
   that the numbers print and the verdict column says it is too short.
2. **It soaked happily against a dead fixture.** One run reported "824 failures swallowed" beside
   memory figures for a process that never connected — the containers had stopped. The endpoint is
   now probed before any session starts, and a run with zero connections says so before anything
   else.
3. **A failure count with no reason attached.** 824 failures and no clue why. Each role now records
   what its last failure said, and the report carries it.

## What it found immediately — QS139

The first working run produced a defect the seventy-two-hour run was supposed to find:

| one printing session, ~3 min | parser on | parser off (`--no-parse`) |
|---|---|---|
| moved | 153 MB | 18,176 MB |
| managed heap after a forced full collection | 2,055 MB | 8.9 MB |
| private memory | 3,676 MB | 41.6 MB |

**This was first read as the emulator retaining thirteen bytes per byte parsed, and that reading was
wrong.** `ParseRetentionTests` feeds one emulator 64 MB of `cat-log` headlessly and finds under 8 MB
held after a blocking compacting gen2 collection — the emulator retains nothing.

What the numbers actually say: the host sent roughly two gigabytes while the parser consumed 153 MB,
and the difference sat in the channel. Bypassing the parser made the *consumer* fast, so nothing
accumulated — which is exactly why the experiment implicated the wrong half. It is a flow-control
defect, and QS139 now says so.

Worth recording as a method note: an A/B that changes one call can still change two things. Removing
`Feed` removed the retention **and** removed the slowness that caused it, and the conclusion followed
the change rather than the cause. The headless test is what separated them.

## Also worth knowing

The fixture's containers **stop on their own**, repeatedly, always `Exited (0)` — a graceful stop,
most likely Docker Desktop's resource saver. That is fatal to a three-day run and is why the
endpoint probe above exists. QS136 covers the same problem making the test suite print "Passed"
while skipping a hundred tests.

GPU memory and atlas occupancy are **not** watched: this harness holds no graphics device, because
nothing above the seam attaches a pane to a session yet. The design names atlas memory specifically,
and it is part of QS78's remainder rather than something quietly dropped.
