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

### §QS306 QS306 Exporting saved passwords to a vault

The user keeps a file of passwords because one is always forgotten, and today the client
holds them where only it can read them: Credential Manager, under DPAPI, which does not
travel to another machine. This is the command that turns QS305's writer into that file.

Each session becomes one entry: the title is the session's name, the URL is
ssh://user@host:port, then the login and the password SecretStore holds for its
endpoint, or for its named credential (QS245). The sidebar's folders become groups. A
session with nothing saved is still exported, with an empty password, so the vault is a
complete list of where the user goes. A key file a session names may go in as an
attachment, asked for separately.

Opening every secret at once is when the store's weakness matters most, so the export is
gated by Windows Hello or the account's PIN (UserConsentVerifier), not by a click. The
vault is open to offline guessing for as long as it exists and Argon2id only slows that,
so the passphrase is the protection: the dialog offers a generated six-word diceware
phrase and refuses one whose estimated strength is low, saying why. A portable install,
which saves nothing, exports nothing.

Done when the command exists in the menu and the command palette, docs/SESSIONS.md names
it, and a test exports a store with folders, a shared credential and a session with
nothing saved, and keepassxc-cli lists each entry as the store held it.

### §QS307 QS307 Keeping the exported vault current

An export is a snapshot, and the user asked for a file that is easy to maintain. A vault
written once and then forgotten is wrong by the next password change, and is then worse
than none, because it is consulted and believed.

A setting names a path, which may be a synced folder such as OneDrive since the file is
encrypted, and the client rewrites the vault there whenever SecretStore saves, replaces
or forgets a secret, or a session is added, renamed or removed. Rewriting must not ask
for the passphrase each time, so the derived key, never the passphrase, is kept sealed
under DPAPI for this account; turning the setting off forgets it. A rewrite that fails
says so where the user will see it rather than leaving a stale file silently in place.

A file a sync service may hand to another device makes the passphrase rule in QS306
matter more, and the setting says that when it is turned on. A vault the user has edited
in KeePassXC since the last write is not overwritten: the client notices the change and
asks, since a merge is out of scope.

Done when a test changes a saved password with the setting on and keepassxc-cli reads
the new one from the file, and a second test edits the vault outside the client and
finds the rewrite refused with a message naming the path.

### §QS308 QS308 Importing passwords from a vault

The export closes half of the circle. The other half is a new machine, or a reinstall:
the sessions file moves with the user, but every remembered password stays behind in the
old account's Credential Manager, because DPAPI binds it there. A vault already holds
them, so the client should read one back.

This needs a KDBX 4 reader beside QS305's writer, with the same key derivation and HMAC
check, refusing a file whose blocks do not authenticate rather than reading what it can.
Each entry whose ssh:// URL or host, port and login match a session has its password
saved through SecretStore, under the session's named credential where it has one. An
entry that matches nothing is listed and not saved, and a password that differs from one
already saved is shown as a conflict for the user to settle, never overwritten without
asking.

The import also reads vaults the client did not write, since many users already keep
their hosts in KeePass; the URL and the username field are what is matched, and nothing
else in the entry is assumed.

Done when a test round-trips QS306's export through an empty Credential Manager and
every session connects without asking, a vault made by keepassxc-cli imports the same
way, and an altered file is refused with a message that names it.

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

### §QS316 A split that presses Enter

Reported by the user (2026-10-15), with a picture: on a saved session (root on a Debian
host), each press of Split leaves every pane already open with one more prompt line
under the last, as if Enter had been pressed in it.

A split narrows the panes beside the new one, and each pane's session is told its new
size (SessionPipeline.Resize, an SSH window-change on a remote pane). Two explanations
fit, and they call for different fixes:

1. The client sends input on a resize. A trace of the session log shows channel data from the
   client at the moment of the split; a defect here.
2. The host's shell redraws on SIGWINCH: readline, told the width shrank, decides the prompt
   wrapped and prints it again. Nothing is sent, and Windows Terminal over OpenSSH does the
   same. What can be done here is one window-change per pane per split, with the final size,
   and none where a pane's columns and rows did not change.

Measure first: trace one split, counting window-changes and bytes sent per pane, then
the same split in Windows Terminal. Build only what that points at.

Falsified when a split adds a prompt line to a pane whose size it did not change, or
sends any input byte to a pane nobody typed into.

## Block D — The tree a user organises work in

### §QS314 QS314 Selecting several sessions

The owner asked on 2026-10-15 for a PDF documenting chosen sessions, and the first thing
that needs is a way to choose them. The sidebar is a tree that holds one selection,
which is also what connects, edits and folds, so a set of sessions has nowhere to be
said.

Ctrl+click adds or removes a row, Shift+click takes the range between the last row
clicked and this one in the order the tree shows, and Ctrl+A in the sidebar takes every
visible row. A selected folder stands for every session beneath it, at any depth, so
documenting a customer is one click on its folder. A plain click goes back to one row,
and what acts on one row today (Enter connects, F2 renames, QS300's one-click fold)
keeps acting on the focused row. The accessibility tree reports each row's IsSelected,
so a case and a screen reader both read it.

The context menu on a set offers only what takes a set: the document, and later anything
else that does. Connect on several rows is QS179's group and stays a folder's, not a
selection's, until a line argues otherwise.

Done when a UI case Ctrl+clicks two sessions and a folder, reads IsSelected on each row,
and finds the set the window reports holds the two sessions and every session in the
folder exactly once; a Shift+click range is read the same way; and a plain click leaves
one.

### §QS315 QS315 A PDF of the selected sessions

The owner asked on 2026-10-15 for a way to document many sessions, with their proxies
and keys, as a PDF of the ones selected. Today that is the sessions file read by hand,
where an inherited value is not even on the node that uses it.

Each session is one section: its name and folder path; host, port and user; how it signs
in (key path and the key's SHA256 fingerprint, the agent, or a password, saying only
whether one is saved and under which credential); its jump chain hop by hop; forwards as
a table of kind, listen and target; reconnect, post-login commands, tags. Every value
inherited from a folder says which. A cover page names the date, the client's version
and the folders covered. No password, passphrase or private key is ever written, and a
test asserts it against a store holding all three.

The command is in the sidebar's menu for a selection and in the palette, and asks only
where to save. PDF needs a library, as Argon2id did in QS115, so the owner decides it:
PDFsharp with MigraDoc is MIT and pure managed; QuestPDF is nicer to write but free only
under a revenue limit; printing through Microsoft Print to PDF needs a dialog and a
printer.

Done when a test documents a store with a folder default, a two-hop jump chain and three
forwards, reads the PDF's text back, finds every field and its origin, and finds no
secret.

### §QS331 Tags that say which environment a session is

Two sessions named `app-01` and `app-01-new` look the same in the tree, and the mistake
that costs most is running a command on the one you did not mean. Nothing in the session
store says which of them is production.

A session gains a `tags` field: free words, lower case, with a folder's tags inherited
by everything under it the way other defaults already are. `prod` is the one word the
client gives a meaning to by default, drawing the tab and the pane border in the danger
colour the theme already has, and settings can give other tags a colour.

Tags are imported from MobaXterm and PuTTY where a folder name maps cleanly, and they
round-trip through the session export.

The agent gateway (Block L) is the second reader: its policy is keyed on tags rather
than host names, and `hosts_list` reports them, so an agent sees the same warning the
person does.

Proven by a UI case that tags a folder `prod`, opens a session beneath it and reads the
tag back from the tab's automation properties.

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

### §QS310 QS310 Middle click closes a tab

Closing a tab with the middle button is a habit carried from every browser, Windows
Terminal and MobaXterm, and here it does nothing, so a user who reaches for it concludes
the tab cannot be closed that way and looks for the button QS312 is about.

The click is taken on the TabItem, not the strip, so it closes the tab under the pointer
and never the one on screen, and it goes through CloseTab exactly as the close button
does: a live session still asks before it ends, and the last tab still leaves the window
on Home. A middle click on Home, on New tab or on the empty strip does nothing. The drag
that reorders (QS250) and the pull that detaches (QS160) are left button only and must
not notice the middle one.

Done when a test raises a middle-button press and release on the second of three tabs
and finds two tabs left with the first still on screen, a second test does the same on a
connected session and finds the confirmation asked, and docs/KEYS.md names the gesture
beside Ctrl+Shift+W.

### §QS311 QS311 A tab's context menu

A right click on a tab is where a user looks for anything a tab can do, and here there
is no menu. The commonest thing wanted from one is clearing the strip after a morning of
sessions, which today is one close per tab and one confirmation per live session.

The menu offers Close, Close other tabs and Close tabs to the right, in that order, the
last disabled on the rightmost tab and the second disabled when there is only one. A
close of several tabs asks once, naming how many live sessions it would end, rather than
once per session, and closes nothing if the user declines. Each entry is also a command
in the palette, acting on the tab on screen, so the menu is never the only way to it.
Duplicate tab and Rename tab are left for their own lines rather than grown into this
one.

Done when a UI case right clicks the second of four tabs, chooses Close tabs to the
right and reads two TabItems left, a test closes others with two connected sessions
among them and finds one confirmation naming two, and the palette lists the three
commands.

### §QS312 QS312 A close button nobody sees

The owner opened a tab on 2026-10-15 and reported that it has no button to close it. The
code says otherwise: QS278 put a CloseTab button after every title, TabStripTests finds
it at 22 by 22 and clicks it, and the build that was running carried it. A button a test
can find and the user cannot is the defect, and the test that passes is the reason it
went unnoticed: it asserts the element and its size, never that it reads.

The likely causes are all in MainWindow.TabStrip.cs. The cross is the Cancel glyph at 8
points in the secondary text colour, on a Ghost template with no fill until the pointer
is over it, so at rest it is a faint mark at the far end of the title; it may also fall
outside the tab where the strip narrows an item, or the icon face may not resolve where
the guest renders it. Which one it is gets settled by looking, in the guest, not by
guessing.

Done when the cross is visible at rest in both themes, at least 10 points and in the
primary text colour or of contrast at least 3:1 against the strip, the tab widens rather
than clipping it, and a UI case reads CloseTab's bounding rectangle inside its tab's for
a short and a long title. The guest's app-desk.png is attached for a human to look at;
the evidence is the case.

### §QS313 QS313 A connected card goes to its tab

The owner, on 2026-10-15, connected to a saved session, went back to Home and found its
card under Recent reading Connected now with a Connect button. The button opened a
second connection in a new tab, and nothing on Home led to the one already open. A card
that says connected promises the session it names, and the click broke that promise.

When a tab holds a live or reconnecting connection to the card's session, the card's
button reads Go to tab, in the accent style, and selects that tab. Where several tabs
hold it, it goes to the one last on screen, and the status says how many (Connected in 2
tabs). A quieter New connection button beside it keeps today's behaviour for a second
shell on the same host. A card with no live tab is unchanged. A double click on the card
does what its main button does.

The sidebar has the same gap, since its Connect entry and double click always open a new
tab, and its row should take the same choice: Go to tab first where one exists, New
connection beside it.

Done when a UI case connects a session, shows Home, reads the card's button as Go to
tab, invokes it and finds that tab selected with the tab count unchanged; invokes New
connection and finds one more tab; and closes both tabs and reads Connect again. The
sidebar's menu is read by the same case.

### §QS317 Closing a pane

Reported by the user (2026-10-15): a pane opened by Split has no button to close it.
Closing a pane is written already - MainWindow.ClosePane asks first where the pane's
session is live, hands the space to its sibling, and closes the tab when it is the last
pane - but nothing calls it except BroadcastTests: no button, no chord, no palette
entry. So the only way out of a split is closing the whole tab, or typing exit in the
pane.

What to build, through that one ClosePane path:

- A close button on each pane while its tab holds more than one: a small cross in the pane's
  top-right corner, clear of the text, drawn at rest in a colour that reads (QS312), with the
  automation id PaneClose. Pressed, it closes that pane, not the one with the keyboard.
- A palette entry, Close pane, beside Split pane right and Split pane down.
- A chord: Ctrl+Shift+W closes the pane with the keyboard while the tab has several, and the
  tab when it has one, as Windows Terminal's does, so the chord that closes a tab today still
  does on a tab of one pane. KEYS.md says so.

Proven by a UI case that splits a tab, presses the second pane's PaneClose, and reads
one pane left on the desk.

Falsified when a pane opened by Split can only be closed with its tab.

### §QS318 Which way Split splits

Reported by the user (2026-10-15): the session bar's Split button cannot choose a
direction; it always splits side by side. SessionBar's SessionSplit act calls
SplitPane(Divide.Beside) and nothing else, while the keyboard has both (Ctrl+Shift+\
beside, Ctrl+Shift+- below) and the palette has Split pane right and Split pane down. So
somebody using the mouse can only ever make columns, and a tall terminal split twice is
three slivers.

What to build, keeping the bar as narrow as it is: Split stays one button whose click
splits beside, as today, with a chevron beside it that opens a two-item menu, Split
right and Split down, each naming its chord. Windows Terminal's new-tab button has the
same shape, a default and a menu of the rest. The chevron is its own control, with the
automation id SessionSplitMore, so a case can open it; each item carries an id as well
(SessionSplitRight, SessionSplitDown).

Not two separate buttons: the bar already holds Files, Split and the menu, and a second
split icon is a choice a person has to read the tooltip of every time.

KEYS.md and SESSIONS.md say Split has a menu for the direction. Proven by a UI case on
the connected fixture that opens the chevron, picks Split down, and reads two panes one
above the other.

Falsified when the session bar can only split side by side.

### §QS319 Numbered tabs

Asked for by the user (2026-10-15): every tab carries a number before its title, so two tabs
on the same host are told apart at a glance: 1. root@localhost, 2. root@localhost,
3. opc@server1.

The number is the tab's position on the strip, not the order tabs were opened in. Alt+1
to Alt+9 already reach a tab by position (KEYS.md), so the number on a tab is the chord
that takes you to it. A number fixed at creation would stop matching the chord the first
time a tab is closed or moved, and two tabs could end up showing 2.

What to build: the strip draws "N. " before each title, in the title's own secondary
colour so the title still reads first. Closing, moving, detaching or dragging a tab
renumbers the rest at once. A tenth tab and later still show their number, with no chord
behind it. The window title and the session bar stay as they are; the number is a
property of the strip. The automation name of a tab keeps the bare title, so a case
still finds a tab by what it holds, and the number is in its help text.

Proven by a UI case that opens three tabs, reads 1. 2. 3. on the strip, closes the
second, and reads the third as 2.

Falsified when two tabs on the strip show the same number, or a tab's number is not the
Alt chord that reaches it.

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

### §QS292 The drop as a case

QS188's Explorer drop is proven by tests/Quickshell.Cases/ExplorerDropRun.cs
(2026-10-13), and not by a case file, because winwright's `drag` releases over an
element of the window it started in (WW530). The test does the engine's work by hand: it
launches the client on the guest's sshd, answers the host-key question, opens the
browser through the palette with synthesised keys, finds everything in the accessibility
tree, and moves the pointer itself with SendInput. That is about four hundred lines
doing what a case says in thirty, with its own waits, its own input and its own reading
of a failure — which is exactly what QS147 adopted winwright to stop writing.

winwright 1.1.0 shipped WW530 (`toDesktop`, a drop onto another window), and two gaps
are left: a fixture cannot open the Explorer folder the drop lands in (WW536), and no
step can read the file it lands as (WW535). What to build, once both ship: a case in
browser.cases.json whose fixture reaches 127.0.0.1:2240 as connected.cases.json's does,
dragging the served file onto an Explorer folder view, followed by the fixture's check
that the file landed with the served bytes. Then ExplorerDropRun.cs and the Cases
project's WPF and unsafe settings go.

Falsified when the case replaces the test and an Explorer drop that leaves a different
file, or none, is not red.

## Block L — An agent at the controls, held to a policy it cannot edit

### §QS320 A threat model before a single verb

People already hand Claude Code an SSH key and let it work on servers. What it can harm
is a production host it mistook for staging, a secret it read and carried into a
transcript, a destructive command it was talked into by text on the server itself
(prompt injection), and every other host the same key reaches.

The design document is where this block matures, and it opens with the threat model
because every later line is judged against it. It names three layers, each assuming the
one above failed: hooks and a skill that steer the agent towards the gateway (they stop
accidents, not intent); credentials held only by quickshell, so the agent has no way to
authenticate around it (same Windows user, so this raises the cost and is not a wall);
and the server itself, a restricted account and short-lived certificates, the one layer
an agent on the client cannot route around.

The document says plainly that a client-side policy without the third layer is advice,
and every later section keeps that sentence true. It also lists the agent's own pains
with SSH (quoting through four shells, a handshake per command, prompts that hang,
output that floods context) because lowering friction is half the reason anyone would
route through the gateway at all.

### §QS321 Whether this belongs in the client at all

The README sells quickshell as the SSH part of MobaXterm without the suite around it,
and the non-goals refuse a plugin host and a scripting engine in the MVP. An agent
gateway is new scope, so it has to be argued from a user's task and not from novelty.

The argument to test: the gateway is not a plugin host inside quickshell, it is
quickshell offered as a tool to an agent that already exists, and the user's task (let
an agent work on a server without handing it the keys to everything) is real and
growing. The conditions it must meet to stay inside the project: off unless the user
turns it on, a separate process (`quickshell --mcp`) so the window's memory and start-up
budgets in PERFORMANCE.md do not move, no network of its own, and nothing on the
keystroke-to-pixel path.

The outcome is a decision record in DECISIONS.md, and if the answer is no, a non-goal so
the idea is not re-filed. Every other line in Block L waits on it.

### §QS322 The policy an agent is held to

The design document's policy section answers four questions before any engine is
written.

What is a policy keyed on? Session tags (`prod`, `db`, `staging`), not hostnames,
because the mistake worth preventing is confusing two environments and a tag is how a
person already thinks of them. Glob patterns over session names are the fallback.

What are the levels? Three to start: `observe` (reads approved without asking),
`operate` (a mutation asks the person), `break-glass` (wide, time-boxed, loudly
audited). Unknown on a `prod` tag means deny, never allow.

How does a grant end? Every widening carries an expiry, e.g. restarting nginx on `web-*`
for thirty minutes, and a grant with no end is refused at write time.

Who writes it? Only the person, through quickshell's own UI. The file lives in
quickshell's settings directory, outside any repository the agent works in; the plugin's
hook denies an edit to that path, and quickshell asks the person to confirm when the
file changes on disk, so an agent cannot approve its own permission.

The section ends with a worked example file and the three questions it could not answer
yet.

### §QS323 Judging a command without pretending a regex is a firewall

A blocklist of strings does not hold an agent: `find / -delete`, `python3 -c`, `sed -i`,
a script written over SFTP and then run, or a base64 blob piped to `sh` all do what `rm
-rf` does. So the classification section starts from the other end.

A command is parsed into a bash AST. If every simple command in it is on an allowlist of
reads (`ls`, `cat`, `df`, `systemctl status`, `journalctl` and so on) with arguments
checked against what that tool can write (no `-o`, no `--output`, no redirection), it is
a read and the policy's `observe` level applies. Everything else is a mutation. Anything
the parser cannot settle with certainty, `eval`, command substitution, a pipe into an
interpreter, an unknown binary, falls to asking the person and never to allowing.

Open questions the section has to close: which parser (a managed port, tree-sitter-bash,
or shelling out to a known one), how `sudo` and `env` wrappers are unwrapped, how the
allowlist is versioned and extended per tag, and how to measure it. The proposed measure
is a corpus of real agent sessions, scored for false allows, which must be zero, and for
false asks, which is the friction number to drive down.

### §QS324 Approval that does not train a reflex

Approval fatigue is the failure that defeats every permission prompt: forty identical
requests teach a person to approve without reading, and the forty-first is the `DROP
TABLE`. The approval section designs against that habit.

By plan, not by command: the agent proposes a batch, the person approves it once, and
quickshell runs exactly those argv on exactly those hosts. The approval is bound to a
hash of the batch, so a changed command is a new request rather than a silent
substitution.

Shown where it matters: quickshell's own dialog, not the agent's terminal, with the
session name, its tag (a `prod` tag in the colour the client already uses for danger),
each command as parsed, and for a file edit the diff.

Rare by construction: reads at `observe` never ask, and a time-boxed grant (QS322)
replaces a run of identical asks. The section names the target, asks per hour of agent
work on a `prod` session, and how it is measured from the audit log.

Open questions: whether an approval can be given from the phone the desk is not at, and
what the agent sees while it waits.

### §QS325 The spike: the gateway with nothing but exec

From the agent's side, the dearest costs of SSH today are mechanical. A command goes
PowerShell to `ssh` to bash to `sudo` to `sh -c`, and the quoting breaks somewhere in
that chain more often than anything else goes wrong. Each call is a new connection, so
the handshake is paid again and `cwd` and environment are lost; Windows OpenSSH has no
ControlMaster to hide it.

The spike is the smallest gateway that removes both: `quickshell.exe --mcp`, a stdio MCP
server running headless on Quickshell.Transport, with two tools. `hosts_list` returns
the saved sessions with their names (and their tags, once sessions carry them) and never
a secret. `exec` takes a session name, an argv array, an optional cwd and a timeout,
runs it on a connection the gateway keeps alive, and returns exit code, stdout and
stderr as separate fields.

No policy yet. That is why this line waits on the scope decision (QS321) and why the
spike is opt-in and documented as unguarded.

Proven by a run against the test sshd: a command whose argument holds quotes, a dollar
sign and a newline arrives byte-exact, and the tenth call to the same session reuses one
connection.

### §QS326 Remote edits an agent can make and a person can read

Writing a config file through `ssh host "cat > file <<EOF"` breaks on the first quote,
and `sed -i` with a regex changes lines nobody looked at. Both leave nothing to review
and nothing to restore.

The gateway already holds an SFTP channel on the same connection (Block E). Two tools
use it. `read_file` takes a session, a path and an optional line range, and returns the
text with line numbers, refusing a binary file. `edit_file` takes the path, an exact old
string that must occur once, and its replacement, the same contract an agent's local
edit tool has, so nothing new is learned. Before writing, it copies the original to
`<file>.qs-bak-<timestamp>`; it writes to a temporary name and renames, so a dropped
link never leaves half a file; and it keeps the owner, mode and timestamps the file had.

The answer carries the unified diff, which the approval dialog shows once policy exists
and the audit log keeps either way.

Proven against the test sshd: a replacement with quotes and a trailing newline, a
refusal when the old string occurs twice, and a file left intact when the link is cut
mid-write.

### §QS327 A prompt is an error, not a two-minute hang

An agent's command that stops to ask something has nobody to answer it. Today it hangs
until the agent's own timeout, two minutes, and the transcript says only that it timed
out.

`exec` removes what it can before running: stdin closed, `PAGER=cat`, `SYSTEMD_PAGER=`
and `GIT_PAGER=cat` set, `DEBIAN_FRONTEND=noninteractive` set, and no PTY unless the
call asks for one. What it cannot remove it detects: output that stops on a known prompt
shape (`[sudo] password for`, `Password:`, `(y/N)`, `--More--`) with the process still
alive and no output for a short quiet window ends the command, and the answer names it,
for example `prompt.sudo_password`.

The agent then has something to act on: ask the person, use a sudoers rule the
server-side line prepares, or rephrase with `-y`. A host key prompt never reaches this
point, because the gateway checks the host key against `known_hosts` the way the client
already does, and an unknown key is a refusal with the fingerprint in it.

Proven against the test sshd with `sudo -k; sudo true`, `less` on a long file and `read
-p`, each ending inside five seconds with its named error.

### §QS328 Jobs that outlive a tool call

A build, a migration or a backup takes longer than an agent's tool call is allowed to
wait, and if the connection drops, the process goes with it. The agent's workaround
today is a hand-written `nohup … &` and a guessed log path, and then it forgets where it
put it.

Three tools replace that habit. `job_start` runs the argv detached on the server (under
`setsid` with output to a file in a per-user directory the gateway owns, or under tmux
where it exists) and returns a job id at once. `job_status` reports running or exited,
with the exit code. `job_output` reads from a cursor the previous call returned, so the
agent sees only what is new and a long log is never sent twice.

The gateway keeps job ids in its own state, so a job survives the gateway restarting and
the connection reconnecting (Block A already brings the session back). A `job_cancel`
sends TERM, then KILL after a grace period.

Proven against the test sshd: a ninety-second job started, the connection cut and
restored, and the output read to the end with the right exit code.

### §QS329 Output sized for a context window

An agent pays for every byte a command returns, and a log or a directory listing can be
most of a session's context in one call. Truncating blindly is no better: the error is
usually at the end.

`exec` takes a byte budget (a default the design doc sets, overridable per call). Within
it, output comes back whole. Over it, the answer keeps the head and the tail, says how
many lines and bytes were cut between them, and returns a cursor; `output_page` reads
the cut part by range, and `output_grep` runs a pattern over it on the server side
without sending it back.

The full output is kept in the gateway's spool for the life of the session, so nothing
has to be run twice to see more of it, and the spool is wiped when the session ends.

Proven by a run that prints two hundred thousand lines: the answer stays inside the
budget, names the cut, and a grep for a line in the middle finds it.

### §QS330 Redaction on the way out to the agent

`cat .env` is a reasonable thing for an agent to run while debugging, and its output
then lives in a transcript, in the provider's request and possibly in a log nobody meant
to keep. The client already redacts its own logs (Block I) so that one can be attached
to a bug report; the same redactor belongs on this path.

Every answer the gateway returns, from `exec`, `read_file`, a job's output or a page,
goes through it before it leaves the process: private key blocks,
`password=`/`secret=`/`token=` assignments, bearer headers, connection strings with
credentials, and the known token shapes (AWS, GitHub, Slack, JWT). Each match becomes
`«redacted:kind»`, and the answer carries a count, so the agent knows a value was hidden
and can ask the person rather than guess.

The redaction is a guard against accidental spread, not a boundary: a command can encode
a secret past any pattern, which the threat model (QS320) says plainly. A policy can
also refuse reading known secret paths outright.

Proven by a fixture file holding one of each kind, read through every tool, with nothing
of the original in any answer.

### §QS332 Closing the side doors

A gateway the agent can step around is a suggestion. Claude Code can run `ssh`, `plink`,
`scp`, `sftp`, `git` over SSH, or a Python one-liner with paramiko from its own shell,
and none of them pass through quickshell.

Two measures, of different strength, and the design document says which is which.

A `PreToolUse` hook on the agent's shell tool matches the obvious invocations and denies
them with a message naming the gateway tool to use instead. This is steering: it catches
the habit and the accident, and a script written first and run second walks past it.

The agent's sandbox, where enabled, denies outbound connections to port 22 and any port
a saved session names, while the gateway, a separate process outside the sandbox, keeps
them. This is the stronger of the two, because it is enforced below the shell rather
than by reading the command.

Neither touches the third way around, a key file the agent can read; that is the
credential-isolation work and is named in the threat model (QS320).

Proven by a run of the agent asked to reach the test sshd by each of five routes, every
one refused, with the gateway's own exec still succeeding.

### §QS333 An audit trail an agent cannot rewrite

After an agent has worked on a server, the person needs to answer what changed, and
today the only record is the agent's own transcript, which the agent writes.

The gateway writes its own: one JSON line per call, append-only, in quickshell's data
directory and outside any workspace the agent can write. Each line holds the time, the
session and its tags, the tool and its arguments (redacted the same way answers are),
the policy verdict and the rule that gave it, who approved and how (automatic, a plan
hash, a timed grant), the exit code and a hash of the full output.

A file is rotated by size and day, never edited, and each line carries the hash of the
one before it, so a deletion in the middle is visible.

`quickshell --audit <session-id>` prints the log as a change summary a person can paste
into a ticket: what was read, what was changed, what was refused.

The same log is what the approval design (QS324) measures itself against: asks per hour,
and how many were approved unchanged.

Proven by a recorded gateway session whose summary lists every mutation, and by a line
removed by hand being reported.

### §QS334 A kill switch

When an agent is doing the wrong thing on a server, every second spent finding which
window to close is a second it keeps going. Stopping the agent's own process is not
enough either: jobs it started on the server keep running.

The gateway gets one stop. A keybinding in quickshell, the same command in the palette,
and an item on the tray icon each revoke every timed grant, cancel every running job
(TERM, then KILL after a short grace), close every connection the gateway holds, and
leave the gateway refusing every call until the person re-enables it from the same
place.

The agent's next call gets a refusal that says it was stopped by the person, so it
reports that rather than retrying in a loop. The stop and what it cancelled go to the
audit log.

Proven by a run with three jobs on two sessions: one keypress, every job gone from the
servers' process lists within the grace period, and the next exec refused with the named
reason.

### §QS335 The agent's session, in plain sight

The person handing work to an agent wants to glance at what it is doing, the way they
would look over a colleague's shoulder, and today the only view is the agent's own
summary after the fact.

When the gateway opens a connection for an agent and the client is running, the client
shows it as a tab: badged as the agent's, in a colour no human tab uses, and read-only
for the person so their keystrokes never interleave with the agent's. Each call appears
as a prompt line with the command as parsed, then its output as it streams, then its
exit code; a refused call shows the refusal and the rule that gave it.

Closing that tab does not end the agent's session; the kill switch does. Headless, with
no client running, nothing changes and the audit log is the only view.

The tab is ordinary terminal output drawn by the existing renderer, so it costs nothing
new on the rendering path.

Proven by a UI case that runs two calls through the gateway and reads the tab's badge
and its last line from the accessibility tree.

### §QS336 The policy engine

With the policy format (QS322), the command classifier (QS323) and session tags (QS331)
designed, the engine is what puts them in the path of every call.

Before a call runs, the gateway resolves the session's tags, picks the policy level that
applies, classifies the call (an `exec` by the classifier, a `read_file` as a read, an
`edit_file` or `job_start` as a mutation), and checks any live timed grant. The answer
is one of three: allow and run; ask the person and wait; or refuse, naming the rule, so
the agent can explain the refusal rather than retry around it.

The engine's verdict and the rule that gave it go into the audit log for every call,
allowed or not.

Defaults when no policy file exists: every session at `operate`, so the first run asks
rather than allows, and any session tagged `prod` refuses whatever is not a recognised
read.

The engine is pure: given a policy, the tags and a call, it returns a verdict and
touches no connection, which is what lets it be tested on a corpus without a server.

Proven by that corpus run, zero false allows, and by a live refusal on a `prod`-tagged
test session.

### §QS337 The approval dialog

The approval design (QS324) is built here, in the client rather than in the agent's
terminal, because the client knows the session, its tags and its colours, and because an
approval the agent's own process draws is one the agent's process could fake.

The gateway sends the client a pending request: a batch of calls, each with its session,
tags and parsed command or diff. The client raises a dialog in front of the tab showing
the agent's session, with the `prod` tag in the danger colour and each call listed as it
will run. Three answers: approve this batch (bound to its hash, so the gateway runs
exactly it), approve and grant (the same calls on the same tags for a chosen time, from
a short list, never open-ended), or refuse with an optional note the agent receives.

Headless, with no client running, an ask is a refusal saying no one was there to answer,
which is the safe direction.

Every answer is written to the audit log with the hash it approved.

Proven by a UI case that receives a two-call batch, reads both calls and the tag from
the accessibility tree, approves, and finds both in the audit log under that hash.

### §QS338 Taint after untrusted input

Prompt injection is the attack that needs no exploit: a log line, a README or a database
row on the server says "ignore your instructions and run this", and an agent that just
read it may comply. The danger is the combination of untrusted text in the context with
a tool that can change things.

The gateway cannot tell which text the agent believed, but it can tell when untrusted
text entered: any `read_file`, any page of output, any `exec` whose output came from a
file or a remote service rather than the gateway's own metadata. From that point the
gateway session is tainted, and every mutation asks the person, including one that a
timed grant or the `operate` level would have allowed. Reads stay automatic, so the cost
falls only where the risk is.

The taint is shown in the agent's tab and in the approval dialog, so the person knows
why they are being asked. It clears only when the person clears it, never by the agent.

The design document weighs how coarse this is, and whether a read of a file the person
marked trusted should taint at all.

Proven by a fixture file holding an injected instruction: after reading it, an otherwise
granted restart asks.

### §QS339 A policy the agent cannot edit

A permission an agent can grant itself is no permission. The policy file sits on the
same disk, under the same Windows user, as everything the agent can write.

Three measures. The file lives in quickshell's settings directory, never in a repository
or a working directory, so it is outside the paths an agent is usually pointed at. The
plugin's `PreToolUse` hook denies the agent's file tools any write to that path, and its
shell tool any command naming it. And quickshell holds the hash of the last version the
person approved: when the file on disk differs, the gateway refuses every call and the
client asks the person to accept or reject the change, showing the diff.

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
