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

## Block L — An agent at the controls, held to a policy it cannot edit

### §QS360 Windows Hello in front of the agent authority

QS341 keeps the agents' certificate authority sealed with DPAPI for the person's Windows
account and unseals it for each signing. That stops a copied file from being used on
another machine. It does not stop a process on this one: any program running as the
person, an agent's included, can ask DPAPI for the key and sign itself a certificate.
The certificate still opens only the account the server limited, but it bypasses the
client's policy and audit.

An option in Settings, off by default, should ask Windows Hello before each signing
(`UserConsentVerifier`, or a key held by Windows Hello through the platform key storage
provider rather than DPAPI). A signing nobody confirms fails, and the gateway reports
the connection failing, as it does today when there is no authority. Since the gateway
keeps one connection per session and signs only when it opens one, the person is asked
once per session per gateway run, not once per command.

The design has to settle three things. First, whether the authority moves into a
Hello-bound key (a format change, so existing setups are set up again) or stays sealed
with a Hello prompt in front, which is weaker but needs no migration. Second, what an
unattended desk does: a locked screen answers no. Third, how a test proves it without a
person.

Proven where Windows Hello is available: with the option on, a signing with no
confirmation writes no certificate and the gateway's call fails, naming why.

### §QS362 The plugin, installed for real and used

QS343 writes the agent gateway as a Claude Code plugin and installs it from Settings. What
was proven:
- `claude plugin validate --strict` accepts the plugin and its marketplace;
- the built `quickshell.exe` refuses a plugin of another version;
- Settings asks first, then writes the plugin and runs its three `claude plugin` steps, against
  a stand-in that records them.

What was not: that a real Claude Code takes those steps without asking anything QS343
did not expect, loads the MCP server and the hook from the installed copy, and that an
agent's first `exec` then reaches a server. The section QS343 came from asked for
exactly that run, on a clean guest. The guest has no Claude Code, and installing into
the operator's own Claude Code to prove it would change their configuration.

The work:
1. Provision Claude Code in the guest, signed in with credentials meant for testing. This is a
   requirement, declared in `roadkeep.toml`, so the line is offered only to a caller that has it.
2. Accept the offer through the Settings window as a UI case.
3. Run `claude -p` with a prompt that calls `hosts_list` and one `exec` on the guest's sshd, and
   read the audit log the gateway wrote.

A second run, after the client's version changes and before the plugin is installed
again, should show the gateway refusing with its message in `claude mcp list`.

Proven by the audit log holding that `exec`, its exit code and the session it ran on.

### §QS363 The plugin, inside WSL2

QS358 settled that on a Windows desk the wall around an agent is WSL2: the agent runs
there with Claude Code's sandbox on, and the gateway is the Windows `quickshell.exe`,
started through interop. It measured that this works: from inside WSL2, the Windows
client answers the MCP handshake over its streams.

The plugin QS343 writes cannot be used there. Its MCP entry and its hook name the client
by its Windows path (`C:\…\quickshell.exe`), which a Linux process cannot start. A
person following the recommendation is back to writing the MCP configuration and the
hook by hand, which is the tedium QS343 removed.

What to build: when Settings offers the plugin, it also looks for Claude Code inside the
installed WSL distributions (`wsl -l -q`, then `wsl -d <name> -e sh -c 'command -v
claude'`) and offers to install the plugin there too. It writes a second copy of the
marketplace whose commands are the `/mnt/<drive>/…` path of the same executable
(`wslpath -u`), and installs it with that distribution's `claude plugin`, saying first
what it will run, as the Windows install does. The sandbox settings go with this copy,
since there the sandbox exists: `enabled`, `failIfUnavailable`, and
`allowUnsandboxedCommands: false`.

Proven by a WSL2 distribution with a stand-in `claude` that records its arguments: the
offer finds it, the written plugin's commands are `/mnt` paths, and the gateway started
from that path answers `tools/list`.
