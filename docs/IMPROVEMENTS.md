# Improvements

## Block A — A session that stays up, or says why it did not

### §QS36 The seam, and exactly what may not cross it

`ISshTransport` owns connecting, authenticating, opening channels and closing. Its
vocabulary is this client's own: a host, a credential, a channel, a failure carrying a
reason a user can read. No type from the protocol library appears in its signatures, and
no assembly above it references that library at all — enforced by project references
rather than by review.

Three things must be able to cross it: a channel that behaves as an `IPtyChannel`, a
channel carrying a file transfer session, and a channel carrying a forwarded connection.
Those three are the entire surface the rest of the client needs, and a fourth is a
question about whether the seam is in the right place.

What must not cross: exception types, key objects, connection-info structures, and
anything whose lifetime the library manages. Each is a hook that makes the library
unremovable, and each looks harmless on its own.

The seam earns its keep against two credible futures. The gap analysis may conclude the
library cannot do agents or jump hosts, in which case a second implementation over
libssh2 or a wrapped OpenSSH is the answer rather than a rewrite. And a synthetic
implementation that replays recorded byte streams is what lets the client be tested with
no server at all.

Falsified when a search for the library's namespace finds a hit outside the transport
assembly.

### §QS37 The second implementation of an interface that already works

Everything above `IPtyChannel` was built and proven against a local pseudo-console, so
this is genuinely an implementation rather than an integration: open a session channel,
request a pseudo-terminal with the right terminal type and geometry, request a shell,
hand the channel's streams to the same pipeline that already runs.

The terminal type this client claims is a decision with consequences. Claiming
`xterm-256color` is a promise about behaviours the emulator then has to actually have,
which is why the conformance work comes before this line and not after it. Claiming
something smaller is safer and immediately visible to the user as a worse terminal.

Terminal modes go out at pty-request time, and the one that matters is that the client
does not want the server doing anything the terminal already does for itself.

The buffering behaviour of the library's shell stream is a named risk from the gap
analysis, so this line measures instead of assuming: throughput under `cat` of a large
file, and allocations per megabyte, both against the same figures taken locally. A gap
between them belongs to the library, and it is either closed here or it is the trigger
for the second implementation the seam exists to permit.

Channel close, a server-side exit and a dropped connection are three different endings,
and the user is told which one happened.

Falsified when local and remote throughput differ by more than the network explains.

### §QS38 What survives a drop, and what honestly cannot

Three distinct failures wearing one appearance: the server closed the session; the
network went away and came back; the network went away and the TCP connection is still
sitting there open, waiting, and will wait for a very long time.

Keepalive addresses the third. Protocol-level keepalive at a configurable interval
detects a dead peer in seconds rather than in the operating system's own good time, and
it keeps a NAT mapping alive besides — which is what stops an idle session dying after
twenty minutes on a corporate link.

Reconnect addresses the second, and it is honest about its limits. A new connection
means a new shell and a new remote state: working directory, environment and any running
program are gone, and no client recovers those without cooperation on the far side. What
quickshell keeps is the scrollback, the tab, the session's settings and the layout — so
a drop costs the user a command, not an afternoon.

Backoff is exponential with a ceiling and a cap on attempts, and every attempt is
visible: which attempt this is, when the next one is due, and how to stop. A client that
reconnects silently and forever is a client hammering a server that is deliberately
refusing it.

Reconnect is per-session, and off for hosts where an unexpected new login is itself an
event.

Falsified when a reconnect claims to restore state the protocol cannot restore.

### §QS39 The message is the feature

Every failure mode of a connection is enumerated here and given a sentence, because the
alternative is a stack trace and a user who cannot tell which of six things went wrong.

The list: the name did not resolve; the port refused; the port accepted but nothing
there spoke SSH; the handshake failed with no algorithm overlap, naming what each side
offered; the host key did not match, which belongs to Block B and gets the strongest
wording of anything here; authentication failed, distinguishing no method accepted from
a method that failed; the shell request was refused, which is what a restricted or
SFTP-only account looks like; and the connection timed out, distinguishing a connect
timeout from a handshake timeout.

Each message says what happened, what it means, and what the user might do about it —
three short clauses, not a paragraph, written for somebody who has not read the code.

Where a technical detail matters for diagnosis it goes to the log rather than into the
dialog, so the message stays readable while the detail stays available.

An algorithm mismatch deserves particular care, being the failure most often met against
old appliances: the useful message names the algorithm the server wanted and says
whether it is refused as insecure or absent as unimplemented, because those two have
opposite remedies.

Falsified when a connection failure surfaces a library exception type to a user.

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

### §QS41 Every way in, and the order they are tried

Four methods, and the order matters as much as the set.

**Public key** first, since it is what most users have and costs nothing to attempt.
Formats: OpenSSH's own, PEM, PKCS#8, and PuTTY's `.ppk`, because a MobaXterm user's keys
are very often in that last one. Types: RSA with SHA-2 signatures, ECDSA, and ed25519. A
passphrase is asked for once and the decrypted key is never written anywhere.

**Keyboard-interactive** next, which is the shape a second factor actually arrives in:
the server sends prompts and the client displays them *as the server worded them*.
Rendering the server's own text rather than substituting the client's is the entire
feature — a user reads "Duo push sent" or "Enter your token", and a client that says
"Password:" has thrown away the only useful information in the exchange.

**Password** last, and only where offered.

Where several methods are available the server states an order, and the client follows
it rather than imposing its own. A partial success — a key accepted with a second factor
still required — is a normal state, not an error, and it is shown as progress.

`none` is attempted first as the protocol intends, since that is what makes the server
list its methods at all.

Falsified when a server's own prompt text is replaced by wording of the client's.

### §QS42 Fail closed, and the dialog with no default button

An encrypted connection to an unverified host is an encrypted connection to whoever
answered. So this fails closed: an unknown or mismatched key stops the connection, and
no setting turns the check off globally.

The store is OpenSSH `known_hosts` format, read from and written to the standard
location, because the user already has one and a client with a private store of its own
makes them maintain two. Hashed entries are read. Certificate authority lines are
honoured where the library can, and the gap analysis has already said whether it can.

Three outcomes. A known and matching key connects with no interaction at all. An unknown
key raises trust-on-first-use: the fingerprint in SHA-256 and the legacy form, the
algorithm, the host and port, and an explicit accept — with **no default button**,
because a dialog whose default is Yes is a check that does not exist.

A *changed* key is not that dialog and must not resemble it. It is a warning, it names
what changed, it says plainly that this is what an interception looks like as well as
what a rebuilt server looks like, and continuing requires removing the old entry
deliberately rather than clicking through.

Several keys of different algorithms for one host coexist, which is normal and not a
mismatch.

Falsified when any code path connects without consulting the store.

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

### §QS44 Where a secret rests, and how long it stays in memory

Saving a password is optional and off by default. Where a user chooses it, the storage
decision is made here rather than improvised later.

At rest, DPAPI scoped to the user is the floor: the ciphertext binds to the Windows
account, so a copied file is useless on another machine. Windows Credential Manager is
the alternative and is preferable for a per-host secret, because it gives the user a way
to see and revoke what is stored using a tool they already have and already trust.

An optional master password sits above that, for users who want the store to survive a
stolen laptop, and it is a real key derivation — Argon2id or an equivalent memory-hard
function — feeding an AEAD, not a hash and a comparison. Without one, DPAPI is honest
but is no defence against an attacker already running as the user, and the settings
surface says exactly that rather than implying more.

In memory, secrets live in pinned buffers and are zeroed after use. They are never put
in a `string`, which is immutable, garbage-collected, and may be copied by a compaction
no code here will ever see.

Portable mode complicates this, since DPAPI binds to the machine's user. There a master
password is required rather than offered.

Falsified when a stored secret can be read on another machine with no master password.

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

## Block C — Emulation that does not lie about the remote

### §QS29 Composition over a surface the IME cannot see

An input method needs two things from the client: somewhere to draw the candidate list,
and the composition string as it evolves. A GPU-rendered surface offers neither by
default, because the IME cannot inspect what is on it.

So the client handles the IME messages itself rather than letting a default window
procedure guess. `WM_IME_STARTCOMPOSITION` opens composition, `WM_IME_COMPOSITION`
delivers the string in progress and then the committed result, and the candidate window
is positioned explicitly at the cursor's cell — converted out of grid coordinates, which
the client is the only thing capable of doing.

The composition string is drawn by the terminal as part of its own grid, at the cursor,
with the underline the convention expects. It is *not* written into the buffer: it is
display state that vanishes when composition is cancelled, and putting it in the buffer
is what leaves abandoned text behind after a cancel.

The committed result goes to the host as encoded characters, down the same path any
other typed character takes.

Wide characters compound this with the width model: a composition of CJK characters
occupies two cells each, so the candidate window must be placed against real width and
never against a character count.

Falsified when the candidate window appears anywhere other than at the cursor.

### §QS30 Selecting over a wrapped line, and the paste that runs itself

Selection has three modes because users have three intents: character, word and line, on
single, double and triple click. Dragging past the edge scrolls; shift-click extends an
existing selection rather than starting a new one. Block selection on a modifier is the
fourth, and it is what makes copying one column out of tabular output possible at all.

Copying is where the wrapped flag earns its place. A logical line broken across three
rows must copy as one line with no inserted break, and trailing whitespace is stripped
per row, because a terminal pads rows and the user did not type that padding. Text goes
to the clipboard, and hyperlinks with it where the selection carries any.

Pasting is the security half of this line. Text pasted into a shell executes the moment
it contains a newline, and a command with a newline hidden in it is an old and effective
trick. So bracketed paste, DECSET 2004, is honoured whenever the program enables it,
which lets the program itself decline to run the text. Where bracketed paste is
unavailable, a paste containing a newline raises a confirmation that shows exactly what
will be sent.

Control characters in a paste are filtered out, since nothing legitimate pastes an
escape sequence.

Falsified when a wrapped line copies with a line break inside it.

### §QS31 A viewport onto the ring, and finding something in it

The ring already holds the history, so scrolling back is a viewport offset rather than a
data structure. Wheel, scrollbar, shift-PageUp and a configurable line-wise chord all
move that same offset.

Two behaviours decide whether this feels right. New output arriving while the user is
scrolled back does *not* yank the viewport to the bottom — somebody reading does not
want the screen stolen — though the scrollbar shows that output arrived. Typing does
return to the bottom, because typing means the reading is finished.

Under the alternate screen there is no scrollback at all, and the wheel is instead
translated into arrow keys or mouse events for the program. That is what makes scrolling
inside `less` and `man` behave the way users expect, rather than scrolling the terminal
out from under a full-screen program.

Search runs over logical lines and not physical rows, so a match spanning a wrap is
found. Case-insensitive by default with a case-sensitive option, regular expressions
optional, matches highlighted in place and navigable, and the count shown. Searching a
large scrollback must not stall the parser, so it runs against a consistent snapshot
instead of holding a lock across the whole scan.

Falsified when a match spanning a wrapped line is not found.

### §QS33 Judged by somebody else's tests

Two external suites, run against the headless model with a pseudo-console driving them
and no renderer or network involved.

`esctest`, from the iTerm2 project, is the more valuable of the two: it is programmatic,
it asserts specific buffer states, and it covers exactly the corners this project would
otherwise discover from a user — parameter defaults, clamping at margins, the
interaction of origin mode with CUP, DECSC across a screen switch.

`vttest` is interactive and older, and its value is different in kind: it exercises the
DEC behaviours a program written in 1985 still depends on, and network appliances are
full of programs written in 1985.

The result is a pass rate per section, committed to the repository, so a change that
improves one area while quietly breaking another shows up as a number rather than as a
feeling. Known failures are listed with a reason each — not implemented, deliberately
not implemented, or a defect with a task id beside it. A failure with no entry in that
list is a regression by definition.

The target is above ninety per cent of `esctest` before the emulator is called finished,
with the remaining tenth named individually rather than waved at.

Falsified when a pass rate is quoted without the run and the date that produced it.

### §QS34 Shaping across a boundary the grid says exists

A cell grid and a ligature disagree by construction. `=>` in a programming font is one
glyph spanning two cells, so the renderer must draw a glyph belonging to no single cell
and the atlas key must be the run rather than the character.

The approach: runs of identical attributes are shaped by `IDWriteTextAnalyzer`, and the
resulting glyphs are cached against the run's text instead of against a code point. A
cached run draws as a short sequence of instances whose positions come from the shaper
rather than from the grid. The grid still owns the cursor, the selection and the copy —
a ligature changes only what is drawn.

That is also where the cost hides. Shaping is per run, so a line where every cell
carries a different colour degenerates into per-cell shaping, and a syntax-highlighted
source file is exactly that line. The cache must be measured against that case and not
against prose.

Two behaviours are non-negotiable either way: the cursor sits on a character and never
on half a ligature, and a ligature under the cursor breaks apart so the user can see
which character they are on.

Off by default, and a per-font setting, because a sizeable share of this client's users
consider ligatures a defect rather than a feature.

Falsified when the cursor cannot be placed between the two characters of a ligature.

### §QS35 Three channels of alpha, and what that costs the blend

Grayscale antialiasing is correct and slightly thin. ClearType is what Windows users
have looked at for twenty years, and text differing from every other application on the
machine reads as wrong even when nobody can say why.

The obstacle is arithmetic. Subpixel coverage produces a separate alpha per colour
channel and standard alpha blending has one. There are two honest ways out: dual-source
blending computes the per-channel factor in the shader and blends in a single pass,
which is the right answer wherever the hardware supports it; otherwise the pass splits,
at the cost of a second draw over the same fill.

`DWRITE_TEXTURE_CLEARTYPE_3x1` is the rasteriser half, and an atlas page for a face
rendered this way becomes three channels. Both kinds of page coexist, since emoji stay
RGBA and a fallback face may stay grayscale.

Two conditions make it wrong to switch on blindly, which is why it is a setting and not
an upgrade. It assumes a horizontal RGB subpixel layout, so it is wrong on a rotated
display and on some panels. And it assumes an opaque background, so it degrades wherever
the window is translucent.

Gamma becomes visible here in a way it is not in grayscale, so DirectWrite's contrast
enhancement has to be carried through rather than dropped on the floor.

Falsified when text on a rotated display shows colour fringing.

### §QS91 The mark that took no cell and then went nowhere

QS10 settled what a combining mark costs in columns: nothing, which is what the host
also decided, so the cursor lands where it should. It said nothing about drawing the
mark, and so nothing does. `e` followed by U+0301 renders as a bare `e` — visible in the
QS10 capture, whose last cells read `ea` and should read `éä`.

Three shapes, and which is right is what this task decides.

Normalise on the way in. NFC folds `e` plus U+0301 to U+00E9, which the primary face
already has. Cheapest, and it covers most of what arrives — but a mark with no
precomposed form still disappears, and the model would no longer hold what the host
sent.

Give the cell a second glyph slot. The instance grows, every cell pays for the rare one,
and stacked marks still overflow it.

Draw the mark as its own instance, positioned over the base cell rather than beside it.
A zero-span instance already exists for the trailing half of a wide pair, so the shader
knows how to draw a quad owning no column; what it lacks is an offset. This is the shape
that neither lies nor caps the count.

The model has to carry the marks either way, which is where this meets the buffer's own
line rather than the renderer's.

Falsified when a decomposed accent renders identically to its precomposed form.

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

### §QS93 The screen no author would have written

QS12's design asks for one scene more than QS12 shipped: a full screen of `htop` output
replayed from a captured corpus. It could not be built, because there is no parser to
replay bytes through and no corpus to replay.

The seven scenes that exist are each a sentence somebody chose. That is exactly their
weakness: they cover the attributes their author remembered, in the combinations their
author thought of. A real screen from a real program is dense, has colour changes
mid-run, box-drawing meeting text, wide characters against narrow ones, and the specific
adjacencies nobody would think to write down.

What this needs is the pseudo-console landing, so a program can be run locally and its
byte stream captured; then a scene is a recorded stream replayed into the model and
drawn once. The corpus is committed beside the references, because a scene whose input
is regenerated is a reference that moves on its own.

`htop` is the design's example and a good one - colour, box drawing, bars, rapid update.
Worth having beside it: `git log --graph --oneline`, which is box-drawing meeting
proportional-looking text, and `ls --color` in a directory of long unicode filenames.

Falsified when a capture replays to a different screen than the one it was recorded
from, which would make the corpus a picture of a bug rather than of a program.

### §QS101 The ninety-six bytes QS24 could not name

QS24 took the parse path from fifty-five kilobytes of allocation per megabyte of stream
to zero on all five captured streams. On the twenty pathological shapes it reaches
ninety-six bytes and stops there, and the gate is a ceiling of two hundred and fifty-six
rather than the zero the design asked for.

What is known: three shapes account for it — lone surrogates as UTF-8, truncated
multi-byte characters, and one enormous line with no newline — at thirty-two bytes each,
every pass. Each measures exactly zero fed on its own with a warm-up, and zero fed
alternately with any single other shape. So the cost appears only when the full sequence
runs, which says something oscillates between two states as the shapes change and pays
thirty-two bytes on one of the transitions.

Thirty-two bytes is a small object: a string of four characters, a boxed value, a short
array. Ruled out by measurement already are the segmenter's buffer, the decoder's
buffer, the tab stops, the cluster and link tables, the reply and command lists, and the
clipboard buffers.

It is a fixed cost of the sequence and not a cost per byte — seven hundred kilobytes of
hostile input and seven megabytes both pay it once — so it cannot grow with a session.
That is why it is a ceiling rather than a bug on the hot path.

Falsified when the sequence allocates zero and the ceiling can be lowered to it.

## Block D — The tree a user organises work in

### §QS55 The file a user will still have in five years

The session store is what a user accumulates and what they will not abandon. That makes
its format a commitment: human-readable, diffable and documented, so it can go under
version control and be edited without this client running at all.

The structure is a tree of folders, because that is how people organise fleets — by
environment, by customer, by datacentre. A folder carries defaults its children inherit
and may override: user, port, key, jump host, terminal settings, colour scheme.
Inheritance is what makes a hundred hosts manageable, and each node states explicitly
where a value came from, so a user can see the source rather than deduce it.

Search across the tree by name, host, tag and folder, with results reachable from the
palette — above about fifty entries the tree stops being how anybody finds anything.

Secrets are not in this file. It holds a *reference* to a credential and never a
credential, so the file is safe to commit, share and back up. Which is what a user will
do with it whether or not the design allowed for it, so the design allows for it.

Opening a session is one action from the palette or a double click, and one already open
is focused rather than opened twice unless the user asks for a second.

Falsified when the store cannot be edited by hand and reloaded.

### §QS56 Reading a file the user already maintains

A developer arriving at this client very often has a `~/.ssh/config` that already works:
hosts, users, ports, keys, jump chains, per-host options accumulated over years. Reading
it is the difference between switching in an evening and not switching.

The directives worth honouring are the ones people actually use: `Host` with its
patterns and negation, `HostName`, `User`, `Port`, `IdentityFile`, `IdentitiesOnly`,
`ProxyJump`, `ProxyCommand`, `ServerAliveInterval`, `StrictHostKeyChecking`, `Include`,
and `Match host`. First-value-wins is the OpenSSH rule, and it has to be the rule here
too, because a config written against it behaves differently under any other.

Read-only, and that is the important decision. A user's `ssh_config` is shared with
`ssh`, `scp`, `rsync` and `git`, so a client that reformats or reorders it is a client
that quietly broke four other tools. quickshell's own additions live in quickshell's own
store, which may reference a config host by name.

Directives that are not honoured are reported rather than ignored. Silently dropping
`ProxyCommand` produces a host that looks configured and simply never connects, which is
the worst diagnostic outcome available.

Falsified when this client writes to a file OpenSSH also reads.

### §QS57 A connection carried inside another connection

A jump host is not a proxy setting. It is a connection nested inside another:
authenticate to the bastion, open a direct-tcpip channel from it to the real target, and
run an entire second SSH session over that channel's stream. The target's host key is
the target's own, verified like any other, and the bastion never sees the target's
traffic in the clear.

That framing decides the design. The transport seam must accept a *stream* and not only
a socket — a requirement on the seam, and the reason the seam was drawn where it was.
Chains follow by recursion: a jump through two bastions is the same operation twice, to
whatever depth the user configured.

`ProxyCommand` is the other route, spawning an external process and using its pipes. It
is supported because `ssh_config` files in the wild are full of it, and because it is
the escape hatch for everything the built-in path will never cover: a corporate SSO
helper, a cloud provider's session manager, somebody's shell script.

Failures must name which hop failed. A bare connection-refused with no hop named is the
least useful message a chain can produce, and producing it is what this line exists to
prevent.

Each hop's credentials are its own, and a chain never silently reuses the first hop's
key.

Falsified when a failure in a two-hop chain does not name the hop that failed.

### §QS58 The dialog is the settings surface with the highest traffic

Most users will configure a session here rather than in the file, so this dialog is
where the store's model becomes visible and where a bad default silently becomes
everybody's default.

It asks for the minimum that can open a connection — a host, and nothing else that can
be inherited or defaulted — with everything further behind a disclosure. A dialog
demanding twelve fields for a machine on the local network is a dialog that makes simple
work feel heavy, and that impression is formed once.

Inherited values are shown as inherited with their source named, and overriding one is a
deliberate act. Without that, a user cannot tell why one session behaves unlike its
siblings, and answering that question is where an evening goes.

Post-login commands are supported and are exactly as dangerous as they sound: text sent
to a shell as though typed. So they are visible on the session, they are never silently
inherited from a folder, and the dialog states what will happen. Sending a password this
way is refused, with the credential store named as the alternative.

Per-session terminal settings — scheme, font size, terminal type, scrollback — override
the global ones, which is what lets a production host look visibly unlike a staging one.
That visual difference is a safety feature far more than a preference.

Falsified when the dialog requires a field the store could have inherited.

## Block E — SCP and SFTP as a thing a person operates

### §QS59 A second channel, not a second connection

SFTP is a subsystem channel on an existing SSH connection. That is the whole design
decision, and it is worth stating because the alternative — opening a fresh connection
for the file browser — is what most clients do, and it costs the user another password,
another second factor, and another entry in the server's auth log.

So the transport seam exposes a file-transfer channel alongside the shell channel, and
the session owns both. Closing the session closes both.

SFTP version 3 is the floor, since that is what nearly every server speaks; later
versions are used where offered, because they carry better attribute and rename
semantics.

The operations needed are ordinary: list, stat, read, write, mkdir, remove, rename,
symlink, chmod, set times. Reading and writing keep multiple requests in flight rather
than looping request-and-wait, which is where SFTP throughput actually comes from — a
naive implementation over a high-latency link runs at a fraction of the available
bandwidth and is universally misdiagnosed as a network problem.

Paths belong to the server, not to Windows. Case sensitivity, separators, permitted
characters and length limits are all the far side's, and a client that normalises them
corrupts names.

Falsified when opening the file browser prompts for a credential the session already
used.

### §QS60 Two panes, and the operations between them

Local on one side, remote on the other, because that is the shape of the task and every
alternative makes the user hold the direction in their head.

Each side lists name, size, modified time and permissions, sorts by any of them, and
shows hidden entries on a toggle. Navigation is by double click, by typing a path, and
by history. A large listing arrives incrementally rather than after it completes: a
directory of fifty thousand files should show its first screen immediately, and a
browser that blocks until the listing finishes is a browser people stop opening.

The operations are the ordinary ones — copy either direction, rename, delete, create a
directory, change permissions — plus opening a remote file in a local editor with
write-back on save. That last one earns its cost: editing a config file remotely is why
people open a file browser at all, and the manual download-edit-upload round trip is
exactly what they are trying to avoid.

The remote pane follows the session's working directory wherever the shell reports one,
which is what the OSC work already bought.

Deleting asks, and says how many entries and whether any of them is a directory.

Falsified when listing fifty thousand entries blocks the pane until it completes.

### §QS61 The queue, and what resume actually means

Transfers are a queue that outlives the dialog which created them, so a user can queue
work and go and do something else in the client. Each entry shows the file, its size,
bytes done, rate and estimated time; the queue shows the aggregate.

Concurrency is configurable and low by default. Several files at once helps over a
high-latency link and hurts on a saturated one, and a default that saturates somebody's
uplink is a default that gets this client blamed for the network.

Cancel and pause work per entry and for the whole queue, and cancel genuinely stops
rather than letting the current file run to completion first.

Resume is why this line exists. SFTP reads and writes at an offset, so an interrupted
transfer continues from where it stopped — but only when the client can establish that
the partial file is genuinely a prefix of the source. Size alone does not establish
that. So resume is offered where size and modification time agree, with a checksum
comparison wherever the server can compute one, and where neither is available the
honest answer is to restart and to say why.

A failed entry stays in the queue carrying its reason and retryable, rather than
vanishing.

Falsified when a resumed transfer produces a file that differs from the source.

### §QS62 Recursion, and the four answers to a collision

Copying a directory is a walk, and the walk has to answer questions the flat case never
poses.

**Symbolic links**: followed, copied as links, or skipped. Following one is how a
recursive copy walks into a loop or drags in an entire filesystem, so the default is to
copy the link and the choice is explicit rather than buried.

**Permissions and times**: preserved wherever the destination can express them, and
where it cannot — a Unix mode landing on NTFS — the loss is stated once rather than once
per file. The reverse direction has the same asymmetry and the same treatment.

**Order**: directories created before their contents, and an empty directory in the
source is an empty directory in the destination. That sounds too obvious to write down
and is the most commonly skipped part of a recursive copy.

**Collisions** get four answers and no fifth: overwrite, skip, rename, or compare and
take the newer. The dialog shows both sides with size and time, and offers to apply the
answer to the rest — a user answering the same question four hundred times will pick
whichever option ends it soonest, and that is a data-loss mechanism.

Overwrite writes to a temporary name and renames into place wherever the server allows
it, so an interruption does not leave a truncated file where a complete one used to be.

Falsified when an interrupted overwrite destroys the destination file.

### §QS63 Kept for the appliance, and honest about why

The non-goal is already written: SCP is not the primary transfer path, because it has no
directory listing, no resume, no reliable progress and a long history of
filename-handling flaws — and OpenSSH itself moved its own `scp` onto SFTP for those
reasons.

What it is kept for is narrow and real: an embedded device, a network appliance or an
old server whose sshd offers no SFTP subsystem. For those hosts, SCP is the difference
between transferring a file and not transferring one.

So it is a fallback, offered when the subsystem request is refused, and it announces
itself rather than switching silently. The user is told which protocol is in use and
what it costs them: no listing, no resume, and progress that is an estimate.

The implementation is deliberately minimal — send and receive a file or a directory,
nothing else. The browser does not run on top of it, because a browser needs a listing
and this protocol has none; the usual workaround is to parse the output of a shell
command, which is exactly where the filename injection flaws live and is not somewhere
this client is going.

Filenames are escaped for the remote shell without exception, since this protocol's
entire vulnerability class is a filename that becomes a command.

Falsified when a filename containing a shell metacharacter transfers without escaping.

### §QS64 The gesture users try before reading anything

Two drop targets, and they mean different things.

Dropping onto a **file browser pane** is a transfer into the directory shown, which is
the obvious case, and it joins the existing queue like anything else.

Dropping onto a **terminal pane** is not a transfer. It types the path, quoted for the
remote shell — because what a user dropping a file onto a shell prompt wants, nine times
in ten, is the path as an argument. A modifier turns it into a transfer to the working
directory instead, and that difference is stated in the reference rather than left to be
discovered.

Dragging *out* of a remote pane to Explorer is a download, and it is the harder
direction: Windows wants the data during the drop rather than afterwards. Deferred
rendering through `CFSTR_FILECONTENTS` is the mechanism, and where a file is large
enough that the drop would stall, it is queued and the drop completes against a
placeholder.

Dragging between two remote panes on different hosts transfers through this client, and
it says so — the user may reasonably have assumed the two servers were talking to each
other.

Several files and directories are one operation, one group in the queue, one progress
figure.

Falsified when a path typed into a terminal by a drop is not quoted for the remote
shell.

### §QS65 Comparing two trees before touching either

Synchronising is a comparison followed by a transfer, and the comparison is the part
worth designing.

Both trees are walked and compared on size and modification time, with tolerance for
clock skew and for filesystems whose timestamp resolution differs — one-second
granularity on one side against hundred-nanosecond on the other will otherwise report
every file as changed and the feature is useless. A content comparison is offered
wherever the server can compute a hash, and it is opt-in, since it costs a full read of
both sides.

The result is shown before anything is transferred: what is new, what changed, what
exists only on the destination. Nothing runs until the user has seen that list. A sync
that acts first and reports afterwards is a sync that deletes something.

Direction is explicit — upload, download, or mirror — and mirror, which deletes on the
destination, takes its own confirmation naming what will be deleted.

A two-way sync with conflict resolution is deliberately out of scope. It needs a change
history this client does not have, and guessing in its absence is precisely how a
two-way sync loses somebody's work.

Filters exclude by pattern, using a syntax people already know rather than one invented
here.

Falsified when a mirror deletes anything the user was not shown first.

## Block F — A forward is a lifecycle, not a checkbox

### §QS66 Listen here, connect there, and the parts that go wrong

A local forward listens on a local address and, for each accepted connection, opens a
direct-tcpip channel to a host and port resolved by the *server*. That the resolution
happens on the far side is the whole point, and the thing users most often
misunderstand: the target name is resolved in the remote network's DNS, never in the
local one.

Binding is a decision with a security consequence. The default is loopback only, because
binding to all interfaces turns the user's laptop into an open route into the remote
network for anybody on the same cafe wifi. Binding wider is possible, and it warns.

Port zero means the operating system chooses, and the chosen port is reported back —
which is what lets several forwards to the same service coexist without the user
allocating ports by hand.

Each accepted connection is its own channel, so a forward carrying twenty connections is
twenty channels and one closing disturbs none of the others. Half-close propagates in
both directions, since a protocol that shuts one direction down and waits — and many do
— hangs otherwise.

Three errors must be told apart: the local port is already in use, the server refused to
open the channel, or the target refused the connection. Three remedies, one symptom.

Falsified when a forward binds beyond loopback without the user asking for it.

### §QS67 The direction where the server holds the veto

A remote forward asks the server to listen and to send each accepted connection back as
a channel. Everything about it mirrors a local forward except the part that matters: the
server decides whether it happens.

`GatewayPorts` on the server governs whether that listener binds beyond the server's own
loopback, and it is usually off. So a forward that appears to succeed and is unreachable
from a third machine is almost always this setting — and the client says so, rather than
leaving the user to go and read `sshd_config` to find out.

Port zero here means the server allocates, and it reports the port in its reply. Reading
that reply is what lets the client show the user the actual port instead of the zero
they asked for.

Incoming channels are handled as they arrive, each connected to the local target, with
no assumption about how many arrive at once.

The forward's life is the connection's life, and a server that fails to clean up a stale
listener is a real situation rather than a hypothetical — so a reconnect explicitly
re-requests, and a request refused because the port is still held says that, not
something generic.

Refusal is the common case here rather than the exception, and every refusal carries the
server's own stated reason wherever it gave one.

Falsified when a refused remote forward is reported without the server's reason.

### §QS68 One forward that covers a network

Dynamic forwarding is a SOCKS proxy served by the client: the application says where it
wants to go and the client opens a channel there. One forward covers everything
reachable from the remote host, which is why this is the forward a browser or a cloud
CLI actually wants.

SOCKS5 with no authentication on loopback is the working configuration, and SOCKS4a is
supported because old tools still speak it. `CONNECT` is the command that matters;
`BIND` and UDP associate are not implemented and are refused cleanly rather than left to
time out.

Hostname targets are passed to the server unresolved, and that is the whole security
property. Resolving locally leaks every hostname the user visits to the local network's
DNS, and it also breaks any name that exists only inside the remote network. A SOCKS
proxy that resolves locally is a common defect and a quiet one, because most names
happen to resolve both places.

Binding follows the local forward's rule — loopback by default, a warning beyond it —
with more force here, since this listener is a route into an entire network rather than
to one port.

Failures are reported with the correct SOCKS reply code, because an application handed a
generic failure retries forever instead of telling its user anything.

Falsified when a hostname is resolved locally rather than by the server.

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

### §QS46 What is on screen by default, and what is not

The window is the first argument this project makes. The incumbent opens onto a toolbar,
a sidebar, a status bar and an advertisement. quickshell opens onto a terminal.

So the default is a title bar, a tab strip that hides itself while there is one tab, and
the terminal. No toolbar, no status bar, no sidebar until a user opens one. Every
element added later is spending a budget this line is what establishes.

Theme is light, dark, or follow the system — and following the system means reacting
while running, not reading it once at start-up. The terminal's own colour scheme is a
separate thing from the application chrome's theme, because a user with a favourite
scheme wants it under either chrome; conflating the two is a common and irritating
mistake.

The window remembers position, size and maximised state per monitor configuration, so
plugging in a dock does not scatter it across a screen it can no longer see.

Start-up sits on the critical path for the cold-start figure, so the window appears and
is interactive before any session work begins, and the first paint waits on neither
configuration parsing nor a network call.

Closing with sessions open asks once, listing what is open, with a way to say never
again — and then honouring it.

Falsified when a default installation shows chrome beyond a title bar and a terminal.

### §QS47 Tabs, and the title the remote host is writing

A tab owns a session, its terminal state and its lifetime. Create, close, reorder by
drag, and detach into a new window — and a detached tab keeps its connection rather than
reconnecting, which is the observable consequence of the session living in the tab and
not in the window.

The title comes from three places in priority order: a name the user set, the title the
remote host is writing through OSC, and the session's host name. That middle source is
why tabs are worth building on top of the OSC work: a shell reporting its working
directory or its running command turns the tab strip into information rather than a row
of identical host names.

Closing a tab with a live session asks, unless the session already ended by itself. A
tab whose session died stays open showing why, with a reconnect — a tab that vanishes
takes the error message with it, which is the one thing the user needed.

Keyboard navigation is next, previous, by index, and most-recently-used. Those chords
are reserved from the remote program, and that cost is stated in the keybinding
reference rather than discovered.

A tab shows activity: output arrived while it was hidden, or the session dropped. A dot,
not a badge with a count, because a terminal has no meaningful unit to count.

Falsified when detaching a tab reconnects its session.

### §QS48 A tree of panes, and who owns the focus

A tab holds a tree rather than a session: each node is a horizontal or vertical split,
each leaf is a session. Recursive, so any pane can be split again — the only model that
does not run out at some arbitrary depth chosen by whoever wrote it.

Sizing is proportional, so resizing the window preserves the arrangement's shape.
Dragging a divider sets the proportion; a chord equalises. A pane can be zoomed to fill
the tab temporarily and restored, which is the cheapest genuinely useful feature here:
it turns a cramped four-way split into a workable one without disturbing the layout.

Focus follows click and moves by direction from the keyboard, and directional movement
over a tree needs geometry rather than tree order — moving right means the pane whose
rectangle lies to the right, which is often not the sibling.

Closing a pane collapses its parent and gives the space to the remaining sibling.

Each pane is a full terminal with its own scrollback, size and title, so each resizes
independently and each notifies its own host. The resize path is now exercised several
times per window drag, which makes the debounce there load-bearing rather than tidy.

Falsified when directional focus movement follows tree order instead of screen position.

### §QS49 One device, many surfaces, one atlas

Panes multiply, and the naive arrangement multiplies everything along with them. The
device, the shaders, the constant buffers and — above all — the glyph atlas are
process-wide. Only the swapchain and the instance buffer are per pane.

The atlas is why this matters. Sixteen panes at the same font share one atlas and one
copy of every glyph; sixteen atlases would be sixteen copies of the same texture memory
and sixteen rasterisation passes over the same characters. Panes at different sizes
share it too, since size is already part of the cache key.

Rendering is one thread for all panes rather than a thread each. It waits on whichever
pane has damage, draws only those, presents only those. A thread per pane would multiply
both the context switching and the number of things contending for the device's
immediate context, which is not free-threaded and will serialise them anyway.

An invisible pane — another tab, a minimised window, an occluded one — draws nothing at
all. `DXGI_STATUS_OCCLUDED` from a present is the signal to stop; damage is what resumes
it.

Device loss now takes every pane at once, so recovery is process-wide, and each pane
rebuilds from terminal state that never touched the GPU.

Falsified when atlas memory in use scales with the number of open panes.

### §QS50 A good default beats a checkbox

The rule this surface is built on: prefer a good default to an option. Every setting is
a permanent compatibility contract, a line in a reference nobody reads, and one more
state combination a bug report can arrive in.

What is genuinely worth exposing: font family, size and fallback chain; the ligature
setting, since users are sincerely divided on it; the colour scheme; cursor shape and
blink; scrollback capacity; keybindings; and the terminal behaviours that are
host-dependent rather than a matter of taste — the paste warning, clipboard access from
the remote side, and the reported terminal type.

Settings are a file the user can edit *and* a UI over that same file, with the file as
the source of truth. That way it goes under version control, and support can ask for it.
The UI writes it back preserving comments, or the UI is not worth having.

Changes apply live. A font size that needs a restart is a font size nobody experiments
with, and experimenting is the entire reason to expose it.

Anything not on the list above needs an argument rather than a preference, and the
argument is a user's task and never a feature comparison — which is the parity non-goal
applied to the one surface where it is hardest to hold the line.

Falsified when a setting exists that no reference documents.

### §QS51 Reading the two formats that already exist

Nobody types twenty colours. Schemes circulate as files, in two formats that between
them cover very nearly everything published: the iTerm2 `.itermcolors` property list,
and the Windows Terminal JSON fragment.

Reading both is a small piece of work with a disproportionate effect. It means a user
arrives with the scheme they already use, on the first evening, instead of approximating
it and quietly resenting the result.

A scheme is twenty values: sixteen palette entries, default foreground and background,
cursor, and selection. Where a format omits one, the omission is derived by a stated
rule rather than guessed, and that rule lives in the reference.

The client ships a small set of defaults and no more. A scheme gallery is a maintenance
burden and an invitation to screenshots, and an import path makes it unnecessary.

Applying a scheme repaints existing scrollback — which works only because the model
stores colour roles rather than resolved values. This line is where that earlier
decision gets spent.

Contrast is checked and reported, never enforced: a scheme with unreadable combinations
is the user's choice to make, and a warning naming which pair is unreadable is more use
than a refusal.

Falsified when applying a scheme leaves existing scrollback in the previous palette.

### §QS52 The surface that lets the other surfaces stay small

A palette is the mechanism that makes the non-goal about toolbars affordable. Every
action the client can perform is reachable by typing part of its name, so an action does
not need a button in order to be findable, and the interface stays as empty as the
window line promised it would.

It lists sessions to connect to, tabs and panes to switch to, settings to change, and
the client's own commands. Fuzzy matching over the name, ranked with recency, because
what a user wants is usually what they wanted recently.

Each entry shows its keybinding where it has one, which turns the palette into how
chords are discovered rather than something a user has to read a reference to learn.

It opens on a chord, closes on escape, and never takes focus without being asked. It has
no configuration of its own.

The discipline that keeps it useful is that the action list is generated from the
actions themselves rather than maintained alongside them. A hand-maintained list drifts,
and a palette missing a third of the actions is worse than no palette, because the user
stops trusting it and never comes back.

Falsified when an action exists that the palette cannot reach.

### §QS53 Typing once into several hosts, visibly

Input broadcast sends what is typed to several panes at once. It is one of the few
incumbent features that earns its place unarguably, because the alternative is a person
typing the same command eight times and getting the seventh one wrong.

The target set is explicit: the panes in this tab, a selection the user made, or a saved
group. Never all sessions everywhere — the mistake this feature enables is precisely a
command reaching a host the user did not have in mind.

While it is on, the client says so unmistakably: the panes receiving input are outlined,
and the state is visible without hunting for it. This is a mode, and an invisible mode
that sends keystrokes to production hosts is the worst kind of mode there is.

Each pane keeps its own output, which is the point. Eight hosts answering differently is
the information the user was after.

It ends when the tab loses focus or the user turns it off, and it never survives a
restart, because a mode restored from a previous session is a mode nobody remembers
enabling.

Falsified when broadcast is on and any receiving pane is not visibly marked.

### §QS54 Publishing a buffer nothing else can see

Everything this renderer does well is what makes it invisible to a screen reader. There
are no controls, no text elements and no automation tree — there is a texture. So the
accessibility surface is built rather than inherited, and if it is not built it does not
exist.

A UI Automation provider over the terminal exposes the buffer as a text pattern: the
visible screen and the scrollback as one document, with ranges so a reader can move by
character, word and line, and the cursor exposed as the caret.

Change notification decides whether this works in practice. Output arriving raises
text-changed events, and those must be throttled by the same reasoning that governs
frames — a screen reader handed one notification per row during a `cat` says nothing
useful for a minute, by which time the user has lost the session.

The cursor moving raises a caret event, which is what lets a reader follow a shell
prompt as the user types.

Everything else in the shell — tabs, dialogs, settings — is built from framework
controls that already carry names and roles, so the work there is labelling, and it is
done as those surfaces are built rather than swept up here at the end.

Falsified when a screen reader cannot read a line of output that is on screen.

### §QS83 Borrowing a design system rather than rediscovering one

Two shipped clients in this family have already answered this, and what they share is a
pattern rather than a library - which is the only reason it can be borrowed at all.

Colour is declared once as bytes - freewilly's `Palette.cs`, claude-tray's `Brand.cs` -
because no single type serves every edge: the tray icon is GDI+ and wants a
`System.Drawing.Color`, the window is WPF and wants a frozen `Brush`, and markup wants
something `{x:Static}` can reach. Each edge converts. One `Theme.cs` makes the
application and merges one `Theme.xaml`; `ThemeMode="System"` stays on each window and
never moves to the application or to code, which freewilly settled with four captures.
`RowStyle` shapes a row, and each screen is a page in a page window.

quickshell adds a third edge to the colour rule and nothing else: the pane is D3D11 and
wants floats, so the same bytes feed the brush, the icon and the clear colour, and the
terminal's own palette and the chrome's accent stop being two decisions.

The boundary is where the pane starts. The grid is D3D11 and a non-goal forbids WPF text
in it, so the design system covers tabs, settings, the palette, the session tree and
every dialog, and stops at the pane's edge.

One consequence is already measured. With a child HWND per pane, anything over a pane
must be a popup or drawn by the pane itself; an adorner will not appear.

Falsified when two windows in this repository declare the same colour.

## Block H — The reason to leave the incumbent

### §QS74 A format that can change without breaking anyone

Settings, the session store and keybindings are files. Text, commented, hand-editable,
under the user's own control — which is what lets them be versioned, shared and diffed,
and what makes support possible without screenshots.

Every file carries a schema version from the first release, because the alternative is
discovering at version 1.4 that no key can be changed without breaking every
installation that exists. Migration is forward-only, runs on load, and writes a backup
of the original beside it before touching anything.

An unknown key is preserved rather than dropped. A user running a newer build on one
machine and an older build on another must not have their settings silently pruned by
the older one, which is exactly what discarding unknown keys does.

Portable mode is that file layout beside the executable instead of in the user profile,
chosen by a marker file. It is what the footprint argument implies in practice: this
client on a USB stick with no installation. Its consequence for credentials is already
stated in Block B and is not softened here.

Otherwise the location follows the platform's own convention, and the client can say
where its files are — a user who cannot find them cannot back them up.

Falsified when a key an older build does not recognise is lost on save.

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

### §QS76 Proving the window does nothing

The renderer was designed to do nothing when nothing has changed. This line proves it,
because a design intention is not a measurement, and this is the figure most likely to
have quietly regressed since it was designed.

The measurement is a window open, connected, with a shell at a prompt, untouched for ten
minutes: draw calls issued, CPU time consumed, GPU utilisation, and timer resolution
requested. The target is zero draw calls and no measurable occupancy.

The things that break it are known, and each is checked separately. A cursor blink is a
legitimate wake, its cost is bounded, and it disappears when blinking is off. A UI
framework animating something invisible is not legitimate and is a common cause. A timer
polling anything is not legitimate. Keepalive traffic wakes the process, and its
interval is a genuine trade against battery.

Requesting a high timer resolution is the one that is invisible and expensive: it
affects the whole system, so a client that raises it and never lowers it is costing
battery inside every other application on the machine too.

Several idle panes must cost what one costs, which is the occlusion and damage work
verified rather than assumed.

The comparison against the incumbent on the same machine is part of the result, since
this is the claim the project is built on.

Falsified when an idle window issues a draw call or holds a raised timer resolution.

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

The harness and the budgets have both existed for a long time by this point, and the
numbers have been watched long enough for their noise to be known. That is the
precondition this line waited for: a gate built on a measurement nobody trusts gets
disabled inside a month, and then there is no gate and no measurement.

CI runs parse throughput, frame cost, the allocation assertion and cold start on a
consistent machine, and fails a build that regresses beyond a threshold. The threshold
is derived from observed variance rather than chosen, and it is written down.

A deliberate regression is allowed and is an explicit act: a marker in the commit naming
which figure moved and why, so the history records the trade that was made. Silence is
what is refused here, not the regression itself.

Results are published per commit, so gradual drift shows as a trend. The failure a
threshold cannot catch is one per cent on every commit for a year, and only the trend
reveals it.

The allocation assertion is the strictest of the four because it is exact rather than
statistical: zero is zero, and no noise threshold applies to it.

Falsified when the gate is disabled to land a change and the disabling is not itself a
filed line.

### §QS86 The figure the flags were bought for

QS7 bought the present path its three flags and proved one thing: with the waitable
object the frame queue is one deep. What it could not prove is what that is worth, and
the failed attempt is the useful part of this line.

Two controls were run and neither discriminated: latency one against three both averaged
0.98 frames queued, and waiting on the handle against not waiting also both averaged
0.98. The reason is not the flags but the workload - one clear per frame with a vsync
present, where `Present` blocks on the flip and the application can never get ahead of
the display. A queue cannot be deep if nothing is ever queued.

So the figure the budget opens with - input to photon - has never been measured on this
client, and the flags that exist to bound it are an argument rather than a number.

What closes this is a frame with real work in it: a grid drawn from an atlas, which is
what QS9 lands. Then the two arms differ, because an application that spends
milliseconds per frame is one the runtime can queue ahead of, and the wait is what stops
it.

The display bounds the absolute answer at 16.7 ms, as PERFORMANCE.md records, so what
this line settles is the shape - one frame against several - not the eight milliseconds
a 120 Hz panel would allow.

Falsified when this repository quotes an input-to-photon figure with no run behind it.

## Block I — An error a user can act on

### §QS71 A log worth sending, which means a log without secrets

Two levels of detail and one hard rule.

**Ordinary logging** records the shape of what happened: connections opened and closed
with their outcome, authentication methods attempted and their results, channels opened,
forwards started and stopped, transfers, and every error in the same wording the user
saw. On by default, at a level that costs nothing, and it is what a bug report needs.

**A transport trace** is the second level, off by default and enabled per session:
version exchange, algorithm negotiation with what each side offered, key exchange, and
the sequence of channel operations. This is what diagnoses an appliance that will not
negotiate, which is the failure class this client will meet most often.

The hard rule is that neither level may contain a secret. Passwords, key material,
passphrases, agent responses and channel contents are redacted *at the point of writing*
rather than filtered afterwards — a filter is a list of things somebody remembered, and
the forgotten one is always the one that matters. Payloads are logged as lengths and
types, never as bytes.

Logs rotate by size against a bounded total, because a trace left running overnight must
not fill a disk.

The file's location is discoverable from inside the client, since a log a user cannot
find is a log that does not exist.

Falsified when any secret appears in a log at any level.

### §QS72 The last second, and what is kept from it

Crashes happen, and this client has unusually good reasons to expect them: a graphics
driver, a native interop boundary, and a protocol library parsing input chosen by a
remote machine.

An unhandled exception writes a report before anything else — the exception, the stack,
the version, the GPU and driver in use, the number of open sessions, and the last log
entries under the same redaction rules the log itself uses. Written to a file, locally.
Nothing is sent anywhere.

Then it tells the user plainly that the client crashed, where the report is, and what it
was doing at the time. A silent disappearance is the outcome this line exists to
prevent, because a user whose client vanished has nothing to report and nothing to
report it with.

Sending is entirely the user's own act: a button that opens the file so they can read it
before deciding. A report a user has not seen is a report they should not be asked to
send. This is the telemetry non-goal applied at the exact moment it is most tempting to
break.

A render-thread failure gets separate handling, since a lost device is recoverable and
must not be reported as a crash. Drawing that distinction carefully is what stops the
reports filling with the one failure that is already handled.

Falsified when a crash exits with no report and no message.

### §QS73 One action that produces everything a maintainer will ask for

A defect report costs several round trips because the first message never carries what
is needed. This is one action that collects it: version and build, Windows version, GPU
and driver, the render backend actually in use and whether it fell back, the negotiated
algorithms of the affected session, the configuration with secrets removed, the recent
log, and any crash reports.

It writes one file the user can inspect before sending, and inspecting it is the point —
the same reasoning as the crash path, and the reason this is not a button that uploads.

The terminal has one thing to add that nothing else can: recording a session's raw byte
stream. Terminal defects are close to impossible to describe and trivial to reproduce
from bytes, so a recording turns *the box drawing looks wrong on this router* into a
file that reproduces it on a maintainer's machine. Per session, explicit, visibly
indicated while running, and it captures output only — input is what the user typed, and
may be a password.

That recording is also a corpus entry, so a defect found this way becomes a regression
test by moving one file rather than by writing one.

Nothing here is automatic and nothing is sent.

Falsified when a recording captures typed input.

## Block J — Leaving MobaXterm, proven by the switch

### §QS80 Reading the incumbent's files

The barrier to leaving is not features. It is the two hundred sessions somebody has
accumulated over five years. So this reads them.

MobaXterm keeps sessions in an INI file in a positional format. PuTTY keeps them in the
registry under its own key, one subkey per session, and the PuTTY-derived clients follow
it. Both are readable, and reading them is the single highest-leverage piece of work in
this block.

What maps cleanly: name, folder, host, port, user, key file, and the terminal settings
with an equivalent here. What does *not* map is at least as important, and it is
reported per session rather than dropped: X11 settings, macros, and everything else the
non-goals already refuse. A user who imports and is told what was not carried over has
an accurate picture; one who is told nothing discovers it three weeks later and blames
the client for hiding it.

Keys are referenced rather than copied, and a `.ppk` is read where it lies rather than
converted — converting somebody's key without being asked is the kind of surprise that
costs trust in the first ten minutes.

Import is previewed before it writes: what will be created, where, and what will be
skipped. Nothing lands unseen.

Falsified when an import silently drops a setting the source file carried.

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

### §QS89 What dotnet test does with this tree that the assembly does not

QS88 delivered a command whose exit code is the verdict; it is not this one, and this
one is still wrong. What is known, measured on 2026-08-22 against SDK 10.0.303 and
xunit.v3 4.0.0:

The test assembly run directly — `bin\Debug\net10.0-windows\*.Tests.exe`, or through
`dotnet exec` on its dll — discovers and passes every test and exits 0. Handed the same
tree, `dotnet test` prints "zero tests ran" and exits 5, in about 150 ms, having plainly
never started the assembly.

`Platform` correlates with it. Before `AppendPlatformToOutputPath` was turned off,
`dotnet test <csproj>` passed and `dotnet test <csproj> -p:Platform=x64` and `dotnet
test Quickshell.sln` both reported zero. After it, all three report zero. So the
platform segment in the output path is part of the story and not all of it.

`global.json` already declares the Microsoft.Testing.Platform runner, the projects now
build against it, and `Microsoft.Testing.Extensions.MSBuild` is in the output — so this
is not a project that forgot to opt in.

The three candidates worth separating: the path the SDK's MTP integration launches the
app from under a non-default platform; a protocol version between that integration and
the one xunit.v3 4.0.0 carries; and something in `Directory.Build.props` that only bites
when MSBuild is the launcher. Each is answerable by one run.

Falsified when `dotnet test Quickshell.sln` reports the same count as `run-tests.cmd`
and exits zero, and non-zero when a test is broken.

### §QS90 The clone that has never been made

Block K asks that a clean clone build and pass with nothing taken from memory. That has
never been checked here, and the checking is the whole task: what a machine already has
is invisible from on top of it.

The specific things this tree might be leaning on without saying so. A NuGet cache that
already holds Vortice, xunit and their transitive graph, so a restore that would fail
behind a proxy succeeds here. A D3D debug layer `GraphicsDevice` asks for and quietly
does without — the fallback is deliberate, but nobody has watched it taken. A
`global.json` pinned to 10.0.100 with `rollForward` at `latestFeature`, satisfied here
by 10.0.303 and by nothing on a machine carrying only the pinned one. And DirectWrite
finding Consolas, which every Windows has and no container necessarily does.

The check is one run: clone into an empty directory on a machine carrying only the .NET
SDK, run `run-tests.cmd`, and read the count. What it turns up goes in a README naming
the prerequisites, or into the repository as a step — a requirement discovered and then
written only into a commit message is one the next machine still will not meet.

CI is the closest thing that exists and is not the same: the runner image carries a
Windows SDK and a warm tool cache, and has never been asked what it used.

Falsified when the clean clone needs a step this task did not name.

### §QS99 QS

The suite is run by invoking the test assembly directly, because `dotnet test` prints
nothing (QS89). That works, and it has one failure mode that has now cost two debugging
cycles: when the build ahead of it fails, the assembly from the last successful build is
still sitting there, and running it prints a full green summary for code that was never
compiled.

Both times the green summary was believed for a moment. The first time an analyser error
(CA1823, an unused field left by a deliberate probe) failed the build; the second time a
shell short-circuit meant the patch step never ran at all. In each case the output was a
confident `total: 201, failed: 0` describing a binary from several minutes earlier.

The fix is that nothing should be able to report a pass for a stale binary. The runner
compares the assembly's write time against the newest source file feeding it and refuses
to run when the source is newer, saying so rather than testing. That is a cheaper check
than reading build output carefully every time, and unlike careful reading it cannot be
skipped when the run looks routine.

Falsified when a build that fails is followed by a run that reports a pass.

### §QS102 Continuous fuzzing, and why the suite's mutator is not it

QS24 ships a deterministic mutator inside the test suite: three thousand mutations
seeded from the captured streams and from twenty named pathological shapes, at a fixed
seed so a failure is reproducible from its iteration number. That is the right thing to
run on every build — bounded, fast, and it fails the build.

It is not fuzzing. A bounded run at a fixed seed explores the same three thousand inputs
for ever, and finds only what those inputs find. What the design asked for was SharpFuzz
over libFuzzer, which instruments the assembly and steers mutation by coverage — the
difference between checking a list and searching a space.

Why it was not shipped with QS24: libFuzzer on Windows needs a prebuilt driver binary
that is not on NuGet, the run is unbounded so it cannot live in the one test command,
and a corpus that grows across runs needs somewhere to live. Each of those is a decision
about the harness rather than about the parser, which is why this is Block K.

What it owes: the instrumented build, the driver, a seed corpus taken from the captured
streams, a place for findings to land as new seeds, and a way to run it that is not a
developer remembering to. A crash it finds becomes a case in the suite's own list.

Falsified when a crash found here is not reproducible from the suite afterwards.
