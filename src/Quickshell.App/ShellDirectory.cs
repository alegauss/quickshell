namespace Quickshell.App;

/// <summary>
/// Where a shell says it is, as a path on the host the session connected to — or nothing (QS184).
///
/// <para><b>OSC 7 carries a URL, and the host in it is the shell's idea of its own name.</b> That is
/// not always the name the session connected to, and a nested <c>ssh</c> reports a different
/// machine's directory altogether. A path is taken only where the URL names no host or names this
/// session's; anything else is somebody else's directory, and listing it on this server would show
/// whatever happens to be at the same path here.</para>
///
/// <para><b>A short name matches its own long one.</b> A shell's <c>hostname</c> is the short name
/// nearly everywhere, and the session was opened by the long one — <c>web1</c> reporting while the
/// session went to <c>web1.example.com</c> is the same machine saying so, not a nested hop.</para>
/// </summary>
public static class ShellDirectory
{
    /// <summary>The path the report names, when it names one on <paramref name="host"/>.</summary>
    /// <param name="reported">What OSC 7 last set: <c>file://host/path</c>, or empty where nothing has.</param>
    /// <param name="host">What the session connected to, as the saved session spells it.</param>
    /// <returns>The path with its escapes undone, or null where there is none to follow.</returns>
    public static string? On(string reported, string host)
    {
        ArgumentNullException.ThrowIfNull(reported);
        ArgumentNullException.ThrowIfNull(host);

        const string Scheme = "file://";

        string named;
        string path;

        if (reported.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            string rest = reported[Scheme.Length..];
            int slash = rest.IndexOf('/', StringComparison.Ordinal);

            if (slash < 0)
            {
                return null;
            }

            named = rest[..slash];
            path = rest[slash..];
        }
        else if (reported.StartsWith('/'))
        {
            // A bare path is a shell that left the host out, which is the same as an empty one.
            named = string.Empty;
            path = reported;
        }
        else
        {
            return null;
        }

        if (named.Length > 0 && !Same(named, Machine(host)))
        {
            return null;
        }

        try
        {
            return Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>The machine a session's host names, without the account in front or the port behind.</summary>
    private static string Machine(string host)
    {
        int at = host.LastIndexOf('@');
        string machine = at < 0 ? host : host[(at + 1)..];
        int colon = machine.LastIndexOf(':');

        if (colon > 0 && machine[(colon + 1)..].All(char.IsAsciiDigit) && machine.IndexOf(':') == colon)
        {
            machine = machine[..colon];
        }

        return machine;
    }

    /// <summary>Whether two names are one machine: equal, or one the short form of the other.</summary>
    private static bool Same(string reported, string session)
    {
        if (string.Equals(reported, session, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool reportedShort = !reported.Contains('.', StringComparison.Ordinal);
        bool sessionShort = !session.Contains('.', StringComparison.Ordinal);

        // Exactly one of them short, or two long names that differ really are two machines.
        if (reportedShort == sessionShort)
        {
            return false;
        }

        string shortName = reportedShort ? reported : session;
        string longName = reportedShort ? session : reported;

        // An address is not a name with a domain, and its first number is no machine's short name.
        return !longName.All(c => char.IsAsciiDigit(c) || c == '.')
               && longName.StartsWith(shortName + ".", StringComparison.OrdinalIgnoreCase);
    }
}
