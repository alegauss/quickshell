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

Hence the order. The exposure bites when the reader stops, and it stops because the
parser is slow — QS141. Fixing that shrinks it to the hostile case without closing it.

Falsified when an unread session grows without limit.</body>

### §QS140 A cap that is the number it says

`Emulator.MaximumReplyLength` is 4,096 and `Send` refuses to answer once `_reply.Count`
has reached it — then, having passed the check, appends a whole answer. So the buffer
can end at `MaximumReplyLength - 1` plus one answer's length. Two hundred thousand
undrained cursor-position requests reach **4,098** bytes, measured.

Two bytes is not a memory problem and this is not filed as one. It is filed because the
constant is documented as the bound — *"how much the terminal will owe the host before
it stops answering"* — and it is not the bound, which makes it the wrong number to
reason from. The next answer added to that switch could be longer than a cursor
position; a `DECRPSS` string reply is not two bytes, and nothing in the check knows how
long the thing it is about to append is.

The fix is to reserve headroom: refuse when the buffer plus the longest answer this file
can build would exceed the maximum. That wants the longest answer to be a stated
constant next to the cap, which is worth having anyway — right now it is a fact spread
across a switch.

`ParseRetentionTests.TheReplyBufferDoesNotGrowWithoutBound` asserts the present
behaviour with sixty-four bytes of slack and checks that answers really were refused, so
the growth property stays watched until this makes the number exact.

Falsified when the reply buffer exceeds the constant that names its maximum.

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

### §QS155 The mouse the host asked for, and the wheel

Counted while wiring QS30's selection: `src/Quickshell.App/*.cs` names neither
`MouseReporting`, nor the encoder QS21 shipped, nor `Viewport` at all. So a program that
turns mouse tracking on gets nothing — no click, no drag, no wheel — and the scrollback
exists in the ring with nothing able to look back into it.

QS30 already made the decision this depends on, and made it the way every terminal does:
the host owns the pointer once it asks for it, and shift takes it back for selection.
What is missing is only the other half of that sentence. A press, a release, a move
while a button is down and a wheel notch each become the sequence `Emulator.Encode`
already produces, and go out through the same `TypeAsync` a keystroke does.

The wheel is the piece with a decision in it, and `Viewport.Wheel` has already made that
too: back through the history on the ordinary screen, to the program where it asked for
the mouse, and as arrow keys under a full-screen program that did not — which is what
makes a wheel work inside a pager that never heard of one.

Falsified when a program with mouse tracking on cannot tell a click from silence.

### §QS156 The boundary ICU is asked for and ASCII never needed

Measured on the replayed corpus, same machine, one build apart. Segmentation falls from
181 MB/s to 82 on `cat-log` and from 83 to 59 on `ls-color-r`; the whole emulate stage
falls about a sixth, 18 MB/s to 16. Nothing else moved: the escape scan, the parser and
the decoder are within noise of where they were.

The cause is one call. `GraphemeSegmenter` asks `StringInfo.GetNextTextElementLength`
for every cluster, and with ICU present that is a call across the interop boundary into
`icu.dll` rather than the runtime's own simplified breaking. QS154's trade was worth
taking — the client could not show a text box — but the rate is Block C's to defend and
this is where it went.

What it does not need is a different segmenter. A terminal's stream is overwhelmingly
characters that cannot begin a cluster at all: an ASCII letter followed by another ASCII
letter is one cell and needs nobody asked. The rule is cheap and exact, and it is the
same shape as the fast paths already in the parser.

What has to stay true is the answer. The segmenter's whole reason for using the
runtime's tables is that this project does not maintain its own, and a fast path that
guessed a boundary would be that by another name.

Falsified when the segmenter answers a boundary the runtime's own tables would not.

### §QS158 A scrollbar with nowhere to be drawn

`Viewport` already answers all three questions: how far back the view is, how much
history there is, and whether output arrived while somebody was reading.
`PaneAttachment.Where` returns them. Nothing shows them.

Where it goes is the whole difficulty and is why QS31 shipped without it. A WPF
scrollbar beside the pane is chrome, and `Chrome.Default` says a default installation
shows a title bar and a terminal — a claim QS46 shipped and `WindowTests` holds. A
scrollbar over the pane is not possible at all: the pane is a child HWND with a
swapchain presenting into it, so WPF cannot draw on top of it.

That leaves the answer every terminal reaches on its own, which is also the one this
client is best placed to take: draw it in the grid. A column of cells at the right edge,
in the session's own palette, sized by the same metrics as the text — no chrome, no
airspace, and it scales with the font because it is the font.

The unseen-output half is the part worth getting right. Somebody reading is not to be
interrupted, so it is a mark and never a jump: the whole point of the anchor is that new
output moves nothing.

Falsified when a reader scrolled into the history cannot tell that output arrived.

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
This line exists to hold the count and the rule until those are opened. QS60's browser
is the twelfth: its remote half lists over a session's file channel, and no tab holds an
SSH session yet.

Falsified when a component ships with no line naming what will reach it.

### §QS151 A signal nothing is sleeping on

`SessionPipeline` raises a `DamageSignal` when the parser has changed the model, and the
pane's render loop sleeps on one. QS116 found that those have to be the same object and
gave `Start` a parameter for it: `LocalSession` hands in the pane's, and
`LocalSessionTests` asserts the identity rather than the text, because a session parsing
correctly into the model while setting a signal of its own passes every assertion about
what the screen says and is still a window that draws one frame and stops.

`RemoteSession.LiveAsync` calls `SessionPipeline.Start(channel, _emulator)` with no
signal, once per connection, and exposes none. So the same fault is already written down
in the file QS126 will reach for, and it is worse there than it was here: a reconnect
replaces the pipeline, so even a client that found the first signal and slept on it
would be sleeping on a dead one from the second connection onwards. The reconnect is the
whole point of that class, the scrollback survives it, and a window that goes blank on
the first reconnect is the failure a user reports as losing their session.

What it needs is the shape `LocalSession` already has: the signal belongs to the pane,
is passed in once, and every pipeline the session opens over its life is given that one.
Which is also the smaller claim to test, because it is an identity and not a wait.

Falsified when a session survives a reconnect and the window still shows what the second
connection printed.

### §QS152 The session that ended and did not say so

`IPtyChannel.Closed` completes with a `PtyExit` carrying the code the program left with,
and `ConPtyChannel` fills it in. Nothing in the client waits on it. QS116 opened a shell
on the pane and joined every path that carries bytes; the one path that carries an
ending was not among them, because a session that has ended sends nothing and nothing is
what the pane keeps drawing.

So the client a user actually meets does this: they type `exit`, the shell goes, the
pipeline's loops finish, and the window sits on the last frame the shell printed, cursor
still blinking. Every keystroke after that is taken by `Typist` and dropped, which is
correct for a window with no session and indistinguishable from a window that has
stopped responding. The client is not hung and the user has no way to tell.

What it needs is small and it is a user-facing decision rather than a mechanism: the
window has to say the session ended and what it ended with, in the terminal itself,
where the person is already looking. `RemoteSession` already words this for the remote
case, and its sentence distinguishes a shell that exited from a link that dropped, which
is the distinction worth keeping here too.

Whether the window then offers a new session is the second half and belongs with tabs,
not with this line.

Falsified when a shell exits and the window is indistinguishable from one that has
stopped responding.

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

Falsified when detaching a tab reconnects its session.

### §QS163 The divider that is a gap between two child windows

`PaneLayout.Share` takes a proportion, clamps it so neither side can be dragged away,
and is called by nothing but the equalise chord. What is missing is the drag, and the
obstacle is the same one that runs through every pane in this client: they are child
windows.

A `GridSplitter` needs a WPF element between two WPF elements. Between two `HwndHost`
panes there is no such thing — the panes are positioned on a canvas by proportion, and
what lies between them is a few pixels of nothing that receives no WPF mouse input
because the child windows either side take it first. So the divider has to be either a
real element placed over the gap and given a cursor and a drag, or a pointer captured by
whichever pane the press landed near the edge of.

The second is tempting because the mouse plumbing already exists and would need no new
element, and it is wrong: a drag that starts inside a terminal is a selection, and
deciding between the two by how close to an edge the press was is a rule a user will
lose against.

Worth having beside it: a chord that nudges a divider, which needs none of this and is
what somebody without a mouse has.

Falsified when a pane can be resized only by closing it and splitting again.

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
`--broadcast`, `--import` and `--palette` today. Each is parsed where it is used in the
entry point, and each is explained by a comment beside that line, which is the one place
a user will never read.

KEYS.md solved the same problem for chords, and the shape carries over: the flags become
one list in code that the entry point reads rather than five separate lookups, and a
test holds a reference page to that list in both directions, so a flag nobody documented
fails the build and so does a flag the page describes that nothing parses.

Nothing about this is a help screen. The client is a windowed program with no console to
print one into, and a `--help` that opened a dialog would be the client choosing to put
a window in front of a script.

Falsified when the entry point acts on a flag the reference does not name.

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

### §QS135 The settings that are still only stored

QS74 built the file and its contract: a schema from the first release, forward-only
migration with a backup, unknown keys preserved, and portable mode. What it could wire
was the theme, because WPF's `ThemeMode` repaints a live window and so can be applied as
a correction after the first paint.

`fontFamily`, `fontSize` and `scrollback` are read, held and written back faithfully.
Nothing consumes them. The pane that would is not attached to a session, and inventing a
consumer to make the file look finished would have been the wrong order — a setting
wired to a stub is harder to remove than one that was never wired.

Storing them anyway was deliberate: a user editing a hand-editable file will set these
before anything reads them, and a build that dropped what it could not use would lose
the choice at the moment it was made. They are preserved by exactly the mechanism that
preserves a future build's keys.

What this owes: the typeface and size reaching the glyph rasteriser without a restart —
they are a live change, not a start-up one — and the scrollback depth reaching the
emulator's buffer, which has to decide what happens to lines already held when the depth
shrinks.

Falsified when a user changes the font in the settings file and the terminal does not
change.

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

### §QS133 A recording that stops before the disk does

QS71 worried about a trace left running overnight and gave the log a bounded total.
QS73's recording has none, and it is the greedier of the two: it writes every byte a
host sends. A `cat` of a large file is thirty megabytes in a few seconds, and the corpus
already holds one capture that was 33 MB raw.

Rotation is not the answer here, and that is the whole difficulty. A log is a sequence
of independent lines and dropping the oldest costs the oldest. A recording is one
continuous byte stream feeding a state machine — cut the front off and what is left
starts mid-escape-sequence, which is a file that no longer reproduces anything. So the
bound has to be a **stop**, not a roll.

Which makes it a question about what the user is told. A recording that quietly stopped
at a limit is worse than one that filled a disk, because the defect the user was trying
to capture happened after it stopped and nobody said so. So: a cap the user can see
before starting, the title's indication changing when it is reached, and the file itself
carrying a last line saying where it was cut.

Compressed size is the number to bound, since that is what reaches a disk and what the
user has to send.

Falsified when a session left recording overnight fills a disk, or stops without saying
that it did.

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
one that names the file it wrote so the user can find it. The title already changes; the
stop is what needs the sentence.

Keep the asymmetry. Starting is a decision made once, in the open; stopping is safe at
any time and can be a keystroke. A recording that can be started by a keystroke is one
somebody starts by accident.

Falsified when a user who has just hit a terminal defect has no way to record the
session that shows it.

### §QS150 The device that is now in the room

`DiagnosticBundle` answers the adapter question by opening a fresh `DxgiAdapterProbe`
and asking what the chain would choose. That was the only honest answer available when
it was written: nothing in the composing layer held a device, so the bundle described a
hypothetical one.

Since QS116 the client holds a real one, and it has been drawing. The difference matters
for the report this feature exists to produce. "The terminal is black" is answered by
frames drawn, presents that reached the glass, presents DXGI answered
`DXGI_STATUS_OCCLUDED` to, and how deep the present queue got — none of which a probe
can know, and all of which `PresentSurface` and `RedrawGate` already count. A probe can
also disagree with the running device outright: it chooses again, and a client that fell
back to WARP after a device was lost would be reported on the adapter it did not end up
using.

So the bundle should take the live view where there is one and keep the probe for where
there is not — a client that crashed before its pane was laid out, which is a state
worth naming rather than papering over.

Nothing here is new work in the render layer: the counters exist and are public.

Falsified by a bundle naming an adapter the client is not drawing on.

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

### §QS136 A green that says how much it covered

`run-tests.cmd` already refuses one way of shrinking silently: no test applications
found is not a pass. It does not refuse the other. The docker sshd fixture stops on its
own, and every test needing it calls `Assert.SkipUnless`. The run then reports
**Passed**, `All 5 test assemblies passed`, and exits 0, with 103 of 228 tests never
executed. The line a person reads is identical either way.

That is the shape of failure the script's own comment argues against: a command
reporting something the tree does not have teaches people to stop reading it. A false
green is worse than a false red, because nobody investigates it.

What to do, in order of worth. **Print the skip count in the summary**, always — one
number, and the difference becomes visible. **Fail on a skip budget**: a number per
assembly, checked in, so a new skip is a decision rather than weather. **Say why**: the
skip reasons are already one line each. CI needs this most, because nobody there watches
a terminal.

The fixture half is the **engine**, not the containers. They exit `(0)` because the
engine under them stops — this desk runs FreeWilly, which then answers "the FreeWilly
engine is not running". So `restart: unless-stopped` in `compose.yaml` is worth having
and is not enough: the suite has to notice. One session saw four green runs skipping 81,
103, 96 and 96 of 228 tests.

Falsified when a run that skipped the whole network suite exits 0 without saying
so.</body>

### §QS149 The other invisible byte

A source file was rewritten by a shell one-liner that read it as one encoding and wrote
it as another. Every em dash in it turned to mojibake, in prose that had been correct
for months and in a comment written minutes earlier. It compiled, the tests passed, and
nothing anywhere reported it: `git diff --stat` showing far more changed lines than the
edit accounted for is what caught it, and only because someone looked.

This is the same failure `SourceHygieneTests` already exists for, one codepage along. A
raw ESC byte is invisible in a diff; mojibake is visible but only to a reader already
looking at that line for another reason, and a review of a large diff is exactly where
nobody is. Both survive a compile, both survive a test run, and whether either happens
at all is a property of the tool that wrote the file rather than of the file.

So the same test should refuse it: the byte sequences a UTF-8 file acquires when it has
been decoded as Windows-1252 and re-encoded, none of which occurs in this repository's
real prose in any language it is written in.

Cheap, and it belongs beside the check it generalises rather than in a new file.

Falsified by a file that carries mojibake through a green suite.

### §QS159 Two instructions about evidence, and the wrong one is louder

`.claude/skills/roadmap-docs/SKILL.md` says a UI task is not done without the picture,
capture the window, and names the `/run` skill as the way to launch the client for it.
`agents.md` says the opposite and says why: for UI the evidence is the accessibility
tree, because a capture needs foreground granted and a desk somebody is at, and this one
refused foreground twenty-five times running.

The skill is what an agent reads when it is about to ship something. So a session
shipping QS30 and QS31 followed it: it granted foreground, sent synthetic keystrokes,
moved the operator's windows, clicked into their editor twice, lost keystrokes to focus
changes, and read screenshots of a desktop somebody was working at. It never opened
`cases/`, never ran `run-tests-vm.cmd`, and did not know `Quickshell.Cases` existed
until the operator said so.

Nothing here was missing. The engine is adopted, the cases directory exists,
`winwright.json` is written, and the guest is configured. What was missing is that the
document telling an agent when a task may ship is also the one telling it how to prove
the task works, and it says the wrong thing.

Falsified when a session reads the shipping discipline and still reaches for a
screenshot as evidence.

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

### §QS181 A dialog five seconds late

While QS53 was being shipped, one full run of `run-tests.cmd` failed a single case: the
import preview was not on the desk under its caption after sixty polls over five
seconds, and the step that refuses it then invoked the main window's Close button
instead, because that was the rightmost button left to find. `Quickshell.Cases` run on
its own straight afterwards passed all six cases, and so did the next full run. Nothing
in that commit touches the import path.

So this is the intermittent red Block K's criterion rules out, and the report kept by
QS175 is what named it. What it does not say is why. The dialog is asked for with
`BeginInvoke` before the dispatcher starts, and it shares the window's thread with the
first pane's layout, device and shell, all of which a desk still busy with the previous
assembly can slow. Five seconds is the engine's resolve timeout, not a figure anybody
measured for this dialog.

The first move is the measurement: how long the preview takes to appear from launch,
over enough runs on a loaded and an idle desk to say what the wait has to be. A longer
timeout chosen before that is a guess that happens to be generous.

The second hazard is worth closing whatever the first finds: a step that looks for a
dialog's button must not be able to land on the window behind it.

Falsified when the case fails on a tree nothing changed.

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
