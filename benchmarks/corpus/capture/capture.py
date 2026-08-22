#!/usr/bin/env python3
"""Run one program on a real pty of a fixed size and keep every byte it writes.

`script` cannot set the window size, and a corpus captured at 24x80 would not contain the
long wrapping lines and full-screen redraws the parser is going to be judged on. So the pty
is opened here, TIOCSWINSZ is set before the exec, and nothing between the program and the
file interprets anything: what lands is the byte stream a terminal would have received.
"""

import fcntl
import os
import pty
import select
import signal
import struct
import sys
import termios
import time

COLS = 200
ROWS = 50


def capture(path, argv, keys, seconds, resize_at=None, resize_to=None):
    """Run argv on a pty, feed `keys` (text, delay) pairs, and write the raw output to path.

    `resize_at` is seconds from the start at which the pty's window size becomes `resize_to`
    and SIGWINCH is delivered - a real resize, which is the only way to record a real repaint
    after one.
    """
    pid, master = pty.fork()

    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["COLUMNS"] = str(COLS)
        os.environ["LINES"] = str(ROWS)
        os.execvp(argv[0], argv)

    fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))

    written = 0
    started = time.time()
    resized = resize_at is None
    deadline = started + seconds
    pending = list(keys)
    next_key = time.time() + (pending[0][1] if pending else 0)

    with open(path, "wb") as sink:
        while time.time() < deadline:
            if not resized and time.time() - started >= resize_at:
                rows, cols = resize_to
                fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
                os.kill(pid, signal.SIGWINCH)
                resized = True

            if pending and time.time() >= next_key:
                text, _ = pending.pop(0)
                os.write(master, text.encode())
                next_key = time.time() + (pending[0][1] if pending else 0)

            ready, _, _ = select.select([master], [], [], 0.05)

            if not ready:
                continue

            try:
                chunk = os.read(master, 65536)
            except OSError:
                break

            if not chunk:
                break

            sink.write(chunk)
            written += len(chunk)

    try:
        os.kill(pid, signal.SIGKILL)
        os.waitpid(pid, 0)
    except OSError:
        pass

    os.close(master)
    print(f"{os.path.basename(path)}: {written} bytes", flush=True)
    return written


if __name__ == "__main__":
    print("capture.py is imported by session.sh", file=sys.stderr)
