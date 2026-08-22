---
name: roadmap-docs
description: quickshell's own shipping discipline — the one-task-one-commit rule and the `run-commit.cmd` that closes every task, design-before-ship, the blocks A–K that are reused rather than opened, the `## Done when` criteria a block is read against before it is called finished, and the user-facing surface a shipped feature must reach. The roadmap/changelog/rationale write path itself is NOT here: roadkeep owns it, and its own skill says which command to call. Use whenever a task is finished, before committing, when picking the next task, or when a governed file needs to change.
---

# Shipping discipline

**The write path is not in this file.** [`docs/ROADMAP.md`](../../../docs/ROADMAP.md),
[`docs/CHANGELOG.md`](../../../docs/CHANGELOG.md) and
[`docs/IMPROVEMENTS.md`](../../../docs/IMPROVEMENTS.md) are written by **roadkeep**, configured in
[`roadkeep.toml`](../../../roadkeep.toml): the fields are refused at insertion, the id and the
`(deps: … ✅)` annotation are derived, and a hand-edit is denied by the hook. Which command to call —
`add`, `ship`, `amend`, `status`, `pick`, `brief`, `section`, `non-goal`, `criterion` — is the
**`roadkeep` skill** at [`.claude/skills/roadkeep/SKILL.md`](../roadkeep/SKILL.md), which is the same
text in every project that adopted the tool. A rule stated in two files is a rule two files can
disagree about, so nothing here repeats it.

**How roadkeep is wired here, because it is not the plugin.** There is no plugin install in this
repo; the three surfaces were written from the sibling checkout at `..\roadkeep` —
[`.mcp.json`](../../../.mcp.json) (the `roadkeep` MCP server), the guard on `SessionStart`,
`PreToolUse` and `Stop` in [`.claude/settings.json`](../../settings.json), and the copied
`roadkeep` skill. Consequences worth knowing before you are surprised by one: the tools arrive as
`mcp__roadkeep__*` **only after the session picks up `.mcp.json`**, so in a session started before
the install, use the CLI — `python "..\roadkeep\scripts\roadkeep.py" <command>`, same engine, same
refusals; the package is not installed here and `roadkeep` is on no PATH. The guard is live either
way: it denies a hand-edit to a governed file and runs `lint` as the turn ends. And because the
surfaces are copies, `roadkeep install --check` is what keeps them in step with the checkout — it
exits 1 on anything that drifted, and `roadkeep install` closes it.

What this file holds is what roadkeep has no opinion about: **when** a commit happens, what a task
owes before it may ship, and what a shipped feature owes a user.

## What quickshell is, because every gate below is read against it

A **Windows desktop client for SSH, SCP and tunnels**, .NET / C#, built as a deliberately lean
competitor to MobaXterm: no X11 server, no games, no macro zoo. The three things it is judged on are
**speed, stability and a clean interface** — which means a task that adds a surface is spending the
budget the whole project exists to protect. *"MobaXterm has it"* is not a reason; it is the premise
this project was started to argue with. When the answer is no, that conclusion is a **non-goal**
(below), not a line nobody files.

Its users are **people at a terminal**, not adopting projects. So the surface gate below is about
what a person sees — a pane, a keybinding, a settings key, an error message — and not about an API.

## ⛔ One task, one commit (non-negotiable)

**You may NOT do more than one task before committing.**

- **One task → one `run-commit.cmd`.** The moment a task is complete and validated, run
  `roadkeep ship <id>` and commit — code and docs in that one commit — **before touching the next
  task.** Finishing a task means *the commit landed*.
- **A multi-task request** (a whole block, "execute Block C", a list of `QS<n>`s) **is not permission
  to batch.** It is a request to run tasks one at a time, committing after each. A single giant diff
  spanning many tasks is the failure this rule exists to prevent.
- **For any batch of ≥2 tasks, drive it with `/loop`** (self-paced): exactly one task per iteration,
  `run-commit.cmd` at the end of the iteration, then let the loop advance. Do not hand-roll a loop
  that defers commits.
- **Self-check before starting task N+1:** run `git status` / `git log -1`. If the previous task's
  work is not committed, stop and commit it first.
- `.\run-commit.cmd -m "<ascii conventional-commits title>" -- <path> ...` from the repo root.
  **`-m` always**, and ASCII.
  - **The leading `.\` is not decoration.** `run-commit.cmd` is also a name on this machine's
    `PATH` — a different script, which writes its own commit message and stages everything — and
    `NoDefaultCurrentDirectoryInExePath` is set here, so a bare `run-commit.cmd` finds *that* one
    and not the repository's.
  - **Pass the paths.** They are the scope the task's claim declared, and `roadkeep ship` prints
    them as a `git add --` line at the moment it releases the claim — copy that line's paths in.
    Then the commit contains this task and nothing else, which is the whole point of the rule
    above.
  - **Without them the whole tree is staged.** That is fine for one session in one checkout, and it
    is how a second session's half-written work once landed inside another task's commit, credited
    to the wrong id in a history nobody can correct afterwards. The script now lists what it is
    about to sweep before it does; read the list.
  - Either way a stray scratch file rides along, and in a .NET tree that means `bin/`, `obj/` and a
    `.user` file. **`.gitignore` earns its keep here:** if it does not yet cover the build output,
    that is the first thing to fix.
- **`roadkeep lint` must be clean for what you touched** before that commit. The `Stop` guard runs it
  for you, but the commit is yours to hold: never let a task add a finding.

## Design before ship

`IMPROVEMENTS.md` is not documentation written afterwards — it is the rationale the roadmap line
*points at*, and `ship` deletes it once the reasoning has done its job. Two things follow.

**Right now the backlog is empty and `lint` is clean — keep it that way from line one.** winwright
let its roadmap run 84 lines ahead of its design and carries 84 standing `ref.unresolved` findings
for it; that debt is cheaper to never open than to pay down. **A task's design section is written
before the task ships**, with `roadkeep section add <id> --title "…"` and the prose on stdin.

**`💭` is in the open set for exactly this.** Mark a line whose design is unwritten with
`roadkeep status <id> 💭`, so what is planned is told apart from what is merely listed. (`pick
--designed` cannot filter on it until `roadkeep.toml` declares `undesigned = ["💭"]` under
`[markers]`; declare it when the filtering is wanted, and not before it is.)

## A block is a theme, and a theme is reused

**Reuse a block. Do not open one per batch of work.** A block names a **capability of this client**,
and every task about that capability files under it, whenever it is found. Before `roadkeep add` the
question is *which theme is this*, never *which letter is next*.

| Block | Theme | What files under it |
|---|---|---|
| **A** | The connection — a session that stays up, or says why it did not | the SSH transport, handshake, algorithm negotiation, keepalive, reconnect, timeouts, the failure a user can read |
| **B** | Identity — keys, agents, and the host you think you reached | key formats and passphrases, the agent, password and keyboard-interactive, the known-hosts store and what a changed fingerprint does, where secrets rest on disk |
| **C** | The terminal — emulation that does not lie about the remote | the emulator, rendering and the font, input and paste, scrollback and search, encodings, resize and `$TERM` fidelity |
| **D** | Sessions — the tree a user organises work in | saved sessions and folders, defaults and inheritance, jump hosts and proxy-command chains, import and export of the session store |
| **E** | Files — SCP and SFTP as a thing a person operates | the browser, transfer and resume, overwrite and conflict, permissions and timestamps, progress and cancellation, drag-and-drop |
| **F** | Tunnels — a forward is a lifecycle, not a checkbox | local, remote and dynamic (SOCKS) forwards, binding and port conflicts, tunnels tied to a session's life, seeing what is currently forwarded |
| **G** | The shell around it — the clean interface, defended | window, tabs and split panes, themes, the settings surface, keybindings, what is on screen by default and what is not |
| **H** | Footprint — the reason to leave the incumbent | cold start, memory and idle CPU, the installer and update path, portability, what ships in the box |
| **I** | Diagnostics — an error a user can act on | logs and their level, the transport trace, redaction of secrets in output, the crash path, reproducing a report without telemetry |
| **J** | Leaving MobaXterm — the proof is the switch | importing its sessions and keys, the migration path, and the checklist naming what quickshell deliberately does not carry over |
| **K** | The build and the harness — what a green run is evidence of | the one command that builds and runs everything, the output tree, exit codes that mean what they say, the CI matrix, determinism of the build itself |

**Only `## Block A` is declared in the governed files today.** The table above is the intended map,
not the current state: `roadkeep block add B --title "…"` writes the heading into every governed file
at once — it is never hand-typed, and `ship` refuses with *"no heading declares Block B"* until it
exists. Declare a block the first time a task actually files under it, not all ten up front.

**A block empties; it does not close** — `pick --block E` answering *"nothing is open in Block E"*
means that theme is quiet today, not finished.

**A new letter is only for a theme the table has no row for**, and then it is named for the
**capability**, never for the batch that found it: *"Serial and local shells — the connections that
are not SSH"*, not *"what the Block C review turned up"*. **Add the row to the table above in the
same commit**, or the next task has nothing to reuse and the drift starts again.

## The other two lists are governed too

`## Done when — Block X` and `## Non-goals` in the roadmap are not prose you may edit. They are
`roadkeep criterion` and `roadkeep non-goal`, declared in `roadkeep.toml` by `[criteria]` and
`[non_goals]` being present at all — both are declared here — and the hook denies a hand-edit to
either.

**When a task is decided against, the conclusion lands as a non-goal** (`roadkeep non-goal add`), so
the same idea is not re-filed by the next person who has it. In this project that list carries an
unusual amount of weight: **every MobaXterm feature quickshell refuses is a non-goal**, and the
refusal has to be written down once so it is not re-argued monthly. `non-goal list` is a read to run
*before* proposing work, not after: the list binds what may be proposed, and nothing checks a
proposal against it for you.

### Criteria — what makes a block done, when the block will never be "empty"

A non-goal says what is *not* built; nothing said what would make a block **done**, so the only test
left was *a line count reaching zero* — and a block declared closed that way was reopened six times
in the project this discipline came from. A definition of done written into a rationale section is
one `ship` correctly deletes; this list is where it survives. For a client like this it is also where
the numbers live: *"cold start under 400 ms on the reference machine"* is a criterion, not a mood.

- **Two units, and the difference matters.** `--block X` is a claim about the capability and
  **outlives every line under it**. `--task <id>` is a claim about one line, lives under its own
  `## Done when — <id>` heading, and **leaves with the line**: `ship` and `retire` take the whole
  region in the same transaction. So a condition that stops mattering once the task lands goes on
  the task; one that still has to be true in a year goes on the block. Naming both addresses at once
  is refused.
- **Write the task's criteria at `add` time**, whenever "done" is anything more than the line's own
  sentence. `roadkeep brief <id>` prints the task's list *and* its block's, each with its address —
  which is the whole point: an agent that started the task through `brief` never has to ask what
  finishing means, and never invents its own answer.
- **The lead is the address, not a field.** `criterion amend <lead> --why "…"` rewrites the reason
  where it sits, because `add` appends and the order of the list is the shape of what finishing
  means — a drop-and-re-add moves a line and shows a reviewer a deletion. A changed *lead* is the
  one case that is genuinely `drop` plus `add`. The address is the **pair**, so the same lead under
  two blocks is two claims and not a duplicate.
- **`add` opens the `## Done when — …` heading** where there is none — but never the block: a label
  the roadmap does not declare is refused, so a typo opens nothing. The heading also survives the
  last bullet, since a block whose criteria all went is one somebody asked the question about.
- **`criterion list [--block X|--task <id>]` is never refused**, and it says *which* empty it found —
  ungoverned, unasked, or all dropped. Read it **before a block's last open line ships**: "the block
  is finished" is that reading, never a line count reaching zero. Write each `--why` so it names a
  run or a measurement that settles it, which is what makes the reading a check instead of a mood.
- **Presence, not enforcement.** roadkeep asserts the list exists and is well formed; whether the
  work *satisfies* a criterion is a judgement it has no model for. Nothing goes red when a criterion
  is untrue, which is exactly why the read-back before shipping is a rule here rather than a
  suggestion.

## `ship --why`, or live with it

`ship` copies the roadmap line's `why` into the ledger by default — and that line states a
**problem**, because that is what a roadmap line is for. A ledger entry states an **outcome**: what
now works. `--why` is the only chance to say so. **`amend` refuses a shipped id** ("it is already in
the changelog"), so write it at ship time:

```
roadkeep ship QS1 --why "A dropped connection reconnects on the session's own settings and says which attempt is running, so a flaky link no longer costs the scrollback."
```

**There is one door back, and it is not a second draft.** `record amend <id> --why "…"` rewrites an
entry's sentence where it stands — not `drop` plus `add`, which would move the line to the end of
its block and show a reviewer a deletion where a word changed. Use it for a sentence that is *wrong
about the repository*, not for one you would now phrase better.

## A task that leaves without shipping

**`retire` works in this project** — `[markers] retired = "🗑"` is declared and no `[ledger] marker =
false` suppresses it, so a 🗑 entry can be told from a ✅ one. `roadkeep retire <id> --why "…"`, and
the sentence carries the whole burden of not lying:

- **Open with the decision, not with work.** *"Measured before deciding, and the premise did not
  survive: …"* — never a sentence that reads as something built.
- **Give the evidence that settled it**, in numbers where there are numbers. A decision with no
  measurement behind it is an opinion that has taken an id.
- **Say where the conclusion now lives** — usually the non-goal you filed with it.

## The user-facing surface gate

quickshell's users are people driving a terminal, so a shipped feature owes them a way to find it.
**Every time a task ships, run this decision:**

1. **Would a user do something differently because this shipped?** A pane or a menu item, a
   keybinding, a settings key, a session-file field, a refusal or an error they can now hit, a
   default that changed. If **no** — an internal refactor, a build change, a dev-only flag — it gets
   **no** README or docs change. Say so in the commit message and stop. Don't invent thin docs for
   internal work.
2. **If yes, hit the surfaces that exist:** the README's feature list, the settings/keybindings
   reference, and the in-app text itself — the label, the tooltip, the error string. **The error
   string is documentation** in a client like this: it is what a user reads at the moment the thing
   fails, and it is read far more often than the README.
3. **A new setting is a surface with a cost.** Prefer a good default over a checkbox; if the checkbox
   is genuinely needed, it is named for the behaviour and it appears in the reference. An option
   nobody documented is an option nobody finds.
4. **Write for the user, not the commit.** Never paste `IMPROVEMENTS.md` rationale verbatim: that
   file argues *why*, a reference says *what it does and how to use it*.
5. These surfaces are in the same repo, so they belong in the **same commit** as the task.

## Prove it by running

A terminal client is judged on behaviour under a real connection, so its own tasks are held to that:

- **A claim is proven by a run, against a real endpoint** — a live sshd, a container, a jump host,
  a port that is genuinely in use — and not by a unit test asserting the shape of a request. Tests
  are still written; they are not the evidence that the feature works.
- **A UI task is not done without the picture.** Capture the window and say what it shows. The `/run`
  skill is the way to launch the app for it.
- **A footprint claim is a number or it is nothing.** "Faster" and "lighter" are Block H's whole
  reason to exist; measure cold start and memory the same way each time and say which machine.
- **Never report a pass that skipped a check.** A summary saying it works while the reconnect path
  or the tunnel teardown was never exercised is worse than a red run. If something could not be
  exercised here, that goes in the report — named — and not in an info line.

## Release notes

A release is not a task. Cutting one is a `chore: release vX.Y.Z` commit of its own, never bundled
with a task.
