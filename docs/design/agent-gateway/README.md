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

## Sections to come

Each is written by the line that owns it, into this document.

| Line | Section |
|---|---|
| QS321 | Whether this belongs in a client sold as "an SSH client, not a suite" |
| QS340, QS341 | The server account and short-lived certificates |
