# Shipped Ledger

## Block A — A session that stays up, or says why it did not

- ✅ **QS5** **Nothing measures whether the chosen protocol library reaches a modern sshd, so the network layer is unpriced risk** — SSH.NET does certificates, jump hosts and two factors against OpenSSH 9.6; the agent and ssh_config are ours to write (design §QS5 recorded in `docs/measurements/ssh-net-probe.md`).
- ✅ **QS36** **Protocol library types would reach the terminal and the UI, so replacing that library means rewriting the client** — The seam is four interfaces of this client's own words, with a second implementation behind it that needs no server and a test that fires when a library escapes.
- ✅ **QS37** **No remote host's output has ever reached the emulator** — A remote shell arrives through the same four members a local one does, carrying 124.8 MB/s at 233 KB allocated per megabyte against a real OpenSSH server.
- ✅ **QS38 (the reconnect)** **A link that drops for ten seconds costs the whole session and its scrollback** — A dropped link costs a command rather than the scrollback: the model outlives the connection, backoff is bounded and visible, and an exit is not retried.
- ✅ **QS39** **A refused connection reports a library exception, so a user cannot tell a wrong port from a wrong key** — Eleven failures, each provoked against a real socket or server before its rule was written, and each carrying what happened, what it means and what to do.

## Block B — Keys, agents, and the host you think you reached

- ✅ **QS41** **Only one way in is proven, so a host needing a second factor or an ed25519 key is unreachable** — Three key types in four formats, none offered first and a password last, and a second factor asked in the server's own words against an account that enforces both.
- ✅ **QS42** **Nothing checks the host key, so a machine in the middle is indistinguishable from the server** — The user's own known_hosts decides, hashed entries and all, and a changed key is refused without ever being put to anybody as a question.
- ✅ **QS43 (the OpenSSH agent)** **A key already unlocked in an agent must be typed again, and a hardware key cannot be used at all** — A key generated inside an agent, held nowhere else and readable by nothing, authenticates against a real OpenSSH server through a protocol written here.
- ✅ **QS44** **A saved password would rest on disk where anything running as the user can read it** — A password lives in a pinned buffer that is zeroed and can never be a string, and rests in the user's own Credential Manager bound to their Windows account.

## Block C — Emulation that does not lie about the remote

- ✅ **QS6** **Nothing opens a graphics device, so no pixel this client draws has ever reached a screen** — A D3D11 device opens on the window's adapter, the default one or WARP, names what it skipped, and rebuilds every GPU resource on a loss.
- ✅ **QS7** **A frame reaches the screen whenever the compositor chooses, so no bound on input-to-photon delay can be claimed** — A flip-discard swapchain waits on its own latency handle at a queue measured one frame deep, asks for tearing, and resizes without a stretch.
- ✅ **QS8** **No glyph is on the GPU, so drawing a character would cost a rasterisation on the frame that needs it** — Every glyph is rasterised once and sampled thereafter, keyed on the subpixel offset so a character keeps its weight in every column (design §QS8 recorded in `src/Quickshell.Render/GlyphAtlas.cs`).
- ✅ **QS9** **Nothing turns a grid of cells into a draw call, so text on this client's screen is still hypothetical** — The whole grid is one DrawInstanced of twenty-byte cells, blended in linear light so text weighs the same either way round (design §QS9 recorded in `src/Quickshell.Render/CellRenderer.cs`).
- ✅ **QS10** **A CJK character, a combining mark and a colour emoji each draw wrongly or not at all** — A fallback face, a colour page and a two-cell span, so CJK and emoji draw where the model says they are (design §QS10 superseded: its fallback API needs a callback Vortice cannot marshal).
- ✅ **QS87** **The frame-queue measurement reads two on runs where nothing regressed, so its result is not evidence either way** — The warm-up waits for DXGI's statistics instead of counting frames, and the assertion is the bound the instrument can see (design §QS87 superseded: the counter it blamed reads identical).
- ✅ **QS11** **Underline, undercurl, strikethrough, the cursor and a selection have no representation on screen** — Five underline styles, overline, strike, three cursor shapes and selection, all derived in the pixel shader from the same twenty bytes (design §QS11 recorded in `src/Quickshell.Render/Grid.hlsl`).
- ✅ **QS12** **Nothing catches a renderer that draws correctly on one vendor's driver and wrongly on another's** — Seven scenes rendered offscreen and compared against committed references, on this machine's adapter and on WARP.
- ✅ **QS13** **A multi-byte character split across two network reads decodes as two broken ones** — A stateful decoder, UAX #29 clusters that survive any split, and a width table generated from Unicode 17.0.0 (design §QS13 recorded in `tools/generate-width-table.py`).
- ✅ **QS14** **A byte carrying an escape sequence is indistinguishable from text, so a host cannot move the cursor** — Williams' table over fourteen states and all 256 bytes, emitting events and allocating nothing per parse (design §QS14 superseded: a UTF-8 stream cannot honour single-byte C1).
- ✅ **QS15** **Nothing holds what the remote host has printed, so a line that scrolls off the top is gone** — A ring with a moving origin: one scroll writes one row, whether the scrollback is ten lines or a hundred thousand.
- ✅ **QS96** **A third rasteriser shifts one antialiased pixel by six levels, which is three times the measured tolerance** — Text scenes allow the eight levels the rasteriser was measured to move; a glyph-free scene holds the shader to one (design superseded: the shader cannot move a pixel one level, let alone six).
- ✅ **QS97** **The frame queue reaches three under a virtualised presentation path, where the bound was measured at two** — The test asserts the queue does not grow, since its depth is the presentation pipeline's and its growth is the renderer's (design superseded: reading the swapchain gives the two it had).
- ✅ **QS16** **The cursor cannot be moved, a line cannot be erased and no character carries a colour** — Cursor movement, erasing and editing that clamp, plus SGR in both spellings, with default and indexed colours left unresolved.
- ✅ **QS17** **A full-width line wraps a column early and a scrolled region redraws the wrong rows** — Writing the last column owes a wrap rather than taking one, and margins, origin mode and a real tab-stop set all hold.
- ✅ **QS18** **Box drawing renders as letters, and the title, palette and working directory a host sends are ignored** — ESC ( 0 now draws a box corner where lqqqk used to be letters, and the title, palette, default colours, working directory and hyperlinks a host sends all land, OSC 52 still refused.
- ✅ **QS19** **A remote host can write the local clipboard and read back a string it planted in the title** — The clipboard is off per session and write-only when on, the title is set and never reported, and every other reply is a constant and some numbers.
- ✅ **QS20** **A program asking the terminal what it is gets no answer and falls back to a dumber mode** — DA1, DA2 and the status reports answer, and DECRQSS reports SGR, the region and the level and refuses everything else instead of leaving the asker waiting.
- ✅ **QS21** **A click inside a remote editor or pager does nothing, because no mouse event reaches the host** — A click, drag or wheel reaches the host in whichever of the four modes it asked for, in SGR where offered, and past column 223 the legacy encoding refuses out loud rather than naming another cell.
- ✅ **QS22** **Nothing records which rows changed, so any redraw is a whole-screen redraw** — Every mutation moves a generation the renderer compares, a scroll dirties one row rather than the screen, and after the host stops a window authorises no further frame.
- ✅ **QS23** **Narrowing the window turns wrapped output into ragged fragments that never recover** — Narrowing and widening again comes back to the same rows, the cursor stays on the character it was on, and the alternate screen is replaced rather than re-wrapped.
- ✅ **QS24** **A malformed escape sequence from a hostile host has never been shown not to crash or allocate** — Mutated and pathological input is answered rather than thrown at, and replaying a real stream allocates zero bytes where it used to allocate fifty-five kilobytes per megabyte.
- ✅ **QS25** **The emulator has no real producer of bytes, so every test of it is a fixture its own author wrote** — A real shell runs behind the same four members an SSH channel will be behind, its output reaches the emulator as VT bytes, and closing it leaves no process and no handle behind.
- ✅ **QS26** **Under heavy output the delay before a typed character appears grows without bound** — The parser drains its whole queue before the renderer hears anything, so thirty-two times the bytes cost one and a half times the worst wait and no byte is dropped getting there.
- ✅ **QS27** **A keystroke queues behind a screenful of pending output before it is written to the host** — A keystroke leaves in twenty microseconds while forty-eight megabytes stream in, sharing no queue with the output and allocating nothing of its own.
- ✅ **QS94** **Segmenting a printed run allocates a string per character, which is the whole of the render arm's cost** — The render arm's allocation went from fifty-four megabytes per megabyte of stream and a hundred and two collections to the harness's own floor and none.
- ✅ **QS28** **Arrow keys, function keys and modified keys send nothing a remote program recognises** — Arrows, function keys and modified keys send the forms this client's own terminal name promises, and change shape when the host asks for the application modes.
- ✅ **QS29 (the composition model)** **Typing Japanese or Chinese shows no composition and commits nothing** — Composition is display state that never touches the buffer, so cancelling leaves nothing behind, and a candidate list is placed by real cell width rather than by counting characters.
- ✅ **QS30 (the selection and paste model)** **Text on screen cannot be selected or copied, and a paste arrives as keystrokes the host may run** — A wrapped line copies as one line with no break in it, a terminal's padding is not copied as text, and a paste is stripped of control bytes and bracketed or confirmed.
- ✅ **QS31 (the viewport and the search)** **Output that scrolled past cannot be scrolled back to, and nothing in it can be found** — A search finds a match the wrap fell inside, and a viewport anchored to a line stays on the text a reader is reading while the host keeps printing.
- ✅ **QS32** **Resizing the window leaves the remote program drawing to the geometry it had before** — The model reflows first and the far end is told once the drag settles, so a real shell reports the size the drag ended on, and a drag of two hundred sizes costs a handful of requests.
- ✅ **QS33 (esctest)** **No external suite has ever judged this emulator, so its fidelity is the author's own opinion** — Somebody else's suite now judges the model through a real pseudo-console: 151 of 568 pass, and the 375 failures are grouped by cause and named by class in a report the repository keeps.
- ✅ **QS34** **A programming font's ligatures do not form, so text set in it looks unlike the same text elsewhere** — A run's glyphs now come from the font's shaper, cached by the run's own text, and the cell under the cursor goes back to its character so no ligature can hide which one it is.
- ✅ **QS35** **Text is rasterised in grayscale, so it looks thinner here than in every other Windows application** — Coverage is three numbers per pixel where the display has three stripes, refused on a panel that has none and reversed on one that runs the other way.
- ✅ **QS109** **The golden suite's text tolerance does not hold on the CI runner, so every commit's build is red for the same pixels** — A text scene is judged by how far the whole picture moved rather than by its noisiest pixel, which separates a rasteriser from a regression by 251 times.

## Block D — The tree a user organises work in

- ✅ **QS55** **Every connection is retyped, so a host used daily costs the same as one used once** — A tree of folders whose settings a session inherits and may override, each value naming the node that set it, in a file that holds no secret and survives a hand edit.
- ✅ **QS56** **Hosts already defined for OpenSSH have to be defined a second time here** — The user's own ssh_config is read with OpenSSH's first-value-wins rule and its negations, never written, and every directive not acted on names its file and line.
- ✅ **QS57 (the built-in jump path)** **A host reachable only through a bastion cannot be reached at all** — A host reachable only on the container network is reached through a bastion, one loop for any depth, and every failure names which hop of how many it was.

## Block E — SCP and SFTP as a thing a person operates

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

- ✅ **QS4** **No decision records which window host can present a swapchain without adding frames of latency** — WPF with a child HWND per pane won on a click-to-pixel floor of 13.8 ms, the only host of three to answer within one refresh interval (design §QS4 recorded in `docs/measurements/host-probe.md`).
- ✅ **QS46 (the shell)** **There is no window, only a render surface, so nothing can be opened, themed or closed** — A window that opens onto a title bar and a terminal, follows the system theme while running, and comes back where it was on this arrangement of screens.
- ✅ **QS54** **A screen reader finds nothing on the terminal surface, so the client cannot be used without sight** — The buffer is published as a text pattern with scrollback and screen as one document, the cursor as the caret, and a flood of output as one announcement.

## Block H — The reason to leave the incumbent

- ✅ **QS1** **The repository holds no buildable tree, so no claim about this client's speed or size can be measured** — A fresh clone builds and tests four layered projects with nullable, warnings-as-errors and the allocation analyzers on, and a project reference that inverts the layering fails the build.
- ✅ **QS2** **Fast and lean are this client's whole premise, and no number anywhere states what either word means** — docs/PERFORMANCE.md fixes six figures, the method that settles each and the machine they are measured on, so a claim about speed or size is checked against a number instead of a word.
- ✅ **QS82** **The tree targets net8.0, where WPF has no Fluent theme, so the chrome the sibling clients share cannot be built here** — Every project targets net10.0-windows, so WPF's Fluent ThemeMode is reachable here and a guard test fails any retarget that would take it back out.
- ✅ **QS84** **The pipeline pins its actions by tag and nothing watches them, so a stale or advisory-bearing action is found by chance** — Dependabot watches the actions the pipeline pins and opens one grouped pull request a week, so a stale action arrives as a review rather than as a surprise.
- ✅ **QS85** **The commit script stages the whole tree, so a second session's half-written work lands inside another task's commit** — run-commit.cmd stages only the paths a commit declares after --, and says what it is sweeping when none are declared, so a second session's files stay out.
- ✅ **QS3** **Nothing replays a real terminal workload, so a change that halves throughput lands unnoticed** — Both replay arms are wired, and the gap between them is 623 against 8 MB/s on the 32 MB stream.

## Block I — An error a user can act on

## Block J — Leaving MobaXterm, proven by the switch

## Block K — The build and the harness — what a green run is evidence of

- ✅ **QS88** **dotnet test reports zero tests and exits non-zero on a tree whose suite passes when the assembly is run directly** — `.\run-tests.cmd` runs every test assembly, and its exit code is the verdict (design §QS88 superseded: it falsified only on dotnet test) (design §QS88 recorded in `run-tests.cmd`).
- ✅ **QS95** **The render suite takes the screen with topmost windows, and anything the operator does corrupts it** — One command runs the suite on a VMware guest, and the host's screen stops being part of the measurement (design superseded: the guest is a third rasteriser, not this desk).
- ✅ **QS98** **A test can carry a raw escape byte, and a file where it went missing fails as though the code were wrong** — A test asserts no source under tests carries a control byte, and the ninety-six already there are escapes now.
- ✅ **QS100** **The ban on raw control bytes covers tests and not src, so the same byte went into shipped source unseen** — The walk covers src and tests both, and the six raw ESC bytes it found in Emulator.Replies.cs are spelled as escapes.
