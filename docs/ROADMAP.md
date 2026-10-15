# Roadmap (active backlog)

## Priority

## Block A — A session that stays up, or says why it did not

## Block B — Keys, agents, and the host you think you reached

- 📋 **QS344** (deps: —) **The Windows Hello prompt before a vault export has never been seen to run, since no test desk has Hello set up** — Every test answers the gate through MainWindow.Verifies, so the COM interop call to UserConsentVerifier is proven by no run. → §QS344
- 📋 **QS345** (deps: QS306 ✅) **An exported vault restores the passwords but not the key files sessions name, so a new machine still lacks the keys** — QS306's design allowed key files as attachments asked for separately, and the export shipped without them. → §QS345
- 📋 **QS346** (deps: —) **Reading a KDBX 4 vault encrypted with ChaCha20, or derived with AES-KDF, is proven by no vault** — keepassxc-cli makes only KDBX 3.1 and the client writes AES with Argon2id, so those two reader paths have never met a real file. → §QS346

## Block C — Emulation that does not lie about the remote

- 📋 **QS350** (deps: a desk with the Docker SSH fixture) **Whether a narrowed bash pane's extra prompt line on a split is bash's or this client's reflow was never traced** — No desk here has a bash over SSH, so QS316 fixed the client's half and left the narrowed pane's half unmeasured. → §QS350

## Block D — The tree a user organises work in

- 📋 **QS351** (deps: —) **Only the prod tag has a look on the strip, and no import brings tags in from MobaXterm or PuTTY** — QS331 gave prod its meaning and left a colour for other tags and tags from imported folder names to a later line. → §QS351

## Block E — SCP and SFTP as a thing a person operates

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

## Block H — The reason to leave the incumbent

- ⏳ **QS77** (deps: a code-signing certificate, an update signing key) **There is no way to install this client, so it can only be run from a build directory** — Signing with a real certificate and an update check verified against a pinned key are still owed, and the machine-wide install has not run elevated. → §QS77
- ⏳ **QS79** (deps: QS3 ✅, QS196 ✅, a CI runner that is always the same machine) **A change that costs performance is caught by whoever happens to notice it** — CI still has to run the gate on every commit on a runner that is the same machine each time, and frame cost has to join it once QS196 measures it. → §QS79

## Block I — An error a user can act on

## Block J — Leaving MobaXterm, proven by the switch

## Block K — The build and the harness — what a green run is evidence of

- 📋 **QS292** (deps: QS188 ✅, winwright WW535, winwright WW536) **The Explorer drop is proven by a test that drives the desk by hand instead of by a UI case** — winwright 1.1.0 drops onto Explorer (WW530), but a case still cannot open that folder or read the file it lands as. → §QS292
- 📋 **QS347** (deps: winwright WW538) **A middle click closing a tab is proven by routed events raised in a unit test, not by a UI case** — A case's click verb presses only the left button, so the gesture waits on winwright to name another. → §QS347
- 📋 **QS348** (deps: winwright WW539) **That a tab's close cross is drawn inside its tab is proven by an in-process render, not by a UI case** — A case cannot claim one element sits within another, so the geometry QS312 fixed waits on winwright to name it. → §QS348
- 📋 **QS349** (deps: winwright WW540) **Choosing several sidebar rows is proven by automation peers in a unit test, not by a UI case** — A case cannot add to a selection or Ctrl+click, so QS314's selection waits on winwright to name the act. → §QS349

## Block L — An agent at the controls, held to a policy it cannot edit

- 📋 **QS325** (deps: QS321 ⏸) **An agent reaches a saved session only through raw ssh, quoting through four shells and paying a handshake per command** — A headless quickshell --mcp exposes hosts_list and exec(host, argv) over the client's own kept-alive connection, with exit code and stderr apart. → §QS325
- 📋 **QS326** (deps: QS325) **An agent edits a remote file by heredoc or sed -i, blind, with no diff for the person and no copy to go back to** — read_file and edit_file over SFTP, an exact and unique replacement, a backup beside the file and the diff returned and logged. → §QS326
- 📋 **QS327** (deps: QS325) **A sudo password, a pager or an apt dialog hangs an agent's command until its timeout, with no word of what it waited for** — exec runs with no stdin and a dumb pager, and a prompt it detects ends the command with a named error the agent can act on. → §QS327
- 📋 **QS328** (deps: QS325) **A long remote command dies at the agent's timeout, or with its connection, and leaves no way to look back in on it** — job_start, job_status and job_output run it detached on the server and read its output by cursor, across a reconnect. → §QS328
- 📋 **QS329** (deps: QS325) **A large command output lands whole in the agent's context, so one journalctl costs the rest of the session's room** — exec answers within a byte budget, head and tail kept with what was cut counted, and the rest paged by cursor or searched on the server. → §QS329
- 📋 **QS330** (deps: QS325) **A secret printed by a remote command, an .env or a token, goes straight into the agent's transcript and its provider** — The client's log redactor runs over every answer the gateway returns, and each redaction is counted so the agent knows something was hidden. → §QS330
- 📋 **QS332** (deps: QS325) **With the gateway installed an agent can still run ssh, plink, scp or paramiko from its own shell and skip every rule** — A PreToolUse hook steers those to the gateway's tools, and the agent's sandbox denies port 22, so the gateway is the only way out. → §QS332
- 📋 **QS333** (deps: QS325) **Nothing records what an agent ran on which server, who approved it, or what came back, once the session scrolls away** — An append-only audit log per gateway session keeps each call, its verdict, its approver and an output hash, readable as a change summary. → §QS333
- 📋 **QS334** (deps: QS325) **A person who sees an agent going wrong on a server has no single act that stops it everywhere at once** — One keybinding and a tray command revoke every grant, cancel running jobs and close every gateway connection, and the agent is told why. → §QS334
- 📋 **QS335** (deps: QS325) **What an agent runs on a server is invisible to the person until it reports back, if it reports at all** — Each gateway connection appears as a quickshell tab badged as the agent's, showing every command and its output live as it runs. → §QS335
- 📋 **QS336** (deps: QS322 ✅, QS323 ✅, QS325, QS331 ✅) **Every call an agent makes through the gateway is allowed, whatever the host and whatever the command** — The gateway judges each call against the policy before it runs: allow, ask the person, or refuse with the rule named. → §QS336
- 📋 **QS337** (deps: QS324 ✅, QS336) **A call the policy says to ask about has nowhere to be asked, so the gateway can only allow or refuse it** — A quickshell dialog shows the plan, its host, tag and diff, approves the exact batch by hash, and can turn it into a timed grant. → §QS337
- 📋 **QS338** (deps: QS336) **Text an agent read from a server can steer its next command, and the policy treats that command as if nothing happened** — Reading untrusted content marks the gateway session, and from then on every mutation asks the person, even one a grant allowed. → §QS338
- 📋 **QS339** (deps: QS322 ✅, QS332) **An agent able to write files can rewrite the very policy that limits it, and grant itself whatever it wants** — The plugin's hook denies writes to the policy path, and quickshell re-asks the person whenever the file changes outside its own UI. → §QS339
- 📋 **QS340** (deps: QS320 ✅, QS321 ⏸, the Docker SSH fixture) **A client-side policy is advice the server never hears, and nothing helps set up an account that limits an agent there** — A guided setup writes an agent account, a sudoers allowlist and key restrictions, shown first. → §QS340
- 📋 **QS341** (deps: QS340) **An agent's key on a server is valid until someone removes it, so a key that leaks works for months** — quickshell acts as a small SSH CA and signs the agent a certificate for minutes, bound to its principal, source address and options. → §QS341
- 📋 **QS342** (deps: QS321 ⏸) **Asking a local Claude Code about what a pane shows means copying the scrollback by hand into another window** — A local pane runs claude with the gateway configured, and a palette command sends a pane's selection or recent scrollback to it. → §QS342
- 📋 **QS343** (deps: QS325, QS332) **Wiring the gateway into Claude Code means hand-editing MCP config, hooks and sandbox rules, so few will** — A Claude Code plugin installs the MCP server, the hooks and a skill in one step, and quickshell offers to install it when it finds claude. → §QS343

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

- **Every action the client can perform is reachable from the palette** Settled by
  generating the action list from the actions themselves and asserting the palette
  enumerates all of it, which fails as a test.
- **A screen reader reads output on screen and follows the cursor** Settled by a run
  with a screen reader against a live shell session, reading output back and following a
  prompt as text is typed into it.
- **Sixteen open panes share one glyph atlas** Settled by reading atlas memory in use
  with one pane and with sixteen at the same font, which must not differ by more than
  the instance buffers.
- **A default installation opens on Home, with no toolbar, menu or status bar** Restated
  by the owner on 2026-10-13 (QS272). Settled by a first run on a clean profile: title
  bar, tab strip, sessions sidebar and Home, nothing else, beside the incumbent's first
  run; a shell start shows a terminal in Home's place.

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

## Done when — QS43

- **A key on a token authenticates a session, whichever agent holds it** The named pipe
  half landed, which is the Windows OpenSSH agent and Pageant from 0.78 on. What is left
  is the shared-memory transport older Pageants speak, and a token-backed key is the
  case with no other route at all.

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

## Done when — QS215

- **A recorded blank names its cause** A guest contrast.txt carries a reference retry
  line saying whether the second read of the same target had ink.

## Done when — QS153

- **A phrase composed with a real input method shows no box of its own** Checked by
  typing Japanese through Microsoft IME into a running client: the phrase is underlined
  in the pane with no floating box, the candidate list sits beside it, and the committed
  text reaches the shell once.

## Done when — QS214

- **The guest suite runs ten times without this test counting a byte** Ten consecutive
  run-tests-vm passes with tmux-resize at zero, after the sequence the diagnosis names
  is fixed.

## Done when — QS253

- **An unchanged commit holds on a quiet desk within the 10 % bound** run-perf-gate.cmd
  at the baseline's own commit, on the reference machine with nothing else running,
  reads held on all three figures with every allowance under 10 %, and the row is in
  gate-xps.md.

## Done when — QS255

- **An idle pane with blink off spends no measurable core time** Quickshell.Idle
  --frames on one local pane with cursorBlink false, ten minutes on a quiet reference
  desk, reads zero frames and under 0.1 % of a core.

## Done when — QS288

- **The gate's start holds against 40fb448** Settled by run-perf-gate.cmd on the
  reference desk with nothing else running, start judged and held rather than not judged
  as too noisy.

## Done when — Block L

- **The design document describes what shipped, not what was planned**
  docs/design/agent-gateway/README.md is updated in the same commit as every Block L
  line that ships, and a read of it against the shipped tools, the policy format and the
  audit log finds no section describing behaviour the gateway does not have.
- **No command the classifier allows can change a server** The classifier corpus, real
  agent sessions plus the known bypass shapes (find -delete, interpreter one-liners,
  base64 into sh), scores zero false allows on every run, and the false-ask rate is
  reported beside it as the friction figure.
- **The gateway moves none of the window's performance figures** With the gateway
  installed and an agent working, run-perf-gate.cmd passes against the reference
  baseline with no Performance-Moved trailer, because the gateway is its own process and
  nothing on the keystroke-to-pixel path.
- **With the client-side policy off, the server still refuses what it should** Against
  the test sshd set up by the guided setup, a command outside the agent account's
  sudoers list fails with the gateway's policy disabled, which is the run that proves
  the boundary is not the client.

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
  monitor-covering window is the exception: QS225 timed it in independent flip, median
  24 ms against 33.
- **No pane resized because a host asks for 132 or 80 columns** A pane in a split tab
  has no single size to become, and xterm too ignores DECCOLM's width unless allowC132
  is set. QS208 keeps its clear, margin reset and home; vttest cuts 010, 012 and 030
  differ for this.
- **No X device-independent colour spellings: CIELab and its kind, TekHVC, rgbi** Xcms
  converts them through the X display's characterisation, so the answer is a property of
  a server this client does not have; rgb: and the # forms are what hosts send.
  esctest's 21 tests of them stay failed and named here.
- **No host control of the window through CSI t, and no title read back** A remote
  program moving or iconifying a local window reaches into chrome the user owns, and a
  title read back types host text at the shell (QS19). Reports describe the pane and
  titles come back empty, so 15 esctest cases (move, iconify, DECSLPP, title readback)
  fail by decision (QS236).
- **No claim to be xterm or a VT5xx in DA, DA2 or DECSCL** xterm's VT525 answer lists
  132 columns, a printer, national sets, a locator and more that this client refuses,
  and a program that believes it sends what cannot be honoured. It stays a VT220 with
  colour, so 4 DA and DA2 cases and DECRQSS of DECSCL fail by decision (QS243).
- **No features switched off by the DECSCL conformance level** This client claims VT220,
  so gating by level would take DECRQM and DECSLRM from every program by default, and
  raising the claim is the identity non-goal. DECSCL is not obeyed; esctest's level 2
  and 3 cases fail by decision, and level 5 by DECCOLM's (QS244).
- **No font size of a session's own** Every pane in a window draws from one font and one
  glyph atlas (QS135), so a per-session size would mean panes of different cell sizes in
  one window, two atlases and a divider that cannot line up. FontSize stays readable in
  the file and is offered nowhere (QS245).
- **No agent forwarding; a jump host reaches the third machine instead** SSH.NET has
  none, and writing it here is the protocol work a non-goal forbids; while open it lets
  root on the far host sign as the user. JumpHost already reaches a machine behind a
  bastion with no agent exposed (QS45, decided by the owner 2026-10-09).
