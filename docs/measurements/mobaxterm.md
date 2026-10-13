# quickshell beside MobaXterm

QS81's comparison, every row of the run it came from. Produced by `tools/compare-mobaxterm-vm.ps1`
at commit `6426854`, in the VMware guest, on 2026-10-13 at 18:41 (-03:00).

**The machine.** The guest on the reference desk: Intel Core i7-14700 with 8 of its logical
processors given to the guest, 8 GB, Windows 11 Home 10.0.26300, no GPU of its own. It is not the
reference machine of [PERFORMANCE.md](../PERFORMANCE.md), and these figures are compared with each
other, never with that page's.

**The builds.** quickshell 0.1.0 published as `tools/release.ps1` publishes it (Release,
self-contained, ReadyToRun), started with no arguments, which opens on Home. MobaXterm Home Edition
26.5, portable, `MobaXterm_Personal_26.5.exe`, SHA-256
`f0e77836182fe52e66c68f88fbe13801f935f100623103df1d8be2181294e8e5`, signed by Mobatek, from
<https://mobaxterm.mobatek.net/download-home-edition.html>; started with no arguments, which opens
its home tab and its X server. Mobatek publishes no checksum, so the hash is the one measured.

**The method**, the same for both, from outside the process, since MobaXterm reports nothing of its
own:

- *shown*: process creation to the first visible top-level window of the process started, by wall
  clock, polled every 5 ms;
- *private* and *working set*: the private bytes and the working set of the process and every
  process it started, 15 seconds after the window was shown;
- *idle*: the processor time that process tree spent over the next 60 seconds, untouched, as a
  share of one core;
- *credential prompt*: whether a Windows credential prompt was opened during the run (none was).

Five starts of each, one after another, each ended before the next.

| client | run | shown (ms) | processes | private (MB) | working set (MB) | idle (% of a core) |
|---|---|---|---|---|---|---|
| quickshell | 1 | 1725 | 2 | 63.0 | 140.6 | 0 |
| quickshell | 2 | 1092 | 2 | 62.8 | 140.4 | 0 |
| quickshell | 3 | 903 | 2 | 62.2 | 139.8 | 0 |
| quickshell | 4 | 1019 | 2 | 63.1 | 140.6 | 0 |
| quickshell | 5 | 1095 | 2 | 63.1 | 140.7 | 0 |
| MobaXterm | 1 | 1932 | 2 | 72.8 | 103.8 | 0.911 |
| MobaXterm | 2 | 2809 | 2 | 73.1 | 104.0 | 0.911 |
| MobaXterm | 3 | 2171 | 2 | 72.5 | 103.6 | 0.417 |
| MobaXterm | 4 | 1751 | 2 | 72.5 | 103.6 | 0.807 |
| MobaXterm | 5 | 1772 | 2 | 72.4 | 103.7 | 0.703 |

Medians: quickshell 1092 ms, 63.0 MB private, 140.6 MB working set, 0 % idle; MobaXterm 1932 ms,
72.5 MB private, 103.7 MB working set, 0.807 % idle.

**To run it again**, with the guest set up as `tools/vm-guest.ps1` describes:

```
powershell -File tools\compare-mobaxterm-vm.ps1 -MobaXterm <MobaXterm_Portable_v26.5.zip> -Sha256 f0e77836182fe52e66c68f88fbe13801f935f100623103df1d8be2181294e8e5
```

It brings `TestResults\vm\compare.json` back with every row and prints the medians.

**Not measured here.** Terminal throughput: MobaXterm's terminal cannot be timed from outside without
watching its screen, and a figure taken one way for one client and another way for the other is not
a comparison. quickshell's own is in [throughput.md](throughput.md), on the reference machine. Input
latency likewise: [PERFORMANCE.md](../PERFORMANCE.md) has quickshell's, from an instrumented path
inside the client that MobaXterm has no equivalent of.
