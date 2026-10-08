# Keys

**Every chord on this page is one the program on the far side will never see.** That is the cost of
having them, and it is the reason this page exists: a user who loses a chord to their client should
read about it here rather than discover it by pressing it and watching nothing happen.

**You do not have to learn any of them.** `Ctrl+Shift+P` opens the command palette, which lists
everything this client does and shows each one's chord beside it. This page is the reference; the
palette is how you find something without one.

**Some actions have no chord at all**, because a chord is taken from the program on the far side
and they are not reached for often enough to be worth one. They are in the palette and nowhere
else: *Browse files* opens the file browser, *Install quickshell for this user* installs the copy
you are running — it is offered only by a copy that is not already the installed one, and
[INSTALL.md](INSTALL.md) says what it writes — *Open session* turns the palette into a list of
your saved sessions, found by path, host or tag, with the ones you opened last at the top,
*Show forwards* lists every forward the client holds, what each is carrying now, and the ones
that did not start,
*Trace this session* connects the saved session in the pane again with a trace of its own under
the log folder, handshake included, and says in the pane where the file is; the remote shell is a
new one, as after any reconnect, and the trace lasts until the client closes,
*New tab, recorded* and *Open session, recorded* open a session that keeps everything its host sends
in a file you name first — never what you type, up to 256 MB compressed, under `recordings` in
the client's folder, with the title saying so while it runs — and *Stop recording* closes the
file and says where it is, and
*Leave this pane out of broadcast typing* is described under [Panes](#panes).

Nothing here is configurable yet. When it becomes configurable this page becomes the defaults.

## Tabs

| Chord | What it does |
|---|---|
| `Ctrl+Shift+T` | Opens a tab. |
| `Ctrl+Shift+W` | Closes the tab. If sessions are still live in it you are asked first. |
| `Ctrl+Tab` | The next tab, wrapping past the last. |
| `Ctrl+Shift+Tab` | The previous tab, wrapping past the first. |
| `Ctrl+Shift+PageUp` | Moves the tab you are in one place left along the strip. It stops at the first place rather than wrapping. |
| `Ctrl+Shift+PageDown` | Moves it one place right, and stops at the last. |
| `Alt+1` `Alt+2` `Alt+3` `Alt+4` `Alt+5` `Alt+6` `Alt+7` `Alt+8` `Alt+9` | The tab in that position. A number past the last tab does nothing, rather than landing you somewhere you did not ask for. |

## Panes

| Chord | What it does |
|---|---|
| `Ctrl+Shift+\` | Splits the focused pane side by side. |
| `Ctrl+Shift+-` | Splits it one above the other. |
| `Alt+Shift+Left` `Alt+Shift+Right` `Alt+Shift+Up` `Alt+Shift+Down` | Moves the focus to the pane that way on screen. Nothing that way leaves the focus where it is. |
| `Ctrl+Alt+Shift+Left` `Ctrl+Alt+Shift+Right` `Ctrl+Alt+Shift+Up` `Ctrl+Alt+Shift+Down` | Moves the divider beside the focused pane a step that way. A divider can also be dragged with the mouse. |
| `Ctrl+Shift+Z` | Zooms the focused pane to fill the tab, and back. The other panes keep running. |
| `Ctrl+Shift+E` | Gives every pane in the tab an equal share. |
| `Ctrl+Shift+B` | Types into every pane in the tab at once, pastes included; press it again to stop. Each pane receiving what you type has an orange edge. |

There is no chord that closes a pane. A pane goes when the thing running in it ends, which is what
typing `exit` has always meant.

**Typing into several panes at once.** While it is on, what you type into a pane with an orange
edge reaches every pane with one, and nothing outside this tab ever receives it. To leave one pane
out — the host a command must not reach — focus it and pick *Leave this pane out of broadcast
typing* in the palette: its edge goes, and what you type into it reaches that pane alone until you
pick *Include this pane in broadcast typing*. It stops by itself when you switch tabs, split, zoom
or close a pane, and it is never on when the client starts.

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

Dropping files from Explorer onto a terminal types their paths at the prompt of the pane you let
go over, each quoted for the shell running there and followed by a space, and that pane takes the
keyboard. Nothing is copied: a drop onto a terminal is the path as an argument.

Holding `Shift` while you let go sends the files instead, into the directory the shell in that
pane says it is in, over the pane's own SSH connection. The title says how many landed. Nothing is
sent from a local pane, or from one whose shell has not reported its directory (OSC 7, from its
prompt), and the title says which. A name already taken there is left alone.

## The client itself

| Chord | What it does |
|---|---|
| `Ctrl+Shift+P` | The command palette: everything this client does, found by typing part of its name. Each entry shows its chord, so this is also how you learn them. |
| `Ctrl+Shift+,` | Opens the settings window. Each change is written to [the settings file](SETTINGS.md) as you make it, and applied at once. |
| `Ctrl+Shift+R` | Rereads [the settings file](SETTINGS.md). Saving it is normally enough; this is what to press when it was not. |
| `Ctrl+Shift+I` | Imports sessions from another client, into a folder of their own; the sessions you already have stay. |
| `Ctrl+Shift+F1` | Help: opens the palette, which lists everything this client can do and the keys that do it. The diagnostic report is in there too, as *Write a diagnostic report*. |

## In the file browser

The browser is a window of its own, so none of its keys is taken from the program in the
terminal, and each is written on the button that does the same thing. They are the keys two-pane
file tools on this platform have always used:

- `F5` copies the selection into the directory the other side is showing, after saying where.
- `F2` renames, and `F7` makes a directory.
- `F8` or `Delete` deletes, after saying how many entries and whether any is a directory.
- `Alt+Enter` sets the mode of the selected entry, and `Ctrl+R` lists the directory again.
- `Enter` opens a directory, `Backspace` goes up, and `Alt+Left` and `Alt+Right` go back and forward.

Files dragged from Explorer onto either side are copied into the directory that side is showing,
without a question first, since you aimed the drop; a name already there is still asked about.

## Why they all look like that

**Two modifiers, almost always.** A terminal owes the program on the far side everything it can
give it, and a chord with two modifiers is one almost nothing binds. That is why the client's own
actions are on `Ctrl+Shift+…` rather than on the single-modifier chords a desktop application would
normally take.

**`Alt` and not `Ctrl` for the digits.** `Ctrl` with a digit is a control character a host has
meanings for. `Alt` with one is not.

**`Ctrl+Tab` is the exception, and it is taken anyway.** A full-screen program could plausibly want
it. A client where you cannot leave the tab you are in has no tabs, so this one is spent knowingly.

**The chords on punctuation follow your keyboard layout.** `Ctrl+Shift+\`, `Ctrl+Shift+-` and
`Ctrl+Shift+,` are bound to the character and not to a key: whichever key types that character
without Shift on the layout you are using is the one that works. On a layout where the character
needs AltGr or Shift, that chord does not exist — Windows has no key for it to be — and the command
is in the palette (`Ctrl+Shift+P`) instead. Every layout installed on the machine the tests run on
is checked against this.

## What is deliberately not taken

- **`Ctrl+C`.** It is how a person stops a runaway program. A client that took it would have taken
  away the thing they reach for at the exact moment something has gone wrong. Copy is
  `Ctrl+Shift+C` and always will be.
- **`F1` on its own.** An unmodified function key belongs to the program on the far side.
- **`PageUp` and `PageDown` on their own.** A pager and an editor both bind them, and taking them
  would mean the terminal scrolled while the thing on screen did not.
- **Anything with no modifier at all.** Those are the remote program's, without exception.
