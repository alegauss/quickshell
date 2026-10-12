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
- ⏸ **QS260** (deps: —) **A cursor blink redraws the whole grid, so a blinking cursor alone costs half a per cent of a core while idle** — set aside (Needs the reference desk and the operator's leave.): Core, GPU and photon cost per one-cell blink against a whole frame, measured on the reference desk. → §QS260

## Block D — The tree a user organises work in

## Block E — SCP and SFTP as a thing a person operates

- ⏸ **QS188** (deps: QS219 ✅) **Nothing can be dragged out of the host's pane, so a file on the server reaches Explorer only through a copy** — set aside (The guest has no SSH fixture to drag from.): A real drop onto Explorer is untried: no SSH tab has a remote pane until QS219, and then the guest drags one. → §QS188

## Block F — A forward is a lifecycle, not a checkbox

## Block G — The clean interface, defended

- ⏸ **QS257** (deps: QS225 ✅) **There is no full-screen mode, the one window shape whose echoes reach the glass an interval sooner** — set aside (Its UI case waits for the guest to resume.): A monitor-covering chain echoes at 24 ms against 33 windowed, and no client window has that shape. → §QS257
- ⏸ **QS272** (deps: QS268 ✅, QS271 ✅) **The window opens on a local Command Prompt, and nothing on screen says this client connects to servers** — set aside (Waits on the owner restating Block G's bare-window criterion and allowing a host gate run.): A new window still opens on a shell rather than on Home. → §QS272

## Block H — The reason to leave the incumbent

- ⏸ **QS78** (deps: QS139 ✅) **Nothing has run for longer than a working session, so a slow leak would reach users first** — set aside (The operator frees the machine for three days.): The 72-hour run, atlas and GPU watched, has not been run: it waits for a machine free for three days. → §QS78
- ⏸ **QS253** (deps: —) **The gate fails unchanged code, because the desk runs a seventh slower some days than when the baseline was taken** — set aside (Needs the reference desk with nothing else running.): A check on a quiet desk at an unchanged commit has not yet been seen to hold within the 10 % bound. → §QS253
- ⏸ **QS255** (deps: —) **An idle pane costs 0.6 % of a core and GPU work even with the cursor blink off, and four cost nearly three times one** — set aside (Needs ten quiet minutes on the reference desk.): With zero frames the pane still spends 0.16 % of a core on a busy desk, over the 0.1 % bound. → §QS255

## Block I — An error a user can act on

- ⏸ **QS128** (deps: QS71 ✅) **A trace shows what this client offered and never what the server did** — set aside (Waits for an SSH.NET release exposing it; 2026.0.0 is still newest (2026-10-09).): SSH.NET parses the server's KEXINIT and never surfaces it. → §QS128

## Block J — Leaving MobaXterm, proven by the switch

- ⏸ **QS81** (deps: QS80 ✅, QS116 ✅, QS126 ✅, QS217, QS218, QS219) **A user weighing the switch has nothing that says what they will and will not get** — set aside (Needs MobaXterm in the guest.): Its figures need MobaXterm measured where it may run, and the build it describes still lacks a password prompt. → §QS81
- ⏸ **QS285** (deps: —) **The repository declares no licence, so "free and open" cannot be said of it to anyone it is shown to** — set aside (The owner chooses the licence.): With no LICENSE file, all rights are reserved. → §QS285

## Block K — The build and the harness — what a green run is evidence of
