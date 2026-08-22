# SshProbe

Six questions about SSH.NET, each answered by a run against a live OpenSSH 9.6 server. The record
is [docs/measurements/ssh-net-probe.md](../../docs/measurements/ssh-net-probe.md); the verdict is
in [docs/DECISIONS.md](../../docs/DECISIONS.md).

```
sh prototypes/SshProbe/fixture/up.sh          # keys, a CA, a signed cert, two containers
dotnet run --project prototypes/SshProbe -- prototypes/SshProbe/fixture/keys <output-dir>
docker compose -f prototypes/SshProbe/fixture/compose.yaml down
```

`up.sh` is safe to re-run: it makes the keys and the certificate only if they are missing.

**`fixture/keys/` is not committed and must not be.** It holds a private key and a signing CA.
They authorise nothing but two throwaway containers on loopback, and that is exactly the habit
worth not forming — `up.sh` makes fresh ones in seconds.

The fixture is built so the answers can be wrong. `certonly` has no `authorized_keys` at all, so a
connection that succeeds for it succeeded on the certificate and nothing else; without the
certificate the same connection is refused. `twofactor` is under
`AuthenticationMethods publickey,keyboard-interactive`. `jump` reaches `target` only over the
compose network, so the jump-host answer cannot be satisfied by the target's published port.
