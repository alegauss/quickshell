# The corpus

Six byte streams, **captured from live sessions** on a real Debian bookworm and stored with no
interpretation: what is in `streams/` is what a terminal would have received, gzipped and nothing
else. Synthetic input flatters a parser, which is why none of this was written for the occasion.

Every session ran on a real pty at **200x50**, `TERM=xterm-256color`, driven by
[capture/capture.py](capture/capture.py) — `script` cannot set a window size, and a corpus taken at
24x80 would not contain the wrapping and full-screen redraws the parser will be judged on.

Re-capture with:

```
docker build -t qs-corpus benchmarks/corpus/capture
docker run --rm --privileged -v "<repo>/benchmarks/corpus/streams:/corpus" qs-corpus
```

`--privileged` is for `dmesg` alone: without it there is no kernel ring buffer to read.

| stream | raw | gz | sha256 (gz, first 16) | what was run |
|---|---|---|---|---|
| `htop` | 19,611 | 3,057 | `4a0af81d7c2f8f74` | `htop -d 2` for 14 s — a curses application repainting on its own clock |
| `vim-scroll` | 243,486 | 21,969 | `588991fe10d16e63` | `vim` with syntax on over `/usr/include/eigen3/Eigen/src/misc/lapacke.h` (1,058,369 bytes of real C), 60 page-downs |
| `ls-color-r` | 830,242 | 168,845 | `d5ada34d17b0aa1a` | `ls --color=always -R /usr` — short lines, dense SGR changes |
| `cat-log` | 33,554,432 | 5,099,287 | `4fa114d732442757` | `cat` of 180,639,768 bytes of real headers and docs; **the first 32 MB of that capture is what is committed** |
| `tmux-resize` | 15,811 | 2,423 | `bafede4297278624` | a `tmux` session split twice, then the pty really resized 200x50 → 120x30 with a real `SIGWINCH` nine seconds in |
| `dmesg` | 184,563 | 24,313 | `084340f0b46d1122` | the kernel ring buffer — long lines that wrap, which is what breaks naive line handling |

## The one truncation, said plainly

`cat-log` was captured whole at **184,761,307 bytes** (sha256 `5cc7b8fe62f20ae1…`) and the
committed stream is its **first 32 MB**. A prefix of a capture is still a capture — nothing was
generated — and 32 MB is far more than any measurement here needs, while 185 MB in git is a cost
the repository would pay on every clone forever.

The design asked for a hundred-megabyte log and the machine produced a hundred and eighty. What is
committed is smaller than the request on purpose, and the full capture is reproducible from the
command above.

## Two numbers that already disagree, usefully

`cat-log` scans at about **1,500 MB/s** through the replay harness and about **3,000 MB/s** under
BenchmarkDotNet, on the same bytes and the same machine. Both are honest: BenchmarkDotNet runs one
tight loop over the whole array, and the harness feeds 64 KB spans the way a transport actually
delivers them. Chunking costs half the throughput before a parser has done anything at all, and
that is the sort of thing a corpus is for.
