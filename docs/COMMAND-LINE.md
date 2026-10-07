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
| `--session` | `<path>` | Opens a saved session in a tab of its own, by its path in the session store. |
| `--trace` | | With `--session`: records that session's negotiation and channels in a log of its own, for this run only. |
| `--import` | `[file]` | Previews importing MobaXterm's sessions, from the file named or from where MobaXterm keeps them; nothing is written until you agree. |
| `--browse` | | Opens the file browser. |
| `--palette` | | Opens the palette. |
| `--startup-report` | `<file>` | Times this start and writes the milestones to the file once the shell is on screen. |

## Installing and removing

These open no terminal window. Each answers with an exit code: zero is done, 740 is the Windows code
for needing an administrator, and one is anything else that did not happen.

| Flag | Takes | What it does |
|---|---|---|
| `--install` | | Installs this copy for the current user, or for everyone with --all-users, and opens no window. |
| `--uninstall` | | Removes an installed copy, and asks whether to keep the settings unless --quiet. |
| `--all-users` | | With --install or --uninstall: for every user of the machine, which needs an administrator. |
| `--quiet` | | With --install or --uninstall: asks nothing and shows nothing; the exit code is the answer. |
