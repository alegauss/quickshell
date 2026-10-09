# Sessions

Every field a saved session can have, what it means, and what a folder hands down. If a field is not
on this page it is not part of the store.

**The file is yours.** It lives at `%AppData%\quickshell\sessions.json`, at `data\sessions.json`
beside a portable copy, or in the folder `QUICKSHELL_DATA` names ([INSTALL.md](INSTALL.md)). It is
indented JSON, it accepts comments and trailing commas, and when this client writes it back your
comments stay beside the session or field they stood before. Field names are read in any case and
written as this page spells them. **New session**, **Open session** and **Open session, recorded** in
the palette ([KEYS.md](KEYS.md)) are ways into it; editing it by hand is equally first-class.

**It is a tree.** A node with a `Host` is a session; a node with `Children` is a folder. A folder's
`Settings` are inherited by everything under it unless something lower down sets the same field, and
the session dialog says beside every field whether it is set there or inherited, and from which folder.
Two things are never inherited, on purpose: what a session types after login, and its forwards.

```jsonc
{
  "Name": "",
  "Children": [
    {
      // Every host in Work logs in as deploy, through the bastion, and comes back after a drop.
      "Name": "Work",
      "Settings": { "User": "deploy", "JumpHost": "bastion.example.com", "Reconnect": true },
      "Children": [
        { "Name": "build-box", "Host": "build.example.com", "Tags": ["ci"] },
        {
          "Name": "db-primary",
          "Host": "db1.example.com",
          "Settings": { "Port": 2222 },
          "Forwards": [ { "Kind": "Local", "ListenPort": 5432, "TargetHost": "localhost", "TargetPort": 5432 } ]
        }
      ]
    },
    { "Name": "nas", "Host": "nas.home.arpa", "Settings": { "User": "admin", "Reconnect": false } }
  ]
}
```

## A node

### `Name`

What it is called, and how it is addressed: a session's path is its folders' names and its own,
joined with `/` (`Work/db-primary`), which is what `--session` and **Open session** take. The root
node's name is empty.

### `Host`

The machine to connect to. A node with one is a session; a node without one is a folder.

### `Settings`

The fields under [What a folder hands down](#what-a-folder-hands-down). Any of them may be left out,
which on a session means "what the folder says" and on a folder means "what the folder above says".

### `PostLogin`

Text sent to the shell once it is up, as though you had typed it, followed by Enter. A session's own
and **never inherited**: a folder that could set it would type into every host under it, including
ones added later by somebody who never saw it. The dialog refuses a command that carries a password —
save the password as a credential instead.

### `Tags`

Labels **Open session** finds a session by, beside its path and its host. A session is found by its
folders' tags too.

### `Forwards`

Port forwards that start with the session and stop with it. A session's own and **never inherited**:
a folder's forward handed to each session under it would be one port opened once per session, and
every one after the first would fail. **Show forwards** in the palette lists them while they run. Each
is an object:

| field | means |
|---|---|
| `Kind` | `Local` (OpenSSH's `-L`), `Remote` (`-R`) or `Dynamic` (`-D`, a SOCKS proxy) |
| `ListenPort` | the port listened on — here for `Local` and `Dynamic`, on the server for `Remote`; `0` lets that side choose |
| `TargetHost` | where a `Local` or `Remote` forward goes; unused by `Dynamic` |
| `TargetPort` | the port there |
| `Bind` | the address a `Local` or `Dynamic` forward accepts on; loopback when left out |

### `Children`

The nodes in a folder. A folder opened by its path opens every session in it as panes of one tab.

## What a folder hands down

Every field here may be set on a folder or a session, and the nearest one wins.

### `User`

The account to log in as. Your Windows user name when nothing sets it.

### `Port`

The SSH port. 22 when nothing sets it.

### `Key`

A private key file. `~` means your profile. When nothing sets it, the keys OpenSSH looks for by default
are offered — `~/.ssh/id_ed25519`, `id_ecdsa`, `id_rsa` — and the Windows OpenSSH agent where it runs;
then the server's own questions are put to you.

### `JumpHost`

A host to go through, written as OpenSSH writes it: `[user@]host[:port]`. The jump host is offered
your keys alone; its questions would be asked as though they were the target's, so none are put to
you.

### `Reconnect`

`true` to connect again after the link drops, keeping the scrollback; `false` or unset to end the
session there. **Off unless something sets it**, deliberately: an unexpected new login is an event on
plenty of hosts. A program running on the host does not survive a reconnect — the shell is a new one.
The session dialog's **Reconnect after a drop** sets it.

### `Scheme`

The colour scheme this session's pane wears, in place of the window's: a scheme file, named as the
`scheme` setting names one in [SETTINGS.md](SETTINGS.md), with a relative path read from beside this
file. A path that does not read leaves the pane in the window's scheme.

### `Credential`

The **name** of a saved credential in Windows Credential Manager, never the secret itself — which is
what makes this file safe to commit or share. **Not applied yet**: sign-in finds a remembered password
by the host it is for, not by this name (QS245).

### `FontSize`

A point size for this session's text. **Not applied yet**: every pane in a window shares one font
(QS245).

### `TerminalType`

What the terminal claims to be when the shell opens, in place of `xterm-256color` — `vt100` or
`screen-256color` for a host whose terminfo has no `xterm-256color`. Letters, digits and `-+._` only;
anything else is not sent, and `xterm-256color` is. The emulator is the same whatever this claims, so
a type promising less than it does is safe and one promising more is not.

### `Scrollback`

How many lines of history this session's pane keeps, in place of the `scrollback` setting of
[SETTINGS.md](SETTINGS.md).
