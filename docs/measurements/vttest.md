# vttest

`vttest` 2.7 (20221229), judged by xterm instead of by a person, on 2026-10-06. The suite runs
it on every build: `VttestTests.EveryVttestScreenIsDrawnAsXtermDrawsIt` in
`tests/Quickshell.Terminal.Tests`.

## How a screen is judged

vttest draws a screen and asks whoever is looking whether it is right, so on its own it is not
a test. `tests/Quickshell.Terminal.Tests/Vttest/oracle` is a Debian container that:

1. runs vttest on a real 80x24 pty with `TERM=xterm`, answers its device-attributes queries as
   xterm does, and keeps every byte it writes;
2. walks menus 1 (cursor movements), 2 (screen features) and 8 (VT102 insert and delete),
   cutting the stream each time vttest stops and waits for RETURN, which gives 31 cuts;
3. has a real xterm, under Xvfb, draw each cut and print its own screen with Media Copy, with
   printer extent set so a screen inside a scrolling region is printed whole.

The cuts, the stream and xterm's 31 screens are committed beside the test. The test replays each
cut into the emulator and compares the text of every row with xterm's. Regenerate with:

```
docker build -t qs-vttest tests/Quickshell.Terminal.Tests/Vttest/oracle
docker run --rm -v <empty folder>:/out qs-vttest
```

## What came back

| | cuts | of 31 |
|---|---:|---:|
| drawn as xterm draws them | 23 | 74% |
| drawn differently, each with a filed defect | 8 | 26% |

| cuts | what differs | line |
|---|---|---|
| 010, 012, 030 | 132 columns asked for; xterm obeys, this keeps 80 | QS208 |
| 014, 016 | cursor up and down leave the scrolling region under origin mode | QS205 |
| 022 | restoring the cursor does not restore the line-drawing set | QS206 |
| 025, 026 | insert mode overwrites instead of inserting; 026 inherits 025's row | QS207 |

A cut that disagrees and is not in that table fails the suite as a regression, and a listed cut
that starts agreeing fails it too, so the list cannot go stale.

## What this does not judge, said plainly

- **Attributes.** xterm's printout is text, so the rendition screens are judged on where their
  words land and not on whether they are bold, blinking or reversed. The golden-image suite is
  where rendition is looked at.
- **The other menus.** 3 (character sets), 4 (double-sized characters), 5 (keyboard),
  6 (terminal reports), 7 (VT52), 9 to 11. Each needs keys answered or reports read back that
  this driver does not do yet.
- **xterm's own mistakes.** xterm is the reference, so where it is wrong this agrees with it.
