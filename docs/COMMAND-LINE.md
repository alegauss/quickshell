# The command line

This client has no menu, on purpose. That makes the command line a real surface rather than a
convenience: it is how a shortcut, a script or another program asks the client for anything. Every
flag it acts on is on this page, and nothing else is — a test reads the client's own list and fails
when the two differ.

```
quickshell.exe --session Work/build-box --trace
quickshell.exe --tabs 3 --panes 2
quickshell.exe --install --all-users --quiet
```

There is no `--help`. This is a windowed program with no console to print one into, and a help flag
that opened a dialog would be the client putting a window in front of a script. This page is the
help.

## Opening things

| Flag | Takes | What it does |
|---|---|---|
| `--tabs` | `<n>` | Opens that many tabs, up to sixteen, as pressing Ctrl+Shift+T that many times would. |
| `--panes` | `<n>` | Splits the first tab that many ways, up to sixteen, side by side. |
| `--broadcast` | | Types into every pane of the first tab at once, after the split. |
| `--session` | `<path>` | Opens a saved session in a tab of its own, by its path in the session store. A folder's path opens every session in it as panes of one tab, typing into all of them. |
| `--trace` | | With `--session`: records that session's negotiation and channels in a log of its own, for this run only. |
| `--import` | `[file]` | Previews importing MobaXterm's sessions from the file named; with no file, MobaXterm's from where it keeps them and PuTTY's, each previewed on its own, or a file you choose where neither is found. Nothing is written until you agree. Each import is added under a folder of its own, named for the client and the date, and the sessions already in your store stay. |
| `--browse` | | Opens the file browser. |
| `--palette` | | Opens the palette. |
| `--home` | | Opens Home, with quick connect and your recent sessions, over the first tab. |
| `--startup-report` | `<file>` | Times this start and writes the milestones to the file once the shell is on screen. |
| `--frame-report` | `<file>` | Writes, every five seconds, how often the render loop woke and why, the frames it drew and the bytes hosts sent, so an idle window's cost comes with its cause. |

## Installing and removing

These open no terminal window. Each answers with an exit code: zero is done, 740 is the Windows code
for needing an administrator, and one is anything else that did not happen.

| Flag | Takes | What it does |
|---|---|---|
| `--install` | | Installs this copy for the current user, or for everyone with --all-users, and opens no window. |
| `--uninstall` | | Removes an installed copy, and asks whether to keep the settings unless --quiet. |
| `--all-users` | | With --install or --uninstall: for every user of the machine, which needs an administrator. |
| `--quiet` | | With --install or --uninstall: asks nothing and shows nothing; the exit code is the answer. |

## Serving an agent

`--mcp` is how an agent's host starts the agent gateway: a Model Context Protocol server on standard
input and output, with no window. It exits when its input ends. Without the `agentGateway` setting
([SETTINGS.md](SETTINGS.md#agentgateway)) it writes why to standard error and exits with 2.

| Flag | Takes | What it does |
|---|---|---|
| `--mcp` | | Serves the agent gateway to an agent such as Claude Code on standard input and output, and opens no window. Refused unless the agentGateway setting is on, and unguarded: no policy limits what runs yet. |
