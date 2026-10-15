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

## Sections to come

Each is written by the line that owns it, into this document.

| Line | Section |
|---|---|
| QS321 | Whether this belongs in a client sold as "an SSH client, not a suite" |
| QS322 | The policy format: levels per session tag, grants that expire, where the file lives |
| QS323 | How a remote command is judged read-only, and why not with a regex |
| QS324 | Approval by plan, so a person reviews a batch once instead of clicking yes forty times |
| QS340, QS341 | The server account and short-lived certificates |
