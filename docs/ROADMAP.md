# Roadmap (active backlog)

## Priority

## Block A — A session that stays up, or says why it did not

- ⏳ **QS38** (deps: QS37 ✅, QS111) **A link that drops for ten seconds costs the whole session and its scrollback** — A peer that stopped answering on a socket that stayed open is still invisible, because the library's keepalive keeps a NAT mapping rather than detecting a death. → §QS38
- 📋 **QS40** (deps: QS38 ⏳, QS39 ✅) **This client has met one server, so an appliance that negotiates differently is an unknown** — Interoperability failures are found by connecting to unusual servers and by no other method, so the unusual servers are enumerated and connected to deliberately. → §QS40
- 📋 **QS110** (deps: QS37 ✅) **Remote throughput has no local figure taken beside it, so a slow link and a slow client look alike** — Cancelling a pending read aborts a Windows pipe and takes the pseudo-console with it, so the local half of the comparison read nothing at all. → §QS110
- 📋 **QS111** (deps: QS37 ✅) **A peer that stopped answering on a socket that stayed open is not noticed, so the session looks live for minutes** — The library's keepalive sends without expecting an answer, so it keeps a NAT mapping alive and cannot tell a frozen host from a quiet one. → §QS111
- 📋 **QS112** (deps: QS39 ✅) **A dead route and a port that is not SSH read as one failure, so the remedy offered covers two things** — The library's asynchronous connect gives both the same sentence, and the synchronous one that tells them apart takes no cancellation token. → §QS112
- 📋 **QS142** (deps: QS139) **The library will not bound a channel and will not resize one, and only it can do both** — Choosing between a resizable terminal and memory a fast host cannot exhaust is a choice this client should not have to make, and no local change removes it. → §QS142

## Block B — Keys, agents, and the host you think you reached

- ⏳ **QS43** (deps: QS41 ✅, QS114) **A key already unlocked in an agent must be typed again, and a hardware key cannot be used at all** — Pageant older than 0.78 speaks over shared memory rather than a pipe, and that transport is the half of it this does not reach. → §QS43
- 📋 **QS45** (deps: QS43 ⏳) **Nothing forwards an agent, and nothing would stop a compromised host from using one if it did** — Forwarding hands a remote machine the ability to authenticate as the user everywhere, so it is decided per host rather than by a checkbox set once. → §QS45
- 📋 **QS113** (deps: QS41 ✅, QS46 ✅) **A key accepted with a second factor still to come looks the same as a connection that has stalled** — Partial success is a normal state of the protocol and nothing in this client reports it, so the one moment a user most wants progress is the one with none. → §QS113
- 📋 **QS114** (deps: QS41 ✅) **A PuTTY user on Pageant older than 0.78 has an agent this client cannot reach at all** — That version carries the same requests over shared memory and a window message, and this client speaks only the named pipe the newer one added. → §QS114
- 📋 **QS115** (deps: QS44 ✅) **A master password is stretched by a function a graphics card is good at, where the design asked for one it is not** — The framework ships no memory-hard derivation, so the choice was a third-party dependency where a mistake is unrecoverable, or the strongest thing it has. → §QS115

## Block C — Emulation that does not lie about the remote

- 🛠 **QS33** (deps: QS17 ✅, QS18 ✅, QS20 ✅, QS21 ✅, QS25 ✅, QS93) **No external suite has ever judged this emulator, so its fidelity is the author's own opinion** — vttest's verdict is a person looking at the screen, so automating it needs something to compare against rather than a way to press its keys. → §QS33
- 📋 **QS91** (deps: —) **A combining mark takes no cell and is then drawn nowhere, so an accent typed as two codepoints vanishes** — Text that arrives decomposed is ordinary on macOS and in Git output, and a client that silently drops the accent is one that shows the wrong filename. → §QS91
- 📋 **QS92** (deps: —) **The golden suite has run on two rasterisers and none of the three vendor drivers its matrix names** — A driver bug is by definition the thing the machine that wrote the code cannot see, so a suite that has only ever run here is one nobody has tested yet. → §QS92
- 📋 **QS93** (deps: QS25 ✅) **Every golden scene is text somebody typed into the test, so none of them is a screen a real program drew** — A scene an author invented exercises what that author thought of, which is never the combination that turns out to break on somebody's machine. → §QS93
- 📋 **QS101** (deps: —) **Ninety-six bytes of the parse path's allocation is measured but unattributed** — Three hostile shapes cost thirty-two bytes each per pass, only when the whole sequence runs, so something oscillates between two states and the zero-allocation claim carries a ceiling instead. → §QS101
- 📋 **QS103** (deps: —) **Nothing answers a request for a rectangle's checksum, so an external suite cannot read the screen back** — It is how esctest checks a cell, so its absence fails two hundred and twenty-eight tests that are about something else entirely. → §QS103
- 📋 **QS104** (deps: —) **A program asking whether a mode is set gets no answer, so it cannot tell off from unsupported** — DECRQM is how a program discovers what this terminal can do, and twenty-two esctest cases fail on the silence alone. → §QS104
- 📋 **QS105** (deps: —) **Backspace at the left edge stops there instead of wrapping to the end of the line above** — A shell editing a command that wrapped moves the cursor back through the wrap, and a terminal that will not follow leaves the cursor and the shell disagreeing. → §QS105
- 📋 **QS107** (deps: QS35 ✅) **ClearType coverage is drawn without the contrast enhancement Windows applies, so stems stay lighter than elsewhere** — The coverage DirectWrite hands back is raw, and the curve its renderers apply on top is not published, so matching it needs a reference rather than a formula. → §QS107
- 📋 **QS108** (deps: QS26 ✅, QS27 ✅) **The suite builds the solution and then measures wall-clock latency against what that build left running** — Two latency tests fail only after a build, so the suite cannot tell a regression from a busy machine, and a red run teaches nobody anything. → §QS108
- 📋 **QS139** (deps: —) **A host sending faster than the parser consumes buffers gigabytes inside the channel** — The bytes sit in the transport rather than in the emulator, so an unread session grows without limit while a headless parse of 64 MB retains under 8 MB. → §QS139
- 📋 **QS140** (deps: —) **The reply buffer exceeds the maximum its own constant states** — The cap is checked before an answer is appended rather than after, so a hostile host reaches 4098 bytes against a stated 4096 and the constant is not the bound. → §QS140
- ⏳ **QS141** (deps: QS139) **Feed runs near 4 MB/s where the budget asks for 400, and the budget measures a different arm** — Clustering costs nine times what reaches it and cell writes five times again, and what is left is a budget figure for the whole path that somebody has to argue for. → §QS141
- 📋 **QS153** (deps: QS29 ✅) **The text being composed is a box the input method draws over the grid, not underlined text inside it** — QS29 placed that box on the cursor and stopped there, so a phrase being typed sits on top of the line it belongs in. → §QS153
- 📋 **QS155** (deps: QS21 ✅, QS30 ✅) **A program that asks for the mouse never gets one, and the wheel scrolls nothing at all** — QS21's encoder and QS22's viewport are named by no code in the client, so both shipped features are unreachable from the running program. → §QS155
- 📋 **QS156** (deps: QS154 ✅) **Grapheme segmentation runs at half its old rate, because every cluster now asks ICU where the boundary is** — QS154 turned invariant globalization off to keep the client alive, and StringInfo stopped using the runtime's own simple breaking. → §QS156
- 📋 **QS158** (deps: QS31 ✅) **Nothing says how far back the view is, so a reader who scrolled has no idea where they are or that output arrived** — QS31 gave the viewport a wheel and a find and left it with no indicator, and the reading it already answers reaches nobody. → §QS158
- 📋 **QS177** (deps: —) **The strip past the last whole cell is never painted, so every pane has a black band at its right and bottom edges** — No instance covers those pixels and nothing clears the target, so they come back black on every scheme, and on a light one the band is plain to see. → §QS177
- 📋 **QS183** (deps: —) **A paste pressed while another process holds the clipboard open does nothing and says nothing** — Phone Link and WSLg's bridge open the clipboard just after every change, so a paste landing then reads empty and the keystroke is simply lost. → §QS183

## Block D — The tree a user organises work in

- 📋 **QS117** (deps: QS55 ✅) **Comments a user wrote in the session store are gone the next time the client writes it** — Reading the file is lossless and writing it is not, so a store edited by hand and then edited through the client loses the half a person put there for themselves. → §QS117
- 📋 **QS119** (deps: QS57 ✅) **A jump carries traffic through a local port anything running as this user can connect to** — A bastion exists to be the only way in, and a port on this machine that reaches the target unauthenticated is a second way in that the user never opened. → §QS119
- 📋 **QS121** (deps: QS55 ✅, QS58 ✅) **Nothing owns the session store file and nothing opens the session dialog, so neither is reachable** — A store with no owner and a dialog with no way in are two finished parts that a user cannot get to, which is the same to them as neither existing. → §QS121
- 📋 **QS179** (deps: QS121) **A fleet has no saved group to broadcast to, so the same split is rebuilt by hand before every broadcast** — QS53 built the tab as its target and left the saved group, which needs a session store the running client can read, and that is QS121. → §QS179

## Block E — SCP and SFTP as a thing a person operates

- 📋 **QS122** (deps: QS59 ✅) **The shared-session trick rests on six SSH.NET members reached by name, and only a live server proves it holds** — A library upgrade can break it, and the test that would notice needs a container running, so an upgrade on a machine without one looks clean. → §QS122
- 📋 **QS123** (deps: QS62 ✅) **A symbolic link on the server cannot be copied, because nothing here can read where it points** — A tree copied down loses every link in it, which for a source checkout or a set of config files is a copy that does not work at the far end. → §QS123
- 📋 **QS184** (deps: QS126) **The remote pane opens at the account's home while the shell beside it has already said where it is** — OSC 7 already records the shell's directory and nothing reads it, so a user who cd'd into a deployment finds the browser somewhere else. → §QS184
- 📋 **QS185** (deps: QS60 ✅, QS126) **A remote file cannot be edited in a local editor, so changing a config is still a download, an edit and an upload** — It is why most people open a file browser at all, and the manual round trip is exactly what they want the client to do for them. → §QS185
- 📋 **QS186** (deps: —) **A copy started from the browser shows nothing until it ends and cannot be stopped, so a large tree looks hung** — The queue under it reports each file's progress and can pause, cancel and retry, and none of that reaches the window. → §QS186
- 📋 **QS188** (deps: —) **Nothing can be dragged out of the host's pane, so a file on the server reaches Explorer only through a copy** — Windows asks for a dragged file's data during the drop, and a server cannot always answer in that time, so it needs deferred rendering. → §QS188
- 📋 **QS189** (deps: QS126) **A file dropped onto an SSH terminal can only be typed as its path, never sent to the directory the shell is in** — QS64 typed the path and left the modifier that transfers instead, which needs an SSH tab and the directory its shell reported. → §QS189

## Block F — A forward is a lifecycle, not a checkbox

- 📋 **QS68** (deps: QS66 ✅, QS127) **Reaching many hosts on the remote network needs one forward configured per host** — A SOCKS proxy is a single forward covering a whole network, which is what a browser or a cloud tool needs and what per-host forwards cannot provide. → §QS68
- 📋 **QS69** (deps: QS66 ✅, QS67 ✅, QS68, QS38 ⏳) **A forward is set up by hand each time and dies silently when its session drops** — A forward is something a user relies on for hours without looking at it, so its failure has to be visible and its restart has to be automatic. → §QS69
- 📋 **QS70** (deps: QS69) **Nothing says which forwards are running, so a stale one is discovered through a port conflict** — A forward is invisible by nature, and a client that will not show its own listeners makes the user consult netstat to understand the client. → §QS70
- 📋 **QS124** (deps: QS66 ✅) **A forward drops the whole connection when one direction half-closes, so protocols that shut and wait hang** — Many protocols send, shut their sending half, and wait for the answer; against this forward they get a closed socket instead of a reply. → §QS124
- 📋 **QS125** (deps: QS66 ✅) **A forward cannot tell a refused target from a normal close, and cannot be bound to every interface** — The design asks for three failures told apart and only one is, so a user with a wrong port and a user with a wrong name are shown the same nothing. → §QS125
- 📋 **QS127** (deps: QS124) **The library's SOCKS proxy answers about one request in six with something that is not a SOCKS reply** — A proxy that drops a request in six is worse than none: a browser retries and a script fails, and neither can tell this apart from the network. → §QS127

## Block G — The clean interface, defended

- 📋 **QS83** (deps: QS82 ✅, QS46 ✅) **Every window would invent its own colours and row shapes, so the chrome drifts from the two clients it should match** — The design system already exists in two shipped clients, so what is decided here is whether it is borrowed whole or rediscovered a window at a time. → §QS83
- 📋 **QS126** (deps: QS121) **Eleven shipped transport components are named by no code in the application, so none of them can be used** — Four blocks of tested, working machinery are unreachable from the running program, so the feature count falls while the product does not move. → §QS126
- 📋 **QS151** (deps: QS116 ✅) **A reconnecting session makes a new damage signal per connection, so a pane asleep on the first never repaints** — QS126 wires this up and inherits a window frozen at whatever frame it drew first. → §QS151
- 📋 **QS152** (deps: QS116 ✅) **A shell that exits leaves the window holding its last frame, with nothing saying the session ended** — Typing exit is the ordinary way a session ends, and the client that answers it with a frozen picture reads as hung. → §QS152
- 📋 **QS160** (deps: QS47 ✅) **A tab cannot be dragged into a new order or out into its own window, so the workspace is fixed as it was opened** — QS47 put the session in the tab and stopped there, so the one thing that arrangement was for is the one thing that cannot be done. → §QS160
- 📋 **QS163** (deps: QS48 ✅) **A divider cannot be dragged, so the only sizes a split has are the halves it was created with** — QS48 gave the layout a share to set and left every way of setting it to a chord, so the gesture a person reaches for first does nothing. → §QS163
- 📋 **QS164** (deps: QS48 ✅) **Four panes open four graphics devices, and nothing in the client stops a user opening sixteen** — QS48 made splitting one chord away and QS49 has not landed, so the cost the block's own criterion bounds is now reachable by holding a key. → §QS164
- 📋 **QS168** (deps: QS50 ✅, QS49 ✅) **Changing the font in the settings file does nothing until the client is restarted** — QS50 applies the cursor and the blink live and leaves the one setting people actually experiment with needing a restart, which is what stops them experimenting. → §QS168
- 📋 **QS170** (deps: QS50 ✅) **Every setting has to be found and typed by hand, because the surface over the file was never built** — QS50 shipped the file and its reference and left the half a user meets first, so discovering a setting means reading a document about a file. → §QS170
- 📋 **QS171** (deps: —) **Splitting a pane is bound to one Oem key, and which character that is depends on the layout** — Splitting beside was bound twice because the backslash moves between layouts; splitting below was bound once, and nothing tested the keyboards it moves on. → §QS171
- 📋 **QS172** (deps: —) **A user looking for help presses Ctrl+Shift+F1 and gets a defect report** — F1 is where a person looks for help, this client binds it to collecting a diagnostic bundle, and the keys reference it should open is a file in the repository. → §QS172
- 📋 **QS174** (deps: —) **A settings value the client could not use is not mentioned anywhere the user will look** — A mistyped scheme path, an unreadable scheme file and a settings file that will not parse all load as the defaults in silence, and the client looks broken rather than misconfigured. → §QS174
- 📋 **QS178** (deps: —) **Five command-line flags exist and no page names them, so a script author finds them by reading the source** — The client has no menu, so the command line is how another program asks it for anything, and a surface nobody documented is one nobody finds. → §QS178

## Block H — The reason to leave the incumbent

- ⏳ **QS75** (deps: QS2 ✅, QS46 ✅, the incumbent closed on the reference desk) **Nothing has been measured starting, so the cold start figure is an aspiration** — The incumbent has not been started beside it on the same machine, so the comparison the figure exists for is not made. → §QS75
- ⏳ **QS77** (deps: a code-signing certificate, an update signing key) **There is no way to install this client, so it can only be run from a build directory** — Signing with a real certificate and an update check verified against a pinned key are still owed, and the machine-wide install has not run elevated. → §QS77
- ⏳ **QS78** (deps: QS139) **Nothing has run for longer than a working session, so a slow leak would reach users first** — The seventy-two-hour run itself is still owed, with atlas and GPU memory watched, which needs a pane attached to a session. → §QS78
- 📋 **QS79** (deps: QS3 ✅) **A change that costs performance is caught by whoever happens to notice it** — The measurements exist and are trusted by now, so all that is left is making a regression fail a build instead of reaching a release. → §QS79
- 📋 **QS86** (deps: QS7 ✅, QS9 ✅) **Input to photon is the first figure in the budget and the only one nothing has ever measured** — The present path was built to bound it and the one workload that exists cannot run ahead of the display, so the flags remain an argument rather than a number. → §QS86
- 📋 **QS135** (deps: QS74 ✅) **Three settings are read, written and kept faithfully, and nothing acts on them** — The typeface, its size and the scrollback depth reach no pane, so a user who edits the file sees the theme change and the rest do nothing. → §QS135
- 📋 **QS137** (deps: QS76 ✅) **The idle figure is measured on a window with no session and no render loop in it** — Zero core time over ten minutes is real and is not the connected-session number the budget will be read against, and nothing yet can put the client in that state. → §QS137
- 📋 **QS190** (deps: —) **The window's constructor spends 230 ms of a 647 ms start building chrome the first frame does not show** — The tab strip and the find bar are built before the first paint although both are collapsed until somebody asks for them. → §QS190
- 📋 **QS191** (deps: —) **The graphics device and the shell both wait for the window, so a start runs three slow things one after another** — Neither the device, the atlas and the shaders nor the pseudo-console needs a window, and each could be ready while WPF builds one. → §QS191
- 📋 **QS192** (deps: —) **A portable copy that installs itself leaves its saved sessions and settings behind in the copy it came from** — Somebody who tried the archive portable and imported their sessions opens an installed copy with none of them, and no word of where they went. → §QS192

## Block I — An error a user can act on

- 📋 **QS128** (deps: QS71 ✅) **A trace shows what this client offered and never what the server did** — Half a negotiation cannot settle "no algorithm in common", so the appliance failures this level exists for are still diagnosed by guesswork. → §QS128
- 📋 **QS129** (deps: QS71 ✅) **Nothing in the client says where its log is, or turns the trace on** — A log a user cannot find is a log that does not exist, and a trace that can only be enabled by editing code is one no bug report will ever carry. → §QS129
- 📋 **QS130** (deps: QS71 ✅) **The log goes quiet exactly where a transfer or a tunnel failed** — A connection's own life is recorded and everything carried over it is not, so the reports hardest to reproduce are the ones the log has least to say about. → §QS130
- 📋 **QS131** (deps: QS72 ✅) **The crash dialog mixes this client's English with Windows' own button words** — Two dialogs do it now, the crash report and the diagnostics bundle, and "Yes/No" names neither of the things their buttons actually do. → §QS131
- 📋 **QS132** (deps: QS72 ✅) **A crash report cannot name the GPU, on the failures most likely to be about one** — The adapter line reads "no device is held at this level" because nothing above the pane holds one, so a driver report arrives without the driver. → §QS132
- 📋 **QS133** (deps: QS73 ✅) **A recording has no bound and will fill a disk if it is left running** — The log rotates against a fixed total and a recording does not, so the one that writes every byte a host sends is the one with nothing stopping it. → §QS133
- 📋 **QS134** (deps: QS73 ✅, QS129) **Nothing in the window can start a recording, so nobody can capture the defect they hit** — The recorder and the title's indication both work, and the only caller that can begin one is a test, so the feature reaches no user. → §QS134
- 📋 **QS150** (deps: —) **A defect report about a black window says what an adapter probe found, not what the window did** — The bundle opens its own probe because nothing at that level held a device, and now the client holds one that has been drawing. → §QS150

## Block J — Leaving MobaXterm, proven by the switch

- 📋 **QS81** (deps: QS80 ✅, QS116 ✅, QS126) **A user weighing the switch has nothing that says what they will and will not get** — The non-goals list is already written and honest, and a user deciding whether to move their fleet is exactly who needs to read it beforehand. → §QS81

## Block K — The build and the harness — what a green run is evidence of

- 📋 **QS89** (deps: —) **dotnet test reports zero tests and exits 5 while run-tests.cmd runs the same assemblies and passes** — Every .NET tool and CI template reaches for that command first, so a repository where it lies looks broken to everyone who has not read the script. → §QS89
- 📋 **QS90** (deps: —) **Nobody has built this repository from a clean clone, so the steps it needs beyond the SDK are unknown** — Every build so far has run on the machine that wrote the code, which is the one machine whose caches and installed components prove nothing about the next. → §QS90
- 📋 **QS99** (deps: —) **A failed build leaves the old test binary in place and running it reports a green suite that proves nothing** — Twice this session a compile error was swallowed and the previous assembly ran, reporting the old pass count as if it were the new one. → §QS99
- 📋 **QS102** (deps: QS24 ✅) **The parser is fuzzed only by the suite's own mutator, which stops when the build does** — A bounded run at a fixed seed explores the same inputs for ever, so coverage-guided fuzzing needs a harness that runs for hours outside the suite. → §QS102
- 📋 **QS136** (deps: —) **A run with a hundred tests skipped prints the same "Passed" as one with none** — The fixture stops on its own and the summary line does not change, so the green that proves nothing looks exactly like the green that proves everything. → §QS136
- 📋 **QS149** (deps: —) **A tool that rewrites a source file can mangle its punctuation and every check stays green** — The hygiene test bans control bytes and says nothing about text that decoded through the wrong codepage on its way back to disk. → §QS149
- 📋 **QS159** (deps: QS147 ✅) **The shipping discipline tells an agent to screenshot a UI task, and this repository's evidence rule forbids that** — A session followed the skill, spent an hour driving the operator's desk with synthetic keystrokes, and never found the winwright cases already here. → §QS159
- 📋 **QS167** (deps: QS95 ✅) **An allocation assertion fails about once in two guest runs on a tree nothing touched** — Block K asks that no test fail intermittently, and this one was watched doing it: red in the guest, green on the host and green on the guest's next run. → §QS167
- 📋 **QS176** (deps: —) **The winwright this project restores is older than the engine beside it, so cases work around gaps that are closed** — Three case files explain why they use a flag instead of the chord a user presses, and the engine grew chords in a version this repository does not reference. → §QS176
- 📋 **QS181** (deps: —) **The import preview case missed its dialog in one full run and found it alone, so its five-second wait is a guess** — Block K rules out a test that fails on an unchanged tree, and when this one did its refusing step invoked the main window's Close button instead. → §QS181
- 📋 **QS182** (deps: —) **A resumed guest can hold run-app-vm past its own deadline and then yield an all-black picture reported as a success** — The host suite needs the guest suspended, so every picture after the first resumes it, and both halves of that cycle failed while QS53 shipped. → §QS182
- 📋 **QS193** (deps: —) **The release archive is built only by hand, so a change that breaks the self-contained publish passes CI** — CI builds the framework-dependent tree and runs the suite, while ReadyToRun, the runtime packs and the publish itself are exercised only by release.cmd. → §QS193

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

## Done when — QS136

- **The run repeats its failures and its skip count at the very end** Progress lines
  dominate the log, so any truncated view loses the one thing being looked for — it cost
  two rereads in one session. Checked by the last lines of a red run naming every failed
  test and the skip count, without scrolling.

## Done when — QS141

- **Where the hundredfold goes is named, per stage** `parse` reports 1,200 MB/s on
  cat-log and `emulate` 10, and a ratio is not a diagnosis. Checked by a measurement
  attributing the difference to named work — cell writes, scrolling, grapheme
  segmentation, decoding — rather than to the emulator as a whole.
- **The whole path has a budget figure somebody argued for** PERFORMANCE.md now says
  there is none rather than inventing one. Checked by figure 2 or a figure beside it
  stating a number for the `emulate` arm, with the reasoning for that number and not
  merely the measurement it was taken from.

## Done when — QS38

- **A peer that stopped answering is noticed in seconds, not in the OS's own time**
  Reconnect landed with its backoff and its honesty about what a new shell cannot
  restore. This is the third failure of the three, the one where the socket stays open,
  and the library's keepalive answers a different question: QS111 is where that is
  measured.

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
