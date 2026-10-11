# Idle cost — figure 4

Figure 4 of the performance budget is **zero draw calls and no measurable core occupancy**, and
"small" is not a pass there. Measured on the reference machine (Core i7-14700, RTX 4060, Windows 11
Pro 26200) on **2026-08-31**, ten minutes per subject after a thirty-second settle, by
`tools/Quickshell.Idle` — re-run it with:

```
dotnet build Quickshell.sln
tools\Quickshell.Idle\bin\Debug\net10.0-windows\Quickshell.Idle.exe ^
    --launch src\Quickshell.App\bin\Debug\net10.0-windows\win-x64\quickshell.exe ^
    --for 600 --settle 30 --label quickshell --out benchmarks\results\idle-h.md
```

## The comparison

| | quickshell | MobaXterm (both its processes) |
|---|---|---|
| core time over ~600 s | **0 ms** | 3,625 ms |
| occupancy of one core | **0.0000 %** | 0.6037 % |
| private memory | 79.4 MB | **62.4 MB** |
| threads | 9 | 9 |
| handles | **630** | 912 |
| raised the system timer | no | no |

**quickshell consumed no measurable core time at all** over ten minutes — not a small number, the
number the budget asks for. The incumbent spent 3.6 seconds of a core doing nothing, which is the
figure a laptop user experiences as battery.

**Neither raised the system timer**, which is the half nobody notices: a process that asks Windows
for a finer system timer and never gives it back spends battery inside every other application on
the machine. Note the baseline on this desk is already 1.000 ms rather than the 15.6 ms default —
something else on it has raised it — so what this run can detect is a subject raising it *further*.
On a quiet desk the baseline would be coarser and the test sharper.

**MobaXterm uses less private memory than quickshell does**, and that is reported rather than
buried. It is the incumbent's own number on an installation with an X server running, against a
quickshell window that has no session in it at all — so this row is not a defeat, it is a warning
that figure 6 has no slack to spend. QS137 is where it is followed up.

## The draw-call half

Measured separately, in the layer that owns it, against a real D3D11 device and a real visible
window: `PresentSurfaceTests.AnIdleRenderLoopPresentsNothingToTheGpu` drives a loop written the way
a renderer's is — ask `RedrawGate`, present only when told — through 300 idle wake-ups with the host
silent, and asserts DXGI's own `PresentCount` does not advance. **Zero presents, and the gate
skipped all 300.** `RedrawGateTests` already covers the decision; this covers the loop around it,
which is what would present anyway.

## What this run does not measure, said plainly

The budget's wording is "a window nobody is typing into", and the design asks for that window to be
**connected, with a shell at a prompt**. quickshell cannot be in that state yet: nothing above the
seam constructs a transport, and no pane is attached to a render loop. So the 0.0000 % above is a
true measurement of the shell this client currently is — a WPF window, the crash guard, the
settings read — and it is **not** the connected-session figure the budget will eventually be read
against.

Specifically unmeasured, and each one filed rather than assumed:

- **A connected session's idle cost**, including keepalive traffic waking the process. QS137.
- **Several idle panes costing what one costs** — the occlusion and damage work verified rather than
  assumed. There is one pane and it holds nothing. QS137.
- **GPU utilisation of a live render loop.** The draw-call count is zero by the test above, but no
  loop runs in the application, so there is no occupancy figure for one.
- **A cursor blink's bounded cost.** `RedrawGate` refuses to wake for a hidden cursor and blinks for
  a visible one; what a blink costs per hour has not been put on a clock.

## A live client, measured — 2026-10-10 (QS137)

The same desk and the same tool, which now also reads each GPU engine's busy time for the subject
from Windows' own `GPU Engine` counters. Ten minutes per subject after a thirty-second settle, the
Debug build at d972ff7 with QS139 in it, each launched with `QUICKSHELL_DATA` on a scratch folder so
nothing in the operator's profile was read. The connected session is the docker fixture's sshd,
opened with `--session` and left at its prompt with the file browser's SFTP channel open beside it.

| subject | core time / 600 s | occupancy of one core | GPU engine | private memory |
|---|---|---|---|---|
| one local pane | 3,719 ms | 0.62 % | 0.17 % | 172.5 MB |
| one local pane, `cursorBlink: false` | 3,438 ms | 0.57 % | 0.20 % | 175.0 MB |
| four local panes | 10,484 ms | 1.74 % | 0.31 % | 218.3 MB |
| a connected session, a local tab behind it | 5,797 ms | 0.96 % | 0.33 % | 211.4 MB |

None raised the system timer.

**Figure 4 fails on every live window.** The empty window above measured zero; a window with one
pane in it does not, and **the cursor blink is not why**: with blinking off the pane costs 0.57 %
of a core where it cost 0.62 with it on, and still keeps a GPU engine busy. Something other than
the blink wakes a pane and draws, and it is filed as QS255.

**Several panes do not cost what one costs.** Four cost 2.8 times one. Whatever wakes a pane wakes
each of them.

**Figure 6 fails before a session is opened.** One local pane holds 172.5 MB against a budget of
120 MB for a *connected* session, and the connected session 211.4 MB. Filed as QS256.

Keepalive's own share could not be separated here: the connected row also carries a local tab and
the browser's SFTP channel, so it reads as one pane's cost plus about a third.

## What woke the pane — 2026-10-10 (QS255)

The client now writes its render loop's counters with `--frame-report <file>`, and the idle tool reads
them over its interval with `--frames <file>`. One local pane, `cursorBlink: false`, three minutes
after a fifteen-second settle, the Debug build, on the same busy desk:

| | before | spin fixed | and the blink setting honoured |
|---|---|---|---|
| loop woken by the blink timer | **196,878** | 384 | **0** |
| loop woken by damage | 0 | 0 | 0 |
| frames drawn | 266 | 340 | **0** |
| bytes from the host | 0 | 0 | 0 |
| GPU engine time | 302 ms | 559 ms | **0 ms** |
| occupancy of one core | 0.62 % | 0.93 % | 0.29 % |

**Two defects, and neither was the host.** The loop woke on the blink timer about 1,100 times a
second: a phase change under a millisecond away became `Task.Delay(0)`, which returns at once, so the
loop spun until the boundary passed. And the blink was running at all because `cursorBlink: false`
never reached a pane opened at start: the window applies its settings before a pane's view exists,
and the shape and the blink were given only to a view. Both are fixed; the blink now waits in whole
milliseconds, and a pane holds its cursor settings until its view opens.

**What is left is not the loop.** With nothing woken and nothing drawn the pane still spends 0.29 % of
a core, which figure 4 counts as measurable. And with the blink on, each frame costs about 5 ms of core
time, so a blinking cursor is 0.6 % of a core by itself.

Ten minutes each, on the same busy desk, one local pane with `cursorBlink: false`:

| | occupancy of one core | frames | GPU engine |
|---|---|---|---|
| with `--frame-report`, rewriting its file every 5 s | 0.41 % | 0 | 0 ms |
| without it | **0.16 %** | not counted | 0 ms |

**The report was most of what it measured.** Writing its file every five seconds whether or not a
counter had moved cost the idle client 0.25 % of a core; it now writes only when one moves. What
is left with the loop silent is 0.16 %, on a desk with other projects running. Figure 4's bound is
0.1 %, so the reading that settles it is ten quiet minutes on the reference desk.

## Where one pane's memory goes — 2026-10-10 (QS256)

The release publish of this tree, one local pane, settled forty seconds: **164.9 MB private**. A full
dump read with `dotnet-dump analyze` (`eeheap -gc`, `dumpheap -stat`) for the managed half, and a
probe that opens the render stack alone, step by step, for the native half:

| owner | private MB | read from |
|---|---|---|
| runtime and the WPF window, no pane | ~79 | QS76's empty window, above |
| the D3D11 device, before anything is drawn | **~47** | the probe: 8.3 MB of runtime, then 55.0 with the device open |
| DirectWrite and the font, the atlas's first page and ASCII, the renderer and its shaders | ~10 | the probe: 55.0 to 65.4 |
| `Cell[]`, five arrays: both screens and a 10,000-line scrollback allocated whole | **28.8** | `dumpheap -stat`, the largest managed type by two orders of magnitude |
| the rest of the managed heap | ~10 | `eeheap -gc`: 39.0 MB committed in all |
| **sum** | **~165** | |

**Two owners are most of it.** The device is the driver's, allocated by opening it and the same in any
process that opens one on this adapter; nothing in this client draws it down. The cells are this
client's: the ring is sized for its whole capacity when the pane opens, so a shell that has printed one
prompt holds ten thousand lines of empty cells.

**Figure 6 is out of reach as stated.** The window and the device alone are about 126 MB, against a
budget of 120 for a connected session. Allocating scrollback as it fills takes about 29 MB off a new
pane; the rest of the gap is the figure's to argue.

**After the cut.** The ring now starts at the screen and 256 lines and doubles as it fills, up to the
configured depth. The same publish, one pane, settled forty seconds, three starts: **139.6 to 140.0 MB**,
from 164.9: 25 MB off every new pane, the rest of the 29 being the screens themselves. The window and
the device, 126 MB of it, are what stand between this and figure 6.

## A blink drawn as one cell — 2026-10-11 (QS260)

A blink now redraws the cursor's cell alone: the chain is flip-sequential, so a buffer keeps its
picture, and once both buffers hold the current one a blink patches one instance, draws the grid
clipped to that cell and presents it as one dirty rectangle. The frame report counts these as
`cursorFrames`.

Measured in the **VMware guest**, not on the reference desk: one local pane, Release publish,
`QUICKSHELL_DATA` on a scratch folder, three minutes after a fifteen-second settle, by
`tools/Quickshell.Idle --attach --frames`.

| | core time / 181 s | frames | of them one cell |
|---|---|---|---|
| HEAD (84774fa), blink on | 656 ms | 340 | not counted |
| this change, blink on | 672 ms | 349 | not counted |
| this change, blink on, again | 562 ms | 349 | **349** |
| this change, `cursorBlink: false` | 344 ms | 0 | 0 |

**Every blink took the one-cell path**, and the guest cannot tell it apart from a whole frame on core
time: with the blink-off floor taken away, a blink costs 0.6 to 0.9 ms of core either way, and two
runs of the same build differ by more than the two builds do. In the guest a frame's core cost is
the wake and the present, not the grid. The guest reports no GPU engine time at all, so the half
this change is aimed at most directly, GPU and compositor work per blink, is not visible there.

What the reference desk has to settle, and has not: core and GPU time per blink against the 4 ms
QS255 measured there, and `tools/Quickshell.Photon`, because flip-sequential is also the path an
echo takes.
