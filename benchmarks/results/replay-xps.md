# Replay results

Captured streams replayed through every consumer that exists. Run on XPS, .NET 10.0.11, 28 logical cores, 2026-08-21. Best of 5 after 1 warmup, 64 KB chunks.

| stream | MB | consumer | MB/s | alloc KB/MB | gen0 |
|---|---|---|---|---|---|
| `cat-log` | 32.00 | escape-scan | 1506 | 0.0 | 0 |
| `dmesg` | 0.18 | escape-scan | 871 | 2.1 | 0 |
| `htop` | 0.02 | escape-scan | 481 | 19.6 | 0 |
| `ls-color-r` | 0.79 | escape-scan | 982 | 0.5 | 0 |
| `tmux-resize` | 0.02 | escape-scan | 399 | 24.4 | 0 |
| `vim-scroll` | 0.23 | escape-scan | 713 | 1.6 | 0 |

## The arms that are empty

Each stream is meant to be replayed twice: headless, which measures the parser alone, and
through the whole pipeline with a renderer, which measures what coalescing saves. Neither
consumer exists yet - there is no parser and no renderer - so the table above has one arm,
and it is the floor rather than either of them. A number here is a ceiling: whatever the
parser costs is on top of touching the bytes at all.

- `escape-scan` - touches every byte and counts ESC - the ceiling a parser is measured against
