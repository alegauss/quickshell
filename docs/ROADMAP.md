# Roadmap (active backlog)

## Priority

## Block A — A session that stays up, or says why it did not

- 📋 **QS142** (deps: QS139 ⏸) **The library will not bound a channel and will not resize one, and only it can do both** — Choosing between a resizable terminal and memory a fast host cannot exhaust is a choice this client should not have to make, and no local change removes it. → §QS142
- 📋 **QS220** (deps: QS151 ✅) **An SSH tab whose link drops stays ended, though the session that reconnects was built for it** — RemoteShell connects once and RemoteSession, which reconnects and keeps the scrollback, is reached by nothing. → §QS220

## Block B — Keys, agents, and the host you think you reached

- 📋 **QS218** (deps: QS126 ✅) **A saved session to a host that takes a password or a one-time code cannot be connected from the client** — The connection offers keys and the agent only, so the server's own prompt has nowhere to be shown and a saved password is never used. → §QS218

## Block C — Emulation that does not lie about the remote

- ⏳ **QS141** (deps: QS139 ⏸) **Feed runs near 4 MB/s where the budget asks for 400, and the budget measures a different arm** — Clustering costs nine times what reaches it and cell writes five times again, and what is left is a budget figure for the whole path that somebody has to argue for. → §QS141
- 📋 **QS177** (deps: —) **The strip past the last whole cell is never painted, so every pane has a black band at its right and bottom edges** — No instance covers those pixels and nothing clears the target, so they come back black on every scheme, and on a light one the band is plain to see. → §QS177
- 📋 **QS183** (deps: —) **A paste pressed while another process holds the clipboard open does nothing and says nothing** — Phone Link and WSLg's bridge open the clipboard just after every change, so a paste landing then reads empty and the keystroke is simply lost. → §QS183
- 📋 **QS198** (deps: —) **A scheme's cursor colour and OSC 12 are stored and never drawn, so every pane's cursor is the same grey** — The renderer draws a process-wide constant and never reads the palette's cursor, while SETTINGS.md tells users a scheme sets it. → §QS198
- 📋 **QS203** (deps: —) **Two combining marks on the same side of one base are drawn on the same pixels instead of stacked** — Each mark is placed where it sits over the base alone, because DirectWrite misplaces every mark after the first. → §QS203
- 📋 **QS205** (deps: —) **Cursor up and down cross the scrolling region's edges, so a program moving inside a region writes outside it** — CUU and CUD clamp only to the screen, where DEC and xterm stop them at the margin the cursor started inside. → §QS205
- 📋 **QS206** (deps: —) **Restoring the cursor does not restore the character set, so line drawing after ESC 8 prints letters** — DECSC saves the designations and shift state with the position, and a program that switched sets in between relies on getting them back. → §QS206
- 📋 **QS207** (deps: —) **Insert mode is ignored, so text printed with CSI 4 h overwrites the row instead of pushing it right** — Editors and readline-style prompts use IRM to insert in place, and a terminal that overwrites shows the line the program did not draw. → §QS207
- 📋 **QS208** (deps: —) **A request for 132 columns is ignored without its side effects, and nothing says whether that is the intent** — DECCOLM also clears the screen and resets the margins, so a program that asks gets neither the width nor the clean screen it assumes. → §QS208
- 📋 **QS211** (deps: —) **esctest is judged through a pseudo-console that answers its queries itself, so the figure measures conhost** — A DECRQCRA sent through ConPtyChannel came back as conhost's 0000 and never reached the emulator, so 228 failures cannot move. → §QS211
- 📋 **QS214** (deps: —) **Replaying the tmux resize recording sometimes allocates in steady state in the guest, and the failure says no more** — One guest run counted 7,288 bytes and the next none, and a number with no type is a failure nobody can act on. → §QS214

## Block D — The tree a user organises work in

- 📋 **QS179** (deps: QS121 ✅) **A fleet has no saved group to broadcast to, so the same split is rebuilt by hand before every broadcast** — QS53 built the tab as its target and left the saved group, which needs a session store the running client can read, and that is QS121. → §QS179

## Block E — SCP and SFTP as a thing a person operates

- 📋 **QS184** (deps: QS126 ✅) **The remote pane opens at the account's home while the shell beside it has already said where it is** — OSC 7 already records the shell's directory and nothing reads it, so a user who cd'd into a deployment finds the browser somewhere else. → §QS184
- 📋 **QS185** (deps: QS60 ✅, QS126 ✅) **A remote file cannot be edited in a local editor, so changing a config is still a download, an edit and an upload** — It is why most people open a file browser at all, and the manual round trip is exactly what they want the client to do for them. → §QS185
- 📋 **QS186** (deps: —) **A copy started from the browser shows nothing until it ends and cannot be stopped, so a large tree looks hung** — The queue under it reports each file's progress and can pause, cancel and retry, and none of that reaches the window. → §QS186
- 📋 **QS188** (deps: —) **Nothing can be dragged out of the host's pane, so a file on the server reaches Explorer only through a copy** — Windows asks for a dragged file's data during the drop, and a server cannot always answer in that time, so it needs deferred rendering. → §QS188
- 📋 **QS189** (deps: QS126 ✅) **A file dropped onto an SSH terminal can only be typed as its path, never sent to the directory the shell is in** — QS64 typed the path and left the modifier that transfers instead, which needs an SSH tab and the directory its shell reported. → §QS189
- 📋 **QS219** (deps: QS126 ✅) **The file browser opened over an SSH tab shows nothing on the host's side** — Its remote half asks the tab for a file channel and nothing answers, though the tab now holds the connection one opens on. → §QS219

## Block F — A forward is a lifecycle, not a checkbox

- ⏳ **QS69** (deps: QS66 ✅, QS67 ✅, QS68 ✅, QS38 ✅, QS220) **A forward is set up by hand each time and dies silently when its session drops** — A reconnect does not bring forwards back, since an SSH tab does not reconnect until QS220, and what holds a busy port is not named. → §QS69
- 📋 **QS70** (deps: QS69 ⏳) **Nothing says which forwards are running, so a stale one is discovered through a port conflict** — A forward is invisible by nature, and a client that will not show its own listeners makes the user consult netstat to understand the client. → §QS70

## Block G — The clean interface, defended

- 📋 **QS172** (deps: —) **A user looking for help presses Ctrl+Shift+F1 and gets a defect report** — F1 is where a person looks for help, this client binds it to collecting a diagnostic bundle, and the keys reference it should open is a file in the repository. → §QS172
- 📋 **QS174** (deps: —) **A settings value the client could not use is not mentioned anywhere the user will look** — A mistyped scheme path, an unreadable scheme file and a settings file that will not parse all load as the defaults in silence, and the client looks broken rather than misconfigured. → §QS174
- 📋 **QS178** (deps: —) **Five command-line flags exist and no page names them, so a script author finds them by reading the source** — The client has no menu, so the command line is how another program asks it for anything, and a surface nobody documented is one nobody finds. → §QS178
- 📋 **QS199** (deps: —) **The test that says a chrome theme leaves the terminal's colours alone checks a palette nothing draws with** — Appearance.Palette is a second model of the terminal's colours that no running code reads; the colours a pane draws come from Settings.Colours. → §QS199
- 📋 **QS217** (deps: QS126 ✅) **A saved session can only be opened by typing its path on a command line** — The palette reaches every other action and lists no session, so a user with forty hosts has to remember each one's path. → §QS217
- 📋 **QS221** (deps: QS116 ✅) **A graphics driver reset leaves every pane frozen, and nothing in the client recovers or says so** — The device can recover and nothing asks it to, and a recovery would rebuild closed panes' swapchains because no resource ever leaves its list. → §QS221

## Block H — The reason to leave the incumbent

- ⏳ **QS75** (deps: QS2 ✅, QS46 ✅, the incumbent closed on the reference desk) **Nothing has been measured starting, so the cold start figure is an aspiration** — The incumbent has not been started beside it on the same machine, so the comparison the figure exists for is not made. → §QS75
- ⏳ **QS77** (deps: a code-signing certificate, an update signing key) **There is no way to install this client, so it can only be run from a build directory** — Signing with a real certificate and an update check verified against a pinned key are still owed, and the machine-wide install has not run elevated. → §QS77
- ⏳ **QS78** (deps: QS139 ⏸) **Nothing has run for longer than a working session, so a slow leak would reach users first** — The seventy-two-hour run itself is still owed, with atlas and GPU memory watched, which needs a pane attached to a session. → §QS78
- ⏳ **QS79** (deps: QS3 ✅, QS196, a CI runner that is always the same machine) **A change that costs performance is caught by whoever happens to notice it** — CI still has to run the gate on every commit on a runner that is the same machine each time, and frame cost has to join it once QS196 measures it. → §QS79
- 📋 **QS190** (deps: —) **The window's constructor spends 230 ms of a 647 ms start building chrome the first frame does not show** — The tab strip and the find bar are built before the first paint although both are collapsed until somebody asks for them. → §QS190
- 📋 **QS191** (deps: —) **The graphics device and the shell both wait for the window, so a start runs three slow things one after another** — Neither the device, the atlas and the shaders nor the pseudo-console needs a window, and each could be ready while WPF builds one. → §QS191
- 📋 **QS192** (deps: —) **A portable copy that installs itself leaves its saved sessions and settings behind in the copy it came from** — Somebody who tried the archive portable and imported their sessions opens an installed copy with none of them, and no word of where they went. → §QS192
- 📋 **QS196** (deps: —) **Figure 3 of the budget, steady-state frame cost, is measured by nothing, so no gate can hold it** — The render arm reports stream throughput with parsing folded in, and no harness times one full grid drawn again and again on the CPU and the GPU. → §QS196
- 📋 **QS197** (deps: —) **The parse figure spreads by a fifth between runs on the reference machine, so the gate lets a regression that big pass** — A 28 ms pass on a CPU with performance and efficient cores is timed wherever the scheduler put it, which moves the number more than the parser does. → §QS197
- 📋 **QS200** (deps: —) **The release gate times parse and emulate on the replay harness's build, not on the assemblies being archived** — A runtime setting that exists only in the published client, such as tiered compilation, would slow what ships while the gate reports the figures held. → §QS200
- 📋 **QS201** (deps: —) **An echo at a prompt reaches the glass two refresh intervals after it arrives, one more than the vblank wait explains** — At 120 Hz that shape is still 17 ms, twice figure 1, so a faster panel alone cannot meet the budget. → §QS201

## Block I — An error a user can act on

- ⏳ **QS129** (deps: QS71 ✅, QS220) **Nothing in the client says where its log is, or turns the trace on** — An open tab cannot turn its trace on, since a trace records the handshake and the tab cannot reconnect until QS220. → §QS129
- 📋 **QS134** (deps: QS73 ✅, QS129 ⏳) **Nothing in the window can start a recording, so nobody can capture the defect they hit** — The recorder and the title's indication both work, and the only caller that can begin one is a test, so the feature reaches no user. → §QS134

## Block J — Leaving MobaXterm, proven by the switch

- 📋 **QS216** (deps: —) **Importing sessions writes over the whole session store, so sessions made since are lost without a word** — The import writes the imported tree alone, and since QS121 the store can hold sessions the user made here. → §QS216

## Block K — The build and the harness — what a green run is evidence of

- 📋 **QS176** (deps: —) **The winwright this project restores is older than the engine beside it, so cases work around gaps that are closed** — Three case files explain why they use a flag instead of the chord a user presses, and the engine grew chords in a version this repository does not reference. → §QS176
- 📋 **QS182** (deps: —) **A resumed guest can hold run-app-vm past its own deadline and then yield an all-black picture reported as a success** — The host suite needs the guest suspended, so every picture after the first resumes it, and both halves of that cycle failed while QS53 shipped. → §QS182
- 📋 **QS193** (deps: —) **The release archive is built only by hand, so a change that breaks the self-contained publish passes CI** — CI builds the framework-dependent tree and runs the suite, while ReadyToRun, the runtime packs and the publish itself are exercised only by release.cmd. → §QS193
- 📋 **QS195** (deps: —) **Each test class carries its own copy of the fixture plumbing, so a fault in one copy is a fault in all of them** — QS138 was one fault in two copies of one helper, and the STA runner, the fixture's trust and skip, and the repository walk are each copied sixteen to twenty-one times. → §QS195

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

## Done when — QS69

- **Forwards come back after a reconnect, each said** An SSH tab whose link dropped
  reconnects with every forward it had started again, and the pane names any that did
  not come back and what holds its port.

## Done when — QS129

- **An open tab turns its trace on from the palette** The palette's trace entry
  reconnects the tab with its session traced, the pane says where the trace is written,
  and the next run starts untraced.

## Done when — QS153

- **A phrase composed with a real input method shows no box of its own** Checked by
  typing Japanese through Microsoft IME into a running client: the phrase is underlined
  in the pane with no floating box, the candidate list sits beside it, and the committed
  text reaches the shell once.

## Done when — QS160

- **A tab detached into its own window keeps its connection** Checked by detaching a tab
  with a live SSH session into a new window and asserting the transport is the same
  object, no reconnect was logged, and the shell answers a keystroke in the new window.

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
