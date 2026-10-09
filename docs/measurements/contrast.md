# Text weight against Windows' own

QS107 asked whether this renderer's text is lighter than the text Windows draws, because Direct2D
applies an enhanced-contrast curve to DirectWrite's coverage and this renderer applies none. It is
measured by `ContrastTests.ThisRenderersInkIsMeasuredAgainstDirect2Ds` in
`tests/Quickshell.Render.Tests`, which writes `TestResults/contrast.txt` on every run.

## How

The run `Hamburgefonstiv 0123456789` in Consolas 11 pt at 96 DPI is drawn twice: through this
renderer, one glyph per cell from the atlas, and through Direct2D's `DrawText` on a DXGI surface
with the system's own rendering parameters. Both are read back and the ink is summed over the row
of cells the run occupies, in linear light: the light added for light text on a dark ground, the
light taken away for dark text on a light one. Ink is where the strokes are, and an enhancement
that thickens stems shows up as more of it.

## What came back

Reference machine, 2026-10-06, hardware adapter:

| polarity | antialiasing | this renderer | Direct2D | ratio |
|---|---|---:|---:|---:|
| light on dark | grayscale | 595.9 | 571.4 | **1.043** |
| light on dark | ClearType | 602.7 | 566.1 | **1.065** |
| dark on light | grayscale | 596.9 | 722.6 | **0.826** |
| dark on light | ClearType | 602.8 | 682.8 | **0.883** |

## Reading it

- **This renderer weighs the same both ways, and Windows does not.** 596 light-on-dark and 597
  dark-on-light is the symmetry QS9 built the linear blend for and tests for. Direct2D's text is
  571 one way and 723 the other.
- **On a dark theme, the default, this text is 4 to 7 % heavier than Windows'.** There is
  nothing to carry over for it, and the thinness QS35 was filed under is not this.
- **On a light theme it is 12 to 17 % lighter.** That is the symptom QS107 names, and it is real
  for dark text on a light ground only.
- Matching Windows means giving up the symmetry, weighting coverage by polarity as Direct2D's
  curve evidently does. That trades one property that is tested against another that is
  measured here, so it is a decision, not a fix. QS212 carries it.

What this does not settle: the per-pixel shape of the curve, since a total is not a profile. The
guest, on WARP and with no ClearType panel, measures its own numbers and is not the reference.

## Matching Windows (QS212)

The owner chose to match Windows on 2026-10-09. The grid shader now applies DirectWrite's own
enhanced-contrast curve, `a(k+1)/(ak+1)`, to coverage. The weight is full for ink darker than a
quarter's lightness and zero past three quarters, so light text on a dark ground is drawn as
before. The k was fitted by the same test's sweep, run in the guest on 2026-10-09; at k 0 the
guest reproduces the reference desk's rows above:

| antialiasing | k 0 | k 1.0 | k 2.0 |
|---|---:|---:|---:|
| grayscale | 0.826 | 0.943 | **1.007** |
| ClearType | 0.883 | **1.005** | — |

The defaults are `CellRenderer.DefaultDarkContrastGrayscale` (2.0) and
`DefaultDarkContrastClearType` (1.0). The test now holds dark on light within 3 % of Direct2D,
and light on dark keeps its 1.043 and 1.065. QS9's symmetry is still tested, with the curve
turned off (`DarkContrast = 0`).
