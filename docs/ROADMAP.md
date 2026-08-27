# Roadmap (active backlog)

## Priority

## Block A — A session that stays up, or says why it did not

- 📋 **QS36** (deps: QS5 ✅, QS25) **Protocol library types would reach the terminal and the UI, so replacing that library means rewriting the client** — The gap analysis may well end in a different library, and a seam decided afterwards is one negotiated against code that already exists. → §QS36
- 📋 **QS37** (deps: QS36, QS26) **No remote host's output has ever reached the emulator** — This is where the terminal stops being a local demonstration and becomes the product, and everything above it was already written to accept it. → §QS37
- 📋 **QS38** (deps: QS37) **A link that drops for ten seconds costs the whole session and its scrollback** — Mobile and VPN links drop routinely, so a client treating every drop as final makes the user pay for the network's ordinary behaviour. → §QS38
- 📋 **QS39** (deps: QS37) **A refused connection reports a library exception, so a user cannot tell a wrong port from a wrong key** — The error message is the documentation a user reads at the moment something fails, which is far more often than they read anything else. → §QS39
- 📋 **QS40** (deps: QS38, QS39) **This client has met one server, so an appliance that negotiates differently is an unknown** — Interoperability failures are found by connecting to unusual servers and by no other method, so the unusual servers are enumerated and connected to deliberately. → §QS40

## Block B — Keys, agents, and the host you think you reached

- 📋 **QS41** (deps: QS36) **Only one way in is proven, so a host needing a second factor or an ed25519 key is unreachable** — Authentication is where a client meets the widest variety of server policy, and each method it lacks is a whole population of hosts it cannot open. → §QS41
- 📋 **QS42** (deps: QS36) **Nothing checks the host key, so a machine in the middle is indistinguishable from the server** — This is the one check that makes an encrypted session mean anything, and a client defaulting to accept is a client whose encryption is decoration. → §QS42
- 📋 **QS43** (deps: QS41) **A key already unlocked in an agent must be typed again, and a hardware key cannot be used at all** — An agent is where a passphrase is entered once a day instead of once a connection, and it is the only route to a key the client may never hold. → §QS43
- 📋 **QS44** (deps: QS41) **A saved password would rest on disk where anything running as the user can read it** — A client that stores credentials badly is worse than one storing none, and the whole difference lies in choices made before the first password is saved. → §QS44
- 📋 **QS45** (deps: QS43) **Nothing forwards an agent, and nothing would stop a compromised host from using one if it did** — Forwarding hands a remote machine the ability to authenticate as the user everywhere, so it is decided per host rather than by a checkbox set once. → §QS45

## Block C — Emulation that does not lie about the remote

- 📋 **QS23** (deps: QS17 ✅) **Narrowing the window turns wrapped output into ragged fragments that never recover** — This is the single behaviour terminal emulators most reliably get wrong, so it is isolated behind a pure function that can be tested without a window. → §QS23
- 📋 **QS24** (deps: QS14 ✅) **A malformed escape sequence from a hostile host has never been shown not to crash or allocate** — The parser is the one component whose input is chosen entirely by a remote machine, so its failure modes belong to somebody else until they are tested. → §QS24
- 📋 **QS25** (deps: QS15 ✅) **The emulator has no real producer of bytes, so every test of it is a fixture its own author wrote** — A pseudo-console gives the terminal a genuine adversary locally, which tells a terminal defect apart from a network defect before the network exists. → §QS25
- 📋 **QS26** (deps: QS25, QS22 ✅, QS9 ✅) **Under heavy output the delay before a typed character appears grows without bound** — The parser and the renderer run at rates differing by orders of magnitude, and any design that couples them makes the faster one wait for the slower. → §QS26
- 📋 **QS27** (deps: QS26) **A keystroke queues behind a screenful of pending output before it is written to the host** — Output volume is the host's choice while echo latency is what the user feels and blames the client for, so the two paths must not share a queue. → §QS27
- 📋 **QS28** (deps: QS27) **Arrow keys, function keys and modified keys send nothing a remote program recognises** — What a key sends depends on modes the host has set, so this is a function of terminal state rather than a static lookup table. → §QS28
- 📋 **QS29** (deps: QS28) **Typing Japanese or Chinese shows no composition and commits nothing** — Composition is drawn by the input method into a window it expects the client to place, and a client that ignores it leaves the candidate list in the wrong corner. → §QS29
- 📋 **QS30** (deps: QS26, QS21 ✅) **Text on screen cannot be selected or copied, and a paste arrives as keystrokes the host may run** — Copying is the second most common thing a user does in a terminal, and pasting is where a terminal most easily runs something the user did not intend. → §QS30
- 📋 **QS31** (deps: QS15 ✅, QS26) **Output that scrolled past cannot be scrolled back to, and nothing in it can be found** — The ring already holds the history, so what is missing is a viewport over it and a search across it, and users reach for both within a minute. → §QS31
- 📋 **QS32** (deps: QS25, QS23) **Resizing the window leaves the remote program drawing to the geometry it had before** — Three parties hold a copy of the size and only the client knows it changed, so telling the other two is an obligation rather than a convenience. → §QS32
- 📋 **QS33** (deps: QS17 ✅, QS18 ✅, QS20 ✅, QS21 ✅, QS25) **No external suite has ever judged this emulator, so its fidelity is the author's own opinion** — A test written by whoever wrote the parser tests that person's understanding of the specification, which is the thing most likely to be wrong. → §QS33
- 📋 **QS34** (deps: QS10 ✅) **A programming font's ligatures do not form, so text set in it looks unlike the same text elsewhere** — Shaping a run across cell boundaries contradicts the grid the renderer is built on, so it is a deliberate later decision and not a font setting. → §QS34
- 📋 **QS35** (deps: QS8 ✅, QS9 ✅) **Text is rasterised in grayscale, so it looks thinner here than in every other Windows application** — Subpixel coverage is three channels of alpha where the blend and the atlas were both built assuming one. → §QS35
- 📋 **QS91** (deps: —) **A combining mark takes no cell and is then drawn nowhere, so an accent typed as two codepoints vanishes** — Text that arrives decomposed is ordinary on macOS and in Git output, and a client that silently drops the accent is one that shows the wrong filename. → §QS91
- 📋 **QS92** (deps: —) **The golden suite has run on two rasterisers and none of the three vendor drivers its matrix names** — A driver bug is by definition the thing the machine that wrote the code cannot see, so a suite that has only ever run here is one nobody has tested yet. → §QS92
- 📋 **QS93** (deps: QS25) **Every golden scene is text somebody typed into the test, so none of them is a screen a real program drew** — A scene an author invented exercises what that author thought of, which is never the combination that turns out to break on somebody's machine. → §QS93
- 📋 **QS94** (deps: QS13 ✅) **Segmenting a printed run allocates a string per character, which is the whole of the render arm's cost** — The parser was built to allocate nothing and the layer directly above it allocates more than the bytes it was given, so the zero it achieved buys nothing. → §QS94

## Block D — The tree a user organises work in

- 📋 **QS55** (deps: QS37) **Every connection is retyped, so a host used daily costs the same as one used once** — The session tree is the artefact a user builds over years and the one that makes leaving a client expensive, so its format is a decision and not a serialisation. → §QS55
- 📋 **QS56** (deps: QS55) **Hosts already defined for OpenSSH have to be defined a second time here** — A user with a working config has already made every one of these decisions, and asking them to make each of them again is the real cost of switching client. → §QS56
- 📋 **QS57** (deps: QS56, QS36) **A host reachable only through a bastion cannot be reached at all** — Nearly every production environment this client targets sits behind one, so a client with no jump path cannot open the hosts that actually matter. → §QS57
- 📋 **QS58** (deps: QS55) **A session cannot be created or edited, so the store can only be built by editing its file** — The dialog is where most users meet every decision this client has made about connecting, so what it asks for and what it assumes are both design. → §QS58

## Block E — SCP and SFTP as a thing a person operates

- 📋 **QS59** (deps: QS37) **Moving a file means a second authentication and a second connection to a host already open** — A file transfer is a channel on the session that already exists, and treating it as a new connection costs the user another second factor for no reason. → §QS59
- 📋 **QS60** (deps: QS59, QS46) **There is no way to see what is on the remote host without running a command** — Browsing is what turns a transfer tool into something a person operates, and it is where the incumbent's users spend a large share of their time. → §QS60
- 📋 **QS61** (deps: QS59) **A transfer has no progress, cannot be cancelled, and starts again from zero if it fails** — Transfers are long and links are unreliable, so a queue that cannot resume turns one dropped connection into an hour of repeated work. → §QS61
- 📋 **QS62** (deps: QS61) **A folder cannot be copied, and a name that already exists on the far side is resolved by guessing** — Recursion and collision handling are where a transfer tool quietly destroys data, and both are policy decisions rather than implementation details. → §QS62
- 📋 **QS63** (deps: QS59) **A host too old to offer an SFTP subsystem cannot receive a file at all** — SCP is refused as the primary path in the non-goals, and the only reason to keep it is the appliance that offers nothing else. → §QS63
- 📋 **QS64** (deps: QS60) **A file can only be transferred through the browser, so dragging one from Explorer does nothing** — Dragging a file onto a session is the shortest path a user has to moving it, and it is the interaction they try first without being told it exists. → §QS64
- 📋 **QS65** (deps: QS62) **Keeping a local and a remote directory alike means comparing them by eye** — The deploy-and-check loop is a large part of what this audience does all day, and doing it by hand is where files quietly get missed. → §QS65

## Block F — A forward is a lifecycle, not a checkbox

- 📋 **QS66** (deps: QS37) **A port on the remote network cannot be reached from a local tool** — A forward is what lets a database client or a debugger reach a machine the user has no route to, and it is the second reason this audience opens an SSH client. → §QS66
- 📋 **QS67** (deps: QS66) **A service running locally cannot be reached from the remote host** — The direction is reversed, the server does the listening, and the server's own configuration decides whether the request succeeds at all. → §QS67
- 📋 **QS68** (deps: QS66) **Reaching many hosts on the remote network needs one forward configured per host** — A SOCKS proxy is a single forward covering a whole network, which is what a browser or a cloud tool needs and what per-host forwards cannot provide. → §QS68
- 📋 **QS69** (deps: QS66, QS67, QS68, QS38) **A forward is set up by hand each time and dies silently when its session drops** — A forward is something a user relies on for hours without looking at it, so its failure has to be visible and its restart has to be automatic. → §QS69
- 📋 **QS70** (deps: QS69) **Nothing says which forwards are running, so a stale one is discovered through a port conflict** — A forward is invisible by nature, and a client that will not show its own listeners makes the user consult netstat to understand the client. → §QS70

## Block G — The clean interface, defended

- 📋 **QS46** (deps: QS4 ✅, QS9 ✅) **There is no window, only a render surface, so nothing can be opened, themed or closed** — The shell is where the clean interface is either defended or lost, and its defaults decide what a user sees before they have configured anything. → §QS46
- 📋 **QS47** (deps: QS46, QS26) **Every session needs its own window, so working across four hosts means four windows to arrange** — A tab is what makes several sessions one workspace, and it is the surface the window title, the session tree and the layout all attach to. → §QS47
- 📋 **QS48** (deps: QS47) **Two sessions cannot be seen at once, so comparing output means alternating between tabs** — Watching a log while typing on another host is the ordinary case for this audience, and a tab-only client makes it impossible rather than merely awkward. → §QS48
- 📋 **QS49** (deps: QS48, QS6 ✅) **Each pane would open its own graphics device, so four panes cost four times the driver's attention** — The device, the atlas and the shaders are process-wide resources panes should share, and sharing them is a decision about ownership rather than an optimisation. → §QS49
- 📋 **QS50** (deps: QS46) **Nothing can be configured, so the font, the colours and the keybindings are whatever the code says** — A settings surface is where a lean client most easily becomes a bloated one, so what is offered and what is simply decided are both chosen here. → §QS50
- 📋 **QS51** (deps: QS50) **A user's chosen colour scheme has to be entered as twenty colours by hand** — Schemes already circulate in two formats everybody shares, so reading them is a small piece of work standing between a user and a familiar terminal. → §QS51
- 📋 **QS52** (deps: QS47) **Every action needs a chord the user memorised or a menu they have to go looking for** — A palette is how a lean interface keeps its actions reachable without growing toolbars, which is the same trade this project makes everywhere else. → §QS52
- 📋 **QS53** (deps: QS48) **The same command on eight hosts has to be typed eight times** — Fleet work is a large part of why this audience uses a tabbed client at all, and mistyping the eighth is how that pattern fails today. → §QS53
- 📋 **QS54** (deps: QS9 ✅, QS15 ✅) **A screen reader finds nothing on the terminal surface, so the client cannot be used without sight** — A GPU surface is opaque to assistive technology by construction, so the text is published deliberately or it does not exist to anything but a camera. → §QS54
- 📋 **QS83** (deps: QS82 ✅, QS46) **Every window would invent its own colours and row shapes, so the chrome drifts from the two clients it should match** — The design system already exists in two shipped clients, so what is decided here is whether it is borrowed whole or rediscovered a window at a time. → §QS83

## Block H — The reason to leave the incumbent

- 📋 **QS74** (deps: QS1 ✅) **Settings have no file, so nothing survives a restart and nothing can be moved to another machine** — The config format is a compatibility contract from the first release, and a schema with no version is one that cannot change without breaking somebody. → §QS74
- 📋 **QS75** (deps: QS2 ✅, QS46) **Nothing has been measured starting, so the cold start figure is an aspiration** — Start-up is the first thing a user compares against the incumbent, and it is decided by publishing choices far more than by application code. → §QS75
- 📋 **QS76** (deps: QS26, QS2 ✅) **A window nobody is typing into has never been measured, so the low-idle claim is untested** — Idle cost is what a laptop user experiences as battery life, and it is the figure the incumbent loses on most clearly. → §QS76
- 📋 **QS77** (deps: QS75) **There is no way to install this client, so it can only be run from a build directory** — Installation and update are where a lean client is judged before it is started, and an unsigned binary is one a corporate machine simply refuses. → §QS77
- 📋 **QS78** (deps: QS37, QS66) **Nothing has run for longer than a working session, so a slow leak would reach users first** — Leaks in a long-lived client appear after days rather than minutes, which is exactly the interval no test covers and every user reaches. → §QS78
- 📋 **QS79** (deps: QS3 ✅, QS75) **A change that costs performance is caught by whoever happens to notice it** — The measurements exist and are trusted by now, so all that is left is making a regression fail a build instead of reaching a release. → §QS79
- 📋 **QS86** (deps: QS7 ✅, QS9 ✅) **Input to photon is the first figure in the budget and the only one nothing has ever measured** — The present path was built to bound it and the one workload that exists cannot run ahead of the display, so the flags remain an argument rather than a number. → §QS86

## Block I — An error a user can act on

- 📋 **QS71** (deps: QS39) **When a connection fails there is nothing to read afterwards but the dialog that has gone** — A failure the user cannot reproduce is diagnosed from a log or not at all, and a log that holds secrets is one they are unable to send. → §QS71
- 📋 **QS72** (deps: QS1 ✅) **A crash takes the session, the scrollback and any explanation of what happened with it** — A client holding four connections that simply vanishes is worse than one that fails visibly, and the difference is entirely what it does in its last second. → §QS72
- 📋 **QS73** (deps: QS71, QS72) **A user reporting a defect has no way to say what their client was doing when it happened** — Every report arrives without the version, the server, the settings or the environment, and gathering those by correspondence costs days per defect. → §QS73

## Block J — Leaving MobaXterm, proven by the switch

- 📋 **QS80** (deps: QS55) **A MobaXterm or PuTTY user has to recreate every session by hand before they can start** — The session tree is the whole cost of switching, so a client that cannot read the incumbent's is one most users never get past the first evening with. → §QS80
- 📋 **QS81** (deps: QS80) **A user weighing the switch has nothing that says what they will and will not get** — The non-goals list is already written and honest, and a user deciding whether to move their fleet is exactly who needs to read it beforehand. → §QS81

## Block K — The build and the harness — what a green run is evidence of

- 📋 **QS89** (deps: —) **dotnet test reports zero tests and exits 5 while run-tests.cmd runs the same assemblies and passes** — Every .NET tool and CI template reaches for that command first, so a repository where it lies looks broken to everyone who has not read the script. → §QS89
- 📋 **QS90** (deps: —) **Nobody has built this repository from a clean clone, so the steps it needs beyond the SDK are unknown** — Every build so far has run on the machine that wrote the code, which is the one machine whose caches and installed components prove nothing about the next. → §QS90
- 📋 **QS99** (deps: —) **A failed build leaves the old test binary in place and running it reports a green suite that proves nothing** — Twice this session a compile error was swallowed and the previous assembly ran, reporting the old pass count as if it were the new one. → §QS99

## Done when — Block A

- **A session survives a sixty-second link outage** Settled by the reconnect soak: a
  session dropped and restored on a timer for seventy-two hours keeps its scrollback and
  its tab every time, and states which attempt restored it.
- **Every server in the compatibility matrix connects and runs a full-screen program**
  Settled by reading the matrix: each row names what was connected to and which
  algorithms were negotiated, and a row carrying no algorithm list has not been tested.
- **No connection failure reaches a user as a library exception** Settled by walking the
  enumerated failure list against a live server, forcing each one in turn, and reading
  every message the way a user would read it.

## Done when — Block B

- **No code path connects to a host whose key was not checked against the store**
  Settled by a run against a server whose key was deliberately changed, which must
  refuse, and whose dialog must offer no default accept button.
- **A key held only in an agent or on a hardware token authenticates a session** Settled
  by a run with no key file present and the agent holding the only identity, against a
  live server, for both the Windows agent and Pageant.
- **No stored secret can be read from disk on another machine** Settled by copying the
  store to a second machine and confirming it does not decrypt, run both with and
  without a master password configured.

## Done when — Block C

- **esctest passes above ninety per cent with every failure named individually** Settled
  by the recorded pass rate per section, dated, with the known-failure list accounting
  for every remaining failure by reason or by task id.
- **Input to photon stays within one refresh interval while a large file is printing**
  Settled by a high-speed capture on the reference machine, taken at rest and under a
  hundred-megabyte cat, with both figures reported side by side.
- **The golden-image suite passes on all five environments in the GPU matrix** Settled
  by running the suite on NVIDIA, AMD, Intel integrated, WARP and inside an RDP session,
  with any difference image attached to the result.
- **An idle window issues no draw calls** Settled by the ten-minute idle measurement:
  zero draw calls, no measurable core occupancy, and no raised system timer resolution
  held by the process.
- **The parse path allocates zero bytes in steady state** Settled by the allocation
  assertion over a full corpus replay, which fails the build rather than reporting a
  number somebody reads later.
- **Mixed Latin, CJK and emoji agree with the host about the cursor column** Settled by
  printing such a line and comparing the cursor position report against what the remote
  shell believes, at several terminal widths.

## Done when — Block D

- **An existing ssh_config opens its hosts without anything being retyped** Settled by
  pointing the client at a config carrying patterns, includes and a ProxyJump chain,
  then connecting to every host it defines.
- **A two-hop bastion chain connects, and names the failing hop when it does not**
  Settled by connecting through two bastions, then breaking each hop in turn and reading
  the message each failure produces.
- **The session store round-trips through a hand edit** Settled by editing the file in a
  text editor, reloading the client, and confirming nothing was reformatted, reordered
  or lost.

## Done when — Block E

- **An interrupted transfer resumes without producing a file unlike the source** Settled
  by interrupting a large transfer at several offsets, resuming each time, and comparing
  checksums of source and destination.
- **The browser lists fifty thousand entries without blocking** Settled by a run against
  such a directory, timing the first visible screen rather than the moment the complete
  listing arrives.
- **No transfer destroys a destination file it did not finish writing** Settled by
  interrupting an overwrite of an existing file at several offsets and confirming the
  original survives each time intact.

## Done when — Block F

- **Every forward returns after a reconnect, or the client says it did not** Settled by
  dropping a session carrying all three forward kinds and reading what the client
  reports about each one on restore.
- **No listener outlives the session that created it** Settled by closing sessions under
  load and confirming the process holds no listening socket afterwards, checked from
  outside the client.
- **No forward binds beyond loopback without an explicit choice** Settled by inspecting
  the bound addresses of every forward kind created with default settings, on a machine
  with several interfaces.

## Done when — Block G

- **A default installation shows no chrome beyond a title bar and a terminal** Settled
  by a screenshot of a first run on a clean profile, placed beside the same screenshot
  of the incumbent for comparison.
- **Every action the client can perform is reachable from the palette** Settled by
  generating the action list from the actions themselves and asserting the palette
  enumerates all of it, which fails as a test.
- **A screen reader reads output on screen and follows the cursor** Settled by a run
  with a screen reader against a live shell session, reading output back and following a
  prompt as text is typed into it.
- **Sixteen open panes share one glyph atlas** Settled by reading atlas memory in use
  with one pane and with sixteen at the same font, which must not differ by more than
  the instance buffers.

## Done when — Block H

- **Cold start reaches an interactive local shell under four hundred milliseconds**
  Settled on the named reference machine, reported for cold and warm file cache, beside
  the same measurement taken of the incumbent.
- **Resident memory stays flat across a seventy-two-hour soak** Settled by the soak run:
  memory, handles, threads and sockets all flat after warm-up, with the raw series kept
  rather than summarised.
- **A performance regression fails a build rather than reaching a release** Settled by
  deliberately regressing each gated figure past its threshold and confirming CI refuses
  the build in every case.
- **The shipped binary is signed and installs without an administrator prompt** Settled
  by installing the release artefact on a clean managed machine as a standard user, with
  SmartScreen enabled.

## Done when — Block I

- **No secret appears in any log at any level** Settled by running a full authentication
  and a transfer with the transport trace enabled, then searching the log for every
  credential used.
- **A crash leaves a report and tells the user where it is** Settled by forcing a crash
  on each thread that can take one, then reading what the user is shown and what the
  report contains.

## Done when — Block J

- **A MobaXterm session file imports with every unmapped setting named** Settled by
  importing a real file carrying X11 and macro settings and reading the per-session
  report of what was skipped.
- **Every figure in the comparison document reproduces from a documented run** Settled
  by re-running each measurement it cites, from its own stated method, on its own stated
  machine.

## Done when — QS23

- **Narrow then widen restores the original rows exactly** Settled by property-based
  tests over random buffers and width sequences, never by a manual drag of the window.
- **The character under the cursor is invariant across any resize sequence** Settled by
  the same property tests, asserting the cursor's character rather than its coordinates.
- **If reflow is not shipped, that omission is a filed non-goal** Settled by reading the
  non-goal list, since shipping without reflow silently is what turns a decision into a
  stream of defect reports.

## Done when — QS26

- **Echo latency under a hundred-megabyte cat matches echo latency at rest** Settled by
  a high-speed capture taken in both conditions on the reference machine, reported as
  two numbers rather than one verdict.
- **No byte of host output is ever dropped, only frames** Settled by replaying a corpus
  stream through the whole pipeline and comparing the final buffer against the headless
  result byte for byte.
- **The parser drains its queue fully before signalling damage** Settled by
  instrumenting queue depth at the moment of signalling, under load, which must be zero
  every time.

## Done when — QS42

- **A changed host key cannot be accepted by clicking a default button** Settled by
  triggering that case and confirming the dialog carries no default action and requires
  the old entry to be removed deliberately.
- **known_hosts written by this client is read by OpenSSH unchanged** Settled by
  connecting with this client and then with ssh to the same host, confirming neither
  rewrites or invalidates the other's entry.

## Done when — QS44

- **A password is never present in a managed string** Settled by inspecting the managed
  heap after an authentication and searching it for the credential that was used.
- **The settings surface states what DPAPI alone does not protect against** Settled by
  reading the wording, which must name an attacker already running as the same user
  rather than implying more than DPAPI gives.

## Done when — QS57

- **The target's own host key is verified, not the bastion's** Settled by connecting
  through a bastion to a host whose key is unknown, which must raise trust-on-first-use
  for the target itself.
- **A three-hop chain uses the same code path as a one-hop chain** Settled by a run
  through three bastions, since a special case appearing at depth two is where chain
  support usually turns out to stop.

## Done when — QS61

- **Resume is refused when the partial cannot be shown to be a prefix** Settled by
  resuming against a source modified since the interruption, which must restart from
  zero and say why rather than continue.

## Done when — Block K

- **One command builds and runs every test, and its exit code is the verdict** Run it on
  a clean tree and again on a tree with one deliberately broken test: the first exits
  zero and names the count, the second exits non-zero and names the test. A command that
  reports zero tests and exits five satisfies neither half.
- **Each configuration has exactly one output tree** Build the solution, edit one source
  file, build a single project, and look for an assembly older than that edit anywhere a
  runner might load one. Two trees under `bin` is how a stale binary gets run and
  believed, which has already cost a debugging cycle here.
- **A clean clone builds and passes with nothing taken from memory** Clone into an empty
  directory on a machine carrying only the .NET SDK, run the one command, read the
  count. Any step supplied from someone's head — a package source, an SDK component, a
  path — is a step the next machine will not take and the CI runner never had.
- **No test fails intermittently on an unchanged tree** Twenty runs over one unchanged
  checkout report the same result twenty times, and a failure that is not reproducible
  that way is a defect filed against the test rather than a re-run. A flake teaches
  everyone to re-run, and the first real regression is then re-run away as the flake.

## Non-goals

- **No X11 server or X11 forwarding** The bundled X server is the largest single piece
  of what this client was started to leave behind, a user who needs remote GUI already
  has one installed, and carrying it would spend the whole footprint budget Block H is
  judged on.
- **No embedded Cygwin, BusyBox or POSIX userland** Shipping a Unix environment in the
  box turns a client into a distribution, with its own update path and its own CVEs; a
  user who wants one installs WSL, and the box stays small enough for the cold-start
  number to be defensible.
- **No RDP, VNC, Telnet, serial or plain FTP** Each is a second protocol stack with its
  own failure modes, its own UI and its own security surface, while the three this
  client does carry all share one connection; breadth here is precisely what made the
  incumbent slow.
- **No web runtime: Electron, WebView2 or a JavaScript terminal** A browser engine
  sitting between a keystroke and a pixel is the latency this project exists to remove,
  and it also costs the memory and cold-start figures the client will be measured on.
- **No CPU-rasterised terminal grid: GDI, GDI+, WPF or WinForms text** Rasterising the
  grid on the CPU caps frame rate and CPU cost at roughly what the incumbent already
  achieves, which forfeits the single differentiator this client is being built around.
- **No second graphics backend beside D3D11** A cell grid never approaches the draw-call
  ceiling D3D12 and Vulkan exist to raise, so a second backend buys nothing measurable
  while doubling the surface every driver bug must be reproduced against.
- **No macro recorder, scripting engine or plugin host in the MVP** Each is a permanent
  compatibility contract and a security boundary, and none of them is why a user leaves
  the incumbent; the decision is reopened only against a measured request, never against
  a feature list.
- **No sixel or kitty inline graphics in the MVP** An inline image protocol changes the
  cell model the entire renderer is built on, so it is a design decision to take
  deliberately and later, never a feature to bolt onto a shipped grid.
- **No Linux or macOS build** Every window, pseudo-console and credential-store decision
  here is a Windows one, and a portable client is a different project; only the render
  backend keeps a seam, so that answer can change without the rest pretending it might.
- **No telemetry on by default** This client holds credentials, hostnames and command
  output, so anything that leaves the machine leaves on an explicit act by the user; a
  build that reports home by default is a build this project would not ship.
- **No SSH protocol implemented in this repository** A crypto transport is a decade of
  other people's review and audit, so the protocol stays a library behind a seam and
  this repository's own work is everything above it.
- **No feature carried over because MobaXterm has it** The incumbent's surface is the
  premise this project was started to argue with, so every feature is argued from a
  user's task instead; parity is not a reason, and this list is where each refusal is
  recorded once.
- **No SCP as the primary transfer path** The protocol has no directory listing, no
  resume and a history of filename-handling flaws, and OpenSSH 9 moved its own scp onto
  SFTP; it stays only as a fallback for hosts that offer nothing better.
