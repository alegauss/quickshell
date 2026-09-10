# Keys

**Every chord on this page is one the program on the far side will never see.** That is the cost of
having them, and it is the reason this page exists: a user who loses a chord to their client should
read about it here rather than discover it by pressing it and watching nothing happen.

**You do not have to learn any of them.** `Ctrl+Shift+P` opens the command palette, which lists
everything this client does and shows each one's chord beside it. This page is the reference; the
palette is how you find something without one.

Nothing here is configurable yet. When it becomes configurable this page becomes the defaults.

## Tabs

| Chord | What it does |
|---|---|
| `Ctrl+Shift+T` | Opens a tab. |
| `Ctrl+Shift+W` | Closes the tab. If sessions are still live in it you are asked first. |
| `Ctrl+Tab` | The next tab, wrapping past the last. |
| `Ctrl+Shift+Tab` | The previous tab, wrapping past the first. |
| `Alt+1` `Alt+2` `Alt+3` `Alt+4` `Alt+5` `Alt+6` `Alt+7` `Alt+8` `Alt+9` | The tab in that position. A number past the last tab does nothing, rather than landing you somewhere you did not ask for. |

## Panes

| Chord | What it does |
|---|---|
| `Ctrl+Shift+\` | Splits the focused pane side by side. |
| `Ctrl+Shift+-` | Splits it one above the other. |
| `Alt+Shift+Left` `Alt+Shift+Right` `Alt+Shift+Up` `Alt+Shift+Down` | Moves the focus to the pane that way on screen. Nothing that way leaves the focus where it is. |
| `Ctrl+Shift+Z` | Zooms the focused pane to fill the tab, and back. The other panes keep running. |
| `Ctrl+Shift+E` | Gives every pane in the tab an equal share. |
| `Ctrl+Shift+B` | Types into every pane in the tab at once; press it again to stop. A paste goes to all of them too. Every pane receiving what you type has an orange edge for as long as it lasts, and nothing outside this tab ever receives it. It stops by itself when you switch tabs, split, zoom or close a pane, and it is never on when the client starts. |

There is no chord that closes a pane. A pane goes when the thing running in it ends, which is what
typing `exit` has always meant.

## Reading and finding

| Chord | What it does |
|---|---|
| `Shift+PageUp` | Back one screen through the scrollback. |
| `Shift+PageDown` | Forward one screen. |
| `Ctrl+Shift+F` | Opens the find bar, or closes it if it is open. |

## Copy and paste

| Chord | What it does |
|---|---|
| `Ctrl+Shift+C` | Copies the selection. |
| `Ctrl+Shift+V` | Pastes. A paste carrying a newline is shown to you first unless the host has turned bracketed paste on — see [`warnOnPaste`](SETTINGS.md#warnonpaste). |

## The client itself

| Chord | What it does |
|---|---|
| `Ctrl+Shift+P` | The command palette: everything this client does, found by typing part of its name. Each entry shows its chord, so this is also how you learn them. |
| `Ctrl+Shift+R` | Rereads [the settings file](SETTINGS.md). Saving it is normally enough; this is what to press when it was not. |
| `Ctrl+Shift+I` | Imports sessions from another client. |
| `Ctrl+Shift+F1` | Collects a diagnostic report. |

## Why they all look like that

**Two modifiers, almost always.** A terminal owes the program on the far side everything it can
give it, and a chord with two modifiers is one almost nothing binds. That is why the client's own
actions are on `Ctrl+Shift+…` rather than on the single-modifier chords a desktop application would
normally take.

**`Alt` and not `Ctrl` for the digits.** `Ctrl` with a digit is a control character a host has
meanings for. `Alt` with one is not.

**`Ctrl+Tab` is the exception, and it is taken anyway.** A full-screen program could plausibly want
it. A client where you cannot leave the tab you are in has no tabs, so this one is spent knowingly.

## What is deliberately not taken

- **`Ctrl+C`.** It is how a person stops a runaway program. A client that took it would have taken
  away the thing they reach for at the exact moment something has gone wrong. Copy is
  `Ctrl+Shift+C` and always will be.
- **`F1` on its own.** An unmodified function key belongs to the program on the far side.
- **`PageUp` and `PageDown` on their own.** A pager and an editor both bind them, and taking them
  would mean the terminal scrolled while the thing on screen did not.
- **Anything with no modifier at all.** Those are the remote program's, without exception.
