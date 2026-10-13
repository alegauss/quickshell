# quickshell

**SSH on Windows, without the suite around it.** A free SSH client for Windows 10 and 11 that
does the part of MobaXterm most people open it for (sessions, keys, SFTP, tunnels, tabs and
split panes) and leaves out the X server, the Cygwin userland and the rest of the bundle.

The terminal is drawn on the GPU (Direct3D 11 and DirectWrite, the whole grid in one draw call),
and a window with nothing happening in it draws nothing at all. Every performance claim in this
repository has a measurement behind it, with the machine, the date and the command that
produced it.

[Website](https://alegauss.github.io/quickshell/) ·
[Install](docs/INSTALL.md) ·
[Sessions](docs/SESSIONS.md) ·
[Keys](docs/KEYS.md) ·
[Command line](docs/COMMAND-LINE.md) ·
[Performance budget](docs/PERFORMANCE.md) ·
[Roadmap](docs/ROADMAP.md)

> **Status: pre-release, version 0.1.0.** No release has been published yet. The SSH client,
> SFTP, port forwards, tabs, split panes and the MobaXterm/PuTTY importers work today; the
> installer is not signed yet, so Windows SmartScreen warns on first start. What is done and
> what is still open is in the [roadmap](docs/ROADMAP.md) and the [changelog](docs/CHANGELOG.md).

## Coming from MobaXterm

Import your sessions once and keep working:

```
quickshell.exe --import
```

Or `Ctrl+Shift+I` inside the client. MobaXterm session files and PuTTY's saved sessions are
both read. A session type quickshell doesn't support (RDP, VNC, serial and so on) is named in
the import report instead of being dropped silently. Known issue: a key file that MobaXterm
stored as `_ProfileDir_\...` is not found after import yet, so point the session at the key
again ([QS276](docs/ROADMAP.md)).

Your `~/.ssh/config` is read live as well, `ProxyJump` and `Include` included, so hosts you
already defined there appear without being retyped.

## quickshell vs MobaXterm

MobaXterm is a suite. quickshell is an SSH client. The table shows both sides: what you gain,
and what you give up if you used MobaXterm for more than SSH.

### Price and licence limits

| | quickshell | MobaXterm Home | MobaXterm Professional |
|---|---|---|---|
| Price | Free | Free | $69 per user |
| Saved sessions | Unlimited | 12 | Unlimited |
| SSH tunnels | Unlimited | 2 | Unlimited |
| Deploying it across a company | Allowed: `--install --all-users --quiet` | Not allowed by its licence | Allowed |
| Source code | Public, on GitHub, MIT licence | Closed | Closed |

MobaXterm limits are from [its download page](https://mobaxterm.mobatek.net/download.html)
and [Home Edition licence](https://mobaxterm.mobatek.net/license.html), read 2026-10-12.

### SSH and files

| | quickshell | MobaXterm |
|---|---|---|
| Password, public key, keyboard-interactive, 2FA | Yes | Yes |
| Windows OpenSSH agent and Pageant | Yes | Yes |
| Host key check | Yes, against your own OpenSSH `known_hosts`; a changed key is refused, with no accept button | Yes |
| Jump hosts / `ProxyJump`, multi-hop | Yes | Yes |
| Local, remote and SOCKS forwards | Yes; they come back after a reconnect | Yes |
| SFTP browser | Yes, on the same connection, no second login | Yes |
| Edit a remote file in your local editor | Yes | Yes |
| Tabs and split panes | Yes, up to 16 panes per tab | Yes |
| Type into several sessions at once | Yes (`Ctrl+Shift+B`) | Yes (MultiExec) |
| Portable copy that writes nothing to your profile | Yes | Yes |

### What quickshell does not do

These are decisions, published as [non-goals](docs/ROADMAP.md), not gaps waiting to be
filled. If you depend on one of them, keep MobaXterm for that job.

| | quickshell | MobaXterm |
|---|---|---|
| X11 server / X11 forwarding | No | Yes |
| RDP, VNC, Telnet, serial, FTP | No | Yes |
| Cygwin / Unix tools on Windows | No | Yes |
| Macro recorder | No | Yes |
| Local shells | `cmd.exe` today; PowerShell, WSL and Git Bash profiles are not there yet | Bash, `cmd`, PowerShell, WSL |
| SSH agent forwarding | No | Yes |

### Measured side by side

Same machine (Intel Core i7-14700, RTX 4060, Windows 11 Pro), same sitting, same observer for
both programs.

| | quickshell | MobaXterm | Run |
|---|---|---|---|
| Start to window shown, warm, median of 10 | **612 ms** | 894 ms | [startup-h.md](benchmarks/results/startup-h.md), 2026-10-10 |
| CPU time of an idle empty window, over 10 minutes | **0 ms** | 3,625 ms | [idle-h.md](benchmarks/results/idle-h.md), 2026-08-31 |
| Private memory, empty window | 79.4 MB | **62.4 MB** | [idle-h.md](benchmarks/results/idle-h.md), 2026-08-31 |

MobaXterm uses less memory: about 79 MB of quickshell's figure is the .NET runtime and the WPF
window before any session exists, and another ~47 MB is the graphics driver's share once
Direct3D is opened. One connected session plus a local tab holds 142 to 145 MB. The budget for
that is 160 MB, and [PERFORMANCE.md](docs/PERFORMANCE.md) explains why.

The desk was not idle during the start-time runs. Because both programs were timed in the same
sitting, the comparison holds better than either number on its own.

## What's inside

- **Terminal.** A parser that allocates nothing per byte, Unicode 17 widths, grapheme clusters,
  emoji and CJK fallback, true colour, all mouse modes, bracketed paste, IME and a screen
  reader. vttest draws 28 of 31 screens the way xterm does; the other three are 132-column
  screens, a non-goal.
- **Renderer.** Direct3D 11 with a DirectWrite glyph atlas, ClearType, optional ligatures,
  any installed font, and iTerm2 or Windows Terminal colour schemes.
- **Files.** SFTP transfers show progress, cancel, and resume from where they stopped, refusing
  to resume when the partial file doesn't match the source. A local and a remote folder can be
  compared and mirrored. Files drag in from Explorer.
- **Every action in one place.** `Ctrl+Shift+P` opens a command palette that reaches anything the
  client can do.
- **Safety with a hostile host.** `OSC 52` clipboard access is off by default and write-only
  when on. The window title is never reported back to the host. A multi-line paste asks first.
- **Secrets.** Passwords go to Windows Credential Manager. Logs are redacted and never contain a
  secret, so you can attach one to a bug report.
- **Diagnostics.** Connection traces, a crash report that tells you where it is, and a
  diagnostics bundle for bug reports. Nothing leaves your machine on its own: no telemetry, no
  update check, no account.

## Install

There is no release yet. Until there is one:

- **Build it**: needs Windows x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).
  `run-tests.cmd` builds everything and runs the tests; `run.cmd Release` builds the client and
  starts it.
- **CI builds** an unsigned `quickshell-<version>-win-x64.zip` on every push, kept for five days
  under the workflow run's artifacts.

The archive carries its own .NET runtime. Unzip it anywhere and it runs as a portable copy, or
install it for yourself with `quickshell.exe --install` (no administrator prompt) or for every
user with `--install --all-users`. [INSTALL.md](docs/INSTALL.md) lists everything it does to
your machine, which is not much.

## How this repository works

- `src/Quickshell.Terminal`: the parser and the screen buffer, with no graphics and no network.
- `src/Quickshell.Render`: Direct3D 11.
- `src/Quickshell.Transport`: SSH (through [SSH.NET](https://github.com/sshnet/SSH.NET)), SFTP,
  forwards and ConPTY.
- `src/Quickshell.App`: the WPF client, the only project allowed to reference the other three.
- `benchmarks/`: the replay corpus, the harnesses and every committed result.

`docs/PERFORMANCE.md` sets six figures before the code they bind, and `run-perf-gate.cmd` fails a
release that regresses one. The roadmap, changelog and decision records are written by
[roadkeep](https://github.com/alegauss/roadkeep), not by hand.

## More tools by alegauss

For Claude Code:

- **[Claude Code Tray](https://alegauss.github.io/claude-tray/)**: your rate-limit usage as a Windows tray icon that turns red before you run out.
- **[roadkeep](https://alegauss.github.io/roadkeep/)**: keeps ROADMAP, CHANGELOG and IMPROVEMENTS to one format, as a CLI and a Claude Code plugin.
- **[commitclerk](https://alegauss.github.io/commitclerk/)**: Conventional Commits messages from your staged diff, in a single-file Python CLI.
- **[polyweave](https://alegauss.github.io/polyweave/)**: a Claude Code plugin that makes a game's 3D assets, sounds and names by declaring what each has to satisfy.

For Windows developers:

- **[winwright](https://alegauss.github.io/winwright/)**: drives a Windows desktop app from a test and reports what was actually observed.
- **[FreeWilly](https://alegauss.github.io/freewilly/)**: Docker on Windows without Docker Desktop, with no admin prompt and no telemetry.

All of them: [alegauss.github.io](https://alegauss.github.io/)

## Licence

MIT: see [LICENSE](LICENSE). The libraries the release carries beside it, each under MIT or ISC, are
credited in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Sponsor

Sponsored by [Viglet](https://www.viglet.org): open source search and content management.
