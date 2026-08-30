# Planning: what makes this backlog predictable

The roadmap's open count stayed near sixty for a long stretch while work shipped steadily. That
looked like a plan going nowhere, and it was not. Splitting the count showed the plan converging and
something else running alongside it.

Run `.\roadmap-report.cmd` for the current figures. On 2026-08-30 they were:

    planned     open  38   shipped  55    59% done
    discovered  open  21   shipped   4    16% done
    harness     open   4   shipped   4    50% done

    discovery rate: 0.6 opened per planned task shipped

The planned backlog is more than half delivered. What kept the total flat is the third line: for
every ten planned tasks shipped, about six new ones opened. That rate is the number to watch, and
bringing it down is what this document is for.

## Where discovered work comes from

Classifying the discovered lines by cause, at the time of writing:

| Cause | Count | Predictable? |
|---|---|---|
| Scope deferred deliberately during design | 7 | Yes, and healthy — the design decided and recorded |
| Quality or diagnosis noticed while using it | 5 | Partly |
| **A library or platform could not do what the design assumed** | 4 | **Yes, by asking first** |
| Debt in the test harness | 3 | Yes |
| **Two finished parts with nothing joining them** | 2 | **Yes, by asking first** |

The two in bold are the ones worth changing, because both come from a question nobody asked.

## Ask the dependency before designing the block

A design that assumes a library can do something is a design that may be wrong in a way no amount of
writing will reveal. Spend an hour proving it against the real thing, before the block's lines are
written, and file what is missing as its own line.

Three examples, all found mid-delivery and all findable in advance:

- SSH.NET exposes **no way at all** to open an SFTP channel on a connection that already exists.
  Every public route builds its own. QS59's design was written as though it did, and the answer
  became six members reached by name (QS122).
- SSH.NET has **no readlink**, publicly or internally. It can create a symbolic link and never read
  one. QS62's design said links would be copied; downward, they cannot be (QS123).
- `ForwardedPortLocal`'s convenience constructor binds to a **link-local address other machines can
  reach**, and the library refuses the unspecified address outright. QS66 was designed around a
  loopback default and an "all interfaces" option; only the first exists (QS125).

QS66 is the first line where the spike came first, and it changed the design twice before a line of
the implementation was written. That is the difference between a spike and an estimate.

## Open the way in beside the thing it reaches

A component and the code that makes it reachable are two pieces of work, and for a long time only
the first was ever on the roadmap. An audit on 2026-08-30 found **eleven shipped transport
components named by no file in the application** — jump hosts, host-key trust, agents, saved
credentials, the whole of file transfer, port forwarding. All tested against real servers, none
reachable from anything a user can run (QS126).

So: when a line is opened for a component, open the line that says who will open it. It is usually
one sentence, and it is the difference between a feature count falling and a product moving.

## Keep the four kinds apart

`roadmap-report.cmd` derives the split from what the files already say — an id at or above 100 was
opened during delivery, and Block K is the harness — so nothing is maintained by hand. Two things
keep it honest:

- **Harness debt goes in Block K.** It is not a feature and it is not a defect in the product; it is
  the measuring apparatus. Three test defects sat in Block C for a while and made a feature block
  look worse than it was.
- **A deferral is filed as a line, not carried in somebody's head.** That is why the deferred count
  is high, and it should be.
