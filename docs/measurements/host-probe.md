# Window host probe — the run behind the host decision

Three hosts, one rig, the same run three times each. This file is the evidence; the decision it
supports is one line in [DECISIONS.md](../DECISIONS.md), and the rig is in
[prototypes/](../../prototypes/).

## What was measured, and with what

Each host shows a pane that a D3D11 device fills with a colour and presents continuously. The
pane cycles a dark idle colour so no two frames are identical, and turns near-white for 250 ms
when it is clicked.

**Presented frame rate** is the pane's own count of completed presents over a five-second
sample, taken twice: once clean and once with a dropdown open over the pane.

**Click to pixel** is the interval from the QPC instant a click is injected with `SendInput` to
the `LastPresentTime` of the desktop frame in which the sampled pixel first reads near-white.
That timestamp comes from **DXGI output duplication**, which is the whole reason the number
means anything: it is the composited desktop, so whatever the compositor adds after the
application presents is inside the interval, and it is the same instrument for all three hosts.

Two instruments were tried and discarded, both worth recording because each produced a
confident wrong answer first:

- **GDI (`GetPixel` on the screen DC)** does not see a flip-model swapchain at all. It reported
  a pane that was never drawn, on a host that was presenting sixty frames a second.
- **The application's own present time** would have flattered exactly the host this run exists
  to interrogate, since the D3DImage path's cost is what WPF's compositor adds *after* the
  application is finished.

Thirty trials per host per pass, three passes, so ninety samples per host.

## The machine

The reference machine in [PERFORMANCE.md](../PERFORMANCE.md), whose display runs at **60 Hz**.
One refresh interval is 16.7 ms, and that bounds everything below: no host can answer a click
faster than the display can show it, so these numbers compare hosts against each other and
settle nothing about the 8.3 ms input-to-photon budget.

## What came back

Frame rate, every host, every pass: **59.6–60.2 fps clean, and the same with a dropdown open**.
The rate separates nothing at 60 Hz.

Click to pixel, in milliseconds, pooled over ninety samples per host:

| Host | min | p10 | p25 | median |
|---|---|---|---|---|
| **WPF child HWND** | **13.8** | **16.0** | **18.5** | 33.2 |
| WPF `D3DImage` | 20.2 | 32.9 | 35.9 | 50.3 |
| WinUI 3 `SwapChainPanel` | 27.9 | 29.9 | 31.9 | 46.4 |

Per pass, p10 — which is where the reading is, and the next section says why:

| Host | pass 1 | pass 2 | pass 3 |
|---|---|---|---|
| WPF child HWND | 15.7 | 15.6 | 52.2 |
| WPF `D3DImage` | 34.9 | 32.9 | 32.4 |
| WinUI 3 `SwapChainPanel` | 28.4 | 47.0 | 30.3 |

## Why the low percentiles and not the median

**The medians do not survive the noise.** Pass by pass the child-HWND median reads 19.0, 19.7,
63.1 and the WinUI median reads 31.6, 49.4, 46.9: the spread between passes of one host is
larger than the distance between hosts, and a winner picked from any single pass would have
been picked by whatever else the machine was doing. Pass 3 is visibly a degraded environment.

Latency noise is one-sided — a busy machine delays a frame and never hurries one — so the floor
of a distribution estimates what a host can do and the median estimates what the machine was
doing that minute. Read that way the passes agree, and the agreement is the finding:

- The child-HWND host reaches **one refresh interval** (13.8–18.5 ms) in two passes of three.
- Neither other host reaches a single frame in **any** pass: `D3DImage` never reads below
  32.4 ms at p10 and `SwapChainPanel` never below 28.4 ms.

## The airspace test

The chosen host was required to show a dropdown and a modal correctly overlapping a pane that is
still presenting. Both were captured: [dropdown](../../prototypes/runs/wpf-child-hwnd-dropdown.png),
[modal](../../prototypes/runs/wpf-child-hwnd-modal.png). WPF's popups are HWND-backed, which is
the exception that makes the option credible, and the pictures are it working.

WinUI 3's [modal](../../prototypes/runs/winui-swapchainpanel-modal.png) also composes over a
running pane. **Its dropdown was not captured** — the rig opened it and the shot came back with
the popup closed — so no picture here supports the no-airspace claim for that host, only the
modal one. It closed on latency and not on airspace, so this gap does not change the decision,
but the run did not prove what it set out to prove for that host and this line is instead of
pretending it did.

## What this run does not settle

- **Whether `D3DImage` caps the achievable frame rate.** The expectation was that it would. At
  60 Hz it sustained the display rate, so the expectation is neither confirmed nor refuted here;
  the display cannot show more than 60, and a 120 Hz panel is what would answer it. `D3DImage`
  was closed by latency, not by rate.
- **Absolute input-to-photon.** Bounded by a 60 Hz panel, as above.
- **Anything at all about several panes.** One pane, one swapchain, every run.
