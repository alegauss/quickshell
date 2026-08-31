using System.Globalization;
using System.Text;

namespace Quickshell.Transport;

/// <summary>How much a log records.</summary>
public enum LogDetail
{
    /// <summary>Nothing at all.</summary>
    Off,

    /// <summary>
    /// The shape of what happened: connections, authentication attempts, channels, forwards,
    /// transfers, and every error in the words the user saw. On by default, and what a bug report
    /// needs.
    /// </summary>
    Ordinary,

    /// <summary>
    /// The above, and the negotiation beneath it: versions exchanged, algorithms each side offered,
    /// key exchange, channel operations in sequence.
    ///
    /// <para>Off unless asked for. It is what diagnoses an appliance that will not negotiate, which
    /// is the failure this client will meet most often against old hardware.</para>
    /// </summary>
    Trace,
}

/// <summary>What a credential is, which is all a log is ever told about one.</summary>
public enum CredentialKind
{
    /// <summary>A password.</summary>
    Password,

    /// <summary>A private key, with or without a passphrase.</summary>
    PrivateKey,

    /// <summary>A key held by an agent.</summary>
    Agent,

    /// <summary>An answer typed at a prompt.</summary>
    Interactive,
}

/// <summary>What a channel was for.</summary>
public enum ChannelKind
{
    /// <summary>A shell.</summary>
    Shell,

    /// <summary>A file transfer subsystem.</summary>
    FileTransfer,

    /// <summary>A command run on the far side.</summary>
    Command,

    /// <summary>A forwarded connection.</summary>
    Forward,
}

/// <summary>
/// What happened, written where a user can find it and send it.
///
/// <para><b>The hard rule is that no level may contain a secret, and it is enforced by there being
/// no way to write one.</b> There is no method here that takes a password, a key, a passphrase, an
/// agent reply, or a byte of channel content — not one that redacts them, one that does not accept
/// them. A filter applied afterwards is a list of things somebody remembered, and the forgotten one
/// is always the one that matters; a surface that cannot express a secret has nothing to
/// forget.</para>
///
/// <para><b>The library's own logging is deliberately not plumbed through.</b> SSH.NET offers a
/// process-wide hook that takes a logger factory, and what it then writes is its decision rather
/// than this project's. A log whose contents another component chooses cannot carry a no-secrets
/// guarantee, so the trace here is written from this client's own seam and says only what this
/// client knows.</para>
///
/// <para><b>Payloads are lengths and kinds.</b> A byte of a channel is a byte of somebody's session,
/// which is why the only thing recorded about one is how many there were.</para>
///
/// <para><b>It rotates against a bounded total</b>, because a trace left running overnight must not
/// fill a disk, and <see cref="Path"/> is public because a log a user cannot find is a log that does
/// not exist.</para>
/// </summary>
public sealed class SessionLog : IAsyncDisposable
{
    private readonly Lock _guard = new();
    private readonly string _folder;
    private readonly string _stem;
    private readonly long _each;
    private readonly int _keep;

    private StreamWriter? _writing;
    private long _written;
    private bool _disposed;

    private SessionLog(string folder, string stem, LogDetail detail, long each, int keep)
    {
        _folder = folder;
        _stem = stem;
        _each = each;
        _keep = keep;

        Detail = detail;
    }

    /// <summary>How much is being recorded. Changeable while running, per session.</summary>
    public LogDetail Detail { get; set; }

    /// <summary>The file being written now. Shown to the user, because they have to find it.</summary>
    public string Path => System.IO.Path.Combine(_folder, $"{_stem}.log");

    /// <summary>Every file this log has, newest first.</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            if (!Directory.Exists(_folder))
            {
                return [];
            }

            return [.. Directory.EnumerateFiles(_folder, $"{_stem}*.log")
                                .OrderBy(file => file, StringComparer.Ordinal)];
        }
    }

    /// <summary>The most this log will ever occupy.</summary>
    public long Bounded => _each * (_keep + 1);

    /// <summary>
    /// Opens a log in a folder.
    /// </summary>
    /// <param name="folder">Where the files go. Created where it is not there.</param>
    /// <param name="detail">How much to record.</param>
    /// <param name="each">How large one file may become before it is rolled.</param>
    /// <param name="keep">How many rolled files to keep behind the current one.</param>
    public static SessionLog InFolder(string folder, LogDetail detail = LogDetail.Ordinary,
                                      long each = 4 * 1024 * 1024, int keep = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentOutOfRangeException.ThrowIfLessThan(each, 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        Directory.CreateDirectory(folder);

        return new SessionLog(folder, "quickshell", detail, each, keep);
    }

    // ---- Connections ----

    /// <summary>A connection is being attempted.</summary>
    public void Connecting(SshEndpoint where) => Say(LogDetail.Ordinary, "connecting", Of(where));

    /// <summary>A connection succeeded.</summary>
    public void Connected(SshEndpoint where, TimeSpan took) =>
        Say(LogDetail.Ordinary, "connected", Of(where), Ms("took", took));

    /// <summary>A connection ended.</summary>
    public void Disconnected(SshEndpoint where, bool expected) =>
        Say(LogDetail.Ordinary, "disconnected", Of(where),
            ("how", expected ? "closed" : "dropped"));

    /// <summary>
    /// A connection failed, in the words the user saw.
    /// </summary>
    /// <param name="where">The endpoint.</param>
    /// <param name="kind">Which failure it was.</param>
    /// <param name="reason">
    /// The sentence shown to the user. It is this client's own wording, which is why it can be
    /// written: nothing composes it from a credential.
    /// </param>
    public void Failed(SshEndpoint where, SshFailureKind kind, string reason) =>
        Say(LogDetail.Ordinary, "failed", Of(where), ("kind", kind.ToString()),
            ("reason", One(reason)));

    // ---- Authentication ----

    /// <summary>A credential was offered. Its kind is recorded and never its content.</summary>
    public void Offered(SshEndpoint where, CredentialKind kind) =>
        Say(LogDetail.Ordinary, "auth-offered", Of(where), ("kind", kind.ToString()));

    /// <summary>
    /// The server accepted the session.
    ///
    /// <para><b>It does not say which credential won, and the omission is deliberate.</b> SSH.NET
    /// reports that authentication succeeded and not which method got there, so naming one of
    /// several offered would be a guess written into the one file a user sends when they cannot get
    /// in. A log that guesses is worse than a log that is quiet.</para>
    /// </summary>
    public void Authenticated(SshEndpoint where) => Say(LogDetail.Ordinary, "auth-accepted", Of(where));

    /// <summary>The server refused one.</summary>
    public void Refused(SshEndpoint where, CredentialKind kind) =>
        Say(LogDetail.Ordinary, "auth-refused", Of(where), ("kind", kind.ToString()));

    // ---- Channels, forwards, transfers ----

    /// <summary>A channel opened or closed.</summary>
    public void Channel(ChannelKind kind, bool opened) =>
        Say(LogDetail.Ordinary, opened ? "channel-open" : "channel-close",
            ("kind", kind.ToString()));

    /// <summary>A forward started or stopped, by its ports rather than by what crosses it.</summary>
    public void Forward(int localPort, int remotePort, bool started) =>
        Say(LogDetail.Ordinary, started ? "forward-start" : "forward-stop",
            Number("local", localPort), Number("remote", remotePort));

    /// <summary>
    /// Bytes moved, as a count.
    /// </summary>
    /// <param name="kind">What carried them.</param>
    /// <param name="bytes">How many. Never which.</param>
    public void Moved(ChannelKind kind, long bytes) =>
        Say(LogDetail.Ordinary, "moved", ("kind", kind.ToString()), Number("bytes", bytes));

    // ---- The trace ----

    /// <summary>The two version strings, which are the first thing an old appliance disagrees on.</summary>
    public void Versions(string ours, string theirs) =>
        Say(LogDetail.Trace, "versions", ("ours", One(ours)), ("theirs", One(theirs)));

    /// <summary>
    /// What each side offered for one negotiation, which is what diagnoses "no algorithm in common".
    /// </summary>
    /// <param name="what">Which negotiation: key exchange, cipher, mac, host key.</param>
    /// <param name="ours">What this client offered.</param>
    /// <param name="theirs">What the server offered.</param>
    /// <param name="chosen">What was agreed, or empty where nothing was.</param>
    public void Negotiated(string what, string ours, string theirs, string chosen) =>
        Say(LogDetail.Trace, "negotiated", ("what", One(what)), ("ours", One(ours)),
            ("theirs", One(theirs)), ("chosen", One(chosen)));

    /// <summary>A payload crossed, recorded as its size and nothing else.</summary>
    public void Payload(ChannelKind kind, bool inbound, int length) =>
        Say(LogDetail.Trace, "payload", ("kind", kind.ToString()),
            ("way", inbound ? "in" : "out"), Number("length", length));

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_guard)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

#pragma warning disable CA1849
            _writing?.Flush();
#pragma warning restore CA1849
            _writing?.Dispose();
            _writing = null;
        }

        return ValueTask.CompletedTask;
    }

    private static (string, string) Of(SshEndpoint where) => ("where", where.ToString());

    private static (string, string) Ms(string name, TimeSpan took) =>
        (name, took.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));

    private static (string, string) Number(string name, long value) =>
        (name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// One line's worth of a value: line breaks removed so a field cannot become a record.
    ///
    /// <para>Not a redaction — the values that reach here are this client's own words and the
    /// server's algorithm names. It keeps one field from splitting into two, which is what makes a
    /// log readable by something other than a person.</para>
    /// </summary>
    private static string One(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    private void Say(LogDetail level, string what, params (string Name, string Value)[] fields)
    {
        if (Detail == LogDetail.Off || level > Detail)
        {
            return;
        }

        StringBuilder line = new();

        line.Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(what);

        foreach ((string name, string value) in fields)
        {
            line.Append(' ').Append(name).Append('=').Append(value);
        }

        Write(line.ToString());
    }

    private void Write(string line)
    {
        lock (_guard)
        {
            if (_disposed)
            {
                return;
            }

            _writing ??= Open();

            _writing.WriteLine(line);

            // Flushed synchronously, under the lock, on purpose: a log exists to survive the thing
            // that went wrong, and a line still in a buffer when the process dies is a line that was
            // never written. Nothing here is on a path where a millisecond matters.
#pragma warning disable CA1849
            _writing.Flush();
#pragma warning restore CA1849

            _written += line.Length + Environment.NewLine.Length;

            if (_written >= _each)
            {
                Roll();
            }
        }
    }

    private StreamWriter Open()
    {
        FileInfo file = new(Path);

        _written = file.Exists ? file.Length : 0;

        // Shared for reading and writing: a user is going to open this file while it is being
        // written, with a tail or an editor, and a log they cannot open until the client exits is
        // one they will not send.
        return new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write,
                                               FileShare.ReadWrite))
        {
            AutoFlush = false,
        };
    }

    /// <summary>
    /// Moves the current file aside and drops the oldest, so the total stays bounded.
    /// </summary>
    private void Roll()
    {
#pragma warning disable CA1849
        _writing?.Flush();
#pragma warning restore CA1849
        _writing?.Dispose();
        _writing = null;

        string oldest = Rolled(_keep);

        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int which = _keep - 1; which >= 1; which--)
        {
            if (File.Exists(Rolled(which)))
            {
                File.Move(Rolled(which), Rolled(which + 1), overwrite: true);
            }
        }

        if (_keep > 0 && File.Exists(Path))
        {
            File.Move(Path, Rolled(1), overwrite: true);
        }
        else if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        _written = 0;
    }

    private string Rolled(int which) =>
        System.IO.Path.Combine(_folder, $"{_stem}.{which}.log");
}
