# Replay results

Captured streams replayed through every consumer that exists. Run on XPS, .NET 10.0.11, 28 logical cores, 2026-08-31. Best of 5 after 1 warmup, 64 KB chunks.

| stream | MB | consumer | MB/s | alloc KB/MB | gen0 |
|---|---|---|---|---|---|
| `cat-log` | 32.00 | escape-scan | 2948 | 0.0 | 0 |
| `cat-log` | 32.00 | parse | 1155 | 0.0 | 0 |
| `cat-log` | 32.00 | decode | 499 | 1.9 | 0 |
| `cat-log` | 32.00 | segment | 259 | 3.9 | 0 |
| `cat-log` | 32.00 | emulate | 25 | 3.9 | 0 |
| `cat-log` | 32.00 | render | 7 | 0.0 | 0 |
| `dmesg` | 0.18 | escape-scan | 1411 | 4.0 | 0 |
| `dmesg` | 0.18 | parse | 152 | 4.0 | 0 |
| `dmesg` | 0.18 | decode | 134 | 0.2 | 0 |
| `dmesg` | 0.18 | segment | 78 | 4.0 | 0 |
| `dmesg` | 0.18 | emulate | 26 | 0.2 | 0 |
| `dmesg` | 0.18 | render | 9 | 4.0 | 0 |
| `htop` | 0.02 | escape-scan | 483 | 2.1 | 0 |
| `htop` | 0.02 | parse | 29 | 37.2 | 0 |
| `htop` | 0.02 | decode | 46 | 37.2 | 0 |
| `htop` | 0.02 | segment | 22 | 2.1 | 0 |
| `htop` | 0.02 | emulate | 21 | 37.2 | 0 |
| `htop` | 0.02 | render | 18 | 37.2 | 0 |
| `ls-color-r` | 0.79 | escape-scan | 1583 | 0.9 | 0 |
| `ls-color-r` | 0.79 | parse | 347 | 0.0 | 0 |
| `ls-color-r` | 0.79 | decode | 281 | 0.0 | 0 |
| `ls-color-r` | 0.79 | segment | 111 | 0.9 | 0 |
| `ls-color-r` | 0.79 | emulate | 29 | 0.9 | 0 |
| `ls-color-r` | 0.79 | render | 8 | 0.9 | 0 |
| `tmux-resize` | 0.02 | escape-scan | 280 | 2.6 | 0 |
| `tmux-resize` | 0.02 | parse | 135 | 2.6 | 0 |
| `tmux-resize` | 0.02 | decode | 67 | 2.6 | 0 |
| `tmux-resize` | 0.02 | segment | 31 | 2.6 | 0 |
| `tmux-resize` | 0.02 | emulate | 12 | 9.3 | 0 |
| `tmux-resize` | 0.02 | render | 7 | 2.6 | 0 |
| `vim-scroll` | 0.23 | escape-scan | 718 | 0.2 | 0 |
| `vim-scroll` | 0.23 | parse | 140 | 3.0 | 0 |
| `vim-scroll` | 0.23 | decode | 174 | 3.0 | 0 |
| `vim-scroll` | 0.23 | segment | 75 | 3.0 | 0 |
| `vim-scroll` | 0.23 | emulate | 33 | 3.0 | 0 |
| `vim-scroll` | 0.23 | render | 11 | 3.0 | 0 |

## What the arms noticed

Counters read off the subsystem itself after the run, which a throughput
figure cannot report. `cells moved by scrolling` against the cells a stream
printed is what says whether the terminal's cost is writing or shifting.

| stream | consumer | what it counted |
|---|---|---|
| `cat-log` | `emulate` | 830401 scrolls, 166080200 cells moved by scrolling, 0 clusters interned |
| `dmesg` | `emulate` | 1650 scrolls, 330000 cells moved by scrolling, 0 clusters interned |
| `htop` | `emulate` | 0 scrolls, 0 cells moved by scrolling, 0 clusters interned |
| `ls-color-r` | `emulate` | 12112 scrolls, 2422400 cells moved by scrolling, 0 clusters interned |
| `tmux-resize` | `emulate` | 0 scrolls, 0 cells moved by scrolling, 0 clusters interned |
| `vim-scroll` | `emulate` | 0 scrolls, 0 cells moved by scrolling, 0 clusters interned |

## Reading these numbers

Each stream is replayed through six consumers, in order of how much of a terminal each one is,
so that consecutive arms differ by one stage. `escape-scan` is the floor: it touches every byte
and does the cheapest thing a parser must also do, so no parser can beat it. `parse` is the
Williams table with a handler that only counts. `decode` adds UTF-8 decoding. `segment` adds
grapheme clustering - everything done to printed text short of writing a cell. `emulate` is the
real `Emulator` - cells, scrollback, reflow, every sequence it implements - which is the call a
session makes for every byte a host sends. `render` is the glyph path: atlas lookups, instances,
and one draw call per 16 KB of stream.

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

**Where the hundredfold goes.** Read the arms as a ladder and convert to time per megabyte,
on the 32 MB stream where fixed cost is noise. Each rung adds one stage of what `Emulator.Feed`
does, so the difference between two rungs is that stage's cost and nothing else:

| rung | ms/MB | added by this stage |
|---|---|---|
| `escape-scan` | 0.34 | the floor - touching every byte |
| `parse` | 0.85 | +0.51, the state machine |
| `decode` | 1.91 | +1.06, UTF-8 decoding |
| `segment` | 16.4 | **+14.5, grapheme clustering** |
| `emulate` | 76.9 | **+60.5, writing cells** |
| `render` | 200 | +123, glyph and instance work |

It is not one thing. Grapheme clustering costs about nine times what reaches it, and writing
cells about five times again; multiplied, that is the hundredfold. Cell writing is the larger
absolute cost and clustering the larger multiplier, so either is worth attacking and neither
alone is the answer.

**Allocation above the floor starts at `decode`, not at the terminal.** Block C asks the parse
path to allocate zero in steady state; `parse` reports the floor, `decode` 1.9 KB/MB, `segment`
3.9, and `emulate` adds none of its own. So the cells are free and the text handling is not,
which is the opposite of where one would look first.

- `escape-scan` - touches every byte and counts ESC - the ceiling a parser is measured against

- `parse` - the parser alone - the Williams table over every byte, with a handler that only counts

- `decode` - the parser and UTF-8 decoding, counting characters and building no cells

- `segment` - the parser, decoding and grapheme clustering - everything done to printed text short of writing a cell

- `emulate` - the parser and the real terminal it writes into - cells, scrollback and reflow, which is the call a session makes for every byte a host sends

- `render` - bytes to a drawn frame - parse, decode, cluster, atlas, instances and one draw call
