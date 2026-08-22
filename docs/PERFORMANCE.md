# Performance budget

Six figures. They are design constraints, not descriptions: each one was written before the
code it binds, and each is changed only by a commit that argues for the change.

**A number without a machine is a mood**, so every figure below is a claim about the
reference machine named here, measured the way the figure says. A claim produced by any
other method does not count — not a stopwatch, not a screen recording, not a feel.

**A figure quoted without the run that produced it is not a measurement.** Commit messages,
release notes and the changelog cite the run: the command, the machine, the date. This
document is falsified the moment one of its numbers appears somewhere without one.

## The reference machine

| | |
|---|---|
| CPU | Intel Core i7-14700, 20 cores / 28 threads, 2.1 GHz base |
| GPU | NVIDIA GeForce RTX 4060 (driver 32.0.16.1074) |
| Display | 3840 x 2160 at **60 Hz** |
| Memory | 64 GB |
| Storage | NVMe SSD (Kingston SNV2S1000G) |
| OS | Windows 11 Pro 26200, x64 |

A second machine may be measured, and then the machine is named beside the number. It never
replaces this one silently: two machines quoted as if they were one is how a regression
becomes a rounding difference.

## The six figures

### 1. Input to photon — under 8.3 ms

A keystroke echoed by a **local** shell, from the key going down to the glyph being on the
glass. 8.3 ms is one refresh interval at 120 Hz.

*Measured by* a high-speed capture of key and screen in one frame, or an equivalent
instrumented path that timestamps the input event and the present. Never by feel.

*Known limit of this machine:* the reference display runs at 60 Hz, so the photon half of
this figure cannot be observed at 120 Hz granularity here. Until a 120 Hz panel is on the
desk, the instrumented path is what settles this number and the capture is what would
confirm it — a run that used only the instrumented path says so.

### 2. Sustained parse throughput — at least 400 MB/s

A stream of mixed text and escape sequences, parsed end to end.

*Measured by* a headless harness with **no renderer attached**, on the reference machine,
over a stream long enough that startup and file I/O are noise. Parsing must never be the
reason output is slow, so this figure is deliberately measured where nothing else can be
blamed for it.

### 3. Steady-state frame cost — under 2 ms

One filled 200x50 grid, redrawn continuously.

*Measured by* GPU and CPU frame time on the reference machine, in a steady state and not on
the first frame. Under 2 ms leaves the frame budget almost entirely unspent for one pane,
which is what keeps several panes affordable.

### 4. Idle cost — zero draw calls, no measurable core occupancy

A window nobody is typing into.

*Measured by* a frame-capture tool reporting **zero** draw calls submitted over an idle
interval, and a sampling profiler or the OS scheduler reporting no measurable occupancy on
any core. This is the figure the incumbent loses on and the one a laptop user actually
feels, so "small" is not a pass here: the number is zero.

### 5. Cold start — under 400 ms

Process start to an interactive local shell — a prompt that accepts a keystroke, not a
window that has appeared.

*Measured by* wall clock from process creation to the shell's first prompt being drawable,
from a cold file cache wherever the measurement can arrange one, with the warm figure
reported beside it when it cannot.

### 6. Resident memory — under 120 MB

One connected session at default scrollback.

*Measured by* the private working set after the session has settled, not at the instant the
window opens. Block H also asks that this figure stay **flat** across a seventy-two-hour
soak: a number that passes on minute one and drifts is a leak that passed.

## What is deliberately not budgeted here

Transfer throughput for SCP and SFTP, and connection setup time, are bounded by the network
and the remote host far more than by this client. They get their own measurements under
Blocks E and A when there is something to measure; a number stated here would be a claim
about somebody else's link.
