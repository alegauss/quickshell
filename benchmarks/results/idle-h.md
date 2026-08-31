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
