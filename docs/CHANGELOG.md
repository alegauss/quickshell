# Shipped Ledger

## Block A — A session that stays up, or says why it did not

- ✅ **QS5** **Nothing measures whether the chosen protocol library reaches a modern sshd, so the network layer is unpriced risk** — SSH.NET does certificates, jump hosts and two factors against OpenSSH 9.6; the agent and ssh_config are ours to write (design §QS5 recorded in `docs/measurements/ssh-net-probe.md`).

## Block B — Keys, agents, and the host you think you reached

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

## Block D — The tree a user organises work in

## Block E — SCP and SFTP as a thing a person operates

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

- ✅ **QS4** **No decision records which window host can present a swapchain without adding frames of latency** — WPF with a child HWND per pane won on a click-to-pixel floor of 13.8 ms, the only host of three to answer within one refresh interval (design §QS4 recorded in `docs/measurements/host-probe.md`).

## Block H — The reason to leave the incumbent

- ✅ **QS1** **The repository holds no buildable tree, so no claim about this client's speed or size can be measured** — A fresh clone builds and tests four layered projects with nullable, warnings-as-errors and the allocation analyzers on, and a project reference that inverts the layering fails the build.
- ✅ **QS2** **Fast and lean are this client's whole premise, and no number anywhere states what either word means** — docs/PERFORMANCE.md fixes six figures, the method that settles each and the machine they are measured on, so a claim about speed or size is checked against a number instead of a word.
- ✅ **QS82** **The tree targets net8.0, where WPF has no Fluent theme, so the chrome the sibling clients share cannot be built here** — Every project targets net10.0-windows, so WPF's Fluent ThemeMode is reachable here and a guard test fails any retarget that would take it back out.
- ✅ **QS84** **The pipeline pins its actions by tag and nothing watches them, so a stale or advisory-bearing action is found by chance** — Dependabot watches the actions the pipeline pins and opens one grouped pull request a week, so a stale action arrives as a review rather than as a surprise.
- ✅ **QS85** **The commit script stages the whole tree, so a second session's half-written work lands inside another task's commit** — run-commit.cmd stages only the paths a commit declares after --, and says what it is sweeping when none are declared, so a second session's files stay out.
- ✅ **QS3 (the corpus and the whole-stream harness)** **Nothing replays a real terminal workload, so a change that halves throughput lands unnoticed** — Six streams captured from live sessions replay through a harness that reports MB/s and allocation per MB, and the results file is committed.

## Block I — An error a user can act on

## Block J — Leaving MobaXterm, proven by the switch

## Block K — The build and the harness — what a green run is evidence of

- ✅ **QS88** **dotnet test reports zero tests and exits non-zero on a tree whose suite passes when the assembly is run directly** — `.\run-tests.cmd` runs every test assembly, and its exit code is the verdict (design §QS88 superseded: it falsified only on dotnet test) (design §QS88 recorded in `run-tests.cmd`).
