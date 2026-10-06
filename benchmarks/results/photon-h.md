# Input to photon — figure 1

Figure 1 of the performance budget is **a keystroke echoed by a local shell on the glass in under
8.3 ms**. Measured on the reference machine (Core i7-14700, RTX 4060, 3840 x 2160 at 60 Hz,
Windows 11 Pro 26200) on **2026-10-06**, by `tools/Quickshell.Photon` — re-run it with:

```
dotnet build tools\Quickshell.Photon -c Release
tools\Quickshell.Photon\bin\Release\net10.0-windows\Quickshell.Photon.exe ^
    --seconds 20 --passes 3 --out benchmarks\results\photon-h.md
```

This is the **instrumented path** the budget allows, not a high-speed capture, and the run is all
it used. Each echo is scheduled on the clock and stamped with the instant it was due, so an echo
that falls due while the loop is parked on the swapchain is charged for the wait. The photon end
is the vblank DXGI's frame statistics report the present carrying it was shown at
(`SyncQPCTime`), on the same QPC clock. The panel's own response time is not in it.

Every frame is the figure-3 grid: 200 x 50 cells drawn from the atlas, filled from the `cat-log`
stream, through `GridPainter` and one instanced draw. That is the work QS7's two controls lacked:
one clear per frame cannot get ahead of the display, so there was nothing to queue.

## The verdict

**Not met, and not meetable on this desk.** An echo at a prompt reaches the glass in a median of
**33 ms**, two refresh intervals at 60 Hz, and no run timed one under 19 ms. The display makes
8.3 ms impossible here. The finding that will outlast this panel is the **two intervals**: at
120 Hz the same shape would be about 17 ms, still twice the budget. QS201 follows it up.

**The flags are worth about a frame under load, and nothing at a prompt.** That is the question
QS7 left open, and now it has a measured answer.

## The run

Three passes of 20 s per arm, each pass alternating the arm order, the same rhythm of echoes for
every arm in a pass. Milliseconds from the echo being due to its vblank.

| workload | arm | echoes | unresolved | min | p10 | median | p90 | max |
|---|---|---|---|---|---|---|---|---|
| typing | **client** (latency 1, waited after painting, 2 buffers) | 400 | 0 | 21.2 | 25.7 | **33.3** | 40.6 | 71.1 |
| typing | wait-first (latency 1, waited before reading, 2 buffers) | 400 | 0 | 21.5 | 25.4 | 32.5 | 39.3 | 55.3 |
| typing | unbought (latency 3, Present blocks, 2 buffers) | 398 | 2 | 19.2 | 24.3 | 32.8 | 40.1 | 54.2 |
| typing | unbought-3 (latency 3, Present blocks, 3 buffers) | 400 | 0 | 19.2 | 25.1 | 31.8 | 39.8 | 54.8 |
| busy | **client** (latency 1, waited after painting, 2 buffers) | 577 | 18 | 20.3 | 32.7 | **42.2** | 53.2 | 96.5 |
| busy | wait-first (latency 1, waited before reading, 2 buffers) | 596 | 0 | 29.8 | 34.5 | 41.7 | 48.6 | 66.6 |
| busy | unbought (latency 3, Present blocks, 2 buffers) | 573 | 23 | 23.8 | 33.6 | 55.6 | 76.8 | 91.0 |
| busy | unbought-3 (latency 3, Present blocks, 3 buffers) | 593 | 2 | 27.5 | 32.3 | 44.8 | 59.5 | 64.3 |

Per pass, median:

| workload | client | wait-first | unbought | unbought-3 |
|---|---|---|---|---|
| typing | 32.1, 33.3, 35.0 | 31.3, 32.7, 33.4 | 30.3, 33.4, 33.4 | 30.3, 31.4, 34.7 |
| busy | 46.4, 41.9, 39.5 | 40.2, 42.5, 42.3 | 58.2, 59.2, 53.8 | 51.6, 46.9, 38.6 |

No frame in any run was occluded. *Unresolved* is an echo whose present DXGI had already gone past
when it was next asked, so the vblank it was shown at is unknown; it is left out rather than given
the next vblank, which would flatter it by up to a frame.

## Reading it

- **At a prompt the queue is empty, so no arm can differ.** Typing is one frame per echo with
  nothing else drawn. All four arms sit at 32–33 ms and their passes overlap. The flags bound a
  queue, and here there is none.
- **Under load the flags are what keep the echo one frame behind and not two.** With output
  streaming, the client's two buffers at latency one hold a median of 42 ms. The same two buffers
  without the flags hold 56 ms, and every pass of that arm is slower than every pass of the client.
  That is the frame the flags were bought for, and this is the first run that shows it.
- **A third buffer without the flags is noisier, not faster.** Its passes run from 39 to 52 ms. A
  deeper chain lets the queue form on some runs and not on others, which is the variance a latency
  of one removes.
- **Moving the wait before the read is not worth a change on this evidence.** `TerminalView` paints
  and then waits, so an echo landing during the wait misses that frame. Waiting first gives the
  same median (41.7 against 42.2) and a shorter tail (p90 48.6 against 53.2), and the difference
  is inside what the passes show for each arm.
- **Two intervals even at a prompt, and the floor is never under one.** An echo due at a random
  moment waits half a frame on average for the next vblank, and the present takes one more frame
  on top of that. That pattern fits a windowed flip-model chain the compositor presents a frame
  late. Whether that is so, and what removes it, is QS201's question.

## What this run does not settle, said plainly

- **The panel.** The vblank is DXGI's account of the glass. A capture of key and screen would add
  the panel's response and is what the budget calls confirmation.
- **The keystroke half.** The echo is scheduled, not typed: the path from the key going down,
  through the shell, to the echoed byte arriving is not in this figure. For a local shell it is the
  smaller half, and it is not zero.
- **The window host.** The tool's window is a plain popup, not the client's WPF child HWND.
  `docs/measurements/host-probe.md` measured that host at 13.8 ms minimum click to pixel with
  desktop duplication, a different instrument whose timestamp is the composed desktop frame and
  not the vblank. The two are not one number.
- **120 Hz.** Nothing here has run on a panel faster than 60 Hz.
