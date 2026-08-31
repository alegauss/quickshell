# Improvements

## Block A — A session that stays up, or says why it did not

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

### §QS110 The other half of the comparison

QS37's design asks for throughput and allocations "both against the same figures taken
locally". The remote half landed and is asserted on: 32 MB of `cat` through the fixture
at **124.8 MB/s and 233 KB allocated per MB**, against QS5's 81–103 MB/s and 112–126 KB.
The local half did not, and what stopped it is worth writing down rather than retrying
blindly.

Two things, both found by measurement. A line typed at `cmd.exe` behind a pseudo-console
needs a carriage return; a line feed is accepted by a Unix pty in canonical mode and
silently ignored here, so the command was never submitted and the reader waited out its
whole deadline looking like a dead channel. And draining the login banner by cancelling
a read after a few hundred milliseconds **aborts the pipe**: `ConPtyChannel.ReadAsync`
hands the token to a Windows pipe read, and a cancelled one does not resume. After that
the channel is open and permanently silent.

The second is the interesting one, because it is a property of the shipped local channel
and not of the test. Whether a cancelled read is recoverable is not stated anywhere and
the session loop is entitled to assume either answer.

What this needs: a local figure taken without cancelling anything, the two printed side
by side, and `ConPtyChannel` either surviving a cancelled read or saying in its own
words that it does not.

Falsified when a local figure is quoted without saying which machine and which day
produced it.

### §QS111 A keepalive that keeps and does not detect

QS38's design says "protocol-level keepalive at a configurable interval detects a dead
peer in seconds rather than in the operating system's own good time". Measured against
the fixture, that is not what SSH.NET's `KeepAliveInterval` does.

The run: connect with `KeepAlive` at one second, run a command to prove the link works,
then `docker pause` the container — frozen rather than killed, because a killed process
closes its socket and that is the easy case. A paused one leaves the connection exactly
as a vanished network does, open and answering nothing. Thirty seconds later, twice:
**the transport had not noticed, the channel had not noticed, and `IsConnected` still
answered true.**

The reason is structural rather than a setting. A keepalive that expects no reply cannot
detect anything: sending succeeds because the kernel buffers it, and it will go on
succeeding until TCP retransmission gives up, which is the operating-system timeout the
design wanted to avoid. What detects a death is a request the server is obliged to
answer — OpenSSH's own clients use a global request `keepalive@openssh.com` with
`want_reply` set — and the library exposes no way to send one.

What it does do is real and worth keeping: traffic on the socket holds a NAT mapping
open, which is what stops an idle session dying after twenty minutes on a corporate
link. That half is tested.

Falsified when a frozen peer is reported as dead by anything that did not require an
answer.

### §QS112 Two timeouts the async path cannot tell apart

QS39's design asks for the connection timing out to be reported "distinguishing a
connect timeout from a handshake timeout". Measured, that distinction is available and
costs something QS39 was not willing to pay.

Through `SshClient.ConnectAsync`, an address routed nowhere (192.0.2.1, TEST-NET-1) and
a socket that accepts and then says nothing both produce `SshOperationTimeoutException`
carrying the same sentence: **"Connection has timed out."** Same type, same wording,
nothing to read.

Through the synchronous `Connect()`, they differ: **"Connection failed to establish
within 3000 milliseconds"** against **"Socket read operation has timed out after 4000
milliseconds"**. That method takes no cancellation token, so choosing it would mean a
connection attempt a user cannot abandon — which is a worse thing to be than a message
that covers two readings.

So the message covers both, honestly, and the remedy names both checks. Naming the wrong
one confidently is what this avoids: sending somebody to inspect a firewall when the
port is simply wrong wastes more of their time than saying it might be either.

What would close it: `Connect()` on a thread with the token abandoning the wait rather
than the connect, which is the same trade QS37 made for reading and is defensible here
too; or a version of the library whose async path keeps the wording. Either way the
measurement above is what says whether it worked.

Falsified when the two are reported apart without a run showing they can be.

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

### §QS113 Progress through an authentication that takes two steps

QS41's design asks that a partial success — a key accepted with a second factor still
required — be "shown as progress" rather than treated as an error. The state is reached
and tested: the fixture's `twofactor` account is under `AuthenticationMethods
publickey,keyboard-interactive`, and a connection there completes both. What is missing
is anybody being told, in between.

Three things happen during that connection and none of them reaches a caller. The server
may send an authentication banner, which is its own words and is exactly the sort of
thing worth showing. The first method succeeds. The second method's prompts arrive —
those do reach the caller, through `SshCredential.Interactive`, which is why the
falsification test can assert them.

So the gap is narrow and specific: the two moments before the prompt. A user watching a
connection that takes six seconds because somebody has to approve a push notification is
watching nothing at all until the prompt appears, and a client that shows nothing there
is one a user assumes has hung.

This waits on the window, because progress is a thing that is shown. What belongs in the
transport is the report: a callback or a state on `ISshTransport` carrying the banner
when there is one, and the fact that a method completed with more still wanted.
`SshNetTransport` already subscribes to nothing for either, and
`ConnectionInfo.AuthenticationBanner` is the library's half of the first.

Falsified when a two-step authentication shows the same thing at second five as at
second one.

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

### §QS103 The one sequence that makes the emulator testable from outside

QS33 ran esctest against the model for the first time: 151 of 568 passed. Of the 375
failures, 228 are the same failure — the suite could not read the screen, so a test
about backspace or about a scrolling region fails for a reason that has nothing to do
with either.

The mechanism is DECRQCRA, `CSI Ps ; Pu ; Pi ; Pt ; Pl ; Pb ; Pr * y`: the host asks for
a checksum over a rectangle of cells and the terminal answers. It is the only way an
automated suite can see a screen it does not own, which is why esctest leans on it for
nearly everything.

This is not a fidelity feature for its own sake. It is what turns three hundred and
seventy-five failures into a number that means something: with it, each of those 228
either passes or names a real defect, and telling those apart is the whole value of
having run an external suite.

The algorithm is xterm's and worth being exact about: the negated sum of the cells'
characters, attributes optionally folded in, and the rectangle taken from the current
margins where parameters are omitted. Arithmetic subtly wrong is worse than no answer,
because the suite would report differences that are the checksum's rather than the
terminal's.

Falsified when a checksum reply differs from xterm's for a screen both have drawn.

### §QS104 Telling a program what is on, and what was never there

QS20 taught the terminal to report a setting when asked in DECRQSS's syntax, and left
the other half undone: DECRQM, `CSI Ps $ p` and its private form, which asks whether a
mode is currently set. Twenty-two esctest cases fail on the silence, and they are not
the interesting part — what silence costs a real program is.

A mode has four answers, not two: set, reset, permanently set, permanently reset. The
last two are how a terminal says *this is not a thing I have* as distinct from *this is
a thing I have and it is off*, and the difference decides whether a program falls back
or waits. Answering everything as merely reset is worse than answering nothing: a
program told a mode is off will try to turn it on.

Two is the answer for modes this client honours and has on, one for honoured and off,
four for the ones it will never have — which by now is a list it can state, a mouse
encoding it refused, an inline-graphics protocol that is a non-goal. Zero is for a mode
it has never heard of, and is the honest answer for anything not enumerated.

The reply is built from a number and a constant, so it goes down the path QS19 built and
carries no byte the host supplied.

Falsified when a mode this client refuses on purpose is reported as merely off.

### §QS105 The wrap a cursor has to be able to go back through

Found by esctest in QS33, in the eight `BSTests` and two `CUBTests` failures that are
not about the checksum: reverse wrap.

Moving left from column one does not always stop. Where the row above continues into
this one — which the wrapped flag already records, and which QS23 and QS30 both lean on
— the cursor belongs at the end of that row instead. A shell editing a command longer
than the terminal is wide does exactly this on every backspace over the wrap point, and
a terminal that refuses leaves the cursor and the shell's own idea of the cursor in
different places. Everything typed afterwards lands somewhere neither of them meant.

It is a mode, DECSET 45, and off by default in xterm — but a client that never
implements it cannot honour the mode either, and the tests that fail here are the ones
that turn it on and then check.

The interaction to be careful about is the left margin: with one set, reverse wrap goes
to the margin and not to column one, and the row above is only a candidate if the wrap
actually happened there. The pending-wrap state QS17 keeps is part of the same question,
since a cursor owing a wrap is not yet in the row it appears to be in.

Falsified when backspacing over a wrap point puts the cursor somewhere the host does not
also think it is.

### §QS107 The half of the thinness that is not the coverage

QS35 answered the stated cause — grayscale coverage — and left a second one standing
that its own design named and this did not do: "DirectWrite's contrast enhancement has
to be carried through rather than dropped on the floor".

`IDWriteRenderingParams` publishes `Gamma`, `EnhancedContrast` and `ClearTypeLevel`.
`IDWriteFactory2::CreateGlyphRunAnalysis` takes none of them, so the coverage
`CreateAlphaTexture` returns has had none of them applied: they are the client's to
apply, and Direct2D applies them inside a shader whose curve Microsoft does not
document.

Gamma is arguably already handled and handled better. This renderer mixes coverage in
linear light, which is what gamma correction exists to approximate, so carrying
DirectWrite's gamma across would be correcting twice. Enhanced contrast is the part with
nothing standing in for it: a deliberate perceptual boost that thickens stems, and thin
stems are exactly the symptom QS35 was filed under.

What this needs is a measurement rather than a guess at the curve. Draw one glyph run
through Direct2D into an offscreen target, draw the same run through this renderer, and
difference them. If the pictures agree, the boost is not being applied by D2D either and
there is nothing to carry. If they differ, the difference is the size of the thing to
fix and a lookup table fitted to it is honest where a guessed exponent is not. Direct2D
used only as the reference a test compares against is not a second backend.

Falsified when the two pictures differ and the number is not written down.

### §QS108 Measured against the suite's own build

Two wall-clock tests in `Quickshell.App.Tests` failed on 2026-08-30, one each on
separate full-suite runs: `TheDelayBeforeAReadIsParsedDoesNotGrowWithTheFile` and
`AKeystrokeLeavesAsFastUnderALargeFileAsAtRest` — the second at 20.8 ms against a 2.6 ms
bound.

What was measured rather than guessed. The assembly alone, three runs: no failures. The
assembly alone straight after `dotnet build Quickshell.sln`, two runs: one failure. One
test alone after a build, five runs: no failures. So the trigger is a build followed by
the whole assembly's work, and `Quickshell.App.Tests` is the first directory
`run-tests.cmd` iterates — it is the one that always runs into it.

What a build leaves behind, counted: about thirty resident `dotnet` processes, MSBuild's
node reuse keeping workers alive for fifteen minutes, and a `VBCSCompiler` holding 1.1
GB with over two thousand CPU-seconds against it. Building with node reuse and shared
compilation off gave three clean runs, which at that sample size against a roughly
one-in-two failure rate is suggestive and not decisive.

The fix has two halves and they are separable. The harness should not leave its own
build resident while measuring. And a latency assertion should be able to say "the
machine was busy" as something other than "the code regressed" — a floor on the at-rest
reading, a retry that reports both, or a statistic sampled rather than a single worst
case. [[QS106]] is the narrower flaw in one of the two.

Falsified when a build with node reuse off still fails at the same rate over twenty
runs.

## Block D — The tree a user organises work in

### §QS117 A file that reads by hand and writes by machine

QS55 committed to a format that is "human-readable, diffable and documented, so it can
go under version control and be edited without this client running at all", and shipped
half of it properly. Reading accepts comments and trailing commas, so a file somebody
typed is a first-class one. Writing serialises the tree, so anything that is not the
tree — every comment a person wrote to explain why the staging box uses a different jump
host — is not in the output.

The falsification QS55 was given is that the store cannot be edited by hand and
reloaded, and that passes. The one this leaves is narrower and lands on the same user:
somebody comments their file, renames a folder through the palette, and the comments are
gone with no warning.

Three ways out, in order of cost. Never write the file from the client, making every
change an instruction the user applies — honest, and unusable. Keep the parsed document
with its trivia and write back through it, which `System.Text.Json` cannot do and a
format with a syntax tree can. Or move to a format whose .NET libraries round-trip
trivia, which means a dependency and a migration for anybody who already has a store.

Until then the behaviour is written where somebody editing the file will meet it, in
`SessionTree`'s own summary.

Falsified when a file with comments is written by the client and still has them.

### §QS119 A door held open on the loopback

SSH.NET offers no way to hand a session a stream, so both routes through a bastion end
at the same shape: a `ForwardedPortLocal` bound to `127.0.0.1:0` for a jump, and a
`TcpListener` on `127.0.0.1:0` for a proxy command. The nested session connects there
and travels on inside the carrier. End to end the traffic is still the target's own
encryption and the bastion sees none of it, so the confidentiality claim holds.

What does not hold is exclusivity. While the session lasts, that port is open to every
process running as this user. Anything connecting to it is speaking to the target's sshd
— it must still authenticate, so this is no way past the target's own credentials, but
it is a way past the bastion, which is the control the user was relying on. A machine
reachable only through a jump host has, for the life of the session, a direct route from
this desktop that nothing audited.

The forwarded port is also accepted more than once. A jump is one nested session and a
proxy command is exactly one, so a second connection is by definition not the client's.

What would close it: bind and accept once, then refuse; or match the accepted socket's
owning process against this one. Neither is offered by `ForwardedPortLocal`, so the jump
path may need the same hand-built listener the proxy path already has.

Falsified when a second process can connect to a live jump's bound port and reach the
target.

### §QS121 Two finished halves with nothing between them

QS55 built the tree and QS58 the dialog over it, and neither is reachable from the
running application. `SessionTree.ReadFrom` and `WriteTo` take a path that nothing
supplies, and `SessionDialog` is constructed by its tests and by nothing else. The
window has no menu, no key bindings and no session list, so the roadmap's original
symptom — the store can only be built by editing its file — is still true of the shipped
program.

Three decisions are missing, and each is a decision rather than plumbing.

Where the file lives. It is the artefact a user builds over years, so it wants a path
they can find, back up and put in a repository, and a default under `%APPDATA%` is a
file most users never learn they have.

What opens the dialog. A session list is the obvious answer and is a design in its own
right: it is the tree made visible, with the folders that carry the inheritance QS58's
fields report.

When the file is written. Saving on every edit loses a hand-edit made while the client
is running; writing on exit loses everything to a crash. QS117 already carries what a
write does not preserve, and this decides when one happens at all.

Falsified when a user can create a session, close the client, reopen it and connect to
that session without touching a file.

## Block E — SCP and SFTP as a thing a person operates

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

### §QS122 Six names holding up a security property

Sharing one connection between the shell and the file browser is not offered by
SSH.NET's public API, so `SharedSftpSession` reaches for four members by name:
`BaseClient.Session`, `SftpSession`, `SftpResponseFactory` and
`SftpClient._sftpSession`. Two more, the session's own remove and rename requests, are
reached for the same way.

It fails loudly rather than falling back, which is the safe direction: a fallback would
open a second connection and cost a second authentication without saying so. But loudly
means at runtime, against a server. Every test that would catch a break skips when the
fixture is not up, so `dotnet build` after an SSH.NET upgrade is clean and the break
waits for a user.

Two things would close the gap, and they are cheap next to what they guard.

A test with no server in it, asserting only that the six members resolve on the
referenced assembly. It runs everywhere, including in CI without docker, and it fails on
the upgrade rather than on the user.

A pinned version. The reflection is written against 2026.0.0 and nothing records that; a
floating reference would move underneath it silently.

Falsified when an SSH.NET upgrade that breaks the sharing passes a run with no fixture.

### §QS123 A link that cannot be read is a link that cannot be copied

SFTP has `SSH_FXP_READLINK` and SSH.NET does not expose it: not on `SftpClient`, not on
`ISftpFile`, and not on the internal `ISftpSession`, which offers `RequestSymLink` to
create one and nothing to read one. `Get` follows a link and reports the target's
attributes without saying what the target is called.

So a downward copy leaves every link out, with the reason attached. That is the honest
answer — a link recreated from a guess points somewhere nobody chose, and it looks like
it worked. It is not a good answer. A checkout, a set of dotfiles, or anything with a
`current -> releases/2026-08` in it arrives subtly broken.

Two ways to close it, and the second is better.

Send the request directly. `SftpSession` already carries the plumbing to send a message
and match a response, and the client already reaches into it for remove and rename. One
more member, and the same fragility QS122 describes.

Ask the shell. A session already has one, and `readlink -n` answers exactly this. It
costs a round trip per link and it uses only documented behaviour of the far side, but
it means a file operation depending on a shell that a restricted account may not have.

Falsified when a tree containing a symbolic link is copied down and the link is missing
from the result.

## Block F — A forward is a lifecycle, not a checkbox

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

### §QS124 Half of a close is not a close

Measured on 2026-08-30 against the fixture: a socket through the forward that calls
`Shutdown(Send)` finds the whole connection gone — `Connected` false, the next read
returning zero — instead of the far end's answer. SSH.NET's `ForwardedPortLocal` treats
either direction ending as the end of both.

That breaks a real class of protocol. HTTP/1.0 without keep-alive, several database wire
protocols, and anything shaped like `cat | remote-tool` send their request, shut the
sending half to signal the end of input, and wait. Against this forward they get a
closed socket and either hang until a timeout or report a network error, and the user
has no way to tell that from a server that went away.

The library offers no way to fix it from outside: the listener, the accept loop and the
channel all live inside `ForwardedPortLocal`, and nothing on its surface carries an end
of stream in one direction.

What would answer it is our own listener over a direct-tcpip channel, which is what
OpenSSH does. `ISession.CreateChannelDirectTcpip` exists and is internal, so this costs
the same kind of reach into the library that sharing an SFTP session did — and buys,
besides half-close, per-connection error reporting and a channel that fits the seam's
own `IForwardedChannel`.

Falsified when a connection that shuts its sending half still receives what the far end
sent afterwards.

### §QS125 Three failures, one of them legible

Measured on 2026-08-30. A forward to a port with nothing listening on the far side
closes the connection with no bytes and raises nothing at all: no exception, no event.
It is indistinguishable from a server that hung up normally. Only the local port clash
is reported cleanly, and that one is caught before any traffic flows.

So of the three remedies the design wanted to offer, two cannot be reached: a wrong
target port and a server that closed look identical, and the user is shown an empty read
either way.

Binding has the same shape of gap. `ForwardedPortLocal` resolves its bound host as a
name and refuses the unspecified address outright, and its constructor without a bound
host binds to whatever empty-name resolution returns first — measured here, a link-local
address other machines can reach. So there is no way to say "every interface", and the
one convenience constructor that looks like it says that says something worse.

Both fall out of the same cause: the accept loop and the channel belong to the library.
A listener of our own over a direct-tcpip channel sees the channel-open failure and the
socket that never connected as separate events, and binds where it is told. QS124 wants
the same thing for its own reason, so the two are one piece of work.

Falsified when a wrong target port and a closed connection produce the same message.

### §QS127 A proxy that answers wrongly is worse than no proxy

Measured on 2026-08-30 with twenty lines using SSH.NET alone, no code of this project
involved. Speaking SOCKS5 to `ForwardedPortDynamic` and reading the ten-byte reply:

    one client, one proxy, eight requests:       0 0 0 0 0 0 0 0
    one proxy, connects around a BIND:          -4 0 0 0 | BIND 0 | 0 0 0 0
    a fresh client and proxy each time:          0 0 -4 0 0 0 -4 0

Zero is a granted reply. Minus four is a reply whose first byte is not 5 — the connect
reply was never sent and the target's own first bytes arrived instead. It is not a race
with `BIND`, and not only the first request after opening; it worsens as a process opens
and closes more of them.

The same proxy also answers a `BIND` request with success and then connects, so an
application that asked to listen is handed a connection somewhere else.

Neither can be corrected from outside: the listener, the SOCKS conversation and the
channel all live inside the library. A front end that holds the SOCKS conversation and
passes only a connect through was written and thrown away — it inherits the
unreliability of whatever it forwards to.

So dynamic forwarding waits on the same listener of our own that QS124 needs, over a
direct-tcpip channel, speaking SOCKS here.

Falsified when a hundred connects through the proxy all receive a well-formed reply.

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

### §QS116 The three halves that have never met

Every piece is built and tested on its own. `CellRenderer` draws a grid in one call into
a `PresentSurface`. `SessionPipeline` carries bytes from a channel into an `Emulator`
and raises a `DamageSignal` when something changed. `TerminalPane` owns a child window
whose handle a swapchain can be created against. Nothing joins them, so the window QS46
opens shows a rectangle the colour of whatever was behind it.

What the joining needs, and none of it is speculative. A swapchain on
`TerminalPane.PaneHandle` rather than on a test window. A loop that waits on the damage
signal rather than on a timer, because QS12's criterion is that an idle window issues no
draw calls and a frame drawn on a clock is a frame drawn for nothing. Cells built from
the `TerminalBuffer`'s lines through the glyph atlas — the Painter in the golden tests
already does exactly this and is the shape of it, written in a test because there was
nowhere else for it to live. A resize that reaches three parties in the order QS32
settled. And keystrokes from WPF's input into `Emulator.Encode` and out through
`SessionPipeline.TypeAsync`.

The order matters for what it proves: cells from a real buffer first, because that is
the piece living in a test file rather than in the client, and every other piece is
already exercised somewhere.

Falsified when a window is drawing at a steady rate while nothing on screen is changing.

### §QS126 Machinery with no way in

Counted on 2026-08-30 across `src/Quickshell.App/*.cs`. Not one file names `SshChain`,
`SftpChannel`, `ScpChannel`, `IFileCopy`, `TransferQueue`, `TransferPlan`, `SyncPlan`,
`LocalForward`, `SshAgent`, `KnownHosts` or `TrustOnFirstUse`. `SecretStore` appears
once, in a doc comment. The application layer uses three transport types in total.

So jump hosts, host-key trust, agents, saved credentials, the whole of file transfer and
now port forwarding are shipped, tested against real servers, and reachable from nothing
a user can run. QS121 said this about the session store and the dialog; the audit says
it about four blocks.

This is not a missing feature. It is a missing question: every line opened so far asked
what a component must do and none asked who would open it. A component and its way in
are two pieces of work, and only one of them has been on the roadmap.

What closes it is not one task. It is a rule — a line that makes a component reachable
is opened beside the line that builds it — plus the connecting work already outstanding.
This line exists to hold the count and the rule until those are opened.

Falsified when a component ships with no line naming what will reach it.

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

### §QS130 What crosses the connection, and not only the connection

The log surface QS71 built already carries `Channel`, `Forward`, `Moved` and `Payload`.
Nothing calls three of them. The transport records a shell channel opening and closing;
the SFTP channel, the scp fallback, every forward and every byte counted are silent.

That is the wrong half to have. A dropped connection is visible in the window and the
user can say what happened. A transfer that stopped at 40% against one server, or a
forward that went away an hour into a session, is precisely the report that arrives as
"it sometimes doesn't work" — and the log is the only thing that could say the channel
closed, when, and with what error.

What to wire, all of it against methods that already exist: the file-transfer channel
and the scp fallback as `channel-open` and `channel-close` with their kind; each
forward's start and stop by its ports, including the one that stopped because the
session did; transfers as byte counts at completion rather than per chunk, since a
progress bar in a log file is a rotation nobody wanted.

The rule QS71 established holds without restating it: these methods take counts and
kinds, and there is no overload that takes a byte. A path being transferred is a
filename, which is the user's business but not a credential — record it.

Falsified when a transfer that failed halfway leaves nothing in the log.

### §QS131 The crash dialog says what its buttons do

QS72 tells the user through `MessageBox`, which is the right amount of machinery for a
process that is already dying — no window to build, no resources to load, nothing that
can fail a second time. It has one cost, visible the moment it was photographed on a
Portuguese Windows: the sentence is this client's English and the buttons are the
operating system's "Sim" and "Não".

Two problems, and the second is the larger. **The dialog is bilingual**, which reads as
a client that was not finished. And **"Yes/No" names neither action**: the question is
"Open the report now?", so the buttons should say "Open the report" and "Close" — naming
the act is what lets somebody answer without re-reading the sentence above it.

The fix is a small window of this client's own, and the constraint it inherits is the
reason `MessageBox` was chosen: it has to be constructible after an unhandled exception,
on a thread that may not be the dispatcher, with the application object possibly already
torn down. So it loads no styles, references no session state, and falls back to
`MessageBox` if constructing it throws — a dialog that fails to appear is worse than a
bilingual one.

While there, the report's own path is long enough to wrap awkwardly; a button that opens
the containing folder is usually what a person actually wants.

Falsified when the buttons on the crash dialog do not say what they do.

### §QS132 The adapter line, filled in

QS72's report carries a field for the adapter and a placeholder in it. That is not an
oversight in the report: the composing layer genuinely has no device to ask. The render
layer opens a `GraphicsDevice` where a pane needs one, and nothing at the window level
holds a reference — so `Entry.Doing` writes what is true rather than a name it guessed
at.

The cost is exactly where it hurts. `CrashKind.DeviceLost` exists to say a failure was
about the machine, and a device-loss report that cannot say which adapter, which vendor,
or how many recoveries had already happened is a report naming a category and no
evidence. `AdapterChoice.ToString` already renders the line wanted — which link of the
chain answered, the adapter's own description, and what was skipped to reach it — and
`GraphicsDevice.Recoveries` already counts the losses survived. Both are one reference
away.

So this is a wiring question and not a design one: whatever ends up owning the device
for a pane exposes it to the crash context, through an interface narrow enough that the
composing layer does not gain a second reason to know about D3D. A delegate returning a
string is probably the whole of it.

Do it when the pane holds a device, and not before — a hook with nothing on the other
end is a field that says "unknown" in a different way.

Falsified when a device-loss report cannot say which adapter was lost.

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
