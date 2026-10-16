# The agent gateway — design

Block L's design: a way for a coding agent such as Claude Code to work on the servers saved in
quickshell, through quickshell, instead of through an SSH key handed to it. This document is where
the block matures. It opens with the threat model (QS320) because every later section is judged
against it; the sections after it are written by the lines that own them, listed at the end.

## What is at stake

People already give an agent an SSH key and let it run commands on servers. These are the harms,
roughly in the order they happen in practice:

1. **The wrong host.** The agent runs a correct command on production, believing it is staging,
   because the two have similar names or the same user.
2. **A secret carried out.** The agent reads an `.env`, a private key or a token on the server,
   and the output lands in its transcript, which goes to the model provider and may be logged or
   shared.
3. **A command it was talked into.** Text on the server, in a log, a README, a web page the
   server fetched or a file somebody planted, tells the agent to run something (prompt
   injection), and it does.
4. **Reach beyond the task.** The key that opens one host opens every host that trusts it, and an
   agent with a shell can add its own key, a cron job or a service, so the access outlives the
   session.
5. **The rules rewritten.** An agent that can write files can edit whatever limits it, if that
   lives anywhere it can write.
6. **A hang, a flood, a mess.** A sudo prompt, a pager or an interactive installer stalls a
   command until it times out; a large output fills the agent's context; a half-finished edit
   leaves a file broken with no copy to go back to.

The first three are the ones that hurt. The fourth is what turns a mistake into an incident. The
fifth decides whether any of the rest can be relied on, and the sixth is why an agent would use a
gateway at all.

## Who causes it

- **The agent, by mistake.** The commonest cause, and the one most controls are good at.
- **Text the agent read.** An injected instruction acts with the agent's authority. Nothing on
  the client can tell a command the agent meant from one it was talked into. The design treats
  every byte read from a server as untrusted input (QS338).
- **Another process running as the same Windows user.** It can read what quickshell can read and
  drive what quickshell drives. Nothing in this design stops it, and the document does not claim
  otherwise (see the second layer).

A person deliberately using the agent to attack their own servers is out of scope: they hold the
credentials anyway.

## Three layers, each assuming the one above has failed

### 1. Steering: hooks and a skill

A Claude Code plugin installs a `PreToolUse` hook and a skill (QS332, QS343). The hook sends
`ssh`, `scp`, `plink` and their kin to the gateway's tools, and the agent's sandbox denies outbound
port 22, so the gateway is the easy way out and the direct way is closed.

**What it stops:** accidents. An agent that reaches for `ssh` out of habit gets the gateway.

**What it does not stop:** intent. A hook sees the tool call, not every way a process can open a
socket; a language runtime, a proxy or a binary the agent writes can route around both the hook
and the sandbox's port rule. Steering is a convenience that makes the safe path the default. It
is not a boundary.

### 2. Credential isolation: quickshell holds the keys

The agent never holds a credential. Keys, agent identities and saved passwords stay with
quickshell, which authenticates on the agent's behalf over its own kept-alive connection (QS325).
An agent without a key has nothing to authenticate with if it bypasses the gateway.

**What it stops:** the agent carrying a credential away, using it outside the session, or
reaching hosts that are not saved sessions.

**What it does not stop:** anything running as the same Windows user. Credential Manager and DPAPI
answer to the account, not to the process (see `SecretStore.WhatThisDoesNotProtect`), so a process
that runs as the user can ask for the same secrets quickshell asks for, or drive quickshell itself.
This raises the cost of going around the gateway from "run `ssh`" to "write code that reads
Credential Manager", which matters against an agent that is mistaken or nudged. It is not a wall
against one that is determined.

### 3. The server: an account that cannot do the harm

The server enforces what the client only asks. A dedicated account for the agent, a `sudoers`
allowlist naming the commands it may run as root, `authorized_keys` options (`from=`,
`no-port-forwarding`, `no-agent-forwarding`, and `command=` where one command is the whole job),
and certificates signed for minutes rather than keys valid for years (QS340, QS341).

**What it stops:** everything the account is not allowed to do, whatever the client was talked
into. It is the only layer an agent on the client cannot route around, because it is not on the
client.

**What it does not stop:** what the account is allowed to do. An agent with a `sudoers` line for
`systemctl restart` can restart the wrong service. The server bounds the damage; the policy and
the approval in front of it (QS322 to QS324) decide what happens inside that bound.

## The sentence every later section keeps true

**A client-side policy without the server layer is advice.** The gateway's policy, its command
judging and its approvals are worth having: they stop the commonest harm, the agent's own mistake,
before it reaches a server, and they leave a record of what was run. But an agent that steps
around the gateway, or a process running as the user, is limited only by the account it lands in.
Every section below that describes a client-side control says which harm it addresses and that the
server layer is what makes it hold.

## Harms against layers

| Harm | Steering | Credentials in quickshell | Restricted server account |
|---|---|---|---|
| Wrong host | Tags and the tab badge make it visible (QS331, QS335) | — | A staging account cannot reach production at all |
| Secret carried out | — | The agent never holds keys | The account cannot read what it is not given |
| Injected command | Untrusted-content mode asks before mutating (QS338) | — | `sudoers` and `command=` refuse what is not listed |
| Reach beyond the task | The hook and port-22 denial keep it to the gateway | No key to reuse elsewhere | Short-lived certificates expire; the account cannot add keys |
| Rules rewritten | The hook denies writes to the policy file (QS339) | The policy lives outside any workspace the agent can write | The server's rules are not on the client |
| Hang, flood, mess | — | — | — (the gateway's own tools address these: QS326 to QS329) |

A dash means the layer does nothing for that harm. That is intended, and the reason there are
three layers.

## What an agent suffers with SSH today

Lowering friction is half the reason anyone would route through the gateway, so the pains are part
of the design:

- **Quoting through four shells.** The agent's own shell, `ssh`'s argument handling, the remote
  login shell and whatever the command itself parses. A command that works locally arrives mangled.
- **A handshake per command.** Each `ssh host cmd` negotiates again; twenty commands are twenty
  handshakes, and a jump host doubles them.
- **Prompts that hang.** `sudo`, a pager, `apt`'s dialogs, a host-key question: the command waits
  for a person who is not there until the agent's timeout, and the agent learns nothing about why.
- **Output that floods context.** One `journalctl` or `find /` costs the rest of the session's room.
- **Blind edits.** A heredoc or `sed -i` changes a file with no diff for the person and no copy to
  return to.
- **No record.** Once the session scrolls away, nobody can say what ran where.

The gateway's tools answer these one by one (QS325 to QS330, QS333) and are what make the safer
path the easier one.

## What this design does not try to do

- **Protect against the Windows account being compromised.** A process running as the user can
  do anything the user can, and no client-side design changes that.
- **Sandbox the agent's own machine.** That is the agent's sandbox and its host's job.
- **Replace the server's own controls.** The gateway helps set them up (QS340); it does not stand
  in for them.

## The policy an agent is held to (QS322)

A client-side control, so everything here is bounded by the sentence above: it stops the agent's
own mistakes before they reach a server, and the server's account is what makes it hold.

**Keyed on tags.** A rule names session tags (`prod`, `db`, `staging`, QS331), because the mistake
worth preventing is confusing two environments, and a tag is how a person already names one. A tag a
folder carries is carried by every session under it, as the store's other settings are. A glob over
session paths (`Work/web-*`) is the fallback for a store nobody has tagged.

**Three levels.**

| Level | A read | A mutation |
|---|---|---|
| `observe` | runs without asking | refused, with the rule named |
| `operate` | runs without asking | asks the person (QS324) |
| `break-glass` | runs without asking | runs without asking, and every call is badged and audited loudly |

What counts as a read is QS323's to say; whatever cannot be judged a read with certainty is a
mutation. A session no rule matches takes the policy's `default`, which is `deny` unless the person
wrote otherwise, and a session tagged `prod` that no rule names is `deny` whatever the default says.
Where several rules match, the narrowest level wins: a session tagged both `prod` and `db` gets the
stricter of the two.

**Every widening ends.** A grant lets named commands run without asking on named sessions, until a
time, for example restarting nginx on `Work/web-*` for thirty minutes. A grant with no `until`, or one
further away than four hours, is refused when it is written. `break-glass` is a grant in this sense
too: it cannot be a standing level. An expired grant is ignored, not deleted, so the audit (QS333)
can still say what it allowed.

**Only the person writes it.** The policy is `agent-policy.json` in quickshell's own folder
(`%AppData%\quickshell`, or `data\` in a portable copy), outside any repository the agent works in,
and is edited through quickshell's own window. The plugin's hook denies a write to that path
(QS339). quickshell watches the file as it watches its settings: a change it did not make suspends
the policy (every call asks) until the person confirms the change in a dialog that shows it. So an
agent that does write the file gains nothing: its edit makes the gateway stricter until somebody
looks.

**A worked example.**

```json
{
  "schema": 1,
  "default": "deny",
  "rules": [
    { "tags": ["staging"], "level": "operate" },
    { "tags": ["prod"], "level": "observe" },
    { "sessions": ["Work/scratch-*"], "level": "operate" }
  ],
  "grants": [
    {
      "sessions": ["Work/web-*"],
      "commands": [["systemctl", "restart", "nginx"]],
      "until": "2026-10-15T15:30:00-03:00",
      "why": "rolling the config change from ticket 4411"
    }
  ]
}
```

Here staging asks before anything changes, production can be read and nothing else, the scratch
boxes ask, and for half an hour nginx may be restarted on the web servers without asking. A grant's
commands are argument vectors, matched whole, never strings or patterns: `systemctl restart nginx`
does not admit `systemctl restart nginx; rm -rf /`, because the second is not that vector (QS323).
A top-level `"certificateMinutes"` (1 to 60, 5 when absent) sets how long a certificate signed for an
agent lasts (QS341); a value outside that refuses every call, as any policy that does not read does.

**Three questions this section cannot answer yet.**

1. Does a grant belong to the person or to one agent session? Two agents running at once would
   otherwise share a grant only one of them was given.
2. Does a jump host's tag constrain what is reached through it? A `prod` bastion in front of a
   `staging` host is a real arrangement, and either answer surprises somebody.
3. Should `observe` refuse reads of files that hold secrets (`/etc/shadow`, `~/.ssh`, `.env`)? Those
   are reads, and harm 2 is about exactly them. The redaction QS330 adds narrows the harm but does
   not remove it.

## Judging a command without pretending a regex is a firewall (QS323)

A blocklist of strings holds nothing: `find / -delete`, `python3 -c`, `sed -i`, a script written over
SFTP and then run, or a base64 blob piped to `sh` all do what `rm -rf` does. So the judgement starts
from the other end. A command is a **read** only if it can be shown to be one; everything else is a
**mutation**, and anything that cannot be settled with certainty asks the person. The two numbers
that matter follow from that: false allows, which must be zero, and false asks, the friction to drive
down.

**The parser is small on purpose.** Not a port of bash, not tree-sitter-bash (a native dependency for
a grammar far larger than the job), not `bash -n` (which checks syntax and returns no tree). A managed
parser in the gateway accepts a deliberately narrow subset and calls everything outside it uncertain:

- words: plain, single-quoted, and double-quoted with no `$(`, backtick or `${…}` inside;
- `$NAME` only as a whole argument, never as the command;
- pipelines, `&&`, `||` and `;` between simple commands;
- one redirection, `2>/dev/null`, since it writes nothing a person would miss.

A subshell, a brace group, a heredoc, process or command substitution, any other redirection, an
assignment before a command, a glob in the command position: uncertain, so asked. A real shell
accepts far more, and that is the point. The gateway does not have to understand every command an
agent might write, only to recognise with certainty the ones that are harmless.

**The allowlist is data, with arguments checked.** A JSON file shipped with the client and versioned
with it names each read: the command, the subcommands that are reads (`systemctl status`,
`is-active`, `show`, `list-units`; not `restart`), and the options that make it write (`find`:
`-delete`, `-exec`, `-execdir`, `-ok`, `-fprint`, `-fls`; `sort`: `-o`; `journalctl`: `--vacuum-*`,
`--rotate`, `--flush`). A simple command is a read when its name is listed, its subcommand is a
listed read, and none of its arguments is a listed writing option. Interpreters (`sh`, `bash`,
`python*`, `perl`, `awk`, `node`) and editors in the stream (`sed`, `tee`) are never reads, whatever
their arguments: what they do is in their argument, and judging that is the problem this section
refuses to pretend it has solved. A pipeline is a read only when every command in it is.

**Wrappers are unwrapped, narrowly.** `sudo` with no option other than `-n` or `-u <user>` is judged
by the command it runs; any other `sudo` option is uncertain. `env` followed only by `NAME=value`
pairs is judged by its command, except that setting `PATH`, `LD_PRELOAD`, `LD_LIBRARY_PATH` or any
`BASH_*` makes it uncertain. `timeout <duration>` and `nice` are unwrapped the same way; `nohup`,
`setsid` and `xargs` are not, since each changes what runs or for how long.

**Extended per tag, never globally by the agent.** The policy (QS322) may add reads for the sessions
a rule names, in the same shape as the shipped list (a site's own `myapp-status`, say), written by
the person as the rest of the policy is. Nothing the agent writes or says extends it.

**Measured, and gated.** The test suite holds two corpora: commands from real agent sessions,
labelled, and an adversarial set drawn from the ways tools are known to write or execute (the
GTFOBins catalogue is the starting point). A build where any command in either is judged a read
when it is not fails. False asks over the real corpus are reported as a number in the run, the one
this section's later changes are argued with.

## Approval that does not train a reflex (QS324)

Approval fatigue defeats every permission prompt: forty identical requests teach a person to
approve without reading, and the forty-first is the `DROP TABLE`. This section is written against
that habit, and like the policy it is a client-side control (the server account is what bounds a
mistaken yes).

**By plan, not by command.** The agent proposes a batch: the sessions, and for each the argument
vectors to run and the files to edit with their diffs. The person approves it once, and the
gateway runs exactly that and nothing else. The approval is bound to a SHA-256 of the batch in a
canonical form (session paths, then each argv as length-prefixed strings, then each edit's path and
diff), so a batch that changes by one byte after it was shown is a new request, never a silent
substitution. A batch is run in its order and stops at the first command that fails, saying which.

**Shown where it cannot be faked.** In quickshell's own dialog, never in the agent's terminal, where
the agent controls what is printed. The dialog shows each session by name and address with its tag,
a `prod` tag in the colour the client already uses for danger; each command as parsed into its
words, so quoting cannot hide a second command; each edit as a diff; and the batch's short hash,
which the agent's request also carries, so the person can match the two. Three answers: run it,
refuse it, or turn it into a grant (QS337), which asks for the grant's end before it is written.

**Rare by construction.** Reads at `observe` and `operate` never ask (QS323 decides what a read
is). A grant replaces a run of identical asks with one decision that ends. The target is at most
four asks per hour of agent work on a `prod` session, measured from the audit log (QS333): asks,
approvals, refusals and the seconds between showing and answering, per session tag. An answer in
under two seconds, repeatedly, is what a reflex looks like, and the number the section is judged by.

**Two answers the line left open.**

1. *Approval from somewhere other than the desk:* not now. Approving from a phone moves the
   decision away from the screen that shows the diff, and the dialog's whole value is that the
   person sees exactly what runs. If it is ever wanted, it is a separate line with its own threat
   model, not a setting.
2. *What the agent sees while it waits:* the tool call blocks and its progress says "waiting for
   approval in quickshell, plan `<short hash>`". After ten minutes with no answer it returns a named
   error, `approval timed out`, which the agent can report or retry. Silence is never consent: a
   batch nobody answered is a batch that did not run.

## Whether this belongs in the client (QS321)

**Yes, on four conditions, decided by the owner on 2026-10-15.** The README sells quickshell as the
SSH part of MobaXterm without the suite around it, and the non-goals refuse a plugin host and a
scripting engine in the MVP. The gateway is neither, and the conditions below are what keep it
that way. A change that breaks one of them reopens this decision; it does not get argued past it.

**Why it is in scope.** The gateway is not something quickshell hosts. It is quickshell offered as
a tool to an agent that already exists on the machine, for a task people already do badly: letting
an agent work on a server by handing it an SSH key that opens everything the key opens. The task is
the user's, and the client that already holds their sessions, their keys and their known hosts is
the place that can narrow it. No code from outside runs inside quickshell, and nothing the agent
sends extends what the client can do: it asks for the operations listed in the policy, and the
client decides.

**The conditions.**

1. **Off until the user turns it on.** A default installation exposes nothing, starts nothing and
   listens on nothing for an agent. Turning it on is a deliberate act in the settings, and turning
   it off again stops it.
2. **Its own process.** The gateway runs as `quickshell --mcp`, started by the agent's host over
   standard input and output, never inside the window's process. The window's start time, memory
   and idle figures in [PERFORMANCE.md](../../PERFORMANCE.md) are measured with it off and must not
   move with it on; Block L's criteria hold that figure.
3. **No network of its own.** It opens no listening port and reaches no service other than the SSH
   servers a session names. Its channels are the pipe the agent's host started it with, those
   connections, and the user's own desk, where an approval is shown (QS324); nothing else can reach
   it.
4. **Nothing on the terminal's path.** No code it adds runs between a keystroke and a pixel, or
   between bytes from a server and the emulator. A pane an agent drives is drawn by the same path as
   any other, and a pane it does not drive pays nothing for it.

**What this does not decide.** It does not open the door to plugins, scripts or macros in the window:
those remain non-goals, and the gateway's existence is not an argument for them.

## The spike: hosts_list and exec (QS325)

What shipped first, before any policy, and off unless the `agentGateway` setting is on.

**The process.** `quickshell --mcp` is a Model Context Protocol server on standard input and output,
one JSON-RPC message a line, started by the agent's host. It branches off before anything the window
needs is made, so it costs the window nothing (QS321's second condition). With the setting off it
writes why to standard error and exits with 2. Code: `src/Quickshell.App/AgentGateway.cs`.

**Two tools.** `hosts_list` answers each saved session's path, host, port, user and tags, never a
credential name or a key path. `exec` takes a session's path, an argument vector, an optional `cwd`
and a timeout (60 seconds by default, ten minutes at most), and answers `exitCode`, `stdout`,
`stderr` and `timedOut` apart. A connection that fails is a tool error the agent can read; a command
that exits non-zero is a result.

**Quoting once.** The vector is joined for a POSIX shell by `Argv.ForPosix` in the transport: a word
of safe characters goes bare, any other is single-quoted. That is the only layer between the agent and
the program, so a quote, a dollar sign or a newline arrives as itself (proven against the Linux
fixture). A Windows server's shell is not POSIX; a bare word reaches it intact, and anything quoted
does not, which a later line has to settle before such a server is offered.

**One connection per session.** Kept between calls over an exec channel of its own
(`ICommandChannel`, which stays off the `ISshTransport` seam), and replaced when it drops. It signs in
with the session's key and the agents, never a typed password, and refuses a host key the user's
known_hosts does not already hold, saying to connect once from the window.

**The server-side boundary (QS340).** `AgentAccountSetup.Plan` writes the setup for one
server as a POSIX script the person reads before anything runs, and its undo. The script makes the
account (`agent` by default) where it is not there, installs the agent key with `restrict` and
`from="<the address the server sees this machine connect from>"`, and writes
`/etc/sudoers.d/quickshell-agent` naming each allowed command as its program's full path on that
server (`command -v`) and its arguments, checked with `visudo -c` before it is put in place. A command
with no arguments is written with `""`, which in sudoers means none; a bare one would mean any. An
argument a sudoers line cannot hold exactly (a space, or any of `, : = \ * ? [`) is refused rather than
escaped into something the person did not read. A session's `AgentUser` and `AgentKey` (SESSIONS.md)
are the identity the gateway then signs in with, offering that key alone.

The guided half is *Set up an agent account…* on a session's menu, there only while `agentGateway` is
on (`MainWindow.AgentSetup.cs`). It opens the person's own tab on the server and, over that tab's
connection, reads the address the server sees (`SSH_CLIENT`); a Windows server is refused, since the
setup writes sudoers. It makes the agent's ed25519 key in `agent-keys` in quickshell's folder, one per
session, kept if one is there. The dialog (`AgentSetupDialog`) starts its list from the `sudo` commands
the policy's grants name for that session, and writes the exact script again on every change. While a
line cannot be written exactly, it says why and offers no Run. Run puts the script in the person's
home, readable by them alone, and types one line into their tab: `sudo sh` of it, then a mark left only
if it succeeded. The person answers their own sudo there. The window asks for the mark every two
seconds for ten minutes. When it appears, `AgentUser` and `AgentKey` are written to the session, and
the setup goes in an audit run of its own as `agent_setup`, with the script's hash. Any other ending
leaves the session as it was, says why, and removes the key it made. Removing the account again is
`Plan.Undo`, which nothing in the window offers yet. Any process of the person's can read the key,
an agent's included. A copy used directly skips the client's policy and audit, but signs in as the
same limited account, which is why the account is the boundary.

**Certificates that expire with the task (QS341).** quickshell is a small SSH certificate authority
(`AgentAuthority.cs`). Its ed25519 pair is made the first time a server is set up, in `agent-keys`:
the public half as `ca_ed25519.pub` and the private half only as `ca_ed25519.sealed`, protected with
DPAPI for this Windows account. It is unsealed into a file only while `ssh-keygen -s` (the OpenSSH that
ships with Windows) signs. That file is created with an access list naming this account alone, which
ssh-keygen demands, and is deleted afterwards. The setup dialog signs the agent in on a certificate by
default. The server then trusts the authority, not the agent's key, through one `authorized_keys` line:
`cert-authority,principals="<account>",restrict,from="…"`. So `sshd_config` is never edited, and the
line carries the source address. A session set up this way has `AgentCertificate` (SESSIONS.md). Before
each connection, the gateway signs a certificate for the agent's key with the account as its one
principal. It is valid from a minute back, for a server's clock, for the policy's `certificateMinutes`
(five by default, at most sixty). It is handed to SSH.NET beside the key. A failed signing is reported to
the agent as the connection failing. The key alone opens nothing, and an expired certificate opens
nothing. Revoking the agent is not signing again. Moving it to a new key means setting the server up
again. A connection the gateway keeps open stays open past its certificate, since sshd checks a
certificate only at sign-in; a kill switch (QS334) closes it. Settled and not built: one authority spans
every server, since a separate authority per tag adds keys to keep without narrowing what a certificate
opens. Each certificate already names one account, and each server trusts that account alone. Windows
Hello in front of the authority is not built.

**A policy the agent cannot widen (QS339).** Three measures. The policy lives in quickshell's folder,
outside any repository an agent is pointed at. `quickshell --agent-hook` denies the agent's `Write`,
`Edit`, `MultiEdit` and `NotebookEdit` any path at or under the files that limit or watch it: the
policy and its approved copy, `agent-taints`, `agent-asks` (or an agent could write its own approval),
`agents-stopped` and `audit`. It also denies its shell any command naming them. Reading them is left
alone. And quickshell keeps the version the person approved as `agent-policy.json.approved`: while
the policy on disk differs from it, or exists with no approved copy, the gateway refuses every call
with that rule, and the window shows the change as a diff, *Accept the change* or *Reject the change*,
the second putting the approved bytes back. A grant written from the approval dialog updates both, so
the person's own answers never trip it. What stays open: a same-user process can write the approved
copy too, past the hook, so the server-side layer remains the real boundary. Code:
`PolicyApproval.cs`, `PolicyChangeDialog.cs`, `AgentHook.cs`.

**Taint after untrusted text (QS338).** A call that brings in text somebody else could have written
taints the gateway run: `read_file`, a job's output or a page or search of kept output that returned
anything, and an `exec` (or a plan's call) that printed anything, unless its program only reports the
machine's own state (`uptime`, `df`, `free`, `whoami`, `id`, `groups`, `hostname`, `uname`, `nproc`,
`date`, `pwd`, `du`, `lscpu`, `lsblk`, `lsmem`, and `systemctl is-*`). Even `ps` and `systemctl
status` print text others control, so they taint. From then on a change the level or a grant would
allow asks instead, its rule naming what was read; a read still runs; a change the policy refuses is
still refused. The taint is `agent-taints/<run>.taint`, whose presence is the taint and whose text says
what brought it in; the agent's tab shows a warning line when it happens, and *Trust what agents have
read* in the palette removes every run's taint. It is as coarse as the design said it would be: a run
that reads anything asks for every change after, which is the cost of not knowing what the agent
believed. A same-user process could delete the file, which the side-door hook does not yet cover; it is
the file QS339's guard should protect beside the policy. Code: `AgentGateway.Untrusted`, `Tainted`.

**Approval, built (QS337).** An ask writes `agent-asks/<id>.ask.json` into quickshell's folder, holding
the batch: each call's session, address, tags, tool, the words it runs or an edit's diff (previewed
without writing it, and redacted), and the rule that asked. The window holds the named mutex
`Local\quickshell-agent-asks` while it is open, so a gateway with no window to ask refuses at once,
saying nobody was there; otherwise it waits up to ten minutes for `<id>.answer.json` and then answers
`approval timed out`. The window watches the folder, and for each request raises `ApprovalDialog` in
front of itself: each call as it will run, a command as words each in ‹ ›, prod in the danger colour,
and the batch's short hash. *Run it* approves the hash; *Run it and allow these for* writes a grant of
the same commands on the same tags (or the session, untagged) until 30 minutes, one hour or four hours
from now into the policy, creating one that keeps the no-file levels where there was none, and is
offered only for a batch of commands; *Refuse* sends the person's note. An answer to any other hash is a
refusal. The batch's hash is SHA-256 over each call's session, tool, length-prefixed words, path and
diff. `plan` puts several calls to one approval, runs them in order and stops at the first that fails;
a single call that asks is a plan of one. The audit line carries `approved <hash>`, `refuse <hash>` or
`automatic`. Code: `AgentAsks.cs`, `ApprovalDialog.cs`, `AgentGateway.ApprovedAsync`.

**The policy in the path of every call (QS336).** Before a call that reaches a session runs, the
gateway reads `agent-policy.json` from quickshell's folder (`AgentPolicy`) and judges it: `exec` and
`job_start` by their argument vector, `read_file`, `job_status`, `job_output` and `job_cancel` as reads
(a cancel only ends what the agent started), `edit_file` as a mutation. `hosts_list` and the kept
output reach no server and are not judged. A refusal or an ask comes back as a tool error carrying
`verdict` and `rule`, and the verdict and rule go into the audit line. An ask is refused for now with
a sentence saying so, since nothing can ask the person until the approval dialog exists (QS337); that
is the headless answer QS337 gives anyway. The judge (`CommandJudge`, with `agent-reads.json` built in)
takes the argument vector, unwraps `sudo` (`-n`, `-u` only), `env` (no `PATH`, `LD_*` or `BASH_*`),
`timeout` and `nice` narrowly, treats interpreters and stream editors as never reads, and parses an
`sh -c` script only within the narrow subset; inside one, an argument that expands (`$`, a glob) makes
a command with writing options or subcommands uncertain. Rules may add reads for their sessions.
`break-glass` is a grant (`"level": "break-glass"`), never a rule's level, which a policy that tries
is refused whole. A policy that does not read refuses every call, naming why. Measured: the corpus in
`tests/Quickshell.App.Tests/agent-commands.json`, 130 vectors, half adversarial from GTFOBins, has no
false allow, and 2 false asks of 29 common reads (a `find` with a glob, `$HOME/...` inside a word).
Two of QS322's questions are still open: whether a grant belongs to one agent session, and whether a
jump host's tag constrains what is behind it; the third, refusing secret paths at `observe`, is not
done, and redaction (QS330) is what stands there.

**A prompt is an answer, not a hang (QS327).** On a POSIX server `exec` puts
`exec </dev/null; export PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER= DEBIAN_FRONTEND=noninteractive;`
before the command. With no terminal and nothing on standard input, nothing can wait for a person:
sudo refuses at once, a `read` gets end-of-file, a pager prints and returns, apt asks nothing. What the
command said on its way out is then matched against the shapes those questions take, and the answer
carries `prompt` (`prompt.sudo_password`, `prompt.password`, `prompt.confirmation`, `prompt.pager`)
and a `hint` saying what to do instead. The quiet-window detection the line proposed is not needed:
measured against the fixture, nothing is left to hang. A Windows server is told apart by the version it
sends (`OpenSSH_for_Windows`) and gets the command without the preamble. Code:
`src/Quickshell.App/CommandPrompts.cs`.

**The agent's work in plain sight (QS335).** Each gateway run appends what it does to
`agent-feeds/<run>.vt` in quickshell's data folder, as terminal output: a title, then for each call a
prompt line (`agent`, the session, the tool, the command as the agent gave it) written before it runs,
then what came back, errors in red, and `[exit N]` or the refusal. All of it is redacted as answers
are. The window opens each feed written in the last ten minutes once it is idle, and any feed written
after that as it appears, in a tab of its own. The tab wears an `agent` chip in the accent, drawn the
way a tag's is, and is put behind the tab the person is in so it never takes the screen. A tab tails its
file through `FeedChannel`, a pty channel that reads the file and writes nowhere, so it is read-only
and is drawn by the ordinary pipeline and renderer. Closing the tab ends nothing; the kill switch does.
With no window open, nothing changes. The line asked for output as it streams: `exec` answers when the
command ends (QS325), so a call shows its prompt at once and its output when it is done. Code:
`src/Quickshell.App/AgentFeed.cs`.

**The kill switch (QS334).** `Ctrl+Shift+F12` in the window, or *Stop every agent* in the palette,
writes `agents-stopped` in quickshell's data folder. Every gateway watches for that file. When it
appears, the gateway ends any call in flight, sends TERM to every job still running for the account on
each session it holds and KILL after five seconds, closes every connection, and records the stop and
what it cancelled in the audit log. From then on every call is refused with `stopped: true` and a
sentence telling the agent the person stopped it and not to retry. A gateway whose watcher missed the
file sweeps at its next call. *Let agents run again*, in the palette alone so it is never pressed by
accident, removes the file; nothing cancelled is started again. The window and the gateway are
separate processes (QS321), so a file is the signal: it needs no port, and a gateway started later
finds the stop already in force. The line also asked for a tray item, and the client has no tray icon
yet, so it waits for one; grants (QS337) do not exist yet, so there are none to revoke. Code:
`MainWindow.AgentStop.cs`, `AgentGateway.StopAsync`, `RemoteJobs.CancelAllAsync`.

**The audit trail (QS333).** Every tool call, answered or refused, is one JSON line in
`audit/<run>.jsonl` under quickshell's data folder, one file per gateway run, appended and never
edited, rotated at 16 MB into `<run>.1.jsonl` and on with the chain unbroken. A line holds the time, the
call's number, the tool and what kind of call it is (read, changed, ran), the session and its tags,
the arguments redacted as answers are, the verdict, the rule and the approval, whether it failed, the
exit code, the SHA-256 of the whole answer, and the SHA-256 of the line before it. Until the policy
exists (QS336) the verdict is `allowed` by `no policy yet` and the approval `none`. `quickshell --audit`
lists the runs and `quickshell --audit <run>` prints one as a change summary, changed, ran, refused and
read, saying first whether every line follows the one before it; a broken chain names the line and
exits 1. The chain shows a slip or a casual edit, not a determined one: a process running as the same
user can rewrite every line and every hash, which is the server-side log's to answer. Code:
`src/Quickshell.App/AuditLog.cs`.

**Closing the side doors (QS332).** Two measures of different strength. *Steering:*
`quickshell --agent-hook` is a `PreToolUse` hook on the agent's `Bash` and `PowerShell` tools; the
client is the hook, so it needs no runtime and matches the gateway's version. It reads the command line
and denies, through `permissionDecision: "deny"` whose reason the agent reads, `ssh`, `scp`, `sftp`,
`slogin`, PuTTY's tools, mosh, autossh, sshpass, rsync to a host, git with an SSH address, PowerShell
remoting with `-HostName`, and Python with an SSH library, including behind `sudo`, `timeout`, a pipe
into `xargs` or a `$( )`. A script written first and run second walks past it (`SideDoors.cs`). *The
wall:* Claude Code's sandbox has no port rule to write; it has no route out but its HTTP proxy, so a
raw `ssh` cannot connect inside it at all, and MCP servers run outside it, so the gateway keeps its
connections. The configuration is `sandbox.enabled`, `failIfUnavailable` and
`allowUnsandboxedCommands: false`, with nothing SSH in `excludedCommands`; COMMAND-LINE.md shows it.
The sandbox runs on macOS, Linux and WSL2 and not on native Windows, where only the hook stands on
the agent's side (QS358). A key file the agent can read is the third way around, left to the
credential-isolation work the threat model names.

**Redaction on the way out (QS330).** Every tool's answer goes through `SecretRedactor` before it
leaves the process: private key blocks (and half of one, where a budget cut the other half off),
`password=`, `secret=`, `token=` and `api_key:` style assignments, bearer headers, the password in a
URL, and the AWS, GitHub, Slack and JWT token shapes. Each becomes `«redacted:kind»` with the name
around it kept, and the answer carries `redacted`, the count. The line assumed the client's log
redactor could be reused; there is none to reuse, because the client keeps secrets out of its logs
by construction, so this is its own. It is a guard against accidental spread, not a boundary: an
encoded secret passes. A line holding a secret comes back redacted from `read_file`, so it cannot be
quoted to `edit_file` as `old_string`; an agent edits around it or asks the person. Code:
`src/Quickshell.App/SecretRedactor.cs`.

**Output sized for a context window (QS329).** `exec` takes a `budget`, 16 KB per stream by default
and 256 KB at most. Within it a stream comes back whole. Over it, the stream keeps whole lines from
each end, half the budget each, with one line between them naming how many lines and bytes were cut
and how to reach them, and the answer carries an `output` id and the counts under `cut`. The whole
output is kept in the gateway, which already received every byte, so `output_page` (a line range,
numbered, within a budget) and `output_grep` (a .NET regular expression, at most 200 numbered matches)
send only what is asked and run nothing again on the server. The line proposed grepping on the server;
the gateway's copy makes that a second round trip for nothing. The spool keeps 64 M characters and
lets the oldest go first, and goes with the process. Code: `src/Quickshell.App/OutputSpool.cs`.

**Jobs that outlive a call (QS328).** `job_start` runs an argument vector detached on a POSIX server
and returns a twelve-hex-digit id at once; `job_status` says running, exited with its code,
cancelled, or lost; `job_output` reads stdout or stderr from a byte cursor, 64 KB at a time cut at a
whole character, and returns the next cursor; `job_cancel` sends TERM to the job's process group, then
KILL after five seconds. The line proposed keeping job ids in the gateway's own state; everything is
kept on the server instead, in `~/.quickshell/jobs/<id>/` (the pid, `out`, `err`, and an `exit` file
the job writes by rename when it ends). A job therefore outlives the connection, the gateway and the
agent's machine, and any later connection to the account can look back in, with nothing on this side
to keep or lose. Detached under `setsid`, or `nohup` where there is none, with nothing on its input.
An id is checked against its shape before it reaches a path. A Windows server is refused. Code:
`src/Quickshell.App/RemoteJobs.cs`.

**Reading and editing a file (QS326).** `read_file` and `edit_file` use the same connection's SFTP
channel, opened on first use. A file is read whole up to a megabyte and refused when it holds a NUL or
is not UTF-8; lines come back numbered, a range on request. An edit is the contract an agent's own
edit tool has: `old_string` must occur exactly once, or nothing is written and the answer says how
many times it occurred. The new text is written whole under `<file>.qs-new-<id>` and given the
original's mode; the original is then renamed to `<file>.qs-bak-<time>` and the new one renamed into
place. Two renames, because SFTP version 3 does not rename onto a file that exists; the backup is
therefore the original itself, with its owner, mode and times, and the edited file has a new
modification time, as any edit does. If the second rename fails the original is moved back, and if
that fails too the error names both paths. The answer carries one `diff -u` hunk with three lines of
context; the audit log that keeps it is QS333's. Code: `src/Quickshell.App/RemoteText.cs`.

## Sections to come

Each is written by the line that owns it, into this document.

| Line | Section |
|---|---|
| QS340, QS341 | The server account and short-lived certificates |
