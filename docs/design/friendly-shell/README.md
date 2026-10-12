# A friendlier way in — the approved design

The design QS268 to QS272 build from, approved on 2026-10-11 by the project's owner. The live canvas, where the screens can
be looked at, clicked through and commented on, is
<https://claude.ai/artifact/KppK8ZsPfYQRjRKTi9WBbW> (private to its owner until shared).

The `.dc.html` files here are its sources. They render only inside that canvas, which supplies their
runtime, but they read as ordinary HTML and inline CSS: every colour, size, radius and spacing the
screens use is written out in them, which is what makes them a reference rather than a picture.

| File | Screen |
|---|---|
| `Main.dc.html` | Home: the sessions sidebar, quick connect, recent sessions and three ways to start |
| `Connected.dc.html` | A connected tab: the session bar, the terminal, the status bar, the sidebar folded to a rail |
| `NewSession.dc.html` | The new-session dialog over the window |
| `HomeDark.dc.html` | Home in the dark theme |
| `canvas.json` | Where each screen sits on the canvas |

| Task | What it builds | Screen |
|---|---|---|
| QS268 | The sessions sidebar, folding to a rail | `Main.dc.html`, `Connected.dc.html` |
| QS269 | The session bar over a remote tab, and the status bar | `Connected.dc.html` |
| QS270 | The session dialog's sign-in choice, grouped options and Test connection | `NewSession.dc.html` |
| QS271 | Quick connect: `user@host` and Connect | `Main.dc.html` |
| QS272 | The Home tab a window opens on, after QS268 and QS271 | `Main.dc.html`, `HomeDark.dc.html` |

## What is decided, and what is only the mockup's

**Decided.** The structure and the language claude-tray and freewilly already share: Fluent under
`ThemeMode`, Segoe UI Variable, flat cards with a 1 px border and an 8 px radius, a 3 px accent
underline under the selected tab, rows that are surfaces rather than forms, and a status chip that
always carries a word, never a colour alone. A field a folder hands down says so beside itself. The
local shell is a way to start, not the first thing on screen.

**The mockup's.** The violet (`#5b3fd6` light, `#9b87fc` dark) is quickshell's site colour, used here
to stand for the accent. In the client the accent is Fluent's own brushes, which follow the Windows
accent, and no `.xaml` or code carries a hex for a themed surface, as in both sibling apps. On an
accent fill in the dark theme the text is dark, because white on the light violet fails contrast.
The sessions, hosts and terminal output are the examples in `docs/SESSIONS.md`; `[SIZE]` and its
neighbours are placeholders, not figures.

Changing the design means changing the canvas and copying its sources here again, in the same
commit as the code that follows it.
