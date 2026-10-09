# esctest

`esctest` from the terminal working group, run against the headless model on xps, 2026-10-09. No renderer and no pseudo-console: the suite runs on a Linux pty in WSL whose other end is a socket the emulator reads and answers, so every reply it judges is this client's own (QS211). 48 s, 469,369 bytes parsed.

| | tests | of total |
|---|---:|---:|
| passed | 388 | 68.3% |
| known bugs in xterm itself | 43 | 7.6% |
| failed | 137 | 24.1% |

## Why the failures fail

Grouped by what the traceback says, because 'three hundred failures' is not a
finding and 'one missing sequence and seventy real gaps' is.

| cause | tests |
|---|---:|
| the screen read back differs from xterm's (a checksum) | 31 |
| the suite declined the test itself | 11 |
| a real difference in behaviour | 95 |

## The failing tests

Every one, by class, so a change that improves one area while quietly breaking
another shows up as a number rather than as a feeling.

| class | failing |
|---|---:|
| `XtermWinopsTests` | 14 |
| `DECDSRTests` | 11 |
| `DECRQMTests` | 9 |
| `DECSETTests` | 9 |
| `DECCRATests` | 8 |
| `ChangeColorTests` | 7 |
| `ChangeDynamicColorTests` | 7 |
| `ChangeSpecialColorTests` | 7 |
| `DECRQSSTests` | 7 |
| `BSTests` | 6 |
| `DECERATests` | 5 |
| `DECFRATests` | 5 |
| `DECSTRTests` | 5 |
| `HPRTests` | 4 |
| `VPRTests` | 4 |
| `DECALNTests` | 3 |
| `DECRCTests` | 3 |
| `DECSCLTests` | 3 |
| `DECSETTiteInhibitTests` | 3 |
| `SCORCTests` | 3 |
| `CUBTests` | 2 |
| `DA2Tests` | 2 |
| `DATests` | 2 |
| `RISTests` | 2 |
| `ResetSpecialColorTests` | 2 |
| `XtermSaveTests` | 2 |
| `DECIDTests` | 1 |
| `EDTests` | 1 |

## Reproducing this

```
# once: the suite is POSIX, so it lives in WSL
curl -L -o esctest.zip \
  https://codeload.github.com/ThomasDickey/esctest2/zip/refs/heads/master
unzip -q esctest.zip && mv esctest2-master/esctest ~/esctest

# then, from the repository root
dotnet run --project tools/Quickshell.Conformance -c Release
```

`QUICKSHELL_ESCTEST` overrides where the suite lives. An argument is a regular
expression over test names, so `... -- CUP` runs one section.
