# Replay results

Captured streams replayed through every consumer that exists. Run on XPS, .NET 10.0.11, 28 logical cores, 2026-08-22. Best of 5 after 1 warmup, 64 KB chunks.

| stream | MB | consumer | MB/s | alloc KB/MB | gen0 |
|---|---|---|---|---|---|
| `cat-log` | 32.00 | escape-scan | 2594 | 0.0 | 0 |
| `cat-log` | 32.00 | parse | 979 | 0.0 | 0 |
| `cat-log` | 32.00 | render | 9 | 54227.3 | 102 |
| `dmesg` | 0.18 | escape-scan | 2192 | 4.0 | 0 |
| `dmesg` | 0.18 | parse | 249 | 4.0 | 0 |
| `dmesg` | 0.18 | render | 11 | 44360.4 | 0 |
| `htop` | 0.02 | escape-scan | 1127 | 37.2 | 0 |
| `htop` | 0.02 | parse | 84 | 37.2 | 0 |
| `htop` | 0.02 | render | 25 | 22271.0 | 0 |
| `ls-color-r` | 0.79 | escape-scan | 2251 | 0.9 | 0 |
| `ls-color-r` | 0.79 | parse | 569 | 0.9 | 0 |
| `ls-color-r` | 0.79 | render | 10 | 50645.3 | 2 |
| `tmux-resize` | 0.02 | escape-scan | 979 | 46.1 | 0 |
| `tmux-resize` | 0.02 | parse | 242 | 46.1 | 0 |
| `tmux-resize` | 0.02 | render | 17 | 31163.9 | 0 |
| `vim-scroll` | 0.23 | escape-scan | 1557 | 3.0 | 0 |
| `vim-scroll` | 0.23 | parse | 201 | 3.0 | 0 |
| `vim-scroll` | 0.23 | render | 13 | 42049.8 | 0 |

## Reading these numbers

Each stream is replayed through three consumers. `escape-scan` is the floor: it touches every
byte and does the cheapest thing a parser must also do, so no parser can beat it. `parse` is the
Williams table with a handler that only counts. `render` is the whole path - parse, decode,
segment into grapheme clusters, resolve each through the glyph atlas, fill instances, and one
draw call per 16 KB of stream.

**The gap between `parse` and `render` is the figure this harness exists for.** Today it is
dominated by allocation rather than by drawing: the render arm allocates tens of megabytes per
megabyte of stream, and the cause is `GraphemeSegmenter.Feed` returning a `List<string>` - one
string per cluster, which for a screen of text is one per character.

**Read the allocation column against `escape-scan`, not against zero.** That consumer allocates
nothing whatsoever, so whatever it reports is the harness's own fixed cost divided by the size
of the stream - which is why a 0.02 MB stream shows tens of KB per MB and the 32 MB one shows
zero. `parse` reports the same figure as `escape-scan` on every stream, to the decimal, which
is what says the parser itself allocates nothing. `render` does not.

The render arm also stands in for a terminal buffer that does not exist yet: cursor, wrap,
carriage return, line feed and erase-display, and nothing else. What it measures is the volume
of glyph and instance work a stream implies, which is the part a real buffer would not change.
It never presents - a vsync-locked present would cap the replay at the display's refresh rate
rather than measure the renderer.

- `escape-scan` - touches every byte and counts ESC - the ceiling a parser is measured against

- `parse` - the parser alone - the Williams table over every byte, with a handler that only counts

- `render` - bytes to a drawn frame - parse, decode, cluster, atlas, instances and one draw call
