# prototypes

Measurement rigs, not product code. They are deliberately **outside `Quickshell.sln`**: CI builds
the client, and a WinUI 3 project in that solution would put a Windows App SDK restore on every
push for a rig that answers one question and is then evidence.

## `HostProbe.*` — which window host presents without adding frames

The run behind [docs/measurements/host-probe.md](../docs/measurements/host-probe.md) and the
decision in [docs/DECISIONS.md](../docs/DECISIONS.md).

```
dotnet build prototypes/HostProbe.WpfHwnd
prototypes/HostProbe.WpfHwnd/bin/Debug/net8.0-windows/HostProbe.WpfHwnd.exe <output-dir>
prototypes/HostProbe.WpfD3DImage/bin/Debug/net8.0-windows/HostProbe.WpfD3DImage.exe <output-dir>
prototypes/HostProbe.WinUI/bin/Debug/net8.0-windows10.0.19041.0/win-x64/HostProbe.WinUI.exe <output-dir>
```

Each writes `<host>.json` plus two screenshots and takes about a minute.

**It drives the mouse.** The rig injects real clicks at the pane's centre, so it makes its window
topmost first and refuses to click at all until it has seen the pane's own idle colour at that
point — without that check it would click thirty times on whatever window is actually there,
which is what its first version did. Do not use the machine while it runs.

- `HostProbe.Core` — the pane state, the D3D11 swapchain renderer, the DXGI desktop-duplication
  probe that reads the composited pixel, and the driver that runs the same run on every host.
- `HostProbe.WpfHwnd`, `HostProbe.WpfD3DImage`, `HostProbe.WinUI` — one host each, differing in
  how a frame reaches the screen and in nothing else.

`runs/` holds what the passes produced. It is evidence for a decision that has been taken, so it
is committed and it is not re-run by anything.
