# Roadmap (active backlog)

## Priority

## Block A — A session that stays up, or says why it did not

- 📋 **QS142** (deps: QS139 ⏸) **The library will not bound a channel and will not resize one, and only it can do both** — Choosing between a resizable terminal and memory a fast host cannot exhaust is a choice this client should not have to make, and no local change removes it. → §QS142

## Block B — Keys, agents, and the host you think you reached

## Block C — Emulation that does not lie about the remote

- ⏳ **QS141** (deps: QS139 ⏸) **Feed runs near 4 MB/s where the budget asks for 400, and the budget measures a different arm** — Clustering costs nine times what reaches it and cell writes five times again, and what is left is a budget figure for the whole path that somebody has to argue for. → §QS141
- 📋 **QS233** (deps: —) **No left or right margin can be set, so everything esctest checks inside a column region fails** — DECLRMM (mode 69) and DECSLRM are absent, and they are what 64 failing tests across 25 classes have in common. → §QS233
- 📋 **QS234** (deps: —) **Asking for a colour with OSC 4, 5 or 10 to 19 gets no answer, and their resets are ignored** — The emulator sets these colours but answers no query and handles no reset, which is 47 esctest failures, each a timeout. → §QS234
- 📋 **QS235** (deps: —) **Character protection is not modelled, so selective erase and protected fields erase everything** — DECSCA, SPA and EPA are absent and DECSED, DECSEL and DECSERA erase like their plain forms, which is 36 failures. → §QS235
- 📋 **QS236** (deps: —) **Window operations through CSI t neither report the window nor say they are refused** — XtermWinopsTests fails 28 of its tests, 26 by timing out, because no CSI t report is answered and no resize is acted on. → §QS236
- 📋 **QS237** (deps: —) **DECRQM answers not recognised for modes xterm reports as permanently reset or as set** — Twenty-six DECRQM tests and one DECSCL test fail because the answer is 0 where xterm answers 4, 1 or 2. → §QS237
- 📋 **QS238** (deps: —) **The rectangular area operations DECCRA, DECFRA and DECERA do nothing** — Copy, fill and erase of a rectangle are absent, and so is DECSACE that shapes them, which is 19 esctest failures. → §QS238
- 📋 **QS239** (deps: —) **The saved cursor is one for both screens, and XTSAVE, XTRESTORE and DECSTR leave saved state behind** — Sixteen failures across SCORC, DECRC, the tite-inhibit tests, XtermSave and DECSTR come from how saved state is kept. → §QS239
- 📋 **QS240** (deps: —) **Most DSR variants and the checksum report get no answer** — Ten of eleven DECDSR tests time out, because only the cursor position report is answered. → §QS240
- 📋 **QS241** (deps: —) **HPR and VPR move the cursor nowhere** — CSI a and CSI e are not handled, so all eight HPR and VPR tests fail, the default parameter included. → §QS241
- 📋 **QS242** (deps: —) **Reverse wraparound does not follow a backspace back across a wrapped line as xterm does** — BS and CUB fail seven tests and DECSET two on reverse wraparound, the mode 45 cases and xterm 1045. → §QS242
- 📋 **QS243** (deps: —) **DECRQSS answers invalid for settings the client holds, and DA, DA2 and DECID answer another identity** — Six DECRQSS tests and five identity tests fail on replies that are well formed but not the ones xterm gives. → §QS243
- 📋 **QS244** (deps: —) **Seventeen esctest failures in eleven classes have no family of their own** — Each is one or two tests and one cause, and together they are the rest of what Block C needs named. → §QS244

## Block D — The tree a user organises work in

- 📋 **QS245** (deps: —) **A session's scheme, font size, terminal type, scrollback and credential are stored and inherited but never applied** — The dialog shows each with the folder it came from, so a user believes it configured something the client then ignores. → §QS245

## Block E — SCP and SFTP as a thing a person operates

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

## Block H — The reason to leave the incumbent

- ⏳ **QS75** (deps: QS2 ✅, QS46 ✅, the incumbent closed on the reference desk) **Nothing has been measured starting, so the cold start figure is an aspiration** — The incumbent has not been started beside it on the same machine, so the comparison the figure exists for is not made. → §QS75
- ⏳ **QS77** (deps: a code-signing certificate, an update signing key) **There is no way to install this client, so it can only be run from a build directory** — Signing with a real certificate and an update check verified against a pinned key are still owed, and the machine-wide install has not run elevated. → §QS77
- ⏳ **QS78** (deps: QS139 ⏸) **Nothing has run for longer than a working session, so a slow leak would reach users first** — The seventy-two-hour run itself is still owed, with atlas and GPU memory watched, which needs a pane attached to a session. → §QS78
- ⏳ **QS79** (deps: QS3 ✅, QS196 ✅, a CI runner that is always the same machine) **A change that costs performance is caught by whoever happens to notice it** — CI still has to run the gate on every commit on a runner that is the same machine each time, and frame cost has to join it once QS196 measures it. → §QS79

## Block I — An error a user can act on

## Block J — Leaving MobaXterm, proven by the switch

## Block K — The build and the harness — what a green run is evidence of

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

## Done when — QS78

- **A seventy-two-hour run has actually finished** Checked by a report in
  benchmarks/results whose span is at least 72 h, whose verdict column says flat rather
  than "too short to judge", and whose session table shows every role connected with the
  failures it swallowed named.
- **Atlas and GPU memory are among the watched counters** The design names atlas memory
  specifically, being the one cache with an eviction policy and therefore the one where
  a policy defect is indistinguishable from a leak. Checked by both appearing as rows in
  the report, which needs a graphics device the harness does not yet hold.

## Done when — QS141

- **Where the hundredfold goes is named, per stage** `parse` reports 1,200 MB/s on
  cat-log and `emulate` 10, and a ratio is not a diagnosis. Checked by a measurement
  attributing the difference to named work — cell writes, scrolling, grapheme
  segmentation, decoding — rather than to the emulator as a whole.
- **The whole path has a budget figure somebody argued for** PERFORMANCE.md now says
  there is none rather than inventing one. Checked by figure 2 or a figure beside it
  stating a number for the `emulate` arm, with the reasoning for that number and not
  merely the measurement it was taken from.

## Done when — QS43

- **A key on a token authenticates a session, whichever agent holds it** The named pipe
  half landed, which is the Windows OpenSSH agent and Pageant from 0.78 on. What is left
  is the shared-memory transport older Pageants speak, and a token-backed key is the
  case with no other route at all.

## Done when — QS75

- **The incumbent's start is timed beside this client's on the reference machine**
  Settled by MobaXterm's process start to its first window, and to a local shell where
  it can be read, timed on the same desk the same day as tools/Quickshell.Startup's run
  and recorded in startup-h.md.

## Done when — QS77

- **The release archive is signed with a certificate a clean Windows trusts** Settled by
  release.cmd -Certificate producing an archive whose quickshell.exe and Quickshell
  assemblies verify Valid under Get-AuthenticodeSignature on the guest, timestamped,
  with no -unsigned in its name.
- **An update is offered, declinable, and verified against a pinned key** Settled on the
  guest against a local static file: an older copy finds a newer release on its schedule
  and never at start-up, a decline leaves it alone, nothing installs while a session is
  open, and a payload with a bad signature is refused.
- **The machine-wide install and uninstall have run elevated** Settled by
  run-install-vm's checks repeated with --all-users from an elevated prompt: Program
  Files, every user's Start menu and the HKLM entry written, the installed copy started,
  then all three removed.

## Done when — QS79

- **The gate runs on every commit, on a runner that is the same machine each time**
  Settled by a CI job on that runner that fails a pushed commit carrying an injected
  emulate slowdown, passes the commit that removes it, and leaves one row per commit in
  its gate history.
- **Frame cost is one of the gated figures** Settled when the gate's figures include the
  arm QS196 adds, a baseline carries its threshold, and a check prints it beside parse,
  emulate and start.

## Done when — QS40

- **Every server class the design names has a row** Windows OpenSSH, a network appliance
  and a commercial server each have a row in docs/measurements/compatibility.md with
  their negotiated algorithms.

## Done when — QS114

- **A real Pageant older than 0.78 signs through the window** A session opens with a key
  loaded only in an installed Pageant 0.77 or older, reached through
  SshAgent.PageantWindow.

## Done when — QS215

- **A recorded blank names its cause** A guest contrast.txt carries a reference retry
  line saying whether the second read of the same target had ink.

## Done when — QS153

- **A phrase composed with a real input method shows no box of its own** Checked by
  typing Japanese through Microsoft IME into a running client: the phrase is underlined
  in the pane with no floating box, the candidate list sits beside it, and the committed
  text reaches the shell once.

## Done when — QS160

- **A tab detached into its own window keeps its connection** Checked by detaching a tab
  with a live SSH session into a new window and asserting the transport is the same
  object, no reconnect was logged, and the shell answers a keystroke in the new window.

## Done when — QS188

- **A file dragged from the host's pane lands in Explorer byte for byte** Once QS219
  gives an SSH tab its remote pane: run-app-vm opens the browser in the guest, a
  winwright case drags a file from the host's pane onto an Explorer folder, and the file
  there is compared with the server's.

## Done when — QS197

- **The gate's baseline derives a parse threshold under ten per cent** run-perf-gate.cmd
  takes a new baseline on the attended reference desk with the guest suspended, and
  benchmarks/results/gate-xps.md shows the parse threshold it derived below 10%.

## Done when — QS214

- **The guest suite runs ten times without this test counting a byte** Ten consecutive
  run-tests-vm passes with tmux-resize at zero, after the sequence the diagnosis names
  is fixed.

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
- **No echo under two refresh intervals for a windowed client** QS201 measured every
  windowed chain composed on the reference desk, made for the window or for composition,
  in a popup or the client's WPF host: the second interval is the compositor's. A
  monitor-covering window is the open exception.
- **No pane resized because a host asks for 132 or 80 columns** A pane in a split tab
  has no single size to become, and xterm too ignores DECCOLM's width unless allowC132
  is set. QS208 keeps its clear, margin reset and home; vttest cuts 010, 012 and 030
  differ for this.
