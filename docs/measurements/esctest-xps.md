# esctest

`esctest` from the terminal working group, run against the headless model on xps, 2026-10-08. No renderer and no pseudo-console: the suite runs on a Linux pty in WSL whose other end is a socket the emulator reads and answers, so every reply it judges is this client's own (QS211). 101 s, 667,147 bytes parsed.

| | tests | of total |
|---|---:|---:|
| passed | 216 | 38.0% |
| known bugs in xterm itself | 43 | 7.6% |
| failed | 309 | 54.4% |

## Why the failures fail

Grouped by what the traceback says, because 'three hundred failures' is not a
finding and 'one missing sequence and seventy real gaps' is.

| cause | tests |
|---|---:|
| the screen read back differs from xterm's (a checksum) | 112 |
| the suite declined the test itself | 86 |
| a real difference in behaviour | 111 |

## The failing tests

Every one, by class, so a change that improves one area while quietly breaking
another shows up as a number rather than as a feeling.

| class | failing |
|---|---:|
| `XtermWinopsTests` | 28 |
| `DECRQMTests` | 26 |
| `DECSEDTests` | 15 |
| `ChangeSpecialColorTests` | 14 |
| `DECSETTests` | 14 |
| `ChangeColorTests` | 13 |
| `ChangeDynamicColorTests` | 13 |
| `DECDSRTests` | 11 |
| `DECSELTests` | 10 |
| `DECRQSSTests` | 9 |
| `BSTests` | 8 |
| `DECCRATests` | 8 |
| `DECDCTests` | 6 |
| `DECICTests` | 6 |
| `DECSERATests` | 6 |
| `DECERATests` | 5 |
| `DECFRATests` | 5 |
| `DECSTRTests` | 5 |
| `ResetSpecialColorTests` | 5 |
| `DECBITests` | 4 |
| `DECFITests` | 4 |
| `DLTests` | 4 |
| `FFTests` | 4 |
| `HPRTests` | 4 |
| `INDTests` | 4 |
| `LFTests` | 4 |
| `SCORCTests` | 4 |
| `SDTests` | 4 |
| `SUTests` | 4 |
| `VPRTests` | 4 |
| `VTTests` | 4 |
| `CRTests` | 3 |
| `CUBTests` | 3 |
| `DCHTests` | 3 |
| `DECALNTests` | 3 |
| `DECRCTests` | 3 |
| `DECSCLTests` | 3 |
| `DECSETTiteInhibitTests` | 3 |
| `CNLTests` | 2 |
| `CPLTests` | 2 |
| `DA2Tests` | 2 |
| `DATests` | 2 |
| `EDTests` | 2 |
| `ICHTests` | 2 |
| `ILTests` | 2 |
| `RISTests` | 2 |
| `ResetColorTests` | 2 |
| `SMTests` | 2 |
| `XtermSaveTests` | 2 |
| `CHATests` | 1 |
| `CHTTests` | 1 |
| `CUFTests` | 1 |
| `CUPTests` | 1 |
| `DECIDTests` | 1 |
| `ECHTests` | 1 |
| `ELTests` | 1 |
| `HVPTests` | 1 |
| `NELTests` | 1 |
| `REPTests` | 1 |
| `RITests` | 1 |

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
