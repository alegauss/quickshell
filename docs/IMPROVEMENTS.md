# Improvements

## Block A — A session that stays up, or says why it did not

### §QS40 The servers that are not OpenSSH on Linux

OpenSSH on Linux is the easy case and the one every client passes. This matrix is
deliberately weighted towards the others.

**OpenSSH 7.x through 9.x**, which spans the deprecation of `ssh-rsa` with SHA-1 and the
arrival of newer key exchanges. A client that has only ever met the newest fails against
the oldest in a way that reads to the user as a broken server.

**Dropbear**, which is what an embedded device runs, offering a much smaller algorithm
set.

**A network appliance**, Cisco IOS or similar: old algorithms, a shell that is not a
shell, and terminal behaviour written decades ago. This is the case that most reliably
exposes an emulator, so the terminal is exercised here and not only the transport.

**Windows OpenSSH**, where the far side is `cmd` or PowerShell and the line-ending and
console behaviour are entirely different from a Unix host's.

**A commercial server**, and **a container image**, so the whole matrix is reproducible
by somebody who owns none of this hardware.

Each entry records what was connected to, which algorithms were negotiated, and what did
not work. An entry saying only that it worked has not been tested; it has been visited.

Falsified when the matrix records a pass with no negotiated algorithm list beside it.

## Block B — Keys, agents, and the host you think you reached

### §QS43 Two agents, one protocol, and the key that never leaves

An agent holds the private key and performs signatures on request, so the client never
sees the key. That is what makes it more than convenience: for a key on a smart card or
a hardware token the agent is the *only* possible route, because the key is not
extractable at all.

Two agents to reach on Windows. The Windows OpenSSH agent listens on a named pipe.
Pageant, which PuTTY and MobaXterm users already run, uses a shared-memory protocol and,
in recent versions, a named pipe as well. Both speak the same request format above the
transport, so only the transport differs between them.

Two operations are needed: list identities, and sign with a chosen one. The rest of the
agent protocol is out of scope here and stays out.

Ordering: agent identities are tried before file-based keys, since an agent key needs no
prompt and a file key may. A user with ten identities in an agent will exceed a server's
authentication attempt limit, so identities are filtered by what the session names
wherever it names one.

If the library cannot do this — and the gap analysis will already have said —
implementing the agent client directly is the fallback, the protocol being small enough
to be worth it.

Falsified when a token-backed key cannot authenticate a session.

### §QS344 QS344 The Windows Hello gate, run for real

QS306 gates the vault export on Windows Hello through `WindowsHello.VerifyAsync`, which
calls UserConsentVerifier by its COM interop interface (`IUserConsentVerifierInterop`)
rather than a WinRT projection. Every test answers the gate through
`MainWindow.Verifies`, so the interop itself, the activation factory, the parameterised
IID of the operation and the polled status, has never run against a real prompt. What is
proven is only the IID derivation, against two identifiers Windows publishes.

The guest has no Windows Hello: its account is local and has no PIN. A PIN can be set
for a local account without a TPM, by policy, and a provisioning script beside the
others (`tools/provision-guest-hello.ps1`) could set one. The prompt still needs a
person to answer it, so the run that settles this is not an unattended one: what can run
unattended is the call up to the prompt, read through the accessibility tree as a
Windows Security window owned by the client, then cancelled, which proves the interface,
the IID and the HWND owner and answers Declined.

Done when a guest run opens the export, sees the Windows Security prompt appear over the
client, cancels it, and reads that nothing was written; and the owner has once verified
with a real PIN on the reference desk and seen the vault written.

## Block C — Emulation that does not lie about the remote

### §QS92 The three environments this machine is not

QS12 landed the suite and ran it on two environments: this machine's own adapter, which
reproduces the references bit for bit, and WARP, which is a separate rasteriser
altogether and differs by at most one level of 255 on under 0.3 per cent of pixels. That
agreement is worth something — two implementations of the same shader arithmetic
reaching the same picture — and it is not the matrix.

Missing: NVIDIA, AMD and Intel integrated, which is where the users are, and a session
over RDP, which is where a graphics assumption fails silently rather than loudly. A
laptop with switchable graphics is its own case again, because the adapter can change
while the process is running.

Nothing here is a code change. What it needs is machines, and the shapes are: a
self-hosted CI runner per vendor; a cloud instance with a passed-through GPU, which
covers NVIDIA and AMD and not Intel; or a person running `run-tests.cmd` on hardware
they have and reporting the numbers the failure message already prints.

What must not happen is the tolerance being raised until a vendor passes. The measured
drift is one level; if a driver needs more than the two allowed, that is a finding about
the shader — most likely `pow` precision in the sRGB conversion — and it is filed rather
than absorbed.

Falsified when this repository claims cross-vendor correctness with no run behind it on
any vendor's silicon.

### §QS153 Composition drawn by the terminal rather than over it

QS29 gave the input method a point and let it draw. That is the arrangement Windows
falls back to and it works: the composition appears at the cursor because
`ImmSetCompositionWindow` was told where the cursor is. It is a floating box in the
system's own font over a GPU surface it cannot see, so it does not scroll with the line,
does not take the session's colours, and covers whatever is under it.

What the design asked for instead is the composition drawn as part of the grid — at the
cursor, in the session's font and palette, underlined, the convention every terminal
follows. `Composition` already holds everything that needs: the text, the caret inside
it, and the width in cells. What is missing is a painting path, because `GridPainter`
builds instances from a `TerminalBuffer` and a composition is deliberately not in one.

So the work is an overlay: cells appended after the buffer's, from the composition's own
text, through the same atlas. It is also where the underline goes, and `CellMetrics`
already answers where an underline sits in a cell.

Two things fall out for free. The client stops needing `CFS_POINT` to draw anything, and
a composition that reaches the right edge wraps the way the text under it does, because
it is the same grid.

Falsified when a composition is on screen in a font the session did not choose.

### §QS214 A steady-state allocation only the guest sees

`HostileInputTests.ReplayingARealStreamAllocatesNothingInSteadyState("tmux-resize")`
feeds the recording once to warm up and asserts the second pass allocates nothing. In
the guest on 2026-10-06 one full-suite run reported **7,288 bytes** in the second pass
and the next run of the same tree passed. On the host it passes alone and in its class,
every time.

What is known: the emulator has no clock, so the parse path cannot branch on time;
nothing in `Quickshell.Terminal` rents from `ArrayPool`; the measurement is
`GC.GetAllocatedBytesForCurrentThread`, on a synchronous test, so another test's
allocations are not counted. What differs is the machine — fewer cores and less memory
than the host, and whatever the suite ran before it on that thread.

Done first: on failure the test refeeds the stream in 512-byte pieces and names each
piece that allocated, offset and bytes. The type cannot come from inside the test: an
in-process listener gets no sampled-allocation events, which need startup, and the tick
fires per 100 KB; `dotnet-trace` in the guest is the way to a type. Then fix whatever
that names — the other four recordings never did this, so it is likely a path only a
resize stream reaches.

Falsified when the guest suite runs ten times without this test counting a byte.

### §QS215 A reference that is sometimes blank

`ContrastTests.ThisRenderersInkIsMeasuredAgainstDirect2Ds` draws one run four ways
through this renderer and through Direct2D and compares the light each put on the glass.
In the guest suite on 2026-10-06 the fourth pass — dark on light, ClearType — came back
with **Direct2D's ink at 0.0** against this renderer's 602.8, so the ratio was infinite
and the test failed. The same tree's other guest runs that day measured 682.8 for that
pass, and the three passes before it in the failing run were normal.

So the reference sometimes draws nothing, and the test reads that as this renderer being
infinitely bright. The likely shapes are a Direct2D target read back before its draw was
flushed, or a device lost under the guest's software adapter between the draw and the
read; neither is measured yet.

What to do: make the reference say when it drew nothing — assert its ink is above zero
before dividing, with a message naming the pass — so a red run says "the reference was
blank" rather than "∞ is out of range". Then find which of the two it is by repeating
the reference draw on a blank read and recording whether a second read of the same
target has ink.

Falsified when the guest suite runs ten times with no pass reading 0.0 for Direct2D.

### §QS260 A blink that costs one cell

Found shipping QS255 (`benchmarks/results/idle-h.md`): on the reference desk a blink
frame cost about 4 ms of core and kept a GPU engine 0.3 % busy, so a blinking cursor
alone was about half a per cent of a core.

What landed: the chain is `FlipSequential`, `PresentSurface.Retains` says when the back
buffer holds the current picture, `RedrawGate.Decide` answers `Redraw.Cursor` for a
blink alone, and `TerminalView.DrawCursor` patches one instance, draws the grid clipped
to that cell and presents one dirty rectangle. The frame report counts these as
`cursorFrames`.

What is left is the measurement, and it needs the reference desk. In the VMware guest
every blink took the one-cell path (349 of 349) and core time did not separate from a
whole frame's: a blink costs 0.6 to 0.9 ms of core there either way, inside the
run-to-run noise, and the guest reports no GPU engine time at all. On the reference
desk, with the operator's leave, run `tools/Quickshell.Idle --frames` for ten minutes
each on 84774fa and on this change, blink on and off, and read core and GPU time per
blink. Then `tools/Quickshell.Photon` at a prompt and under streaming output, against
`benchmarks/results/photon-h.md`, because the swap effect is also an echo's path.

Falsified when a one-cell blink costs what a whole-grid one did on that desk. Then the
swap effect goes back to `FlipDiscard` unless photon says sequential costs nothing, and
the per-frame cost is the wake and the present, which is a different line.

### §QS350 QS350 Tracing the narrowed pane's prompt line

QS316 settled the client's half of the extra prompt line a split leaves:
SessionPipelineTests prove a resize writes no input byte and a burst of resizes is one
window-change at the final size, and a pane whose size did not change is now told
nothing. The pane a split narrows is told, as it must be, and whether a real bash at its
prompt then adds a line (readline redrawing on SIGWINCH, which Windows Terminal over
OpenSSH would show too) or this client's reflow moves the cursor under it was never
traced: neither the guest (Windows OpenSSH, cmd) nor the host has a bash over SSH. The
Docker fixture's Debian sshd (prototypes/SshProbe/fixture) does. The trace: one split on
a Debian session with a short prompt, the session log at trace level counting
window-changes and bytes each way, the screen before and after; the same split in
Windows Terminal over ssh. If the line appears in both, it is bash's and is recorded as
such in docs/KEYS.md under Panes; if only here, the reflow on narrowing is the defect
and gets a line of its own. Done when that trace is recorded in benchmarks/results and
the conclusion is written where a user will look.

## Block D — The tree a user organises work in

## Block E — SCP and SFTP as a thing a person operates

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

### §QS257 A full-screen pane that keeps independent flip

QS225 timed echoes in a chain covering the whole monitor on the reference desk
(`benchmarks/results/photon-h.md`): shown as overlay, independent flip, on nearly every
echo, with a median of 24 ms typing and 24.5 ms under load against 32.5 and 41.7 for
every windowed chain. The windowed non-goal names this as its exception. The client has
no window of that shape, so no user gets the faster echo.

What to build: a full-screen toggle, F11 and a palette entry, beside the existing window
commands. The window becomes borderless at its monitor's exact bounds, topmost only
while it has the foreground, and the pane's chain is resized to the monitor's pixel
size. The tab strip either hides or is drawn inside the chain, since a WPF element over
the chain makes it composed again; which one is the open choice, and the photon tool's
monitor host is how it is settled.

The client's own frame order lost the flip on two echoes in three while typing in that
run (waited after painting, 28.3 ms), where wait-first kept it on every one. So the mode
ships with the pane waiting on the frame-latency handle before it reads the model,
measured by photon in the client's own host at full screen before and after.

A UI case toggles it and reads the window's bounds against the monitor's through the
accessibility tree.

Falsified when a full-screen pane's echoes are composed in photon's report.

### §QS353 A recent card that fits its buttons

Reported by the owner on 2026-10-15 with a picture of a restored window: the recent card
of a connected session shows "onnection" cut on the left and "Go to tab" against the
card's edge, with the card's "when" text showing through between them.

Why, in HomePage.cs: the recent cards sit in a UniformGrid of three equal columns inside
a body capped at 880 wide, so a card gets a third of what the window leaves after the
sidebar. QS313's two buttons are one horizontal StackPanel aligned right, in the same
Grid cell as the "when" TextBlock. The StackPanel is wider than the column, so it is
clipped on the left, and the text sits under it.

What to build: the "when" text gets a row of its own above the buttons, never the same
cell; the buttons go in a right-aligned WrapPanel, so on a narrow card Go to tab moves
under New connection instead of pushing it out. Go to tab stays the accent and the last
one. Not a smaller font or an ellipsis: a button whose label is cut is the defect.

Proven by a UI case on the connected fixture that narrows the window and reads both
buttons inside the card. A case cannot claim geometry yet (winwright WW539), so until it
can, a test in process lays the card out at the narrowest width Home allows and asserts
each button's desired size fits what it was arranged into.

### §QS354 QS354 A menu bar with every action

Asked by the owner on 2026-10-15: today every feature is reached by a hotkey or by
typing in the palette, and there is no menu where a user can browse what the client
does.

The constraint it keeps: Block G's Done when says a default installation opens with no
toolbar, menu or status bar (restated by the owner on 2026-10-13, QS272). So the menu is
not visible by default. It follows the Windows convention: pressing and releasing Alt
shows a menu bar under the title bar, access keys underlined, and Esc or a click
elsewhere hides it. A setting (menuBar: hidden | alt | always, default alt) and a
palette entry keep it on screen for good.

What to build: the menu is generated from the same action registry the palette
enumerates, grouped by category (File/Session, Edit, View, Terminal, Tab and Pane,
Transfer, Forward, Help), each item showing its current keybinding, so a rebinding is
reflected without a second list. No action is written into the menu by hand. Disabled
state follows the action's own can-execute rule, for example Close pane with no pane
focused.

Done when: a test asserts that the menu and the palette enumerate the same action set,
so an action added to one and not the other fails; a UI case presses Alt, finds the menu
bar in the accessibility tree, opens a category and invokes an item; a first run on a
clean profile still shows no menu bar until Alt is pressed.

## Block H — The reason to leave the incumbent

### §QS77 Installing, signing, and updating without a service

An installer that does the least: a per-user install with no administrator prompt by
default, a machine-wide option for managed deployment, and a portable archive with no
installer at all. Per-user by default matters because this audience frequently cannot
elevate on the machine they actually work on.

Code signing is not optional. An unsigned binary is blocked by SmartScreen, refused by
corporate policy, and reported as suspicious — which, for a client that handles
credentials, is the worst possible first impression available. The certificate and the
signing step are part of the release process rather than a later improvement.

Updating checks a static file over HTTPS on a schedule, and never at start-up, because a
start-up check spends the cold-start figure this project is measured on. It says what
changed and lets the user decline. It never installs while sessions are open.

The payload is signed and verified before anything is replaced, and verified against a
pinned key rather than only against the transport. A client that updates itself is a
client able to run arbitrary code as the user, which makes that check the security
boundary rather than a formality.

Uninstall removes the application and leaves the configuration, asking before removing
that.

Falsified when an update is applied without verifying a signature against a pinned key.

### §QS78 Seventy-two hours, and what is watched over them

A terminal client stays open for weeks. Every defect that scales with time is therefore
invisible to every test written up to this point.

The soak is seventy-two hours with twenty sessions: some idle, some printing
continuously, some opening and closing on a loop, some with forwards carrying traffic,
some deliberately dropped and reconnected on a timer, and at least one running a
full-screen program that redraws constantly.

Watched throughout: resident memory, managed heap by generation, GPU memory, handle
count, thread count, socket count. Every one of those must be *flat* after warm-up. Flat
is the criterion rather than merely bounded — a slow rise that stays under a limit for
three days is a leak that reaches the limit in three weeks, and three weeks is an
ordinary uptime here.

Atlas memory is watched specifically, being the one cache with an eviction policy and
therefore the one where a policy defect is indistinguishable from a leak.

The scrollback ring is the deliberate counter-example: it grows to its configured
capacity and stops, and confirming that it stops is part of the run rather than an
exception to it.

Anything that rises is a defect with a line of its own, found here rather than by a user
three weeks in.

Falsified when a watched counter rises across the run and the run is called a pass.

### §QS79 Making the number a gate instead of a report

The measurements had been watched long enough for their noise to be known, which is the
precondition this line waited for: a gate built on a number nobody trusts is disabled
inside a month, and then there is no gate and no measurement.

The first half is built and is described where it runs: `run-perf-gate.cmd`, and
`release.cmd` before it archives, hold `parse`, `emulate` and warm `start` on the
machine that took the baseline. Each threshold is derived from that baseline's own
samples and written beside them. A worsening past it fails; one a commit named with a
`Performance-Moved` trailer passes once and then owes a new baseline before a release;
every judged run is a row in `benchmarks/results/gate-<machine>.md`. PERFORMANCE.md says
how, and what it does not hold.

What is left needs a machine this repository does not have: the same gate run by CI on
every commit, on a runner that is the same machine each time, so drift is read per
commit without anybody remembering to run it. A hosted runner is a different machine
every run. Frame cost joins it through the replay's `--only cat-log:frame` arm (QS196):
its JSON entry carries CPU and GPU median and 99th-percentile milliseconds, lower being
better.

The allocation assertion is not part of it: it is exact rather than statistical, zero is
zero, and the suite checks it on every run already.

Falsified when the gate is disabled to land a change and the disabling is not itself a
filed line.

### §QS253 A baseline judged by its numbers, on a desk that drifts

Found shipping QS197. On 2026-10-10 the gate judged d972ff7 against the 2026-10-07
baseline and failed it: parse 13 % worse against an allowance of 1.5, the warm start
11.6 % against 3.6. Emulate, 15.5 % worse, held only because its allowance is 17.7.

The baseline's own commit, 40fb448, replayed from a worktree on the same desk minutes
later, read 967 MB/s for parse and 48.6 MB/s for emulate: as slow as d972ff7. The code
did not get slower; the desk did, by about a seventh, in three days. Every row is in
`benchmarks/results/gate-xps.md`.

The thresholds come from seven runs taken minutes apart, the noise within one sitting.
Day-to-day drift (thermals, Windows' own work, another session's load) is larger, so the
gate fails unchanged code, and a gate red for nothing teaches people to retake the
baseline until it is green.

What to build: judge against the baseline's code, not its numbers. The gate already
publishes the client being judged; it also publishes the baseline commit's client from a
worktree and replays the two interleaved in the same sitting, so whatever the desk is
doing lands on both. The verdict is the ratio, against a threshold derived from that
ratio's spread. Absolute medians are still printed and stop being the yardstick.

Falsified when the gate at an unchanged commit, on a day the desk reads a seventh
slower, still passes.

### §QS255 What wakes a pane nobody is typing into

The first part is shipped and measured in `benchmarks/results/idle-h.md`: the render
loop of an idle pane with `cursorBlink: false` now wakes zero times and draws nothing.
Two defects did it: the blink wait truncated to `Task.Delay(0)` and spun, and the blink
setting never reached a pane opened at start. The count that found them is
`--frame-report`, read by `Quickshell.Idle --frames`.

What is left, ten busy-desk minutes: **0.16 % of a core with zero wakes and zero
frames**, so it is spent outside the render loop. The window with no pane measured zero
(QS76), so it belongs to what a pane brings with it besides the loop: the pipeline's
three loops over a silent pseudo-console, the pane's `HwndHost` and its layout. The next
step is the first one again: count before fixing. `dotnet-counters` and a sampled
`dotnet-trace` of the idle process name the threads that run, and the frame report gains
a counter for each loop that wakes.

And with the blink on, a frame costs about 5 ms of core time, so a blinking cursor alone
is 0.6 % of a core. A blink changes one cell; the frame rebuilds every instance and
draws them all. Drawing only the cursor's cell, over the frame already presented, is the
shape of that fix, and QS260 carries it.

Falsified when one local pane, blink off, reads zero frames and under 0.1 % of a core
over ten idle minutes on a quiet desk.

### §QS288 The window's templates, without a XAML parser

Found 2026-10-13 by the performance gate on the reference desk (xps), run for QS272:
start 1315 ms against 907 ms for the baseline's code (40fb448), 35.7 % worse where 32.4
% is allowed. The gate times a shell start (--tabs 1), which QS272 leaves as it was, so
the cost is from the window itself.

The likeliest cause is QS277 and QS278, shipped 2026-10-12: the sessions sidebar and the
tab strip build their ControlTemplates with XamlReader.Parse (Templates.cs,
SessionsSidebar's row and bare text box), and both are built in MainWindow's
constructor, between process start and the first paint. XamlReader loads WPF's XAML
parser and its schema context on first use, which on a cold start is a large share of a
second; nothing else in the client parsed XAML before.

What to build: the same templates from FrameworkElementFactory and Triggers in code,
with the DynamicResource setters as DynamicResourceExtension values, so no XAML parser
is loaded on start. Measure first: run the startup harness on the reference desk with
and without the templates, and if XamlReader is not the cost, find what is from the
milestones (constructed and shown moved most in the guest's before-and-after for QS272)
before changing anything.

Falsified when the gate's start holds against 40fb448 on the reference desk.

## Block I — An error a user can act on

### §QS128 A trace that carries both sides of the negotiation

QS71 shipped a trace that records this client's version, the algorithms it was willing
to speak for each of kex, host key, cipher and mac, and what was agreed. What it cannot
record is the server's own offer, and the log writes "not reported by the library" in
that field rather than leaving a blank that would read as "the server offered nothing".

SSH.NET's `ConnectionInfo` exposes the supported sets on this side and the `Current*`
result of each negotiation. The peer's KEXINIT lists are parsed inside the library and
never surfaced. So the one failure this level exists for — an appliance that shares no
algorithm — leaves a log holding one of the two lists a reader has to compare.

Three ways out, in increasing cost. The library may expose the peer's lists in a later
version, which is a version bump and a call. Its `Session` type raises message events
that a reflection-free seam might subscribe to, if KEXINIT is among them. Failing both,
this client reads the version exchange and the first KEXINIT off the socket itself
before handing it to the library, which is a real protocol implementation in a
repository whose non-goals say there is not one — so that route is a decision, not an
implementation detail.

Measure first: check what the installed SSH.NET actually exposes before assuming the
worst of it.

Falsified when a failed negotiation against the `legacy` fixture leaves a log naming
both sides' algorithm lists.

## Block J — Leaving MobaXterm, proven by the switch

## Block K — The build and the harness — what a green run is evidence of

### §QS347 QS347 UI cases for the middle click and the tab menu

QS310's middle click on a tab and QS311's right-click menu on a tab are both proven in
TabStripTests (routed mouse events raised on the tab item, the menu built by
MainWindow.TabMenu and its entry clicked), not by UI cases, because a case's click verb
presses only the left button (winwright WW538). Shift+F10 was tried for the menu on
2026-10-15 and does not stand in for it: the press does not give the TabItem the
keyboard, so another menu opens and MenuItem#CloseTabsToTheRight is never found.

Once winwright spells the button, cases/tabs.cases.json gains two cases over a four-tab
fixture: one middle-clicks TabItem[order=left][index=2] and reads that the first tab
stays selected; one right-clicks the same tab, invokes MenuItem#CloseTabsToTheRight,
reads Window#Closing > Text#Asking as "Close 2 sessions?" (the four tabs are live
shells), invokes Button#close, and reads the second tab selected and the first behind
it. The unit tests stay, since they hold the live-session questions without a desk.

Done when both cases run green in the guest beside the others.

### §QS348 QS348 A UI case for the close cross

QS312 fixed a tab's close cross that painted nothing (the theme's 11-a-side padding on a
22-wide button) and proved it in process: TabStripTests shows the window off the desk,
renders the button through a VisualBrush, and counts painted pixels against New tab's
plus. The design wanted a UI case reading CloseTab's bounding rectangle inside its
tab's, which a case cannot claim yet (winwright WW539). Once it can,
cases/tabs.cases.json asserts Button#CloseTab within its TabItem for the short and the
long title, so a regression that clips or empties it fails in the guest too. Done when
that case runs green beside the others.

### §QS349 QS349 A UI case for the sidebar's selection

QS314's several-row selection in the sidebar is proven in process (SessionSelectionTests
reads each row's IsSelected through ListBoxItemAutomationPeer and the set the window
reports), not by the UI case its design asked for, because a case cannot add to a
selection or click with Ctrl or Shift held (winwright WW540). Once it can,
cases/sessions.cases.json, over a store of its own, adds two sessions and a folder to
the selection, reads each row selected, and reads the set the window reports; a Shift
range and a plain click that leaves one row follow. The set has no reading of its own
yet: the first action that takes one, QS315's document, is what the case then reads.
Done when that case runs green in the guest beside the others.

### §QS357 A slow producer is not a failing pipeline

QS263 cut SessionPipelineTests.TheDelayBeforeAReadIsParsedDoesNotGrowWithTheFile to
readings at 2 and 8 MB of a 48 MB `type` through cmd in a real ConPTY, inside a 30
second patience. On 2026-10-15 it went red twice in the guest, both times "6 MB parsed
after 30 s", then passed twice, once on HEAD and once on the same tree that had failed.
No code on its path had changed. So the guest's ConPTY sometimes prints well under 300
KB/s for half a minute, and the test reads that as the pipeline failing.

What the test claims is a ratio: the mean wait before a read is parsed does not grow
with the bytes. A slow producer does not falsify that, and neither does a fast one, so
the test should not depend on how fast cmd prints. What to build: measure until a byte
count or a time is reached, whichever comes first, and compare the two intervals by what
each covered. If the second interval covered less than three times the first's bytes
when time ran out, the test says the producer was too slow to judge and skips with the
rate it measured, which the skip budget then counts, rather than going red. A producer
the test controls, with no cmd, would lose what the test is for: a producer that does
not wait.

Done when ten guest runs in a row go green or skip with a rate named, and a parser made
to wait on its renderer still fails it.

## Block L — An agent at the controls, held to a policy it cannot edit

### §QS339 A policy the agent cannot edit

A permission an agent can grant itself is no permission. The policy file sits on the
same disk, under the same Windows user, as everything the agent can write.

Three measures. The file lives in quickshell's settings directory, never in a repository
or a working directory, so it is outside the paths an agent is usually pointed at. The
plugin's `PreToolUse` hook denies the agent's file tools any write to that path, and its
shell tool any command naming it. The same guard covers `agent-taints` (QS338), whose
deletion would lift a taint the person never lifted. And quickshell holds the hash of
the last version the person approved: when the file on disk differs, the gateway refuses
every call and the client asks the person to accept or reject the change, showing the
diff.

The third measure is the one that holds even if the first two are walked around, since
the gateway enforces the approved version, not whatever is on disk.

The design document says what stays open: a same-user process can still read the
approved hash's store, so the server-side layer remains the real boundary.

Proven by an agent asked to widen its own policy by edit, by shell and by a script, each
refused or caught, with the gateway refusing calls until the person answered.

### §QS340 The server-side boundary

Everything the gateway does on the client can be walked around by an agent running as
the same Windows user, which the threat model (QS320) says plainly. The one layer the
agent cannot route around is the server, so the gateway should make that layer easy
rather than leave it to a person who already finds it tedious.

A guided setup, from the session's context menu, proposes for one server: a dedicated
account (`agent` by default) with its own key, `authorized_keys` options on that key
(`restrict`, `from=` this machine's address, optionally `command=`), and a sudoers file
granting only the commands the person ticks from a list derived from the session's
policy. It shows the exact files and commands it will run, runs them only on approval,
as the person's own account, and records them in the audit log.

The session then gains an agent identity: the gateway connects as `agent`, the person
still as themselves.

The design document weighs what to offer for a server where the person has no root, and
how to undo the setup.

Proven against the test sshd: after setup, a command outside the sudoers list fails on
the server even with the client-side policy turned off.

### §QS341 Certificates that expire with the task

A plain public key in `authorized_keys` is valid forever. If the agent account's private
key leaves the machine, by a backup, a transcript or a compromised process, it keeps
working until someone notices.

OpenSSH certificates solve exactly this. The guided setup (QS340) can instead install a
CA public key on the server with `TrustedUserCAKeys` and an `AuthorizedPrincipalsFile`
for the agent account. quickshell then holds the CA key (in Credential Manager, behind
Windows Hello when the person asks for it) and signs a fresh certificate when the
gateway opens a connection: valid for a short window the policy sets, principal `agent`,
`source-address` this machine, and `force-command` where the policy has one.

A stolen certificate is worthless within minutes, nothing has to be removed from servers
when a key rotates, and revocation is simply not signing another.

The design document has to settle where the CA key lives and how a person rotates it,
whether one CA spans all servers or one per tag, and how SSH.NET presents a certificate,
which may need work in the library seam rather than here.

Proven against the test sshd: a connection with a fresh certificate succeeds, and the
same certificate is refused after its window.

### §QS342 Claude Code in a pane, with the pane's context

The most common moment to want help is while looking at an error in a session's output,
and today that means selecting it, switching to another window and pasting it into an
agent with no idea which server it came from.

quickshell already runs local shells through ConPTY, so the least new surface is a pane:
a profile that starts `claude` in a local pane with the gateway's MCP configuration and
plugin already in place, so it reaches saved sessions through the gateway from its first
command. A palette command, "Send to Claude", takes the current selection, or the last
screenful when nothing is selected, and types it into that pane prefixed with the
session name and tags it came from.

This deliberately does not build a chat interface inside the client. Claude Code already
is one, a second would cost the window budget, and a person who wants it elsewhere keeps
using it there with the same gateway. The design document records that choice and what
would reopen it.

Proven by a UI case that selects text in an SSH pane, invokes the command, and reads the
prefixed text from the Claude pane's buffer.

### §QS343 Shipped as a Claude Code plugin

Security that takes an afternoon to configure is security most people skip. The
gateway's pieces are an MCP server entry, two hooks (the side-door steering and the
policy-path guard), sandbox network rules, and a skill telling the agent which tool does
what. Each is a hand edit in a different file today.

A Claude Code plugin carries all of them. Its MCP entry points at the installed
`quickshell.exe --mcp`, found the way the client's own installer records its path. Its
hooks are small and run without a runtime the user must install. Its skill is short: use
`exec` with argv rather than a shell string, prefer `edit_file` to heredocs, expect asks
on `prod`, and report a refusal rather than work around it.

quickshell's settings page offers to install the plugin when it finds `claude` on the
PATH, and says what it will change before it does.

The design document settles where the plugin is published (this repository, or a
marketplace entry beside roadkeep's), and how its version is tied to the client's so a
mismatched gateway refuses with a message rather than misbehaving.

Proven by a clean guest: install the client, accept the offer, and an agent's first
`exec` reaches the test sshd.

### §QS356 Quoting for the server's own shell

Found by QS325. `exec` joins the argument vector with `Argv.ForPosix`: a safe word bare,
anything else in single quotes. Windows OpenSSH runs an exec request through the
account's default shell, cmd.exe unless the server's DefaultShell says otherwise, and
cmd gives a single quote no meaning. So on the guest's own server `whoami` and
`hostname` work, and an argument holding a space or a quote arrives with the quotes in
it or split in two. Nothing says so: the command runs, wrongly.

What to build: the gateway learns the server's family once per connection and quotes for
it. The cheapest reliable probe is the version string the handshake already carries
(`OpenSSH_for_Windows_9.5`), with an `echo %OS%` exec as the check where the banner is
silent. For a Windows server the vector is joined as `CommandLineToArgvW` reads a
command line, the rules .NET's ProcessStartInfo.ArgumentList follows, and run through
cmd with `^`-escaping of `& | < > ^ %` outside quotes, or through `powershell
-EncodedCommand` with the vector as a literal array, which sidesteps cmd entirely and is
the choice to try first. A `cwd` becomes `Set-Location -LiteralPath` there.

Until it is built, `exec` refuses a vector with any non-bare word on a server it
recognises as Windows, naming the word, rather than running something else.

Done when the guest's server receives an argument holding a space, a double quote, a
percent sign and an ampersand byte for byte, read back by the program it was given to.

### §QS358 A wall on native Windows

Found by QS332. On macOS, Linux and WSL2, Claude Code's sandbox gives the agent's shell
no route out but its HTTP proxy, so a raw `ssh` cannot connect, while the gateway, which
runs outside the sandbox, keeps its connections. That is the measure that holds. Claude
Code runs commands unsandboxed on native Windows, which is where this client runs. There
the only thing on the agent's side is `quickshell --agent-hook`, which reads command
lines, and a script written first and run second walks past it.

What to look at, in this order, each measured before it is chosen:

1. Windows Firewall outbound rules by program: block TCP to port 22 and the saved sessions'
   ports for `ssh.exe`, `plink.exe` and `python.exe`. Cheap, but an unnamed program gets out.
2. A rule by user: the agent runs as a second local account that a WFP filter denies those
   ports, and the gateway runs as the person. A boundary below the shell, at the cost of
   another account.
3. The agent in WSL2 with the sandbox on and the gateway on the Windows side, which may
   be the answer to recommend, with nothing to build.

Done when, on native Windows, a script the hook cannot see fails to reach the test sshd
while the gateway's exec reaches it, or when the recommendation is written in the design
with the reason the other two lost.
