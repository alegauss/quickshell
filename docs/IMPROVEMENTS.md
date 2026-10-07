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

### §QS218 A password has nowhere to be typed

`RemoteShell` offers a session's key file, OpenSSH's default keys and the Windows agent,
and nothing else. A host that takes a password, or asks its own questions through
keyboard-interactive — a one-time code, a push to approve — refuses every connection the
client makes to it, and the user is told only that no method was accepted.

The pieces exist. `SshCredential.Interactive` hands each of the server's prompts to a
callback with whether it may be echoed (QS41), and `SshCredential.Password` takes a
`Secret`. `SecretStore` saves a secret against an endpoint under DPAPI (QS44), and a
session's `Credential` names a saved one. What is missing is the window's half: a prompt
that shows the server's own words, masks what must not be echoed, and offers to remember
the answer in `SecretStore` — and `RemoteShell` offering a remembered password and the
interactive callback after the keys, password last as QS41 orders them.

The prompt is asked on the window's thread, which the connection waits for, the way
`MainWindow.AskHostKey` already is.

Falsified when a session to a host that takes only a password cannot be connected from
the client, or when a password the user chose to remember is asked for again.

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

### §QS177 The pixels no cell owns

A pane is almost never a whole number of cells, and the pixels past the last whole
column and the last whole row are drawn by nothing. The grid is one instance per cell
and nothing clears the target first, so with a flip-discard swapchain those pixels come
back black whatever the scheme says.

Measured on the guest while shipping QS53: the scheme's background read (16, 18, 24)
inside the grid and (0, 0, 0) in the strip under it, about fifteen pixels deep at that
size. On a dark scheme it is a seam a careful eye finds; on a light scheme it is a black
band down the right of every pane and along its bottom. Between two panes split side by
side it is a dark gutter that looks like a divider nobody drew.

The remedy is one clear to the palette's default background before the grid is drawn,
which costs a fill of the target on a frame that is being drawn anyway and adds no frame
to an idle window. Painting the edge cells' own background outward is the alternative
and it is wrong: a host that coloured its last column would find the colour smeared into
pixels it never addressed.

Falsified when a pane whose size is not a whole number of cells shows any pixel outside
its grid that is not the scheme's background.

### §QS183 A clipboard that is busy for a moment

Measured while shipping QS180: on this desk CrossDeviceService and msrdc, the phone-link
service and WSLg's clipboard bridge, each open the clipboard in the moments after it
changes, and one of them sometimes rewrites it. The tests stopped depending on that. The
client still does.

`SystemClipboard.Read` asks once. A paste pressed while another process holds the
clipboard open comes back empty, and an empty paste sends nothing and says nothing, so
the user's `Ctrl+Shift+V` is simply lost; they press it again and it works, and learn
that this client's paste is unreliable. A copy is the same in the other direction:
`Write` fails, `CopySelection` answers empty, and whatever was on the clipboard before
is what the next paste anywhere produces.

A clipboard held by a listener is held for milliseconds, so the move is a bounded retry
inside the two calls, a tenth of a second at most, which no person pressing a chord
would notice. Past that bound the failure is real and should reach the user rather than
vanish; this client has no status bar, so where it lands is part of the work.

Built as a wrapper over the seam QS180 added, the retry is testable without the desk: a
clipboard that refuses the first few asks and then answers, and a paste that must still
send what it holds.

Falsified when a paste pressed while another process holds the clipboard for less than a
tenth of a second sends nothing.

### §QS198 A cursor colour that reaches the pane

Found while shipping QS83. ColourScheme.ApplyTo writes the scheme's cursor into the
pane's Palette, and OSC 12 writes it there too, but CellRenderer draws with its own
CursorColour, which nothing in src sets, so every pane's cursor is Brand.Cursor whatever
the scheme or the host asked for. docs/SETTINGS.md says a scheme sets the cursor colour,
which is not true today. The built-in scheme is also inconsistent with a scheme file: a
file that omits its cursor gets the foreground, while the built-in one's cursor is a
different grey. The move is for the view to hand the palette's cursor to the renderer
each frame, the way cell colours already travel, with a test that sets a scheme's cursor
and reads the colour a block cursor was drawn with. Falsified when a scheme or OSC 12
sets the cursor and the drawn cursor is not that colour.

### §QS203 Stacking what the shaper would not

QS91 draws a cluster as one atlas glyph, shaped whole by DirectWrite. On Consolas the
shaper places the first mark after a base correctly and every later one a whole base
advance to the left, out of the cell. That held for both the Latin and the Common
shaper. So `GlyphRasteriser.Restack` places each mark from the base shaped with that
mark alone.

That is right for marks on different sides, a dot below and an acute above. Two marks on
the same side, an acute and a diaeresis above one vowel as Vietnamese or IPA write them,
are each placed where they would sit alone, so they land on the same pixels.

What to build: after placing each mark alone, read its ink bounds from the rasterised
pair, and push a later mark above, or below, the ink of the earlier ones on its side by
that height plus a pixel. The side is the sign of where the ink sits against the base's
x-height. Then check the shaper again first: if a newer DirectWrite, or a face with
mark-to-mark positioning, places the second mark right, `Restack` should step aside for
it rather than override it.

Falsified when `a` with U+0301 and U+0308 reads back two separate inks above the a.

### §QS205 A movement that respects the region

Found by QS33's vttest run, cuts 014 and 016. vttest turns origin mode on, sets the
region to rows 12 and 13, and moves with `CSI 24 B` and later `CSI 24 A`, expecting both
to stop at the region's edge. Here `CSI A` clamps only to row 0 and `CSI B` only to the
last row, so the cursor leaves the region and the soft-scroll test writes over row 1.

What to build: CUU stops at the top margin when the cursor starts at or below it, and
CUD at the bottom margin when it starts at or above it, which is DEC's rule and xterm's.
A cursor already outside the region moves to the screen's edge as now. CNL and CPL are
the same movement plus a carriage return and take the same clamp.

Falsified when vttest's cuts 014 and 016 still disagree with xterm after the change, or
when esctest's CUU and CUD sections lose a test.

### §QS206 The save that keeps the character set

Found by QS33's vttest run, cut 022. vttest designates the DEC special graphics set into
G0, saves the cursor with `ESC 7`, switches G0 back to ASCII, and restores with `ESC 8`,
expecting the line-drawing set to come back with the position. DEC's DECSC saves the
character set designations and the shift state with the cursor, and xterm restores them.
Here they are not saved, so half of every line-drawing run prints as the letter q.

What to build: SaveCursor keeps the four designations and which set is shifted in,
beside what it keeps already, and RestoreCursor puts them back. The alternate screen's
save is the same structure and takes the same fields.

Falsified when cut 022 still shows a q.

### §QS207 Insert mode

Found by QS33's vttest run, cuts 025 and 026. `CSI 4 h` turns on insert mode, in which a
printed character pushes the rest of the row right instead of overwriting it, and the
character pushed off the right margin is lost. vttest prints 78 stars in insert mode in
front of a B and expects the B at the right edge; here the stars overwrite it. Cut 026
only inherits that row.

What to build: IRM as a mode bit, set and reset by SM and RM 4, and in PrintCluster an
insert of the character's width at the cursor before the write, using the buffer's
existing InsertCells so damage is recorded. A wide character inserts two cells. DECRQM
(QS104) reports it once both exist.

Falsified when cut 025 does not end in B.

### §QS208 132 columns, obeyed or refused

Found by QS33's vttest run, cuts 010, 012 and 030. `CSI ? 3 h` asks for 132 columns; the
xterm in the oracle obeys and redraws, and this emulator ignores it, so every 132-column
screen vttest draws is read here as 80 columns of it.

This is a choice before it is a build. Obeying means the client resizes its window, or
its grid, when a host says so, which a tabbed and split client has no single answer for.
xterm itself ignores the sequence unless allowC132 is set, and many modern terminals do
the same. Ignoring it entirely also drops the side effects DECCOLM carries everywhere:
clear the screen, home the cursor, reset the margins.

What to decide, then build: either obey within the pane by reflowing to 132 columns, or
keep ignoring the width and still perform the clear, home and margin reset. The second
is small and is what xterm does with allowC132 set and no room to grow. If the answer is
to ignore it, that is a non-goal and this line retires into it.

Falsified when cut 010 is decided against without a line in the non-goals saying so.

### §QS211 A judge with nothing in between

Found shipping QS103. The emulator answered DECRQCRA correctly in its own tests, and the
esctest run still read every checksum as zero. A probe sent `A`, then DECRQCRA, then DA1
from WSL through the client's own `ConPtyChannel` and recorded what reached the
emulator. It received `ESC P 7 ! ~ 0000 ESC \` and `ESC [ ? 61;6;7;… c` as text: the
console host behind the pseudo-console had answered both queries itself and the tty
echoed its answers. The queries never arrived.

So `tools/Quickshell.Conformance` measures conhost for every sequence conhost
intercepts: the device attributes, the cursor and status reports, DECRQCRA and likely
DECRQM. The 151 of 568 in `docs/measurements/esctest-xps.md` is that mixture, and no
change to the emulator can move the part that is conhost's.

What to build, in order of preference. First, try `PSEUDOCONSOLE_PASSTHROUGH_MODE` (0x8)
in `CreatePseudoConsole` for the conformance run only, and record whether this Windows
build honours it; the in-box console host may not, since the flag arrived with the
console host Windows Terminal ships. Second, take conhost out entirely: esctest runs in
WSL on a Linux pty whose other end is a socket the conformance tool reads and writes, so
every byte esctest sends reaches the emulator and every reply goes back unchanged.

Then rerun esctest and rewrite the measurement, naming which path judged it.

Falsified when an esctest reply is produced by anything other than this emulator.

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

What to do first is make it say where: on failure, repeat the second pass under an
allocation listener (an `EventListener` on the runtime's `GCAllocationTick`, or a
`dotnet-trace` session in the guest) and report the type allocated, so a red run carries
its own diagnosis instead of a number. Then fix whatever that names — the other four
recordings never did this, so it is likely a path only a resize stream reaches.

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

## Block D — The tree a user organises work in

### §QS179 A fleet chosen once

QS53's design names three targets for broadcast typing: the panes in this tab, a
selection the user made, or a saved group. The tab shipped first because it needs
nothing but the panes on screen. A saved group needs the one thing this client cannot
yet do, which is read the session store from the running program, and that is QS121.

A group is a named set of saved sessions. Opening it opens each as a pane in one new tab
and turns broadcasting on for that tab, so the target is still the panes in front of the
user and the outline QS53 draws is still what says which panes receive. What the group
adds is that the fleet is chosen once and kept, rather than re-split by hand every
morning.

It never broadcasts across tabs and never to a session that is not on screen: the whole
argument of QS53 is that a keystroke reaches only what is visibly marked, and a group
does not change that.

Falsified when opening a group leaves any of its sessions out of the tab, or broadcasts
to a pane that is not one of them.

## Block E — SCP and SFTP as a thing a person operates

### §QS184 A pane that goes where the shell is

Carried out of QS60's design: the remote pane follows the session's working directory
wherever the shell reports one. The emulator already records it —
`Emulator.WorkingDirectory` is what OSC 7 writes — so the reading exists and nothing
uses it.

Following means two things and only two. The browser opens where the shell is rather
than at the account's home, because a user who has `cd`'d into a deployment directory
and opens the browser is looking for that directory. And while the browser is open, a
change the shell reports moves the pane — unless the user has navigated the pane
themselves since, because a pane that jumped away from where somebody was reading would
be the browser taking the directory out of their hands.

OSC 7 carries a URL, `file://host/path`, and the host in it is the shell's idea of its
own name, which is not always the name the session connected to. A path is followed only
when the host matches the session's or is empty; a shell reporting another machine's
directory, which is what a nested `ssh` does, is ignored rather than listed on the wrong
server.

A shell that reports nothing — most do not without a line in their profile — leaves the
pane at home, and the pane does not guess.

Falsified when the browser opens at home while the shell has reported a different
directory of the same host.

### §QS185 A save that lands on the server

Carried out of QS60's design, which argued for it: editing a configuration file on a
server is why most people open a file browser at all, and the round trip it replaces —
download, edit, upload, and hope the upload went to the same path — is what they are
trying to stop doing by hand.

Opening a remote file downloads it to a temporary directory this client owns, opens it
in the program Windows associates with it, and watches the copy. Each save is uploaded
to the path it came from over the session's own file channel, and the pane says whether
it landed. A save while the previous upload is still running waits for it rather than
racing it.

Two things make this more than a watcher. The file on the server may have changed since
it was opened, and an upload that silently overwrote somebody else's edit is the worst
outcome there is, so its modification time is checked before every write-back and a
change is a question. And the temporary copy is the user's text: it goes when the
session ends, never before its upload landed.

It waits on QS60's operations, since writing a file back is one of them with a watcher
in front, and on a tab that holds an SSH session, which is QS126.

Falsified when a save in the local editor does not reach the server, or reaches it over
a change somebody else made in the meantime without asking.

### §QS186 A copy you can watch and stop

QS60's operations run a copy through `TransferQueue`, and the browser says one thing
about it: a sentence when it is over. The queue knows much more — each entry's bytes
against its length, a rate over a short window rather than since the start, and whether
it is waiting, running, paused, failed or skipped — and it can pause, cancel and retry
any entry or all of them. None of that reaches the window.

So a tree of a few gigabytes over a slow link is a browser that shows nothing for
minutes, indistinguishable from one that hung, and the only way to stop it is to close
the window. That is the shape a person gives up on and repeats from a shell.

The move is a strip under the panes that exists only while a copy does: one line per
copy with what is moving, how far it has got, the rate and the time left, and a button
that stops it. A failed entry stays there with its reason and a retry instead of being
folded into the closing sentence. The queue does not change; this is its state, read at
the rate the strip is drawn.

It answers to the block's criterion that an interrupted transfer resumes without
producing a file unlike the source: stopping from the strip is an interruption, and the
retry is the resume.

Falsified when a copy that has moved bytes for a second shows no progress in the
browser.

### §QS188 A drag that starts on the server

Carried out of QS64's design, which shipped the drops into the client and left the
direction out of it. Dragging entries out of the host's pane onto Explorer or the
desktop is a download, and it is the harder direction: Windows asks for the data during
the drop, not afterwards, and a server over a slow link cannot answer in the time a drop
is allowed to take.

The mechanism is deferred rendering. The pane offers a data object carrying
`CFSTR_FILEDESCRIPTOR` — the names, sizes and times, which it already has from the
listing — and `CFSTR_FILECONTENTS`, whose stream for each file is read from the
session's file channel only when Explorer asks for it. A file large enough that reading
it would stall the drop is not read there: it is queued as an ordinary download into the
directory the drop landed in, and the drop completes against a placeholder the queue
replaces, which is how the queue's guarantee about half-written files keeps holding.

The same drag source is what dragging between two browsers needs, one per tab, on two
hosts. That copy goes through this client, and it says so, because a user may reasonably
have assumed the two servers were talking to each other.

Falsified when dragging a file from the host's pane onto a local folder does not leave
that file there, byte for byte.

### §QS189 A drop that sends the file

Carried out of QS64's design: a drop onto a terminal types the dropped paths, and a
modifier held while dropping turns it into a transfer into the shell's working directory
instead. The typing shipped; the transfer cannot, because it needs two things this
client does not have yet — a tab whose session is SSH, which is QS126, and a directory
the shell has said it is in.

The directory is the emulator's reading of OSC 7, which QS184 uses for the same purpose
and which a shell reports only when its profile asks it to. Where nothing has been
reported, a modified drop says so in the pane rather than uploading to a guess: the
account's home is the likeliest wrong directory there is, because it is where the file
would land without anybody noticing.

Which modifier is the one decision here. Shift is what Explorer uses to change a drop's
meaning, and the reference says what it does on a terminal next to the plain drop,
rather than leaving the difference to be discovered by a user who happened to be holding
it.

Falsified when a file dropped with the modifier onto an SSH tab whose shell reported its
directory does not arrive in that directory.

### §QS219 A browser with a connection beside it and nothing listed

QS60's file browser has a remote half that lists over a session's file channel, proven
on fifty thousand entries, and it has never been opened on anything:
`MainWindow.RemoteFiles` asks a tab for its remote side, and until QS126 no tab held an
SSH session to answer with. Now one can — `RemoteShell.Transport` is the connection
behind an SSH tab — and the browser still opens with an empty remote pane on it.

What to build: the program sets `RemoteFiles` to answer, for a tab whose focused pane
runs a `RemoteShell`, a remote side over `Transport.OpenFileTransferAsync` — a channel
of the same connection, never a second one (QS59), so a hardware token is touched once.
A local tab still answers nothing and the browser says so, as it does today. The channel
closes with the browser, not with the shell, and a dropped connection ends the browser's
listing with the reason rather than leaving it hung.

Falsified when the browser opened over an SSH tab lists nothing from that host, or opens
a second connection to list it.

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

### §QS164 The device per pane that splitting made cheap to ask for

`TerminalLeaf.Open` attaches a view, and a view opens a `GraphicsDevice`, a
`GlyphAtlas`, two shaders and a swapchain. Before QS48 a client had one. Now it has one
per pane, and a pane is Ctrl+Shift+backslash away.

Block G's criterion says sixteen open panes share one glyph atlas, and QS49 is the line
that makes them. What changed is not the plan but the exposure: the criterion was a
claim about a future with splits in it, and the splits arrived first. Sixteen panes
today is sixteen devices, sixteen atlases and sixteen copies of the same rasterised
font, and nothing between a user and that but their patience.

Two things follow and only one of them is QS49. The sharing is QS49's. The other is that
this client has no ceiling anywhere: `--panes` clamps at sixteen because a command line
is a surface a script drives, and the chord clamps at nothing at all. A ceiling is not a
substitute for the sharing, but it is what stops a held key from being a way to exhaust
a graphics driver.

Falsified when a client with sixteen panes open holds sixteen devices.

### §QS168 The font that is built into an atlas and a grid

QS50's design says changes apply live, and gives the reason in the same breath: *a font
size that needs a restart is a font size nobody experiments with, and experimenting is
the entire reason to expose it.* The cursor and the blink do apply live, because the
loop reads both every frame. The font does not.

It cannot yet, and QS49 is why rather than an oversight. The atlas was rasterised at a
size, the cell was measured from it, and every pane's grid came out of that measurement
— so a new font is a new atlas, a new set of metrics, and a resize of every pane in the
process at once. All three belong to the share, which is exactly the right place for
them and exactly why a leaf cannot do it alone.

What the work is: the share rebuilds its atlas and its renderer, every view takes the
new metrics and recomputes its grid, and each grid change goes to its model and out to
its far end the way QS32 settled. The last part is already built — `GridChanged` does it
for a window drag — so this is a new reason for a path that exists.

Falsified when a font size changed in the file needs a restart to be seen.

### §QS170 A window over the file, and the file still in charge

QS50's design asks for both halves: *settings are a file the user can edit and a UI over
that same file, with the file as the source of truth.* The file landed, documented,
applying live and keeping the notes a user writes in it. The window did not.

The hard prerequisite is done and it is the one that made the sentence conditional —
*the UI writes it back preserving comments, or the UI is not worth having.*
`SettingsFile.WriteTo` edits values where they sit, so a window over it inherits that
for free.

What is left is the window, and it is small if it stays small: one dialog on a chord,
one control per documented key, and a write on each change rather than an OK button —
because the file is the truth and a dialog holding unsaved state is a second copy of it.
It also has an obligation the file does not: every control has to say what the key
means, which is `docs/SETTINGS.md`'s sentences and not new prose, or the two drift.

What it must not become is the place new settings appear. A control is cheaper to add
than a key, which is exactly why the reference test is on the key.

Falsified when a setting can be changed in the window and not in the file.

### §QS171 Chords built on keys that are not where you left them

Ctrl+Shift+\ is bound to both `Key.OemBackslash` and `Key.Oem5` because those are the
same character on different physical keyboards and binding one would have worked on half
of them. Ctrl+Shift+- is bound to `Key.OemMinus` alone. Nothing decided that asymmetry;
the first binding hedged because somebody thought about it and the second did not.

The failure is quiet in the way that matters: a user on a layout where the key is
elsewhere presses the chord, the terminal receives nothing the client claims, and the
character they pressed goes to the remote program instead. So the client both fails to
split and types something. There is no error and nothing to search for.

Oem keys are the only ones with this problem. Letters, digits and the named keys are the
same `Key` value everywhere; the Oem range is defined by position on a US keyboard and
every layout that differs remaps it.

What is wanted is a decision rather than more bindings: either the split chords move off
Oem keys entirely, or every Oem chord is bound across the values it takes on the layouts
this client supports, and which layouts those are is written down. The keys reference
QS162 wrote is where the answer belongs, because it is already the one list of what this
client takes.

Falsified when a chord in the reference does not fire on a supported layout.

### §QS172 The one chord a user guesses, pointed at the wrong thing

Ctrl+Shift+F1 collects a diagnostic report. The comment where it is bound argues F1
because *that is where a person looks for help* — which is exactly the argument for it
not being this. A user who has never read anything about this client and wants to know
what it can do will press it, and will be shown a folder of logs.

QS162 wrote docs/KEYS.md, so there is now something to show. It is a file in the
repository that a user who installed a binary has no path to at all, which makes the
reference half a surface: it answers the question for anybody reading the source and
nobody else.

The cheap answer is that help is the chord and the report moves. The report is a
maintenance action reached deliberately, and moving it costs nothing because nobody has
it in their fingers yet — this client has no users. Delaying is what makes it expensive.

What help opens is the smaller question and should stay small: the keys, and a way to
reach the settings file. Not a manual, not a window this client has to lay out. A shell
that opens the reference and a chrome-free view of the same table are both defensible; a
help system is not, and would be the surface Block G exists to refuse.

Falsified when a user presses the help chord and is shown something that is not help.

### §QS174 The difference between broken and misconfigured

Three things in the settings path fail quietly by design, and each design is right on
its own. A file that will not parse loads as the defaults, because overwriting what
somebody was editing is worse. A scheme path leading nowhere is the built-in scheme,
because refusing to start over a typo is unusable. A scheme with unreadable colours
loads as written, because that is the user's choice.

Together they add up to a client that answers every mistake by looking normal. Somebody
who sets `colourScheme` to a path with a typo in it sees the colours they had before,
and has no way to tell whether the client read their file, read it and failed, or
ignored the key entirely. The one reading available to them is that the feature does not
work.

What is wanted is small and is not a dialog. The settings read already knows what it
could not use — unrecognised keys are kept, and scheme resolution already returns
nothing on failure — so the missing piece is somewhere for those to go. The diagnostic
report is where they go today, which is the right place for the detail and the wrong
place for the first hint, since nobody opens it before they already suspect something.

Not a notification system. One line, in one place, that a user looking for it can find.

Falsified when a settings value this client could not use leaves no trace a user can
find.

### §QS178 A surface with no reference

The client has no menu on purpose, and the command line is where that decision put
everything a script or another program might ask of it: `--tabs`, `--panes`,
`--broadcast`, `--import [file]` and `--palette` today. Each is parsed where it is used
in the entry point, and each is explained by a comment beside that line, which is the
one place a user will never read.

KEYS.md solved the same problem for chords, and the shape carries over: the flags become
one list in code that the entry point reads rather than five separate lookups, and a
test holds a reference page to that list in both directions, so a flag nobody documented
fails the build and so does a flag the page describes that nothing parses.

Nothing about this is a help screen. The client is a windowed program with no console to
print one into, and a `--help` that opened a dialog would be the client choosing to put
a window in front of a script.

Falsified when the entry point acts on a flag the reference does not name.

### §QS199 One model of the terminal's colours

Found while shipping QS83. Appearance carries a TerminalPalette with a foreground,
background, cursor and selection, and
WindowTests.TheChromesThemeDoesNotTouchTheTerminalsColours asserts that changing the
chrome theme leaves it alone. Nothing at run time reads it: panes take their colours
from Settings.Colours through ColourScheme.ApplyTo, and MainWindow never passes its
Appearance palette anywhere. So the test proves an invariant about a model no pixel
comes from, and the invariant that matters, that a theme change leaves a pane's scheme
alone, is untested. The move is to delete TerminalPalette and Appearance.Palette, and to
rewrite the test against the real path: apply settings with another theme and read that
an open pane's palette is unchanged. Falsified when a model of the terminal's colours
exists that no pane draws from.

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

A UI case reads it off the accessibility tree: the entry is listed, and typing part of a
session's name brings that session to the top.

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
every run. Frame cost joins the gate once QS196 has measured it.

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

### §QS190 Building what the first frame shows, and nothing else

Measured by QS75 on the reference machine: the release publish reaches the prompt in 647
ms warm, and the main window's constructor is the largest single step in that, 230 ms.
Taking the theme assignment out of it saved 18 ms, so the cost is not the Fluent theme.
It is WPF being asked to build, before the first paint, a window's worth of things the
first paint does not show.

The constructor builds a `TabControl` whose strip is collapsed while there is one tab,
and a find bar — a text box, three buttons and their templates — that is collapsed until
somebody presses `Ctrl+Shift+F`. Both are the window's argument about what is on screen
by default, and both are paid for at start-up by every user who never opens either.

The move is to build each the first time it is needed: the strip when a second tab
opens, the find bar when it is asked for. The window's layout keeps a place for each so
nothing shifts when it arrives. What a test checks about them — that the strip appears
with a second tab, that the find bar takes the keyboard — does not change, and neither
does anything a user sees.

It is measured with `tools/Quickshell.Startup` before and after, on the same publish,
and the result goes into `benchmarks/results/startup-h.md` beside the figure it moved.

Falsified when the release publish's warm start is not measurably earlier at
`constructed` once the hidden chrome is built on demand.

### §QS191 Three slow things, in parallel

Measured by QS75 on the reference machine: in the release publish's 647 ms warm start
the shell is started at 498 ms and the graphics device is ready at 617 ms, and both wait
for the window — the shell for the synchronous work in the entry point after `Show()`,
the device for the first layout. Neither needs the window for most of what it does.

The device, the glyph atlas and the compiled shaders are process-wide since QS49 and ask
nothing of a window: only the swapchain needs a handle. Opened on their own thread as
the process starts, they would be ready while WPF spends its 230 ms building the first
window. The pseudo-console and the shell are the same: nothing about them depends on a
pixel, and cmd's start could overlap WPF's.

What cannot move is the order a user sees: the window first, then the terminal in it.
Starting work earlier changes nothing about that; it only stops two slow things from
queueing behind a third.

The risk is a failure surfacing earlier than there is a window to report it in, which
the crash guard already covers by being armed before the window exists.

Measured with `tools/Quickshell.Startup` before and after, and recorded in
`benchmarks/results/startup-h.md`.

Falsified when the device is still created after the first layout, or the shell still
started after the window is shown, in a timed start of the release publish.

### §QS192 Bringing a portable copy's sessions into the installed one

Found while shipping QS77. Installing from a portable copy copies its program files and
deliberately leaves the marker and `data\` behind, so the copy it came from keeps
working. That is right for the copy and wrong for the person: the natural path through
this client is to unzip it, try it, import the MobaXterm sessions, and install it once
it has earned that. The installed copy then starts in `%AppData%\quickshell` with no
sessions and no settings, and says nothing about where they went.

The move is one copy, once. When the copy being installed is portable and the installed
copy's settings folder does not exist yet, the portable `data\` is copied into it: the
settings, the saved sessions and the window placements. Never the logs, the crash
reports or the recordings, which describe the other copy.

Where the installed copy's folder already exists, nothing is copied and nothing is
merged. Two sets of sessions are a decision a person makes, and the finished install's
sentence says where the portable ones are instead. Either way that sentence says which
happened, so somebody who expected their sessions knows whether to look for them.

Falsified when a portable copy holding saved sessions installs into a profile with no
settings folder, and the installed copy starts without them.

### §QS196 A frame, timed on both sides

Found building QS79's gate. Figure 3 of the budget is steady-state frame cost: one
filled 200 by 50 grid redrawn continuously, under 2 ms of GPU and CPU time on the
reference machine, and never the first frame. Nothing in this repository measures it.
The replay harness's `render` arm reports megabytes of stream per second through the
glyph path, which is a throughput and folds parsing in; the render tests assert what is
drawn and that an idle pane draws nothing. So the gate holds parse, emulate and start,
and the figure a second pane most depends on is the one it cannot hold.

The measurement belongs in the replay harness beside `render`, as its own arm: a grid
filled once from a real stream and then drawn a few thousand times without new input,
timed on the CPU around each `Draw` and on the GPU with timestamp queries around the
same work, so the two halves of the figure are read separately. It never presents, for
the reason the render arm never does: a vsync-locked present measures the display. The
report gives the median and the 99th percentile per frame, since a frame budget is
broken by the slow frame and not by the average one.

Once it exists the gate reads it as a fourth figure, lower being better, with a
threshold derived from its samples like the others.

Falsified when figure 3 is quoted anywhere without a run of that arm behind it.

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

### §QS200 Timing the bytes that ship

Found by the QS79 review. release.cmd hands the gate the published client, but only the
start figure is measured on it. Parse and emulate come from Quickshell.Replay built from
the same source in Release, framework-dependent and with its own runtimeconfig. A
setting in the App project or in the publish command, such as TieredPGO off or a
different GC mode, changes what ships and not what the gate measures. PERFORMANCE.md now
says so plainly. The move is to run the replay arms against the published assemblies:
publish the harness beside the client with the same runtime settings, or load the arms
from the publish folder, then take a new baseline. Falsified when a runtime setting
added only to the client's publish slows its emulate path and the release gate still
reports emulate held.

### §QS201 Where the second refresh interval goes

QS86's run, in `benchmarks/results/photon-h.md`: at a prompt, with the queue empty, an
echo reaches DXGI's vblank in a median of 33 ms on the 60 Hz panel, two intervals, in
every swapchain arm. Waiting for the next vblank explains half an interval on average,
so about one whole interval is spent after the present.

That interval is the part a faster panel does not remove. At 120 Hz the same shape is
about 17 ms, twice the 8.3 ms budget, so figure 1 fails on the reference machine's
successor as well as on this one unless the second interval goes.

The likely owner is composition. A windowed flip-model chain the compositor draws into
the desktop is shown a frame after it is presented. Independent flip, or a hardware
overlay plane, shows it at the next vblank, and the compositor grants those only under
conditions of its own: the swapchain covering its window, no transform, and a host that
does not redirect the window.

What to build first: report the presentation mode beside the latency, from PresentMon or
the DXGI and DWM events it reads, so each arm says composed or independent flip. Then
time the client's real window host, the WPF child HWND, in the same tool, since the
photon tool's popup is not that host. Only then choose the change, whether a
DirectComposition visual, the child window's styles, or a non-goal that says 60 Hz
composition is the floor.

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

### §QS216 An import that replaces instead of adding

`MainWindow.ImportSessions` writes `SessionTree.Of(preview.Tree())` to the store's path,
which is the imported tree and nothing else. Whatever the store held before — sessions
made through the dialog since QS121, or a store edited by hand — is gone, and the
preview the user agreed to said only what would be imported, not what would be lost.

Until QS121 nothing else wrote the store, so an import landing on an empty file was the
only case there was. Now a user can make sessions, import from MobaXterm a week later to
pick up the ones they forgot, and lose every session they made here.

What to build: the import goes into the store as it is, read at the moment of writing,
the way `NewSession` writes. The imported sessions go under a folder of their own —
"Imported from MobaXterm", with the date — so they never collide by name with what is
already there, and moving them out is the user's decision. The preview says how many
sessions the store already has and that they stay. Comments survive through `StoreText`
as for any write.

Falsified when a store holding sessions, imported into and agreed to, holds fewer
sessions afterwards than it did before plus what was imported.

## Block K — The build and the harness — what a green run is evidence of

### §QS167 The allocation that was there once

`KeyTests.EncodingAKeyAllocatesNothing` failed in the guest during QS166's run, passed
on this machine immediately afterwards, and passed on the guest's very next run of the
same tree. Nothing between those three runs changed a byte of what it tests.

An allocation assertion is a measurement, and this one is being read as an assertion.
`GC.GetAllocatedBytesForCurrentThread` counts everything the thread allocated, which on
a first call through a path includes whatever the runtime did on the way — a
tiered-compilation rejit, a lazily built static, a resized thread-local buffer. On a
warm machine that is nothing; on a cold guest under a full suite it is sometimes not.

What the test means to say is that the encoding path allocates nothing per call, and
there are ways to say that which do not depend on when the JIT got round to things: warm
the path first and measure the second run of it, or measure many calls and divide, or
assert against a small ceiling rather than zero and say in the message what the ceiling
is for.

What must not happen is the ceiling quietly becoming a budget. Zero is the claim; the
noise is the runtime's, and the fix is to stop measuring the runtime.

Seen again on 2026-10-07: the `tmux-resize` replay allocated 7,288 bytes in a guest run
and none in three host runs, and that test already measures a warmed second pass.

Falsified when the same tree gives two verdicts on two runs of the same machine.

### §QS176 A package a version behind the reason for its comments

`Quickshell.Cases` references Winwright 0.1.0-alpha.3. WW317 — a chord that `press` can
spell — shipped in the engine's own source after that, so the version this repository
restores refuses `Ctrl+Shift+P` and names the traversal keys it does take.

The cost is not the refusal, which is clear. It is that three case files now carry
comments explaining that a flag was used *because the engine cannot do chords*, and that
sentence became false without any of them changing. QS52's case was written against the
chord first, on the strength of the engine's changelog, and only the run said otherwise.

A case that opens the palette with `--palette` is checking a window this client can
build. A case that opens it with `Ctrl+Shift+P` is checking the route a user takes, and
for a palette — a surface whose entire purpose is the keyboard — that is the more
interesting half by a distance. The same holds for `--tabs` and `--import`; QS53's
broadcast case could not be written at all, this version refusing the `description`
reading a pane's state is in.

The move is to take a newer engine and delete the three workarounds with their comments.
What makes it a task rather than a version bump is the bootstrap: `packages/` beside the
engine holds alpha.2, this project restores alpha.3 from somewhere else, and nothing
here records which feed that is. Finding out is most of the work.

Falsified when a case explains itself by an engine limitation that no longer exists.

### §QS182 A picture of a desk that was not drawing

The host suite runs with the guest suspended, because a running guest holds the host's
clipboard (QS180), so every picture taken after a suite resumes it.

The resume does not hang; reading its output does. Found while shipping QS77: `vmrun
start ... gui` resumed the guest and exited within seconds, but it had started the
Workstation window, `vmware.exe --fd 1388`, and that window inherited the pipe
`Invoke-VmRun` reads vmrun's output through. PowerShell waits for the end of that pipe,
which comes when the window closes. So the script said it was starting the guest and
then nothing, with vmrun gone and `checkToolsState` answering `running`. A deadline on
the start would only turn every resume into a refusal.

And the picture after it was of nothing. The next `run-app-vm` built the client, let it
draw for twelve seconds and brought back a 3838 by 1841 capture in which every pixel is
black: the desk the guest resumed to draws nothing, most likely a display that went dark
or a session that locked while suspended. The script reported success.

Two moves. `start` sends its output to a file and waits for vmrun's own exit, never
reading through a pipe another process can inherit. And a capture that is one colour
from edge to edge is refused as a picture of nothing, because a black rectangle filed as
evidence is worse than no file.

Falsified when `Connect-Guest` is still waiting after vmrun has exited, or a
single-colour capture exits zero.

### §QS193 The archive built by the pipeline that gates the tree

Found while shipping QS77. `release.cmd` publishes the client self-contained and
ReadyToRun, which is the build QS75 measured and the one people download. CI never runs
it: it builds the solution framework-dependent and runs the suite. A publish fails for
reasons a build never meets - a runtime pack that will not restore, a ReadyToRun compile
error, the SDK refusing a property for WPF as it refused trimming with NETSDK1168 - and
each of those would pass CI and surface on the day of a release.

The move is one step in the workflow that already gates the tree: `release.cmd
-Unsigned` on the Windows runner after the suite, with the archive kept as a workflow
artifact for a few days. The archive becomes something the pipeline proves on every
push, and a reviewer can take exactly what a change would ship without building it.

It stays unsigned there. The signing certificate is the maintainer's, and whether a
pipeline may hold it is a question QS77's remainder settles; until then the archive is
named for what it is, which is the guard `release.cmd` already has.

Falsified when a change that breaks `release.cmd -Unsigned` passes CI.

### §QS195 Plumbing written once per test assembly

Found shipping QS138, whose fault was one mistake in two copies of one helper: the SCP
and the remote-forward tests each carried their own way of running a command on the
server, both looked for a bare newline where a pseudo-terminal sends CRLF, and nothing
could have fixed one and reached the other. Moving both onto a shared `RemoteShell` was
most of the fix.

The rest of the plumbing is copied the same way. Counted on the tree today: sixteen App
test classes carry their own STA runner, sixteen transport classes their own
host-key-trusting callback, seventeen their own skip for a fixture that is not up, and
twenty-one files their own walk up to the repository root. Each copy is a place a fix
has to be remembered, and QS138 is what happens when it is not: the copies agreed on the
fault and differed on the timer.

The move is one small internal file per test assembly for what every class there needs
- the STA runner, the fixture's key, trust and skip, the repository root - and the
classes calling it. No framework and no base class: a test that reads as a sequence of
calls keeps reading that way. Done as one sweep rather than piecemeal, so the diff is
one mechanical change a reviewer can check by reading it once.

Falsified when a fix to how a test reaches the fixture or builds a window has to be made
in more than one file.

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
