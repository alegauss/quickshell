"""Writes assets\\quickshell.ico from the mark the design draws in its title bar (QS283).

The mark is Main.dc.html's: a rounded terminal frame, a prompt chevron and an underscore, stroked at
2 on a 24 grid. Windows draws an application icon outside any theme, on a light or a dark taskbar, so
the icon is the one place this client fixes a colour: the white mark on a tile of the site's violet,
which reads on both.

Each size is drawn on its own grid, not scaled down from the largest: the stroke and every edge are
put on whole pixels of that size and then drawn at four times over and reduced, so a 16-pixel icon
is sharp and not a blur of the 256-pixel one. The .ico holds a PNG per size.

    python -I tools\\make-icon.py          # writes assets\\quickshell.ico and assets\\quickshell.svg
"""

import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
VIOLET = (0x5B, 0x3F, 0xD6, 255)
WHITE = (255, 255, 255, 255)
OVER = 4

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "assets"

SVG = """<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 24 24">
  <!-- quickshell's mark (QS283): the design's title-bar glyph, white on the site's violet. -->
  <rect x="0" y="0" width="24" height="24" rx="5" fill="#5b3fd6"/>
  <g fill="none" stroke="#ffffff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"
     transform="translate(3.6 3.6) scale(0.7)">
    <rect x="3" y="4" width="18" height="16" rx="3"/>
    <path d="M7 9l3 3-3 3"/>
    <path d="M12 15h5"/>
  </g>
</svg>
"""


def snap(value, size):
    """A coordinate on the 24 grid, put on a whole pixel of this size, in oversampled pixels."""
    return round(value * size / 24) * OVER


def draw(size):
    big = size * OVER
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    pen = ImageDraw.Draw(image)

    pen.rounded_rectangle([0, 0, big - 1, big - 1], radius=max(OVER, round(big * 5 / 24)), fill=VIOLET)

    if size <= 24:
        small(pen, size)

        return image.resize((size, size), Image.Resampling.LANCZOS)

    # The glyph at seventy per cent of the tile, centred: (3.6 + 0.7 * x) on the 24 grid.
    def at(x, y):
        return snap(3.6 + 0.7 * x, size), snap(3.6 + 0.7 * y, size)

    # Never thinner than one whole pixel of this size, so the smallest icon keeps its lines.
    width = max(OVER, round(2 * 0.7 * size / 24) * OVER)

    left, top = at(3, 4)
    right, bottom = at(21, 20)
    pen.rounded_rectangle([left, top, right, bottom], radius=max(OVER, snap(0.7 * 3, size)), outline=WHITE, width=width)

    pen.line([at(7, 9), at(10, 12), at(7, 15)], fill=WHITE, width=width, joint="curve")
    pen.line([at(12, 15), at(17, 15)], fill=WHITE, width=width)

    return image.resize((size, size), Image.Resampling.LANCZOS)


def small(pen, size):
    """
    The mark at 24 pixels and under: the tile is the terminal's frame, so the inner frame goes and the
    prompt and underscore are drawn large, on a 16 grid, with a stroke of whole pixels laid on pixel
    centres. A frame of one pixel inside sixteen is a grey smudge; this is a prompt somebody can read.
    """
    unit = size / 16
    width = max(2, round(2 * unit)) * OVER

    def at(x, y):
        # Pixel centres for an even stroke are pixel edges, so whole numbers of the 16 grid, snapped.
        return round(x * unit) * OVER, round(y * unit) * OVER

    pen.line([at(4, 5), at(7.5, 8.5), at(4, 12)], fill=WHITE, width=width, joint="curve")
    pen.line([at(8.5, 12), at(12.5, 12)], fill=WHITE, width=width)


def ico(images):
    """An .ico of PNG entries, one per size, in the order given."""
    head = struct.pack("<HHH", 0, 1, len(images))
    blobs = []

    for image in images:
        buffer = io.BytesIO()
        image.save(buffer, format="PNG")
        blobs.append(buffer.getvalue())

    offset = len(head) + 16 * len(images)
    entries = b""

    for image, blob in zip(images, blobs):
        side = image.width if image.width < 256 else 0
        entries += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    return head + entries + b"".join(blobs)


def main():
    ASSETS.mkdir(exist_ok=True)
    (ASSETS / "quickshell.svg").write_text(SVG, encoding="utf-8", newline="\n")
    (ASSETS / "quickshell.ico").write_bytes(ico([draw(size) for size in SIZES]))
    print(f"wrote {ASSETS / 'quickshell.ico'} ({', '.join(str(size) for size in SIZES)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
