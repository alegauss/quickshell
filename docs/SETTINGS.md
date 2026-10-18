# Settings

Everything this client can be configured to do, and nothing it cannot. If a key is not on this page
it is not a setting — it is a decision, and changing it takes an argument rather than a preference.

**The file is the setting.** It lives at `%AppData%\quickshell\settings.json`, or at
`data\settings.json` beside the executable in portable mode. It is text, you may edit it by hand, and
you may put it under version control — which is the point. When this client writes it back it edits
the values in place, so your comments, your blank lines and your spacing survive.

Saved sessions are a file of their own, `sessions.json` beside this one, and
[SESSIONS.md](SESSIONS.md) is its reference.

A key you leave out is that key at its default. A key this build has never heard of is carried
through untouched, so a newer build's settings survive being opened by an older one.

**Saving it is enough.** The client watches the file and applies what you saved — no restart, and no
button. It waits a moment for the file to stop moving first, because one save arrives as several
writes and reading between them would find a file that is half there.

**Ctrl+Shift+,** opens a window over the same file: one control per key below, each written to the
file as you change it. The file stays the setting — the window is a way of typing into it, and it
will not write over a file that does not parse. Anything in the file this client could not use — a
misspelt key, a value of the wrong kind, a scheme path that leads nowhere — is named at the top of
that window, so a typo is not mistaken for a setting that does not work.

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
  "colourScheme": "",
  "sessionsSidebar": true,
  "startWith": "Home"
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

This is the shape a program gets when it says nothing. A program can ask for another while it runs
(DECSCUSR) — an editor switching to a bar in insert mode is the usual one — and that shape is drawn
until the program asks for the default back or the terminal is reset. Whether the cursor blinks
stays `cursorBlink`'s to decide, whatever the program asked.

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
report (*Write a diagnostic report*, in the palette), because *the text went invisible* is a support question and that is where
support questions are answered.

A path that leads nowhere, or a file this cannot read, is the built-in scheme and no error. This is
a value somebody typed, so it is the likeliest line in the file to have a typo in it, and refusing
to start over one would be refusing at the worst possible moment.

### `sessionsSidebar`

Whether the saved sessions are listed down the left of the window, or folded to a narrow rail.

`true` by default. The fold at the top of the sidebar sets it, so a window opens the way the last
one was left; every open window follows. Folded, the rail keeps **Show sessions**, **Files on this
server** and **Tunnels** as buttons, and the terminal takes the width.

### `startWith`

What a new window opens on: Home, or a local shell.

`Home` (the default) or `Shell`. `Home`: quick connect, the sessions you connected to last, and the ways to start, with
the sessions sidebar beside them. No shell is started until you ask for one. `Shell` opens a local
shell in a tab instead, as quickshell did before Home. A window started with `--session`, `--tabs`
or `--panes` opens on what it asked for, whichever this says.

### `vaultFile`

The KeePass vault the saved passwords are written to again whenever one changes. Empty, the default, keeps none.

*Export saved passwords to a KeePass vault* sets it when **Keep this file up to date** is ticked
([SESSIONS.md](SESSIONS.md#exporting-saved-passwords)). From then on the vault is written again
whenever a password is remembered, changed or forgotten, or a session is added, renamed or removed,
without asking for the passphrase: the vault's derived key, never the passphrase, is kept sealed for
your Windows account. Emptying this forgets that key. Typing a path here does not start keeping
one, because the client has no key for it; export to it once instead.

A vault changed outside the client since it was last written — an entry edited in KeePassXC — is
not written over: the client says so, naming the file, and leaves it until you export again.

### `tagColours`

Which tags a tab shows besides prod, and in which colour, as staging=caution, db=accent. Empty, the default, shows prod alone.

A tag is given one of the theme's own colours, `critical`, `caution`, `success` or `accent`, never a
hex, so no scheme or theme can make it unreadable. A tab shows up to two coloured tags of its session,
its folders' tags included, after its title, and a count for the rest. `prod` is `critical` without
being named here; naming it gives it another colour. An entry that does not read is said in the
settings window and ignored.

### `agentGateway`

Whether an agent such as Claude Code may run commands on your saved sessions through quickshell --mcp. Off by default. What it may run on each is the agent policy's to say.

With it on, an agent's host can start `quickshell --mcp` ([COMMAND-LINE.md](COMMAND-LINE.md#serving-an-agent))
and call four tools: `hosts_list`, which names your saved sessions and never a password or a key;
`exec`, which runs a program with its arguments on one of them over a connection the gateway keeps
for the next call, and answers its exit code, output and errors apart, with nothing in it able to wait
for a person, and a question it would have asked, such as sudo's password, named; `read_file`, which reads a
text file there with its line numbers; and `edit_file`, which replaces one exact piece of a text
file, keeps the original beside it as `<file>.qs-bak-<time>`, and answers the diff. `job_start`,
`job_status`, `job_output` and `job_cancel` run a long command detached on the server, kept under
`~/.quickshell/jobs` there, so it outlives the call and the connection and can be read back later.
A long output comes back as its first and last lines within a budget, and `output_page` and
`output_grep` reach the rest without running the command again. Passwords, tokens, keys and the like in
anything a tool returns are replaced with `«redacted:…»` before the agent sees them; that guards against
a slip, not a determined command, which can always encode a value past it. Every call is recorded in
`audit` in quickshell's folder, and `quickshell --audit` prints what a run changed, ran, refused and
read ([COMMAND-LINE.md](COMMAND-LINE.md#serving-an-agent)). Each run also appears in quickshell, while it is open, as a read-only tab badged `agent` that shows every
call and what came back. `Ctrl+Shift+F12` stops every agent at once,
cancelling the jobs they started, until *Let agents run again* in the palette ([KEYS.md](KEYS.md)). It signs in with the session's
key and your agents, never a typed password, and refuses a host whose key you have not already trusted
from the window. It is a separate process: the window, its start and its memory are the same with
it on or off.

Every call that reaches a server is judged first by the agent policy, `agent-policy.json` in
quickshell's folder. With no such file every session is at *operate*: what can be shown to be a read
runs (`df`, `journalctl`, `systemctl status`, and the list the client ships), and anything else asks you
in a quickshell dialog that shows each command as its words, its session, its tags and an edit's diff:
*Run it*, *Run it and allow these for* 30 minutes, an hour or four hours (written into the policy as a
grant), or *Refuse* with a note the agent reads. With no quickshell window open, or no answer within ten
minutes, the call is refused and the agent is told why. Once an agent has read text from a server (a file,
a log, a command's output other than the machine's own state such as `uptime` or `df`), every change asks
you, even one a grant allows, because that text may be telling it what to do. The agent's tab says when
this happens, and *Trust what agents have read* in the palette lifts it. A change to `agent-policy.json`
made outside quickshell, in an editor or by a script, holds every call until you accept or reject it in
the dialog quickshell shows with the change. A session tagged `prod` is at *observe*: reads only. In
the file you set levels by tag or by session path (`deny`, `observe`, `operate`), and grants that
expire within four hours: named commands on named sessions, or `"level": "break-glass"` for
everything. `certificateMinutes` (1 to 60, 5 by default) is how long a certificate signed for an
agent lasts, on a server set up to take it in on one. The design (`docs/design/agent-gateway/README.md`)
gives the format with an example.

### `agentCertificateHello`

Whether Windows Hello asks for you before quickshell signs an agent a certificate. Off by default; on, an agent signs in only while you are at the desk to answer.

On a session set up to take its agent in on a certificate (`AgentCertificate`, [SESSIONS.md](SESSIONS.md)),
the gateway asks quickshell's window before each signing, and the window shows the Windows Hello prompt
over itself, naming the session and the account. The gateway signs once per connection, so the prompt
comes once per session per agent run, not once per command. With no window open, or Hello declined or
not set up for your account, nothing is signed and the agent is told why. Without it, any program
running as you can have quickshell sign a certificate; that certificate still opens only the account
the server limited.

### `claudeCommand`

What Open Claude Code runs in its pane, found on the path: claude by default.

*Open Claude Code* in the palette opens a local tab running this, through the command processor,
with the agent gateway as its MCP server and `--agent-hook` installed (both written into quickshell's
folder each time). *Send to Claude* types the selection, or the screen, of the pane with the keyboard
into it, under a line naming the session and its tags. Both need `agentGateway` on.

Below the settings, *Install the Claude Code plugin…* is offered where this command is found on the
path. It writes a plugin into `claude-plugin` in quickshell's folder: the gateway as an MCP server,
`--agent-hook` before every shell command and file write, and a skill saying which gateway tool does
what. It then installs the plugin for your user through `claude plugin`, after showing every command
it will run. Install it again after updating quickshell. A plugin written by another version is
refused by the gateway, which says so.

*Install into WSL…* beside it does the same for a Claude Code inside WSL2, the setup the agent gateway's
design recommends on Windows. It looks in your WSL distributions for `claude` only when you press it,
and writes the plugin naming quickshell by the `/mnt` path that distribution uses. Turn Claude Code's
sandbox on there yourself; the plugin does not change your Claude Code settings.

### `menuBar`

When the menu bar of every action is on screen: Hidden, Alt (shown by pressing and releasing Alt) or Always (the default).

The bar lists what the palette lists, read off the same bindings, under Session, Edit, View, Terminal,
Tab and Pane, Transfer, Forward, Agents and Help, each action with its keys beside it. An action
that cannot run now is shown disabled rather than left out. With `Always`, the default, it sits under
the title bar, and pressing and releasing Alt alone puts the keyboard on its first heading. With `Alt`,
a window opens with no bar: an Alt tap shows it with the keyboard on its first heading, and Esc,
choosing an item or clicking elsewhere puts it away. Alt held with another key is still that chord,
sent to the terminal as before. *Let Alt show the menu bar* in the palette writes `Alt`, and *Keep the
menu bar on screen* writes `Always` again.

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
