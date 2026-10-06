#!/usr/bin/env python3
"""Drive vttest on a real pty, keep every byte, and have xterm say what each screen is.

vttest's verdict is a person looking at a screen. Two things replace the person: the stream,
cut at every point where vttest stops and waits for RETURN, and xterm's own reading of each cut,
printed by xterm itself. A terminal that draws the cut the way xterm does passed that screen.
"""

import json
import os
import pty
import fcntl
import select
import struct
import subprocess
import sys
import termios
import time

COLS, ROWS = 80, 24
OUT = "/out"

# Menu entry, then how many RETURNs walk through its screens. The counts are generous: a RETURN
# past the last screen lands on the menu again, which is still a screen.
MENUS = [("1", 7), ("2", 15), ("8", 8)]

# What xterm 379 on bookworm answers to the two device-attributes queries, as it printed them.
PRIMARY = b"\x1b[?64;1;2;6;9;15;16;17;18;21;22;28c"
SECONDARY = b"\x1b[>41;379;0c"


def drive():
    pid, master = pty.fork()

    if pid == 0:
        os.environ["TERM"] = "xterm"
        os.execvp("vttest", ["vttest", f"{ROWS}x{COLS}"])

    fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))

    stream = bytearray()
    cuts = []

    def pump(seconds):
        until = time.time() + seconds
        while time.time() < until:
            ready, _, _ = select.select([master], [], [], 0.05)
            if ready:
                try:
                    chunk = os.read(master, 65536)
                except OSError:
                    return
                stream.extend(chunk)

                # vttest asks who the terminal is and waits for the answer. These are xterm's own
                # answers, so the stream is the one vttest writes to an xterm.
                if b"\x1b[c" in chunk or b"\x1b[0c" in chunk:
                    os.write(master, PRIMARY)
                if b"\x1b[>c" in chunk or b"\x1b[>0c" in chunk:
                    os.write(master, SECONDARY)

    def key(text, label):
        pump(1.5)
        cuts.append({"name": label, "offset": len(stream)})
        os.write(master, text.encode())

    pump(1.5)
    cuts.append({"name": "menu", "offset": len(stream)})

    for entry, screens in MENUS:
        os.write(master, f"{entry}\r".encode())
        for screen in range(screens):
            key("\r", f"menu-{entry}-screen-{screen + 1}")
        pump(0.8)

    os.write(master, b"0\r")
    pump(1.0)

    try:
        os.kill(pid, 9)
    except OSError:
        pass

    return bytes(stream), cuts


def xterm_reads(prefix, number):
    path = f"/tmp/cut-{number}.raw"
    with open(path, "wb") as f:
        f.write(prefix)

    target = f"{OUT}/xterm/{number:03d}.txt"
    if os.path.exists(target):
        os.remove(target)

    # The screen is printed by xterm itself, asked with Media Copy (CSI i) once the cut has been
    # drawn: no synthetic key, which xterm refuses by default, and nothing that changes the screen.
    # Printer extent (CSI ? 19 h) first, because without it a VT102 prints only the scrolling
    # region, and a cut taken inside a region test would be read as a screen of two rows.
    command = [
        "xterm", "-geometry", f"{COLS}x{ROWS}",
        "-xrm", f"XTerm*printerCommand: cat > {target}",
        "-xrm", "XTerm*printAttributes: 0",
        # No output processing, so a bare LF stays one; no echo, so xterm's answers to vttest's
        # queries are not drawn back onto the screen being read.
        "-e", "sh", "-c", f"stty -opost -echo -icanon; cat {path}; printf '\\033[?19h\\033[i'; sleep 60",
    ]
    proc = subprocess.Popen(command, env=dict(os.environ, DISPLAY=":9"))
    for _ in range(60):
        if os.path.exists(target) and os.path.getsize(target) > 0:
            break
        time.sleep(0.1)
    time.sleep(0.2)
    proc.kill()
    proc.wait()


def main():
    os.makedirs(f"{OUT}/xterm", exist_ok=True)
    stream, cuts = drive()

    with open(f"{OUT}/vttest.raw", "wb") as f:
        f.write(stream)
    with open(f"{OUT}/cuts.json", "w") as f:
        json.dump(cuts, f, indent=1)

    xvfb = subprocess.Popen(["Xvfb", ":9", "-screen", "0", "1280x1024x24"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.0)

    for number, cut in enumerate(cuts):
        xterm_reads(stream[:cut["offset"]], number)
        print(f"{number:03d} {cut['name']} at {cut['offset']}", flush=True)

    xvfb.kill()
    print(f"{len(stream)} bytes, {len(cuts)} cuts", flush=True)


if __name__ == "__main__":
    sys.exit(main())
