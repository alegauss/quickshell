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

### §QS281 An inset around the terminal, in its own colour

Seen 2026-10-12 in the owner's screenshot: the first column of the grid sits about 2px
from the sidebar's divider and the first row against the window's top, where
`Connected.dc.html` gives the terminal `padding: 14px 18px`.

What to build: the terminal area keeps an inset of 14 above and below and 18 left and
right, in the colour scheme's own background so the inset reads as part of the terminal
and not as a frame: a Border around `_terminal` whose Background follows the scheme the
focused pane wears (QS245's own scheme where a session sets one), changed with it. The
grid is laid out inside the inset, so the column and row counts the host is told shrink
by exactly what the inset takes; split panes keep their 4px dividers inside it, and zoom
fills it. The pointer, selection and drop coordinates stay in the pane's own space,
which the inset does not move.

Tests: WindowTests read the canvas's offset from the cell (18, 14) and that its
background equals the scheme's background after a scheme change; the grid size at a
known window size drops by the expected columns and rows. A run-app-vm.cmd picture for
the owner.

## Block D — The tree a user organises work in

### §QS275 The root's name is never part of a path

Found 2026-10-12 on the owner's own store (`%AppData%\quickshell\sessions.json`), whose
root node is `"Name": "imported"`, written by an early MobaXterm import. Every session
row in the sidebar, double-clicked, ends in "There is no saved session or folder of
sessions called Eztec/ezsv72 (root)": not one of the 42 sessions opens.

The two halves of `SessionTree` disagree about the root. `Walk` (behind `Sessions()`,
`Session(path)`, `Search` and `Group`) starts a path with the root's own name, so the
session resolves as `imported/Eztec/ezsv72 (root)`. `Find`, `Inherits`, `With` and
`SessionsView.Rows` start below the root, so the sidebar, the editor and every write say
`Eztec/ezsv72 (root)`. A probe over a copy of the store resolved all 42 sidebar paths to
null.

What to build: the root's name is never part of a path. `Walk` is called with the root
treated as unnamed (start each child at the empty path), whatever the file says; the
name is kept on write so a hand edit is not rewritten. A test reads a store whose root
is named and asserts, for every row `SessionsView.Rows` lists, that `Session(row.Path)`
is not null and that `Find`, `Inherits` and `Session` agree on it; another asserts the
palette's Open session opens the same path.

Falsified when a session listed in the sidebar of any readable store does not open.

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

### §QS272 A Home tab

Approved on 2026-10-11 (`docs/design/friendly-shell/`, `Main.dc.html`,
`HomeDark.dc.html`). The Home page landed first (2026-10-12): `HomePage.cs`, shown by
`MainWindow.ShowHome` from the palette's Home, `--home` and the strip's Home button,
with quick connect, recent sessions as cards, and three ways to start. While it shows,
`Current` is null, so no pane is drawn and nothing typed reaches a shell.

What is left: Home as the screen a new window opens on. A setting, `startWith`: `home`
by default, `shell` for a local shell as today, in `Settings`, `SettingsFile`,
`docs/SETTINGS.md` and the settings window. A window started with `--session`, `--tabs`
or `--panes` opens on what it asked for. With `home`, `Program.Main` must not start the
early shell (QS191): read `startWith` inside the pool task that starts it, before the
pseudo-console, and give the window a Home and no tab.

Then the measurement. `StartupTimeline` reports at the first shell frame, which a Home
start never reaches, so the startup tool, the gate and `install-vm.ps1` would wait
forever. Mark `home` and write the report when Home is first rendered; keep figure 5
timing the local shell by having the gate pass `--tabs 1`; and time a Home start with
`run-startup-vm.cmd` before and after, the numbers in the commit. The block criterion "A
default installation shows no chrome beyond a title bar and a terminal" is untrue once
Home is the default, and is the owner's to restate.

Falsified when a new window's first screen still gives no way to a server without the
palette.

### §QS277 The sidebar, to the design's pixel

Measured 2026-10-12 against `docs/design/friendly-shell/Main.dc.html` (open) and
`Connected.dc.html` (rail), from the owner's screenshot. Sizes in device-independent px.

Open: header row padding 16/16/10. The fold is a ghost button, 28x28, radius 6, no fill
when on, glyph "panel" (Fluent DockLeft), not the accent-filled square hamburger shown
now. "Sessions" 13px SemiBold, TextFillColorSecondary. New: height 28, padding 0 10, gap
6, 12px plus glyph, 12px SemiBold, accent fill. Search: height 32, radius 6, 1px border,
a 14px magnifier inside at left 10, placeholder 13px.

Rows: list padding 4/8, rows 1px apart, radius 6. A folder row is 32 high: 10px chevron
(rotated 90 when open), 15px folder glyph, name 13 SemiBold, what it hands down 11px
right. A session row is 32 high at indent 30 (8 at the top level): 15px terminal glyph
drawn as a rect and a chevron, never the "C:\" glyph used now, which reads as a local
shell; name 13 over address 11, line-height 1.2, ellipsised; Connected chip 11px.
Selected fill is the accent at 10% alpha, hover SubtleFillColorSecondary, not the opaque
block shown now. Rows keep 8px clear of the scrollbar.

Footer: top divider, padding 10/16/14, buttons 32 high, 15px glyph, gap 10, 13px.

Rail (52 wide): 36x36 ghost buttons, radius 6, 18px glyphs, 4 apart from top 10: Show
sessions, Files on this server, Tunnels; with a remote tab focused the latter two act on
it.

Evidence: SessionsSidebarTests reading each size above off the element tree, and a
run-app-vm.cmd picture for the owner to compare.

### §QS278 The tab strip the design draws

Measured 2026-10-12 against `Main.dc.html` and `Connected.dc.html`. The strip today is
WPF's stock TabControl, hidden with one tab, and Home is a separate button beside it
(QS272's first part). The design draws one strip.

The row: 42 high, padding 0 12, items aligned to the bottom, 2 apart, a 1px divider
under it. Home is the first tab: 15px house glyph, the word Home, no close button. Each
tab is 38 high, padding 0 12 0 16, gap 8: an 8px status dot (filled
SystemFillColorSuccess while connected, caution while reconnecting, a 1.5px hollow
TextFillColorTertiary ring for a local shell or one that ended), the title, and a 22x22
ghost close button with an 8px cross. The selected tab is SemiBold,
TextFillColorPrimary, with a 3px accent underline inset 10 each side at bottom -1 and
radius 2; others are Regular, TextFillColorSecondary. Then a 30x28 ghost "+" (New tab,
radius 6, 5 from the bottom). At the far right a ghost "Commands" button, 28 high, 14px
magnifier, 12px text, and a keycap "Ctrl+Shift+P" (11px, 1px border, radius 4) that
opens the palette.

The strip shows whenever Home is open or two tabs are, which with Home the default is
always. Drag to reorder (QS250), pull down to detach (QS160) and a click on the shown
tab leaving Home (QS272) keep working.

Evidence: tabs.cases.json reads the dot's state through each tab's ItemStatus and the
Home tab's name; a WindowTests check reads the sizes; a run-app-vm.cmd picture for the
owner.

### §QS279 Recent sessions, kept and drawn

Measured 2026-10-12 against `Main.dc.html`. Home's cards (QS272) come from
`MainWindow._recentSessions`, a list in memory per window: after a restart Home shows
the store's first three as "Saved sessions", and no card can say when it was last used.

Kept: the sessions connected last, newest first, at most twenty, each with the time of
its last connection, in a file of the client's own beside the settings
(`%AppData%\quickshell\recent.json`, or `data\` when portable), written when a
connection goes live, never when one is merely asked for. Renaming or deleting a session
from the sidebar or the dialog carries the rename and drops the deleted one, so a card
never opens a path the store lacks. Open session's ranking in the palette (QS217) reads
the same list.

Drawn: "Recent" 16 SemiBold with a "Show all sessions" link at right that focuses the
sidebar's search; three equal columns 12 apart. A card: padding 16, gap 12, radius 8,
1px CardStroke, hover stroke one step stronger; a 34x34 tile, radius 8, on the layer
fill, holding a 17px server glyph in accent; the name SemiBold over user@host in
monospace 12; tag chips 11px (padding 1/7, radius 5, 1px stroke) with the folder as the
first chip; at the bottom, the time in words ("Connected now" while a tab holds it,
"Today", "Yesterday", "Last week", then the date) and a 28-high Connect.

Tests: the list survives a new window; a rename and a delete carry; the time's wording,
from a fixed clock.

### §QS280 Home, to the design's pixel

Measured 2026-10-12 against `Main.dc.html`; `HomePage.cs` and `QuickConnect.cs` (QS271,
QS272) were built to the content, not the layout. Page padding 32/40, sections 28 apart.
The Recent cards are QS279.

Heading: "Connect to a server" 26px Bold, letter-spacing -0.01em; under it, 6 apart,
"Type an address, or pick one of your saved sessions on the left." in
TextFillColorSecondary.

Quick connect is one card (padding 20, radius 8, 1px CardStroke) of one row, bottom
aligned, 12 apart: Address (label 12 SemiBold; a 40-high box with a 16px terminal glyph
at left 12, monospace 14, placeholder "user@host or user@host:port"), then Sign in with
as a 230-wide drop-down ("Key or agent (automatic)", "Password", "A specific key file…",
the last opening a file picker) in place of today's segments, then Connect, 40 high,
padding 0 22, accent, with a 14px arrow. Under the row, 12px secondary: the Save as a
session checkbox, and "Hosts in ~/.ssh/config are offered as you type." Errors stay
under the box.

Start points: three equal cards, 12 apart, padding 14/16, subtle fill, 1px stroke,
radius 8, an 18px accent glyph at left, title SemiBold over 12px secondary: "Bring your
MobaXterm sessions" / "Previewed first; nothing is written until you agree."; "Open a
local shell" / "Command Prompt, PowerShell or WSL in a new tab."; "Browse files" / "Drag
files to and from a server over SFTP."

Evidence: home.cases.json reads the copy and the drop-down's three entries; a
run-app-vm.cmd picture beside Main.dc.html for the owner.

### §QS282 A shell's path, named as the shell

Seen 2026-10-12: the window is titled "C:\WINDOWS\system32\cmd.exe — quickshell", and a
local tab would carry the same path, where the design names a local tab by its shell
("PowerShell" in `Connected.dc.html`). cmd writes its own executable's full path through
OSC as soon as it starts; QS161 deliberately ranks a host-written title above the tab's
host, and that ranking stays.

What to build: one narrow rule in `TerminalLeaf.Title`. Where the title a host wrote is
exactly the full path of an executable (a rooted path ending in `.exe`, nothing after
it), the leaf shows that program's friendly name instead: cmd.exe "Command Prompt",
powershell.exe "Windows PowerShell", pwsh.exe "PowerShell", wsl.exe and bash.exe "WSL",
and otherwise the file's own name without `.exe`. Anything else a host writes, a
directory or a running command included ("Administrator: C:\…", "vim notes.txt"), is
shown as written, and a name the user gave still outranks both. The window's title
follows, as it already does, so the case asserting it contains "quickshell" is
unchanged.

Tests: the mapping for each executable, a rooted path with arguments after it left
alone, a user's name outranking it; tabs.cases.json reads a fresh local tab named
"Command Prompt".

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

### §QS283 The client's own icon

Seen 2026-10-12: the window's caption, the taskbar and Alt+Tab show Windows' generic
application icon. No project in `src` names an `ApplicationIcon` and no `.ico` is in the
repository, so the executable, the Start menu shortcut QS77's installer writes and the
uninstall entry carry the generic one too.

What to build: the mark the design draws in its title bar (`Main.dc.html`: a rounded
terminal frame, a prompt chevron and an underscore, stroke 2 on a 24 grid) as one
`assets\quickshell.ico`, rendered from an SVG kept beside it at 16, 20, 24, 32, 40, 48,
64 and 256 px, with the small sizes hinted to whole pixels rather than scaled down. The
accent in the mockup is the site's violet; the icon is the one place a fixed colour is
right, since Windows draws it outside any theme, so it uses that violet with a light
variant tested against both taskbar themes. `ApplicationIcon` in
`Quickshell.App.csproj`, and `Window.Icon` set from the same resource so every window,
dialogs included, carries it. The installer's shortcut and uninstall entry point at the
executable's icon.

Evidence: a test that the built exe carries an icon resource with every size listed;
`run-install-vm.cmd` reads the shortcut's icon location; a picture for the owner.

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

### §QS276 MobaXterm's path placeholders, expanded

Found 2026-10-12 in the owner's store: imported sessions carry keys such as
`_ProfileDir_\OneDrive\CustomerCerts\EZTEC\EZTEC.ppk`. `_ProfileDir_` is MobaXterm's own
placeholder for the user's profile folder, and `MobaXtermImport.Key` copies it verbatim,
so the file is never found and the session falls back to the default keys and a
password. `RemoteShell.Expand` knows only a leading `~`.

MobaXterm writes these placeholders in a session's key field: `_ProfileDir_` (the
profile, `%USERPROFILE%`), `_CurrentDrive_` (the drive MobaXterm runs from, `C:` where
it is unknown), `_MyDocuments_` (the Documents folder) and `_AppDataDir_` (`%APPDATA%`).
Confirm the list against the MobaXterm.ini fixtures in `cases\fixtures` and the
MobaXterm documentation before relying on it.

What to build: a `MobaXtermPaths.Expand` that replaces each placeholder at the start of
a path, case-insensitively, and is called in two places. On import, so a new store holds
a real path. And in `RemoteShell.Credentials`, before `File.Exists`, so the stores
already imported this way work without being imported again. A key that still does not
exist after expansion is said in the pane ("The key file … named by this session is not
there") rather than silently skipped, since a session that named a key was asking for
that key.

Tests: each placeholder expands; an unknown underscore word is left alone; an imported
fixture line yields a rooted path; a session whose key is missing narrates it.

Falsified when an imported session's key file exists on disk and the connection does not
offer it.

## Block K — The build and the harness — what a green run is evidence of

### §QS273 A case over a connected tab

QS269 shipped the session bar and the status bar over a remote tab. Its design asked for
a UI case reading the state chip's word and the forward chip after a forward opens; none
could be written, because the bars exist only while a pane holds a live SSH session and
no case fixture can depend on a server (filed as WW522 in winwright). The evidence
instead is `RemoteShellTests.TheSessionBarSaysWhereATabGoesAndWhichForwardsItHolds`, an
STA test on a real tab against the Docker sshd fixture, plus `ConnectionBarTests` for
the words.

What to build once WW522 lands: `cases/connected.cases.json`, a fixture staging a store
whose one session is the fixture's `probe` account on `127.0.0.1:2222` with a `Local`
forward and `--session` naming it, declaring that address as what it reaches. Cases:
`SessionBar` is present; `ConnectionState`'s name reads `Connected`; a `Forward`
element's name starts with `Forward `; `StatusBar`'s `ConnectionStatus` names the host.
The automation ids are the ones `SessionBar.cs` and `ConnectionStatusBar.cs` set.

The guest needs the address to answer: either the sshd reached from the guest over the
host's network, or a container in the guest. Which one is the open choice, and an absent
server must read as not observed rather than as a pass.
