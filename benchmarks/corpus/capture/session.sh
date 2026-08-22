#!/bin/sh
# The six sessions, run for real and recorded byte for byte into /corpus.
set -e

OUT=/corpus
mkdir -p "$OUT"

# A real large source file to open in vim, and a real large body of text to cat. Both are
# whatever this machine actually has - found, never written for the occasion.
BIG_SOURCE=$(find /usr/include -type f -size +150k 2>/dev/null | head -1)
[ -n "$BIG_SOURCE" ] || BIG_SOURCE=$(find /usr -type f -name '*.h' -size +60k 2>/dev/null | head -1)

if [ ! -f /var/tmp/big.log ]; then
    find /usr/include /usr/share/doc /usr/share/vim -type f \
         \( -name '*.h' -o -name '*.hpp' -o -name '*.ipp' -o -name '*.txt' -o -name '*.vim' -o -name 'copyright' \) \
         -exec cat {} + > /var/tmp/big.log 2>/dev/null || true
fi

echo "vim opens   $BIG_SOURCE ($(wc -c < "$BIG_SOURCE") bytes)"
echo "cat reads   /var/tmp/big.log ($(wc -c < /var/tmp/big.log) bytes)"

python3 - "$OUT" "$BIG_SOURCE" <<'PY'
import sys
sys.path.insert(0, "/usr/local/bin")
from capture import capture
import os

out, big_source = sys.argv[1], sys.argv[2]

# htop: a full-screen curses application redrawing on its own clock.
capture(os.path.join(out, "htop.raw"), ["htop", "-d", "2"], [], 14)

# vim: opening a large source file and scrolling it, which is the redraw-heavy case.
capture(os.path.join(out, "vim-scroll.raw"),
        ["vim", "-u", "NONE", "-c", "syntax on", "-c", "set nu", big_source],
        [("\x06", 1.2)] + [("\x06", 0.22)] * 60 + [("\x1b:q!\r", 0.4)],
        20)

# ls --color -R over a deep real tree: short lines, dense colour changes.
capture(os.path.join(out, "ls-color-r.raw"), ["ls", "--color=always", "-R", "/usr"], [], 25)

# cat of a large log: the throughput case, almost no escape sequences.
capture(os.path.join(out, "cat-log.raw"), ["cat", "/var/tmp/big.log"], [], 90)

# tmux repainting after a real resize: the window goes 200x50 -> 120x30 mid-session and the
# server gets a real SIGWINCH, which is the only way to record a real repaint after one.
capture(os.path.join(out, "tmux-resize.raw"),
        ["tmux", "-f", "/dev/null", "new-session", "-x", "200", "-y", "50",
         "sh", "-c", "ls --color=always -R /usr/include | head -600; exec sh"],
        [("\x02\"", 2.0), ("ls --color=always /usr/share\r", 1.5), ("\x02%", 2.0)],
        18, resize_at=9.0, resize_to=(30, 120))

# dmesg: long lines that wrap, which is the case that breaks naive line handling.
capture(os.path.join(out, "dmesg.raw"), ["sh", "-c", "dmesg || cat /var/log/dmesg || journalctl -k"], [], 8)
PY

echo "captured:"
ls -l "$OUT"
