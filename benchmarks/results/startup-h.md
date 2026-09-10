# Cold start — figure 5

Figure 5 of the performance budget is **process start to an interactive local shell in under
400 ms** — a prompt that accepts a keystroke, not a window that has appeared. Measured on the
reference machine (Core i7-14700, RTX 4060 driver 32.0.16.1074, 64 GB, Windows 11 Pro 26200) on
**2026-09-10**, by `tools/Quickshell.Startup` — re-run it with:

```
dotnet build Quickshell.sln
tools\Quickshell.Startup\bin\Debug\net10.0-windows\Quickshell.Startup.exe ^
    --launch <client>\quickshell.exe --runs 11 --label "<what was built>" --out benchmarks\results\startup-h.md
```

The client times itself. Each start is given `--startup-report`, and the client writes when each
milestone happened, measured from the moment Windows created its process — so the harness's own
cost of starting a process is not in the figure, and no two clocks are compared. The last
milestone, `interactive`, is the first frame on the glass that carries anything the shell wrote:
cmd's banner and its prompt.

## The verdict

**Not met.** The fastest build reaches the prompt in **647 ms** warm, 247 ms over the budget.
Publishing choices move it by 40 ms; what is left is where the time goes below.

| build | warm median | first start after the build |
|---|---|---|
| Release publish, self-contained, ReadyToRun | **647 ms** | 1,313 ms |
| Release build, framework-dependent | 686 ms | 4,399 ms |
| Debug build, framework-dependent | 698 ms | 1,033 ms |

## Where the time goes

Warm medians of the self-contained ReadyToRun publish, each step since the one before it:

| milestone | at | step | what the step is |
|---|---|---|---|
| main | 63 ms | 63 ms | the runtime loading, before any code of this client |
| application | 99 ms | 36 ms | WPF's `Application` |
| constructed | 329 ms | 230 ms | the main window's constructor |
| shown | 427 ms | 98 ms | `Show()` |
| shell | 498 ms | 71 ms | the settings read, the tab, the pseudo-console and cmd started |
| device | 617 ms | 119 ms | the first layout, the D3D11 device, the atlas, the swapchain |
| frame | 647 ms | 30 ms | the first frame presented, already carrying cmd's output |

**The window's constructor is the largest step, and it is not the theme.** Built again with the
`ThemeMode` assignment taken out of it, the Debug build finished constructing its window at 317 ms
against 335 ms — 18 ms, and the theme is applied a moment later anyway. The rest is WPF's own
first-window cost: its assemblies, the static state of the first `UIElement`, the controls'
templates. ReadyToRun shortens it by a few milliseconds only, which says it is not JIT.

**The device is on the critical path and cannot be moved off it**, as the design said: 119 ms from
the window being shown to a swapchain existing, all of it after the first layout.

## What was not measured, said plainly

- **A cold file cache.** Arranging one means flushing the system's standby list, which needs
  tooling and rights a harness should not take for itself. The "first start after the build"
  column is the first run of files that had just been written — not a cold cache, and inflated by
  the on-access scan of new executables: the Release build's 4.4-second first start is that scan,
  not this client.
- **The incumbent.** The comparison is the claim this figure exists for, and it was not made: a
  MobaXterm with the user's own session in it was open on this desk, and starting a second copy
  beside it, or ending one, is not something a measurement may do to somebody's work.
- **Trimming.** The SDK refuses it for WPF (`NETSDK1168`), so there is no trimmed build to measure;
  the design's "measured rather than assumed" is answered by the refusal.
