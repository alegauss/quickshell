using System.Reflection;
using System.Text;
using Renci.SshNet;

namespace Quickshell.Transport;

/// <summary>
/// Every member of SSH.NET this client reaches by name, checked without a server (QS122).
///
/// <para><b>Why it exists.</b> Four properties of this client rest on members the library keeps to
/// itself: one connection shared by the shell and the file browser, a delete and a rename that do
/// not follow links, a frozen peer noticed by a request it must answer, and a jump's local port
/// closed once its hop is through. Each fails safe at run time against a server — but every test
/// that would show the failure skips where no server is up, so an upgrade that renamed one of them
/// built clean and waited for a user.</para>
///
/// <para><b>One list, read from where the names are used.</b> Each check takes its member from the
/// code that reaches for it — the same constant, the same cached <see cref="MemberInfo"/> — so a
/// name changed in one place cannot be checked under its old spelling in another.</para>
/// </summary>
public static class LibraryShape
{
    /// <summary>
    /// The members this build of SSH.NET is missing, in words; empty where every one is there.
    /// </summary>
    public static IReadOnlyList<string> Missing()
    {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        List<string> missing = [];
        Assembly library = typeof(SshClient).Assembly;

        // The shared SFTP session (QS59).
        Need(typeof(BaseClient).GetProperty(SharedSftpSession.SessionProperty, Hidden),
             $"BaseClient.{SharedSftpSession.SessionProperty}");
        Need(typeof(SftpClient).GetField(SharedSftpSession.SftpSessionField, Hidden),
             $"SftpClient.{SharedSftpSession.SftpSessionField}");
        Need(library.GetType(SharedSftpSession.ResponseFactoryType), SharedSftpSession.ResponseFactoryType);

        Type? session = library.GetType(SharedSftpSession.SftpSessionType);

        if (Need(session, SharedSftpSession.SftpSessionType) is { } sftp)
        {
            Need(sftp.GetConstructors(Any).FirstOrDefault(made => made.GetParameters() is
                     [_, { ParameterType: var timeout }, { ParameterType: var encoding }, _]
                     && timeout == typeof(int) && encoding == typeof(Encoding)),
                 $"{SharedSftpSession.SftpSessionType}(session, int, Encoding, factory)");
            Need(sftp.GetMethod("Connect", Any, Type.EmptyTypes), "SftpSession.Connect()");
            Need(sftp.GetMethod(SharedSftpSession.RemoveRequest, Any), $"SftpSession.{SharedSftpSession.RemoveRequest}");
            Need(sftp.GetMethod(SharedSftpSession.RenameRequest, Any), $"SftpSession.{SharedSftpSession.RenameRequest}");
            Need(sftp.GetProperty("ProtocolVersion", Any), "SftpSession.ProtocolVersion");
        }

        // The liveness request on the shell's channel (QS111).
        Need(SshNetChannel.SessionField, "ShellStream._channel");
        Need(SshNetChannel.Asking, "IChannelSession.SendEnvironmentVariableRequest(string, string)");

        // A jump's local port, closed without ending what it carries (QS119).
        Need(SshChain.StopListening, "ForwardedPortLocal.StopListener()");

        return missing;

        T? Need<T>(T? found, string name) where T : class
        {
            if (found is null)
            {
                missing.Add(name);
            }

            return found;
        }
    }
}
