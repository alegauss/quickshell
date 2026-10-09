# Set aside

## Block A — A session that stays up, or says why it did not

- ⏸ **QS40** (deps: QS38 ✅, QS39 ✅) **This client has met one server, so an appliance that negotiates differently is an unknown** — set aside (Needs guest sshd, an appliance, a server licence.): Windows OpenSSH, a network appliance and a commercial server are still unmet, since no container stands for them. → §QS40

## Block B — Keys, agents, and the host you think you reached

- ⏸ **QS114** (deps: QS41 ✅) **A PuTTY user on Pageant older than 0.78 has an agent this client cannot reach at all** — set aside (Needs a real Pageant installed.): It has met only a window answering as Pageant does, since no real Pageant is installed here or in the guest. → §QS114
- ⏸ **QS43** (deps: QS41 ✅, QS114 ✅) **A key already unlocked in an agent must be typed again, and a hardware key cannot be used at all** — set aside (Needs a hardware token.): Both carriers now reach an agent, and no key on a hardware token has yet signed a session through either. → §QS43

## Block C — Emulation that does not lie about the remote

- ⏸ **QS92** (deps: —) **The golden suite has run on NVIDIA and WARP and never on AMD, Intel integrated graphics or under RDP** — set aside (Waits for an AMD desk, an Intel integrated one and an RDP session; the guest has no GPU.): A driver bug is exactly what the machine that wrote the code cannot see. → §QS92
- ⏸ **QS215** (deps: —) **The contrast test's Direct2D reference sometimes draws nothing in the guest, and the failure reads as infinite ink** — set aside (Waits on a guest run with a blank.): No guest run has yet recorded a blank since, so whether it was an unflushed read or a lost draw is still unknown. → §QS215
- ⏸ **QS153** (deps: QS29 ✅) **The text being composed is a box the input method draws over the grid, not underlined text inside it** — set aside (Needs a person typing through an IME.): Not yet watched with a real input method, which must stop drawing its box and still commit. → §QS153
- ⏸ **QS214** (deps: —) **Replaying the tmux resize recording sometimes allocates in steady state in the guest, and the failure says no more** — set aside (It waits for the guest to fail again.): Fix the sequence the failure now names when the guest next fails. → §QS214
- ⏸ **QS139** (deps: —) **A host sending faster than the parser consumes buffers gigabytes inside the channel** — set aside (Owner's next call: reflection, blocking or upstream.): No bound exists to bump to: 2026.0.0 is newest and SSH.NET's main has no change. → §QS139

## Block D — The tree a user organises work in

## Block E — SCP and SFTP as a thing a person operates

- ⏸ **QS188** (deps: QS219 ✅) **Nothing can be dragged out of the host's pane, so a file on the server reaches Explorer only through a copy** — set aside (The guest has no SSH fixture to drag from.): A real drop onto Explorer is untried: no SSH tab has a remote pane until QS219, and then the guest drags one. → §QS188

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

## Block H — The reason to leave the incumbent

- ⏸ **QS137** (deps: QS76 ✅) **The idle figure is measured on a window with no session and no render loop in it** — set aside (Needs the reference desk attended for ten connected minutes.): The client can now hold a session, and the figure is still the empty window's. → §QS137
- ⏸ **QS197** (deps: —) **The parse figure spreads by a fifth between runs on the reference machine, so the gate lets a regression that big pass** — set aside (Needs the reference desk attended.): The gate's baseline is not retaken with the steadier arm, which starts the client eleven times on the desk. → §QS197
- ⏸ **QS225** (deps: —) **An echo in a window covering its whole monitor has never been timed, the one case offered independent flip** — set aside (It covers the operator's screen; it waits for the user to clear the desk.): Only a monitor-covering chain can show the echo one interval sooner. → §QS225

## Block I — An error a user can act on

- ⏸ **QS128** (deps: QS71 ✅) **A trace shows what this client offered and never what the server did** — set aside (Waits for an SSH.NET release exposing it; 2026.0.0 is still newest (2026-10-09).): SSH.NET parses the server's KEXINIT and never surfaces it. → §QS128

## Block J — Leaving MobaXterm, proven by the switch

- ⏸ **QS81** (deps: QS80 ✅, QS116 ✅, QS126 ✅, QS217, QS218, QS219) **A user weighing the switch has nothing that says what they will and will not get** — set aside (Needs MobaXterm in the guest.): Its figures need MobaXterm measured where it may run, and the build it describes still lacks a password prompt. → §QS81

## Block K — The build and the harness — what a green run is evidence of
