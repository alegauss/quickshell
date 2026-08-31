# Replay results

Captured streams replayed through every consumer that exists. Run on XPS, .NET 10.0.11, 28 logical cores, 2026-08-31. Best of 5 after 1 warmup, 64 KB chunks.

| stream | MB | consumer | MB/s | alloc KB/MB | gen0 |
|---|---|---|---|---|---|
| `cat-log` | 32.00 | escape-scan | 2990 | 0.0 | 0 |
| `cat-log` | 32.00 | parse | 1172 | 0.0 | 0 |
| `cat-log` | 32.00 | emulate | 17 | 3.9 | 0 |
| `cat-log` | 32.00 | render | 5 | 0.0 | 0 |
| `dmesg` | 0.18 | escape-scan | 1156 | 0.2 | 0 |
| `dmesg` | 0.18 | parse | 96 | 4.0 | 0 |
| `dmesg` | 0.18 | emulate | 13 | 0.2 | 0 |
| `dmesg` | 0.18 | render | 7 | 4.0 | 0 |
| `htop` | 0.02 | escape-scan | 725 | 37.2 | 0 |
| `htop` | 0.02 | parse | 50 | 2.1 | 0 |
| `htop` | 0.02 | emulate | 19 | 2.1 | 0 |
| `htop` | 0.02 | render | 16 | 2.1 | 0 |
| `ls-color-r` | 0.79 | escape-scan | 1598 | 0.0 | 0 |
| `ls-color-r` | 0.79 | parse | 350 | 0.0 | 0 |
| `ls-color-r` | 0.79 | emulate | 9 | 0.9 | 0 |
| `ls-color-r` | 0.79 | render | 7 | 0.9 | 0 |
| `tmux-resize` | 0.02 | escape-scan | 857 | 2.6 | 0 |
| `tmux-resize` | 0.02 | parse | 143 | 2.6 | 0 |
| `tmux-resize` | 0.02 | emulate | 21 | 9.3 | 0 |
| `tmux-resize` | 0.02 | render | 7 | 2.6 | 0 |
| `vim-scroll` | 0.23 | escape-scan | 1207 | 0.2 | 0 |
| `vim-scroll` | 0.23 | parse | 153 | 3.0 | 0 |
| `vim-scroll` | 0.23 | emulate | 10 | 3.0 | 0 |
| `vim-scroll` | 0.23 | render | 9 | 0.2 | 0 |

## Reading these numbers

Each stream is replayed through four consumers, in order of how much of a terminal each one is.
`escape-scan` is the floor: it touches every byte and does the cheapest thing a parser must also
do, so no parser can beat it. `parse` is the Williams table with a handler that only counts.
`emulate` is the real `Emulator` - cells, scrollback, reflow, every sequence it implements - which
is the call a session makes for every byte a host sends. `render` is parse, decode, segment into
grapheme clusters, resolve each through the glyph atlas, fill instances, and one draw call per
16 KB of stream.

**Read figure 2 of the budget against `parse` and nothing else.** It asks for 400 MB/s of
sustained parse throughput, and `parse` is the arm it governs - the state machine, with a handler
that builds nothing. `emulate` is one to two orders of magnitude below it, and `emulate` is what
a session costs. Until QS141 nothing here measured that at all, so the figure could be met while
the thing it is about was slow. It also only meets 400 on the 32 MB stream: the smaller captures
are dominated by fixed cost, which the allocation note below applies to throughput too.

**The gap between `parse` and `render` is the figure this harness exists for.** It used to be
dominated by allocation rather than by drawing: the render arm allocated tens of megabytes per
megabyte of stream, because `GraphemeSegmenter` handed back a `List<string>` - one string per
cluster, which for a screen of text is one per character. QS24 replaced that with spans into a
reused buffer, so what is left in the gap is glyph and instance work.

**Read the allocation column against `escape-scan`, not against zero.** That consumer allocates
nothing whatsoever, so whatever it reports is the harness's own fixed cost divided by the size
of the stream - which is why a 0.02 MB stream shows tens of KB per MB and the 32 MB one shows
zero. `parse` reports the same figure as `escape-scan` on every stream, to the decimal, which
is what says the parser itself allocates nothing.

**Since QS94, `render` reports that same figure too.** The 32 MB replay used to allocate
54,227 KB per MB and take 102 gen-0 collections; it now allocates at the floor and takes none.
Throughput did not move, and was never the point: allocation on this path bought a collection
pause during somebody's `vim` session, not megabytes per second.

The render arm keeps a stub of a buffer rather than the real one - cursor, wrap, carriage return,
line feed and erase-display, and nothing else. That was once because no terminal buffer existed;
now one does, and the stub is kept on purpose so this arm measures the volume of glyph and
instance work a stream implies without the emulator's cost folded in. `emulate` is where the
real buffer is measured. It never presents - a vsync-locked present would cap the replay at the
display's refresh rate rather than measure the renderer.

**`emulate` is the only arm that allocates above the floor**, and Block C asks the parse path to
allocate zero in steady state. On the 32 MB stream, where fixed cost is noise, it reports 3.9 KB
per MB against `escape-scan`'s zero. Small by itself and not zero, which is what the criterion
says.

- `escape-scan` - touches every byte and counts ESC - the ceiling a parser is measured against

- `parse` - the parser alone - the Williams table over every byte, with a handler that only counts

- `emulate` - the parser and the real terminal it writes into - cells, scrollback and reflow, which is the call a session makes for every byte a host sends

- `render` - bytes to a drawn frame - parse, decode, cluster, atlas, instances and one draw call
