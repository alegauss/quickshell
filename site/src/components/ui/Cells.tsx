// The scenery. This client's renderer is a grid of cells, so the hero closes into one and
// the footer opens out of it: three rows of lit and unlit cells drifting at different
// speeds, with the block cursor sitting on the nearest row.
//
// Seamlessness here is arithmetic rather than luck. Every row is drawn twice across a
// 2880-unit viewBox laid out at 200% of the band, so 1440 units is exactly one band width;
// the drift translates by 50%, one whole repeat, which means the frame after the last is
// the first. PITCH divides 1440 for the same reason: a pitch that does not close on 1440
// shows a seam once per cycle, and once per cycle is every few seconds.
//
// Which cells are lit comes from a deterministic generator rather than Math.random, because
// the server render and the client hydration have to agree cell for cell.
//
// Decorative only: it carries no copy, so it is hidden from the accessibility tree, dropped
// from the Markdown twin, and it stops moving under prefers-reduced-motion.

const SPAN = 2880; // two identical repeats of REPEAT
const REPEAT = 1440;
const PITCH = 48; // 30 cells per repeat, and 1440 / 48 is exact
const COLUMNS = REPEAT / PITCH;
const CELL_W = 34;

/** xorshift32. Same seed, same row, on the server and in the browser. */
function litColumns(seed: number, density: number): boolean[] {
  let s = seed >>> 0;
  const out: boolean[] = [];
  for (let i = 0; i < COLUMNS; i++) {
    s ^= s << 13;
    s >>>= 0;
    s ^= s >>> 17;
    s ^= s << 5;
    s >>>= 0;
    out.push(s % 1000 < density * 1000);
  }
  return out;
}

// Back to front. Nearer rows are drawn lower and taller, which is what reads as depth; the
// colours and the speeds that go with them are in the stylesheet, where they are tuned.
const ROWS = [
  { key: "back", y: 44, h: 26, seed: 0x51ed270b, density: 0.46, caret: -1 },
  { key: "mid", y: 94, h: 30, seed: 0x2f6b1c33, density: 0.58, caret: -1 },
  // one caret per repeat, and the band shows exactly one repeat, so exactly one is on screen
  { key: "front", y: 146, h: 34, seed: 0x7c5cf501, density: 0.68, caret: 21 },
] as const;

export function Cells({ className }: { className?: string }) {
  return (
    <div
      className={className ? `cells ${className}` : "cells"}
      aria-hidden="true"
      data-twin="omit"
    >
      {ROWS.map((row) => {
        const lit = litColumns(row.seed, row.density);
        return (
          <div className={`cell-row cell-${row.key}`} key={row.key}>
            <svg
              className="cell-drift"
              viewBox={`0 0 ${SPAN} 200`}
              preserveAspectRatio="none"
              focusable="false"
            >
              {[0, 1].map((repeat) =>
                lit.map((on, i) =>
                  on || i === row.caret ? (
                    <rect
                      key={`${repeat}-${i}`}
                      className={i === row.caret ? "cell-caret" : "cell-glyph"}
                      x={repeat * REPEAT + i * PITCH}
                      y={row.y}
                      width={CELL_W}
                      height={row.h}
                      rx="4"
                    />
                  ) : null,
                ),
              )}
            </svg>
          </div>
        );
      })}
    </div>
  );
}
