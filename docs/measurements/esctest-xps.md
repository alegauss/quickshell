# esctest

`esctest` from the terminal working group, run against the headless model on xps, 2026-08-30. No renderer and no network: the suite runs as the pseudo-console's child and this client is the terminal it judges. 90 s, 843,915 bytes parsed.

| | tests | of total |
|---|---:|---:|
| passed | 151 | 26.6% |
| known bugs in xterm itself | 42 | 7.4% |
| failed | 375 | 66.0% |

## Why the failures fail

Grouped by what the traceback says, because 'three hundred failures' is not a
finding and 'one missing sequence and seventy real gaps' is.

| cause | tests |
|---|---:|
| the screen could not be read back at all | 228 |
| the suite declined the test itself | 76 |
| a real difference in behaviour | 71 |

## The failing tests

Every one, by class, so a change that improves one area while quietly breaking
another shows up as a number rather than as a feeling.

| class | failing |
|---|---:|
| `XtermWinopsTests` | 28 |
| `DECRQMTests` | 22 |
| `DECSEDTests` | 17 |
| `DECSETTests` | 17 |
| `ChangeSpecialColorTests` | 14 |
| `ChangeColorTests` | 13 |
| `ChangeDynamicColorTests` | 13 |
| `DECSELTests` | 10 |
| `DLTests` | 10 |
| `EDTests` | 10 |
| `DECCRATests` | 9 |
| `SDTests` | 9 |
| `SUTests` | 9 |
| `BSTests` | 8 |
| `DECSETTiteInhibitTests` | 8 |
| `DECSTBMTests` | 8 |
| `DECDCTests` | 7 |
| `DECICTests` | 7 |
| `DECRQSSTests` | 7 |
| `DECSERATests` | 7 |
| `ELTests` | 7 |
| `DCHTests` | 6 |
| `DECERATests` | 6 |
| `DECFRATests` | 6 |
| `ECHTests` | 6 |
| `ICHTests` | 6 |
| `ILTests` | 6 |
| `DECRCTests` | 5 |
| `DECSTRTests` | 5 |
| `FFTests` | 5 |
| `INDTests` | 5 |
| `LFTests` | 5 |
| `NELTests` | 5 |
| `RISTests` | 5 |
| `RITests` | 5 |
| `ResetSpecialColorTests` | 5 |
| `SCORCTests` | 5 |
| `VTTests` | 5 |
| `DECDSRTests` | 4 |
| `DECSCLTests` | 4 |
| `REPTests` | 4 |
| `SMTests` | 3 |
| `CUBTests` | 2 |
| `DA2Tests` | 2 |
| `DATests` | 2 |
| `DECBITests` | 2 |
| `DECFITests` | 2 |
| `ResetColorTests` | 2 |
| `XtermSaveTests` | 2 |
| `APCTests` | 1 |
| `CBTTests` | 1 |
| `CHATests` | 1 |
| `CNLTests` | 1 |
| `CPLTests` | 1 |
| `CRTests` | 1 |
| `CUPTests` | 1 |
| `DCSTests` | 1 |
| `DECALNTests` | 1 |
| `HPRTests` | 1 |
| `HVPTests` | 1 |
| `PMTests` | 1 |
| `RMTests` | 1 |
| `SOSTests` | 1 |
| `VPRTests` | 1 |

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
