# Set aside

## Block A — A session that stays up, or says why it did not

- ⏸ **QS40** (deps: QS38 ✅, QS39 ✅) **This client has met one server, so an appliance that negotiates differently is an unknown** — set aside (Needs guest sshd, an appliance, a server licence.): Windows OpenSSH, a network appliance and a commercial server are still unmet, since no container stands for them. → §QS40

## Block B — Keys, agents, and the host you think you reached

- ⏸ **QS114** (deps: QS41 ✅) **A PuTTY user on Pageant older than 0.78 has an agent this client cannot reach at all** — set aside (Needs a real Pageant installed.): It has met only a window answering as Pageant does, since no real Pageant is installed here or in the guest. → §QS114
- ⏸ **QS43** (deps: QS41 ✅, QS114 ✅) **A key already unlocked in an agent must be typed again, and a hardware key cannot be used at all** — set aside (Needs a hardware token.): Both carriers now reach an agent, and no key on a hardware token has yet signed a session through either. → §QS43
- ⏸ **QS45** (deps: QS43 ✅) **Nothing forwards an agent, and nothing would stop a compromised host from using one if it did** — set aside (Needs a decision: upstream, library or non-goal.): SSH.NET 2026.0 has no agent forwarding at all, and adding it here means writing protocol a non-goal forbids. → §QS45

## Block C — Emulation that does not lie about the remote

- ⏸ **QS92** (deps: —) **The golden suite has run on NVIDIA and WARP and never on AMD, Intel integrated graphics or under RDP** — set aside (Waits for an AMD desk, an Intel integrated one and an RDP session; the guest has no GPU.): A driver bug is exactly what the machine that wrote the code cannot see. → §QS92
- ⏸ **QS212** (deps: —) **Dark text on a light theme carries 12 to 17 % less ink than Windows draws, while light-on-dark carries more** — set aside (Owner's call: symmetric or Windows' weight.): The linear blend weighs both polarities alike and Direct2D does not; matching it gives up QS9's symmetry. → §QS212

## Block D — The tree a user organises work in

## Block E — SCP and SFTP as a thing a person operates

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

## Block H — The reason to leave the incumbent

## Block I — An error a user can act on

## Block J — Leaving MobaXterm, proven by the switch

## Block K — The build and the harness — what a green run is evidence of

- ⏸ **QS210** (deps: —) **The fuzzing campaign runs only when somebody types run-fuzz.cmd** — set aside (Waits for the owner to choose this desk, the guest or CI.): QS102's search is continuous only on a schedule, and where it runs is a standing change the owner has to choose. → §QS210
