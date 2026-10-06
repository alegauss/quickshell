# Compatibility matrix

Servers that are not the fixture's OpenSSH 9.6, connected to deliberately (QS40): an
interoperability failure is found by connecting to an unusual server and by no other method.
Each row is what was agreed, from the session log at trace level, then a full-screen program run
through the shell. A row saying only that it worked would have been visited, not tested.

Measured by `tests/Quickshell.Transport.Tests/CompatibilityMatrixTests.cs` against the containers
in `prototypes/SshProbe/matrix` (`up.sh`), the 9.6 row against the fixture's target. Each row skips
by name where its container is not up. Reference machine, 2026-10-06, SSH.NET 2026.0.0.

| server | version string | kex | host key | cipher | mac | `top` | RSA key |
|---|---|---|---|---|---|---|---|
| OpenSSH 9.6, Ubuntu 24.04 | `OpenSSH_9.6p1 Ubuntu-3ubuntu13.19` | sntrup761x25519-sha512 | ssh-ed25519 | aes128-ctr | hmac-sha2-256 | draws | (QS41) |
| OpenSSH 8.2, Ubuntu 20.04 | `OpenSSH_8.2p1 Ubuntu-4ubuntu0.13` | curve25519-sha256 | ssh-ed25519 | aes128-ctr | hmac-sha2-256 | draws | accepted |
| OpenSSH 7.2, Ubuntu 16.04 | `OpenSSH_7.2p2 Ubuntu-4ubuntu2.10` | curve25519-sha256 | ssh-ed25519 | aes128-ctr | hmac-sha2-256 | draws | accepted |
| OpenSSH 6.6, Ubuntu 14.04 | `OpenSSH_6.6.1p1 Ubuntu-2ubuntu2.13` | curve25519-sha256 | ssh-ed25519 | aes128-ctr | hmac-sha2-256 | draws | accepted |
| Dropbear 2024.85, Alpine 3.20 | `dropbear_2024.85` | curve25519-sha256 | ssh-ed25519 | aes128-ctr | hmac-sha2-256 | draws (BusyBox `top`) | accepted |

"Draws" means the program's screen arrived as cursor addressing (`ESC [ H`) with its own text in
it, through the same channel a session reads.

## Reading it

- **Nothing failed in these rows.** OpenSSH 6.6 is the far side of the SHA-1 line, where an RSA key
  can only sign as `ssh-rsa`, and the library still signs that way for a server that offers nothing
  newer. A client that had dropped SHA-1 outright would be refused there, with a message about the
  key rather than the server's age.
- **The cipher is aes128-ctr on every row**, which is the library's preference rather than any
  server's: every row offers chacha20-poly1305 as well, and every OpenSSH row AES-GCM (read from
  each server's own KEXINIT with `ssh -vvv`; the library does not report what a server offered). It is a choice this client makes
  and could revisit, not a limit a server imposed.
- **The key exchange is curve25519 everywhere below 9.6**, and the post-quantum hybrid where the
  server has it.

## Not in the matrix yet

The design names three rows that no container stands for: **Windows OpenSSH**, where the far side
is `cmd` or PowerShell; **a network appliance** (Cisco IOS or similar: old algorithms, a shell that
is not a shell); and **a commercial server**. They stay on QS40.
