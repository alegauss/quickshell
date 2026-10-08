using System.Text;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>One question a server asked during sign-in, as the window shows it (QS218).</summary>
/// <param name="Endpoint">Who is asking: the account and the host.</param>
/// <param name="Prompt">The server's own words, shown as written.</param>
/// <param name="Echoed">Whether the answer may be shown as it is typed; false for anything secret.</param>
/// <param name="MayRemember">
/// Whether keeping the answer is offered: a hidden answer to a prompt that asks for a password, and
/// never a one-time code, which would be wrong the next time it was offered.
/// </param>
public sealed record SignInQuestion(SshEndpoint Endpoint, string Prompt, bool Echoed, bool MayRemember);

/// <summary>What the person answered, and whether to keep it for next time.</summary>
/// <param name="Text">The answer.</param>
/// <param name="Remember">Whether to save it, which is only ever offered for a secret answer.</param>
public sealed record SignInAnswer(string Text, bool Remember);

/// <summary>
/// The window's half of a password or keyboard-interactive sign-in (QS218): the server's questions
/// put to the person, and a password they chose to keep offered back without asking.
///
/// <para><b>One per connection.</b> <see cref="For"/> builds the credentials a connection offers after
/// its keys, and <see cref="Commit"/> is called once the connection has succeeded — only then is a
/// password the person asked to remember saved, so a mistyped one is never kept.</para>
///
/// <para><b>A remembered password is used once, then the person is asked.</b> A password changed on
/// the server would otherwise be offered for every one of the server's retries and lock the account;
/// answered once and refused, the next prompt goes to the window like any other.</para>
/// </summary>
public sealed class SignIn
{
    private readonly Func<SignInQuestion, CancellationToken, ValueTask<SignInAnswer?>> _ask;
    private readonly SecretStore? _store;
    private readonly SshEndpoint _endpoint;
    private bool _usedSaved;
    private Secret? _keep;

    private SignIn(Func<SignInQuestion, CancellationToken, ValueTask<SignInAnswer?>> ask, SecretStore? store,
                   SshEndpoint endpoint)
    {
        _ask = ask;
        _store = store;
        _endpoint = endpoint;
    }

    /// <summary>
    /// What a connection to this endpoint offers after its keys and agent: the server's prompts,
    /// answered here, and a remembered password last, as QS41 orders the methods.
    /// </summary>
    /// <param name="endpoint">The account and host being signed in to.</param>
    /// <param name="ask">Puts a question to the person; null back means they declined.</param>
    /// <param name="store">Where remembered passwords are, or null where nothing is remembered.</param>
    public static (SignIn SignIn, IReadOnlyList<SshCredential> Offered) For(
        SshEndpoint endpoint, Func<SignInQuestion, CancellationToken, ValueTask<SignInAnswer?>> ask,
        SecretStore? store)
    {
        ArgumentNullException.ThrowIfNull(ask);

        SignIn signIn = new(ask, store, endpoint);
        List<SshCredential> offered = [new SshCredential.Interactive(signIn.Answer)];

        if (store?.Load(endpoint) is { } saved)
        {
            offered.Add(new SshCredential.Password(saved));
        }

        return (signIn, offered);
    }

    /// <summary>
    /// Whether a password is worth asking for after this refusal: the server said it takes one, and
    /// none was offered — neither a kept one nor one the person typed for this connection.
    /// </summary>
    public bool ShouldAskForPassword(SshException refused)
    {
        ArgumentNullException.ThrowIfNull(refused);

        return refused.Kind == SshFailureKind.NoMethodAccepted
               && refused.ServerAccepts.Contains("password", StringComparer.OrdinalIgnoreCase)
               && _store?.Load(_endpoint) is null;
    }

    /// <summary>
    /// Asks for the password itself, for a server whose only way in is the password method, which
    /// has no prompt of its own to show (QS218). The question is this client's, since the server
    /// asked none; remembering is offered as for any password.
    /// </summary>
    /// <returns>The password as a credential, or null where the person declined.</returns>
    public async ValueTask<SshCredential.Password?> AskPasswordAsync(CancellationToken cancellationToken)
    {
        SignInAnswer? answer = await _ask(new SignInQuestion(_endpoint, "Password:", Echoed: false,
                                                             MayRemember: _store is not null), cancellationToken)
                                   .ConfigureAwait(false);

        if (answer is null)
        {
            return null;
        }

        if (_store is not null && answer.Remember)
        {
            _keep?.Dispose();
            _keep = Secret.From(answer.Text.AsSpan());
        }

        return new SshCredential.Password(Secret.From(answer.Text.AsSpan()));
    }

    /// <summary>Saves what the person asked to remember, now that the connection has succeeded.</summary>
    public void Commit()
    {
        if (_keep is { } keep && _store is { } store)
        {
            store.Save(_endpoint, keep);
        }

        Forget();
    }

    /// <summary>Drops a password nobody will save, because the connection failed.</summary>
    public void Forget()
    {
        _keep?.Dispose();
        _keep = null;
    }

    /// <summary>One of the server's prompts, answered from what is remembered or by the person.</summary>
    private async ValueTask<string> Answer(string prompt, bool echoed, CancellationToken cancellationToken)
    {
        if (!echoed && !_usedSaved && Asks(prompt) && _store?.Load(_endpoint) is { } saved)
        {
            _usedSaved = true;

            using (saved)
            {
                return Encoding.UTF8.GetString(saved.ToUnprotectedArray());
            }
        }

        bool mayRemember = !echoed && Asks(prompt) && _store is not null;

        SignInAnswer answer = await _ask(new SignInQuestion(_endpoint, prompt, echoed, mayRemember), cancellationToken)
                                  .ConfigureAwait(false)
                              ?? throw new OperationCanceledException("The sign-in was declined.");

        if (mayRemember && answer.Remember)
        {
            _keep?.Dispose();
            _keep = Secret.From(answer.Text.AsSpan());
        }

        return answer.Text;
    }

    /// <summary>
    /// Whether a prompt asks for the password, which is the one secret worth remembering: a one-time
    /// code typed into a remembered slot would be wrong the next time it was offered.
    /// </summary>
    private static bool Asks(string prompt) => prompt.Contains("password", StringComparison.OrdinalIgnoreCase);
}
