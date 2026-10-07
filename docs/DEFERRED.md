# Set aside

## Block A — A session that stays up, or says why it did not

- ⏸ **QS40** (deps: QS38 ✅, QS39 ✅) **This client has met one server, so an appliance that negotiates differently is an unknown** — set aside (Needs guest sshd, an appliance, a server licence.): Windows OpenSSH, a network appliance and a commercial server are still unmet, since no container stands for them. → §QS40

## Block B — Keys, agents, and the host you think you reached

- ⏸ **QS114** (deps: QS41 ✅) **A PuTTY user on Pageant older than 0.78 has an agent this client cannot reach at all** — set aside (Needs a real Pageant installed.): It has met only a window answering as Pageant does, since no real Pageant is installed here or in the guest. → §QS114
- ⏸ **QS43** (deps: QS41 ✅, QS114 ✅) **A key already unlocked in an agent must be typed again, and a hardware key cannot be used at all** — set aside (Needs a hardware token.): Both carriers now reach an agent, and no key on a hardware token has yet signed a session through either. → §QS43
- ⏸ **QS45** (deps: QS43 ✅) **Nothing forwards an agent, and nothing would stop a compromised host from using one if it did** — set aside (Needs a decision: upstream, library or non-goal.): SSH.NET 2026.0 has no agent forwarding at all, and adding it here means writing protocol a non-goal forbids. → §QS45
- ⏸ **QS115** (deps: QS44 ✅) **A master password is stretched by a function a graphics card is good at, where the design asked for one it is not** — set aside (Needs a person to accept the dependency.): .NET 10 still ships no Argon2 or scrypt, so closing it is accepting a third-party KDF. → §QS115

## Block C — Emulation that does not lie about the remote

- ⏸ **QS92** (deps: —) **The golden suite has run on NVIDIA and WARP and never on AMD, Intel integrated graphics or under RDP** — set aside (Waits for an AMD desk, an Intel integrated one and an RDP session; the guest has no GPU.): A driver bug is exactly what the machine that wrote the code cannot see. → §QS92
- ⏸ **QS212** (deps: —) **Dark text on a light theme carries 12 to 17 % less ink than Windows draws, while light-on-dark carries more** — set aside (Owner's call: symmetric or Windows' weight.): The linear blend weighs both polarities alike and Direct2D does not; matching it gives up QS9's symmetry. → §QS212
- ⏸ **QS215** (deps: —) **The contrast test's Direct2D reference sometimes draws nothing in the guest, and the failure reads as infinite ink** — set aside (Waits on a guest run with a blank.): No guest run has yet recorded a blank since, so whether it was an unflushed read or a lost draw is still unknown. → §QS215

## Block D — The tree a user organises work in

## Block E — SCP and SFTP as a thing a person operates

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

## Block H — The reason to leave the incumbent

- ⏸ **QS137** (deps: QS76 ✅) **The idle figure is measured on a window with no session and no render loop in it** — set aside (Needs the reference desk attended for ten connected minutes.): The client can now hold a session, and the figure is still the empty window's. → §QS137

## Block I — An error a user can act on

- ⏸ **QS128** (deps: QS71 ✅) **A trace shows what this client offered and never what the server did** — set aside (A decision: parse KEXINIT here or wait.): SSH.NET 2026.0 raises the server's KEXINIT only on an internal session event no caller can reach in time, so the rest is reading it ourselves. → §QS128

## Block J — Leaving MobaXterm, proven by the switch

- ⏸ **QS81** (deps: QS80 ✅, QS116 ✅, QS126 ✅, QS217, QS218, QS219) **A user weighing the switch has nothing that says what they will and will not get** — set aside (Needs MobaXterm in the guest.): Its figures need MobaXterm measured where it may run, and the build it describes still lacks a password prompt. → §QS81

## Block K — The build and the harness — what a green run is evidence of

- ⏸ **QS210** (deps: —) **The fuzzing campaign runs only when somebody types run-fuzz.cmd** — set aside (Waits for the owner to choose this desk, the guest or CI.): QS102's search is continuous only on a schedule, and where it runs is a standing change the owner has to choose. → §QS210
