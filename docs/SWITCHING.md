# Switching from MobaXterm

This page is for somebody who works in MobaXterm today and is deciding whether to move to
quickshell. It says what comes with you, what quickshell does, what it deliberately does not do and
what to use instead, and how the two compare on one machine, with the run that produced every
number. Where MobaXterm is better, it says so.

## In one paragraph

quickshell is an SSH client and terminal for Windows, and only that: SSH sessions with their keys,
agents, jump hosts and forwards, a file browser over the same connection, tabs and split panes, and a
local shell. It draws the terminal on the GPU and does nothing while you are not typing. It does not
carry an X server, RDP, VNC, Telnet, serial, FTP or a Unix toolbox — if your day depends on any of
those, keep the tool you have for it, named below for each one.

## What comes with you

**Your saved sessions.** Press `Ctrl+Shift+I` (or *Import sessions from another client* in the
palette, or the card on Home). quickshell reads `MobaXterm.ini` from where MobaXterm keeps it — your
Documents, OneDrive's Documents or AppData — and PuTTY's sessions from the registry, and shows what
it would do before writing anything. A portable MobaXterm keeps its `MobaXterm.ini` beside
`MobaXterm.exe`; where none is found in the usual places, the import asks you for the file. The
same import runs from the command line as `quickshell.exe --import [file]`.

What carries over, for every SSH session:

- host, port and user;
- the private key, referenced where it lies — a `.ppk` included, which quickshell reads as it is
  and does not convert;
- the jump host, which quickshell reaches with your keys and agents and never with a password;
- MobaXterm's folders, nested as they were, under a folder named *Imported from MobaXterm* and the
  day. The sessions already in quickshell stay as they are.

What does not, and is named rather than dropped: sessions of a kind quickshell does not connect with
(RDP, VNC, Telnet, serial, FTP, Mosh, MobaXterm's local shells and the rest) are listed with the
reason; and any setting a session carried that has nowhere to go here — X11 forwarding, compression,
a macro to run after login, and any field the import does not recognise, by its position — is listed
in the preview with the sessions that had it, and written as a comment above each of those sessions
in `sessions.json`, so you find it beside the session later as well.

How long it takes: the preview reads the whole file at once, so it is one preview and one answer,
whether you have five sessions or five hundred. What takes time afterwards is what the preview names:
a session whose login macro you relied on gets the same commands in its **After login** field
(`PostLogin`), by hand.

**Your keys in an agent.** quickshell asks Windows' own OpenSSH agent and Pageant, through the window
every Pageant opens — tested against a real Pageant 0.77. MobAgent,
MobaXterm's agent, is made to answer as Pageant does, so keys loaded there should be offered while
MobaXterm runs; that one has not been tried with quickshell. Loaded into Pageant or the Windows
agent, they are.

**Your passwords** do not come across: MobaXterm keeps them in its own store. quickshell asks for a
password when a server wants one, and can keep it in Windows Credential Manager under a name you give
the session (`Credential`), where you can see and remove it with Windows' own tools.

## What quickshell does

- **SSH sessions**, saved in one JSON file you can read and edit (`%AppData%\quickshell\sessions.json`,
  or `data\` beside a portable copy), in folders that hand settings down — see [SESSIONS.md](SESSIONS.md).
- **Sign-in** by key file, agent (the Windows OpenSSH agent and Pageant), password, or a server's
  own questions such as a one-time code.
- **Jump hosts**, and **forwards**: local, remote and dynamic (SOCKS), started with the session.
- **A file browser** over the session's own connection (SFTP, or SCP where a server has no SFTP):
  two panes, copy, rename, delete, permissions, files dragged in from Explorer and out to Explorer.
- **Tabs and split panes**, a tab dragged out into a window of its own, and **typing into several
  panes at once** (MobaXterm's MultiExec) with `Ctrl+Shift+B`.
- **Quick connect** from an address or from `~/.ssh/config`, **Home** with the sessions you opened
  last, and **a command palette** (`Ctrl+Shift+P`) where every command is — there is no menu bar.
- **Reconnect** after a dropped link, per session; **a trace** of one session's negotiation when
  something will not connect; **a recording** of what a host sent.
- **A local shell** in a tab, whatever `COMSPEC` names (normally `cmd.exe`).
- **Colour schemes** from iTerm2 or Windows Terminal files, per window or per session.
- **Portable or installed**, for one user, without an administrator prompt — see [INSTALL.md](INSTALL.md).

## What it does not do, and what to use instead

These are decisions, written down in the roadmap's non-goals with the reason for each; none of them
is a feature that is merely late.

| MobaXterm has | quickshell | Use instead |
|---|---|---|
| A built-in X server, and X11 forwarding | No, and no forwarding | An X server of its own: VcXsrv or X410, with `ssh -X` from a terminal that has one; or WSLg for Linux programs on this machine. Or keep MobaXterm for the sessions that run X programs. |
| RDP | No | Windows' own Remote Desktop Connection (`mstsc`). |
| VNC | No | A VNC viewer: TigerVNC or RealVNC Viewer. |
| Telnet, serial (COM ports) | No | PuTTY, which does both. |
| FTP | No (SFTP and SCP only) | WinSCP or FileZilla. |
| Mosh | No | Mosh from inside WSL. |
| A Unix toolbox (Cygwin, BusyBox) and its local terminal | No; the local tab runs Windows' shell | WSL: type `wsl` in a local tab and its shell runs there. |
| Macros and scripting | No | A session's **After login** commands cover what a login macro did; anything more is a script on the server. |
| A password store of its own | No | Windows Credential Manager, per session (`Credential`). |
| Inline images in the terminal | No | — there is no equivalent here yet. |

## How they compare

Measured side by side in the same Windows 11 virtual machine (8 logical processors of an
Intel Core i7-14700, 8 GB), five starts of each, by what Windows says of each client's processes —
the same method for both, since MobaXterm reports nothing of its own. Medians; every row, the
builds, the method and the command that produced them are in
[measurements/mobaxterm.md](measurements/mobaxterm.md).

| | quickshell 0.1.0 | MobaXterm 26.5 Home |
|---|---|---|
| Start to first window | 1.09 s | 1.93 s |
| Private memory | 63 MB | 72.5 MB |
| Working set | 140.6 MB | **103.7 MB** |
| Processor while idle, window open | 0 % | 0.81 % of a core |

What these say and do not say:

- **Idle** is the one a laptop feels: quickshell's window, left alone, spent no measurable
  processor time in a minute; MobaXterm's spent most of a percent of a core, every minute it is
  open.
- **Start** is to the first window, which is each client's home screen, not a connected session.
- **Memory** goes both ways. MobaXterm's working set — everything Windows keeps resident for it —
  is about a quarter smaller. quickshell's private memory — what is its alone — is the smaller;
  the rest of its working set is shareable pages, such as the code of the libraries it loads.
- **Not compared: terminal throughput and input latency.** MobaXterm's terminal cannot be timed from
  outside without watching its screen, and a number taken one way for one client and another way for
  the other is not a comparison. quickshell's own are in [PERFORMANCE.md](PERFORMANCE.md) and
  [measurements/throughput.md](measurements/throughput.md), on a different machine.
- This is a virtual machine without a GPU of its own, so absolute figures are slower than on a real
  desk; the comparison is the point, not the numbers.

## What MobaXterm does better

- **One program for everything above.** If you use its X server, RDP, serial and SSH in the same
  day, MobaXterm is one window and quickshell is one of several.
- **Its working set is smaller** in the measurement above, by about a quarter, though quickshell's
  private memory is the smaller of the two.
- **A Unix toolbox on a machine without WSL.** Where WSL is not allowed, MobaXterm's bundled tools are
  the only Unix shell on the machine.
- **Sessions of every kind in one tree.** quickshell's tree holds SSH sessions and nothing else.
