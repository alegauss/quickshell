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

### §QS142 Taking it upstream, or not staying here

QS139 measured the defect and found the trade: `ShellStream` resizes and buffers without
bound, `Shell` bounds and cannot resize, and the non-goal forbids writing the channel
layer here. Every route out of that runs through somebody else's code.

Three, in increasing cost. **Report it**, with the reproduction QS139 already has — ten
seconds of an unread shell holding three gigabytes is a defect statement that needs no
argument, and the fix upstream is to stop extending the window ahead of consumption.
**Add what is missing**: a window-change request on `Shell`, or a bounded mode on
`ShellStream`, contributed rather than requested. **Or leave**: evaluate another managed
SSH library against the same reproduction, which is a bigger decision than this line and
would want its own measurement of what else would have to move.

What makes this worth filing rather than enduring: the trade is not a tuning choice, it
is two shipped properties that cannot both hold. A user resizing a window and a user
connected to a fast host are the same user.

Before any of it, confirm the premise against the newest SSH.NET rather than the pinned
one — 2026.0.0 is what was measured, and a bounded `ShellStream` may already exist
upstream. That is one version bump and one run of `ChannelBackpressureTests`, and it
would make the rest of this unnecessary.

Falsified when the trade turns out not to exist.

### §QS220 A tab and the session that reconnects, never introduced

QS38 built `RemoteSession`: a model that outlives its connection, a bounded and visible
backoff, a frozen peer noticed by QS111's keepalive, an exit that is not retried. QS126
made a saved session an SSH tab through `RemoteShell`, which connects once and ends with
the connection. The two have never met, so a tab whose link drops shows the ending and
stays ended, exactly the symptom QS38 shipped a fix for.

What to build: `RemoteShell` connects through `RemoteSession` rather than beside it —
the factory builds the transport and the sign-in narration as it does now, and the
session reconnects under the policy, keeping the pane's model and scrollback. The pane
shows the attempt, when the next one is due and how to stop, which `SessionStatus`
already carries. The session's forwards are started again on each new connection, and
the pane says which came back and which did not (QS69's remainder).

`RemoteSession` makes its own pipeline per connection with a damage signal of its own,
which QS151 already says freezes a pane asleep on the first; that is part of this work.

Falsified when an SSH tab whose link dropped for ten seconds is not connected again with
its scrollback, or comes back without saying which of its forwards did.

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

### §QS45 The most dangerous convenience in the protocol

Agent forwarding lets a remote host ask the local agent to sign. It is genuinely useful
— it is how a user reaches a third machine from a bastion without leaving a key on the
bastion — and it is also the sharpest edge in ordinary SSH use: while the session is
open, anyone with root on that host can sign as the user, to anywhere the user's key
opens.

So the design is refusal by default and consent per host. Not a global setting, because
the entire risk is host-specific: forwarding to a bastion the user administers is
reasonable, and forwarding to a shared jump box is handing over a key.

Where it is enabled for a host, the session's settings show it as enabled with the risk
in a sentence, and the running session shows it too — a forward the user has forgotten
about is a forward they cannot reason about.

The forwarded socket closes with the session and is not left behind.

The alternative worth offering in the same breath is a jump host configuration, which
reaches the third machine without any agent ever being exposed on the second. Where that
solves the user's actual problem it is the better answer, and the settings surface says
so instead of staying neutral.

Falsified when a session forwards an agent with no per-host consent recorded.

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

### §QS115 The derivation that is honest work and not what was asked

QS44's design asks for "Argon2id or an equivalent memory-hard function". What shipped is
PBKDF2-HMAC-SHA512 at 600,000 iterations, which is above OWASP's current figure for that
construction and is not memory-hard.

.NET 10 ships no memory-hard derivation — no Argon2, no scrypt, no balloon hashing. So
the choice was between taking a third-party cryptographic package into the one code path
where a mistake cannot be noticed by testing, and using the strongest primitive the
framework does have while saying plainly that it is not the one the design named.

The cost is real and worth stating in the same sentence as the reassurance: PBKDF2 is
compute-hard and not memory-hard, so an attacker with a graphics card gets far more
guesses per second against it than against Argon2id at comparable settings — roughly two
orders of magnitude on commodity hardware. Against an attacker who has the file and is
guessing a master password, that is the whole difference.

Closing it is small and mostly a decision rather than work: `MasterKey.Derive` is four
lines and one call, and the parameters are already isolated there because changing them
makes every stored secret unreadable. What it needs is somebody to decide that
`Konscious.Security.Cryptography.Argon2` — or a successor in the framework — is a
dependency this project accepts, and a format version so existing secrets migrate rather
than break.

Falsified when the derivation changes without a way to read what the old one wrote.

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

### §QS139 Gigabytes waiting in a channel nobody is draining fast enough

A host sending faster than its consumer buffers inside the channel, without bound.

**Settled by asking directly.** A shell opened, the host told to print on a loop,
nothing reading for ten seconds: **3,072 MB held** after a compacting collection, all
but a megabyte of it large object heap. About 300 MB/s of pure buffering, so any host a
user connects to can exhaust this client's memory in seconds. `ChannelBackpressureTests`
is that reproduction, skipped and naming this line.

**It is not the emulator.** One 200x50 emulator with 2,000 lines of scrollback holds
under 128 MB absolutely, and feeding it 64 MB adds under 8 MB; both are asserted in
`ParseRetentionTests`. Measured against the fixture, retention tracks the consumer's
slowness rather than bytes parsed:

| one printing session, ~3 min | moved | retained |
|---|---|---|
| reads only, no parsing | 18,176 MB | 8.9 MB |
| through `SessionPipeline` | 657 MB | 1,035 MB |
| reading the channel directly | 143 MB | 2,055 MB |

The pipeline's bounded queue halves it and moves 4.6 times more, and does not fix it.

**The library forces a choice**, and compile probes settle it. `ConnectionInfo` has no
window-size member. `Shell` — the one API writing received bytes into a `Stream` this
client owns, which is backpressure out of public parts alone — has no
`ChangeWindowSize`. So `ShellStream` gives a resizable terminal and unbounded memory;
`Shell` gives bounded memory and no resize. Writing the channel layer here is what the
non-goal forbids.

In 2026.0.0 `Channel.OnData` adjusts the window on arrival, then raises `DataReceived`
on the session's one loop thread into `ShellStream._readBuffer`. The choice: block that
thread (every channel stalls), steer `LocalWindowSize` by reflection, or change the
library.

Falsified when an unread session grows without limit.</body>

### §QS141 The figure and the path it is measured on

Figure 2 of the budget is *sustained parse throughput, at least 400 MB/s*, measured by a
headless harness with no renderer. `benchmarks/results/replay-xps.md` reports 977 MB/s
for `cat-log` under the `parse` consumer, so the figure looks met.

`Emulator.Feed` does not run at that speed. Measured twice, independently:
`ParseRetentionTests` feeds one emulator 64 MB of `cat-log` in 64 KB chunks and takes
about seventeen seconds — near **4 MB/s**. Through `SessionPipeline` against the
fixture, 657 MB in about three minutes — near **3.5 MB/s**. The two agree with each
other and disagree with the published figure by two orders of magnitude.

The likely explanation is that they are not the same path. The replay harness's `parse`
arm is the scanner and the state machine; the same table's `render` arm is 9 MB/s.
`Feed` also writes cells, and writing cells is what a terminal is for. If so, the figure
is honest about what it measures and silent about what a session costs — which is the
number that matters.

Two things to settle, and the order matters. **Which arm figure 2 governs** — if it is
the scanner alone, the budget needs a figure for the whole path, or nobody can tell
whether a session is fast. And **why cells cost 250 times a scan**, because that is
where the answer is, and it is the same reason QS139's buffer fills.

Falsified when a figure is quoted for a path it was not measured on.

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

### §QS212 Symmetric, or the same as Windows

QS107 measured it, in `docs/measurements/contrast.md`. Against Direct2D's own text, this
renderer's dark-on-light text carries 12 to 17 % less ink, and its light-on-dark text 4
to 7 % more. This renderer weighs the same both ways because it blends coverage in
linear light, which QS9 built on purpose and tests:
`TheSameCharacterLightOnDarkAndDarkOnLightHasMatchingWeight`. Windows does not, so a
light theme here looks thinner than the editor beside it.

Two ways to go, and choosing between them is a judgement about what this client should
look like:

- Keep the symmetry. Text weighs what its coverage says on any theme, and the light-theme gap
  to Windows is the price. Then this line retires into a decision record naming the measured
  gap.
- Match Windows. Fit a correction by polarity, a curve applied to coverage when the ink is
  darker than the ground, fitted against the Direct2D reference `ContrastTests` already draws.
  That rewrites QS9's symmetry test into "matches Windows within N %" for both polarities. A
  lookup table fitted to the measurement is honest where a guessed exponent is not, and the
  fit is checked by the same test going to a ratio near one on both rows.

Either way the fit is per pixel and not per total, so the first step of the second
option is a profile: ink by coverage level, from the same two pictures.

Falsified when a light theme is called fixed without the dark-on-light ratio near one.

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

### §QS227 Turning the faithful esctest figure into lines

QS211 took conhost out of the esctest run, and the first faithful figure is 216 passed,
43 xterm's own known bugs and 309 failed of 568, in `docs/measurements/esctest-xps.md`.
Block C's criterion asks for above ninety per cent with every failure named. Until now
no line could aim at the failures, because conhost's answers hid which were the
emulator's.

The failures are spread over 60 classes, and twelve hold more than half of them:
XtermWinopsTests 28, DECRQMTests 26, DECSEDTests 15, DECSETTests 14, the three colour
query families 40 between them (ChangeColor, ChangeDynamicColor, ChangeSpecialColor),
DECDSRTests 11, DECSELTests 10, DECRQSSTests 9, DECCRATests 8 and BSTests 8.

What to build first: one line per class family, filed from the log with the traceback
that names the difference, starting with the families where one missing answer fails
many tests. DECRQM's 26 are probably modes this client answers zero for, and the colour
queries probably OSC 4, 10, 11 and 12 replies in a form esctest does not read. Each line
names the tests it should turn and is shipped against a rerun of `dotnet run --project
tools/Quickshell.Conformance -c Release -- <Class>`.

A filtered run rewrites the measurement with only that class, so restore the file after
one and rewrite it whole with an unfiltered run before the commit that cites a figure.
Falsified when a failing class has no line and no non-goal naming it.

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

### §QS69 A forward has a life, and it outlives attention

A forward is configured on a session rather than created ad hoc, so it survives the
session being closed and reopened, and so it travels with the session store when that
store is shared.

It starts with the session where it is marked to, and a start that fails does not stop
the session connecting. The terminal is the primary thing, and a port conflict must
never cost the user their shell.

A local port already in use is the most common failure here by a wide margin. The client
names the port and, where it can, what is holding it — the answer is usually a previous
instance of this client, and knowing that saves somebody a reboot.

Reconnect re-establishes every forward the session had, and reports which came back and
which did not. A forward silently absent after a reconnect is worse than one that failed
loudly, because the application using it then fails in a way that points at the
application.

Stopping and starting one individually, without touching the session, is available,
since a user debugging a port conflict needs exactly that and nothing else.

The teardown path is the one tested least and mattering most: closing a session closes
its listeners and its live channels, and a listener outliving its session is what makes
the next start fail.

Falsified when a closed session leaves a listening socket behind.

### §QS70 Showing the thing that has no window of its own

Forwards have no window, no output and no obvious presence, which is exactly why they
have to be shown. One view lists every forward this client currently holds across every
session: direction, local address and port, remote target, owning session, state, and
the number of connections currently carried.

Live connection counts are what make the view diagnostic rather than decorative. A
forward listening with nothing connected and a forward carrying eight connections look
identical in any list that omits the count, and those are precisely the two states a
user is trying to tell apart.

Each row stops and starts, and each copies as an address the user can paste into
whatever tool needs it.

The window's own chrome carries a small indicator whenever any forward is active,
because a user who has forgotten one is running has an open route into a production
network on their laptop and does not know it.

Recent failures appear in the same view with their reasons. A forward that failed an
hour ago is invisible everywhere else by now, and it is exactly what the user is
currently trying to explain to somebody.

This is a surface that survives the leanness argument, because the alternative to it is
`netstat`.

Falsified when a running forward does not appear in this view.

## Block G — The clean interface, defended

### §QS160 Moving a tab, and the connection that must not notice

`TerminalTab` owns a model, a pane, a device, a loop, a keyboard and a shell, and
`MainWindow.Remove` hands one back alive rather than ending it — which was written that
way for this. What is missing is the two gestures that use it.

Reordering is the smaller half: a drag on the strip, and three lists moved in step,
since the tabs, their headers and the panes are held separately.

Detaching is the one with the claim in it, and it is QS47's own falsification:
*falsified when detaching a tab reconnects its session*. A tab moved to a new window
keeps its connection, and it can only do that because the connection was never the
window's. The obstacle is not the session but the pane: it is an `HwndHost`, and taking
one out of a visual tree destroys the child window a swapchain is presenting into. So a
detach is either a reparent WPF has no supported spelling for, or a new device on a new
pane with the same session behind it — and only the first satisfies the falsification
without qualification.

Most-recently-used order is the third gesture, and the cheapest: a list the active
setter appends to.

Found reordering: `Hold` hooks a pane's `Mouse` and `Dropped` to its window, `Program`
wires one window's surfaces, and closing the first window ends the process.

Falsified when detaching a tab reconnects its session.

### §QS217 Sessions in the palette

QS126 made a saved session openable as an SSH tab, and the only way to ask for one is
`quickshell --session <path>` on a command line. The palette, which is where every other
action in this client is reached, lists none of them: a user with forty saved hosts has
to know the path of the one they want and type it into a shortcut.

What to build: an "Open session" entry in the palette that turns the palette into a list
of the store's sessions — path, host and tags, searched the way `SessionTree.Search`
already searches — and opens the chosen one through `MainWindow.OpensSession`, which the
program already wires to `RemoteShell`. The store is read when the list opens, so a
session saved a moment ago is in it. The most recently opened come first, as the palette
already ranks its commands.

Built: the entry, the list, and unit tests through `Choosing`; palette.cases.json reads
"Open session" at the top. Left: a case typing part of a known session's name, which
needs a fixture that hands the client a store of its own, winwright's WW509.

Falsified when a saved session can only be opened by typing its path.

### §QS221 A device loss the client comes back from

`GraphicsDevice` can survive a loss: `RemovedReason` says one happened, and `Recover`
walks the adapter chain again and rebuilds every registered resource. Nothing in the
client calls either. The share's loop draws until a call fails, and a driver update or a
GPU timeout leaves every pane holding its last frame, or throwing on the render thread,
with nothing said.

Two things are owed, in this order.

**Unregistering.** `Register` adds a resource and nothing ever takes one out. A closed
pane's `PresentSurface` is released and stays in the list, and so would an atlas or a
renderer that was replaced. `Recover` would then build a swapchain on a window that no
longer exists and fail mid-recovery. A resource's own `Dispose` should leave the list.

**Asking.** The loop checks `RemovedReason` once a present or a draw has failed, and not
on every frame, because the call costs a round trip and a healthy device never needs it.
On a loss it recovers on the loop's own thread, where the context is used, invalidates
every view, and leaves a line in the log, because QS132's crash report counts recoveries
and a recovery nobody logged is a number with no story behind it.

A device that cannot be recovered at all, because no adapter answers, is a crash of the
DeviceLost kind, and QS72 already reports that as being about the machine.

Falsified when a pane goes black after a driver reset and stays black.

### §QS223 A click on the strip that switches nothing

Found while reading `MainWindow` for QS190. The strip is a `TabControl` whose items
carry only a header. Which tab's panes are on screen is decided by `Active` and nothing
else, and `Active` sets the strip's `SelectedIndex`. Nothing goes the other way: no
handler listens for the strip's selection changing. Read this way, a click on a tab
moves the strip's highlight while the terminal under it stays the tab that was showing.
The keyboard and the palette then type into a session other than the one the highlight
names. Only the chords (Ctrl+Tab, Alt+digit) switch tabs. The `Active` property's own
comment says a click on the strip does the same as they do.

What to build: handle the strip's `SelectionChanged` by setting `Active` to the selected
index. Guard it so the assignment `Active` makes does not re-enter. Add a UI case that
clicks the second tab's header in the guest and reads the focused pane's title from the
accessibility tree.

Confirm it first. A UIA invoke of the second `TabItem` with two tabs open should leave
the window title naming the first tab's session if the reading is right.

Falsified when clicking a tab's header leaves another tab's session on screen and
receiving the keyboard.

## Block H — The reason to leave the incumbent

### §QS75 Where the first four hundred milliseconds go

Cold start is a publishing decision more than a coding one, so it is measured and tuned
here rather than hoped for.

Self-contained, so a user installs one thing and no runtime. ReadyToRun over the startup
path, which removes the JIT cost from precisely the code that runs before the window
appears. Trimming reduces size and is measured rather than assumed — it interacts badly
with reflection, and the interop-heavy parts of this client are where that will bite.

What must not happen before the first frame: parsing the session store, resolving fonts
beyond the one needed, opening a connection, checking for an update, or reading anything
at all from the network. The window appears, and *then* the client does its work. That
ordering is the entire technique.

The graphics device is on the critical path and cannot be deferred, so its creation is
measured separately: an adapter enumeration that walks a slow external GPU is a real and
thoroughly unobvious cost.

Measurement is process start to an interactive local shell, on the named reference
machine, with a cold file cache where that can be arranged and warm where it cannot —
both reported. A single number that does not say which it was is not a measurement.

The result is compared against the incumbent on the same machine, because that
comparison is the actual claim being made.

Falsified when a cold start figure is published without the machine and cache state.

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

### §QS137 The idle figure a connected client owes

QS76 measured what exists and the number is good: 0 ms of core time over 601 seconds, no
system timer raised, and zero presents through a real device. Against MobaXterm's two
processes on the same desk — 3,625 ms and 0.6 % of a core — it is the win the project
claims.

It is also not the figure the budget describes. Figure 4 is a window *connected, with a
shell at a prompt*, and this client cannot be in that state: nothing above the seam
constructs a transport and no pane drives a render loop. What was measured is a WPF
window, the crash guard and a settings read.

Three things are owed once a session can be opened. **A connected session's idle cost**,
which brings keepalive with it — a genuine trade against battery. **Several idle panes
costing what one costs**, which is the occlusion and damage work verified instead of
assumed. **A live loop's GPU occupancy**, since zero draw calls bounds submission and
not residency.

One row of the comparison wants following up on its own: MobaXterm idles in 62.4 MB
against quickshell's 79.4 MB, and quickshell's window is empty. Figure 6 allows 120 MB
for a connected session, so there is 40 MB of headroom for everything a session adds —
which is not much, and is worth knowing before it is spent.

Falsified when the idle figure is quoted for a client that has never held a connection.

### §QS190 Where the window's 200 ms go, measured

QS75 measured the window's constructor as the largest step of a start: 230 ms of 647 on
the reference desk. The first design blamed the collapsed tab strip and find bar. Built
on demand, they moved nothing. `tools/run-startup-vm.ps1` timed eleven guest starts each
way, and the step was 206 ms before, 200 and 168 ms after (one "before" run carried the
change, QS224). Noise; the 40 ms below caps any saving. Not kept.

Marks inside the constructor for one guest run located the time, as warm medians:

- About 100 ms passes before its first line: WPF's own `Window` and the field initialisers.
- About 95 ms goes to setting `ThemeMode` to Fluent. QS75 measured 18 ms on a Debug build
  on the reference desk, so take that figure there again.
- About 40 ms goes to the find bar and the layout.
- The share, the crash guard and the input bindings cost about nothing.

The theme is next. The first frame shows a title bar and a terminal that D3D draws, and
no Fluent control. Applied once that frame is up, the theme leaves the critical path.
The user would see the title bar turn from light to dark, unless DWM is asked for a dark
title bar first. That visible change is this line's decision.

Falsified when the theme, moved after the first frame, does not make `interactive`
measurably earlier in the guest across two runs each way.

### §QS197 A pass too short to time on a hybrid CPU

Found taking QS79's first baseline. Seven replays of `cat-log` through the `parse` arm
read 1,032 to 1,191 MB/s, so the threshold derived from them is 20.9 per cent: the gate
lets a parser regression of a fifth through without a word. The same seven runs put
`emulate` within 6.4 per cent and the warm start within 5.2, so the noise is this arm's
and not the machine's.

The arm is short. At 1.1 GB/s the 32 MB stream takes 28 ms a pass, and the harness keeps
the best of five passes - long enough to be timed, short enough that where the thread
runs decides the number. The reference machine's i7-14700 has eight performance cores
and twelve efficient ones, and a pass the scheduler places on an efficient core is a
pass a fifth slower with nothing about the parser changed.

Two moves, measured one at a time so each earns its place. The replay runs with its
affinity on one performance core and at high priority, which is how the scheduler is
kept out of the figure. And the parse arm replays the stream several times per timed
pass, so a pass lasts long enough that a moment's interruption is a small part of it.
Then the baseline is taken again, and this line says what the threshold became.

Falsified when a baseline's derived threshold for `parse` is still above ten per cent on
the reference machine.

### §QS225 Timing a monitor-covering chain

QS201 found every windowed chain composed on the reference desk, so an echo there costs
two refresh intervals and the non-goal says a windowed client keeps them. The one case
it did not time is the one where Windows offers independent flip: a borderless window
that covers its whole monitor, the shape a maximised or full-screen terminal takes.

The photon tool already has what this needs. Add a host whose window is the monitor's
size, borderless, with the chain at that exact size, and run every arm with the `shown
as` column. If the echoes read independent flip at about one interval, a full-screen
mode is worth designing for figure 1 and the non-goal gets its exception named; if they
stay composed, the exception is removed from the non-goal's sentence.

The run covers the operator's whole screen for a minute and a half, on the host and not
the guest, whose virtual GPU says nothing about the panel. So it is taken only when the
user has cleared the desk for it, never unattended. Falsified when a monitor-covering
chain on the reference desk reports independent flip and the line was still deferred.

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

### §QS129 The log, reachable from inside the window

QS71 built the log and the guarantee that it holds no secret. It did not attach one to
anything a user drives: the app never constructs a transport itself yet — sessions are
started through a delegate the tests supply — so today the only caller that gets a log
is a test.

Three things this owes a person at the terminal. A log **exists** for every session,
opened where the client's own data lives, at the ordinary level, without anybody asking.
Its location is **reachable from the window** — a menu item that opens the folder is
enough, and it is what a support reply can say in one sentence. And the trace is a
**per-session toggle**, on the session rather than global, because the whole point of
the second level is to turn it on for the one host that will not negotiate and leave
every other session cheap.

The toggle is a setting, and this project treats a setting as a surface with a cost, so
it is named for the behaviour and it appears wherever settings are documented. There is
no README today; that is the moment to decide where a user reads about this at all.

The trace is off by default and stays off across a restart: a client that quietly keeps
tracing after somebody diagnosed something once is a client writing a large file nobody
asked for.

Falsified when a running client cannot tell a user where its log is.

### §QS134 Starting a recording from the window

QS73 built the recorder, fed it from the parser stage where the user's keystrokes cannot
reach, and made the window say so in its title. What it did not build is the way to
start one: `SessionPipeline.Start` takes a recording at construction, and nothing at the
window level constructs a session yet — the same hole QS129 names for the log.

That order was deliberate. A recording that could be switched on mid-session is one a
user could be unaware had started, so the decision belongs where the session begins, and
the window's indication is set from the same decision. Wiring a toggle before there was
a session to toggle would have meant inventing a lifecycle to match.

What this owes when the session lifecycle exists: a way to say *record this session* at
the moment it opens, a name for the file that means something later, and a way to stop
one that names the file it wrote so the user can find it. The title changes; a stop
needs the sentence. QS133's `SessionRecording.Limit` is said before starting, and its
`Stopped` event drops the title's mark.

Keep the asymmetry. Starting is a decision made once, in the open; stopping is safe at
any time and can be a keystroke. A recording that can be started by a keystroke is one
somebody starts by accident.

Falsified when a user who has just hit a terminal defect has no way to record the
session that shows it.

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

### §QS210 A campaign nobody has to remember

QS102 built the campaign (`run-fuzz.cmd`) and made every finding fail the suite, and
left the one thing that makes fuzzing continuous rather than occasional: something that
starts a campaign without a person remembering to.

There are three places it could run, and choosing between them is the owner's call
because each is a standing change to somebody's machine or account. A Windows scheduled
task on the reference machine is the cheapest, and it competes with the person at the
desk for the CPU; `-Jobs` should then stay at one, and the window should be overnight.
The VMware guest is quieter, but it is up only while somebody started it, and its runs
would have to be brought back the way `run-tests-vm` brings reports back. A scheduled CI
job runs on a machine that is different every time, which suits fuzzing better than it
suits the performance gate, because a crash is a crash on any machine. It needs the
corpus kept between runs as a cache or an artifact, and the driver fetched under the
pinned hash.

Whichever it is, the run writes a line somewhere a person reads, with the date, the
length, the coverage gained and any finding, so that "it has not run for a month" is
visible.

Falsified when a week passes with no campaign and nothing says so.

### §QS222 The UI suite that passed by doing nothing

`CasesRun.EveryUiCaseInThisRepositoryRuns` reads the desk first, and where it cannot be
observed it calls `Assert.True(true, ...)` and returns. Its own comment says why: xUnit
had no third verdict, so a pass that says it checked nothing was the closest honest
thing.

xunit.v3 has the word now, and QS136 made it matter. Every run ends with a table of what
ran, passed, failed and skipped, and holds skips to `tests/skips.json`. A UI suite that
ran nothing counts there as one passed test. It is exactly the quieter green that table
exists to expose, and it would pass a desk that lost its observer between two runs
without a line anywhere.

So the test should skip with the desk's own reason: `Assert.Skip` with what
`Desk.Read()` says is absent. It then shows in the table with a reason, and it is held
to the budget. The guest can observe, so its runs do not change; a desk that cannot is
told so, every time.

Falsified when a run that drove no UI case counts it as passed.

### §QS224 HEAD only, read from disk

Found while timing QS191. `Send-Tree -CommittedOnly` in `tools/vm-guest.ps1` is
documented as "carry HEAD alone". It takes HEAD's list of files from `git ls-files -c`,
then packs each file with `CreateEntryFromFile` from the working tree on disk. A tracked
file with uncommitted edits therefore goes to the guest edited. A "before" run of an
uncommitted change was the change itself: QS191's HEAD run reported a milestone that
only the uncommitted code writes. A QS190 figure was recorded the same way and had to be
corrected.

What to build: with `-CommittedOnly`, read each file's contents from HEAD rather than
from disk. Either `git archive --format=zip HEAD` straight into the stage, which is one
call and is what HEAD means, or `git show HEAD:<path>` per file. Have the script print
which it carried, as it does now. Untracked files are already excluded.

`run-tests-vm.ps1`, `run-app-vm.ps1` and `run-startup-vm.ps1` all take the switch
through Send-Tree, so one fix covers all three.

Falsified when a guest run with `-CommittedOnly` builds a tracked file's uncommitted
edit.

### §QS226 Clearing the guest clipboard without touching the host's

Twice running on 2026-10-08, with no test failing, the guest suite went red on the skip
budget: the guest's clipboard held a bitmap, so the clipboard case could not put it back
exactly and measured nothing, which is App.Tests' third skip against a budget of two.
The bitmap was the guest's own; the host held text each time, and it came back between
runs, so something in the guest writes it and no code in this repository does.

It was cleared by hand, and in this order because of QS180: VMware shares the clipboard,
so setting the guest's while sharing is on would also overwrite the host's, the very
thing QS180 stopped the suite doing. So `vmrun writeVariable <vmx> runtimeConfig
isolation.tools.copy.disable TRUE` and `paste.disable TRUE` first, then
`[Windows.Forms.Clipboard]::SetText` inside the guest from a script run with
`runProgramInGuest -interactive`, then the suite, then both variables back to FALSE.

What to build: `run-tests-vm.ps1` does exactly that around the run. It reads the guest
clipboard's formats first and does nothing when they are text. When they are not, it
turns sharing off, writes plain text in the guest, runs, and restores sharing in a
`finally`, so a failed run never leaves it off. It says on the console which of the two
it did. Falsified when a guest whose clipboard holds a bitmap runs green and the host
clipboard is unchanged.
