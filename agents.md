# Working in this repository

## Where a finding goes

A defect, an improvement or an idea belongs in the roadmap of **the project it is about**,
which is not always this one.

- **quickshell** — this client's own defects and work. `roadkeep add` here, prefix `QS`.
- **winwright** — the UI-case engine at `D:\Git\alegauss\winwright`, which this repository
  adopts for its accessibility-tree cases (QS147). Anything found by using it — a verb it
  cannot spell, an expectation it cannot express, a refusal that reads wrong — goes in
  **its** roadmap, prefix `WW`, not in a comment here and not in a `QS` line describing a
  workaround. Filed from this checkout with
  `python "..\roadkeep\scripts\roadkeep.py" -C D:\Git\alegauss\winwright add …` or by
  running the CLI from that directory.
- **roadkeep** — the governance tool at `D:\Git\alegauss\roadkeep`, prefix `RK`. Same rule:
  friction using it is filed there.

**Never commit a sibling repository.** They routinely carry another session's uncommitted
work — winwright had twelve modified files, including the two this repository's findings
were about. Write the roadmap line and leave the tree alone; say in the report that the
line was filed and not committed.

Write the line before working around the problem. A workaround with no line beside it is a
finding that leaves with the session that had it.

## What is governed here

`docs/ROADMAP.md`, `docs/CHANGELOG.md`, `docs/IMPROVEMENTS.md` and `docs/DECISIONS.md` are
written by roadkeep and a hand-edit is refused by a hook. The `roadkeep` skill says which
command to call; `.claude/skills/roadmap-docs/SKILL.md` says when a task may ship and what
a commit owes.

`docs/PERFORMANCE.md` is not governed and is not therefore free: each figure is a design
constraint, changed only by a commit that argues for the change.

## Evidence

A claim is proven by a run. For UI that means the accessibility tree rather than a
screenshot — a screenshot says a window was there, not what it contained, and a capture on
this machine needs foreground granted by a desk somebody is at, which refused twenty-five
times running while unattended. A picture is still worth taking for a human to look at. It
should not be the evidence.

**The desk to take it on is the guest, never this one.** `run-app-vm.cmd` starts the client
in the VM, lets it draw, and brings back `TestResults\vm\app-desk.png`; `-Settings` places a
settings file first, which is how a scheme or a font is looked at. `run-tests-vm.cmd` runs
the suite there. Neither touches the operator's screen — driving this desktop with synthetic
input steals the foreground from the person the work is for, and is not a fallback when the
guest is inconvenient.
