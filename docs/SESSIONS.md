# Sessions

Every field a saved session can have, what it means, and what a folder hands down. If a field is not
on this page it is not part of the store.

**The file is yours.** It lives at `%AppData%\quickshell\sessions.json`, at `data\sessions.json`
beside a portable copy, or in the folder `QUICKSHELL_DATA` names ([INSTALL.md](INSTALL.md)). It is
indented JSON, it accepts comments and trailing commas, and when this client writes it back your
comments stay beside the session or field they stood before. Field names are read in any case and
written as this page spells them. **New session**, **Open session** and **Open session, recorded** in
the palette ([KEYS.md](KEYS.md)) are ways into it; editing it by hand is equally first-class.

**The sessions sidebar** down the left of the window lists the store as its tree: a folder with what
it hands down beside it, a session with its address, and **Connected** on each one a tab holds.
Folders start folded, so the tree opens on its top level. Double-click or Enter connects; one
click, Enter or the arrows open or fold a folder; the search box finds a session by name,
host or tag, as **Open session** does. Right-click a session to edit, duplicate, move or delete it,
and a folder to make a session in it. Ctrl+click adds or removes a row, Shift+click takes the rows
between, and Ctrl+A takes every row listed; a chosen folder stands for every session under it, at
any depth, for anything that acts on a set of sessions, such as **Document as PDF…** in the menu
([below](#documenting-sessions)). A plain click goes back to one row. The fold at its top narrows it to a rail
([SETTINGS.md](SETTINGS.md), `sessionsSidebar`). A hand edit shows the next time you come back to the
window.

**Recent sessions** are the ones whose connection last went live, newest first, twenty at most, kept
in `recent.json` beside the store so they survive a restart. Home's cards show the first three with
when each connected, and **Open session** offers them first. A session that was asked for and never
connected is not added. Renaming, moving or deleting a session from the client carries its entry or
drops it; the file is the client's own, and one that will not read is started again empty.

**Quick connect** — *Connect to an address* in the palette — reaches a host nobody has saved. It
takes `host`, `user@host`, `user@host:port` or `[v6-address]:port`, and offers the hosts in your
`~/.ssh/config` as you type; a config host connects with the HostName, User, Port, first
IdentityFile and ProxyJump the config gives it, and what you typed wins over the config, as on
`ssh`'s command line. **Sign in with** is the session dialog's choice, as a drop-down: **Key or agent
(automatic)**, **Password**, or **A specific key file…**, which asks for the file there and then.
**Save as a session**, on by
default, writes it at the top of the store named after the host (with a number after it if that
name is taken by another session) and opens it from there; off, the same session is opened and
nothing is written. An address that does not parse says why under the box.

**Home** — *Home* in the palette — puts quick connect on the screen, with the three sessions you
opened last in this window as cards (the store's first three before you have opened any), each with
**Connect**, and three ways to start: import from MobaXterm, a local shell, and the file browser. A
card whose session is already open in a tab offers **Go to tab** instead, which puts that tab on
screen (the one you looked at last, where several hold it, and the card says how many), with
**New connection** beside it for a second shell on the same host; a double click on a card does what
its main button does. The sidebar does the same: double-clicking a session that is open goes to its
tab, and its menu offers **Go to tab** and **New connection** in place of **Connect**.

**Importing** — Ctrl+Shift+I, *Import sessions from another client* in the palette, or the card on
Home — reads MobaXterm's `MobaXterm.ini` from where MobaXterm keeps it and PuTTY's sessions from the
registry, and previews each before anything is written; where neither is found, it asks for the
file, which a portable MobaXterm keeps beside `MobaXterm.exe`. SSH sessions come across with their
host, port, user, key and jump host, in MobaXterm's folders, nested as they were, under a folder
named for the client and the day. What a session carried that has nowhere to go here is listed in
the preview and written as a comment above that session in this file. A MobaXterm folder named
exactly `prod`, `production` or `prd` comes across tagged `prod`, which the preview says before
anything is written; a name that only contains the word is left untagged. PuTTY keeps no folders,
so its sessions bring no tags.
It takes the terminal's place without closing anything; choose a tab to go back, and the **Home**
button at the start of the tab strip to return. With Home open, closing the last tab goes back to
Home instead of closing the window.

**A connected session shows where it goes.** While the pane with the keyboard holds a saved session,
a bar over the terminal names the session, its `user@host:port` and the jump host it goes through,
and a chip saying **Connected**, **Reconnecting** or **Disconnected**. Each running forward has a chip
of its own, which opens **Show forwards**; **Files** browses the server, **Split** opens a pane beside
it on the same server, over a connection of its own, the chevron beside it offering **Split right**
and **Split down** (`Ctrl+Shift+\` and `Ctrl+Shift+-`), and **…** holds the rest of what the palette offers for a session. A bar along the bottom says
the same connection in a line, whether it reconnects after a drop, and the encoding, terminal type
and grid size. A local shell has neither bar.

**A field this page does not list is refused, not skipped.** A misspelt name, or a setting such as
`Port` written beside `Host` instead of inside `Settings`, stops the store from opening with a
message naming the field, its line and where it belongs; nothing in the file is changed. Skipped,
the same slip would quietly connect on the defaults: as you, to port 22.

**It is a tree.** A node with a `Host` is a session; a node with `Children` is a folder. A folder's
`Settings` are inherited by everything under it unless something lower down sets the same field, and
the session dialog says beside every field whether it is set there or inherited, and from which folder;
**set here instead** takes an inherited value over, and **use … instead** gives it back.

**The session dialog** asks for the host and its port, a name and a folder, and who the session signs
in as. **Sign in with** is a choice of three: **Key or agent** (your keys in `~/.ssh`, then the
Windows agent and Pageant, which is also where MobaXterm's MobAgent answers, then a password if
those are refused), **Password** (asked for when it connects, or kept
under the saved credential you name), or **Key file** (that one file). It writes `Key` and `Credential`
exactly as you would by hand. Jump host, Tunnels, After login and Terminal are folded, each saying what
it holds. **Test connection** connects, signs in and closes again, and says what it reached or why not,
without saving anything or starting any forward; **Save and connect** saves a new session and opens it.
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
node's name is empty; where a file gives it one, the name is kept and is never part of a path.

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
folders' tags too, and carries them: tag a folder `prod` and every session under it is production.
`prod` is the one tag the client gives a meaning to: a tab holding such a session shows **prod** after
its title in the theme's danger colour, so the session you are about to type into says which
environment it is. Every tag of the session in a tab, its folders' included, is also published as the
tab's item type, which a screen reader reads.

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

A private key file. `~` means your profile, and so do MobaXterm's `_ProfileDir_`, which an import
expands; `_MyDocuments_`, `_AppDataDir_` and `_CurrentDrive_` are read too. A key the session names
that is not there is said in the pane before the connection goes on without it. When nothing sets it, the keys OpenSSH looks for by default
are offered — `~/.ssh/id_ed25519`, `id_ecdsa`, `id_rsa` — and the Windows OpenSSH agent where it runs;
then the server's own questions are put to you.

### `JumpHost`

A host to go through, written as OpenSSH writes it: `[user@]host[:port]`, and more than one with a
comma between them, gone through in order — `jump@bastion,ops@inner:2200`. A hop that names no user
signs in as the session's. Each jump host is offered your keys alone; its questions would be asked
as though they were the target's, so none are put to you.

### `Reconnect`

`true` to connect again after the link drops, keeping the scrollback; `false` or unset to end the
session there. **Off unless something sets it**, deliberately: an unexpected new login is an event on
plenty of hosts. A program running on the host does not survive a reconnect — the shell is a new one.
The session dialog's **Reconnect after a drop** sets it.

### `AgentUser`

The account the agent gateway signs in as on this server, in place of `User`. The person's own tabs
never use it. It is meant to name an account set up for an agent on the server, limited there by its
key's options and a sudoers allowlist, so that what the client's agent policy refuses, the server
refuses too ([the design](design/agent-gateway/README.md)). Set on a folder, every session in it
uses the same agent account.

### `AgentKey`

The private key the agent gateway signs in with as `AgentUser`, and the only one it offers: not your
agents, and not the keys OpenSSH looks for. A path, as `Key` is.

### `Scheme`

The colour scheme this session's pane wears, in place of the window's: a scheme file, named as the
`scheme` setting names one in [SETTINGS.md](SETTINGS.md), with a relative path read from beside this
file. A path that does not read leaves the pane in the window's scheme.

### `Credential`

The **name** of a saved credential, never the secret itself — which is what makes this file safe to
commit or share. A session that names one is offered the password remembered under that name, and a
password remembered while signing in to it is kept under that name, so every session naming it shares
one: change it once and every host that uses it has the new one. A session that names none remembers
a password for its own host, as before. Either way the password lives in Windows Credential Manager,
where it can be seen and removed.

### `FontSize`

**Not applied, by design.** Every pane in a window shares one font (the `fontSize` setting in
[SETTINGS.md](SETTINGS.md)), so a size for one session has nowhere to go. The session dialog does not
offer it. A value already in the file is kept as it is when the dialog saves, and has no effect.

### `TerminalType`

What the terminal claims to be when the shell opens, in place of `xterm-256color` — `vt100` or
`screen-256color` for a host whose terminfo has no `xterm-256color`. Letters, digits and `-+._` only;
anything else is not sent, and `xterm-256color` is. The emulator is the same whatever this claims, so
a type promising less than it does is safe and one promising more is not.

### `Scrollback`

How many lines of history this session's pane keeps, in place of the `scrollback` setting of
[SETTINGS.md](SETTINGS.md).

## Exporting saved passwords

*Export saved passwords to a KeePass vault*, in the command palette (`Ctrl+Shift+P`, or **Commands**
on the tab strip), writes every saved session to a KeePass database (`.kdbx`, KDBX 4) that KeePassXC,
KeePass, Strongbox or KeePassDX opens: the file to consult when you forget a password, on any machine.

Each session becomes one entry in a group named for its folder: the session's name as the title, the
login and `ssh://user@host:port` it connects as, and the password remembered for it, under its
`Credential` where it names one. A session with nothing remembered is exported too, with an empty
password, so the vault lists every place you connect to.

Windows Hello (your PIN, face or fingerprint) is asked before any password is read; without Windows
Hello set up for your account nothing is exported. The vault is protected by a passphrase alone, and
anyone who copies the file can guess at it for as long as it exists, so the dialog offers six random
words to write down and refuses a passphrase whose estimated strength is below 64 bits, saying so.
A portable copy remembers no passwords and has nothing to export.

Tick **Include the key files sessions name** and each private key a session's `Key` names goes into
the vault as an attachment, with its `.pub` beside it: once, on the first session that names it, the
others saying where it is. A key without a passphrase of its own is then protected by the vault's
passphrase alone. A key file that is not there is named when the export finishes, and the export goes
ahead without it.

Tick **Keep this file up to date** and the vault is written again whenever a password is remembered,
changed or forgotten, or a session changes, without asking for the passphrase again; the
`vaultFile` setting in [SETTINGS.md](SETTINGS.md#vaultfile) names it, and emptying it stops this. A
vault edited outside the client since it was last written is never written over.

## Documenting sessions

**Document as PDF…**, in the sidebar's menu for a session, a folder or several selected rows, and
*Document sessions as PDF* in the palette, write a PDF of those sessions: the ones selected, a folder
counting as every session under it, or every saved session when nothing is selected. It asks only
where to save.

A cover page names the date, the client's version and the folders covered. Each session then has a
page of its own: its folder, host, port and user; how it signs in, with the key file and its SHA256
fingerprint as `ssh-keygen -l` prints it, or the credential a password is saved under and whether one
is saved on this machine; its jump chain hop by hop, each with the account it signs in as; its
forwards as a table; whether it reconnects after a drop; the commands typed after login; and its
tags. Every value says where it comes from: the session itself, or the folder it inherits it from.

No password, passphrase or private key is ever written into it. A key's fingerprint is read from the
`.pub` beside it, or from the public half an OpenSSH or PuTTY key file carries unencrypted; a key in
another format, with no `.pub`, is documented with the reason it has no fingerprint.

## Importing saved passwords

*Import saved passwords from a KeePass vault*, in the palette, is the way back on a new machine or
after a reinstall, where Credential Manager starts empty. It reads a KeePass database, KDBX 4 or 3.1,
with its passphrase and key file if it has one, from this client or from KeePassXC or KeePass. Each
entry whose URL is `ssh://user@host:port`, or a bare `host` or `host:port` with the user in its
user name field, is matched to the saved sessions that connect to that host, port and login. Its
password is then remembered for them, under their `Credential` where they name one.

Before anything is saved you see how many passwords are new, how many are already remembered as
they are, and each one that differs from what is remembered, which stays as it is unless you tick
it. Entries that match no session are listed and left alone. A vault that was changed or damaged
after it was written is refused whole, naming the file.