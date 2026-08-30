using System.Globalization;
using System.Reflection;
using System.Text;
using Renci.SshNet;

namespace Quickshell.Transport;

/// <summary>
/// An SFTP session opened as a channel of a connection that already exists.
///
/// <para><b>This is the one place in the client that reaches inside SSH.NET, and it is here because
/// the library offers no public way to do the thing the design requires.</b> Every public route to
/// SFTP — every <c>SftpClient</c> constructor — takes connection details and opens its own
/// connection. That is a second TCP session, a second key exchange, a second authentication and a
/// second line in the server's auth log. For a user behind a hardware token it is a second tap, to
/// reach a machine they already have open.</para>
///
/// <para><b>What it does is small.</b> SSH.NET's own <c>SftpSession</c> takes an <c>ISession</c> —
/// exactly what a connected <c>SshClient</c> already holds. So the session is borrowed, an SFTP
/// channel is opened on it, and the result is handed to an ordinary <c>SftpClient</c> whose entire
/// public surface then works normally. Four members are reached for by name; everything after is the
/// library's supported API.</para>
///
/// <para><b>It fails loudly, and a test watches the server rather than this code.</b> If a future
/// SSH.NET renames any of the four, this throws with the member named instead of quietly falling
/// back to a second connection — a fallback would be a security property silently withdrawn. The
/// test that guards it counts authentications in the server's own log, so it is the far end that
/// says whether the claim still holds.</para>
/// </summary>
internal static class SharedSftpSession
{
    private const string SessionProperty = "Session";
    private const string SftpSessionField = "_sftpSession";
    private const string SftpSessionType = "Renci.SshNet.Sftp.SftpSession";
    private const string ResponseFactoryType = "Renci.SshNet.Sftp.SftpResponseFactory";

    /// <summary>
    /// Opens an SFTP channel on a connected client's session and wraps it in a usable client.
    /// </summary>
    /// <param name="over">A connected client whose session carries the new channel.</param>
    /// <param name="timeout">How long one operation may take.</param>
    /// <returns>A client sharing <paramref name="over"/>'s connection, and the session behind it.</returns>
    /// <exception cref="SshException">SSH.NET is not the shape this expects.</exception>
    public static (SftpClient Client, IDisposable Session) OpenOn(SshClient over, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(over);

        Assembly library = typeof(SshClient).Assembly;

        PropertyInfo session = Member(typeof(BaseClient).GetProperty(
            SessionProperty, BindingFlags.NonPublic | BindingFlags.Instance), SessionProperty);

        object live = session.GetValue(over)
            ?? throw Unshareable("the client has no session, so it is not connected");

        Type factoryType = Member(library.GetType(ResponseFactoryType), ResponseFactoryType);
        Type sessionType = Member(library.GetType(SftpSessionType), SftpSessionType);

        object channel;

        try
        {
            object factory = Activator.CreateInstance(factoryType, nonPublic: true)!;

            // Operation timeouts are milliseconds here, and UTF-8 is what every server this client
            // will meet uses for names.
            channel = Activator.CreateInstance(
                sessionType,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                [live, (int)timeout.TotalMilliseconds, Encoding.UTF8, factory],
                culture: null)!;

            Member(sessionType.GetMethod("Connect", BindingFlags.Public | BindingFlags.NonPublic
                                                    | BindingFlags.Instance, Type.EmptyTypes),
                   "SftpSession.Connect")
                .Invoke(channel, null);
        }
        catch (TargetInvocationException opening) when (opening.InnerException is not null)
        {
            // The far end refusing the subsystem is an ordinary failure and not a shape change, so
            // it is reported as what it is.
            throw new SshException(
                SshFailureKind.ShellRefused,
                "The server would not open an SFTP channel on this connection.",
                opening.InnerException.Message,
                "The server may have the sftp subsystem disabled; the shell is unaffected.",
                opening.InnerException.Message);
        }
        catch (MissingMethodException shape)
        {
            throw Unshareable($"{SftpSessionType} is not the shape this expects: {shape.Message}");
        }

        SftpClient client = new(over.ConnectionInfo);

        session.SetValue(client, live);

        Member(typeof(SftpClient).GetField(SftpSessionField,
                                           BindingFlags.NonPublic | BindingFlags.Instance),
               SftpSessionField)
            .SetValue(client, channel);

        return (client, (IDisposable)channel);
    }

    /// <summary>
    /// Removes exactly the path named, without letting the library resolve it first.
    ///
    /// <para><b>This exists because the supported route destroys the wrong file.</b> SSH.NET's
    /// <c>Delete</c> canonicalises a path through the server's <c>realpath</c>, which follows
    /// symbolic links — so deleting a link removes the file it points at and leaves the link behind.
    /// Measured against the fixture: after deleting a link, the directory still held the link and
    /// the target was gone. A file pane offering "delete this shortcut" would silently destroy the
    /// document.</para>
    ///
    /// <para>The session's own request takes the path as given, which is what the design means by
    /// paths belonging to the server.</para>
    /// </summary>
    public static Task RemoveAsync(object session, string path, CancellationToken cancellationToken) =>
        (Task)Request(session, "RequestRemoveAsync")
            .Invoke(session, [path, cancellationToken])!;

    /// <summary>
    /// Renames exactly the path named, for the same reason: renaming a link through the supported
    /// route moves its target and leaves the link dangling.
    /// </summary>
    public static Task RenameAsync(object session, string from, string to,
                                   CancellationToken cancellationToken) =>
        (Task)Request(session, "RequestRenameAsync")
            .Invoke(session, [from, to, cancellationToken])!;

    private static MethodInfo Request(object session, string name) =>
        Member(session.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance),
               name);

    private static T Member<T>(T? found, string name) where T : class =>
        found ?? throw Unshareable($"{name} is not there");

    /// <summary>
    /// The failure this type can produce, which never degrades into opening a second connection.
    ///
    /// <para>A fallback would trade a security property for an error message, and do it at the
    /// moment nobody is watching. Refusing is the safe direction.</para>
    /// </summary>
    private static SshException Unshareable(string what) =>
        new(SshFailureKind.Unrecognised,
            "This build of SSH.NET cannot share a connection with a file transfer.",
            string.Create(CultureInfo.InvariantCulture, $"{what}."),
            "quickshell will not open a second connection instead, because that would cost a second "
            + "authentication without saying so. Report this with the SSH.NET version.");
}
