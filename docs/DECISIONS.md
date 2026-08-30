# Decisions

## Block A — A session that stays up, or says why it did not

- ✅ **QS5** **Nothing measures whether the chosen protocol library reaches a modern sshd, so the network layer is unpriced risk** — SSH.NET stays: it answers certificates, jump hosts and two factors against OpenSSH 9.6, and the two it does not answer are additive work on a public seam.
- ✅ **QS36** **Protocol library types would reach the terminal and the UI, so replacing that library means rewriting the client** — A protocol library may be named only inside Quickshell.Transport, and its package must carry PrivateAssets=all so it reaches nothing above.
- ✅ **QS39** **A refused connection reports a library exception, so a user cannot tell a wrong port from a wrong key** — A failure's classification rule is written against a run that produced it, never against documentation, and the run is named in the comment beside it.

## Block B — Keys, agents, and the host you think you reached

- ✅ **QS42** **Nothing checks the host key, so a machine in the middle is indistinguishable from the server** — A changed host key is never a dialog: the decision delegate is not consulted, so no caller can accept one however it answers.

## Block C — Emulation that does not lie about the remote

- ✅ **QS6** **Nothing opens a graphics device, so no pixel this client draws has ever reached a screen** — Adapter selection walks output-window, then default hardware, then WARP, recording each skip; a device loss rebuilds GPU resources and cannot touch terminal state.
- ✅ **QS7** **A frame reaches the screen whenever the compositor chooses, so no bound on input-to-photon delay can be claimed** — The present path is flip-discard with a frame-latency waitable object at maximum latency one, tearing behind its capability check, Scaling.None on resize, and Per-Monitor V2 in the manifest.
- ✅ **QS8** **No glyph is on the GPU, so drawing a character would cost a rasterisation on the frame that needs it** — Glyph coverage is grayscale in a one-channel atlas, and ClearType subpixel coverage is a separate pipeline rather than a flag on this one.
- ✅ **QS9** **Nothing turns a grid of cells into a draw call, so text on this client's screen is still hypothetical** — Coverage is blended in linear light and never in sRGB: half a pixel covered is half the light, not half the stored byte.
- ✅ **QS10** **A CJK character, a combining mark and a colour emoji each draw wrongly or not at all** — A colour glyph is painted and not tinted, so a cell's foreground means nothing for one and the shader ignores it.
- ✅ **QS87** **The frame-queue measurement reads two on runs where nothing regressed, so its result is not evidence either way** — A frame-queue reading is only meaningful once DXGI's statistics exist, and its absolute value is a startup phase rather than a depth.
- ✅ **QS11** **Underline, undercurl, strikethrough, the cursor and a selection have no representation on screen** — A blinking cursor is the only thing that may wake an idle window, so turning blinking off must answer with no wake at all.
- ✅ **QS12** **Nothing catches a renderer that draws correctly on one vendor's driver and wrongly on another's** — A reference image changes only by a deliberate run with QUICKSHELL_GOLDEN=write, and never by a test that failed against it.
- ✅ **QS13** **A multi-byte character split across two network reads decodes as two broken ones** — The width table is generated from the Unicode Character Database and records its version, so a release is a rebuild and a diff.
- ✅ **QS14** **A byte carrying an escape sequence is indistinguishable from text, so a host cannot move the cursor** — The parser holds no terminal state and the table answers for every byte in every state, checked where it is built.
- ✅ **QS15** **Nothing holds what the remote host has printed, so a line that scrolls off the top is gone** — Scrolling the screen moves an origin and fills one row; only a scrolling region inside the screen moves rows at all.
- ✅ **QS96** **A third rasteriser shifts one antialiased pixel by six levels, which is three times the measured tolerance** — A golden scene's tolerance belongs to the scene: glyph coverage is DirectWrite's and varies by machine, the shader's arithmetic does not.
- ✅ **QS97** **The frame queue reaches three under a virtualised presentation path, where the bound was measured at two** — A frame queue's absolute depth belongs to the display pipeline; only its growth belongs to the renderer, so only growth is asserted.
- ✅ **QS16** **The cursor cannot be moved, a line cannot be erased and no character carries a colour** — A cell stores the colour the host named, default or index, and a frame resolves it - so a theme change repaints the scrollback too.
- ✅ **QS17** **A full-width line wraps a column early and a scrolled region redraws the wrong rows** — Only a printable character takes an owed wrap; every control cancels it, which is what keeps a full-width line from growing a blank one.
- ✅ **QS19** **A remote host can write the local clipboard and read back a string it planted in the title** — No reply may contain a byte the host supplied: replies are composed from an enum and integers, and the method that sends them takes no text at all.
- ✅ **QS35** **Text is rasterised in grayscale, so it looks thinner here than in every other Windows application** — The grid uses no output-merger blend: every cell paints its own opaque background and the pixel shader mixes the coverage itself.
- ✅ **QS109** **The golden suite's text tolerance does not hold on the CI runner, so every commit's build is red for the same pixels** — A golden text scene is judged by mean difference; a maximum is a fact about one pixel on one machine and does not survive a change of machine.

## Block D — The tree a user organises work in

## Block E — SCP and SFTP as a thing a person operates

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

- ✅ **QS4** **No decision records which window host can present a swapchain without adding frames of latency** — The window host is WPF with a child HWND per pane: measured over three passes it is the only one of the three whose click-to-pixel floor is a single refresh interval.

## Block H — The reason to leave the incumbent

- ✅ **QS82** **The tree targets net8.0, where WPF has no Fluent theme, so the chrome the sibling clients share cannot be built here** — The tree targets net10.0-windows: WPF's Fluent theme and ThemeMode arrived in .NET 9, and that is what lets the chrome be borrowed from the sibling clients instead of rewritten.
- ✅ **QS85** **The commit script stages the whole tree, so a second session's half-written work lands inside another task's commit** — A commit stages the paths its task's claim declared, passed to run-commit.cmd after --; sweeping the whole tree stays available and is listed before it happens.

## Block I — An error a user can act on

## Block J — Leaving MobaXterm, proven by the switch

## Block K — The build and the harness — what a green run is evidence of

- ✅ **QS88** **dotnet test reports zero tests and exits non-zero on a tree whose suite passes when the assembly is run directly** — The suite's verdict is run-tests.cmd's exit code, and every configuration builds into exactly one tree under bin.
- ✅ **QS95** **The render suite takes the screen with topmost windows, and anything the operator does corrupts it** — A guest run is a second environment and not a second opinion: where it disagrees, that is a finding rather than a harness fault.
- ✅ **QS100** **The ban on raw control bytes covers tests and not src, so the same byte went into shipped source unseen** — A raw control byte is banned in every C# file this repository has, not only in the half where the first one was noticed.
