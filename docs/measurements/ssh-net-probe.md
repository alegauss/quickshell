# SSH.NET against a live sshd — the six answers

Every line below came from a run against **OpenSSH 9.6p1 (Ubuntu 24.04)** in
[prototypes/SshProbe/fixture](../../prototypes/SshProbe/fixture), driven by
[prototypes/SshProbe](../../prototypes/SshProbe). **SSH.NET 2026.0.0.** Nothing here was
answered from documentation, which is what QS5 forbade.

The fixture is two servers on one network — `target` and `jump` — and it is built to be able to
fail. The `certonly` account has **no `.ssh` directory at all**: the server's only route in for it
is `TrustedUserCAKeys`, and the same connection without a certificate is refused with
*Permission denied (publickey)*. The `twofactor` account is under
`AuthenticationMethods publickey,keyboard-interactive`.

## The matrix

| Question | Answer | What the run did |
|---|---|---|
| A key held by the Windows OpenSSH agent, or Pageant? | **no** | The assembly exports four authentication methods — keyboard-interactive, none, password, private-key — and no exported type whose name mentions an agent. |
| An OpenSSH certificate? | **yes** | Connected as `certonly` with `PrivateKeyFile(key, null, cert)`; the server answered `id -un = certonly` on an account with no `authorized_keys`. |
| A connection carried inside another connection's channel? | **yes** | Opened a direct-tcpip channel on the `jump` connection to `target:22`, bound it locally, and completed a second handshake through it. Two different hostnames; the target's published port unused. |
| Keyboard-interactive with a second factor? | **yes** | Both methods in one `ConnectionInfo`; the server's `Password:` prompt answered from the handler; session reported `id -un = twofactor`. |
| Any of `~/.ssh/config`? | **no** | The OpenSSH client given the same alias file resolved it and answered `id -un = probe`. SSH.NET given the bare alias answered `SocketException: host not known`. No config-file type is exported. |
| What the shell stream sustains | **81–103 MB/s** | `cat` of a 64 MB printable-ASCII file through a `ShellStream`, three runs: 100.4, 103.3, 81.4 MB/s, allocating **112–126 KB per MB read**. |

## What closes the two gaps, and where it already lives

**The agent seam is reachable without a fork.** The run also asked how hard the missing piece
would be: `IPrivateKeySource` is exported and `PrivateKeyFile` is **not sealed**, so an
authentication method that signs through `\\.\pipe\openssh-ssh-agent` and through Pageant's
window-message protocol can be written against the public surface. That work is **QS43**, which
the backlog already carries.

**Reading `~/.ssh/config` is ours entirely.** Host, port, user and key are constructor arguments;
nothing reads a file. Resolving `Host`/`HostName`/`Port`/`User`/`IdentityFile`/`ProxyJump` into a
`ConnectionInfo` before the library is called is **QS56**, also already filed.

Neither gap needed a new line, which is the useful part of asking before Block B starts.

## What this run did not settle

- **No live agent was exercised.** The `ssh-agent` service on this machine is stopped and starting
  it needs elevation, which this run did not have; the agent pipe was absent. So the *negative* is
  evidenced by the library's own surface, and the positive control — an agent holding a key that
  the CLI can use and SSH.NET cannot — was not run.
- **The throughput number is loopback.** Both endpoints are containers on this machine, so it
  measures the library and the local stack and says nothing about a real link. It is also not the
  400 MB/s figure in [PERFORMANCE.md](../PERFORMANCE.md): that one is the *parser*, measured
  headless, and this is the transport handing it bytes.
- **The allocation figure is process-wide**, taken with `GC.GetTotalAllocatedBytes(precise: true)`
  around the read. The first version of this probe used the per-thread counter and reported zero,
  because SSH.NET reads on its own threads — 0 KB/MB was measuring the probe's own loop. ~120 KB
  allocated per MB carried is the transport's real cost and is a number Block C inherits.

## Verdict

**Proceed with SSH.NET, with named work.** Three of the four capability questions are already yes
against a modern sshd, including the two that would have been most expensive to discover late —
certificates and jump hosts. The two nos are additive rather than structural: neither needs the
library replaced, both have a public seam, and both are lines the backlog already carries.

No second candidate is evaluated. That conclusion would need one of the yeses to be a no.
