namespace Quickshell.Transport;

/// <summary>
/// What a user is shown about a key nobody has seen before, and asked to decide.
/// </summary>
/// <param name="Endpoint">Where the connection was going.</param>
/// <param name="Key">What answered.</param>
/// <param name="Stored">
/// The key that was there instead, where this is a changed key rather than an unknown one. Null for
/// first use.
/// </param>
/// <param name="Verdict">What the store said, which decides which of two quite different things this is.</param>
public readonly record struct HostKeyQuestion(SshEndpoint Endpoint, SshHostKey Key,
                                              SshHostKey? Stored, KnownHostVerdict Verdict)
{
    /// <summary>Whether this is a key that changed, which is a warning and not a question.</summary>
    public bool IsChange => Verdict is KnownHostVerdict.Changed or KnownHostVerdict.Revoked;
}

/// <summary>
/// Asked when the store has nothing to say. Answering is a person's job, and the shape of the
/// question is deliberately not a boolean: a caller that could only say yes or no could not
/// distinguish "trust this once" from "remember it".
/// </summary>
public delegate ValueTask<SshHostKeyVerdict> HostKeyDecision(HostKeyQuestion question,
                                                             CancellationToken cancellationToken);

/// <summary>
/// The host-key check, built over a <see cref="KnownHosts"/> store.
///
/// <para><b>It fails closed and there is no setting that turns it off.</b> An encrypted connection
/// to an unverified host is an encrypted connection to whoever answered, so a client whose default
/// is accept is a client whose encryption is decoration. There is no constructor here that takes
/// "trust everything": a test that wants that passes its own delegate, which is a thing a reader can
/// see in the test rather than a flag that might be set in a settings file somewhere.</para>
///
/// <para><b>A changed key is not the first-use question and must not resemble it.</b> First use asks;
/// a change <em>refuses</em>, and getting past it means removing the old entry with
/// <see cref="KnownHosts.Forget"/> — a deliberate act, not a button. The design's own criterion is
/// that a changed key cannot be accepted by clicking a default button, and the way to guarantee that
/// is for there to be no button: this returns <see cref="SshHostKeyVerdict.Refuse"/> for a change
/// whatever the decision delegate says.</para>
/// </summary>
public sealed class TrustOnFirstUse
{
    private readonly HostKeyDecision _ask;
    private readonly KnownHosts _store;

    /// <summary>Builds a check over a store, asking this delegate about keys it does not know.</summary>
    /// <param name="store">The user's own <c>known_hosts</c>.</param>
    /// <param name="ask">
    /// What to do about a key nobody has seen. It is asked only for
    /// <see cref="KnownHostVerdict.Unknown"/>; a change never reaches it.
    /// </param>
    public TrustOnFirstUse(KnownHosts store, HostKeyDecision ask)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ask);

        _store = store;
        _ask = ask;
    }

    /// <summary>What the last connection's key turned out to be, for a test or a diagnostic.</summary>
    public HostKeyQuestion? Last { get; private set; }

    /// <summary>
    /// The check itself, in the shape <see cref="ISshTransport.ConnectAsync"/> takes.
    ///
    /// <para>A method group rather than a lambda so that the falsification — no code path connects
    /// without consulting the store — is a thing a reader can follow: every connection this client
    /// makes passes through here, and here always reads the store first.</para>
    /// </summary>
    public async ValueTask<SshHostKeyVerdict> CheckAsync(SshEndpoint endpoint, SshHostKey key,
                                                         CancellationToken cancellationToken)
    {
        KnownHostVerdict known = _store.Check(endpoint, key, out SshHostKey? stored);
        HostKeyQuestion question = new(endpoint, key, stored, known);

        Last = question;

        switch (known)
        {
            case KnownHostVerdict.Matches:
                // No interaction at all. A client that confirmed a key it already knew would be
                // teaching the user to dismiss the dialog without reading it.
                return SshHostKeyVerdict.Accept;

            case KnownHostVerdict.Changed:
            case KnownHostVerdict.Revoked:
                // Not a question. The delegate is not even asked, so there is nothing for a user to
                // click and nothing for a caller to get wrong.
                return SshHostKeyVerdict.Refuse;

            default:
                break;
        }

        SshHostKeyVerdict decided = await _ask(question, cancellationToken).ConfigureAwait(false);

        if (decided == SshHostKeyVerdict.AcceptAndRemember)
        {
            _store.Add(endpoint, key);
        }

        return decided;
    }

    /// <summary>
    /// What a user is told about a changed key: what it is now, what it was, and that a client
    /// cannot tell the innocent reading from the other one.
    ///
    /// <para>Written here rather than in a window so that the wording is testable and so that every
    /// surface says the same thing. It names both readings deliberately — a server somebody rebuilt
    /// looks exactly like a machine in the middle, and a message that only mentioned the frightening
    /// one would be dismissed by the many users for whom it is the boring one.</para>
    /// </summary>
    public static SshException Refused(HostKeyQuestion question)
    {
        if (question.Verdict == KnownHostVerdict.Revoked)
        {
            return new SshException(
                SshFailureKind.HostKey,
                $"The key {question.Endpoint.Host} presented is marked revoked.",
                "Somebody has recorded this exact key as one never to trust.",
                "Do not connect. If this host is yours, it needs a new key.");
        }

        string now = $"{question.Key.Algorithm} SHA256:{question.Key.Fingerprint}";
        string before = question.Stored is { } was
            ? $"{was.Algorithm} SHA256:{was.Fingerprint}"
            : "a different key";

        return new SshException(
            SshFailureKind.HostKey,
            $"The key {question.Endpoint.Host} presented is not the one stored for it.",
            $"It offered {now}, and {before} is what is remembered. This is what a rebuilt server "
            + "looks like, and it is also what a machine in the middle looks like — from here they "
            + "are the same thing.",
            $"If the server was rebuilt, remove its entry from {question.Endpoint.Host}'s line in "
            + "known_hosts and connect again. If it was not, do not connect.");
    }
}
