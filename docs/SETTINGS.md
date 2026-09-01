# Settings

Everything this client can be configured to do, and nothing it cannot. If a key is not on this page
it is not a setting — it is a decision, and changing it takes an argument rather than a preference.

**The file is the setting.** It lives at `%AppData%\quickshell\settings.json`, or at
`data\settings.json` beside the executable in portable mode. It is text, you may edit it by hand, and
you may put it under version control — which is the point. When this client writes it back it edits
the values in place, so your comments, your blank lines and your spacing survive.

A key you leave out is that key at its default. A key this build has never heard of is carried
through untouched, so a newer build's settings survive being opened by an older one.

**Saving it is enough.** The client watches the file and applies what you saved — no restart, and no
button. It waits a moment for the file to stop moving first, because one save arrives as several
writes and reading between them would find a file that is half there.

If nothing happens, **Ctrl+Shift+R** rereads it. That is worth knowing about rather than a fallback
nobody should need: a settings file on a network share is one this client may not be able to watch,
and the chord works whether the watch armed or not.

```jsonc
{
  "schema": 1,

  // A dark terminal under light chrome is a perfectly reasonable thing to want.
  "theme": "System",
  "fontFamily": "Cascadia Mono",
  "fontSize": 12,
  "scrollback": 10000,
  "ligatures": true,
  "cursor": "Block",
  "cursorBlink": true,
  "warnOnPaste": true,
  "colourScheme": ""
}
```

## The keys

### `schema`

Which version of this file's format the client wrote. Read on load and written back as whatever this
build uses; a file from an older build is migrated forward and backed up beside itself first.

Not something to set by hand. It is here because a file with no version is a file whose format is
frozen by the first person who ran it.

### `theme`

`System`, `Light` or `Dark`. How the **chrome** is painted — the title bar, the tab strip, the
dialogs. `System` follows Windows while the client is running rather than reading it once at
start-up.

This is not the terminal's colours. A user with a favourite scheme wants it under either chrome, and
conflating the two is how a client throws away a choice somebody made deliberately.

### `fontFamily`

The terminal's typeface, by name. Anything installed on the machine.

A face without a fixed advance will still render, and every cell will still be the same width,
because a terminal's grid is a grid. Choosing a proportional font is choosing to have it look wrong.

### `fontSize`

Its size in points. The grid follows: a bigger font in the same window is fewer columns, and the
program on the far end is told.

### `scrollback`

How many lines each session keeps once they have left the screen. Ten thousand by default.

It is per session and not per window, so a window with four panes holds four of these. The cost is
memory and it is roughly the width of your terminal times this number times a cell.

### `ligatures`

Whether the font's ligatures are formed — whether `!=` is drawn as two characters or as one.

Exposed because users are sincerely divided about it and neither answer is wrong enough to decide for
them. It changes nothing about what the terminal thinks is on screen: the cells are the same cells,
and a ligature is drawn across them.

### `cursor`

`Block`, `Underline` or `Bar`.

A host may ask for a different shape while it runs, and today this client does not listen — so this
is the shape, not a starting point. When it starts listening this becomes what a host that says
nothing gets.

### `cursorBlink`

Whether the cursor blinks.

Turning it off is worth knowing about: a blinking cursor is a change on screen twice a second, so a
window showing one draws twice a second by design. With it off, a window whose hosts are silent draws
nothing at all until a byte arrives.

### `warnOnPaste`

Whether a paste carrying a newline is shown before any of it is sent.

**What turning this off costs.** Pasted text runs the moment it contains a newline, and what you read
on a web page and what your clipboard holds are not obliged to match. This client asks only when the
program on the far side has not turned bracketed paste on — a modern shell decides for itself, and
then you are never asked. So turning this off is a decision about the hosts you work with, and it
removes the only check that exists for the ones that do not.

Control characters are stripped from a paste either way. That is not a setting.

### `colourScheme`

A path to a scheme file. Empty — the default — is the built-in scheme.

**Both formats that already circulate are read**, and which one a file is is decided by what is in
it rather than by what it is called:

- **iTerm2**, the `.itermcolors` property list. Nearly every scheme published anywhere exists in
  this form.
- **Windows Terminal**, the JSON fragment out of its `schemes` array — the object with `"name"`,
  `"background"`, `"black"`, `"brightWhite"` and the rest. Paste it into a file of its own and point
  this at it. Both `purple` and `magenta` are accepted for the same colour, since Microsoft's
  schemes use the first name and everybody else's use the second.

**A relative path is relative to this file**, not to wherever the client was started from. A scheme
sitting beside your settings is the arrangement that survives being cloned onto another machine,
which is the point of the file being text you can commit.

**What a scheme sets.** Nineteen colours: the sixteen a terminal numbers, the default foreground and
background, and the cursor. The 6·6·6 cube and the greyscale ramp above index 15 are left alone —
every terminal derives those the same way, and a scheme that changed them would make a 256-colour
program look wrong here and nowhere else.

**What it does not set.** The selection colour, which both formats carry and this client ignores. A
selected cell has its two colours swapped instead. A fixed highlight is a colour chosen without
knowing the scheme it will sit on, and over a blue scheme a blue highlight selects text into
invisibility.

**Where a file leaves the cursor out, it becomes the foreground.** That is a rule and not a guess:
the foreground has to be legible against the background, so a cursor taking it is legible too.

**Applying a scheme repaints what is already on screen**, scrollback included. Nothing needs
reopening.

**Contrast is reported, never enforced.** A scheme is loaded as written even where some of it is
hard to read — many are that way deliberately. Which colours those are is named in the diagnostic
report (**Ctrl+Shift+F1**), because *the text went invisible* is a support question and that is where
support questions are answered.

A path that leads nowhere, or a file this cannot read, is the built-in scheme and no error. This is
a value somebody typed, so it is the likeliest line in the file to have a typo in it, and refusing
to start over one would be refusing at the worst possible moment.

## What is deliberately not here

- **Anything to reach parity with another client.** A feature comparison is not an argument; a user's
  task is.
- **The reported terminal type.** It is `xterm-256color`, and a client that let you change it would be
  a client that lets you tell a host something untrue about what it is talking to.
- **Keybindings.** Not yet configurable. The chords this client takes from the remote program are
  written down in [the keys reference](KEYS.md), which is what that page is for.
- **A scheme gallery.** The client ships one scheme and reads the rest from files. A bundled
  collection is a maintenance burden and an invitation to screenshots, and an import path makes it
  unnecessary.
