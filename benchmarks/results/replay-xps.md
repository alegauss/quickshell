# Replay results

Captured streams replayed through every consumer that exists. Run on XPS, .NET 10.0.11, 28 logical cores, 2026-08-27. Best of 5 after 1 warmup, 64 KB chunks.

| stream | MB | consumer | MB/s | alloc KB/MB | gen0 |
|---|---|---|---|---|---|
| `cat-log` | 32.00 | escape-scan | 2299 | 0.0 | 0 |
| `cat-log` | 32.00 | parse | 977 | 0.0 | 0 |
| `cat-log` | 32.00 | render | 9 | 0.0 | 0 |
| `dmesg` | 0.18 | escape-scan | 1889 | 4.0 | 0 |
| `dmesg` | 0.18 | parse | 227 | 4.0 | 0 |
| `dmesg` | 0.18 | render | 10 | 4.0 | 0 |
| `htop` | 0.02 | escape-scan | 1033 | 37.2 | 0 |
| `htop` | 0.02 | parse | 73 | 37.2 | 0 |
| `htop` | 0.02 | render | 22 | 37.2 | 0 |
| `ls-color-r` | 0.79 | escape-scan | 1856 | 0.9 | 0 |
| `ls-color-r` | 0.79 | parse | 499 | 0.9 | 0 |
| `ls-color-r` | 0.79 | render | 8 | 0.9 | 0 |
| `tmux-resize` | 0.02 | escape-scan | 838 | 46.1 | 0 |
| `tmux-resize` | 0.02 | parse | 178 | 46.1 | 0 |
| `tmux-resize` | 0.02 | render | 13 | 46.1 | 0 |
| `vim-scroll` | 0.23 | escape-scan | 1145 | 3.0 | 0 |
| `vim-scroll` | 0.23 | parse | 275 | 3.0 | 0 |
| `vim-scroll` | 0.23 | render | 12 | 3.0 | 0 |

## Reading these numbers

Each stream is replayed through three consumers. `escape-scan` is the floor: it touches every
byte and does the cheapest thing a parser must also do, so no parser can beat it. `parse` is the
Williams table with a handler that only counts. `render` is the whole path - parse, decode,
segment into grapheme clusters, resolve each through the glyph atlas, fill instances, and one
draw call per 16 KB of stream.

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

The render arm also stands in for a terminal buffer that does not exist yet: cursor, wrap,
carriage return, line feed and erase-display, and nothing else. What it measures is the volume
of glyph and instance work a stream implies, which is the part a real buffer would not change.
It never presents - a vsync-locked present would cap the replay at the display's refresh rate
rather than measure the renderer.

- `escape-scan` - touches every byte and counts ESC - the ceiling a parser is measured against

- `parse` - the parser alone - the Williams table over every byte, with a handler that only counts

- `render` - bytes to a drawn frame - parse, decode, cluster, atlas, instances and one draw call
