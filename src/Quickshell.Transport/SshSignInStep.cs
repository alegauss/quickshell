using System.Text;

namespace Quickshell.Transport;

/// <summary>
/// Something that happened while signing in, reported as it happens so a two-step authentication
/// is not a silence (QS113).
///
/// <para><b>The two moments before a prompt.</b> A key accepted with a second factor still to come
/// — a push to approve, a code to type — takes as long as a person does, and before the second
/// factor's prompt arrives nothing else would reach a caller. A client that shows nothing there is
/// one a user assumes has hung. The prompts themselves arrive through
/// <see cref="SshCredential.Interactive"/>; this is what comes before them.</para>
///
/// <para>Protocol words and the server's own text, nothing of the library's: the methods are named
/// as the protocol names them, <c>publickey</c> and <c>keyboard-interactive</c>.</para>
/// </summary>
/// <param name="Endpoint">The machine signing in, which in a chain is not always the last one.</param>
public abstract record SshSignInStep(SshEndpoint Endpoint)
{
    /// <summary>
    /// The server's banner: its own words, shown before anything is authenticated, and very often
    /// the one place a host says what it is or who to ask.
    /// </summary>
    /// <param name="Endpoint">The machine whose banner it is.</param>
    /// <param name="Text">
    /// The banner with every control character but line breaks and tabs taken out. It is the far
    /// end's text, and an escape sequence in it would otherwise reach whatever draws it.
    /// </param>
    public sealed record Banner(SshEndpoint Endpoint, string Text) : SshSignInStep(Endpoint);

    /// <summary>
    /// A method succeeded and the server wants more: the partial success the protocol has a word for.
    /// </summary>
    /// <param name="Endpoint">The machine that wants more.</param>
    /// <param name="Accepted">The method that was accepted, as the protocol names it.</param>
    /// <param name="StillWanted">What the server will take next, as it listed them.</param>
    public sealed record Partly(SshEndpoint Endpoint, string Accepted, IReadOnlyList<string> StillWanted)
        : SshSignInStep(Endpoint);

    /// <summary>The far end's text with the control characters a display would act on taken out.</summary>
    internal static string Printable(string text)
    {
        StringBuilder kept = new(text.Length);

        foreach (char each in text)
        {
            if (each is '\n' or '\t' || !char.IsControl(each))
            {
                kept.Append(each);
            }
        }

        return kept.ToString();
    }
}
