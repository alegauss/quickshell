# Throughput, remote beside local

So that a slow link and a slow client can be told apart. Both are measured in
`tests/Quickshell.Transport.Tests/SshNetTransportTests.cs`, through the channel a session reads,
over thirty-two megabytes of the same file shape: 78 printable characters and a line break.

| | figure | allocated | where | when |
|---|---:|---:|---|---|
| remote: `cat` through the SSH fixture (QS37) | **124.8 MB/s** | 233 KB per MB | reference machine, Docker sshd on loopback | 2026-08-30 |
| local: `type` through the pseudo-console (QS110) | **1.0 MB/s** | 8 KB per MB | reference machine (XPS) | 2026-10-06 |

The remote figure needs the SSH fixture (`prototypes/SshProbe/fixture/up.sh`) and its test skips
without it. The local one runs everywhere, `TheLocalPseudoConsoleIsMeasuredBesideTheRemote`.

## Reading it

- **The two measure different producers, and the gap is the console host.** A remote `cat`'s bytes
  arrive as the program wrote them. A local `type`'s go through the console host behind the
  pseudo-console, which renders what cmd prints and sends that rendering, a line at a time, and
  drops lines that scroll past between its frames. So the local figure is the source's size over
  the time until a marker printed after it comes back, which is what "how fast does a local file
  print" means. Reads are counted only for the allocation.
- **A slow local session is the console host, not this client.** At a megabyte a second the
  channel and the pipeline are nowhere near their limit; the remote path through the same
  pipeline carries over a hundred.
- **The allocation is low on both,** and the local path's 8 KB per MB is the test's own reading
  loop and the pipe's.

## What the measuring found

Two things QS37 wrote down as properties of the local channel were not:

- **A cancelled read does not silence the channel.** Reads cancelled while the pipe is idle, and
  while output flows, are followed by reads that carry on (`ReadsCancelledWhileOutputFlowsLeave
  TheChannelReading`).
- **What did silence it was a write with no reader.** The pipes carry no buffer, so a write
  completes only when the console host reads it, and the host does not read input while blocked
  writing a screen nobody drains. QS37's measurement wrote its command before it started reading.
  `ConPtyChannel.WriteAsync` now says so.
