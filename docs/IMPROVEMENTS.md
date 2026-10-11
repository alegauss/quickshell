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

### §QS114 The other transport under the same protocol

QS43's design names two agents to reach on Windows and says the useful thing about them:
they speak the same requests, so only the transport differs. One of the two landed — the
named pipe, which Windows' own OpenSSH agent uses and which Pageant added in 0.78.

What is left is older Pageant, and it is the population this client was written for:
PuTTY and MobaXterm users, many of whom are running whatever version their organisation
packaged. Its transport is a file mapping plus a `WM_COPYDATA` sent to a hidden window
whose class name is `Pageant`, with the request written into the mapping and the
mapping's name passed in the message.

None of that touches the protocol above it. `SshAgent` already separates the two —
`Exchange` is the only method that knows there is a pipe — so this is an implementation
of that one method against a different carrier, and everything that reads identities and
asks for signatures is already written and already tested.

Two things it needs that the pipe did not. A security descriptor on the mapping that
Pageant will accept, since it checks the caller's SID. And a message pump, because
`SendMessage` to another process's window blocks and the answer arrives by the mapping
rather than by a return value.

Falsified when a signature obtained through shared memory differs from one obtained
through the pipe for the same key and the same data.

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

### §QS258 Printable runs written as spans

QS141 gave the whole path a figure in `docs/PERFORMANCE.md`: `emulate` at least 100 MB/s
on `cat-log`, the rate one SSH session over loopback delivers with the parser switched
off. The gate's 2026-10-07 baseline reads 53.6 MB/s, so a `cat` of a large file over a
fast link is slowed by the terminal by about half.

Where to look is already measured. The replay ladder in
`benchmarks/results/replay-xps.md` names each stage's cost; QS143 cut clustering by
taking the below-U+0300 fast path, and QS144 found 87 per cent of cell writing in the
per-character path at 87 ns a character. That per-character path is the target: a run of
printable ASCII in one attribute can be written as a span (one bounds check, one
attribute store, a block copy of the code points) instead of a call per character, the
way the parser already hands printable runs on. Clustering's fast path makes a run known
to be one cell per character before it reaches the buffer, so the span is safe for
exactly the input `cat-log` is made of.

Measured on the ladder before and after, each change its own commit, and the gate's
baseline retaken once the figure moves, with the `Performance-Moved` trailer naming it.

Falsified when `emulate` reads 100 MB/s or more on `cat-log` at the gate on the
reference machine.

## Block D — The tree a user organises work in

## Block E — SCP and SFTP as a thing a person operates

### §QS188 A drag that starts on the server

Carried out of QS64's design, which shipped the drops into the client and left the
direction out of it. Dragging entries out of the host's pane onto Explorer or the
desktop is a download, and it is the harder direction: Windows asks for the data during
the drop, not afterwards, and a server over a slow link cannot answer in the time a drop
is allowed to take.

The mechanism is deferred rendering. The pane offers a data object carrying
`CFSTR_FILEDESCRIPTOR` — the names, sizes and times, which it already has from the
listing — and `CFSTR_FILECONTENTS`, whose stream for each file is read from the
session's file channel only when Explorer asks for it. Landed as `RemoteDrag`: no
placeholder or queued download is needed, because the object offers the shell's
asynchronous capability and Explorer copies on its own thread after the drop returns;
the object is served from a thread of its own, so the reads never hold the window.

The same drag source is what dragging between two browsers needs, one per tab, on two
hosts. That copy goes through this client, and it says so, because a user may reasonably
have assumed the two servers were talking to each other.

Falsified when dragging a file from the host's pane onto a local folder does not leave
that file there, byte for byte.

## Block F — A forward is a lifecycle, not a checkbox

### §QS259 A remote forward's channel, answered here

Found shipping QS252, which bounded local and SOCKS forwards. A remote forward is
carried by SSH.NET's `ForwardedPortRemote`, whose `ChannelForwardedTcpip.OnData` writes
each packet to the local target's socket with a blocking `Send` on the session's one
receive thread, the same shape QS252 removed from the other two. So a local service that
stops reading what a remote client sends it freezes every channel on the connection, the
shell included. Not yet reproduced: the reading is of the library's 2026.0.0 source.

The difference from QS252 is who opens the channel. A local forward opens its own, so
`ChannelWindow.Bound` can set the window before the open. A remote forward's channel is
opened by the server and answered by the library inside `ForwardedPortRemote`, which
connects the local socket itself and sends the confirmation carrying the library's 2 GB
window.

What to build: `RemoteForward` answers the server's `forwarded-tcpip` opens itself,
subscribing to the session's channel-open event as `ForwardedPortRemote` does, creating
the channel through the session, giving it one end of a pair sized as `LocalForward`'s
is, and setting the window before the confirmation is sent. The pumps and
`ChannelWindow` are QS252's.

Evidence: the fixture test QS252 wrote, turned round: a remote forward to a local
listener that accepts and never reads, `yes` on the server piped into the forwarded
port, and the shell beside it asked to echo within three seconds.

Falsified when a remote forward nobody reads leaves the shell beside it answering.

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

Found measuring QS137, recorded in `benchmarks/results/idle-h.md`. A window with one
local pane, nobody typing, costs 0.62 % of a core and 0.17 % of a GPU engine over ten
minutes. Figure 4 asks for zero. With `cursorBlink: false` it costs 0.57 % and 0.20 %,
so the blink, the one wake the design allows, is not what spends it. Four panes cost 2.8
times one. The window with no pane in it (QS76) measured zero.

So something wakes a live pane and makes it draw, about as often with the blink off as
on. `PresentSurfaceTests` proves the gate presents nothing for an idle loop it drives
itself, so the suspect is what drives the real one: what `TerminalView`'s loop waits on,
whether a damage counter moves while the host is silent (a local shell's console can
repaint on its own), and the frame-latency wait.

What to build first is the count, not the fix: the pane keeps how many frames it drew
and why each was claimed (damage, cursor, resize), and the idle tool reads it, so the
answer is a number per cause. The fix follows the largest cause. A test then holds a
live pane, host silent and blink off, for a minute and asserts zero frames, beside the
gate test that already proves the loop's half.

Falsified when one local pane, blink off, reads zero frames and no measurable core
occupancy over ten idle minutes.

### §QS256 Where the first pane's ninety megabytes go

Found measuring QS137, recorded in `benchmarks/results/idle-h.md`. Figure 6 allows 120
MB of private memory for one connected session at default scrollback. The window with no
pane measured 79.4 MB (QS76). One local pane, settled, holds 172.5 MB; a connected
session with a local tab behind it holds 211.4 MB; four local panes hold 218.3 MB.

So the budget is spent before a session is opened, and the first pane costs about 93 MB
where three more cost 46 between them. The first pane's cost is therefore mostly what is
created once per process, not per pane: the graphics device and its swapchain, the
atlas's first pages, DirectWrite's font collection, and whatever WPF interop brings in.

What to build first is the account: a breakdown of the first pane's 93 MB by owner,
taken from a heap snapshot (`dotnet-gcdump`) for the managed half and from the process's
committed regions by module for the native half, written beside the idle figures so each
later change can be read against it. Then the largest owner is cut, with the figure
re-measured the same way each time. If the account says the device and the driver hold
most of it, the budget's 120 MB is argued in a commit that changes
`docs/PERFORMANCE.md`, since that file's figures move only that way.

Falsified when a connected session at default scrollback settles under 120 MB of private
memory on the reference machine.

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

### §QS81 The document the non-goals were written for

The non-goals are a decision record aimed inward. This is the same content aimed
outward, at somebody deciding whether to move their working life onto this client.

It states what quickshell does, what it deliberately does not, and what the alternative
is for each refusal — because a user who genuinely needs an X server is better served by
being told to keep one than by discovering the absence after migrating. Every refusal
names the thing to use instead, or says plainly that there is not one.

It states the migration path: import, what carries over, what does not, and roughly how
long it takes.

And it states the comparison honestly, with numbers rather than adjectives, on a named
machine: start-up, memory, idle cost, terminal throughput. A comparison with no
methodology is marketing, and this audience will re-run it themselves within an hour of
reading it.

Where the incumbent is better, that is written down too. A client claiming to win
everywhere is a client nobody believes about anything, and this audience in particular
will find the one exception and then discount the rest of the page.

This is the last thing written rather than the first, since every claim in it has to be
true of the shipped build and not of the plan.

Falsified when a figure in it cannot be reproduced from a documented run.

## Block K — The build and the harness — what a green run is evidence of
